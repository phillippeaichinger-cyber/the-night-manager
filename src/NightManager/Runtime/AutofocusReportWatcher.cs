using NightManager.Core.Analysis;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Watches NINA's autofocus report folder (%LOCALAPPDATA%\NINA\AutoFocus). NINA and Hocus Focus both write
    /// "{time}--{profileId}.json" there when a run finishes; only this profile's reports are picked up, so two NINA
    /// instances on one PC (a twin rig) don't see each other's runs.
    /// </summary>
    internal sealed class AutofocusReportWatcher : IDisposable {
        private readonly Func<string> profileId;
        private readonly Action<AutofocusResult, string> found;
        private FileSystemWatcher watcher;

        public AutofocusReportWatcher(Func<string> profileId, Action<AutofocusResult, string> found) {
            this.profileId = profileId;
            this.found = found;
        }

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "AutoFocus");

        public void Start() {
            try {
                Directory.CreateDirectory(Folder);
                watcher = new FileSystemWatcher(Folder, "*.json") {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                };
                watcher.Created += OnFile;
                watcher.Renamed += OnFile;
                watcher.EnableRaisingEvents = true;
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Autofocus reports can't be watched: {ex.Message}");
            }
        }

        private void OnFile(object sender, FileSystemEventArgs e) {
            var id = profileId();
            if (string.IsNullOrEmpty(id) || !e.Name.Contains(id, StringComparison.OrdinalIgnoreCase)) {
                return;
            }
            _ = Task.Run(() => Read(e.FullPath));
        }

        private void Read(string path) {
            // The writer may still hold the file for a moment.
            for (var attempt = 0; attempt < 5; attempt++) {
                try {
                    Thread.Sleep(500 * (attempt + 1));
                    var result = AutofocusReport.Parse(File.ReadAllText(path));
                    found(result, path);
                    return;
                } catch (IOException) {
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"Autofocus report {Path.GetFileName(path)} could not be read: {ex.Message}");
                    return;
                }
            }
        }

        public void Dispose() {
            if (watcher is null) {
                return;
            }
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFile;
            watcher.Renamed -= OnFile;
            watcher.Dispose();
            watcher = null;
        }
    }
}
