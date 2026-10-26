using NightManager.Core.Json;
using NightManager.Core.Security;
using NightManager.Core.Startup;
using NightManager.Core.Sync;

namespace NightManager.Core.Tests;

public class StartupInventoryTests {
    private string root = null!;

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "nm-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    // The shape of a real NINA profile: device ids and names nested deeper than the profile's own.
    private const string ProfileXml = """
        <Profile xmlns="http://schemas.datacontract.org/2004/07/NINA.Profile" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <CameraSettings><Id>ZWOptical_ZWO ASI1600MM Pro</Id></CameraSettings>
          <FilterWheelSettings><FilterWheelFilters><a:FilterInfo xmlns:a="x"><a:Name>Ha</a:Name></a:FilterInfo></FilterWheelFilters></FilterWheelSettings>
          <Id>b9561562-1f9f-46b6-a176-2e30db60b073</Id>
          <Name>Ridgeview Pier 1</Name>
          <SequenceSettings>
            <DefaultSequenceFolder>C:\Users\Observer\Documents\N.I.N.A</DefaultSequenceFolder>
            <SequencerTargetsFolder>C:\Users\Observer\Documents\N.I.N.A\Targets</SequencerTargetsFolder>
            <SequencerTemplatesFolder>C:\Users\Observer\Documents\N.I.N.A\Templates</SequencerTemplatesFolder>
          </SequenceSettings>
        </Profile>
        """;

    [Test]
    public void ReadsTheProfilesOwnIdNameAndSequenceFolders() {
        var profile = StartupInventoryScanner.ReadProfile(ProfileXml)!;
        Assert.That(profile.Id, Is.EqualTo("b9561562-1f9f-46b6-a176-2e30db60b073"));
        Assert.That(profile.Name, Is.EqualTo("Ridgeview Pier 1"));
        Assert.That(profile.SequenceFolder, Is.EqualTo(@"C:\Users\Observer\Documents\N.I.N.A"));
        Assert.That(profile.TemplatesFolder, Is.EqualTo(@"C:\Users\Observer\Documents\N.I.N.A\Templates"));
    }

    [Test]
    public void SkipsFilesThatArentProfiles() {
        Assert.That(StartupInventoryScanner.ReadProfile("<Other><Id>b9561562-1f9f-46b6-a176-2e30db60b073</Id></Other>"), Is.Null);
        Assert.That(StartupInventoryScanner.ReadProfile("<Profile><Id>not-a-guid</Id></Profile>"), Is.Null);
    }

    [Test]
    public void ListsTheProfileNinaHasOpen() {
        // NINA holds the profile it's using open for writing (only reads shared): it must still be listed.
        var path = Path.Combine(root, "open.profile");
        File.WriteAllText(path, ProfileXml);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        Assert.That(StartupInventoryScanner.Profiles(root).Select(p => p.Name), Is.EqualTo(new[] { "Ridgeview Pier 1" }));
    }

    [Test]
    public void ListsProfilesFromTheFolderSkippingDamagedOnes() {
        File.WriteAllText(Path.Combine(root, "a.profile"), ProfileXml);
        File.WriteAllText(Path.Combine(root, "b.profile"), "<Profile><Id>");
        Assert.That(StartupInventoryScanner.Profiles(root).Select(p => p.Name), Is.EqualTo(new[] { "Ridgeview Pier 1" }));
    }

    [Test]
    public void OnlyWholeSequencesAreOffered() {
        var templates = Directory.CreateDirectory(Path.Combine(root, "Templates")).FullName;
        File.WriteAllText(Path.Combine(templates, "TS_Loop_West.json"), """{"$id":"1","$type":"NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer","Name":"x"}""");
        File.WriteAllText(Path.Combine(templates, "Flat Capture.template.json"), """{"$id":"1","$type":"NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer"}""");
        File.WriteAllText(Path.Combine(root, "notes.json"), """{"hello":1}""");
        var found = StartupInventoryScanner.Sequences([root, templates]);
        Assert.That(found.Select(s => s.Name), Is.EqualTo(new[] { "TS_Loop_West" }), "listed once although both folders contain it");
    }
}

public class StartupInventoryBuilderTests {
    [Test]
    public void GathersProfilesSequencesNinaAndWhichRigsAllowStartingASequence() {
        var root = Path.Combine(Path.GetTempPath(), "nm-inv-" + Guid.NewGuid().ToString("N"));
        try {
            var local = Path.Combine(root, "local");
            var profiles = Directory.CreateDirectory(Path.Combine(local, "NINA", "Profiles")).FullName;
            var seq = Directory.CreateDirectory(Path.Combine(root, "seq")).FullName;
            const string a = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", b = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
            foreach (var (id, name) in new[] { (a, "LEFT"), (b, "RIGHT") }) {
                File.WriteAllText(Path.Combine(profiles, id + ".profile"),
                    $"<Profile xmlns=\"x\"><Id>{id}</Id><Name>{name}</Name><SequenceSettings><DefaultSequenceFolder>{seq}</DefaultSequenceFolder></SequenceSettings></Profile>");
            }
            File.WriteAllText(Path.Combine(seq, "Loop.json"), "{\"$type\":\"NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer\"}");
            var nm = Path.Combine(root, "nm");
            Directory.CreateDirectory(Path.Combine(nm, "rigs", a));
            File.WriteAllText(Path.Combine(nm, "rigs", a, "settings.json"), "{\"allowStartFromApp\": true}");
            Directory.CreateDirectory(Path.Combine(nm, "rigs", b));
            File.WriteAllText(Path.Combine(nm, "rigs", b, "session.json"), "{}");
            var nina = Directory.CreateDirectory(Path.Combine(root, "pf", StartupDefaults.NinaFolderName)).FullName;
            File.WriteAllText(Path.Combine(nina, "NINA.exe"), "");

            var inventory = StartupInventoryBuilder.Build(new LauncherSettings { Enabled = true }, nm, local, Path.Combine(root, "pf"));
            Assert.That(inventory.Profiles.Select(p => p.Name), Is.EqualTo(new[] { "LEFT", "RIGHT" }));
            Assert.That(inventory.Sequences.Select(s => s.Name), Is.EqualTo(new[] { "Loop" }));
            Assert.That(inventory.NinaFound, Is.True);
            Assert.That(inventory.SequenceStartAllowed, Is.EqualTo(new[] { a }));
            Assert.That(File.Exists(Path.Combine(nm, "rigs", b, "settings.json")), Is.False, "reading never creates a rig's settings");
            Assert.That(StartupInventoryBuilder.OpenSessions(nm, inventory.Profiles), Is.EquivalentTo(new[] { b }));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class LaunchPlannerTests {
    private static readonly ProfileInfo Right = new() { Id = "11111111-1111-1111-1111-111111111111", Name = "FF130 RIGHT" };
    private static readonly ProfileInfo Left = new() { Id = "22222222-2222-2222-2222-222222222222", Name = "FF130 LEFT" };
    private const string Sequence = @"C:\Seq\TS_Loop_West.json";

    private static LaunchPlanner.Context Context(StartupPlan plan, ISet<string>? allowed = null, ISet<string>? running = null, ISet<string>? resume = null) => new() {
        Plan = plan, Profiles = [Left, Right],
        SequenceStartAllowed = (IReadOnlySet<string>)(allowed ?? new HashSet<string> { Left.Id, Right.Id }),
        Running = (IReadOnlySet<string>)(running ?? new HashSet<string>()),
        Resume = (IReadOnlySet<string>)(resume ?? new HashSet<string>()),
        FileExists = path => path == Sequence,
    };

    private static StartupPlan Twin(bool runLeft = true) => new() {
        Enabled = true, MasterProfileId = Right.Id,
        Profiles = [
            new PlanProfile { ProfileId = Left.Id, SequencePath = Sequence, RunSequence = runLeft },
            new PlanProfile { ProfileId = Right.Id },
        ],
    };

    [Test]
    public void TheMasterStartsFirstAndTheOthersADelayAfter() {
        var steps = LaunchPlanner.Plan(Context(Twin()));
        Assert.That(steps.Select(s => s.ProfileName), Is.EqualTo(new[] { "FF130 RIGHT", "FF130 LEFT" }));
        Assert.That(steps[0].DelayBefore, Is.EqualTo(TimeSpan.Zero));
        Assert.That(steps[1].DelayBefore, Is.EqualTo(TimeSpan.FromSeconds(60)));
    }

    [Test]
    public void NothingStartsWhenANinaOpenedByHandIsRunning() {
        var steps = LaunchPlanner.Plan(Context(Twin(), running: new HashSet<string> { LaunchPlanner.UnknownProfile }));
        Assert.That(steps, Has.Count.EqualTo(2));
        Assert.That(steps.All(s => s.Skipped is not null), Is.True);
    }

    [Test]
    public void ArgumentsLoadTheProfileTheSequenceAndStartIt() {
        var left = LaunchPlanner.Plan(Context(Twin())).Single(s => s.ProfileId == Left.Id);
        Assert.That(left.Arguments, Is.EqualTo(new[] { "--profileid", Left.Id, "--sequencefile", Sequence, "--runsequence" }));
        var right = LaunchPlanner.Plan(Context(Twin())).Single(s => s.ProfileId == Right.Id);
        Assert.That(right.Arguments, Is.EqualTo(new[] { "--profileid", Right.Id }));
    }

    [Test]
    public void ASequenceOnlyStartsWhereThePcAllowsIt() {
        var left = LaunchPlanner.Plan(Context(Twin(), allowed: new HashSet<string>())).Single(s => s.ProfileId == Left.Id);
        Assert.That(left.Arguments, Does.Not.Contain("--runsequence"));
        Assert.That(left.Arguments, Does.Contain("--sequencefile"), "still loaded, ready to start by hand");
        Assert.That(left.Note, Does.Contain("Allow starting the sequence"));
    }

    [Test]
    public void AMidNightRestartRunsTheSequenceEvenIfThePlanOnlyLoadsIt() {
        var left = LaunchPlanner.Plan(Context(Twin(runLeft: false), resume: new HashSet<string> { Left.Id })).Single(s => s.ProfileId == Left.Id);
        Assert.That(left.RunSequence, Is.True);
        Assert.That(left.Resuming, Is.True);
    }

    [Test]
    public void AMissingSequenceStartsNinaWithoutIt() {
        var plan = Twin() with { Profiles = [new PlanProfile { ProfileId = Left.Id, SequencePath = @"C:\gone.json", RunSequence = true }] };
        var step = LaunchPlanner.Plan(Context(plan)).Single();
        Assert.That(step.Arguments, Is.EqualTo(new[] { "--profileid", Left.Id }));
        Assert.That(step.Note, Does.Contain("gone.json"));
    }

    [Test]
    public void RunningOrMissingProfilesAreSkippedAndDontDelayTheNext() {
        var plan = new StartupPlan {
            Enabled = true, MasterProfileId = Right.Id,
            Profiles = [new PlanProfile { ProfileId = Right.Id }, new PlanProfile { ProfileId = "33333333-3333-3333-3333-333333333333" }, new PlanProfile { ProfileId = Left.Id }],
        };
        var steps = LaunchPlanner.Plan(Context(plan, running: new HashSet<string> { Right.Id }));
        Assert.That(steps[0].Skipped, Does.Contain("already running"));
        Assert.That(steps[1].Skipped, Does.Contain("isn't on this PC"));
        Assert.That(steps[2].DelayBefore, Is.EqualTo(TimeSpan.Zero), "nothing was started before it");
    }

    [Test]
    public void ProfilesSwitchedOffOrAPlanSwitchedOffStartNothing() {
        Assert.That(LaunchPlanner.Plan(Context(Twin() with { Enabled = false })), Is.Empty);
        var plan = Twin() with { Profiles = [new PlanProfile { ProfileId = Left.Id, Start = false }] };
        Assert.That(LaunchPlanner.Plan(Context(plan)), Is.Empty);
    }

    [Test]
    public void TheDelayIsClamped() {
        Assert.That(LaunchPlanner.Plan(Context(Twin() with { DelaySeconds = 1 }))[1].DelayBefore, Is.EqualTo(TimeSpan.FromSeconds(StartupDefaults.MinDelaySeconds)));
    }

    [Test]
    public void ResumeOnlyBeforeDawn() {
        var now = new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);
        var rigs = new[] {
            new RigResumeInfo { ProfileId = Left.Id, LostWhileImaging = true, NightEndsAt = now.AddHours(3) },
            new RigResumeInfo { ProfileId = Right.Id, LostWhileImaging = true, NightEndsAt = now.AddHours(-1) },
            new RigResumeInfo { ProfileId = "x", LostWhileImaging = false, NightEndsAt = now.AddHours(3) },
            new RigResumeInfo { ProfileId = "y", LostWhileImaging = true },
        };
        Assert.That(LaunchPlanner.ResumeSet(rigs, now), Is.EquivalentTo(new[] { Left.Id }));
    }

    [Test]
    public void CommandLinesQuotePathsWithSpaces() {
        Assert.That(LaunchPlanner.CommandLine(["--profileid", "abc", "--sequencefile", @"C:\My Seq\Loop.json"]),
            Is.EqualTo(@"--profileid abc --sequencefile ""C:\My Seq\Loop.json"""));
        Assert.That(LaunchPlanner.CommandLine([@"C:\Folder With Space\"]), Is.EqualTo(@"""C:\Folder With Space\\"""));
    }
}

public class LauncherRunnerTests {
    private static readonly ProfileInfo Right = new() { Id = "11111111-1111-1111-1111-111111111111", Name = "FF130 RIGHT" };
    private static readonly ProfileInfo Left = new() { Id = "22222222-2222-2222-2222-222222222222", Name = "FF130 LEFT" };
    private string root = null!;
    private ApiCredential credential = null!;

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "nm-launcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ApiCredential.TryParse("nm2_key1_0123456789abcdef0123456789abcdefXYZ", out var key);
        credential = key!;
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private sealed class Host : ILauncherHost {
        public readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<IReadOnlyList<string>> Started = [];
        public readonly List<TimeSpan> Waits = [];
        public bool Fails;
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 2, 0, 0, TimeSpan.Zero);
        public IReadOnlySet<string> RunningProfiles() => Running;
        public bool Start(string ninaPath, IReadOnlyList<string> arguments) {
            Started.Add(arguments);
            if (!Fails) Running.Add(arguments[1]);
            return true;
        }
        public Task Delay(TimeSpan delay, CancellationToken token) { Waits.Add(delay); Now += delay; return Task.CompletedTask; }
    }

    private sealed class Server : IApiTransport {
        public LauncherPlanResponse? Plan;
        public readonly List<LauncherStatus> Statuses = [];
        public int PlanCalls;
        public Task<ApiResult> PostJsonAsync(string path, string json, ApiCredential? credential, CancellationToken token) {
            if (path == LauncherRunner.PlanPath) {
                PlanCalls++;
                return Task.FromResult(Plan is null ? new ApiResult(0, null, Error: "offline") : new ApiResult(200, NmJson.Serialize(Plan)));
            }
            Statuses.Add(NmJson.Deserialize<LauncherStatus>(json)!);
            return Task.FromResult(new ApiResult(204, null));
        }
        public Task<ApiResult> UploadBlobAsync(Contracts.BlobUpload upload, string filePath, string contentType, CancellationToken token) => throw new NotSupportedException();
    }

    private LauncherRunner.Inputs Inputs(bool enabled = true, ISet<string>? openSessions = null) => new() {
        Settings = new LauncherSettings { Enabled = true && enabled },
        Inventory = new StartupInventory {
            Profiles = [Left, Right], NinaFound = true, NinaPath = @"C:\NINA\NINA.exe",
            SequenceStartAllowed = [Left.Id, Right.Id],
        },
        OpenSessions = (IReadOnlySet<string>)(openSessions ?? new HashSet<string>()),
        NetworkWait = TimeSpan.FromMinutes(1),
    };

    private static LauncherPlanResponse TwinPlan(bool lostWhileImaging = false, DateTimeOffset? dawn = null) => new() {
        Plan = new StartupPlan {
            Enabled = true, MasterProfileId = Right.Id,
            Profiles = [new PlanProfile { ProfileId = Left.Id }, new PlanProfile { ProfileId = Right.Id }],
        },
        Rigs = [new RigResumeInfo { ProfileId = Left.Id, LostWhileImaging = lostWhileImaging, NightEndsAt = dawn }],
    };

    [Test]
    public async Task ABootStartsTheMasterThenTheOtherAMinuteLaterAndReportsEachStep() {
        var host = new Host();
        var server = new Server { Plan = TwinPlan() };
        var result = await new LauncherRunner(host, server, () => credential, Path.Combine(root, "plan.json")).RunAsync(Inputs(), CancellationToken.None);

        Assert.That(host.Started.Select(a => a[1]), Is.EqualTo(new[] { Right.Id, Left.Id }));
        Assert.That(host.Waits, Does.Contain(TimeSpan.FromSeconds(60)));
        Assert.That(result.Status.Phase, Is.EqualTo("started"));
        Assert.That(server.Statuses.First().Phase, Is.EqualTo("launching"));
        Assert.That(server.Statuses.Last().Instances.Select(i => i.State), Is.All.EqualTo("started"));
    }

    [Test]
    public async Task ANinaThatDoesntComeUpIsReportedAsFailed() {
        var host = new Host { Fails = true };
        var result = await new LauncherRunner(host, new Server { Plan = TwinPlan() }, () => credential, Path.Combine(root, "plan.json")).RunAsync(Inputs(), CancellationToken.None);
        Assert.That(result.Status.Phase, Is.EqualTo("failed"));
    }

    [Test]
    public async Task WithoutNetworkTheLastPlanIsUsedAndAnOpenSessionResumesBeforeDawn() {
        var cache = Path.Combine(root, "plan.json");
        var host = new Host();
        var plan = TwinPlan(dawn: host.Now.AddHours(4)) with {
            Plan = TwinPlan().Plan with { Profiles = [new PlanProfile { ProfileId = Left.Id, SequencePath = Path.Combine(root, "loop.json") }] },
        };
        File.WriteAllText(Path.Combine(root, "loop.json"), "{}");
        await new LauncherRunner(host, new Server { Plan = plan }, () => credential, cache).RunAsync(Inputs(), CancellationToken.None);

        // Next boot: no network, the plugin's session for LEFT was still open.
        host.Running.Clear();
        host.Started.Clear();
        var offline = new Server();
        var result = await new LauncherRunner(host, offline, () => credential, cache).RunAsync(Inputs(openSessions: new HashSet<string> { Left.Id }), CancellationToken.None);
        Assert.That(result.UsedCachedPlan, Is.True);
        Assert.That(offline.PlanCalls, Is.GreaterThan(1), "kept trying while the network came up");
        Assert.That(host.Started.Single(), Does.Contain("--runsequence"));
        Assert.That(result.Status.Reason, Is.EqualTo("resume"));
    }

    [Test]
    public async Task OffInTheAppOrOnThePcStartsNothing() {
        var host = new Host();
        var off = new Server { Plan = TwinPlan() with { Plan = TwinPlan().Plan with { Enabled = false } } };
        var result = await new LauncherRunner(host, off, () => credential, Path.Combine(root, "plan.json")).RunAsync(Inputs(), CancellationToken.None);
        Assert.That(result.Status.Phase, Is.EqualTo("disabled"));
        Assert.That(host.Started, Is.Empty);

        var result2 = await new LauncherRunner(host, new Server { Plan = TwinPlan() }, () => credential, Path.Combine(root, "plan2.json")).RunAsync(Inputs(enabled: false), CancellationToken.None);
        Assert.That(result2.Status.Phase, Is.EqualTo("disabled"));
        Assert.That(host.Started, Is.Empty);
    }
}
