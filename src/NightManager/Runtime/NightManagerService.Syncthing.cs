using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Settings;
using NightManager.Core.Sync;
using NightManager.Core.Syncthing;
using System.IO;
using System.Runtime.InteropServices;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Syncthing (paid plans) and image-drive space. Syncthing is one per PC while a twin rig runs two NINA
    /// instances, so a lock file elects one instance to watch and steer it; the others leave it alone.
    /// </summary>
    public sealed partial class NightManagerService {
        private static readonly TimeSpan SyncthingPoll = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StorageRefresh = TimeSpan.FromMinutes(1);

        private readonly StorageWatch storageWatch = new(SyncEngine.NewId);
        private SyncthingMonitor syncthing;
        private SyncthingClient syncthingClient;
        private (string Url, string Key) syncthingClientFor;
        private FileStream syncthingLock;
        private Task syncthingLoop;
        private StorageState storageState;
        private DateTimeOffset storageCheckedAt = DateTimeOffset.MinValue;
        private long sessionBytes;
        private DateTimeOffset? sessionBytesSince;

        private string SyncthingLockPath => Path.Combine(RootDirectory, "syncthing.lock");

        /// <summary>One-line status for the options page.</summary>
        public string SyncthingSummary {
            get {
                var options = FreshSyncthingSettings();
                if (!options.Enabled) {
                    return "Off.";
                }
                if (syncthing is null) {
                    return "Another NINA on this PC is watching Syncthing.";
                }
                return syncthing.State switch {
                    null => "Connecting…",
                    { Status: SyncthingStatus.Unreachable } s => $"Not reachable: {s.Error}",
                    var s => $"Syncthing {s.Version}: {s.Status}" + (s.ToUploadBytes > 0 ? $", {StorageWatch.FormatBytes(s.ToUploadBytes)} to upload" : ""),
                };
            }
        }

        private void StartSyncthing(CancellationToken token) {
            syncthing = null;
            if (!TryTakeSyncthingLock()) {
                // Retry now and then: when the owning NINA closes, this one takes over.
                syncthingLoop = Task.Run(async () => {
                    while (!token.IsCancellationRequested && !TryTakeSyncthingLock()) {
                        await Task.Delay(TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
                    }
                    if (!token.IsCancellationRequested) {
                        RunSyncthing(token);
                    }
                }, token);
                return;
            }
            RunSyncthing(token);
        }

        private void RunSyncthing(CancellationToken token) {
            syncthing = new SyncthingMonitor(SyncthingApi, FreshSyncthingSettings, SaveSyncthingSettings,
                Path.Combine(RootDirectory, "syncthing-changes.json"), newId: SyncEngine.NewId, log: NinaLog.Instance);
            var loop = Task.Run(async () => {
                while (!token.IsCancellationRequested) {
                    try {
                        syncthing.SessionId = sessions?.CurrentSessionId;
                        var events = await syncthing.PollAsync(token).ConfigureAwait(false);
                        EnqueueAll(events);
                        if (events.Count > 0) {
                            sync?.Wake();
                        }
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                        break;
                    } catch (Exception ex) {
                        NinaLog.Instance.Warning($"Syncthing check failed: {ex.Message}");
                    }
                    try {
                        await Task.Delay(SyncthingPoll, token).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        break;
                    }
                }
            }, token);
            syncthingLoop ??= loop;
        }

        private void StopSyncthing() {
            syncthing = null;
            syncthingClient?.Dispose();
            syncthingClient = null;
            syncthingLock?.Dispose();
            syncthingLock = null;
        }

        private bool TryTakeSyncthingLock() {
            if (syncthingLock is not null) {
                return true;
            }
            try {
                syncthingLock = new FileStream(SyncthingLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return true;
            } catch (IOException) {
                return false;
            }
        }

        /// <summary>Another NINA on this PC may have changed the Syncthing settings: always read them from disk.</summary>
        private SyncthingSettings FreshSyncthingSettings() {
            try {
                return InstallationSettings.Load(InstallationSettingsPath).Syncthing;
            } catch (Exception) {
                return Installation?.Syncthing ?? new SyncthingSettings();
            }
        }

        /// <summary>Syncthing settings as saved (shared by every NINA on this PC).</summary>
        public SyncthingSettings SyncthingSettings => FreshSyncthingSettings();

        /// <summary>From the options page: change the Syncthing settings and apply them at once.</summary>
        public void UpdateSyncthing(Func<SyncthingSettings, SyncthingSettings> change) {
            SaveSyncthingSettings(change(FreshSyncthingSettings()));
            if (syncthing is { } monitor && lifetime is { } life) {
                _ = Task.Run(async () => EnqueueAll(await monitor.PollAsync(life.Token)));
            }
        }

        /// <summary>Saves only the Syncthing part, keeping whatever else another instance saved.</summary>
        private void SaveSyncthingSettings(SyncthingSettings updated) {
            var current = InstallationSettings.Load(InstallationSettingsPath);
            var merged = (current with { Syncthing = updated }).Normalized();
            merged.Save(InstallationSettingsPath);
            Installation = merged;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private ISyncthingApi SyncthingApi() {
            var options = FreshSyncthingSettings();
            var key = string.IsNullOrWhiteSpace(options.ApiKey) ? SyncthingClient.ReadLocalApiKey() : options.ApiKey;
            if (string.IsNullOrWhiteSpace(key)) {
                return null;
            }
            if (syncthingClient is null || syncthingClientFor != (options.Url, key)) {
                syncthingClient?.Dispose();
                syncthingClient = new SyncthingClient(options.Url, key);
                syncthingClientFor = (options.Url, key);
            }
            return syncthingClient;
        }

        private Task<string> HandleSyncthingCommand(RemoteCommand command, CancellationToken token) =>
            syncthing is { } monitor
                ? monitor.HandleAsync(command, token)
                : throw new InvalidOperationException(FreshSyncthingSettings().Enabled
                    ? "This NINA isn't the one watching Syncthing on this PC; the command went to the other instance."
                    : "Syncthing isn't set up in NINA (Options → Plugins → The Night Manager → Syncthing).");

        // MARK: Image drive

        /// <summary>Counts what NINA saves tonight, for "nights left" and the "fills before dawn" warning.</summary>
        private void CountSavedBytes(Uri path) {
            try {
                if (path?.IsFile == true && File.Exists(path.LocalPath)) {
                    sessionBytesSince ??= DateTimeOffset.UtcNow;
                    var length = new FileInfo(path.LocalPath).Length;
                    Interlocked.Add(ref sessionBytes, length);
                    RememberSubSize(length);
                }
            } catch (Exception) {
                // Size is a nice-to-have.
            }
        }

        private void ResetSavedBytes() {
            Interlocked.Exchange(ref sessionBytes, 0);
            sessionBytesSince = null;
        }

        private void RefreshStorage(DateTimeOffset now) {
            if (now - storageCheckedAt < StorageRefresh) {
                return;
            }
            storageCheckedAt = now;
            try {
                var folder = profiles.ActiveProfile?.ImageFileSettings?.FilePath;
                if (string.IsNullOrWhiteSpace(folder)) {
                    storageState = null;
                    return;
                }
                if (!FreeSpace(folder, out var free, out var total)) {
                    storageState = null;
                    return;
                }
                var drives = Drives(folder);
                storageState = new StorageState { Path = folder, FreeBytes = free, TotalBytes = total, SessionBytes = Interlocked.Read(ref sessionBytes), Drives = drives };
                EnqueueAll(storageWatch.ObserveSystem(drives.FirstOrDefault(d => d.System), now));
                var dawn = nightState?.AstronomicalDawn;
                var nightLeft = dawn is { } d && d > now ? d - now : (TimeSpan?)null;
                var elapsed = sessionBytesSince is { } since ? now - since : (TimeSpan?)null;
                storageWatch.SessionId = sessions?.CurrentSessionId;
                EnqueueAll(storageWatch.Observe(storageState, now, nightLeft, elapsed));
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Image drive space unavailable: {ex.Message}");
            }
        }

        /// <summary>Every local disk and mapped network drive on the PC, marking the image and Windows drives.</summary>
        private static IReadOnlyList<DriveSpace> Drives(string imageFolder) {
            var imageRoot = SafeRoot(imageFolder);
            var systemRoot = SafeRoot(Environment.SystemDirectory);
            var list = new List<DriveSpace>();
            foreach (var drive in DriveInfo.GetDrives()) {
                try {
                    if (drive.DriveType is not (DriveType.Fixed or DriveType.Network) || !drive.IsReady || drive.TotalSize <= 0) {
                        continue;
                    }
                    var name = drive.Name.TrimEnd('\\');
                    list.Add(new DriveSpace {
                        Name = name,
                        Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                        FreeBytes = drive.AvailableFreeSpace,
                        TotalBytes = drive.TotalSize,
                        Images = string.Equals(drive.Name, imageRoot, StringComparison.OrdinalIgnoreCase),
                        System = string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
                        Network = drive.DriveType == DriveType.Network,
                    });
                } catch (Exception) {
                    // A drive that vanished or refuses access: skip it.
                }
            }
            // The image folder on a network share (\\nas\astro) isn't a drive letter: add it so it's shown.
            if (imageFolder.StartsWith(@"\\", StringComparison.Ordinal) && !list.Any(d => d.Images) && FreeSpace(imageFolder, out var free, out var total)) {
                list.Add(new DriveSpace { Name = imageRoot?.TrimEnd('\\') ?? imageFolder, FreeBytes = free, TotalBytes = total, Images = true, Network = true });
            }
            return list.OrderByDescending(d => d.Images).ThenByDescending(d => d.System).ThenBy(d => d.Name).ToList();
        }

        private static string SafeRoot(string path) {
            try {
                return Path.GetPathRoot(Path.GetFullPath(path));
            } catch (Exception) {
                return null;
            }
        }

        /// <summary>Free and total space for a local drive or a network share (UNC paths need the Win32 call).</summary>
        private static bool FreeSpace(string folder, out long free, out long total) {
            free = total = 0;
            if (GetDiskFreeSpaceEx(folder, out var available, out var size, out _)) {
                free = (long)available;
                total = (long)size;
                return total > 0;
            }
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) {
                return false;
            }
            var drive = new DriveInfo(root);
            if (!drive.IsReady) {
                return false;
            }
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
            return total > 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
    }
}
