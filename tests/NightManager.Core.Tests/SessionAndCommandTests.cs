using Microsoft.Extensions.Time.Testing;
using NightManager.Core.Commands;
using NightManager.Core.Contracts;
using NightManager.Core.Sessions;

namespace NightManager.Core.Tests;

public class SessionTrackerTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "nm-session-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    private string StatePath => Path.Combine(root, "session.json");

    internal static SessionFrame Frame(int minute, string verdict = Verdicts.Accept, string filter = "Ha", string target = "M42", string type = "LIGHT", double hfr = 2.0) => new() {
        FrameId = $"f{minute}",
        Timestamp = T0.AddMinutes(minute),
        FileName = $"{target}_{filter}_{minute:000}.fits",
        LocalPath = $@"D:\Astro\{target}\{target}_{filter}_{minute:000}.fits",
        ImageType = type,
        Target = target,
        Filter = filter,
        ExposureSeconds = 300,
        Hfr = hfr,
        Stars = 400,
        GuidingRms = 0.6,
        Score = verdict == Verdicts.Reject ? 20 : 90,
        Verdict = verdict,
        Reasons = verdict == Verdicts.Reject ? ["Only 50 stars"] : []
    };

    [Test]
    public void SequenceStart_StartsSession_OnlyOnce() {
        var tracker = new SessionTracker(StatePath);

        var first = tracker.OnSequenceStarted(T0);
        var second = tracker.OnSequenceStarted(T0.AddMinutes(1));

        Assert.That(first.OfType<SessionItem>().Single().Phase, Is.EqualTo(SessionPhases.Started));
        Assert.That(second, Is.Empty);
        Assert.That(tracker.CurrentSessionId, Is.Not.Null);
    }

    [Test]
    public void CalibrationFramesAlone_DoNotStartASession() {
        var tracker = new SessionTracker(StatePath);

        Assert.That(tracker.OnFrame(Frame(0, Verdicts.NotGraded, type: "DARK")), Is.Empty);
        Assert.That(tracker.CurrentSessionId, Is.Null);
    }

    [Test]
    public void Totals_GroupByTargetAndFilter_AndExcludeRejectsFromUsableTime() {
        var tracker = new SessionTracker(StatePath);
        tracker.OnFrame(Frame(0));
        tracker.OnFrame(Frame(5));
        tracker.OnFrame(Frame(10, Verdicts.Reject));
        tracker.OnFrame(Frame(15, Verdicts.Marginal, filter: "OIII", hfr: 2.4));
        tracker.OnFrame(Frame(20, Verdicts.NotGraded, type: "FLAT"));

        var ended = tracker.End(T0.AddHours(1), "manual")!;
        var totals = ended.Item.Totals;

        Assert.That(totals.Frames, Is.EqualTo(5));
        Assert.That(totals.LightFrames, Is.EqualTo(4));
        Assert.That((totals.Accepted, totals.Marginal, totals.Rejected), Is.EqualTo((2, 1, 1)));
        Assert.That(totals.IntegrationSeconds, Is.EqualTo(1200));
        Assert.That(totals.AcceptedIntegrationSeconds, Is.EqualTo(900));
        Assert.That(totals.BestHfr, Is.EqualTo(2.0));
        var m42 = totals.Targets.Single();
        Assert.That(m42.Filters.Select(f => (f.Filter, f.Frames, f.Accepted)), Is.EqualTo(new[] { ("Ha", 3, 2), ("OIII", 1, 1) }));
        Assert.That(ended.Item.EndReason, Is.EqualTo("manual"));
        Assert.That(tracker.CurrentSessionId, Is.Null);
    }

    [Test]
    public void Session_SurvivesRestart() {
        var before = new SessionTracker(StatePath);
        before.OnSequenceStarted(T0);
        before.OnFrame(Frame(5));
        var id = before.CurrentSessionId;

        var after = new SessionTracker(StatePath);
        after.OnFrame(Frame(10));

        Assert.That(after.CurrentSessionId, Is.EqualTo(id));
        Assert.That(after.Summary!.Frames, Is.EqualTo(2));
    }

    [Test]
    public void SequenceFinished_EndsAfterGrace_UnlessRestarted() {
        var tracker = new SessionTracker(StatePath);
        tracker.OnSequenceStarted(T0);
        tracker.OnSequenceFinished(T0.AddMinutes(60));

        Assert.That(tracker.Tick(T0.AddMinutes(70), null).OfType<SessionItem>().Where(s => s.Phase == SessionPhases.Ended), Is.Empty);

        // Restarting the sequence within the grace period keeps the same night.
        tracker.OnSequenceStarted(T0.AddMinutes(75));
        tracker.OnSequenceFinished(T0.AddMinutes(200));
        var ended = tracker.Tick(T0.AddMinutes(221), null).OfType<SessionItem>().Single(s => s.Phase == SessionPhases.Ended);

        Assert.That(ended.EndReason, Is.EqualTo("sequenceFinished"));
    }

    [Test]
    public void Idle_EndsSession_AndRaisesEndedWithFramesForCullList() {
        var tracker = new SessionTracker(StatePath);
        SessionEnded? ended = null;
        tracker.Ended += (_, e) => ended = e;
        tracker.OnFrame(Frame(0));

        tracker.Tick(T0.AddMinutes(46), null);

        Assert.That(ended, Is.Not.Null);
        Assert.That(ended!.Item.EndReason, Is.EqualTo("idle"));
        Assert.That(ended.Frames, Has.Count.EqualTo(1));
    }

    [Test]
    public void Dawn_EndsSessionWhenNoSequenceRuns() {
        var tracker = new SessionTracker(StatePath);
        tracker.OnFrame(Frame(0));
        var night = new NightState { Sunrise = T0.AddMinutes(30) };

        var items = tracker.Tick(T0.AddMinutes(31), night);

        Assert.That(items.OfType<SessionItem>().Single().EndReason, Is.EqualTo("dawn"));
    }

    [Test]
    public void Dawn_EndsSessionWhenALoopingSequenceGoesQuiet() {
        // Target Scheduler's loop starts at dusk, waits out a cloudy night and is still running at dawn.
        var tracker = new SessionTracker(StatePath);
        tracker.OnSequenceStarted(T0);
        var night = new NightState { Sunrise = T0.AddHours(12) };

        Assert.That(tracker.Tick(T0.AddHours(11), night).OfType<SessionItem>().Where(s => s.Phase == SessionPhases.Ended), Is.Empty);
        var ended = tracker.Tick(T0.AddHours(12).AddMinutes(1), night).OfType<SessionItem>().Single(s => s.Phase == SessionPhases.Ended);

        Assert.That(ended.EndReason, Is.EqualTo("dawn"));
        Assert.That(tracker.CurrentSessionId, Is.Null);
    }

    [Test]
    public void Dawn_WaitsForDawnFlats_WhileTheSequenceRuns() {
        var tracker = new SessionTracker(StatePath);
        tracker.OnSequenceStarted(T0);
        var night = new NightState { Sunrise = T0.AddMinutes(30) };
        tracker.OnFrame(Frame(25, Verdicts.NotGraded, type: "FLAT"));

        Assert.That(tracker.Tick(T0.AddMinutes(40), night).OfType<SessionItem>().Where(s => s.Phase == SessionPhases.Ended), Is.Empty);
        Assert.That(tracker.Tick(T0.AddMinutes(71), night).OfType<SessionItem>().Single(s => s.Phase == SessionPhases.Ended).EndReason, Is.EqualTo("dawn"));
    }

    [Test]
    public void Dawn_IsAstronomicalDawn_OnceTheLastSubsAreIn() {
        var tracker = new SessionTracker(StatePath);
        tracker.OnSequenceStarted(T0);
        var night = new NightState { AstronomicalDawn = T0.AddHours(10), Sunrise = T0.AddHours(11).AddMinutes(30) };
        tracker.OnFrame(Frame(10 * 60 - 5));

        // Still imaging just after dawn (a sub two minutes ago): open.
        tracker.OnFrame(Frame(10 * 60 + 3));
        Assert.That(tracker.Tick(T0.AddHours(10).AddMinutes(5), night).OfType<SessionItem>().Where(s => s.Phase == SessionPhases.Ended), Is.Empty);
        // Ten minutes after the last sub (well before sunrise), the night is over.
        var ended = tracker.Tick(T0.AddHours(10).AddMinutes(25), night).OfType<SessionItem>().Single(s => s.Phase == SessionPhases.Ended);
        Assert.That(ended.EndReason, Is.EqualTo("dawn"));
    }

    [Test]
    public void Dawn_WithoutAstronomicalDarkness_UsesNauticalDawn() {
        Assert.That(SessionTracker.Dawn(new NightState { NauticalDawn = T0, Sunrise = T0.AddHours(1) }), Is.EqualTo(T0));
        Assert.That(SessionTracker.Dawn(new NightState { Sunrise = T0 }), Is.EqualTo(T0));
        Assert.That(SessionTracker.Dawn(null), Is.Null);
    }

    [Test]
    public void NextNight_AfterALoopingSequence_IsANewSessionThatKnowsTheSequenceRuns() {
        var tracker = new SessionTracker(StatePath);
        tracker.OnSequenceStarted(T0);
        tracker.Tick(T0.AddHours(12).AddMinutes(1), new NightState { Sunrise = T0.AddHours(12) });

        // The loop takes its first sub the next evening: a new night, starting at that sub.
        var started = tracker.OnFrame(Frame(24 * 60)).OfType<SessionItem>().Single();
        Assert.That(started.StartedAt, Is.EqualTo(T0.AddHours(24)));

        // A cloudy hour mid-night doesn't split it: the sequence is still running.
        var tomorrow = new NightState { Sunrise = T0.AddHours(36) };
        Assert.That(tracker.Tick(T0.AddHours(25).AddMinutes(30), tomorrow).OfType<SessionItem>().Where(s => s.Phase == SessionPhases.Ended), Is.Empty);
    }

    [Test]
    public void LongerThanANight_EndsAtTheNextQuietSpell_EvenWithoutEphemeris() {
        var tracker = new SessionTracker(StatePath);
        tracker.OnSequenceStarted(T0);

        Assert.That(tracker.Tick(T0.AddHours(19), null).OfType<SessionItem>().Where(s => s.Phase == SessionPhases.Ended), Is.Empty);
        Assert.That(tracker.Tick(T0.AddHours(20), null).OfType<SessionItem>().Single(s => s.Phase == SessionPhases.Ended).EndReason, Is.EqualTo("dawn"));
    }

    [Test]
    public void CullReport_ListsRejects_AndEscapesCsv() {
        var frames = new[] {
            Frame(0),
            Frame(5, Verdicts.Reject) with { Target = "=HYPERLINK(\"x\")", Reasons = ["Only 50 stars, likely clouds"] }
        };

        var csv = CullReport.BuildCsv(frames);
        var rejects = CullReport.BuildRejectList(frames);

        Assert.That(rejects.Trim(), Is.EqualTo(@"D:\Astro\M42\M42_Ha_005.fits"));
        Assert.That(csv, Does.Contain("\"'=HYPERLINK(\"\"x\"\")\""), "Formula injection must be neutralised.");
        Assert.That(csv, Does.Contain("\"Only 50 stars, likely clouds\""));
        Assert.That(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(3));
    }
}

public class CommandProcessorTests {
    private FakeTimeProvider time = null!;
    private FakeExecutor executor = null!;
    private HashSet<string> allowed = null!;

    [SetUp]
    public void SetUp() {
        time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.Zero));
        executor = new FakeExecutor();
        allowed = [CommandTypes.AbortSequence];
    }

    private CommandProcessor Create(string? path = null) => new(executor, () => allowed, path, time);

    private RemoteCommand Command(string type, string id = "c1", int expiresInMinutes = 5) => new() {
        Id = id,
        Type = type,
        IssuedAt = time.GetUtcNow(),
        ExpiresAt = time.GetUtcNow().AddMinutes(expiresInMinutes),
        IssuedBy = "owner@example.com"
    };

    [Test]
    public async Task AllowedCommand_Executes() {
        var result = (await Create().ProcessAsync([Command(CommandTypes.AbortSequence)], CancellationToken.None)).Single();

        Assert.That(result.Outcome, Is.EqualTo(CommandOutcomes.Executed));
        Assert.That(executor.Calls, Is.EqualTo(new[] { "abort" }));
    }

    [Test]
    public async Task DisallowedCommand_IsRejectedWithoutTouchingEquipment() {
        var result = (await Create().ProcessAsync([Command(CommandTypes.ParkMount)], CancellationToken.None)).Single();

        Assert.That(result.Outcome, Is.EqualTo(CommandOutcomes.Rejected));
        Assert.That(result.Message, Does.Contain("disabled"));
        Assert.That(executor.Calls, Is.Empty);
    }

    [Test]
    public async Task Ping_IsAlwaysAllowed() {
        allowed.Clear();

        var result = (await Create().ProcessAsync([Command(CommandTypes.Ping)], CancellationToken.None)).Single();

        Assert.That(result.Outcome, Is.EqualTo(CommandOutcomes.Executed));
    }

    [Test]
    public async Task ExpiredCommand_IsNeverExecuted() {
        var command = Command(CommandTypes.AbortSequence);
        time.Advance(TimeSpan.FromMinutes(6));

        var result = (await Create().ProcessAsync([command], CancellationToken.None)).Single();

        Assert.That(result.Outcome, Is.EqualTo(CommandOutcomes.Expired));
        Assert.That(executor.Calls, Is.Empty);
    }

    [Test]
    public async Task DuplicateDelivery_ExecutesOnce_EvenAcrossRestarts() {
        var path = Path.Combine(Path.GetTempPath(), "nm-cmd-" + Guid.NewGuid().ToString("N") + ".txt");

        try {
            await Create(path).ProcessAsync([Command(CommandTypes.AbortSequence)], CancellationToken.None);
            var again = await Create(path).ProcessAsync([Command(CommandTypes.AbortSequence)], CancellationToken.None);

            Assert.That(again, Is.Empty);
            Assert.That(executor.Calls, Has.Count.EqualTo(1));
        } finally {
            File.Delete(path);
        }
    }

    [Test]
    public async Task SafeShutdown_ContinuesPastFailures_AndSkipsMissingEquipment() {
        allowed.Add(CommandTypes.SafeShutdown);
        executor.Unavailable.Add(CommandTypes.CloseDome);
        executor.Failing.Add("park");

        var result = (await Create().ProcessAsync([Command(CommandTypes.SafeShutdown)], CancellationToken.None)).Single();

        Assert.That(executor.Calls, Is.EqualTo(new[] { "abort", "stopGuiding", "park", "warm" }));
        Assert.That(result.Outcome, Is.EqualTo(CommandOutcomes.Failed));
        Assert.That(result.Message, Does.Contain("Done: abort sequence, stop guiding, warm camera").And.Contain("Failed: park mount"));
    }

    [Test]
    public async Task UnavailableEquipment_FailsWithClearMessage() {
        allowed.Add(CommandTypes.CloseDome);
        executor.Unavailable.Add(CommandTypes.CloseDome);

        var result = (await Create().ProcessAsync([Command(CommandTypes.CloseDome)], CancellationToken.None)).Single();

        Assert.That(result.Outcome, Is.EqualTo(CommandOutcomes.Failed));
        Assert.That(result.Message, Is.EqualTo("No dome is connected."));
    }

    private sealed class FakeExecutor : ICommandExecutor {
        public List<string> Calls { get; } = [];

        public HashSet<string> Unavailable { get; } = [];

        public HashSet<string> Failing { get; } = [];

        public Task AbortSequenceAsync(CancellationToken token) => Run("abort");

        public Task StopGuidingAsync(CancellationToken token) => Run("stopGuiding");

        public Task ParkMountAsync(CancellationToken token) => Run("park");

        public Task CloseDomeAsync(CancellationToken token) => Run("closeDome");

        public Task WarmCameraAsync(CancellationToken token) => Run("warm");

        public bool IsAvailable(string commandType) => !Unavailable.Contains(commandType);

        private Task Run(string name) {
            Calls.Add(name);
            return Failing.Contains(name) ? Task.FromException(new InvalidOperationException("Mount did not respond")) : Task.CompletedTask;
        }
    }
}

public class SetupRoleTests {
    private string root = null!;

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "nm-rigs-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void MainRig_IsItsOwnSetup_NamedAfterProfileUnlessRenamed() {
        var registry = new NightManager.Core.Settings.RigRegistry(root);

        var plain = NightManager.Core.Settings.SetupIdentity.Resolve("A", "Pier 1", new NightManager.Core.Settings.RigSettings(), registry);
        var named = NightManager.Core.Settings.SetupIdentity.Resolve("A", "Pier 1", new NightManager.Core.Settings.RigSettings { SetupName = "Twin RedCat" }, registry);

        Assert.That((plain.SetupId, plain.SetupName, plain.ControlsMount), Is.EqualTo(("A", "Pier 1", true)));
        Assert.That(named.SetupName, Is.EqualTo("Twin RedCat"));
    }

    [Test]
    public void LinkedRig_JoinsMainRigsSetup_AndRegistryOffersOnlyOtherMains() {
        var registry = new NightManager.Core.Settings.RigRegistry(root);
        registry.Publish(new NightManager.Core.Settings.RigRecord { RigId = "A", RigName = "RedCat A", SetupName = "Twin RedCat" });
        registry.Publish(new NightManager.Core.Settings.RigRecord { RigId = "B", RigName = "RedCat B", Role = NightManager.Core.Settings.RigRole.Linked, MainRigId = "A" });
        registry.Publish(new NightManager.Core.Settings.RigRecord { RigId = "C", RigName = "Rokinon" });

        var linked = NightManager.Core.Settings.SetupIdentity.Resolve("B", "RedCat B",
            new NightManager.Core.Settings.RigSettings { Role = NightManager.Core.Settings.RigRole.Linked, MainRigId = "A" }, registry);

        Assert.That((linked.SetupId, linked.SetupName, linked.ControlsMount, linked.Problem), Is.EqualTo(("A", "Twin RedCat", false, (string?)null)));
        Assert.That(registry.MainRigs("B").Select(r => r.RigId), Is.EquivalentTo(new[] { "A", "C" }));
        Assert.That(registry.MainRigs("A").Select(r => r.RigId), Is.EqualTo(new[] { "C" }));
    }

    [Test]
    public void LinkedRig_WithoutMain_IsShownAloneWithAProblem() {
        var identity = NightManager.Core.Settings.SetupIdentity.Resolve("B", "RedCat B",
            new NightManager.Core.Settings.RigSettings { Role = NightManager.Core.Settings.RigRole.Linked }, new NightManager.Core.Settings.RigRegistry(root));

        Assert.That(identity.SetupId, Is.EqualTo("B"));
        Assert.That(identity.Problem, Does.Contain("no main rig"));
    }

    [Test]
    public async Task LinkedRig_RefusesMountActions_AndSafeShutdownSkipsThem() {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.Zero));
        var executor = new RecordingExecutor();
        var allowed = new HashSet<string> { CommandTypes.ParkMount, CommandTypes.SafeShutdown };
        var processor = new CommandProcessor(executor, () => allowed, null, time, controlsMount: () => false);
        RemoteCommand Cmd(string id, string type) => new() { Id = id, Type = type, IssuedAt = time.GetUtcNow(), ExpiresAt = time.GetUtcNow().AddMinutes(5) };

        var park = (await processor.ProcessAsync([Cmd("1", CommandTypes.ParkMount)], CancellationToken.None)).Single();
        var shutdown = (await processor.ProcessAsync([Cmd("2", CommandTypes.SafeShutdown)], CancellationToken.None)).Single();

        Assert.That(park.Outcome, Is.EqualTo(CommandOutcomes.Rejected));
        Assert.That(park.Message, Does.Contain("main rig"));
        Assert.That(shutdown.Outcome, Is.EqualTo(CommandOutcomes.Executed));
        Assert.That(executor.Calls, Is.EqualTo(new[] { "abort", "stopGuiding", "warm" }));
    }

    private sealed class RecordingExecutor : ICommandExecutor {
        public List<string> Calls { get; } = [];

        public Task AbortSequenceAsync(CancellationToken token) => Add("abort");

        public Task StopGuidingAsync(CancellationToken token) => Add("stopGuiding");

        public Task ParkMountAsync(CancellationToken token) => Add("park");

        public Task CloseDomeAsync(CancellationToken token) => Add("closeDome");

        public Task WarmCameraAsync(CancellationToken token) => Add("warm");

        public bool IsAvailable(string commandType) => true;

        private Task Add(string name) {
            Calls.Add(name);
            return Task.CompletedTask;
        }
    }
}
