using LibVLCSharp.Shared;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;
using NightManager.Core.Cameras;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Takes one frame from an RTSP camera, entirely inside the plugin: VLC (bundled with the plugin, only the
    /// modules a single-frame grab needs) opens the stream over TCP, decodes into memory (no window, no audio), and
    /// the stream is closed as soon as a clean frame arrives. Works the way NINA's RTSP Client plugin plays a stream,
    /// but never shows video and never relies on that plugin. Same VLC/LibVLCSharp versions, so both can be loaded.
    /// </summary>
    internal static class RtspFrameGrabber {
        private static readonly object InitGate = new();
        private static readonly SemaphoreSlim OneAtATime = new(1, 1);
        private static bool initialized;
        private static string initError;

        /// <summary>Frames to skip after the first: the very first one after connecting can be a half-drawn keyframe.</summary>
        private const int FramesToSkip = 2;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        private static string VlcDirectory =>
            Path.Combine(Path.GetDirectoryName(typeof(RtspFrameGrabber).Assembly.Location) ?? string.Empty, "libvlc", "win-x64");

        private static bool EnsureInitialized(out string error) {
            lock (InitGate) {
                if (!initialized && initError is null) {
                    try {
                        if (!Environment.Is64BitProcess) {
                            initError = "RTSP needs 64-bit NINA";
                        } else if (!File.Exists(Path.Combine(VlcDirectory, "libvlc.dll"))) {
                            initError = "The plugin's video decoder is missing: reinstall The Night Manager";
                        } else {
                            LibVLCSharp.Shared.Core.Initialize(VlcDirectory);
                            initialized = true;
                        }
                    } catch (Exception ex) {
                        initError = $"The video decoder couldn't start: {ex.Message}";
                    }
                }
                error = initError;
                return initialized;
            }
        }

        /// <summary>The <see cref="RtspFrameSource"/> the camera service uses.</summary>
        public static async Task<GrabResult> GrabAsync(string address, string userName, string password, int width, CancellationToken token) {
            if (!EnsureInitialized(out var error)) {
                return GrabResult.Fail(error);
            }
            // One stream open at a time, whatever the number of cameras.
            await OneAtATime.WaitAsync(token).ConfigureAwait(false);
            try {
                return await Task.Run(() => Grab(address, userName, password, width, token), token).ConfigureAwait(false);
            } finally {
                OneAtATime.Release();
            }
        }

        private static GrabResult Grab(string address, string userName, string password, int targetWidth, CancellationToken token) {
            var frame = new FrameSink(targetWidth);
            using var libVlc = new LibVLC(
                "--no-audio", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--no-stats", "--quiet",
                // Software decoding only: no GPU surfaces are involved in a memory grab.
                "--avcodec-hw=none", "--network-caching=600");
            using var media = new Media(libVlc, new Uri(address));
            media.AddOption(":rtsp-tcp");
            media.AddOption(":no-audio");
            if (!string.IsNullOrEmpty(userName)) {
                media.AddOption($":rtsp-user={userName}");
                media.AddOption($":rtsp-pwd={password ?? string.Empty}");
            }
            using var player = new VlcPlayer(media);
            player.SetVideoFormatCallbacks(frame.Format, frame.Cleanup);
            player.SetVideoCallbacks(frame.Lock, null, frame.Display);
            player.EncounteredError += (_, _) => frame.Fail("Couldn't open the stream: check the address and the camera login");
            player.EndReached += (_, _) => frame.Fail("The camera ended the stream before sending a picture");

            try {
                if (!player.Play()) {
                    return GrabResult.Fail("Couldn't start the stream");
                }
                var index = WaitHandle.WaitAny([frame.Done, token.WaitHandle], Timeout);
                if (index == WaitHandle.WaitTimeout) {
                    return GrabResult.Fail(frame.Frames > 0 ? "The stream was too slow to send a clean picture" : "No picture from the stream in time: check the address and login");
                }
                token.ThrowIfCancellationRequested();
                return frame.Result ?? GrabResult.Fail("No picture from the stream");
            } finally {
                // Never stop the player from inside a VLC callback: it waits for those threads.
                player.Stop();
                frame.Dispose();
            }
        }

        /// <summary>Receives decoded frames in memory (VLC's "vmem" output) and keeps one.</summary>
        private sealed class FrameSink : IDisposable {
            private readonly int targetWidth;
            private IntPtr buffer;
            private int width, height, pitch;
            private int frames;
            private readonly object gate = new();

            // Held as fields so the GC never collects a callback VLC still calls.
            public readonly VlcPlayer.LibVLCVideoFormatCb Format;
            public readonly VlcPlayer.LibVLCVideoCleanupCb Cleanup;
            public readonly VlcPlayer.LibVLCVideoLockCb Lock;
            public readonly VlcPlayer.LibVLCVideoDisplayCb Display;

            public ManualResetEvent Done { get; } = new(false);
            public GrabResult Result { get; private set; }
            public int Frames => frames;

            public FrameSink(int targetWidth) {
                this.targetWidth = Math.Clamp(targetWidth, 320, 1024);
                Format = OnFormat;
                Cleanup = OnCleanup;
                Lock = OnLock;
                Display = OnDisplay;
            }

            /// <summary>VLC asks which picture we want: BGRA, scaled down to the camera's width (VLC does the scaling).</summary>
            private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint w, ref uint h, ref uint pitches, ref uint lines) {
                Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
                if (w > targetWidth) {
                    h = (uint)Math.Max(2, Math.Round(h * (double)targetWidth / w / 2) * 2);
                    w = (uint)targetWidth;
                }
                lock (gate) {
                    width = (int)w;
                    height = (int)h;
                    pitch = width * 4;
                    if (buffer != IntPtr.Zero) {
                        Marshal.FreeHGlobal(buffer);
                    }
                    buffer = Marshal.AllocHGlobal(pitch * height);
                }
                pitches = (uint)pitch;
                lines = (uint)height;
                return 1;
            }

            private void OnCleanup(ref IntPtr opaque) {
            }

            private IntPtr OnLock(IntPtr opaque, IntPtr planes) {
                Marshal.WriteIntPtr(planes, buffer);
                return IntPtr.Zero;
            }

            private void OnDisplay(IntPtr opaque, IntPtr picture) {
                if (Interlocked.Increment(ref frames) <= FramesToSkip || Done.WaitOne(0)) {
                    return;
                }
                try {
                    byte[] pixels;
                    lock (gate) {
                        pixels = new byte[pitch * height];
                        Marshal.Copy(buffer, pixels, 0, pixels.Length);
                    }
                    var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, pitch);
                    image.Freeze();
                    var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var stream = new MemoryStream();
                    encoder.Save(stream);
                    Result = GrabResult.Ok(stream.ToArray());
                } catch (Exception ex) {
                    Result = GrabResult.Fail($"The picture couldn't be made: {ex.Message}");
                }
                Done.Set();
            }

            public void Fail(string message) {
                if (!Done.WaitOne(0)) {
                    Result ??= GrabResult.Fail(message);
                    Done.Set();
                }
            }

            public void Dispose() {
                lock (gate) {
                    if (buffer != IntPtr.Zero) {
                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;
                    }
                }
                Done.Dispose();
            }
        }
    }
}
