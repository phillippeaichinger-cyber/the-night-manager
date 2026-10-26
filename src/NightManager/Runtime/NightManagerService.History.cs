using NightManager.Core.Settings;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using NightManager.Core.Contracts;
using NightManager.Core.History;
using NightManager.Core.Sync;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Past imaging from Target Scheduler's database, sent once after the plugin is installed (and again on
    /// "Sync history now" in NINA or the app), so Year in Review starts with every night already taken.
    /// Only numbers travel: no images, and nothing on the PC is changed.
    /// </summary>
    public sealed partial class NightManagerService {
        private int importingHistory;
        /// <summary>The first look waits a few minutes after NINA starts (it's busy enough then).</summary>
        private readonly DateTimeOffset historyCheckAfter = DateTimeOffset.UtcNow.AddMinutes(3);

        private string HistoryMarkerPath => Path.Combine(dataDirectory, "history-imported.txt");

        /// <summary>Once per install, a little after start (the PC is busy enough when NINA opens).</summary>
        private void MaybeImportHistory(DateTimeOffset now) {
            if (now < historyCheckAfter || sync?.DataOffKnown != true || !DataOn(DataFeatures.Scheduler) || File.Exists(HistoryMarkerPath) || Volatile.Read(ref importingHistory) == 1 || !TargetSchedulerBridge.IsInstalled || !File.Exists(TargetSchedulerBridge.DatabasePath)) {
                return;
            }
            _ = ImportHistoryAsync();
        }

        /// <summary>Reads Target Scheduler's records and queues them for upload. Returns what was found, in words.</summary>
        public Task<string> ImportHistoryAsync() {
            if (!File.Exists(TargetSchedulerBridge.DatabasePath)) {
                return Task.FromResult("Target Scheduler isn't installed on this PC: there's no history to send.");
            }
            if (!DataOn(DataFeatures.Scheduler)) {
                return Task.FromResult("Target Scheduler data is switched off (here or in the app), so nothing was sent.");
            }
            if (Interlocked.Exchange(ref importingHistory, 1) == 1) {
                return Task.FromResult("Already sending the history.");
            }
            return Task.Run(() => {
                try {
                    var (subs, targets) = ReadSchedulerHistory();
                    var items = HistoryParts.Build("targetScheduler", subs, targets, SyncEngine.NewId, DateTimeOffset.UtcNow);
                    foreach (var item in items) {
                        Enqueue(item);
                    }
                    File.WriteAllText(HistoryMarkerPath, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    sync?.Wake();
                    var hours = subs.Sum(s => s.Sub.E ?? 0) / 3600;
                    var nights = subs.Select(s => DateTimeOffset.FromUnixTimeSeconds(s.Sub.T).AddHours(-12).Date).Distinct().Count();
                    var message = $"Sent {subs.Count:N0} subs from Target Scheduler: {hours:N0} h over {nights} nights, on {targets.Count} targets.";
                    NinaLog.Instance.Info($"{message} ({items.Count} parts)");
                    return message;
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"Target Scheduler history could not be read: {ex.Message}");
                    return "Couldn't read Target Scheduler's history: " + ex.Message;
                } finally {
                    Volatile.Write(ref importingHistory, 0);
                }
            });
        }

        private static (List<(string ProfileId, HistorySub Sub)> Subs, Dictionary<string, HistoryTarget> Targets) ReadSchedulerHistory() {
            // Read-only, no pooling: TS keeps writing while NINA images; we only ever read.
            using var connection = new SQLiteConnection($"Data Source={TargetSchedulerBridge.DatabasePath};Read Only=True;Pooling=False;");
            connection.Open();
            connection.BusyTimeout = 5000;

            var projects = new Dictionary<long, string>();
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = "select Id, name from project";
                using var r = cmd.ExecuteReader();
                while (r.Read()) projects[r.GetInt64(0)] = r["name"] as string ?? "";
            }

            var targets = new Dictionary<string, HistoryTarget>();
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = "select Id, name, ra, dec, projectid from target";
                using var r = cmd.ExecuteReader();
                while (r.Read()) {
                    var key = r.GetInt64(0).ToString(CultureInfo.InvariantCulture);
                    targets[key] = new HistoryTarget {
                        Key = key,
                        Name = r["name"] as string ?? "Target",
                        Ra = r["ra"] is double ra ? Math.Round(ra, 5) : null,
                        Dec = r["dec"] is double dec ? Math.Round(dec, 5) : null,
                        Project = r["projectid"] is long p && projects.TryGetValue(p, out var name) ? name : null,
                    };
                }
            }

            // Metadata is JSON per sub (HFR, stars, guiding…); the preview images in "imagedata" are never read.
            var subs = new List<(string, HistorySub)>();
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = "select targetId, acquireddate, filtername, gradingStatus, metadata, profileId from acquiredimage where acquireddate is not null";
                using var r = cmd.ExecuteReader();
                while (r.Read()) {
                    var profile = r["profileId"] as string;
                    if (string.IsNullOrEmpty(profile)) continue;
                    using var meta = JsonDocument.Parse(r["metadata"] as string ?? "{}");
                    var m = meta.RootElement;
                    subs.Add((profile, new HistorySub {
                        T = r.GetInt64(1),
                        K = r.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                        F = r["filtername"] as string,
                        E = Number(m, "ExposureDuration", 0),
                        H = Number(m, "HFR", 2),
                        W = Number(m, "FWHM", 2),
                        S = Number(m, "DetectedStars", 0) is double s ? (int)s : null,
                        X = Number(m, "Eccentricity", 3),
                        R = Number(m, "GuidingRMSArcSec", 2),
                        A = Number(m, "Airmass", 2),
                        G = Number(m, "Gain", 0) is double g ? (int)g : null,
                        V = HistoryParts.Verdict(Convert.ToInt32(r["gradingStatus"], CultureInfo.InvariantCulture)),
                    }));
                }
            }
            return (subs, targets);
        }

        /// <summary>A number from the metadata (TS writes some as strings), rounded; null when missing or not finite.</summary>
        private static double? Number(JsonElement m, string name, int digits) {
            if (!m.TryGetProperty(name, out var v)) return null;
            double value;
            if (v.ValueKind == JsonValueKind.Number) value = v.GetDouble();
            else if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) value = parsed;
            else return null;
            return double.IsFinite(value) && Math.Abs(value) < 1e9 ? Math.Round(value, digits) : null;
        }
    }
}
