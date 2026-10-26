using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NINA.Core.Locale;
using System.Globalization;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// NINA's status bar, mirrored to the app ("Idle", "Waiting for dark · 30:18", "Waiting until safe"). Read
    /// every couple of seconds; a snapshot goes out only when the status actually changes, never because a
    /// countdown ticked, since the app counts down from the end time on its own.
    /// </summary>
    public sealed partial class NightManagerService {
        private static readonly TimeSpan StatusPoll = TimeSpan.FromSeconds(2);

        private readonly StatusBar statusBar = new();
        private StatusBarReader statusReader;
        private Timer statusTimer;
        private IReadOnlyList<ActivityLine> statusLines = [];
        private string statusKey = "";
        private int statusPolling;

        /// <summary>NINA's name for the sequencer's line, in the user's language.</summary>
        private static string SequenceSource {
            get {
                try {
                    var name = Loc.Instance["LblSequence"];
                    return string.IsNullOrWhiteSpace(name) ? "Sequence" : name;
                } catch {
                    return "Sequence";
                }
            }
        }

        private void StartStatusBar() {
            statusReader = new StatusBarReader(applicationStatus);
            statusTimer = new Timer(_ => PollStatusBar(), null, StatusPoll, StatusPoll);
        }

        private void StopStatusBar() {
            statusTimer?.Dispose();
            statusTimer = null;
        }

        private void PollStatusBar() {
            if (Interlocked.Exchange(ref statusPolling, 1) == 1) {
                return;
            }

            try {
                var lines = statusBar.Read(statusReader.Read(), DateTimeOffset.UtcNow);
                var key = Key(lines);
                if (key == statusKey) {
                    return;
                }

                var sequenceMoved = SequenceText(lines) != SequenceText(statusLines);
                statusLines = lines;
                statusKey = key;

                if (sequenceMoved) {
                    // The sequencer moved on (a wait began or ended): read which instruction runs now.
                    RefreshSequenceState();
                }
                PushSnapshot();
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Status bar read failed: {ex.Message}");
            } finally {
                Volatile.Write(ref statusPolling, 0);
            }
        }

        private NinaActivity CurrentActivity(CameraState camera) =>
            StatusBar.Compose(statusLines, SequenceSource, sequenceState, camera);

        private static string SequenceText(IReadOnlyList<ActivityLine> lines) {
            var source = SequenceSource;
            return lines.FirstOrDefault(l => string.Equals(l.Source, source, StringComparison.OrdinalIgnoreCase))?.Text;
        }

        private static string Key(IReadOnlyList<ActivityLine> lines) =>
            string.Join("\n", lines.Select(l => string.Join("|", l.Source, l.Text, l.Detail, l.Count,
                l.Progress?.ToString(CultureInfo.InvariantCulture), l.Until?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))));
    }
}
