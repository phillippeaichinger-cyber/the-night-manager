using NightManager.Core.Contracts;
using NightManager.Core.Readiness;

namespace NightManager.Core.Tests;

public class SequenceCheckTests {
    private static SequenceStep S(string type, string kind = SequenceStep.Item, string? name = null, params string[] issues) =>
        new(type, name, new Dictionary<string, string>(), kind, issues.Length > 0 ? issues : null);

    // A remote rig's TS_Loop_East_Guided, as far as the check cares.
    private static readonly SequenceStep[] RemoteRig = [
        S("NINA.Sequencer.Container.SequenceRootContainer", SequenceStep.Container),
        S("NINA.Sequencer.SequenceItem.Connect.ConnectAllEquipment", name: "Connect All Equipment"),
        S("NINA.Sequencer.SequenceItem.Camera.CoolCamera", name: "Cool Camera", issues: "Camera is not connected"),
        S("NINA.Sequencer.Conditions.SafetyMonitorCondition", SequenceStep.Condition),
        S("NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger", SequenceStep.Trigger),
        S("NINA.Sequencer.Trigger.Autofocus.AutofocusAfterHFRIncreaseTrigger", SequenceStep.Trigger),
        S("NINA.Plugin.TargetScheduler.Sequencer.TargetSchedulerContainer", SequenceStep.Container),
        S("NINA.Sequencer.SequenceItem.Telescope.ParkScope", name: "Park Scope"),
    ];

    private static readonly SequenceContext Rig = new() { HasGuider = true, HasSafetyMonitor = true, HasCooledCamera = true, PowersUp = true };

    [Test]
    public void Remote_Sequence_Only_Lacks_Dithering_And_Its_Not_Connected_Warnings_Are_Expected() {
        var findings = SequenceCheck.Evaluate(RemoteRig, Rig);
        Assert.That(findings.Select(f => f.Key), Is.EqualTo(new[] { "sequence.dither" }));
        Assert.That(findings[0].Status, Is.EqualTo(ReadinessStatus.Tip));
    }

    [Test]
    public void A_Bare_Sequence_Gets_Each_Tip_That_Applies_To_The_Rig() {
        var bare = new[] { S("NINA.Sequencer.Container.SequenceRootContainer", SequenceStep.Container), S("NINA.Sequencer.SequenceItem.Imaging.SmartExposure", name: "Smart Exposure") };
        var keys = SequenceCheck.Evaluate(bare, new SequenceContext { HasGuider = false, HasSafetyMonitor = false, HasCooledCamera = true }).Select(f => f.Key).ToList();
        Assert.That(keys, Is.EquivalentTo(new[] { "sequence.flip", "sequence.autofocus", "sequence.cooling", "sequence.end" }));
    }

    [Test]
    public void Unknown_Instructions_Soften_The_Wording_Instead_Of_Claiming_Something_Is_Missing() {
        var steps = new[] { S("Acme.NINA.Robotics.FlipAndPray", SequenceStep.Trigger, "Flip and Pray"), S("NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus", name: "Run Autofocus"),
            S("NINA.Sequencer.SequenceItem.Telescope.ParkScope") };
        var flip = SequenceCheck.Evaluate(steps, new SequenceContext()).Single(f => f.Key == "sequence.flip");
        Assert.That(flip.Detail, Does.Contain("Flip and Pray, which The Night Manager doesn't know"));
    }

    [Test]
    public void Ninas_Own_Warnings_Are_Passed_On() {
        var steps = new[] { S("NINA.Sequencer.SequenceItem.Autofocus.RunAutofocus", name: "Run Autofocus", issues: "No focuser selected"),
            S("NINA.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger", SequenceStep.Trigger), S("NINA.Sequencer.SequenceItem.Telescope.ParkScope") };
        var issue = SequenceCheck.Evaluate(steps, new SequenceContext()).Single(f => f.Key == "sequence.issues");
        Assert.Multiple(() => {
            Assert.That(issue.Status, Is.EqualTo(ReadinessStatus.Warning));
            Assert.That(issue.Detail, Is.EqualTo("Run Autofocus: No focuser selected"));
        });
    }

    [Test]
    public void Tips_Dont_Count_Against_Being_Ready() {
        var report = ReadinessCheck.Evaluate(new ReadinessInput {
            Phase = ReadinessPhases.BeforeDark, Now = DateTimeOffset.UtcNow, Connected = new HashSet<string> { "camera" }, SequenceLoaded = true, SequenceRunning = true,
            SequenceFindings = SequenceCheck.Evaluate(RemoteRig, Rig),
        });
        Assert.That(report.Headline, Is.EqualTo("Ready for tonight"));
        Assert.That(report.Items.Last().Status, Is.EqualTo(ReadinessStatus.Tip));
    }
}
