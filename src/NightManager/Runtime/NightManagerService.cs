using NightManager.Core.Analysis;
using NightManager.Core.Commands;
using NightManager.Core.Contracts;
using NightManager.Core.Outbox;
using NightManager.Core.Pairing;
using NightManager.Core.Scheduler;
using NightManager.Core.Security;
using NightManager.Core.Sessions;
using NightManager.Core.Settings;
using NightManager.Core.Sync;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;

namespace NightManager.Plugin.Runtime {
    /// <summary>
    /// The running Night Manager: collects state and events from NINA, grades frames, tracks the
    /// session, and hands everything to the sync engine. One instance per NINA process.
    /// </summary>
    [Export(typeof(NightManagerService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public sealed partial class NightManagerService : INightManagerRuntime {
        private static readonly TimeSpan FastTick = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan NightRefresh = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan SchedulerRefresh = TimeSpan.FromMinutes(15);
        /// <summary>Re-send an unchanged plan this often, so a new or re-paired backend always has it.</summary>
        private static readonly TimeSpan SchedulerResend = TimeSpan.FromHours(3);

        private readonly IProfileService profiles;
        private readonly ICameraMediator camera;
        private readonly ITelescopeMediator telescope;
        private readonly IGuiderMediator guider;
        private readonly IFocuserMediator focuser;
        private readonly IFilterWheelMediator filterWheel;
        private readonly IRotatorMediator rotator;
        private readonly IWeatherDataMediator weather;
        private readonly ISafetyMonitorMediator safety;
        private readonly IDomeMediator dome;
        private readonly IFlatDeviceMediator flatDevice;
        private readonly ISwitchMediator switchHub;
        private readonly IImageSaveMediator imageSave;
        private readonly IImagingMediator imaging;
        private Delegate imagePrepared;
        private readonly ISequenceMediator sequence;
        private readonly INighttimeCalculator nighttime;
        private readonly IMessageBroker messageBroker;
        private readonly IApplicationStatusMediator applicationStatus;
        private readonly object analysisGate = new();
        private readonly object lifecycleGate = new();
        private readonly StateCollector collector = new();
        private readonly NightManager.Core.Analysis.ThumbnailPolicy thumbnailPolicy = new();

        private readonly object credentialGate = new();
        private RigRegistry registry;
        private string dataDirectory;
        private CredentialStore credentials;
        private ApiCredential credential;
        private DateTimeOffset credentialCheckedAt;
        private DateTime credentialStamp;
        private HttpApiTransport transport;
        private SyncEngine sync;
        private FrameGrader grader;
        private NightWatch watch;
        private SessionTracker sessions;
        private CommandProcessor commands;
        private CancellationTokenSource lifetime;
        private CancellationTokenSource pairing;
        private Task syncLoop;
        private Timer ticker;
        private SequenceState sequenceState = new() { Running = false };
        private NightState nightState;
        private DateTimeOffset nightComputedAt = DateTimeOffset.MinValue;
        private string lastTarget;
        private int anonymousFrameCounter;
        private volatile bool started;
        private int pendingSaves;
        private int processingFrames;
        private long lastSaveActivityTicks;
        private int ticking;
        private readonly object sequenceGate = new();
        private TargetSchedulerBridge scheduler;
        private volatile bool autofocusInSequence;
        private AutofocusReportWatcher autofocusReports;
        private DateTimeOffset nextSchedulerRefresh = DateTimeOffset.MaxValue;
        private DateTimeOffset lastSchedulerSent;
        private string lastSchedulerSignature;
        private int schedulerRefreshing;

        private readonly NINA.Image.Interfaces.IImageDataFactory imageDataFactory;

        [ImportingConstructor]
        public NightManagerService(
            IProfileService profiles,
            ICameraMediator camera,
            ITelescopeMediator telescope,
            IGuiderMediator guider,
            IFocuserMediator focuser,
            IFilterWheelMediator filterWheel,
            IRotatorMediator rotator,
            IWeatherDataMediator weather,
            ISafetyMonitorMediator safety,
            IDomeMediator dome,
            IFlatDeviceMediator flatDevice,
            IImageSaveMediator imageSave,
            ISequenceMediator sequence,
            INighttimeCalculator nighttime,
            IMessageBroker messageBroker,
            IApplicationStatusMediator applicationStatus,
            IImagingMediator imaging,
            NINA.Image.Interfaces.IImageDataFactory imageDataFactory,
            ISwitchMediator switchHub) {
            this.switchHub = switchHub;
            this.imaging = imaging;
            this.imageDataFactory = imageDataFactory;
            this.profiles = profiles;
            this.camera = camera;
            this.telescope = telescope;
            this.guider = guider;
            this.focuser = focuser;
            this.filterWheel = filterWheel;
            this.rotator = rotator;
            this.weather = weather;
            this.safety = safety;
            this.dome = dome;
            this.flatDevice = flatDevice;
            this.imageSave = imageSave;
            this.sequence = sequence;
            this.nighttime = nighttime;
            this.messageBroker = messageBroker;
            this.applicationStatus = applicationStatus;
            Settings = new RigSettings();
            Installation = new InstallationSettings();
        }

        /// <summary>Settings of this rig (the active NINA profile).</summary>
        public RigSettings Settings { get; private set; }

        /// <summary>Settings shared by all NINA instances on this PC.</summary>
        public InstallationSettings Installation { get; private set; }

        /// <summary>The rig this NINA instance represents: the active profile's id.</summary>
        public string RigId { get; private set; }

        public string RigName { get; private set; }

        /// <summary>The setup (mount + optical paths) this rig belongs to.</summary>
        public SetupIdentity Setup { get; private set; }

        /// <summary>Main rigs on this PC this rig could be linked to (for the options page).</summary>
        public IReadOnlyList<RigRecord> LinkableMainRigs => registry?.MainRigs(RigId) ?? [];

        public SyncStatus Status => sync?.Status ?? new SyncStatus(SyncState.NotPaired, null, null, 0, null);

        public bool IsPaired => CurrentCredential() is not null;

        public string CredentialHint => CurrentCredential()?.Redacted;

        public SessionSummary CurrentSession => sessions?.Summary;

        public event EventHandler StatusChanged;

        public event EventHandler<PairingProgress> PairingProgressChanged;

        public static string PluginVersion => typeof(NightManagerService).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0.0";

        /// <summary>%LOCALAPPDATA%\NINA\NightManager: installation settings and the shared credential.</summary>
        private string RootDirectory => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NightManager");

        private string InstallationSettingsPath => Path.Combine(RootDirectory, "settings.json");

        private string SettingsPath => Path.Combine(dataDirectory, "settings.json");

        public void Start() {
            lock (lifecycleGate) {
                if (started) {
                    return;
                }

                Directory.CreateDirectory(RootDirectory);
                Installation = InstallationSettings.Load(InstallationSettingsPath);

                // One NINA profile = one rig. NINA never opens the same profile in two instances, so
                // per-profile folders keep the queues and sessions of parallel instances apart.
                var profile = profiles.ActiveProfile;
                RigId = profile?.Id.ToString() ?? "default";
                RigName = profile?.Name;
                dataDirectory = Path.Combine(RootDirectory, "rigs", RigId);
                MigrateSingleRigData();
                Settings = RigSettings.Load(SettingsPath, seedPath: InstallationSettingsPath);
                registry = new RigRegistry(Path.Combine(RootDirectory, "rigs"));
                PublishRig();

                credentials = new CredentialStore(Path.Combine(RootDirectory, "credential.bin"));
                credentialCheckedAt = DateTimeOffset.MinValue;
                credentialStamp = DateTime.MinValue;
                credential = null;
                CurrentCredential();

                grader = new FrameGrader(GradingThresholds.For(Settings.GradingSensitivity));
                watch = new NightWatch(WatchOptions(Settings));
                sessions = new SessionTracker(Path.Combine(dataDirectory, "session.json"));
                sessions.Ended += OnSessionEnded;
                watch.SessionId = sessions.CurrentSessionId;

                transport = new HttpApiTransport(new Uri(Installation.ServiceUrl), $"NightManager-NINA/{PluginVersion} (NINA {CoreUtil.Version})");
                var outbox = new DurableOutbox(Path.Combine(dataDirectory, "outbox"));
                sync = new SyncEngine(outbox, transport, CurrentCredential, BuildClientInfo, new SyncOptions(), log: NinaLog.Instance);
                sync.StatusChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
                sync.CommandsReceived += OnCommandsReceived;

                commands = new CommandProcessor(
                    new NinaCommandExecutor(sequence, guider, telescope, dome, camera, ClassicRoot),
                    () => Settings.AllowedCommands.ToHashSet(),
                    Path.Combine(dataDirectory, "processed-commands.txt"),
                    log: NinaLog.Instance,
                    controlsMount: () => Setup?.ControlsMount ?? true);
                commands.SnapshotRequested += (_, _) => PushSnapshot();
                planLoader = new SequencePlanLoader(sequence, profiles);
                trees = new SequenceTreeService(sequence, profiles);
                // Hands-on control: dev channel only until it's released.
                if (NightManager.Core.Settings.InstallationSettings.IsDevChannel) commands.EquipmentControl = HandleEquipmentCommand;
                collector.Control = ControlNow;
                commands.PhoneControl = (command, token) =>
                    command.Type == CommandTypes.ImportHistory ? ImportHistoryAsync()
                    : command.Type == CommandTypes.CheckReadiness ? Task.FromResult(CheckReadinessNow())
                    : command.Type == CommandTypes.AdviseExposure ? Task.FromResult(SendExposureAdvice())
                    : command.Type == CommandTypes.AdviseFocus ? Task.FromResult(SendFocusAdvice())
                    : command.Type == CommandTypes.SetFilterBandwidths ? Task.FromResult(SetFilterBandwidths(command))
                    : CommandTypes.Sync.Contains(command.Type) ? HandleSyncthingCommand(command, token)
                    : CommandTypes.Plans.Contains(command.Type) ? Task.FromResult(HandlePlanCommand(command))
                    : Task.FromResult(Phone.Handle(command));

                Subscribe();
                StartScheduler();
                profiles.ProfileChanged += OnProfileChanged;
                lifetime = new CancellationTokenSource();

                if (Settings.Enabled) {
                    syncLoop = Task.Run(() => sync.RunAsync(lifetime.Token));
                }

                StartSyncthing(lifetime.Token);
                StartStartup(lifetime.Token);
                StartLive(lifetime.Token);
                StartLocalLink();
                StartCameras(lifetime.Token);

                ticker = new Timer(_ => Tick(), null, FastTick, FastTick);
                StartStatusBar();
                NightManagerRuntime.Current = this;
                started = true;
            }

            Enqueue(Event(EventKinds.PluginStarted, Severity.Info, "Night Manager started", $"Plugin {PluginVersion} on NINA {CoreUtil.Version}, rig \"{RigName}\""));
            RefreshSequenceState();
            PushSnapshot();
            NinaLog.Instance.Info($"Started for rig \"{RigName}\" ({RigId}), {Setup.Role.ToString().ToLowerInvariant()} rig of setup \"{Setup.SetupName}\". Paired: {IsPaired}. Service: {Installation.ServiceUrl}");
        }

        public async Task StopAsync() {
            Task loop;

            lock (lifecycleGate) {
                if (!started) {
                    return;
                }

                started = false;
                NightManagerRuntime.Current = null;
                profiles.ProfileChanged -= OnProfileChanged;
                ticker?.Dispose();
                StopStatusBar();
                StopCalibrationWatcher();
                Unsubscribe();
                StopSyncthing();
                StopLive();
                StopLocalLink();
                StopCameras();
                scheduler?.Stop();
                pairing?.Cancel();
                loop = syncLoop;
            }

            Enqueue(Event(EventKinds.PluginStopping, Severity.Info, "Night Manager stopping", "NINA is closing, the profile changed or the plugin was disabled."));

            // Stop the loop first so the final flush cannot run concurrently with a loop flush.
            lifetime.Cancel();

            if (loop is not null) {
                try {
                    await loop.WaitAsync(TimeSpan.FromSeconds(5));
                } catch (Exception) {
                }
            }

            if (Settings.Enabled) {
                try {
                    // Best effort: get the last state out before NINA exits; anything left stays queued on disk.
                    using var flushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await sync.FlushOnceAsync(flushTimeout.Token);
                } catch (Exception ex) {
                    NinaLog.Instance.Debug($"Final flush skipped: {ex.Message}");
                }
            }

            transport.Dispose();
            lifetime.Dispose();
        }

        /// <summary>Applies changed rig settings; restarts the sync stack when sending is switched on or off.</summary>
        public async Task ApplySettingsAsync(RigSettings updated) {
            var normalized = updated.Normalized();

            if (!started) {
                // Options page used before NINA initialized the plugin: persist only.
                if (dataDirectory is not null) {
                    normalized.Save(SettingsPath);
                }

                Settings = normalized;
                return;
            }

            var restart = normalized.Enabled != Settings.Enabled;
            normalized.Save(SettingsPath);
            Settings = normalized;
            PublishRig();

            lock (analysisGate) {
                grader.SetThresholds(GradingThresholds.For(normalized.GradingSensitivity));
                watch.SetOptions(WatchOptions(normalized));
            }

            await RestartOrWakeAsync(restart);
        }

        private static NightWatchOptions WatchOptions(RigSettings settings) => new() {
            GuidingRmsLimit = settings.GuidingAlertArcsec,
            ReportCooledDown = settings.NotifyCameraCooled,
        };

        /// <summary>Applies PC-wide settings (affects every NINA instance after its next start).</summary>
        public async Task ApplyInstallationAsync(InstallationSettings updated) {
            var normalized = updated.Normalized();
            var restart = normalized.ServiceUrl != Installation.ServiceUrl;
            normalized.Save(InstallationSettingsPath);
            Installation = normalized;
            await RestartOrWakeAsync(restart && started);
        }

        private async Task RestartOrWakeAsync(bool restart) {
            if (restart) {
                await StopAsync();
                Start();
            } else {
                sync?.Wake();
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private async void OnProfileChanged(object sender, EventArgs e) {
            try {
                var newId = profiles.ActiveProfile?.Id.ToString();

                if (newId is null || newId == RigId) {
                    return;
                }

                NinaLog.Instance.Info($"Profile changed to \"{profiles.ActiveProfile?.Name}\": switching rig.");
                await StopAsync();
                Start();
                StatusChanged?.Invoke(this, EventArgs.Empty);
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not switch rig after a profile change", ex);
            }
        }

        /// <summary>Publishes this rig for the other NINA instances and (re)resolves its setup.</summary>
        private void PublishRig() {
            try {
                registry.Publish(new RigRecord {
                    RigId = RigId,
                    RigName = RigName,
                    Role = Settings.Role,
                    SetupName = Settings.SetupName,
                    MainRigId = Settings.MainRigId,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Could not publish rig information: {ex.Message}");
            }

            ResolveSetup();
        }

        private void ResolveSetup() {
            var resolved = SetupIdentity.Resolve(RigId, RigName, Settings, registry);

            if (resolved != Setup) {
                Setup = resolved;
                sync?.Wake();
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Shared credential: pairing from any instance on this PC links all of them. Re-read when
        /// the file changes (checked at most every 5 s), so other instances pick up pairing/unlinking.
        /// </summary>
        private ApiCredential CurrentCredential() {
            var store = credentials;

            if (store is null) {
                return null;
            }

            lock (credentialGate) {
                var now = DateTimeOffset.UtcNow;

                if (now - credentialCheckedAt < TimeSpan.FromSeconds(5)) {
                    return credential;
                }

                credentialCheckedAt = now;
                var stamp = store.LastWriteTimeUtc;

                if (stamp != credentialStamp) {
                    credentialStamp = stamp;
                    var previous = credential;
                    credential = store.Load();

                    if (previous is not null || credential is not null) {
                        if (previous?.Value != credential?.Value) {
                            sync?.CredentialsChanged();
                        }
                    }
                }

                return credential;
            }
        }

        /// <summary>Moves single-rig data written by earlier builds into the first rig that starts.</summary>
        private void MigrateSingleRigData() {
            try {
                if (Directory.Exists(dataDirectory)) {
                    return;
                }

                Directory.CreateDirectory(dataDirectory);

                foreach (var name in new[] { "outbox", "session.json", "processed-commands.txt" }) {
                    var source = Path.Combine(RootDirectory, name);
                    var target = Path.Combine(dataDirectory, name);

                    if (Directory.Exists(source)) {
                        Directory.Move(source, target);
                    } else if (File.Exists(source)) {
                        File.Move(source, target);
                    }
                }
            } catch (Exception ex) {
                // Another instance may have migrated it at the same moment; nothing is lost either way.
                NinaLog.Instance.Debug($"Single-rig data migration skipped: {ex.Message}");
            }
        }

        #region Pairing

        public async Task PairAsync() {
            if (!started) {
                PairingProgressChanged?.Invoke(this, new PairingProgress(PairingState.Failed, null, null, "The Night Manager is still starting. Try again in a moment."));
                return;
            }

            pairing?.Cancel();
            pairing = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = pairing.Token;
            var progress = new Progress<PairingProgress>(p => PairingProgressChanged?.Invoke(this, p));

            try {
                var request = new PairStartRequest {
                    InstallationId = Installation.InstallationId,
                    SuggestedName = SuggestedObservatoryName(),
                    PluginVersion = PluginVersion,
                    NinaVersion = CoreUtil.Version
                };
                var result = await new PairingClient(transport).PairAsync(request, progress, token);

                if (result is null) {
                    return;
                }

                if (!ApiCredential.TryParse(result.ApiKey, out var issued)) {
                    PairingProgressChanged?.Invoke(this, new PairingProgress(PairingState.Failed, null, null, "The service returned an invalid credential. Update the plugin."));
                    return;
                }

                SetCredential(issued);
                NINA.Core.Utility.Notification.Notification.ShowSuccess($"Night Manager paired{(result.ObservatoryName is null ? "" : " with " + result.ObservatoryName)}.");
            } catch (OperationCanceledException) {
                PairingProgressChanged?.Invoke(this, new PairingProgress(PairingState.Idle, null, null, "Pairing cancelled."));
            } catch (Exception ex) {
                NinaLog.Instance.Error("Pairing failed", ex);
                PairingProgressChanged?.Invoke(this, new PairingProgress(PairingState.Failed, null, null, ex.Message));
            }
        }

        public void CancelPairing() => pairing?.Cancel();

        /// <summary>Advanced: paste a credential issued by the web dashboard.</summary>
        public bool TrySetManualCredential(string value) {
            if (!ApiCredential.TryParse(value, out var parsed)) {
                return false;
            }

            SetCredential(parsed);
            return true;
        }

        public void Unpair() {
            credentials.Delete();

            lock (credentialGate) {
                credential = null;
                credentialStamp = DateTime.MinValue;
                credentialCheckedAt = DateTimeOffset.UtcNow;
            }

            sync.CredentialsChanged();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SetCredential(ApiCredential value) {
            credentials.Save(value);

            lock (credentialGate) {
                credential = value;
                credentialStamp = credentials.LastWriteTimeUtc;
                credentialCheckedAt = DateTimeOffset.UtcNow;
            }

            sync.CredentialsChanged();
            PushSnapshot();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The observatory is the PC (all its NINA instances); rigs are named after their profiles.</summary>
        private static string SuggestedObservatoryName() => Environment.MachineName;

        #endregion

        #region INightManagerRuntime (used by sequence instructions)

        public void SendUserNotification(string title, string message, string severity) {
            Enqueue(Event(EventKinds.UserNotification, severity, string.IsNullOrWhiteSpace(title) ? "Message from your sequence" : title, message));
            sync?.Wake();
        }

        /// <summary>
        /// "Night ending": said straight away so the app knows the silence that follows (PC shutting down) is
        /// planned, not a crash. The session carries on, so subs still being saved are counted.
        /// </summary>
        public void AnnounceNightEnding(string message) {
            lock (analysisGate) {
                watch.NightEnded = true;
            }
            Enqueue(Event("session.ending", Severity.Info, "Night ending",
                string.IsNullOrWhiteSpace(message) ? "Wrapping up: parking, warming and shutting down." : message,
                new Dictionary<string, object> { ["notify"] = true }));
            sync?.Wake();
        }

        public async Task EndSessionAsync(string reason, IProgress<string> progress, CancellationToken token) {
            lock (analysisGate) {
                watch.NightEnded = true;
            }
            var quiet = TimeSpan.FromSeconds(8);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

            while (DateTimeOffset.UtcNow < deadline) {
                var busy = Volatile.Read(ref pendingSaves) > 0 || Volatile.Read(ref processingFrames) > 0;
                var sinceActivity = DateTimeOffset.UtcNow - new DateTimeOffset(Interlocked.Read(ref lastSaveActivityTicks), TimeSpan.Zero);

                if (!busy && sinceActivity >= quiet) {
                    break;
                }

                progress?.Report("Waiting for the last images to be saved…");
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }

            var ended = sessions?.End(DateTimeOffset.UtcNow, reason);

            if (ended is not null) {
                Enqueue(ended.Item);
                PushSnapshot();
            }
        }

        #endregion

        #region Event wiring

        /// <summary>
        /// One host hook (consumer registration or event subscription). Hooks are attached
        /// independently: a hook NINA cannot accept yet is retried on every tick, and one failing
        /// hook never prevents the plugin from starting.
        /// </summary>
        private sealed record Hook(string Name, Func<bool> Ready, Action Attach, Action Detach) {
            public bool Attached { get; set; }
        }

        private List<Hook> hooks;
        private bool hooksAttachedAtStart;

        private List<Hook> BuildHooks() {
            var list = new List<Hook>();
            void Add(string name, Action attach, Action detach, Func<bool> ready = null) => list.Add(new Hook(name, ready ?? (() => true), attach, detach));

            Add("camera consumer", () => camera.RegisterConsumer(collector), () => camera.RemoveConsumer(collector));
            Add("mount consumer", () => telescope.RegisterConsumer(collector), () => telescope.RemoveConsumer(collector));
            Add("guider consumer", () => guider.RegisterConsumer(collector), () => guider.RemoveConsumer(collector));
            Add("focuser consumer", () => focuser.RegisterConsumer(collector), () => focuser.RemoveConsumer(collector));
            Add("filter wheel consumer", () => filterWheel.RegisterConsumer(collector), () => filterWheel.RemoveConsumer(collector));
            Add("rotator consumer", () => rotator.RegisterConsumer(collector), () => rotator.RemoveConsumer(collector));
            Add("weather consumer", () => weather.RegisterConsumer(collector), () => weather.RemoveConsumer(collector));
            Add("safety consumer", () => safety.RegisterConsumer(collector), () => safety.RemoveConsumer(collector));
            Add("dome consumer", () => dome.RegisterConsumer(collector), () => dome.RemoveConsumer(collector));
            Add("flat panel consumer", () => flatDevice.RegisterConsumer(collector), () => flatDevice.RemoveConsumer(collector));
            Add("switch consumer", () => switchHub.RegisterConsumer(collector), () => switchHub.RemoveConsumer(collector));

            // NINA creates the sequencer view models only after all plugins have loaded, and its
            // SequenceMediator dereferences them on subscribe; wait until it reports Initialized.
            Add("sequence started", () => sequence.SequenceStarting += OnSequenceStarting, () => sequence.SequenceStarting -= OnSequenceStarting, SequencerReady);
            Add("sequence finished", () => sequence.SequenceFinished += OnSequenceFinished, () => sequence.SequenceFinished -= OnSequenceFinished, SequencerReady);

            Add("filter changed", () => filterWheel.FilterChanged += OnFilterChanged, () => filterWheel.FilterChanged -= OnFilterChanged);
            Add("meridian flip started", () => telescope.BeforeMeridianFlip += OnBeforeMeridianFlip, () => telescope.BeforeMeridianFlip -= OnBeforeMeridianFlip);
            Add("meridian flip finished", () => telescope.AfterMeridianFlip += OnAfterMeridianFlip, () => telescope.AfterMeridianFlip -= OnAfterMeridianFlip);
            Add("mount slewed", () => telescope.Slewed += OnSlewed, () => telescope.Slewed -= OnSlewed);
            Add("mount parked", () => telescope.Parked += OnParked, () => telescope.Parked -= OnParked);
            Add("mount unparked", () => telescope.Unparked += OnUnparked, () => telescope.Unparked -= OnUnparked);
            Add("guiding started", () => guider.GuidingStarted += OnGuidingStarted, () => guider.GuidingStarted -= OnGuidingStarted);
            Add("guiding stopped", () => guider.GuidingStopped += OnGuidingStopped, () => guider.GuidingStopped -= OnGuidingStopped);
            Add("dithered", () => guider.AfterDither += OnAfterDither, () => guider.AfterDither -= OnAfterDither);
            Add("guide steps", () => guider.GuideEvent += OnGuideStep, () => guider.GuideEvent -= OnGuideStep);
            Add("dome opened", () => dome.Opened += OnDomeOpened, () => dome.Opened -= OnDomeOpened);
            Add("dome closed", () => dome.Closed += OnDomeClosed, () => dome.Closed -= OnDomeClosed);
            Add("download timeout", () => camera.DownloadTimeout += OnDownloadTimeout, () => camera.DownloadTimeout -= OnDownloadTimeout);
            Add("image saved", () => imageSave.ImageSaved += OnImageSaved, () => imageSave.ImageSaved -= OnImageSaved);
            Add("image save starting", () => imageSave.BeforeImageSaved += OnBeforeImageSaved, () => imageSave.BeforeImageSaved -= OnBeforeImageSaved);
            // Attached by reflection: the event args type lives in different NINA assemblies by version.
            Add("image prepared", AttachImagePrepared, DetachImagePrepared);
#if NINA33
            Add("image save failed", () => imageSave.ImageSaveFailed += OnImageSaveFailed, () => imageSave.ImageSaveFailed -= OnImageSaveFailed);
#endif
            return list;
        }

        private bool SequencerReady() {
            try {
                return sequence.Initialized;
            } catch (Exception) {
                // Throws until NINA registers its sequence navigation.
                return false;
            }
        }

        private void Subscribe() {
            collector.DomeFollowing = () => dome.IsFollowingScope;
            collector.Changed += OnStateChanged;
            collector.DeviceTransition += OnDeviceTransition;
            collector.SafetyChanged += OnSafetyChanged;
            collector.AutofocusStarting += OnAutofocusStarting;
            collector.AutofocusFinished += OnAutofocusFinished;
            Phone.Changed += OnPhoneChanged;
            Phone.Requested += OnPhoneRequested;
            autofocusReports = new AutofocusReportWatcher(() => RigId, (result, path) => {
                RecordFocusRun(result);
                OnAutofocusReport(result, path);
            });
            _ = Task.Run(ImportFocusBacklog);
            autofocusReports.Start();
            hooks = BuildHooks();
            hooksAttachedAtStart = false;
            AttachPendingHooks();
            hooksAttachedAtStart = true;
            var pending = hooks.Where(h => !h.Attached).Select(h => h.Name).ToList();

            if (pending.Count > 0) {
                NinaLog.Instance.Info($"Waiting for NINA to finish starting before attaching to: {string.Join(", ", pending)}");
            }
        }

        /// <summary>Attaches hooks that are not attached yet. Called at start and on every tick.</summary>
        private void AttachPendingHooks() {
            var list = hooks;

            if (list is null) {
                return;
            }

            lock (list) {
                foreach (var hook in list.Where(h => !h.Attached)) {
                    try {
                        if (!hook.Ready()) {
                            continue;
                        }

                        hook.Attach();
                        hook.Attached = true;

                        if (hooksAttachedAtStart) {
                            NinaLog.Instance.Info($"Attached to {hook.Name} (NINA finished starting)");
                        }
                    } catch (Exception ex) {
                        NinaLog.Instance.Warning($"Could not attach to {hook.Name} yet: {ex.Message}");
                    }
                }
            }
        }

        private void Unsubscribe() {
            collector.Changed -= OnStateChanged;
            collector.DeviceTransition -= OnDeviceTransition;
            collector.SafetyChanged -= OnSafetyChanged;
            collector.AutofocusStarting -= OnAutofocusStarting;
            collector.AutofocusFinished -= OnAutofocusFinished;
            Phone.Changed -= OnPhoneChanged;
            Phone.Requested -= OnPhoneRequested;
            DetachFailureRoot();
            autofocusReports?.Dispose();
            autofocusReports = null;
            var list = hooks;
            hooks = null;

            if (list is null) {
                return;
            }

            lock (list) {
                foreach (var hook in list.Where(h => h.Attached)) {
                    try {
                        hook.Detach();
                    } catch (Exception ex) {
                        NinaLog.Instance.Debug($"Detach from {hook.Name} failed: {ex.Message}");
                    }

                    hook.Attached = false;
                }
            }
        }

        private void OnStateChanged(object sender, EventArgs e) => PushSnapshot();

        private void OnDeviceTransition(object sender, DeviceTransition t) {
            var label = DeviceLabel(t.Device);
            var severity = t.Connected ? Severity.Info : sequenceState.Running ? Severity.Critical : Severity.Info;
            Enqueue(Event(t.Connected ? EventKinds.DeviceConnected : EventKinds.DeviceDisconnected, severity,
                t.Connected ? $"{label} connected" : $"{label} disconnected",
                t.Name,
                new Dictionary<string, object> { ["device"] = t.Device }));
        }

        private void OnSafetyChanged(object sender, bool isSafe) {
            Enqueue(Event(EventKinds.SafetyChanged, isSafe ? Severity.Info : Severity.Critical,
                isSafe ? "Safety monitor reports SAFE" : "Safety monitor reports UNSAFE",
                isSafe ? null : "Conditions are unsafe. NINA will act according to your sequence's safety triggers.",
                new Dictionary<string, object> { ["isSafe"] = isSafe }));
        }

        private void OnAutofocusStarting(object sender, EventArgs e) => BeginAutofocus(null);

        /// <summary>
        /// A run began: NINA's own autofocus says so; Hocus Focus doesn't, so a running sequence item named like an
        /// autofocus also counts. Only the first signal for a run raises the "Autofocus started" event.
        /// </summary>
        private void BeginAutofocus(string engine) {
            var now = DateTimeOffset.UtcNow;
            var snap = collector.Build(sequenceState, nightState, null);
            if (!collector.Focusing.Start(now, engine, snap.FilterWheel?.CurrentFilter, snap.Focuser?.Temperature, snap.Focuser?.Position)) {
                return;
            }
            ResetFocusSurface();
            var filter = snap.FilterWheel?.CurrentFilter;
            Enqueue(Event(EventKinds.AutofocusStarted, Severity.Info, "Autofocusing",
                string.IsNullOrEmpty(filter) ? "Tap to watch the focus curve." : $"With {filter}. Tap to watch the focus curve.",
                new Dictionary<string, object> { ["notify"] = Settings.NotifyAutofocus, ["filter"] = filter, ["position"] = snap.Focuser?.Position }));
            PushSnapshot();
        }

        private void OnAutofocusFinished(object sender, AutoFocusInfo info) {
            IReadOnlyList<EventItem> resolved;

            lock (analysisGate) {
                resolved = watch.ObserveAutofocus(DateTimeOffset.UtcNow);
                // New focus position: the old HFR baseline no longer describes the optics.
                grader.ResetBaselines();
            }

            EnqueueAll(resolved);

            // The report file normally lands within seconds and carries the curve; without one, finish with what we saw.
            var position = info.Position;
            var temperature = StateCollector.Finite(info.Temperature);
            var filter = info.Filter;
            _ = Task.Run(async () => {
                await Task.Delay(TimeSpan.FromSeconds(45));
                if (!collector.Focusing.IsRunning) {
                    return;
                }
                if (collector.Focusing.CompleteWithoutReport((int)Math.Round(position), temperature, string.IsNullOrEmpty(filter) ? null : filter, DateTimeOffset.UtcNow) is { } done) {
                    FinishAutofocus(done);
                }
            });
        }

        private void OnAutofocusReport(AutofocusResult result, string path) {
            var current = collector.Focusing.Current;
            // A report for a run we already closed without one (it came late): just show the curve.
            var late = current is { Status: AutofocusStatus.Finished, FinishedAt: { } at } && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(5) && current.Fit is null;
            var done = collector.Focusing.Complete(result, DateTimeOffset.UtcNow);
            NinaLog.Instance.Info($"Autofocus report {Path.GetFileName(path)}: {done.Points.Count} points, focus {done.Final?.Position:0}");
            if (late) {
                PushSnapshot();
                return;
            }
            FinishAutofocus(done);
        }

        private void FinishAutofocus(AutofocusState run) {
            if (ComputeFocusSurface() is { } sensor) {
                run = collector.Focusing.AttachSensor(sensor) ?? run with { Sensor = sensor };
            }
            var message = new List<string>();
            if (run.Final is { } final) {
                message.Add($"Position {final.Position:0}");
            }
            if ((run.FinalHfr ?? run.Final?.Value) is double hfr && hfr > 0) {
                message.Add($"HFR {hfr:0.00}");
            }
            if (run.Temperature is double t) {
                message.Add($"{t:0.0}°C");
            }
            if (!string.IsNullOrEmpty(run.Filter)) {
                message.Add(run.Filter);
            }

            sessions?.Count(NightManager.Core.Sessions.NightTime.AutofocusRuns);
            Enqueue(Event(EventKinds.AutofocusFinished, Severity.Info, "Autofocus finished", string.Join(" · ", message),
                new Dictionary<string, object> {
                    ["notify"] = Settings.NotifyAutofocus,
                    ["position"] = run.Final?.Position,
                    ["previousPosition"] = run.Previous?.Position,
                    ["hfr"] = run.FinalHfr ?? run.Final?.Value,
                    ["temperature"] = run.Temperature,
                    ["filter"] = run.Filter,
                    ["engine"] = run.Engine,
                    ["durationSeconds"] = run.DurationSeconds,
                    ["points"] = run.Points,
                    ["fit"] = run.Fit,
                    ["sensor"] = run.Sensor,
                }));
            PushSnapshot();
        }

        private Task OnSequenceStarting(object sender, EventArgs e) {
            if (sequenceState.Running) {
                // Already detected by the periodic check (e.g. started before our hook attached).
                return Task.CompletedTask;
            }

            try {
                classicStarted = SequencerReady() && !sequence.IsAdvancedSequenceRunning();
            } catch (Exception) {
                classicStarted = false;
            }
            RefreshSequenceState(runningOverride: true);
            EnqueueAll(sessions.OnSequenceStarted(DateTimeOffset.UtcNow));
            watch.SessionId = sessions.CurrentSessionId;
            SequenceStartedForControl();
            Enqueue(Event(EventKinds.SequenceStarted, Severity.Info, "Sequence started", sequenceState.Title));
            PushSnapshot();
            return Task.CompletedTask;
        }

        private Task OnSequenceFinished(object sender, EventArgs e) {
            classicStarted = false;
            if (!sequenceState.Running) {
                return Task.CompletedTask;
            }

            sessions.OnSequenceFinished(DateTimeOffset.UtcNow);
            RefreshSequenceState(runningOverride: false);
            Enqueue(SequenceEndEvent());
            PushSnapshot();
            return Task.CompletedTask;
        }

        private Task OnFilterChanged(object sender, FilterChangedEventArgs e) {
            Enqueue(Event(EventKinds.FilterChanged, Severity.Info, $"Filter changed to {e.To?.Name ?? "?"}", null,
                new Dictionary<string, object> { ["from"] = e.From?.Name, ["to"] = e.To?.Name }));
            return Task.CompletedTask;
        }

        private Task OnBeforeMeridianFlip(object sender, BeforeMeridianFlipEventArgs e) {
            Enqueue(Event(EventKinds.MeridianFlipStarted, Severity.Info, "Meridian flip started", null));
            flipping = true;
            sessions?.Count(NightManager.Core.Sessions.NightTime.Flips);
            return Task.CompletedTask;
        }

        private Task OnAfterMeridianFlip(object sender, AfterMeridianFlipEventArgs e) {
            flipping = false;
            Enqueue(Event(EventKinds.MeridianFlipFinished, e.Success ? Severity.Info : Severity.Critical,
                e.Success ? "Meridian flip completed" : "Meridian flip FAILED",
                e.Success ? null : "Check the mount: the flip did not complete successfully.",
                new Dictionary<string, object> { ["success"] = e.Success }));
            return Task.CompletedTask;
        }

        private Task OnSlewed(object sender, MountSlewedEventArgs e) {
            var to = e.To?.Transform(Epoch.J2000);
            Enqueue(Event(EventKinds.MountSlewed, Severity.Info, "Mount slewed", null,
                to is null ? null : new Dictionary<string, object> { ["ra"] = Math.Round(to.RA, 5), ["dec"] = Math.Round(to.Dec, 4) }));
            return Task.CompletedTask;
        }

        private Task OnParked(object sender, EventArgs e) {
            Enqueue(Event(EventKinds.MountParked, Severity.Info, "Mount parked", null));
            return Task.CompletedTask;
        }

        private Task OnUnparked(object sender, EventArgs e) {
            Enqueue(Event(EventKinds.MountUnparked, Severity.Info, "Mount unparked", null));
            return Task.CompletedTask;
        }

        private Task OnGuidingStarted(object sender, EventArgs e) {
            collector.Guiding = true;
            Enqueue(Event(EventKinds.GuidingStarted, Severity.Info, "Guiding started", null));
            return Task.CompletedTask;
        }

        private void OnGuideStep(object sender, NINA.Core.Interfaces.IGuideStep step) {
            try {
                collector.GuideSteps.AddStep(step.RADistanceRaw, step.DECDistanceRaw, DateTimeOffset.UtcNow);
                AddLiveGuideStep(step.RADistanceRaw, step.DECDistanceRaw, step.RADuration, step.DECDuration);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Guide step ignored: {ex.Message}");
            }
        }

        private Task OnGuidingStopped(object sender, EventArgs e) {
            collector.GuideSteps.Reset();
            collector.Guiding = false;
            Enqueue(Event(EventKinds.GuidingStopped, Severity.Info, "Guiding stopped", null));
            return Task.CompletedTask;
        }

        private Task OnAfterDither(object sender, EventArgs e) {
            Enqueue(Event(EventKinds.Dithered, Severity.Info, "Dithered", null));
            sessions?.Count(NightManager.Core.Sessions.NightTime.Dithers);
            return Task.CompletedTask;
        }

        private Task OnDomeOpened(object sender, EventArgs e) {
            Enqueue(Event(EventKinds.DomeOpened, Severity.Info, "Dome opened", null));
            return Task.CompletedTask;
        }

        private Task OnDomeClosed(object sender, EventArgs e) {
            Enqueue(Event(EventKinds.DomeClosed, Severity.Info, "Dome closed", null));
            return Task.CompletedTask;
        }

        private Task OnDownloadTimeout(object sender, EventArgs e) {
            Enqueue(Event(EventKinds.DownloadTimeout, Severity.Warning, "Camera download timed out", "NINA did not receive the image from the camera in time."));
            return Task.CompletedTask;
        }

#if NINA33
        // ImageSaveFailed exists from NINA 3.3. On 3.2, Night Watch's "no images saved" incident still catches the symptom.
        private Task OnImageSaveFailed(object sender, ImageSaveFailedEventArgs e) {
            Enqueue(Event(EventKinds.ImageSaveFailed, Severity.Critical,
                e.IsDiskFull ? "Disk full: images are NOT being saved" : "An image could not be saved",
                e.Exception?.Message,
                new Dictionary<string, object> { ["stage"] = e.FailureStage.ToString(), ["diskFull"] = e.IsDiskFull }));
            return Task.CompletedTask;
        }
#endif

        private Task OnBeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            Interlocked.Increment(ref pendingSaves);
            MarkSaveActivity();
            StartOpticsAnalysis(e);
            StartNoiseMeasurement(e);
            return Task.CompletedTask;
        }

        private void OnImageSaved(object sender, ImageSavedEventArgs e) {
            // Saved: no longer pending in NINA's queue (never below zero if we attached mid-save).
            if (Interlocked.Decrement(ref pendingSaves) < 0) {
                Interlocked.Exchange(ref pendingSaves, 0);
            }

            Interlocked.Increment(ref processingFrames);
            MarkSaveActivity();

            // NINA raises this on its save worker; never block it.
            _ = Task.Run(() => {
                try {
                    HandleFrame(e);
                } catch (Exception ex) {
                    NinaLog.Instance.Error("Could not process saved image", ex);
                } finally {
                    Interlocked.Decrement(ref processingFrames);
                    MarkSaveActivity();
                }
            });
        }

        private void MarkSaveActivity() => Interlocked.Exchange(ref lastSaveActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

        #endregion

        private void HandleFrame(ImageSavedEventArgs e) {
            var now = DateTimeOffset.UtcNow;
            var metrics = FrameMapper.ToMetrics(e, now);
            GradeAnalysis analysis;
            IReadOnlyList<EventItem> incidents;

            lock (analysisGate) {
                analysis = grader.Grade(metrics);
                incidents = watch.ObserveFrame(metrics, analysis);
            }

            var frameId = SyncEngine.NewId();
            CountSavedBytes(e.PathToImage);
            if (FrameGrader.IsLight(metrics.ImageType)) {
                Interlocked.Increment(ref lightFramesThisSequence);
            }
            var fileName = Settings.SendFileNames
                ? FrameMapper.FileName(e.PathToImage)
                : $"{metrics.ImageType}_{Interlocked.Increment(ref anonymousFrameCounter):0000}";

            EnqueueAll(sessions.OnFrame(new SessionFrame {
                FrameId = frameId,
                Timestamp = now,
                FileName = fileName,
                LocalPath = FrameMapper.LocalPath(e.PathToImage),
                ImageType = metrics.ImageType,
                Target = metrics.Target,
                Filter = metrics.Filter,
                ExposureSeconds = metrics.ExposureSeconds ?? 0,
                Hfr = metrics.Hfr,
                Stars = metrics.Stars,
                GuidingRms = metrics.GuidingRmsTotal,
                Score = analysis.Grade.Score,
                Verdict = analysis.Grade.Verdict,
                Reasons = analysis.Grade.Reasons
            }));

            watch.SessionId = sessions.CurrentSessionId;
            // Design C: a thumbnail for every sub of 3 min or more; shorter subs are sampled (ThumbnailPolicy).
            // Sub reports switched off: alerts, grading and the night's totals still work here, but nothing per sub is sent.
            var sendFrame = metrics.ImageType == "SNAPSHOT" || DataOn(DataFeatures.Frames);
            var thumbnail = !sendFrame ? null : metrics.ImageType == "SNAPSHOT" || FrameGrader.IsLight(metrics.ImageType) && thumbnailPolicy.ShouldSend(DateTimeOffset.UtcNow, metrics.ExposureSeconds ?? 0, metrics.Target, metrics.Filter, analysis.Grade.Verdict)
                ? MakeThumbnail(e, metrics.ImageType)
                : null;
            if (thumbnail is not null) {
                LatestFrameId = frameId;
            }
            if (FrameGrader.IsLight(metrics.ImageType)) {
                RememberForLocalLink(frameId, e.Image);
            }
            var optics = TakeOptics(e);
            RecordSub(e, metrics, analysis.Grade.Verdict);
            if (sendFrame) {
                var item = FrameMapper.ToItem(frameId, e, metrics, analysis.Grade, sessions.CurrentSessionId, fileName, thumbnail) with { Optics = optics };
                sync.Enqueue(item, thumbnail is null ? default : thumbnail.Bytes);
            }
            EnqueueAll(incidents);
            PushSnapshot();
        }

        private EncodedThumbnail MakeThumbnail(ImageSavedEventArgs e, string imageType) {
            if (!DataOn(DataFeatures.Thumbnails) || !(FrameGrader.IsLight(imageType) || imageType == "SNAPSHOT")) {
                return null;
            }

            try {
                return ThumbnailEncoder.Encode(e.Image, Settings.ThumbnailMaxDimension, Settings.ThumbnailQuality);
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Thumbnail could not be created: {ex.Message}");
                return null;
            }
        }

        private void OnSessionEnded(object sender, SessionEnded ended) {
            IReadOnlyList<EventItem> resolved;

            lock (analysisGate) {
                resolved = watch.ResolveAll(DateTimeOffset.UtcNow, "Session ended");
                watch.SessionId = null;
            }

            EnqueueAll(resolved);

            if (Settings.WriteCullList) {
                WriteCullList(ended);
            }
            // The night's over: the sub-length advice, once (one small write).
            SendExposureAdvice();
            SendFocusAdvice();
        }

        private void WriteCullList(SessionEnded ended) {
            var lights = ended.Frames.Where(f => FrameGrader.IsLight(f.ImageType)).ToList();

            if (lights.Count == 0) {
                return;
            }

            try {
                var folder = lights.Select(f => f.LocalPath).Where(p => p is not null).Select(Path.GetDirectoryName).FirstOrDefault()
                    ?? profiles.ActiveProfile?.ImageFileSettings?.FilePath;

                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
                    return;
                }

                var baseName = CullReport.BaseName(ended.Item.SessionId, ended.Item.StartedAt);
                File.WriteAllText(Path.Combine(folder, baseName + "_grades.csv"), CullReport.BuildCsv(lights));
                var rejects = CullReport.BuildRejectList(lights);

                if (rejects.Length > 0) {
                    File.WriteAllText(Path.Combine(folder, baseName + "_rejects.txt"), rejects);
                }

                NinaLog.Instance.Info($"Cull list written to {folder} ({ended.Item.Totals.Rejected} rejects of {lights.Count} lights).");
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Cull list could not be written: {ex.Message}");
            }
        }

        private void Tick() {
            if (Interlocked.Exchange(ref ticking, 1) == 1) {
                return;
            }

            try {
                AttachPendingHooks();
                ResolveSetup();
                var now = DateTimeOffset.UtcNow;
                RefreshSequenceState();

                if (now - nightComputedAt >= NightRefresh) {
                    nightState = ComputeNight();
                    nightComputedAt = now;
                }

                EnqueueAll(sessions.Tick(now, nightState));
                TickFailures(now);
                MaybeExportSequence(now);
                MaybeSendCatalog(now);
                MaybeSendForecast(now);
                MaybeImportHistory(now);
                RefreshStorage(now);
                TickReadiness(now);
                TickNightTime(now);
                TickCalibrationScan(now);
                if (collector.Focusing.Tick(now, autofocusInSequence) is { } gaveUp) {
                    Enqueue(Event(EventKinds.AutofocusFinished, Severity.Warning, "Autofocus didn't report a result",
                        $"No result {Math.Round((now - gaveUp.StartedAt).TotalMinutes)} min after it started.",
                        new Dictionary<string, object> { ["failed"] = true }));
                }
                PushSnapshot();
                MaybeRefreshScheduler(now);
            } catch (Exception ex) {
                NinaLog.Instance.Error("Tick failed", ex);
            } finally {
                Volatile.Write(ref ticking, 0);
            }
        }

        private bool flipping;
        private DateTimeOffset lastNightTimeTick;

        /// <summary>Puts the seconds since the last tick in a bucket, during astronomical darkness only.</summary>
        private void TickNightTime(DateTimeOffset now) {
            var previous = lastNightTimeTick;
            lastNightTimeTick = now;
            var night = nightState;
            if (previous == default || night?.AstronomicalDusk is not { } dusk || night.AstronomicalDawn is not { } dawn) return;
            // The plugin reports the coming night: dawn is the next morning's.
            var dark = dawn > dusk ? now >= dusk && now < dawn : now >= dusk || now < dawn;
            if (!dark) return;
            var seconds = Math.Min((now - previous).TotalSeconds, 30);
            var s = lastSnapshot;
            var bucket = NightManager.Core.Sessions.NightTime.Classify(new() {
                SequenceRunning = sequenceState.Running,
                AutofocusRunning = collector.Focusing.IsRunning,
                Flipping = flipping,
                Unsafe = s?.Safety is { Connected: true, IsSafe: false },
                Slewing = s?.Mount?.Slewing == true,
                Activity = s?.Activity?.State,
                Waiting = sequenceState.Waiting,
                Lines = s?.Activity?.Lines?.Select(l => (l.Source, l.Text)).ToList() ?? [],
            });
            sessions?.AddTime(bucket, seconds);
        }

        private void StartScheduler() {
            if (messageBroker is null) {
                return;
            }
            scheduler = new TargetSchedulerBridge(
                messageBroker,
                info => Enqueue(Event(info.Kind, Severity.Info, info.Title, info.Message, info.Data.ToDictionary(d => d.Key, d => d.Value))),
                () => {
                    // A TS transition: show it right away and refresh the plan and progress shortly after.
                    RefreshSequenceState();
                    PushSnapshot();
                    var soon = DateTimeOffset.UtcNow.AddSeconds(30);
                    if (soon < nextSchedulerRefresh) {
                        nextSchedulerRefresh = soon;
                    }
                });
            scheduler.Start();
            nextSchedulerRefresh = TargetSchedulerBridge.IsInstalled ? DateTimeOffset.UtcNow.AddSeconds(20) : DateTimeOffset.MaxValue;
        }

        /// <summary>Collaborator management with this PC's credential, or null when not paired.</summary>
        public NightManager.Core.Sharing.CollaboratorsClient Collaborators() =>
            transport is not null && CurrentCredential() is { } current ? new NightManager.Core.Sharing.CollaboratorsClient(transport, current) : null;

        /// <summary>Plain-words Target Scheduler status for the options page.</summary>
        public string SchedulerSummary {
            get {
                if (!TargetSchedulerBridge.IsInstalled) {
                    return "Target Scheduler isn't installed for this NINA.";
                }
                var live = scheduler?.Current switch {
                    { Status: SchedulerStatus.Imaging } s => $"imaging {s.TargetName}" + (s.ProjectName is null ? "" : $" ({s.ProjectName})"),
                    { Status: SchedulerStatus.Waiting } s => s.NextTargetName is null ? "waiting" : $"waiting for {s.NextTargetName}",
                    { Status: SchedulerStatus.Stopped } => "stopped",
                    _ => "no activity yet this session",
                };
                return $"Target Scheduler detected: {live}. {scheduler?.LastPlanNote ?? "Reading projects…"}";
            }
        }

        /// <summary>The profile's custom horizon (NINA's own interpolation), every couple of degrees; null without one.</summary>
        private IReadOnlyList<double> SampleHorizon() {
            try {
                var horizon = profiles.ActiveProfile?.AstrometrySettings?.Horizon;
                if (horizon is null) {
                    return null;
                }
                return Enumerable.Range(0, 360 / SchedulerPlanItem.HorizonStepDegrees)
                    .Select(i => Math.Round(Math.Clamp(horizon.GetAltitude(i * SchedulerPlanItem.HorizonStepDegrees), -90, 90), 1))
                    .ToList();
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Custom horizon not read: {ex.Message}");
                return null;
            }
        }

        private void MaybeRefreshScheduler(DateTimeOffset now) {
            if (scheduler is null || sync is null || !sync.DataOffKnown || !DataOn(DataFeatures.Scheduler) || now < nextSchedulerRefresh || Interlocked.Exchange(ref schedulerRefreshing, 1) == 1) {
                return;
            }
            nextSchedulerRefresh = now + SchedulerRefresh;
            var profileId = RigId;
            var token = lifetime?.Token ?? CancellationToken.None;

            _ = Task.Run(async () => {
                try {
                    var item = await scheduler.BuildPlanAsync(profileId, token);
                    if (item is null) {
                        return;
                    }
                    item = item with { Horizon = SampleHorizon() };
                    var signature = SchedulerPreview.Signature(item);
                    lastSchedulerPlan = item;
                    if (signature != lastSchedulerSignature || DateTimeOffset.UtcNow - lastSchedulerSent > SchedulerResend) {
                        Enqueue(item);
                        lastSchedulerSignature = signature;
                        lastSchedulerSent = DateTimeOffset.UtcNow;
                        NinaLog.Instance.Info($"Target Scheduler: {item.Projects.Count} active project(s), plan {(item.PreviewAvailable ? $"{item.Plan.Count} entr(ies)" : "unavailable")}");
                    }
                    StatusChanged?.Invoke(this, EventArgs.Empty);
                } catch (OperationCanceledException) {
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"Target Scheduler data could not be read: {ex.Message}");
                } finally {
                    Volatile.Write(ref schedulerRefreshing, 0);
                }
            });
        }

        private void PushSnapshot() {
            if (sync is null) {
                return;
            }

            var snapshot = collector.Build(sequenceState, nightState, sessions?.Summary, scheduler?.Current) with {
                Phone = Phone.State,
                Syncthing = syncthing?.State,
                Storage = storageState,
                Cameras = CameraSummaries(),
                Readiness = readiness,
            };
            snapshot = snapshot with { Activity = CurrentActivity(snapshot.Camera) };
            lastSnapshot = snapshot;
            sync.UpdateSnapshot(snapshot, NightManager.Core.Live.SnapshotSignature.Compute(snapshot));
            IReadOnlyList<EventItem> incidents;

            lock (analysisGate) {
                incidents = watch.ObserveSnapshot(snapshot, DateTimeOffset.UtcNow);
            }

            EnqueueAll(incidents);
        }

        private void RefreshSequenceState(bool? runningOverride = null) {
            lock (sequenceGate) {
                RefreshSequenceStateLocked(runningOverride);
            }
        }

        private void RefreshSequenceStateLocked(bool? runningOverride) {
            try {
                var ready = SequencerReady();
                var wasRunning = sequenceState.Running;
                // The classic (simple) sequencer runs on the same engine but NINA's "is running" / "running items"
                // calls only cover the Advanced Sequencer, so a classic run is read from its own root container.
                var advancedRunning = ready && sequence.IsAdvancedSequenceRunning();
                var classicRoot = ready ? ClassicRoot() : null;
                var classicRunning = !advancedRunning && IsClassicRunning(classicRoot);
                var running = runningOverride ?? (advancedRunning || classicRunning);
                IReadOnlyCollection<ISequenceItem> items = !running || !ready ? null
                    : classicRunning ? SequenceReader.RunningItems(classicRoot)
                    : sequence.GetAdvancedSequencerCurrentRunningItems();
                var target = items?.Select(FindTarget).FirstOrDefault(t => t is not null);
                var targetName = target?.Target?.TargetName;
                if (string.IsNullOrWhiteSpace(targetName)) {
                    targetName = target?.Target?.DeepSkyObject?.Name;
                }
                // Target Scheduler clears its container's target (empty name, RA/Dec 0/0) while waiting or between plans.
                var coordinates = string.IsNullOrWhiteSpace(targetName) ? null : target?.Target?.InputCoordinates?.Coordinates?.Transform(Epoch.J2000);
                double? ra = coordinates is null ? null : Math.Round(coordinates.RA, 5);
                double? dec = coordinates is null ? null : Math.Round(coordinates.Dec, 4);
                if (string.IsNullOrWhiteSpace(targetName) && running && scheduler?.Current is { Status: SchedulerStatus.Imaging } ts) {
                    targetName = ts.TargetName;
                    ra = ts.TargetRightAscension;
                    dec = ts.TargetDeclination;
                }
                var title = classicRunning
                    ? (classicRoot as SequenceRootContainer)?.SequenceTitle ?? "Classic sequence"
                    : Path.GetFileNameWithoutExtension(SafeSavePath());
                var waiting = running ? SequenceReader.Waiting(items, DateTimeOffset.UtcNow) : null;
                IReadOnlyList<SequenceTarget> targets = null;
                if (ready) {
                    try {
                        targets = classicRunning
                            ? SequenceReader.Targets(sequence.GetAllTargetsInSimpleSequence())
                            : SequenceReader.Targets(sequence.GetAllTargetsInAdvancedSequence());
                        // Nothing running: show whichever sequencer actually has exposures planned.
                        if (!running && !targets.Any(t => t.Exposures.Count > 0)) {
                            var classic = SequenceReader.Targets(sequence.GetAllTargetsInSimpleSequence());
                            if (classic.Any(t => t.Exposures.Count > 0)) {
                                targets = classic;
                            }
                        }
                    } catch (Exception ex) {
                        NinaLog.Instance.Debug($"Sequence targets unavailable: {ex.Message}");
                    }
                }

                sequenceState = new SequenceState {
                    Running = running,
                    Title = string.IsNullOrWhiteSpace(title) ? null : title,
                    TargetName = string.IsNullOrWhiteSpace(targetName) ? null : targetName,
                    TargetRightAscension = ra,
                    TargetDeclination = dec,
                    RunningItems = items?.Select(i => i.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct().Take(5).ToList(),
                    StartedAt = running ? sequenceState.StartedAt ?? DateTimeOffset.UtcNow : null,
                    Waiting = waiting?.Label,
                    WaitingUntil = waiting?.Until,
                    Targets = targets is { Count: > 0 } ? targets : null
                };

                AttachFailureRoot(items);

                if (items?.Any(i => IsAutofocusItem(i?.Name)) == true) {
                    autofocusInSequence = true;
                    BeginAutofocus(items.Any(i => i?.Name?.Contains("Hocus", StringComparison.OrdinalIgnoreCase) == true) ? "Hocus Focus" : null);
                } else {
                    autofocusInSequence = false;
                }

                if (runningOverride is null && running != wasRunning) {
                    // The start/finish event was missed (e.g. "--runsequence" starts before plugins can hook in).
                    var now = DateTimeOffset.UtcNow;

                    if (running) {
                        EnqueueAll(sessions.OnSequenceStarted(now));
                        watch.SessionId = sessions.CurrentSessionId;
                        SequenceStartedForControl();
                        Enqueue(Event(EventKinds.SequenceStarted, Severity.Info, "Sequence started", sequenceState.Title));
                    } else {
                        sessions.OnSequenceFinished(now);
                        Enqueue(SequenceEndEvent());
                    }
                }

                if (sequenceState.TargetName is { } name && !string.Equals(name, lastTarget, StringComparison.OrdinalIgnoreCase)) {
                    lastTarget = name;
                    Enqueue(Event(EventKinds.TargetChanged, Severity.Info, $"Now imaging {name}", null,
                        new Dictionary<string, object> { ["target"] = name, ["ra"] = sequenceState.TargetRightAscension, ["dec"] = sequenceState.TargetDeclination }));
                }
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Sequence state unavailable: {ex.Message}");
            }
        }

        private string SafeSavePath() {
            try {
                return sequence.GetAdvancedSequencerSavePath();
            } catch (Exception) {
                return null;
            }
        }

        private static IDeepSkyObjectContainer FindTarget(ISequenceItem item) {
            ISequenceContainer current = item as ISequenceContainer ?? (item as SequenceItem)?.Parent;

            for (var depth = 0; current is not null && depth < 50; depth++) {
                if (current is IDeepSkyObjectContainer dso) {
                    return dso;
                }

                current = (current as SequenceItem)?.Parent;
            }

            return null;
        }

        private NightState ComputeNight() {
            try {
                var data = nighttime.Calculate();
                var astrometry = profiles.ActiveProfile?.AstrometrySettings;
                return new NightState {
                    Sunset = Utc(data.SunRiseAndSet?.Set),
                    NauticalDusk = Utc(data.NauticalTwilightRiseAndSet?.Set),
                    AstronomicalDusk = Utc(data.TwilightRiseAndSet?.Set),
                    AstronomicalDawn = Utc(data.TwilightRiseAndSet?.Rise),
                    NauticalDawn = Utc(data.NauticalTwilightRiseAndSet?.Rise),
                    Sunrise = Utc(data.SunRiseAndSet?.Rise),
                    MoonIllumination = data.Illumination is double illumination ? Math.Round(illumination, 3) : null,
                    Latitude = astrometry is null ? null : Math.Round(astrometry.Latitude, 3),
                    Longitude = astrometry is null ? null : Math.Round(astrometry.Longitude, 3),
                    Elevation = astrometry is null ? null : Math.Round(astrometry.Elevation),
                    UtcOffsetMinutes = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes)
                };
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Night ephemeris unavailable: {ex.Message}");
                return nightState;
            }
        }

        private static DateTimeOffset? Utc(DateTime? value) {
            if (value is not DateTime time) {
                return null;
            }

            var specified = time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Local) : time;
            return new DateTimeOffset(specified.ToUniversalTime(), TimeSpan.Zero);
        }

        private async void OnCommandsReceived(object sender, IReadOnlyList<RemoteCommand> received) {
            try {
                // Each instance only acts on commands for its own rig (or unaddressed, observatory-wide ones).
                var mine = received.Where(c => c.RigId is null || string.Equals(c.RigId, RigId, StringComparison.OrdinalIgnoreCase)).ToList();
                var results = await commands.ProcessAsync(mine, lifetime.Token);
                EnqueueAll(results);
                sync.Wake();
            } catch (OperationCanceledException) {
            } catch (Exception ex) {
                NinaLog.Instance.Error("Remote command processing failed", ex);
            }
        }

        private ClientInfo BuildClientInfo() => new() {
            RigId = RigId,
            RigName = RigName,
            SetupId = Setup?.SetupId ?? RigId,
            SetupName = Setup?.SetupName ?? RigName,
            RigRole = (Setup?.Role ?? RigRole.Main) == RigRole.Main ? "main" : "linked",
            PluginVersion = PluginVersion,
            NinaVersion = CoreUtil.Version,
            MachineName = Environment.MachineName,
            // Linked optical paths never offer mount/dome actions; their main rig does.
            AllowedCommands = CommandProcessor.AlwaysAllowed.Concat(Settings.AllowedCommands)
                .Where(c => Setup?.ControlsMount != false || !CommandProcessor.MountLevel.Contains(c))
                .Distinct()
                .ToList(),
            Capabilities = Settings.Enabled ? ["live"] : null,
            DataOff = Settings.DataOff(),
            Equipment = BuildEquipment(),
            LocalLink = LocalLinkInfo(),
            PcNetwork = PcNetwork(),
        };

        /// <summary>Optional data the owner switched off in the app, as the backend last said.</summary>
        public IReadOnlySet<string> AppDataOff => sync?.DataOff;

        /// <summary>
        /// Optional data is collected and sent only when it's on in NINA's options and the owner hasn't switched it off in
        /// the app. Until the backend first answers, the app's choice isn't known: only what can't wait goes out.
        /// </summary>
        private bool DataOn(string feature) =>
            !Settings.DataOff().Contains(feature) && sync?.DataOff.Contains(feature) != true;

        private static bool IsAutofocusItem(string name) =>
            !string.IsNullOrEmpty(name) && (name.Contains("autofocus", StringComparison.OrdinalIgnoreCase) || name.Contains("auto focus", StringComparison.OrdinalIgnoreCase));

        private EventItem Event(string kind, string severity, string title, string message, IReadOnlyDictionary<string, object> data = null) => new() {
            Id = SyncEngine.NewId(),
            Timestamp = DateTimeOffset.UtcNow,
            Kind = kind,
            Severity = severity,
            Title = title,
            Message = message,
            SessionId = sessions?.CurrentSessionId,
            Data = data
        };

        private void Enqueue(IngestItem item) {
            try {
                sync?.Enqueue(item);
            } catch (Exception ex) {
                NinaLog.Instance.Error("Could not queue item", ex);
            }
            if (item is EventItem e) {
                try {
                    NoticeKitError(e);
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"Camera alert could not be raised: {ex.Message}");
                }
            }
        }

        private void EnqueueAll(IEnumerable<IngestItem> items) {
            foreach (var item in items) {
                Enqueue(item);
            }
        }

        private static string DeviceLabel(string device) => device switch {
            "camera" => "Camera",
            "mount" => "Mount",
            "guider" => "Guider",
            "focuser" => "Focuser",
            "filterWheel" => "Filter wheel",
            "rotator" => "Rotator",
            "weather" => "Weather station",
            "safety" => "Safety monitor",
            "dome" => "Dome",
            "flatDevice" => "Flat panel",
            _ => device
        };
    }
}
