using System.IO;
using NightManager.Core.Analysis;
using NightManager.Core.Focus;
using NightManager.Core.Sync;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// The focus coach: every autofocus run kept on the PC (the backlog of NINA's reports for this profile on first
    /// start, then each new run), and once a night the coach's view, built from the whole record rather than one night.
    /// </summary>
    public sealed partial class NightManagerService {
        private readonly object focusGate = new();
        private FocusHistory focusHistory;

        private string FocusHistoryPath => Path.Combine(dataDirectory, "focus-history.json");
        private string FocusImportedPath => Path.Combine(dataDirectory, "focus-imported.txt");

        private FocusHistory Focus() => focusHistory ??= FocusHistory.Load(FocusHistoryPath);

        private void RecordFocusRun(AutofocusResult result) {
            try {
                lock (focusGate) {
                    if (Focus().Add(result)) Focus().Save(FocusHistoryPath);
                }
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Autofocus run not recorded for the focus coach: {ex.Message}");
            }
        }

        /// <summary>Once per install: NINA keeps a report per autofocus run, named with the profile; read them all.</summary>
        private void ImportFocusBacklog() {
            try {
                if (dataDirectory is null || File.Exists(FocusImportedPath) || !Directory.Exists(AutofocusReportWatcher.Folder)) return;
                var added = 0;
                foreach (var file in Directory.EnumerateFiles(AutofocusReportWatcher.Folder, "*.json")) {
                    if (!Path.GetFileName(file).Contains(RigId, StringComparison.OrdinalIgnoreCase)) continue;
                    try {
                        var result = AutofocusReport.Parse(File.ReadAllText(file));
                        lock (focusGate) {
                            if (Focus().Add(result)) added++;
                        }
                    } catch (Exception) {
                        // A report that can't be read is skipped.
                    }
                }
                lock (focusGate) Focus().Save(FocusHistoryPath);
                File.WriteAllText(FocusImportedPath, DateTimeOffset.UtcNow.ToString("O"));
                NinaLog.Instance.Info($"Focus coach: {added} past autofocus runs read from NINA's reports.");
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Autofocus backlog not read: {ex.Message}");
            }
        }

        private string SendFocusAdvice() {
            try {
                Core.Contracts.FocusAdviceItem advice;
                lock (focusGate) {
                    if (Focus().Runs.Count == 0) return "No autofocus runs recorded yet.";
                    advice = FocusCoach.Assess(Focus().Runs, DateTimeOffset.UtcNow, SyncEngine.NewId);
                }
                Enqueue(advice);
                sync?.Wake();
                NinaLog.Instance.Info($"Focus coach: {advice.Status}, {advice.Nights} nights, {advice.Runs} runs.");
                return advice.Status == Core.Contracts.FocusAdviceStatus.Ready ? "Focus advice sent." : $"Still learning: {advice.Messages.FirstOrDefault()}";
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Focus advice failed: {ex.Message}");
                return "The focus advice couldn't be worked out: " + ex.Message;
            }
        }
    }
}
