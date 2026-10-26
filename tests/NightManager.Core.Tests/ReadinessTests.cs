using NightManager.Core.Contracts;
using NightManager.Core.Readiness;

namespace NightManager.Core.Tests;

public class ReadinessTests {
    private static SequenceStep Step(string type, params (string Key, string Value)[] settings) =>
        new(type, null, settings.ToDictionary(s => s.Key, s => s.Value));

    // A remote rig's POWER ON container (TS_Loop_East_Guided), plus a plugin's script and a smart plug.
    private static readonly SequenceStep[] RemotePowerOn = [
        Step("NINA.Sequencer.SequenceItem.Utility.WaitForTime, NINA.Sequencer"),
        Step("NINA.Sequencer.SequenceItem.Connect.ConnectEquipment, NINA.Sequencer", ("SelectedDevice", "Safety Monitor")),
        Step("NINA.Sequencer.SequenceItem.Connect.ConnectEquipment, NINA.Sequencer", ("SelectedDevice", "Switch")),
        .. Enumerable.Range(0, 6).Select(_ => Step("NINA.Sequencer.SequenceItem.Switch.SetSwitchValue, NINA.Sequencer")),
        Step("WhenPlugin.When.ExternalScript, WhenPlugin", ("Script", @"C:\Astro\power-on.ps1")),
        Step("DaleGhent.NINA.GroundStation.HTTP.HttpClient, DaleGhent.NINA.GroundStation"),
        Step("NINA.Sequencer.SequenceItem.Connect.ConnectAllEquipment, NINA.Sequencer"),
        Step("NINA.Sequencer.SequenceItem.Connect.DisconnectAllEquipment, NINA.Sequencer"),
        Step("NINA.Sequencer.SequenceItem.Connect.ReconnectOnDownloadFailure, NINA.Sequencer"),
    ];

    [Test]
    public void Reads_Power_Up_Steps_From_Any_Plugin() {
        var plan = PowerPlan.From(RemotePowerOn);
        Assert.Multiple(() => {
            Assert.That(plan.PowersUp, Is.True);
            Assert.That(plan.ConnectsAll, Is.True);
            Assert.That(plan.SwitchSteps, Is.EqualTo(6));
            Assert.That(plan.RequestSteps, Is.EqualTo(1));
            Assert.That(plan.Scripts, Is.EqualTo(new[] { @"C:\Astro\power-on.ps1" }));
            Assert.That(plan.ConnectedDevices, Is.EquivalentTo(new[] { "Safety Monitor", "Switch" }));
            Assert.That(plan.Describe(), Is.EqualTo("The sequence sets 6 switches, runs 1 script, sends 1 web request and connects all equipment."));
        });
    }

    [Test]
    public void Equipment_Off_Before_Dark_Is_Expected_When_The_Sequence_Powers_It_Up() {
        var report = ReadinessCheck.Evaluate(new ReadinessInput {
            Phase = ReadinessPhases.BeforeDark, Now = DateTimeOffset.UtcNow,
            Expected = [("camera", 5), ("mount", 5), ("focuser", 5)],
            SequenceLoaded = true, SequenceRunning = true, Waiting = "Until 20:01", SequenceTitle = "TS_Loop_East_Guided",
            Power = PowerPlan.From(RemotePowerOn),
            FreeBytes = 500L << 30, NeededBytes = 14L << 30,
            SchedulerInstalled = true, SchedulerTargetsTonight = 2,
        });
        Assert.Multiple(() => {
            Assert.That(report.Status, Is.EqualTo(ReadinessStatus.Ok));
            Assert.That(report.Headline, Is.EqualTo("Ready for tonight"));
            Assert.That(report.Items.Single(i => i.Key == "equipment").Status, Is.EqualTo(ReadinessStatus.Later));
            Assert.That(report.Items.Single(i => i.Key == "sequence").Title, Is.EqualTo("Sequence running, waiting until 20:01"));
        });
    }

    [Test]
    public void After_Power_Up_A_Device_That_Usually_Comes_Up_Is_A_Problem() {
        var report = ReadinessCheck.Evaluate(new ReadinessInput {
            Phase = ReadinessPhases.AfterPowerUp, Now = DateTimeOffset.UtcNow,
            Connected = new HashSet<string> { "camera", "mount" },
            Expected = [("camera", 5), ("focuser", 4), ("mount", 5)],
            SequenceLoaded = true, SequenceRunning = true, Power = PowerPlan.From(RemotePowerOn),
        });
        Assert.Multiple(() => {
            Assert.That(report.Status, Is.EqualTo(ReadinessStatus.Problem));
            Assert.That(report.Headline, Is.EqualTo("Focuser didn't come up"));
            Assert.That(report.Items[0].Detail, Is.EqualTo("It was connected on the last 4 nights."));
        });
    }

    [Test]
    public void Nothing_Connected_And_Nothing_To_Connect_It_Is_A_Problem_And_Missing_Scripts_Are_Named() {
        var report = ReadinessCheck.Evaluate(new ReadinessInput {
            Phase = ReadinessPhases.LateStart, Now = DateTimeOffset.UtcNow,
            SequenceLoaded = false, MissingScripts = [@"C:\Astro\roof.ps1"], FreeBytes = 1L << 30,
            SafetyConnected = true, IsSafe = false,
        });
        Assert.Multiple(() => {
            Assert.That(report.Headline, Is.EqualTo("Started late: 3 problems before tonight"));
            Assert.That(report.Items.Select(i => i.Key), Is.SupersetOf(new[] { "equipment", "scripts", "storage", "sequence", "safety" }));
        });
    }

    [Test]
    public void Baseline_Expects_What_Came_Up_On_Most_Recent_Nights() {
        var baseline = new PowerBaseline();
        baseline.Record("2026-10-10", ["camera", "mount", "focuser", "guider"]);
        baseline.Record("2026-10-11", ["camera", "mount", "focuser"]);
        baseline.Record("2026-10-12", ["camera", "mount", "focuser", "rotator"]);
        baseline.Record("2026-10-12", ["camera", "mount", "focuser"]);
        Assert.That(baseline.Expected().Select(e => e.Device), Is.EqualTo(new[] { "camera", "focuser", "mount" }));

        var path = Path.Combine(Path.GetTempPath(), "nm-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        baseline.Save(path);
        Assert.That(PowerBaseline.Load(path).Nights, Has.Count.EqualTo(3));
        File.Delete(path);
    }

    [Test]
    public void Watcher_Settles_Once_A_Night_After_The_Connection_Wave() {
        var watcher = new PowerUpWatcher();
        var t = new DateTimeOffset(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
        HashSet<string> Set(params string[] s) => [.. s];
        Assert.That(watcher.Tick(t, "n1", true, Set(), false), Is.Null);
        Assert.That(watcher.Tick(t.AddMinutes(1), "n1", true, Set("safety"), false), Is.Null);
        Assert.That(watcher.Tick(t.AddMinutes(2), "n1", true, Set("safety", "camera", "mount"), false), Is.Null);
        Assert.That(watcher.Tick(t.AddMinutes(6), "n1", true, Set("safety", "camera", "mount"), false), Is.Null);
        var settled = watcher.Tick(t.AddMinutes(7), "n1", true, Set("safety", "camera", "mount"), false);
        Assert.That(settled, Is.EquivalentTo(new[] { "safety", "camera", "mount" }));
        Assert.That(watcher.Tick(t.AddMinutes(20), "n1", true, Set("safety", "camera", "mount"), false), Is.Null);
        // A rig that's been connected all afternoon settles as soon as the next night's window opens.
        Assert.That(watcher.Tick(t.AddHours(20), "n2", false, Set("camera"), false), Is.Null);
        Assert.That(watcher.Tick(t.AddHours(21), "n2", true, Set("camera"), false), Is.EquivalentTo(new[] { "camera" }));
    }
}
