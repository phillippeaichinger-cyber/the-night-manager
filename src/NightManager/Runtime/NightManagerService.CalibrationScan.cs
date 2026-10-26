using System.Collections.Concurrent;
using System.IO;
using NightManager.Core.Exposure;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Read noise from the calibration library already on disk: NINA's image folder (and an optional calibration
    /// folder) is searched once a day for bias frames, dark flats and short darks, reading only headers, and a few
    /// per camera setting are loaded (with NINA's own reader) and measured. A watcher picks up new ones as they're
    /// saved. Never while imaging; stacked masters are skipped (their noise is averaged down).
    /// </summary>
    public sealed partial class NightManagerService {
        private const int CalibrationFilesPerScan = 4000;
        private const int FramesPerSetting = 3;
        private static readonly string[] ImageExtensions = [".fits", ".fit", ".fts", ".xisf"];

        private DateTimeOffset nextCalibrationScan = DateTimeOffset.UtcNow.AddMinutes(5);
        private int calibrationScanning;
        private FileSystemWatcher calibrationWatcher;
        private readonly ConcurrentDictionary<string, DateTimeOffset> calibrationArrivals = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> calibrationSeen;

        private string CalibrationSeenPath => Path.Combine(dataDirectory, "calibration-seen.txt");

        private IReadOnlyList<string> CalibrationFolders() {
            var folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.CalibrationFolder)) folders.Add(Settings.CalibrationFolder);
            try {
                if (profiles.ActiveProfile?.ImageFileSettings?.FilePath is { Length: > 0 } images) folders.Add(images);
            } catch (Exception) {
                // No profile yet.
            }
            return folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void TickCalibrationScan(DateTimeOffset now) {
            if (dataDirectory is null || imageDataFactory is null || !DataOn(Core.Settings.DataFeatures.Frames)) {
                return;
            }
            // Never compete with imaging for disk and CPU.
            if (lastSnapshot?.Activity?.State == Core.Contracts.ActivityStates.Imaging) {
                return;
            }
            EnsureCalibrationWatcher();
            var arrived = calibrationArrivals.Where(a => now - a.Value > TimeSpan.FromSeconds(15)).Select(a => a.Key).ToList();
            if (arrived.Count > 0 || now >= nextCalibrationScan) {
                if (Interlocked.Exchange(ref calibrationScanning, 1) == 1) return;
                var full = now >= nextCalibrationScan;
                if (full) nextCalibrationScan = now.AddHours(24);
                foreach (var path in arrived) calibrationArrivals.TryRemove(path, out _);
                _ = Task.Run(async () => {
                    try {
                        await ScanCalibrationAsync(full ? null : arrived).ConfigureAwait(false);
                    } catch (Exception ex) {
                        NinaLog.Instance.Debug($"Calibration scan stopped: {ex.Message}");
                    } finally {
                        Volatile.Write(ref calibrationScanning, 0);
                    }
                });
            }
        }

        /// <summary>Looks through the folders (or just the files given), measuring a few frames per camera setting.</summary>
        private async Task ScanCalibrationAsync(IReadOnlyList<string> only) {
            calibrationSeen ??= File.Exists(CalibrationSeenPath) ? new HashSet<string>(File.ReadAllLines(CalibrationSeenPath), StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            var files = only ?? CalibrationFolders()
                .SelectMany(folder => SafeFiles(folder))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(CalibrationFilesPerScan)
                .Select(f => f.FullName)
                .ToList();

            var bySetting = new Dictionary<string, List<(string Path, ImageHeader Header)>>();
            foreach (var path in files) {
                var stamp = path + "|" + SafeWriteTicks(path);
                if (!calibrationSeen.Add(stamp)) continue;
                if (CalibrationHeader.Read(path) is not { } header || !header.IsReadNoiseFrame(ReadNoiseMaxSeconds)) continue;
                (bySetting.TryGetValue(header.SettingKey, out var list) ? list : bySetting[header.SettingKey] = []).Add((path, header));
            }

            var measured = 0;
            foreach (var (setting, frames) in bySetting) {
                int known;
                lock (exposureGate) known = History().ReadNoise.TryGetValue(setting, out var values) ? values.Count : 0;
                foreach (var (path, header) in frames.Take(Math.Max(0, FramesPerSetting - known))) {
                    try {
                        var image = await imageDataFactory.CreateFromFile(path, 16, header.Bayered, NINA.Core.Enum.RawConverterEnum.FREEIMAGE).ConfigureAwait(false);
                        var props = image.Properties;
                        if (BackgroundNoise.Measure(image.Data.FlatArray, props.Width, props.Height, header.Bayered) is { } noise) {
                            lock (exposureGate) History().AddReadNoise(setting, noise.Noise);
                            measured++;
                        }
                    } catch (Exception ex) {
                        NinaLog.Instance.Debug($"Calibration frame not read ({Path.GetFileName(path)}): {ex.Message}");
                    }
                }
            }
            lock (exposureGate) SaveExposureHistory();
            try {
                File.WriteAllLines(CalibrationSeenPath, calibrationSeen.TakeLast(20000));
            } catch (Exception) {
                // Only saves a re-read next time.
            }
            if (measured > 0) {
                NinaLog.Instance.Info($"Read noise measured from {measured} calibration frame{(measured == 1 ? "" : "s")} on disk ({bySetting.Count} camera setting{(bySetting.Count == 1 ? "" : "s")}).");
            }
        }

        private void EnsureCalibrationWatcher() {
            if (calibrationWatcher is not null) return;
            var folder = CalibrationFolders().FirstOrDefault();
            if (folder is null) return;
            try {
                calibrationWatcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024, EnableRaisingEvents = true };
                FileSystemEventHandler arrived = (_, e) => {
                    if (ImageExtensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase)) {
                        calibrationArrivals[e.FullPath] = DateTimeOffset.UtcNow;
                    }
                };
                calibrationWatcher.Created += arrived;
                calibrationWatcher.Renamed += (s, e) => arrived(s, e);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Not watching {folder} for calibration frames: {ex.Message}");
                calibrationWatcher = null;
            }
        }

        private void StopCalibrationWatcher() {
            calibrationWatcher?.Dispose();
            calibrationWatcher = null;
        }

        private static IEnumerable<FileInfo> SafeFiles(string folder) {
            try {
                return new DirectoryInfo(folder)
                    .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 })
                    .Where(f => ImageExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            } catch (Exception) {
                return [];
            }
        }

        private static long SafeWriteTicks(string path) {
            try {
                return File.GetLastWriteTimeUtc(path).Ticks;
            } catch (Exception) {
                return 0;
            }
        }
    }
}
