using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;

namespace NightManager.Plugin.Options {
    /// <summary>
    /// The options page as a dashboard: an overview of tiles, each opening one page of settings, instead of one long
    /// scroll. Pages: overview, connection, data, images, control, site, people.
    /// </summary>
    public sealed partial class OptionsViewModel {
        [ObservableProperty]
        private string page = "overview";

        [RelayCommand]
        private void GoTo(string target) => Page = string.IsNullOrEmpty(target) ? "overview" : target;

        private static string OnOff(bool on) => on ? "on" : "off";

        public string ConnectionSummary => IsPaired
            ? "Linked: this PC and every NINA on it show in the app."
            : "Not linked yet. Pair to see this rig in the app.";

        public string DataSummary {
            get {
                if (!Enabled) {
                    return "Paused: nothing is sent from this NINA.";
                }

                var on = new[] { SendSubReports, LiveUpdates, LiveGuideGraph, ShareTargetScheduler, UploadThumbnails }.Count(b => b);
                return $"{on} of 5 optional feeds on. Status, safety and alerts always go.";
            }
        }

        public string ImagesSummary =>
            $"Grading {GradingSensitivity.ToString().ToLowerInvariant()} · previews {OnOff(UploadThumbnails)} · optics {OnOff(OpticsAnalysis)} · cull list {OnOff(WriteCullList)}";

        public string ControlSummary {
            get {
                var allowed = new[] { AllowAbortSequence, AllowStopGuiding, AllowParkMount, AllowCloseDome, AllowWarmCamera, AllowSafeShutdown }.Count(b => b);
                var alerts = GuidingAlertArcsec > 0 ? $"guiding alert at {GuidingAlertArcsec:0.0#}″" : "guiding alert against tonight's normal";
                return $"{allowed} of 6 remote actions allowed · plans from the app {OnOff(AllowPlansFromApp)} · {alerts}";
            }
        }

        public string SiteSummary => $"Syncthing: {SyncthingStatus}\nCameras: {CamerasStatus}";

        public string PeopleSummary => !IsPaired
            ? "Link the observatory first."
            : Collaborators.Count switch {
                0 => "Not shared yet. Invite someone to watch or help run the night.",
                1 => "Shared with 1 person.",
                var n => $"Shared with {n} people."
            };

        private static readonly string[] Summaries = [
            nameof(ConnectionSummary), nameof(DataSummary), nameof(ImagesSummary), nameof(ControlSummary), nameof(SiteSummary), nameof(PeopleSummary)
        ];

        /// <summary>Every setting change refreshes the tiles (a handful of strings, cheap).</summary>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e) {
            base.OnPropertyChanged(e);
            if (e.PropertyName is null || Summaries.Contains(e.PropertyName) || e.PropertyName == nameof(Page)) {
                return;
            }

            foreach (var name in Summaries) {
                base.OnPropertyChanged(new PropertyChangedEventArgs(name));
            }
        }
    }
}
