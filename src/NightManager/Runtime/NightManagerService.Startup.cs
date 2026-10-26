using Microsoft.Win32;
using NightManager.Core.Json;
using NightManager.Core.Settings;
using NightManager.Core.Startup;
using System.Diagnostics;
using System.IO;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Starting NINA when the PC starts. The plugin tells the backend what's on this PC to choose from (profiles and
    /// sequences), installs the Launcher (shipped in the plugin's Launcher folder) and registers it to run at Windows
    /// logon, or removes it from logon when switched off. The plan itself is edited here or in the app.
    /// </summary>
    public sealed partial class NightManagerService {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "NightManagerLauncher";
        private const string LauncherExeName = "NightManager.Launcher.exe";
        private readonly object launcherGate = new();

        private string LauncherDirectory => Path.Combine(RootDirectory, "launcher");

        private string LauncherExe => Path.Combine(LauncherDirectory, LauncherExeName);

        private static string BundledLauncherDirectory =>
            Path.Combine(Path.GetDirectoryName(typeof(NightManagerService).Assembly.Location) ?? string.Empty, "Launcher");

        /// <summary>What was found on this PC at the last scan (for the options page).</summary>
        public StartupInventory StartupInventory { get; private set; }

        /// <summary>Launcher settings as saved (shared by every NINA on this PC).</summary>
        public LauncherSettings LauncherSettings {
            get {
                try {
                    return InstallationSettings.Load(InstallationSettingsPath).Launcher;
                } catch (Exception) {
                    return Installation?.Launcher ?? new LauncherSettings();
                }
            }
        }

        /// <summary>Whether Windows will run the Launcher at logon (the Run entry points at it).</summary>
        public bool LauncherRegistered {
            get {
                try {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                    return key?.GetValue(RunValueName) is string value && value.Contains(LauncherExeName, StringComparison.OrdinalIgnoreCase);
                } catch (Exception) {
                    return false;
                }
            }
        }

        /// <summary>One-line status for the options page.</summary>
        public string StartupSummary {
            get {
                var settings = LauncherSettings;
                var inventory = StartupInventory;
                var found = inventory is null ? "" : $" {inventory.Profiles.Count} profiles and {inventory.Sequences.Count} sequences found.";
                if (!settings.Enabled) {
                    return "Off: NINA isn't started when this PC starts." + found;
                }
                if (inventory is { NinaFound: false }) {
                    return $"NINA wasn't found at {inventory.NinaPath}: set its location below.";
                }
                return (LauncherRegistered ? "On: NINA starts with the startup plan when Windows logs on." : "On, but not yet registered with Windows.") + found;
            }
        }

        /// <summary>The Launcher's last lines (what happened at the last logon).</summary>
        public string LauncherLastRun {
            get {
                try {
                    var log = Path.Combine(LauncherDirectory, "launcher.log");
                    if (!File.Exists(log)) {
                        return "The Launcher hasn't run yet.";
                    }
                    var lines = File.ReadAllLines(log);
                    return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 8)));
                } catch (Exception ex) {
                    return $"The Launcher's log couldn't be read: {ex.Message}";
                }
            }
        }

        private void StartStartup(CancellationToken token) {
            _ = Task.Run(async () => {
                try {
                    ApplyLauncher();
                    await SendStartupInventoryAsync(token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"Startup: {ex.Message}");
                }
            }, token);
        }

        /// <summary>From the options page: change the Launcher settings, then install or remove it and rescan.</summary>
        public void UpdateLauncher(Func<LauncherSettings, LauncherSettings> change) {
            var current = InstallationSettings.Load(InstallationSettingsPath);
            var merged = (current with { Launcher = change(current.Launcher) }).Normalized();
            merged.Save(InstallationSettingsPath);
            Installation = merged;
            ApplyLauncher();
            StatusChanged?.Invoke(this, EventArgs.Empty);
            if (lifetime is { } life) {
                _ = Task.Run(() => SendStartupInventoryAsync(life.Token));
            }
        }

        /// <summary>Scans this PC again and sends it to the backend (the app's choices).</summary>
        public async Task SendStartupInventoryAsync(CancellationToken token) {
            var inventory = ScanStartupInventory();
            if (CurrentCredential() is not { } key || transport is null) {
                return;
            }
            try {
                var result = await transport.PostJsonAsync("/v2/launcher/inventory", NmJson.Serialize(inventory), key, token).ConfigureAwait(false);
                if (!result.IsSuccess) {
                    NinaLog.Instance.Warning($"Startup: the PC's profiles weren't sent ({result.StatusCode}).");
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                NinaLog.Instance.Warning($"Startup: the PC's profiles weren't sent: {ex.Message}");
            }
        }

        public StartupInventory ScanStartupInventory() {
            var inventory = StartupInventoryBuilder.Build(LauncherSettings, RootDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            StartupInventory = inventory;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return inventory;
        }

        /// <summary>The startup plan saved in the app (or here), without counting as a boot.</summary>
        public async Task<StartupPlan> FetchStartupPlanAsync(CancellationToken token) {
            var key = CurrentCredential() ?? throw new InvalidOperationException("Pair this PC first.");
            var inventory = StartupInventory ?? ScanStartupInventory();
            var result = await transport.PostJsonAsync(LauncherRunner.PlanPath, NmJson.Serialize(new { dryRun = true, inventory }), key, token).ConfigureAwait(false);
            if (!result.IsSuccess || string.IsNullOrEmpty(result.Body)) {
                throw new InvalidOperationException($"The startup plan couldn't be read ({result.StatusCode}).");
            }
            return NmJson.Deserialize<LauncherPlanResponse>(result.Body)?.Plan ?? new StartupPlan();
        }

        /// <summary>Saves the startup plan (the app sees it straight away).</summary>
        public async Task SaveStartupPlanAsync(StartupPlan plan, CancellationToken token) {
            var key = CurrentCredential() ?? throw new InvalidOperationException("Pair this PC first.");
            var result = await transport.PostJsonAsync("/v2/launcher/plan/save", NmJson.Serialize(plan), key, token).ConfigureAwait(false);
            if (!result.IsSuccess) {
                throw new InvalidOperationException($"The startup plan wasn't saved ({result.StatusCode}).");
            }
        }

        /// <summary>
        /// On: the Launcher copied next to the settings (so it outlives plugin updates) and registered to run at logon.
        /// Off: the logon entry removed. Twin rigs run this from two NINAs at once, hence the lock.
        /// </summary>
        private void ApplyLauncher() {
            lock (launcherGate) {
                try {
                    if (!LauncherSettings.Enabled) {
                        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                        if (key?.GetValue(RunValueName) is not null) {
                            key.DeleteValue(RunValueName, throwOnMissingValue: false);
                            NinaLog.Instance.Info("Startup: NINA no longer starts at logon.");
                        }
                        return;
                    }
                    CopyLauncher();
                    if (!File.Exists(LauncherExe)) {
                        NinaLog.Instance.Warning($"Startup: the Launcher isn't in {BundledLauncherDirectory}; reinstall the plugin.");
                        return;
                    }
                    using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                    var command = $"\"{LauncherExe}\"";
                    if (!string.Equals(run.GetValue(RunValueName) as string, command, StringComparison.OrdinalIgnoreCase)) {
                        run.SetValue(RunValueName, command);
                        NinaLog.Instance.Info($"Startup: NINA starts at logon ({command}).");
                    }
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"Startup: the Launcher couldn't be set up: {ex.Message}");
                }
            }
        }

        /// <summary>Copies the plugin's Launcher when it differs, unless it's running (it holds its files then).</summary>
        private void CopyLauncher() {
            var source = BundledLauncherDirectory;
            if (!Directory.Exists(source)) {
                return;
            }
            var files = Directory.GetFiles(source);
            var changed = files.Any(file => {
                var target = new FileInfo(Path.Combine(LauncherDirectory, Path.GetFileName(file)));
                var bundled = new FileInfo(file);
                return !target.Exists || target.Length != bundled.Length || target.LastWriteTimeUtc != bundled.LastWriteTimeUtc;
            });
            if (!changed) {
                return;
            }
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LauncherExeName)).Length > 0) {
                NinaLog.Instance.Info("Startup: the Launcher is running; it's updated next time NINA starts.");
                return;
            }
            try {
                Directory.CreateDirectory(LauncherDirectory);
                foreach (var file in files) {
                    var target = Path.Combine(LauncherDirectory, Path.GetFileName(file));
                    File.Copy(file, target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
                }
                NinaLog.Instance.Info($"Startup: Launcher installed in {LauncherDirectory}.");
            } catch (IOException ex) {
                // Another NINA on this PC is copying it at the same moment: its copy will do.
                NinaLog.Instance.Debug($"Startup: Launcher not copied: {ex.Message}");
            }
        }
    }
}
