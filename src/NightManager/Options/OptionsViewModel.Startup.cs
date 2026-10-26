using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightManager.Core.Startup;
using NightManager.Plugin.Runtime;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;

namespace NightManager.Plugin.Options {
    /// <summary>A sequence to choose for a profile ("None" has no path).</summary>
    public sealed record SequenceChoice(string Name, string Path) {
        public override string ToString() => Name;
    }

    /// <summary>One profile in the startup plan, as edited on the Startup page.</summary>
    public sealed partial class StartupRow : ObservableObject {
        public required string ProfileId { get; init; }
        public required string Name { get; init; }
        public required IReadOnlyList<SequenceChoice> Sequences { get; init; }
        /// <summary>This PC allows starting a sequence from the app for this profile.</summary>
        public bool RunAllowed { get; init; }
        public bool OnThisPc { get; init; } = true;

        [ObservableProperty]
        private bool start;

        [ObservableProperty]
        private bool isMaster;

        [ObservableProperty]
        private SequenceChoice sequence;

        [ObservableProperty]
        private bool runSequence;

        public string Hint => !OnThisPc ? "This profile isn't on this PC any more."
            : RunAllowed ? null
            : "Starting the sequence needs “Allow starting the sequence from the app” ticked in NINA with this profile.";
    }

    /// <summary>The Startup page: NINA starting when the PC starts, and the plan it starts with (shared with the app).</summary>
    public sealed partial class OptionsViewModel {
        private static readonly SequenceChoice NoSequence = new("None", null);

        [ObservableProperty]
        private bool launcherEnabled;

        [ObservableProperty]
        private string launcherNinaPath;

        [ObservableProperty]
        private string launcherProfilesFolder;

        [ObservableProperty]
        private string launcherSequenceFolders;

        [ObservableProperty]
        private string startupStatus;

        [ObservableProperty]
        private string launcherLastRun;

        [ObservableProperty]
        private bool planEnabled;

        [ObservableProperty]
        private int planDelaySeconds = StartupDefaults.DelaySeconds;

        [ObservableProperty]
        private string startupMessage;

        [ObservableProperty]
        private string startupPreview;

        [ObservableProperty]
        private bool startupPlanLoaded;

        public ObservableCollection<StartupRow> StartupRows { get; } = [];

        public string DefaultNinaPathHint => "Empty: " + StartupInventoryScanner.DefaultNinaPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        public string DefaultProfilesFolderHint => "Empty: " + StartupInventoryScanner.DefaultProfilesFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        /// <summary>From Refresh (inside its suppressApply).</summary>
        private void LoadLauncherSettings() {
            var launcher = service.LauncherSettings;
            LauncherEnabled = launcher.Enabled;
            LauncherNinaPath = launcher.NinaPath;
            LauncherProfilesFolder = launcher.ProfilesFolder;
            LauncherSequenceFolders = string.Join("; ", launcher.SequenceFolders);
            StartupStatus = service.StartupSummary;
        }

        partial void OnLauncherEnabledChanged(bool value) => ApplyLauncher(s => s with { Enabled = value });

        partial void OnLauncherNinaPathChanged(string value) => ApplyLauncher(s => s with { NinaPath = value ?? string.Empty });

        partial void OnLauncherProfilesFolderChanged(string value) => ApplyLauncher(s => s with { ProfilesFolder = value ?? string.Empty });

        partial void OnLauncherSequenceFoldersChanged(string value) => ApplyLauncher(s => s with {
            SequenceFolders = (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        });

        private void ApplyLauncher(Func<LauncherSettings, LauncherSettings> change) {
            if (suppressApply) {
                return;
            }
            try {
                service.UpdateLauncher(change);
                StartupMessage = null;
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not save the startup settings", ex);
                StartupMessage = "The startup settings could not be saved: " + ex.Message;
            }
        }

        partial void OnPageChanged(string value) {
            if (value == "startup") {
                LauncherLastRun = service.LauncherLastRun;
                if (service.IsPaired && !StartupPlanLoaded) {
                    _ = LoadStartupPlanAsync();
                }
            }
        }

        /// <summary>Scans the PC again (a new sequence or profile), keeping the edits not yet saved.</summary>
        [RelayCommand]
        private async Task RescanStartup() {
            var inventory = await Task.Run(service.ScanStartupInventory);
            BuildStartupRows(inventory, CurrentPlan());
            LauncherLastRun = service.LauncherLastRun;
            if (service.IsPaired) {
                _ = service.SendStartupInventoryAsync(CancellationToken.None);
            }
        }

        [RelayCommand]
        private async Task LoadStartupPlanAsync() {
            StartupMessage = "Loading the startup plan…";
            try {
                var inventory = service.StartupInventory ?? await Task.Run(service.ScanStartupInventory);
                var plan = await service.FetchStartupPlanAsync(CancellationToken.None);
                BuildStartupRows(inventory, plan);
                StartupPlanLoaded = true;
                StartupMessage = null;
            } catch (Exception ex) {
                StartupMessage = ex.Message;
            }
        }

        [RelayCommand]
        private async Task SaveStartupPlanAsync() {
            StartupMessage = "Saving…";
            try {
                await service.SaveStartupPlanAsync(CurrentPlan(), CancellationToken.None);
                StartupMessage = $"Saved at {DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture)}. The app shows it too.";
            } catch (Exception ex) {
                StartupMessage = ex.Message;
            }
        }

        private StartupPlan CurrentPlan() => new() {
            Enabled = PlanEnabled,
            MasterProfileId = StartupRows.FirstOrDefault(r => r.IsMaster)?.ProfileId,
            DelaySeconds = Math.Clamp(PlanDelaySeconds, StartupDefaults.MinDelaySeconds, StartupDefaults.MaxDelaySeconds),
            Profiles = StartupRows.Select(r => new PlanProfile {
                ProfileId = r.ProfileId, Start = r.Start, SequencePath = r.Sequence?.Path, RunSequence = r.RunSequence && r.Sequence?.Path is not null,
            }).ToList(),
        };

        /// <summary>The plan's profiles in its order, then this PC's other profiles (not started until ticked).</summary>
        private void BuildStartupRows(StartupInventory inventory, StartupPlan plan) {
            foreach (var row in StartupRows) {
                row.PropertyChanged -= OnStartupRowChanged;
            }
            StartupRows.Clear();
            var sequences = new List<SequenceChoice> { NoSequence };
            sequences.AddRange(inventory.Sequences.Select(s => new SequenceChoice(s.Name, s.Path)));
            var allowed = inventory.SequenceStartAllowed.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var ids = plan.Profiles.Select(p => p.ProfileId)
                .Concat(inventory.Profiles.Select(p => p.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids) {
                var profile = inventory.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                var entry = plan.Profiles.FirstOrDefault(p => string.Equals(p.ProfileId, id, StringComparison.OrdinalIgnoreCase));
                var choices = sequences;
                var chosen = NoSequence;
                if (entry?.SequencePath is { } path) {
                    chosen = sequences.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
                    if (chosen is null) {
                        // Chosen earlier but not found now (moved or renamed): kept, so saving doesn't drop it silently.
                        chosen = new SequenceChoice(Path.GetFileNameWithoutExtension(path) + " (not found)", path);
                        choices = [.. sequences, chosen];
                    }
                }
                var row = new StartupRow {
                    ProfileId = id, Name = profile?.Name ?? id, Sequences = choices, RunAllowed = allowed.Contains(id), OnThisPc = profile is not null,
                    Start = entry?.Start ?? false, IsMaster = string.Equals(plan.MasterProfileId, id, StringComparison.OrdinalIgnoreCase),
                    Sequence = chosen, RunSequence = entry?.RunSequence ?? false,
                };
                row.PropertyChanged += OnStartupRowChanged;
                StartupRows.Add(row);
            }
            suppressApply = true;
            try {
                PlanEnabled = plan.Enabled;
                PlanDelaySeconds = plan.DelaySeconds ?? service.LauncherSettings.DelaySeconds;
            } finally {
                suppressApply = false;
            }
            UpdateStartupPreview();
        }

        private void OnStartupRowChanged(object sender, PropertyChangedEventArgs e) {
            // One master: choosing one clears the others.
            if (e.PropertyName == nameof(StartupRow.IsMaster) && sender is StartupRow { IsMaster: true } master) {
                foreach (var row in StartupRows.Where(r => r != master)) {
                    row.IsMaster = false;
                }
            }
            UpdateStartupPreview();
        }

        partial void OnPlanEnabledChanged(bool value) => UpdateStartupPreview();

        partial void OnPlanDelaySecondsChanged(int value) => UpdateStartupPreview();

        /// <summary>What the Launcher would do at the next logon with this plan (as if no NINA were open yet).</summary>
        private void UpdateStartupPreview() {
            if (service.StartupInventory is not { } inventory) {
                StartupPreview = null;
                return;
            }
            if (!PlanEnabled) {
                StartupPreview = "The startup plan is off: NINA isn't started when the PC starts.";
                return;
            }
            var steps = LaunchPlanner.Plan(new LaunchPlanner.Context {
                Plan = CurrentPlan(),
                Profiles = inventory.Profiles,
                SequenceStartAllowed = inventory.SequenceStartAllowed.ToHashSet(StringComparer.OrdinalIgnoreCase),
            });
            if (steps.Count == 0) {
                StartupPreview = "No profile is ticked to start.";
                return;
            }
            var lines = new List<string>();
            foreach (var step in steps) {
                if (step.Skipped is not null) {
                    lines.Add($"{step.ProfileName}: not started. {step.Skipped}");
                    continue;
                }
                var when = step.DelayBefore == TimeSpan.Zero ? "At logon" : $"{step.DelayBefore.TotalSeconds:0} s later";
                var what = step.SequencePath is null ? "no sequence" : $"{Path.GetFileNameWithoutExtension(step.SequencePath)}{(step.RunSequence ? ", started" : ", loaded")}";
                lines.Add($"{when}: NINA with {step.ProfileName} ({what})" + (step.Note is null ? "" : $". {step.Note}"));
            }
            if (!LauncherEnabled) {
                lines.Add("“Start NINA when this PC starts” is off on this PC, so none of this happens yet.");
            }
            StartupPreview = string.Join(Environment.NewLine, lines);
        }
    }
}
