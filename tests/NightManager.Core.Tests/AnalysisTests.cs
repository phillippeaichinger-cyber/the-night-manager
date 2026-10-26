using NightManager.Core.Analysis;
using NightManager.Core.Contracts;

namespace NightManager.Core.Tests;

public class FrameGraderTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 22, 0, 0, TimeSpan.Zero);

    internal static FrameMetrics Light(double hfr, int stars, int minute = 0, double? rms = 0.6, double median = 1000, string filter = "L") => new() {
        Timestamp = T0.AddMinutes(minute),
        ImageType = "LIGHT",
        Target = "M42",
        Filter = filter,
        ExposureSeconds = 300,
        Binning = "1x1",
        Hfr = hfr,
        Stars = stars,
        GuidingRmsTotal = rms,
        Median = median
    };

    private static FrameGrader WarmedUp() {
        var grader = new FrameGrader();

        foreach (var (hfr, stars) in new[] { (2.0, 400), (2.1, 410), (1.9, 390), (2.0, 405) }) {
            Assert.That(grader.Grade(Light(hfr, stars)).Grade.Verdict, Is.EqualTo(Verdicts.Accept));
        }

        return grader;
    }

    [Test]
    public void CalibrationFrames_AreNotGraded() {
        var result = new FrameGrader().Grade(Light(5, 1) with { ImageType = "DARK" });

        Assert.That(result.Grade.Verdict, Is.EqualTo(Verdicts.NotGraded));
    }

    [Test]
    public void ZeroStars_IsRejectedEvenWithoutBaseline() {
        var result = new FrameGrader().Grade(Light(0, 0));

        Assert.That(result.Grade.Verdict, Is.EqualTo(Verdicts.Reject));
        Assert.That(result.Grade.Reasons.Single(), Does.Contain("No stars"));
    }

    [Test]
    public void TypicalFrame_IsAcceptedWithHighScore() {
        var result = WarmedUp().Grade(Light(2.05, 400));

        Assert.That(result.Grade.Verdict, Is.EqualTo(Verdicts.Accept));
        Assert.That(result.Grade.Score, Is.GreaterThanOrEqualTo(90));
        Assert.That(result.Grade.BaselineHfr, Is.EqualTo(2.0));
    }

    [Test]
    public void BloatedStars_AreRejectedWithExplanation() {
        var result = WarmedUp().Grade(Light(3.0, 380));

        Assert.That(result.Grade.Verdict, Is.EqualTo(Verdicts.Reject));
        Assert.That(result.Grade.Score, Is.LessThan(40));
        Assert.That(result.Grade.Reasons, Has.Some.Contains("HFR 3.00 is 50% above the baseline 2.00"));
    }

    [Test]
    public void SlightlySoftFrame_IsMarginal() {
        var result = WarmedUp().Grade(Light(2.4, 400));

        Assert.That(result.Grade.Verdict, Is.EqualTo(Verdicts.Marginal));
        Assert.That(result.Grade.Score, Is.InRange(40, 69));
    }

    [Test]
    public void StarLoss_IsRejectedAsClouds() {
        var result = WarmedUp().Grade(Light(2.0, 120));

        Assert.That(result.Grade.Verdict, Is.EqualTo(Verdicts.Reject));
        Assert.That(result.Grade.Reasons, Has.Some.Contains("likely clouds"));
        Assert.That(result.StarsRatio, Is.LessThan(0.45));
    }

    [Test]
    public void TinyHfrChanges_AreIgnoredOnSharpOptics() {
        var grader = new FrameGrader();

        for (var i = 0; i < 4; i++) {
            grader.Grade(Light(0.8, 400));
        }

        // +25% but only 0.2 px: below the noise floor.
        Assert.That(grader.Grade(Light(1.0, 400)).Grade.Verdict, Is.EqualTo(Verdicts.Accept));
    }

    [Test]
    public void BadGuiding_IsFlagged() {
        var result = WarmedUp().Grade(Light(2.0, 400, rms: 1.8));

        Assert.That(result.Grade.Verdict, Is.EqualTo(Verdicts.Reject));
        Assert.That(result.Grade.Reasons, Has.Some.Contains("Guiding RMS"));
    }

    [Test]
    public void RejectedFrames_DoNotPolluteTheBaseline() {
        var grader = WarmedUp();

        for (var i = 0; i < 10; i++) {
            Assert.That(grader.Grade(Light(4.0, 100)).Grade.Verdict, Is.EqualTo(Verdicts.Reject));
        }

        Assert.That(grader.Grade(Light(2.0, 400)).Grade.BaselineHfr, Is.EqualTo(2.0));
    }

    [Test]
    public void Baselines_AreSeparatePerFilter() {
        var grader = WarmedUp();

        // Narrowband has bigger stars and fewer of them: it must not be judged against L.
        var first = grader.Grade(Light(3.5, 90, filter: "Ha"));

        Assert.That(first.Grade.Verdict, Is.EqualTo(Verdicts.Accept));
        Assert.That(first.HasBaseline, Is.False);
    }

    [Test]
    public void StrictSensitivity_RejectsEarlier() {
        var normal = WarmedUp().Grade(Light(2.6, 400)).Grade.Verdict;
        var strict = new FrameGrader(GradingThresholds.For(GradingSensitivity.Strict));

        foreach (var (hfr, stars) in new[] { (2.0, 400), (2.1, 410), (1.9, 390), (2.0, 405) }) {
            strict.Grade(Light(hfr, stars));
        }

        Assert.That(normal, Is.EqualTo(Verdicts.Marginal));
        Assert.That(strict.Grade(Light(2.6, 400)).Grade.Verdict, Is.EqualTo(Verdicts.Reject));
    }
}

public class NightWatchTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 22, 0, 0, TimeSpan.Zero);
    private int ids;

    private NightWatch CreateWatch() => new(new NightWatchOptions(), () => $"id{++ids}");

    private static (FrameGrader Grader, NightWatch Watch) Prepared(NightWatch watch) {
        var grader = new FrameGrader();

        for (var i = 0; i < 4; i++) {
            var frame = FrameGraderTests.Light(2.0, 400, i);
            watch.ObserveFrame(frame, grader.Grade(frame));
        }

        return (grader, watch);
    }

    [Test]
    public void Clouds_OpenOnceAfterTwoPoorFrames_AndResolveWhenClear() {
        var (grader, watch) = Prepared(CreateWatch());
        var events = new List<EventItem>();

        foreach (var (stars, minute) in new[] { (100, 10), (80, 15), (60, 20), (390, 25), (400, 30) }) {
            var frame = FrameGraderTests.Light(2.0, stars, minute);
            events.AddRange(watch.ObserveFrame(frame, grader.Grade(frame)));
        }

        Assert.That(events.Select(e => (e.Kind, e.IncidentPhase)), Is.EqualTo(new[] {
            (EventKinds.Clouds, "open"),
            (EventKinds.Clouds, "resolve")
        }));
        Assert.That(events[0].Severity, Is.EqualTo(Severity.Warning));
        Assert.That(events[0].Timestamp, Is.EqualTo(T0.AddMinutes(15)));
        Assert.That(events[1].Timestamp, Is.EqualTo(T0.AddMinutes(30)));
    }

    [Test]
    public void FocusDrift_OpensAfterThreeSoftFrames_AndAutofocusResolvesIt() {
        var (grader, watch) = Prepared(CreateWatch());
        var events = new List<EventItem>();

        for (var i = 0; i < 3; i++) {
            var frame = FrameGraderTests.Light(2.6, 400, 10 + i);
            events.AddRange(watch.ObserveFrame(frame, grader.Grade(frame)));
        }

        events.AddRange(watch.ObserveAutofocus(T0.AddMinutes(20)));

        Assert.That(events.Select(e => (e.Kind, e.IncidentPhase)), Is.EqualTo(new[] {
            (EventKinds.FocusDrift, "open"),
            (EventKinds.FocusDrift, "resolve")
        }));
    }

    [Test]
    public void Guiding_RequiresSustainedBadRms_AndCloses() {
        var watch = CreateWatch();
        var events = new List<EventItem>();
        ObservatorySnapshot Guiding(double rms) => new() { Guider = new GuiderState { Connected = true, State = "guiding", RmsTotal = rms } };

        events.AddRange(watch.ObserveSnapshot(Guiding(3.0), T0));
        events.AddRange(watch.ObserveSnapshot(Guiding(0.6), T0.AddSeconds(30)));   // brief spike: ignored
        events.AddRange(watch.ObserveSnapshot(Guiding(3.0), T0.AddSeconds(40)));
        events.AddRange(watch.ObserveSnapshot(Guiding(3.2), T0.AddSeconds(100)));  // sustained 60 s: open
        events.AddRange(watch.ObserveSnapshot(Guiding(3.1), T0.AddSeconds(130)));  // already open: no repeat
        events.AddRange(watch.ObserveSnapshot(Guiding(0.7), T0.AddSeconds(140)));
        events.AddRange(watch.ObserveSnapshot(Guiding(0.6), T0.AddSeconds(200)));  // sustained good: resolve

        Assert.That(events.Select(e => (e.Kind, e.IncidentPhase, e.Timestamp)), Is.EqualTo(new[] {
            (EventKinds.GuidingDegraded, "open", T0.AddSeconds(100)),
            (EventKinds.GuidingDegraded, "resolve", T0.AddSeconds(200))
        }));
    }

    [Test]
    public void DewRisk_UsesHysteresis() {
        var watch = CreateWatch();
        var events = new List<EventItem>();
        ObservatorySnapshot Weather(double spread) => new() { Weather = new WeatherState { Connected = true, Temperature = 5, DewPoint = 5 - spread } };

        events.AddRange(watch.ObserveSnapshot(Weather(1.5), T0));
        events.AddRange(watch.ObserveSnapshot(Weather(1.5), T0.AddMinutes(5)));
        events.AddRange(watch.ObserveSnapshot(Weather(2.5), T0.AddMinutes(6)));    // between thresholds: stays open
        events.AddRange(watch.ObserveSnapshot(Weather(2.5), T0.AddMinutes(20)));
        events.AddRange(watch.ObserveSnapshot(Weather(3.5), T0.AddMinutes(21)));
        events.AddRange(watch.ObserveSnapshot(Weather(3.5), T0.AddMinutes(26)));

        Assert.That(events.Select(e => (e.IncidentPhase, e.Timestamp)), Is.EqualTo(new[] {
            ("open", T0.AddMinutes(5)),
            ("resolve", T0.AddMinutes(26))
        }));
    }

    [Test]
    public void NoFrames_WhileSequenceRuns_IsCritical_AndNextFrameResolves() {
        var (grader, watch) = Prepared(CreateWatch());
        var running = new ObservatorySnapshot { Sequence = new SequenceState { Running = true } };

        // 300 s exposures → window is max(15 min, 10 min) after the last frame (minute 3).
        Assert.That(watch.ObserveSnapshot(running, T0.AddMinutes(17)), Is.Empty);
        var opened = watch.ObserveSnapshot(running, T0.AddMinutes(19)).Single();
        Assert.That(watch.ObserveSnapshot(running, T0.AddMinutes(25)), Is.Empty);

        var frame = FrameGraderTests.Light(2.0, 400, 26);
        var resolved = watch.ObserveFrame(frame, grader.Grade(frame)).Single();

        Assert.That((opened.Kind, opened.Severity, opened.IncidentPhase), Is.EqualTo((EventKinds.NoFrames, Severity.Critical, "open")));
        Assert.That(resolved.IncidentPhase, Is.EqualTo("resolve"));
    }

    /// <summary>
    /// A remote rig, 13 Oct: after "Send night report" at 06:17 the sequence kept running its end steps (park, flats,
    /// warm) and "No images are being saved" opened in the same second, then stayed open on an offline card.
    /// </summary>
    [Test]
    public void NoFrames_StaysQuiet_AfterTheNightEnds_WhileWaiting_ParkedOrUnsafe() {
        var running = new SequenceState { Running = true };
        var cases = new (string Why, ObservatorySnapshot Snapshot, bool NightEnded)[] {
            ("end-of-night steps", new ObservatorySnapshot { Sequence = running }, true),
            ("wait instruction", new ObservatorySnapshot { Sequence = running with { Waiting = "Waiting for darkness" } }, false),
            ("Target Scheduler waiting", new ObservatorySnapshot { Sequence = running, Scheduler = new SchedulerState { Status = SchedulerStatus.Waiting } }, false),
            ("parked", new ObservatorySnapshot { Sequence = running, Mount = new MountState { Connected = true, AtPark = true } }, false),
            ("unsafe", new ObservatorySnapshot { Sequence = running, Safety = new SafetyState { Connected = true, IsSafe = false } }, false),
        };
        foreach (var (why, snapshot, nightEnded) in cases) {
            var (_, watch) = Prepared(CreateWatch());
            watch.NightEnded = nightEnded;
            Assert.That(watch.ObserveSnapshot(snapshot, T0.AddMinutes(40)), Is.Empty, why);
            Assert.That(watch.ObserveSnapshot(snapshot, T0.AddMinutes(90)), Is.Empty, why);
        }
    }

    [Test]
    public void NoFrames_OpenAlarm_ClosesWhenTheNightEnds_AndAWaitDoesNotCountAsStall() {
        var (_, watch) = Prepared(CreateWatch());
        var running = new ObservatorySnapshot { Sequence = new SequenceState { Running = true } };
        Assert.That(watch.ObserveSnapshot(running, T0.AddMinutes(19)).Single().IncidentPhase, Is.EqualTo("open"));

        watch.NightEnded = true;
        var closed = watch.ObserveSnapshot(running, T0.AddMinutes(20)).Single();
        Assert.That((closed.IncidentPhase, closed.Title), Is.EqualTo(("resolve", "Night ended")));

        // Next night: a two-hour wait for darkness, then imaging resumes; the wait itself is not a stall.
        watch.NightEnded = false;
        var waiting = new ObservatorySnapshot { Sequence = new SequenceState { Running = true, Waiting = "Waiting for darkness" } };
        watch.ObserveSnapshot(waiting, T0.AddHours(12));
        Assert.That(watch.ObserveSnapshot(running, T0.AddHours(12).AddMinutes(5)), Is.Empty, "just stopped waiting");
    }

    /// <summary>
    /// Two rigs, 13 Oct: every sequence start raised "No images for 10 minutes" while the rigs were still
    /// cooling, slewing, starting guiding or waiting for the target to rise. Nothing is raised before the first light
    /// frame; after it, a real stall still is, and NINA being busy (focusing, centering) doesn't count.
    /// </summary>
    [Test]
    public void NoFrames_IsNeverRaised_BeforeTheFirstLightFrame_AndBusyTimeDoesNotCount() {
        var watch = CreateWatch();
        var grader = new FrameGrader();
        var starting = new ObservatorySnapshot { Sequence = new SequenceState { Running = true } };
        // Cooling and slewing (status bar busy), then plain running with nothing saved yet: an hour and a half.
        var busy = starting with { Activity = new NinaActivity { State = ActivityStates.Busy, Headline = "Cooling camera" } };
        for (var minute = 0; minute <= 40; minute += 5) {
            Assert.That(watch.ObserveSnapshot(busy, T0.AddMinutes(minute)), Is.Empty, $"busy at {minute} min");
        }
        for (var minute = 45; minute <= 90; minute += 5) {
            Assert.That(watch.ObserveSnapshot(starting, T0.AddMinutes(minute)), Is.Empty, $"no light frame yet at {minute} min");
        }

        // Imaging begins: 300 s subs. Autofocus (busy) for 30 minutes isn't a stall either.
        var first = FrameGraderTests.Light(2.0, 400, 95);
        watch.ObserveFrame(first, grader.Grade(first));
        Assert.That(watch.ObserveSnapshot(busy, T0.AddMinutes(125)), Is.Empty, "autofocus");
        // A genuine stall after imaging started is still caught (15 min window for 300 s subs).
        Assert.That(watch.ObserveSnapshot(starting, T0.AddMinutes(135)), Is.Empty);
        Assert.That(watch.ObserveSnapshot(starting, T0.AddMinutes(142)).Single().Kind, Is.EqualTo(EventKinds.NoFrames));
    }

    [Test]
    public void ResolveAll_ClosesOpenIncidents() {
        var (grader, watch) = Prepared(CreateWatch());

        foreach (var minute in new[] { 10, 11 }) {
            var frame = FrameGraderTests.Light(2.0, 50, minute);
            watch.ObserveFrame(frame, grader.Grade(frame));
        }

        var closed = watch.ResolveAll(T0.AddHours(1), "Session ended");

        Assert.That(closed.Single().IncidentKey, Is.EqualTo("clouds"));
        Assert.That(watch.ResolveAll(T0.AddHours(2), "again"), Is.Empty);
    }
}
