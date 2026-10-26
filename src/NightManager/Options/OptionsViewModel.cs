using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightManager.Core.Analysis;
using NightManager.Core.Cameras;
using NightManager.Core.Contracts;
using NightManager.Core.Pairing;
using NightManager.Core.Settings;
using NightManager.Core.Sync;
using NightManager.Plugin.Runtime;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;

namespace NightManager.Plugin.Options {
    public sealed record RoleOption(RigRole Role, string Label);

    /// <summary>View model for the plugin's page under NINA → Options → Plugins.</summary>
    public sealed partial class OptionsViewModel : ObservableObject {
        private readonly NightManagerService service;
        private bool suppressApply;

        public OptionsViewModel(NightManagerService service) {
            this.service = service;
            service.StatusChanged += (_, _) => OnUi(Refresh);
            service.PairingProgressChanged += (_, progress) => OnUi(() => ApplyPairing(progress));
            Refresh();
            LoadCameras();
            if (service.IsPaired) {
                _ = LoadCollaboratorsAsync();
            }
        }

        public IReadOnlyList<GradingSensitivity> Sensitivities { get; } = Enum.GetValues<GradingSensitivity>();

        public IReadOnlyList<RoleOption> RoleOptions { get; } = [
            new(RigRole.Main, "Main rig: controls its own mount (a setup of its own)"),
            new(RigRole.Linked, "Linked optical path: rides on another rig's mount")
        ];

        // ---- Setup ----

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLinked), nameof(IsMain))]
        private RoleOption selectedRole;

        public bool IsLinked => SelectedRole?.Role == RigRole.Linked;

        public bool IsMain => !IsLinked;

        [ObservableProperty]
        private string setupName;

        [ObservableProperty]
        private IReadOnlyList<RigRecord> mainRigOptions = [];

        [ObservableProperty]
        private RigRecord selectedMainRig;

        [ObservableProperty]
        private string setupText;

        [ObservableProperty]
        private string setupProblem;

        [RelayCommand]
        private void RefreshMainRigs() => LoadMainRigs();

        // ---- Connection status ----

        [ObservableProperty]
        private string statusTitle;

        [ObservableProperty]
        private string statusDetail;

        /// <summary>online | offline | warning | idle; drives the status dot color in XAML.</summary>
        [ObservableProperty]
        private string statusKind;

        [ObservableProperty]
        private string sessionText;

        [ObservableProperty]
        private string schedulerText;

        /// <summary>e.g. "This rig: Ridgeview Pier 1". Each NINA instance/profile is its own rig.</summary>
        [ObservableProperty]
        private string rigText;

        [ObservableProperty]
        private bool isPaired;

        [ObservableProperty]
        private string credentialHint;

        // ---- Pairing ----

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPairingCode))]
        private string pairingCode;

        [ObservableProperty]
        private string pairingMessage;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PairCommand))]
        private bool isPairing;

        [ObservableProperty]
        private string manualCredential;

        public bool HasPairingCode => !string.IsNullOrEmpty(PairingCode);

        // ---- Settings ----

        [ObservableProperty]
        private bool enabled;

        [ObservableProperty]
        private bool uploadThumbnails;

        [ObservableProperty]
        private bool sendSubReports;

        /// <summary>Optional: where calibration frames live, if not under NINA's image folder.</summary>
        [ObservableProperty]
        private string calibrationFolder = string.Empty;

        [ObservableProperty]
        private bool liveUpdates;

        [ObservableProperty]
        private bool liveGuideGraph;

        [ObservableProperty]
        private bool shareTargetScheduler;

        /// <summary>What the observatory's owner switched off in the app (null when nothing is).</summary>
        [ObservableProperty]
        private string appDataOffText;

        [ObservableProperty]
        private bool notifyCameraCooled;

        [ObservableProperty]
        private bool notifyAutofocus;

        /// <summary>Measure tilt, curvature and collimation from every light frame and autofocus run.</summary>
        [ObservableProperty]
        private bool opticsAnalysis;

        /// <summary>Microns of focus per focuser step, as typed (empty: unknown; results stay in steps).</summary>
        [ObservableProperty]
        private string focuserStepMicrons = string.Empty;

        [ObservableProperty]
        private bool allowPlansFromApp;

        [ObservableProperty]
        private bool allowStartFromApp;

        // Observatory cameras (PC-wide, Pro): pier cameras watch the kit, all-sky cameras the sky.
        public ObservableCollection<CameraRow> CameraRows { get; } = [];

        public ObservableCollection<CameraRow> PierCameraRows { get; } = [];

        public ObservableCollection<CameraRow> AllSkyCameraRows { get; } = [];

        /// <summary>The list selections: picking in one clears the other, and either one is the camera being edited.</summary>
        [ObservableProperty]
        private CameraRow selectedPierCamera;

        [ObservableProperty]
        private CameraRow selectedAllSkyCamera;

        partial void OnSelectedPierCameraChanged(CameraRow value) {
            if (value is not null) {
                SelectedAllSkyCamera = null;
                SelectedCamera = value;
            }
        }

        partial void OnSelectedAllSkyCameraChanged(CameraRow value) {
            if (value is not null) {
                SelectedPierCamera = null;
                SelectedCamera = value;
            }
        }

        partial void OnSelectedCameraChanged(CameraRow value) {
            OnPropertyChanged(nameof(SelectedIsZwo));
            if (value?.IsZwo == true && ZwoCameraNames.Count == 0) {
                RefreshZwoCameras();
            }
        }

        public bool SelectedIsZwo => SelectedCamera?.IsZwo == true;

        /// <summary>ZWO cameras plugged into this PC that NINA isn't using.</summary>
        public ObservableCollection<string> ZwoCameraNames { get; } = [];

        [ObservableProperty]
        private string zwoMessage;

        [RelayCommand]
        private void RefreshZwoCameras() {
            var (names, error) = service.ZwoCameraNames();
            ZwoCameraNames.Clear();
            foreach (var name in names) {
                ZwoCameraNames.Add(name);
            }
            ZwoMessage = error ?? $"{names.Count} found. NINA's own camera isn't listed; don't pick the guide camera if PHD2 uses it.";
        }

        private void FillCameraLists(string selectId) {
            PierCameraRows.Clear();
            AllSkyCameraRows.Clear();
            foreach (var row in CameraRows) {
                (row.AllSky ? AllSkyCameraRows : PierCameraRows).Add(row);
                row.PropertyChanged += (_, e) => {
                    if (e.PropertyName == nameof(CameraRow.KindIndex) && ReferenceEquals(row, SelectedCamera)) {
                        OnPropertyChanged(nameof(SelectedIsZwo));
                        if (row.IsZwo && ZwoCameraNames.Count == 0) RefreshZwoCameras();
                    }
                };
            }
            var selected = CameraRows.FirstOrDefault(r => r.Id == selectId) ?? CameraRows.FirstOrDefault();
            if (selected?.AllSky == true) SelectedAllSkyCamera = selected; else SelectedPierCamera = selected;
            SelectedCamera = selected;
        }

        public IReadOnlyList<string> CameraKinds => CameraRow.KindLabels;

        public IReadOnlyList<string> CameraModes => CameraRow.ModeLabels;

        public IReadOnlyList<int> CameraWidths => CameraRow.Widths;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedCamera))]
        private CameraRow selectedCamera;

        public bool HasSelectedCamera => SelectedCamera is not null;

        [ObservableProperty]
        private string camerasStatus;

        [ObservableProperty]
        private string cameraMessage;

        [ObservableProperty]
        private string lockUserName = string.Empty;

        [ObservableProperty]
        private string lockPassword = string.Empty;

        [ObservableProperty]
        private bool cameraBusy;

        private void LoadCameras(string selectId = null) {
            CameraRows.Clear();
            foreach (var camera in service.Cameras) {
                CameraRows.Add(CameraRow.From(camera, service.HasCameraPassword(camera.Id)));
            }
            FillCameraLists(selectId);
        }

        [RelayCommand]
        private void AddPierCamera() {
            var row = new CameraRow { Name = $"Pier camera {PierCameraRows.Count + 1}" };
            CameraRows.Add(row);
            FillCameraLists(row.Id);
            CameraMessage = "Fill in the camera, then Save.";
        }

        [RelayCommand]
        private void AddAllSkyCamera() {
            var row = new CameraRow { Name = $"All-sky camera {AllSkyCameraRows.Count + 1}", AllSky = true, KindIndex = (int)CameraSourceKind.Folder };
            CameraRows.Add(row);
            FillCameraLists(row.Id);
            CameraMessage = "Point it at the folder your all-sky software saves to (or its stream), then Save.";
        }

        [RelayCommand]
        private void RemoveCamera() {
            if (SelectedCamera is null) {
                return;
            }
            if (SelectedCamera.Locked) {
                CameraMessage = "Unlock this camera before removing it.";
                return;
            }
            CameraRows.Remove(SelectedCamera);
            FillCameraLists(null);
            SaveCameras();
        }

        /// <summary>What the last history send found (or that it happens by itself after installing).</summary>
        [ObservableProperty]
        private string historyMessage = "Sent automatically a few minutes after installing; send again any time.";

        [RelayCommand]
        private async Task ImportHistoryAsync() {
            HistoryMessage = "Reading Target Scheduler's records…";
            HistoryMessage = await service.ImportHistoryAsync();
        }

        [RelayCommand]
        private void SaveCameras() {
            try {
                var error = service.SaveCameras(CameraRows.Select(r => r.ToSettings()).ToList());
                if (error is not null) {
                    CameraMessage = error;
                    return;
                }
                foreach (var row in CameraRows.Where(r => !string.IsNullOrEmpty(r.Password))) {
                    service.SetCameraPassword(row.Id, row.Password);
                }
                var keep = SelectedCamera?.Id;
                LoadCameras(keep);
                CameraMessage = "Saved.";
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not save cameras", ex);
                CameraMessage = "Could not save: " + ex.Message;
            }
        }

        [RelayCommand]
        private async Task TestCameraAsync() {
            if (SelectedCamera is null) {
                return;
            }
            CameraBusy = true;
            CameraMessage = "Taking a test picture…";
            try {
                CameraMessage = await service.TestCameraAsync(SelectedCamera.ToSettings(), SelectedCamera.Password);
            } finally {
                CameraBusy = false;
            }
        }

        [RelayCommand]
        private async Task LockCameraAsync() {
            if (SelectedCamera is null) {
                return;
            }
            CameraBusy = true;
            CameraMessage = SelectedCamera.IsNetwork ? "Checking the login with the camera…" : "Locking…";
            try {
                var id = SelectedCamera.Id;
                var error = await service.LockCameraAsync(id, LockUserName, LockPassword);
                CameraMessage = error ?? "Locked. Only someone with this login can change the camera settings now.";
                if (error is null) {
                    LockPassword = string.Empty;
                    LoadCameras(id);
                }
            } finally {
                CameraBusy = false;
            }
        }

        [RelayCommand]
        private async Task UnlockCameraAsync() {
            if (SelectedCamera is null) {
                return;
            }
            CameraBusy = true;
            CameraMessage = "Checking…";
            try {
                var id = SelectedCamera.Id;
                var error = await service.UnlockCameraAsync(id, LockUserName, LockPassword);
                CameraMessage = error ?? "Unlocked. Lock it again when you're done.";
                if (error is null) {
                    LockPassword = string.Empty;
                    LoadCameras(id);
                }
            } finally {
                CameraBusy = false;
            }
        }

        // Local Wi-Fi link (PC-wide)
        [ObservableProperty]
        private bool remoteObservatory;

        [ObservableProperty]
        private string localLinkStatus;

        partial void OnRemoteObservatoryChanged(bool value) {
            if (suppressApply) {
                return;
            }
            try {
                service.SetRemoteObservatory(value);
                LocalLinkStatus = service.LocalLinkStatus;
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not save the remote observatory setting", ex);
                StatusDetail = "The remote observatory setting could not be saved: " + ex.Message;
            }
        }

        // Syncthing (PC-wide)
        [ObservableProperty]
        private bool syncthingEnabled;

        [ObservableProperty]
        private string syncthingUrl;

        [ObservableProperty]
        private string syncthingApiKey;

        [ObservableProperty]
        private bool syncthingAllowControl;

        [ObservableProperty]
        private bool syncWindowEnabled;

        [ObservableProperty]
        private string syncWindowStart;

        [ObservableProperty]
        private string syncWindowEnd;

        [ObservableProperty]
        private bool syncLimitOutside;

        [ObservableProperty]
        private int syncLimitKbps;

        [ObservableProperty]
        private string syncDevices;

        [ObservableProperty]
        private string syncthingStatus;

        [ObservableProperty]
        private bool sendFileNames;

        [ObservableProperty]
        private bool writeCullList;

        [ObservableProperty]
        private GradingSensitivity gradingSensitivity;

        [ObservableProperty]
        private double guidingAlertArcsec;

        [ObservableProperty]
        private bool allowAbortSequence;

        [ObservableProperty]
        private bool allowStopGuiding;

        [ObservableProperty]
        private bool allowParkMount;

        [ObservableProperty]
        private bool allowCloseDome;

        [ObservableProperty]
        private bool allowWarmCamera;

        [ObservableProperty]
        private bool allowSafeShutdown;

        /// <summary>Hands-on control shows on the dev channel only until it's released.</summary>
        public bool ShowDevFeatures => NightManager.Core.Settings.InstallationSettings.IsDevChannel;

        [ObservableProperty]
        private bool allowEquipment;

        [ObservableProperty]
        private bool allowMove;

        [ObservableProperty]
        private string serviceUrl;

        [RelayCommand(CanExecute = nameof(CanPair))]
        private async Task Pair() {
            IsPairing = true;
            PairingCode = null;

            try {
                await service.PairAsync();
            } finally {
                IsPairing = false;
            }
        }

        private bool CanPair() => !IsPairing;

        [RelayCommand]
        private void CancelPairing() => service.CancelPairing();

        [RelayCommand]
        private void Unpair() {
            var answer = MessageBox.Show(
                "Unlink this observatory from The Night Manager? The app will stop receiving data until you pair again.",
                "The Night Manager",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes) {
                service.Unpair();
                PairingCode = null;
                PairingMessage = null;
            }
        }

        [RelayCommand]
        private void ApplyManualCredential() {
            if (service.TrySetManualCredential(ManualCredential)) {
                ManualCredential = string.Empty;
                PairingMessage = "Credential saved.";
            } else {
                PairingMessage = "That doesn't look like a Night Manager credential (it starts with nm2_).";
            }
        }

        partial void OnSelectedRoleChanged(RoleOption value) {
            if (value is null) {
                return;
            }

            if (value.Role == RigRole.Linked) {
                LoadMainRigs();
            }

            Apply(s => s with { Role = value.Role, MainRigId = value.Role == RigRole.Linked ? SelectedMainRig?.RigId ?? s.MainRigId : null });
        }

        partial void OnSetupNameChanged(string value) => Apply(s => s with { SetupName = value });

        partial void OnSelectedMainRigChanged(RigRecord value) {
            if (value is not null) {
                Apply(s => s with { MainRigId = value.RigId });
            }
        }

        private void LoadMainRigs() {
            var wasSuppressed = suppressApply;
            suppressApply = true;

            try {
                MainRigOptions = service.LinkableMainRigs;
                SelectedMainRig = MainRigOptions.FirstOrDefault(r => string.Equals(r.RigId, service.Settings.MainRigId, StringComparison.OrdinalIgnoreCase));
            } finally {
                suppressApply = wasSuppressed;
            }
        }

        partial void OnEnabledChanged(bool value) => Apply(s => s with { Enabled = value });

        partial void OnUploadThumbnailsChanged(bool value) => Apply(s => s with { UploadThumbnails = value });
        partial void OnSendSubReportsChanged(bool value) => Apply(s => s with { SendSubReports = value });
        partial void OnCalibrationFolderChanged(string value) => Apply(s => s with { CalibrationFolder = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
        partial void OnLiveUpdatesChanged(bool value) => Apply(s => s with { LiveUpdates = value });
        partial void OnLiveGuideGraphChanged(bool value) => Apply(s => s with { LiveGuideGraph = value });
        partial void OnShareTargetSchedulerChanged(bool value) => Apply(s => s with { ShareTargetScheduler = value });

        partial void OnNotifyCameraCooledChanged(bool value) => Apply(s => s with { NotifyCameraCooled = value });

        partial void OnNotifyAutofocusChanged(bool value) => Apply(s => s with { NotifyAutofocus = value });
        partial void OnOpticsAnalysisChanged(bool value) => Apply(s => s with { OpticsAnalysis = value });
        partial void OnFocuserStepMicronsChanged(string value) {
            var text = (value ?? "").Replace(',', '.').Trim();
            double? microns = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 && v < 1000 ? v : null;
            Apply(s => s with { FocuserStepMicrons = microns });
        }

        partial void OnAllowPlansFromAppChanged(bool value) => Apply(s => s with { AllowPlansFromApp = value });

        partial void OnAllowStartFromAppChanged(bool value) => Apply(s => s with { AllowStartFromApp = value });

        partial void OnSyncthingEnabledChanged(bool value) => ApplySyncthing(s => s with { Enabled = value });

        partial void OnSyncthingUrlChanged(string value) => ApplySyncthing(s => s with { Url = value ?? string.Empty });

        partial void OnSyncthingApiKeyChanged(string value) => ApplySyncthing(s => s with { ApiKey = value ?? string.Empty });

        partial void OnSyncthingAllowControlChanged(bool value) => ApplySyncthing(s => s with { AllowRemoteControl = value });

        partial void OnSyncWindowEnabledChanged(bool value) => ApplySyncthing(s => s with { Window = s.Window with { Enabled = value } });

        partial void OnSyncWindowStartChanged(string value) {
            if (Core.Syncthing.SyncSchedule.ParseTime(value) is not null) {
                ApplySyncthing(s => s with { Window = s.Window with { Start = value } });
            }
        }

        partial void OnSyncWindowEndChanged(string value) {
            if (Core.Syncthing.SyncSchedule.ParseTime(value) is not null) {
                ApplySyncthing(s => s with { Window = s.Window with { End = value } });
            }
        }

        partial void OnSyncLimitOutsideChanged(bool value) => ApplySyncthing(s => s with { Window = s.Window with { Outside = value ? "limit" : "pause" } });

        partial void OnSyncLimitKbpsChanged(int value) => ApplySyncthing(s => s with { Window = s.Window with { LimitKbps = value } });

        partial void OnSyncDevicesChanged(string value) => ApplySyncthing(s => s with {
            Devices = (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        });

        private void ApplySyncthing(Func<SyncthingSettings, SyncthingSettings> change) {
            if (suppressApply) {
                return;
            }
            try {
                service.UpdateSyncthing(change);
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not save Syncthing settings", ex);
                StatusDetail = "Syncthing settings could not be saved: " + ex.Message;
            }
        }

        partial void OnSendFileNamesChanged(bool value) => Apply(s => s with { SendFileNames = value });

        partial void OnWriteCullListChanged(bool value) => Apply(s => s with { WriteCullList = value });

        partial void OnGradingSensitivityChanged(GradingSensitivity value) => Apply(s => s with { GradingSensitivity = value });

        partial void OnGuidingAlertArcsecChanged(double value) => Apply(s => s with { GuidingAlertArcsec = value > 0 ? value : null });

        partial void OnAllowAbortSequenceChanged(bool value) => SetAllowed(CommandTypes.AbortSequence, value);

        partial void OnAllowStopGuidingChanged(bool value) => SetAllowed(CommandTypes.StopGuiding, value);

        partial void OnAllowParkMountChanged(bool value) => SetAllowed(CommandTypes.ParkMount, value);

        partial void OnAllowCloseDomeChanged(bool value) => SetAllowed(CommandTypes.CloseDome, value);

        partial void OnAllowWarmCameraChanged(bool value) => SetAllowed(CommandTypes.WarmCamera, value);

        partial void OnAllowSafeShutdownChanged(bool value) => SetAllowed(CommandTypes.SafeShutdown, value);

        partial void OnAllowEquipmentChanged(bool value) => SetAllowed(CommandTypes.Equipment, value);

        partial void OnAllowMoveChanged(bool value) => SetAllowed(CommandTypes.Move, value);

        partial void OnServiceUrlChanged(string value) {
            if (suppressApply || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)) {
                return;
            }

            _ = ApplyInstallationAsync(service.Installation with { ServiceUrl = value.TrimEnd('/') });
        }

        private async Task ApplyInstallationAsync(InstallationSettings settings) {
            try {
                await service.ApplyInstallationAsync(settings);
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not save settings", ex);
                StatusDetail = "Settings could not be saved: " + ex.Message;
            }
        }

        private void SetAllowed(string commandType, bool allowed) => Apply(s => {
            var set = s.AllowedCommands.ToHashSet();

            if (allowed) {
                set.Add(commandType);
            } else {
                set.Remove(commandType);
            }

            return s with { AllowedCommands = set.ToList() };
        });

        private void Apply(Func<RigSettings, RigSettings> change) {
            if (suppressApply) {
                return;
            }

            _ = ApplyAsync(change(service.Settings));
        }

        private async Task ApplyAsync(RigSettings settings) {
            try {
                await service.ApplySettingsAsync(settings);
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not save settings", ex);
                StatusDetail = "Settings could not be saved: " + ex.Message;
            }
        }

        private static string AppDataOff(IReadOnlySet<string> off) {
            if (off is null || off.Count == 0) {
                return null;
            }
            var names = new (string Feature, string Name)[] {
                (DataFeatures.Frames, "sub reports"), (DataFeatures.Thumbnails, "previews"), (DataFeatures.Optics, "optics"),
                (DataFeatures.Live, "live updates"), (DataFeatures.Guiding, "the live guiding graph"), (DataFeatures.Scheduler, "Target Scheduler"),
                (DataFeatures.Sequence, "sequence sharing"), (DataFeatures.Cameras, "observatory cameras"),
            }.Where(n => off.Contains(n.Feature)).Select(n => n.Name).ToList();
            return names.Count == 0 ? null : $"Switched off in the app by the observatory's owner: {string.Join(", ", names)}. These are not collected or sent, whatever is ticked here.";
        }

        private void Refresh() {
            var settings = service.Settings;
            suppressApply = true;

            try {
                Enabled = settings.Enabled;
                UploadThumbnails = settings.UploadThumbnails;
                SendSubReports = settings.SendSubReports;
                CalibrationFolder = settings.CalibrationFolder ?? string.Empty;
                LiveUpdates = settings.LiveUpdates;
                LiveGuideGraph = settings.LiveGuideGraph;
                ShareTargetScheduler = settings.ShareTargetScheduler;
                NotifyCameraCooled = settings.NotifyCameraCooled;
                NotifyAutofocus = settings.NotifyAutofocus;
                OpticsAnalysis = settings.OpticsAnalysis;
                FocuserStepMicrons = settings.FocuserStepMicrons?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                AllowPlansFromApp = settings.AllowPlansFromApp;
                AllowStartFromApp = settings.AllowStartFromApp;
                SendFileNames = settings.SendFileNames;
                WriteCullList = settings.WriteCullList;
                GradingSensitivity = settings.GradingSensitivity;
                GuidingAlertArcsec = settings.GuidingAlertArcsec ?? 0;
                AllowAbortSequence = settings.AllowedCommands.Contains(CommandTypes.AbortSequence);
                AllowStopGuiding = settings.AllowedCommands.Contains(CommandTypes.StopGuiding);
                AllowParkMount = settings.AllowedCommands.Contains(CommandTypes.ParkMount);
                AllowCloseDome = settings.AllowedCommands.Contains(CommandTypes.CloseDome);
                AllowWarmCamera = settings.AllowedCommands.Contains(CommandTypes.WarmCamera);
                AllowSafeShutdown = settings.AllowedCommands.Contains(CommandTypes.SafeShutdown);
                AllowEquipment = settings.AllowedCommands.Contains(CommandTypes.Equipment);
                AllowMove = settings.AllowedCommands.Contains(CommandTypes.Move);
                ServiceUrl = service.Installation.ServiceUrl;
                RemoteObservatory = service.Installation.RemoteObservatory;
                var st = service.SyncthingSettings;
                SyncthingEnabled = st.Enabled;
                SyncthingUrl = st.Url;
                SyncthingApiKey = st.ApiKey;
                SyncthingAllowControl = st.AllowRemoteControl;
                SyncWindowEnabled = st.Window.Enabled;
                SyncWindowStart = st.Window.Start;
                SyncWindowEnd = st.Window.End;
                SyncLimitOutside = st.Window.Outside == "limit";
                SyncLimitKbps = st.Window.LimitKbps;
                SyncDevices = string.Join(", ", st.Devices);
                LoadLauncherSettings();
                SelectedRole = RoleOptions.First(o => o.Role == settings.Role);
                SetupName = settings.SetupName;
                LoadMainRigs();
            } finally {
                suppressApply = false;
            }

            AppDataOffText = AppDataOff(service.AppDataOff);
            RigText = $"This rig: {service.RigName ?? "(no profile)"}";
            var setup = service.Setup;
            SetupText = setup is null
                ? null
                : setup.Role == RigRole.Main
                    ? $"Main rig of setup “{setup.SetupName}”. Linked optical paths on this mount appear under this setup in the app."
                    : $"Linked optical path of setup “{setup.SetupName}”. Mount and dome actions are left to the main rig.";
            SetupProblem = setup?.Problem;
            IsPaired = service.IsPaired;
            var hint = service.CredentialHint;
            if (hint != CredentialHint) {
                CredentialHint = hint;
                // Paired (or re-paired) here or in another NINA on this PC: reload who has access.
                if (IsPaired && collaboratorsLoadedFor != hint) {
                    collaboratorsLoadedFor = hint;
                    _ = LoadCollaboratorsAsync();
                }
            }
            var status = service.Status;

            (StatusKind, StatusTitle) = (settings.Enabled, status.State) switch {
                (false, _) => ("idle", "Paused"),
                (_, SyncState.NotPaired) => ("idle", "Not paired"),
                (_, SyncState.Connecting) => ("warning", "Connecting…"),
                (_, SyncState.Online) => ("online", status.ObservatoryName is null ? "Connected" : $"Connected as “{status.ObservatoryName}”"),
                (_, SyncState.Offline) => ("warning", "Offline: data is queued and will be sent automatically"),
                (_, SyncState.Unauthorized) => ("offline", "Unlinked: pair again"),
                _ => ("idle", status.State.ToString())
            };

            var parts = new List<string>();

            if (status.LastSuccess is DateTimeOffset last) {
                parts.Add("Last sync " + last.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
            }

            if (status.PendingItems > 0) {
                parts.Add($"{status.PendingItems} item(s) waiting");
            }

            if (!string.IsNullOrEmpty(status.LastError)) {
                parts.Add(status.LastError);
            }

            StatusDetail = string.Join(" · ", parts);

            var session = service.CurrentSession;
            SessionText = session is null
                ? "No session in progress."
                : $"Tonight: {session.Frames} frames, {TimeSpan.FromSeconds(session.IntegrationSeconds):h\\h\\ mm\\m} integration, {session.Rejected} flagged for rejection.";
            SchedulerText = service.SchedulerSummary;
            SyncthingStatus = service.SyncthingSummary;
            LocalLinkStatus = service.LocalLinkStatus;
            CamerasStatus = service.CamerasSummary;
            ApplyDemoIfOn();
        }

        private void ApplyPairing(PairingProgress progress) {
            PairingCode = progress.State == PairingState.WaitingForApproval ? progress.UserCode : null;
            PairingMessage = progress.ExpiresAt is DateTimeOffset expires && progress.State == PairingState.WaitingForApproval
                ? $"{progress.Message} The code expires at {expires.ToLocalTime():HH:mm}."
                : progress.Message;

            if (progress.State == PairingState.Approved) {
                Refresh();
                _ = LoadCollaboratorsAsync();
            }
        }

        // MARK: Collaborators

        /// <summary>People this observatory is shared with (members and pending invitations).</summary>
        public System.Collections.ObjectModel.ObservableCollection<CollaboratorRow> Collaborators { get; } = new();

        public IReadOnlyList<string> CollaboratorRoles { get; } = ["Can view", "Can view and control"];

        [ObservableProperty]
        private string inviteEmail;

        [ObservableProperty]
        private string inviteRole = "Can view";

        [ObservableProperty]
        private string collaboratorsMessage;

        [ObservableProperty]
        private string collaboratorsPlanText;

        [ObservableProperty]
        private string lastInviteLink;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(InviteCommand))]
        private bool sharingBusy;

        /// <summary>The login the collaborator list was last loaded with.</summary>
        private string collaboratorsLoadedFor;

        [RelayCommand]
        private async Task LoadCollaboratorsAsync() {
            if (DemoScreens) {
                ShowDemoCollaborators();
                return;
            }
            var client = service.Collaborators();
            if (client is null) {
                CollaboratorsMessage = "Pair this observatory first.";
                return;
            }
            SharingBusy = true;
            try {
                var list = await client.ListAsync(CancellationToken.None);
                OnUi(() => ShowCollaborators(list));
            } catch (Exception ex) {
                OnUi(() => CollaboratorsMessage = ex.Message);
            } finally {
                OnUi(() => SharingBusy = false);
            }
        }

        private bool CanInvite() => !SharingBusy;

        [RelayCommand(CanExecute = nameof(CanInvite))]
        private async Task InviteAsync() {
            var email = InviteEmail?.Trim();
            if (string.IsNullOrEmpty(email) || !email.Contains('@') || email.Contains(' ')) {
                CollaboratorsMessage = "Enter the email address your collaborator signs in with.";
                return;
            }
            var client = service.Collaborators();
            if (client is null) {
                CollaboratorsMessage = "Pair this observatory first.";
                return;
            }
            SharingBusy = true;
            try {
                var role = InviteRole == "Can view and control" ? "operator" : "viewer";
                var invitation = await client.InviteAsync(email, role, CancellationToken.None);
                var list = await client.ListAsync(CancellationToken.None);
                OnUi(() => {
                    ShowCollaborators(list);
                    InviteEmail = null;
                    LastInviteLink = invitation.Link;
                    CollaboratorsMessage = $"Invited {email}. When they sign in to The Night Manager with that address, the invitation is waiting. You can also send them the link below.";
                });
            } catch (Exception ex) {
                OnUi(() => CollaboratorsMessage = ex.Message);
            } finally {
                OnUi(() => SharingBusy = false);
            }
        }

        [RelayCommand]
        private async Task RemoveCollaboratorAsync(CollaboratorRow row) {
            var client = service.Collaborators();
            if (client is null || row is null) {
                return;
            }
            var answer = MessageBox.Show(
                row.InvitationId is null
                    ? $"Stop sharing this observatory with {row.Email}? They lose access straight away."
                    : $"Cancel the invitation for {row.Email}?",
                "The Night Manager",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) {
                return;
            }
            SharingBusy = true;
            try {
                if (row.InvitationId is not null) {
                    await client.RevokeInvitationAsync(row.InvitationId, CancellationToken.None);
                } else {
                    await client.RemoveMemberAsync(row.Uid, CancellationToken.None);
                }
                var list = await client.ListAsync(CancellationToken.None);
                OnUi(() => {
                    ShowCollaborators(list);
                    CollaboratorsMessage = row.InvitationId is null ? $"{row.Email} no longer has access." : "Invitation cancelled.";
                });
            } catch (Exception ex) {
                OnUi(() => CollaboratorsMessage = ex.Message);
            } finally {
                OnUi(() => SharingBusy = false);
            }
        }

        [RelayCommand]
        private void CopyInviteLink() {
            if (!string.IsNullOrEmpty(LastInviteLink)) {
                Clipboard.SetText(LastInviteLink);
                CollaboratorsMessage = "Link copied.";
            }
        }

        private void ShowCollaborators(NightManager.Core.Sharing.CollaboratorList list) {
            Collaborators.Clear();
            foreach (var m in list.Members) {
                Collaborators.Add(new CollaboratorRow(m.Email ?? "Collaborator", RoleText(m.Role),
                    m.JoinedAt is { } joined ? $"Joined {joined.ToLocalTime():d MMM}" : "Joined", m.Uid, null));
            }
            foreach (var i in list.Invitations) {
                Collaborators.Add(new CollaboratorRow(i.Email, RoleText(i.Role), $"Invited, not joined yet (until {i.ExpiresAt.ToLocalTime():d MMM})", null, i.InvitationId));
            }
            var used = list.Members.Count + list.Invitations.Count;
            CollaboratorsPlanText = list.Plan.MaxCollaborators == 0
                ? $"Your {list.Plan.Label} plan doesn't include collaborators. Upgrade to share; collaborators never pay to see your scopes."
                : $"{used} of {list.Plan.MaxCollaborators} collaborators on your {list.Plan.Label} plan. Collaborators never pay to see your scopes; their own scopes stay on their own plan.";
            if (Collaborators.Count == 0 && list.Plan.MaxCollaborators > 0) {
                CollaboratorsMessage = null;
            }
        }

        private static string RoleText(string role) => role == "operator" ? "Can view and control" : "Can view";

        private static void OnUi(Action action) {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null || dispatcher.CheckAccess()) {
                action();
            } else {
                dispatcher.BeginInvoke(action);
            }
        }
    }
}
