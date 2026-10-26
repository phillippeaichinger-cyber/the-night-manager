#nullable enable
using NightManager.Core.Cameras;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Pier cameras through ZWO's own camera driver (ASICamera2.dll), the copy NINA installs with itself: nothing extra
    /// to install. Written for this plugin against ZWO's published C interface. Each picture opens the camera, takes
    /// one short exposure into memory and closes it again, so the camera is only held for a few seconds every few
    /// minutes. Never used for a camera NINA has connected.
    /// </summary>
    internal static class ZwoCameras {
        public sealed record Camera(int Index, int Id, string Name, int MaxWidth, int MaxHeight, bool IsColour, int[] Bins);

        public sealed record Frame(byte[] Pixels, int Width, int Height, int Channels, int Microseconds, int Gain);

        // ASI control types, image types and exposure states (ASICamera2.h).
        private const int ControlGain = 0, ControlExposure = 1, ControlBandwidth = 6;
        private const int ImageRaw8 = 0, ImageRgb24 = 1;
        private const int ExposureWorking = 1, ExposureSuccess = 2, ExposureFailed = 3;

        private static readonly object Gate = new();
        private static Native? native;
        private static string? loadError;

        /// <summary>Every ZWO camera plugged in, except the ones named in <paramref name="inUse"/>.</summary>
        public static IReadOnlyList<Camera> List(IEnumerable<string?> inUse, out string? error) {
            lock (Gate) {
                if (Load() is not { } api) {
                    error = loadError;
                    return [];
                }
                error = null;
                var busy = new HashSet<string>(inUse.Where(n => !string.IsNullOrWhiteSpace(n))!, StringComparer.OrdinalIgnoreCase);
                var cameras = new List<Camera>();
                var count = api.GetNumOfConnectedCameras();
                for (var i = 0; i < count; i++) {
                    if (Describe(api, i) is { } camera && !busy.Contains(camera.Name)) {
                        cameras.Add(camera);
                    }
                }
                return cameras;
            }
        }

        /// <summary>
        /// One picture: a few quick tries until the brightness is usable (each guided by the last), then the frame.
        /// </summary>
        public static Frame Capture(string name, IEnumerable<string?> inUse, int startMicroseconds, bool dark, double maxNightSeconds,
                                    int? dayGain, int? nightGain, int outputWidth, CancellationToken token) {
            lock (Gate) {
                var api = Load() ?? throw new InvalidOperationException(loadError ?? "ZWO's camera driver isn't available");
                if (inUse.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) {
                    throw new InvalidOperationException($"{name} is connected in NINA: pick a camera NINA isn't using");
                }
                var camera = Enumerable.Range(0, api.GetNumOfConnectedCameras()).Select(i => Describe(api, i))
                    .FirstOrDefault(c => c is not null && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{name} isn't plugged in (or its USB connection dropped)");
                if (api.OpenCamera(camera.Id) != 0) {
                    throw new InvalidOperationException($"{name} is busy: another program (PHD2?) has it open");
                }
                try {
                    Check(api.InitCamera(camera.Id), "start the camera");
                    var (gainMin, gainMax) = GainRange(api, camera.Id);
                    var gain = Math.Clamp(dark ? nightGain ?? (gainMin + gainMax) / 2 : dayGain ?? gainMin, gainMin, gainMax);
                    // Binned 2×2 when that still covers the width wanted: faster, less noise, half the USB traffic.
                    var bin = camera.Bins.Contains(2) && camera.MaxWidth / 2 >= outputWidth ? 2 : 1;
                    var width = camera.MaxWidth / bin / 8 * 8;
                    var height = camera.MaxHeight / bin / 2 * 2;
                    var channels = camera.IsColour ? 3 : 1;
                    Check(api.SetROIFormat(camera.Id, width, height, bin, camera.IsColour ? ImageRgb24 : ImageRaw8), "set the picture size");
                    // The smallest share of the USB bus: never in the way of the imaging camera's downloads.
                    api.SetControlValue(camera.Id, ControlBandwidth, 40, 0);
                    api.SetControlValue(camera.Id, ControlGain, gain, 0);

                    var buffer = new byte[width * height * channels];
                    var microseconds = Math.Clamp(startMicroseconds, PierExposure.Floor(dark), PierExposure.Cap(dark, maxNightSeconds));
                    for (var attempt = 0; ; attempt++) {
                        token.ThrowIfCancellationRequested();
                        Expose(api, camera.Id, microseconds, buffer, token);
                        var (median, saturated) = PierFrame.Brightness(buffer, width, height, channels);
                        var next = PierExposure.Next(microseconds, median, saturated, dark, maxNightSeconds);
                        // Good, or no better to be had, or a retry would take long: keep this one.
                        if (PierExposure.Good(median, saturated) || next == microseconds || attempt >= 3 || next > 2_000_000) {
                            return new Frame(buffer, width, height, channels, PierExposure.Good(median, saturated) ? microseconds : next, gain);
                        }
                        microseconds = next;
                    }
                } finally {
                    api.CloseCamera(camera.Id);
                }
            }
        }

        private static void Expose(Native api, int id, int microseconds, byte[] buffer, CancellationToken token) {
            Check(api.SetControlValue(id, ControlExposure, microseconds, 0), "set the exposure");
            Check(api.StartExposure(id, 0), "start the exposure");
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(microseconds / 1000.0) + TimeSpan.FromSeconds(10);
            while (true) {
                if (token.IsCancellationRequested) {
                    api.StopExposure(id);
                    token.ThrowIfCancellationRequested();
                }
                api.GetExpStatus(id, out var status);
                if (status == ExposureSuccess) {
                    break;
                }
                if (status == ExposureFailed) {
                    throw new InvalidOperationException("The exposure failed (USB?)");
                }
                if (DateTime.UtcNow > deadline) {
                    api.StopExposure(id);
                    throw new InvalidOperationException("The exposure never finished");
                }
                Thread.Sleep(status == ExposureWorking && microseconds > 200_000 ? 100 : 20);
            }
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try {
                Check(api.GetDataAfterExp(id, handle.AddrOfPinnedObject(), buffer.Length), "read the picture");
            } finally {
                handle.Free();
            }
        }

        private static void Check(int code, string what) {
            if (code != 0) {
                throw new InvalidOperationException($"The camera couldn't {what} (ZWO error {code})");
            }
        }

        // ASI_CAMERA_INFO, read by offset (C layout, Windows: long = 4 bytes): 240 bytes.
        private static Camera? Describe(Native api, int index) {
            var info = Marshal.AllocHGlobal(512);
            try {
                if (api.GetCameraProperty(info, index) != 0) {
                    return null;
                }
                var nameBytes = new byte[64];
                Marshal.Copy(info, nameBytes, 0, 64);
                var name = Encoding.ASCII.GetString(nameBytes).Split('\0')[0].Trim();
                var bins = Enumerable.Range(0, 16).Select(i => Marshal.ReadInt32(info, 84 + 4 * i)).TakeWhile(b => b > 0).ToArray();
                return new Camera(index, Marshal.ReadInt32(info, 64), name, MaxWidth: Marshal.ReadInt32(info, 72), MaxHeight: Marshal.ReadInt32(info, 68),
                    IsColour: Marshal.ReadInt32(info, 76) != 0, Bins: bins.Length > 0 ? bins : [1]);
            } finally {
                Marshal.FreeHGlobal(info);
            }
        }

        // ASI_CONTROL_CAPS by offset: Name[64], Description[128], MaxValue @192, MinValue @196, ControlType @212.
        private static (int Min, int Max) GainRange(Native api, int id) {
            if (api.GetNumOfControls(id, out var count) != 0) {
                return (0, 100);
            }
            var caps = Marshal.AllocHGlobal(512);
            try {
                for (var i = 0; i < count; i++) {
                    if (api.GetControlCaps(id, i, caps) == 0 && Marshal.ReadInt32(caps, 212) == ControlGain) {
                        return (Marshal.ReadInt32(caps, 196), Marshal.ReadInt32(caps, 192));
                    }
                }
                return (0, 100);
            } finally {
                Marshal.FreeHGlobal(caps);
            }
        }

        // MARK: Loading the driver

        private static Native? Load() {
            if (native is not null || loadError is not null) {
                return native;
            }
            // NINA keeps it in External\x64\ASI next to NINA.exe (and may already have loaded it: then this is the same copy).
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "External", "x64", "ASI", "ASICamera2.dll");
            if (!(File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)) && !NativeLibrary.TryLoad("ASICamera2.dll", out handle)) {
                loadError = "ZWO's camera driver wasn't found in NINA's folder";
                return null;
            }
            try {
                native = new Native(handle);
            } catch (Exception ex) when (ex is EntryPointNotFoundException or ArgumentException) {
                loadError = "ZWO's camera driver is too old";
            }
            return native;
        }

        private sealed class Native(IntPtr library) {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int NoArgs();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ById(int id);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ByPointer(IntPtr info, int index);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int RoiFormat(int id, int width, int height, int bin, int imageType);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ControlValue(int id, int control, int value, int auto);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ByIdAndFlag(int id, int flag);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int OutInt(int id, out int value);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ControlCaps(int id, int index, IntPtr caps);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int Data(int id, IntPtr buffer, int size);

            public readonly NoArgs GetNumOfConnectedCameras = Get<NoArgs>(library, "ASIGetNumOfConnectedCameras");
            public readonly ByPointer GetCameraProperty = Get<ByPointer>(library, "ASIGetCameraProperty");
            public readonly ById OpenCamera = Get<ById>(library, "ASIOpenCamera");
            public readonly ById InitCamera = Get<ById>(library, "ASIInitCamera");
            public readonly ById CloseCamera = Get<ById>(library, "ASICloseCamera");
            public readonly OutInt GetNumOfControls = Get<OutInt>(library, "ASIGetNumOfControls");
            public readonly ControlCaps GetControlCaps = Get<ControlCaps>(library, "ASIGetControlCaps");
            public readonly RoiFormat SetROIFormat = Get<RoiFormat>(library, "ASISetROIFormat");
            public readonly ControlValue SetControlValue = Get<ControlValue>(library, "ASISetControlValue");
            public readonly ByIdAndFlag StartExposure = Get<ByIdAndFlag>(library, "ASIStartExposure");
            public readonly ById StopExposure = Get<ById>(library, "ASIStopExposure");
            public readonly OutInt GetExpStatus = Get<OutInt>(library, "ASIGetExpStatus");
            public readonly Data GetDataAfterExp = Get<Data>(library, "ASIGetDataAfterExp");

            private static T Get<T>(IntPtr library, string name) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        }
    }
}
