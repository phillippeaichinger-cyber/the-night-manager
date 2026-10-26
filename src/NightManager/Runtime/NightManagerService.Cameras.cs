using NightManager.Core.Cameras;
using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Settings;
using NightManager.Core.Sync;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Observatory cameras (Pro). Video never leaves the observatory: a small still (kilobytes) is taken at most
    /// every camera's interval (never under three minutes) while someone watches, or on the camera's schedule,
    /// and at once on a kit error (after which the interval starts again). One NINA per PC owns the cameras
    /// (lock file, like Syncthing); the other instances pass their kit errors on through a small file.
    /// </summary>
    public sealed partial class NightManagerService {
        private static readonly TimeSpan CameraTick = TimeSpan.FromSeconds(5);
        private static readonly byte[] CameraSecretsEntropy = Encoding.UTF8.GetBytes("NightManager.Cameras.v1");

        private readonly CameraScheduler cameraScheduler = new();
        private CameraGrabber cameraGrabberInstance;
        private CameraGrabber cameraGrabber => cameraGrabberInstance ??= new(rtsp: RtspFrameGrabber.GrabAsync, zwo: GrabZwoAsync);
        /// <summary>Each ZWO pier camera's last good exposure (µs), by camera and light: the next picture starts there.</summary>
        private readonly ConcurrentDictionary<(string Camera, bool Dark), int> zwoExposures = new();
        private readonly ConcurrentDictionary<string, string> cameraErrors = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (string Camera, string CameraId, string Cause)> cameraAlertUploads = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim cameraWake = new(0);
        private FileStream camerasLock;
        private DateTime cameraAlertFileSeen = DateTime.MinValue;

        private string CamerasLockPath => Path.Combine(RootDirectory, "cameras.lock");
        private string CameraAlertPath => Path.Combine(RootDirectory, "camera-alert.json");
        private string CameraSecretsPath => Path.Combine(RootDirectory, "cameras.secrets");

        /// <summary>This NINA takes the pictures (the other instances on the PC leave the cameras alone).</summary>
        public bool OwnsCameras => camerasLock is not null;

        /// <summary>One-line status for the options page.</summary>
        public string CamerasSummary {
            get {
                var cameras = FreshCameraSettings();
                if (cameras.Count == 0) {
                    return "No cameras.";
                }
                if (!OwnsCameras) {
                    return "Another NINA on this PC takes the camera pictures.";
                }
                if (sync?.CamerasAllowed == false) {
                    return "Cameras are part of Pro: no pictures are sent on this plan.";
                }
                var errors = cameras.Where(c => cameraErrors.ContainsKey(c.Id)).Select(c => $"{c.Name}: {cameraErrors[c.Id]}").ToList();
                return errors.Count > 0 ? string.Join(" · ", errors) : $"{cameras.Count(c => c.Enabled)} camera(s) ready.";
            }
        }

        /// <summary>For the snapshot: what the phone may show (only from the owning NINA).</summary>
        private IReadOnlyList<CameraSummary> CameraSummaries() =>
            !OwnsCameras ? null : FreshCameraSettings().Select(c => c.Summary() with { Error = cameraErrors.TryGetValue(c.Id, out var e) ? e : null }).ToList() is { Count: > 0 } list ? list : null;

        private void StartCameras(CancellationToken token) {
            if (sync is not null) {
                sync.BlobUploaded += OnCameraBlobUploaded;
            }
            _ = Task.Run(async () => {
                var lastLockAttempt = DateTimeOffset.MinValue;
                while (!token.IsCancellationRequested) {
                    try {
                        var now = DateTimeOffset.UtcNow;
                        if (!OwnsCameras && now - lastLockAttempt >= TimeSpan.FromMinutes(1) && FreshCameraSettings().Count > 0) {
                            lastLockAttempt = now;
                            TryTakeCamerasLock();
                        }
                        if (OwnsCameras) {
                            await CameraPassAsync(now, token).ConfigureAwait(false);
                        }
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                        break;
                    } catch (Exception ex) {
                        NinaLog.Instance.Warning($"Camera check failed: {ex.Message}");
                    }
                    try {
                        await cameraWake.WaitAsync(CameraTick, token).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        break;
                    }
                }
            }, token);
        }

        private void StopCameras() {
            if (sync is not null) {
                sync.BlobUploaded -= OnCameraBlobUploaded;
            }
            camerasLock?.Dispose();
            camerasLock = null;
        }

        private bool TryTakeCamerasLock() {
            if (camerasLock is not null) {
                return true;
            }
            try {
                camerasLock = new FileStream(CamerasLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            } catch (IOException) {
                return false;
            }
        }

        private async Task CameraPassAsync(DateTimeOffset now, CancellationToken token) {
            var cameras = FreshCameraSettings();
            ReadCameraAlertFromOtherInstances(now);
            if (cameras.Count == 0 || sync?.CamerasAllowed != true) {
                return; // not configured, or the plan has no cameras (or the backend hasn't said yet)
            }
            var watched = IsWatched;
            foreach (var camera in cameras) {
                if (cameraScheduler.Due(camera, now, watched) is not { } reason) {
                    continue;
                }
                var cause = reason == CaptureReason.Alert ? cameraScheduler.PendingAlertCause : null;
                var grabbed = await cameraGrabber.GrabAsync(camera, CameraPassword(camera.Id), now, token).ConfigureAwait(false);
                if (!grabbed.IsSuccess) {
                    cameraErrors[camera.Id] = grabbed.Error ?? "No picture";
                    cameraScheduler.Failed(camera, reason, now);
                    NinaLog.Instance.Warning($"Camera \"{camera.Name}\": {grabbed.Error}");
                    continue;
                }
                var still = ShrinkCameraImage(grabbed.Image, camera.Width, camera.MaxKilobytes * 1024);
                if (still is null) {
                    cameraErrors[camera.Id] = "The picture couldn't be read";
                    cameraScheduler.Failed(camera, reason, now);
                    continue;
                }
                cameraErrors.TryRemove(camera.Id, out _);
                var item = new CameraFrameItem {
                    Id = SyncEngine.NewId(), Timestamp = now, CameraId = camera.Id, Name = camera.Name,
                    Reason = reason switch { CaptureReason.Alert => "alert", CaptureReason.Schedule => "schedule", _ => "watch" },
                    Cause = cause, Width = still.Width, Height = still.Height, Bytes = still.Bytes.Length,
                };
                if (reason == CaptureReason.Alert) {
                    cameraAlertUploads[item.Id] = (camera.Name, camera.Id, cause ?? "Kit error");
                }
                sync.Enqueue(item, still.Bytes);
                cameraScheduler.Captured(camera, reason, now);
                sync.Wake();
            }
            cameraScheduler.SettleAlert(cameras);
        }

        /// <summary>The alert picture is in the cloud: now tell the phone (with the picture attached).</summary>
        private void OnCameraBlobUploaded(object sender, (string ItemId, bool Success) e) {
            if (!cameraAlertUploads.TryRemove(e.ItemId, out var alert) || !e.Success) {
                return;
            }
            Enqueue(Event(EventKinds.CameraSnapshot, Severity.Warning, $"{alert.Camera} at the time of the alert", alert.Cause,
                data: new Dictionary<string, object> { ["cameraId"] = alert.CameraId }));
            sync?.Wake();
        }

        /// <summary>Called for every event this NINA raises: kit errors force a picture from every camera.</summary>
        private void NoticeKitError(EventItem e) {
            if (!CameraScheduler.IsKitError(e) || FreshCameraSettings().Count == 0) {
                return;
            }
            var cause = string.IsNullOrWhiteSpace(e.Message) ? e.Title : $"{e.Title}: {e.Message}";
            if (OwnsCameras) {
                cameraScheduler.RaiseAlert(cause, DateTimeOffset.UtcNow, CameraScheduler.IsSafetyAlert(e));
                cameraWake.Release();
                return;
            }
            // Another NINA on this PC owns the cameras: hand the alert over through a small file it watches.
            try {
                File.WriteAllText(CameraAlertPath, JsonSerializer.Serialize(new { cause, at = DateTimeOffset.UtcNow, safety = CameraScheduler.IsSafetyAlert(e) }));
            } catch (IOException ex) {
                NinaLog.Instance.Warning($"Couldn't pass the kit error to the camera owner: {ex.Message}");
            }
        }

        private void ReadCameraAlertFromOtherInstances(DateTimeOffset now) {
            try {
                if (!File.Exists(CameraAlertPath)) {
                    return;
                }
                var written = File.GetLastWriteTimeUtc(CameraAlertPath);
                if (written <= cameraAlertFileSeen) {
                    return;
                }
                cameraAlertFileSeen = written;
                if (now.UtcDateTime - written > TimeSpan.FromMinutes(2)) {
                    return; // stale (e.g. left over from before NINA restarted)
                }
                using var doc = JsonDocument.Parse(File.ReadAllText(CameraAlertPath));
                var cause = doc.RootElement.TryGetProperty("cause", out var c) ? c.GetString() : null;
                var safety = doc.RootElement.TryGetProperty("safety", out var s) && s.ValueKind == JsonValueKind.True;
                cameraScheduler.RaiseAlert(cause ?? "Kit error", now, safety);
            } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            }
        }

        // MARK: Settings and secrets

        private InstallationSettings FreshInstallation() {
            try {
                return InstallationSettings.Load(InstallationSettingsPath);
            } catch (Exception) {
                return Installation ?? new InstallationSettings();
            }
        }

        private IReadOnlyList<CameraSettings> FreshCameraSettings() => FreshInstallation().Cameras;

        /// <summary>The cameras as saved (PC-wide), for the options page.</summary>
        public IReadOnlyList<CameraSettings> Cameras => FreshCameraSettings();


        /// <summary>Saves the camera list (PC-wide). Locked cameras keep their protected settings unless unlocked first.</summary>
        public string SaveCameras(IReadOnlyList<CameraSettings> cameras) {
            var current = FreshInstallation();
            foreach (var edited in cameras) {
                var before = current.Cameras.FirstOrDefault(c => c.Id == edited.Id);
                if (before is { Locked: true } && CameraLock.ChangesProtectedFields(before, edited)) {
                    return $"\"{before.Name}\" is locked: unlock it with the camera's login first.";
                }
            }
            var removedLocked = current.Cameras.FirstOrDefault(c => c.Locked && cameras.All(e => e.Id != c.Id));
            if (removedLocked is not null) {
                return $"\"{removedLocked.Name}\" is locked: unlock it before removing it.";
            }
            // Edits never carry the lock itself; keep what's stored.
            var merged = cameras.Select(e => current.Cameras.FirstOrDefault(c => c.Id == e.Id) is { } stored
                ? e with { Locked = stored.Locked, LockSalt = stored.LockSalt, LockHash = stored.LockHash }
                : e with { Locked = false, LockSalt = null, LockHash = null }).ToList();
            (current with { Cameras = merged }).Normalized().Save(InstallationSettingsPath);
            foreach (var id in CameraPasswords().Keys.Where(id => merged.All(c => c.Id != id)).ToList()) {
                SetCameraPassword(id, null);
            }
            PushSnapshot();
            return null;
        }

        /// <summary>Locks a camera with its own login (network cameras: checked against the camera) or a passcode (folders).</summary>
        public async Task<string> LockCameraAsync(string cameraId, string userName, string password) {
            var current = FreshInstallation();
            var camera = current.Cameras.FirstOrDefault(c => c.Id == cameraId);
            if (camera is null) {
                return "Save the camera first.";
            }
            if (HasCameraLogin(camera)) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                if (!await cameraGrabber.CredentialsWorkAsync(camera, userName, password, timeout.Token).ConfigureAwait(false)) {
                    return "The camera didn't accept that login, so the settings were not locked.";
                }
            }
            var secret = HasCameraLogin(camera) ? CameraLock.Secret(userName, password) : password;
            var locked = CameraLock.Lock(camera, secret);
            Save(current with { Cameras = current.Cameras.Select(c => c.Id == cameraId ? locked : c).ToList() });
            return null;
        }

        /// <summary>Unlocks with the login it was locked with, or (network cameras) any login the camera itself accepts.</summary>
        public async Task<string> UnlockCameraAsync(string cameraId, string userName, string password) {
            var current = FreshInstallation();
            var camera = current.Cameras.FirstOrDefault(c => c.Id == cameraId);
            if (camera is not { Locked: true }) {
                return null;
            }
            var secret = HasCameraLogin(camera) ? CameraLock.Secret(userName, password) : password;
            var ok = CameraLock.Matches(camera, secret);
            if (!ok && HasCameraLogin(camera)) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                ok = await cameraGrabber.CredentialsWorkAsync(camera, userName, password, timeout.Token).ConfigureAwait(false);
            }
            if (!ok) {
                return "That login doesn't match: the settings stay locked.";
            }
            Save(current with { Cameras = current.Cameras.Select(c => c.Id == cameraId ? CameraLock.Unlock(c) : c).ToList() });
            return null;
        }

        /// <summary>Network cameras have their own login; folders and ZWO cameras are locked with a passcode.</summary>
        private static bool HasCameraLogin(CameraSettings camera) => camera.Kind is CameraSourceKind.Snapshot or CameraSourceKind.Rtsp;

        // MARK: ZWO pier cameras

        /// <summary>Cameras NINA itself has connected: never offered, never opened.</summary>
        private IEnumerable<string> CamerasInUse() {
            var info = camera.GetInfo();
            return info is { Connected: true } && !string.IsNullOrWhiteSpace(info.Name) ? [info.Name] : [];
        }

        /// <summary>ZWO cameras plugged into this PC that NINA isn't using, for the options page.</summary>
        public (IReadOnlyList<string> Names, string Error) ZwoCameraNames() {
            var cameras = ZwoCameras.List(CamerasInUse(), out var error);
            return (cameras.Select(c => c.Name).ToList(), error ?? (cameras.Count == 0 ? "No ZWO camera found besides the ones NINA is using." : null));
        }

        /// <summary>Dark at the observatory: between nautical dusk and dawn (the pier needs the darkness setting).</summary>
        private bool DarkAtObservatory(DateTimeOffset now) {
            var night = nightState;
            if (night?.NauticalDusk is { } dusk && night.NauticalDawn is { } dawn && dusk < dawn) {
                return now >= dusk && now <= dawn;
            }
            if (night?.Sunset is { } sunset && night.Sunrise is { } sunrise && sunset < sunrise) {
                return now >= sunset && now <= sunrise;
            }
            var hour = DateTime.Now.Hour; // no ephemeris (no location in the profile): a rough guess
            return hour >= 20 || hour < 6;
        }

        private Task<GrabResult> GrabZwoAsync(CameraSettings settings, CancellationToken token) => Task.Run(() => {
            try {
                var dark = DarkAtObservatory(DateTimeOffset.UtcNow);
                var start = zwoExposures.TryGetValue((settings.Id, dark), out var known) ? known : PierExposure.StartMicroseconds(dark, settings.MaxNightExposureSeconds);
                var frame = ZwoCameras.Capture(settings.Address, CamerasInUse(), start, dark, settings.MaxNightExposureSeconds,
                    settings.DayGain, settings.NightGain, settings.Width, token);
                zwoExposures[(settings.Id, dark)] = frame.Microseconds;
                var small = PierFrame.Process(frame.Pixels, frame.Width, frame.Height, frame.Channels, settings.Width);
                return GrabResult.Ok(EncodeJpeg(small));
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                return GrabResult.Fail(ex.Message);
            }
        }, token);

        /// <summary>The processed frame as a JPEG, in memory only (the pixels are dropped as soon as it's sent).</summary>
        private static byte[] EncodeJpeg(PierFrame.Result frame) {
            var format = frame.Channels == 3 ? PixelFormats.Bgr24 : PixelFormats.Gray8;
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, format, null, frame.Pixels, frame.Width * frame.Channels);
            bitmap.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }

        private void Save(InstallationSettings settings) {
            settings.Normalized().Save(InstallationSettingsPath);
            PushSnapshot();
        }

        /// <summary>Takes one picture now (from the options page) to check the camera works; nothing is sent.</summary>
        public async Task<string> TestCameraAsync(CameraSettings camera, string password) {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var stored = string.IsNullOrEmpty(password) ? CameraPassword(camera.Id) : password;
            var grabbed = await cameraGrabber.GrabAsync(camera.Normalized(), stored, DateTimeOffset.UtcNow, timeout.Token).ConfigureAwait(false);
            if (!grabbed.IsSuccess) {
                return grabbed.Error;
            }
            var still = ShrinkCameraImage(grabbed.Image, camera.Width, camera.MaxKilobytes * 1024);
            return still is null ? "The picture couldn't be read." : $"OK: {still.Width}×{still.Height}, {still.Bytes.Length / 1024.0:0.#} KB (from {grabbed.Image.Length / 1024.0:0} KB).";
        }

        private Dictionary<string, string> CameraPasswords() {
            try {
                if (!File.Exists(CameraSecretsPath)) {
                    return new Dictionary<string, string>();
                }
                var plain = CredentialStore.UnprotectBytes(File.ReadAllBytes(CameraSecretsPath), CameraSecretsEntropy);
                return NmJson.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(plain)) ?? new Dictionary<string, string>();
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Camera passwords could not be read: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        private string CameraPassword(string cameraId) => CameraPasswords().TryGetValue(cameraId, out var p) ? p : null;

        /// <summary>Stores (or with null, forgets) a camera's password, encrypted for this Windows user.</summary>
        public void SetCameraPassword(string cameraId, string password) {
            var all = CameraPasswords();
            if (password is null) {
                all.Remove(cameraId);
            } else {
                all[cameraId] = password;
            }
            var bytes = CredentialStore.ProtectBytes(Encoding.UTF8.GetBytes(NmJson.Serialize(all)), CameraSecretsEntropy);
            var temporary = CameraSecretsPath + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, CameraSecretsPath, overwrite: true);
        }

        public bool HasCameraPassword(string cameraId) => CameraPassword(cameraId) is { Length: > 0 };

        // MARK: Shrinking

        private sealed record CameraStill(byte[] Bytes, int Width, int Height);

        /// <summary>Resize to the camera's width, then lower the JPEG quality (and if needed the size) until it fits the cap.</summary>
        private static CameraStill ShrinkCameraImage(byte[] input, int width, int maxBytes) {
            try {
                BitmapSource source;
                using (var stream = new MemoryStream(input)) {
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    source = decoder.Frames[0];
                }
                var target = Math.Min(width, source.PixelWidth);
                while (true) {
                    var scale = (double)target / source.PixelWidth;
                    BitmapSource scaled = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
                    var rgb = new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0);
                    rgb.Freeze();
                    for (var quality = 80; quality >= 30; quality -= 10) {
                        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                        encoder.Frames.Add(BitmapFrame.Create(rgb));
                        using var output = new MemoryStream();
                        encoder.Save(output);
                        if (output.Length <= maxBytes) {
                            return new CameraStill(output.ToArray(), rgb.PixelWidth, rgb.PixelHeight);
                        }
                    }
                    if (target <= 240) {
                        return null;
                    }
                    target = (int)(target * 0.8);
                }
            } catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException) {
                NinaLog.Instance.Warning($"Camera picture could not be decoded: {ex.Message}");
                return null;
            }
        }
    }
}
