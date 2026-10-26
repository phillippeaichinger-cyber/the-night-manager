using System.IO;

namespace NightManager.Plugin.Options {
    /// <summary>
    /// Website screenshots: while %LOCALAPPDATA%\NINA\NightManager\demo-screens.flag exists, the page shows the fictional
    /// demo world (Ridgeview Observatory) instead of this PC's names, people and statuses. Display only: nothing is saved
    /// or sent, and deleting the file brings the real page back with the next status update.
    /// </summary>
    public sealed partial class OptionsViewModel {
        private static readonly string DemoFlag = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "NightManager", "demo-screens.flag");

        private bool showingDemo;

        private static bool DemoScreens => File.Exists(DemoFlag);

        private void ApplyDemoIfOn() {
            var demo = DemoScreens;
            if (!demo) {
                if (showingDemo) {
                    showingDemo = false;
                    if (service.IsPaired) {
                        _ = LoadCollaboratorsAsync();
                    }
                }
                return;
            }

            showingDemo = true;
            suppressApply = true;
            try {
                SyncthingEnabled = true;
                SyncthingAllowControl = true;
                SyncWindowEnabled = true;
                SyncWindowStart = "01:00";
                SyncWindowEnd = "07:00";
                SyncDevices = "Home NAS";
                AllowCloseDome = true;
            } finally {
                suppressApply = false;
            }

            (StatusKind, StatusTitle) = ("online", "Connected as “Ridgeview Observatory”");
            StatusDetail = "Last sync 23:41:08";
            IsPaired = true;
            CredentialHint = "nm2_rdgv…";
            RigText = "This rig: Esprit 100 · EQ6";
            SetupText = "Main rig of setup “Esprit 100 · EQ6”. Linked optical paths on this mount appear under this setup in the app.";
            SetupProblem = null;
            SessionText = "Tonight: 47 frames, 2h 21m integration, 3 flagged for rejection.";
            SchedulerText = "Imaging M31 - Andromeda Galaxy (M31 LRGB). Tonight's plan: 3 target stretches.";
            SyncthingStatus = "Waiting for the upload window (01:00). 6.4 GB to send to Home NAS.";
            CamerasStatus = "Roof camera: last picture 3 min ago.";
            HistoryMessage = "Sent 214 nights of Target Scheduler history.";
            AppDataOffText = null;
            ShowDemoCollaborators();
        }

        private void ShowDemoCollaborators() {
            Collaborators.Clear();
            Collaborators.Add(new CollaboratorRow("alex.morgan@example.com", "Can view and control", "Joined 2 Oct", "demo-1", null));
            Collaborators.Add(new CollaboratorRow("sam.lee@example.com", "Can view", "Invited 12 Oct", null, "demo-2"));
            CollaboratorsPlanText = "2 of 4 collaborators on your Pro plan. Collaborators never pay to see your scopes; their own scopes stay on their own plan.";
            CollaboratorsMessage = null;
            OnPropertyChanged(nameof(PeopleSummary));
        }
    }
}
