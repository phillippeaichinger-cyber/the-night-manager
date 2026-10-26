using NightManager.Core.Sessions;

namespace NightManager.Core.Tests;

public class NightTimeTests {
    private static NightTime.Moment Running(string activity, params (string, string)[] lines) =>
        new() { SequenceRunning = true, Activity = activity, Lines = lines };

    [Test]
    public void Puts_Each_Moment_In_The_Right_Bucket() {
        Assert.Multiple(() => {
            Assert.That(NightTime.Classify(Running("imaging")), Is.EqualTo(NightTime.Imaging));
            Assert.That(NightTime.Classify(Running("busy", ("Guider", "Settling"))), Is.EqualTo(NightTime.Guiding));
            Assert.That(NightTime.Classify(Running("busy", ("Plate Solving", "Solving"))), Is.EqualTo(NightTime.Slewing));
            Assert.That(NightTime.Classify(Running("waiting") with { Waiting = "Waiting until safe" }), Is.EqualTo(NightTime.Unsafe));
            Assert.That(NightTime.Classify(Running("waiting") with { Waiting = "Until 20:01" }), Is.EqualTo(NightTime.Waiting));
            Assert.That(NightTime.Classify(Running("imaging") with { Flipping = true }), Is.EqualTo(NightTime.Flip));
            Assert.That(NightTime.Classify(new NightTime.Moment { AutofocusRunning = true }), Is.EqualTo(NightTime.Autofocus));
            Assert.That(NightTime.Classify(new NightTime.Moment { Activity = "idle" }), Is.EqualTo(NightTime.Idle));
            Assert.That(NightTime.Classify(Running("busy", ("Camera", "Downloading"))), Is.EqualTo(NightTime.Overhead));
        });
    }

    [Test]
    public void Session_Updates_Carry_The_Time_And_Counts() {
        var path = Path.Combine(Path.GetTempPath(), "nm-session-" + Guid.NewGuid().ToString("N") + ".json");
        var tracker = new SessionTracker(path);
        var start = new DateTimeOffset(2026, 10, 15, 21, 0, 0, TimeSpan.Zero);
        tracker.OnFrame(new SessionFrame { FrameId = "f1", FileName = "a.fits", Score = 90, Timestamp = start, ImageType = "LIGHT", ExposureSeconds = 300, Target = "M31", Filter = "L", Verdict = "accept" });
        tracker.AddTime(NightTime.Imaging, 300);
        tracker.AddTime(NightTime.Autofocus, 120);
        tracker.AddTime(NightTime.Imaging, 300);
        tracker.Count(NightTime.AutofocusRuns);
        var items = tracker.Tick(start.AddMinutes(20), null);
        var totals = items.OfType<NightManager.Core.Contracts.SessionItem>().Last().Totals;
        Assert.Multiple(() => {
            Assert.That(totals.Time![NightTime.Imaging], Is.EqualTo(600));
            Assert.That(totals.Time[NightTime.Autofocus], Is.EqualTo(120));
            Assert.That(totals.Counts![NightTime.AutofocusRuns], Is.EqualTo(1));
        });
        File.Delete(path);
    }
}
