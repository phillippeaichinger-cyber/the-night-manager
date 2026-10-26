using NightManager.Core.Analysis;
using NightManager.Core.Contracts;

namespace NightManager.Core.Tests;

public class StatusBarTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 18, 30, 0, TimeSpan.Zero);

    [TestCase("Waiting 30:18", "Waiting", 30 * 60 + 18)]
    [TestCase("Waiting 01:12:05", "Waiting", 3600 + 12 * 60 + 5)]
    [TestCase("Waiting 01:00 / 05:00", "Waiting", 240)]
    [TestCase("Waiting 00:10:00 / 01:00:00", "Waiting", 50 * 60)]
    [TestCase("Waiting 12 s", "Waiting", 12)]
    [TestCase("Waiting 3 s / 10 s", "Waiting", 7)]
    [TestCase("Settle 00:00:30", "Settle", 30)]
    public void Splits_timers_off(string status, string text, int seconds) {
        var (t, left) = StatusBar.Split(status);
        Assert.That(t, Is.EqualTo(text));
        Assert.That(left, Is.EqualTo(TimeSpan.FromSeconds(seconds)));
    }

    [TestCase("Slewing")]
    [TestCase("Exposure 3/10")]
    [TestCase("Current altitude -9.4°, waiting until < -12°")]
    public void Leaves_other_text_alone(string status) {
        Assert.That(StatusBar.Split(status), Is.EqualTo((status, (TimeSpan?)null)));
    }

    [Test]
    public void End_time_holds_steady_while_the_timer_ticks() {
        var bar = new StatusBar();
        var first = bar.Read([new RawStatus { Source = "Sequence", Status = "Waiting 30:18" }], Now).Single();
        // 1.3 s later NINA says 30:17 (its timer drifts a little against the clock).
        var second = bar.Read([new RawStatus { Source = "Sequence", Status = "Waiting 30:17" }], Now.AddSeconds(1.3)).Single();
        Assert.That(first.Until, Is.EqualTo(Now.AddSeconds(30 * 60 + 18)));
        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void Progress_is_rounded_to_steps_and_counts_are_written_out() {
        var lines = new StatusBar().Read([
            new RawStatus { Source = "Flat wizard", Status = "Taking flats", Progress = 3, MaxProgress = 10, IsPercent = false },
            new RawStatus { Source = "Autofocus", Status = "Measuring", Progress = 0.437 },
            new RawStatus { Source = "Gone", Status = "" },
        ], Now);
        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines[0].Count, Is.EqualTo("3 / 10"));
        Assert.That(lines[0].Progress, Is.EqualTo(0.3).Within(1e-9));
        Assert.That(lines[1].Progress, Is.EqualTo(0.45).Within(1e-9));
    }

    [Test]
    public void Idle_when_nothing_runs_and_the_bar_is_empty() {
        var activity = StatusBar.Compose([], "Sequence", new SequenceState { Running = false }, null);
        Assert.That(activity.State, Is.EqualTo(ActivityStates.Idle));
        Assert.That(activity.Headline, Is.EqualTo("Idle"));
    }

    [Test]
    public void A_wait_shows_what_it_waits_for_and_until_when() {
        var lines = new StatusBar().Read([new RawStatus { Source = "Sequence", Status = "Waiting 30:18" }], Now);
        var activity = StatusBar.Compose(lines, "Sequence",
            new SequenceState { Running = true, Waiting = "Waiting for darkness", WaitingUntil = Now.AddMinutes(31) }, null);
        Assert.That(activity.State, Is.EqualTo(ActivityStates.Waiting));
        Assert.That(activity.Headline, Is.EqualTo("Waiting for darkness"));
        Assert.That(activity.Detail, Is.Null);
        Assert.That(activity.Until, Is.EqualTo(Now.AddSeconds(30 * 60 + 18)));
    }

    [Test]
    public void A_wait_without_a_timer_keeps_ninas_words_as_detail() {
        var lines = new StatusBar().Read([new RawStatus { Source = "Sequence", Status = "Current altitude -9.4°, waiting until < -12°" }], Now);
        var activity = StatusBar.Compose(lines, "Sequence",
            new SequenceState { Running = true, Waiting = "Waiting for darkness", WaitingUntil = Now.AddMinutes(40) }, null);
        Assert.That(activity.Detail, Is.EqualTo("Current altitude -9.4°, waiting until < -12°"));
        Assert.That(activity.Until, Is.EqualTo(Now.AddMinutes(40)));
    }

    [Test]
    public void A_running_step_is_named() {
        var lines = new StatusBar().Read([new RawStatus { Source = "Sequence", Status = "Slewing" }], Now);
        var activity = StatusBar.Compose(lines, "Sequence", new SequenceState { Running = true, TargetName = "M 31" }, new CameraState { State = "idle" });
        Assert.That(activity.State, Is.EqualTo(ActivityStates.Busy));
        Assert.That(activity.Headline, Is.EqualTo("Slewing"));
        Assert.That(activity.Detail, Is.EqualTo("M 31"));
    }

    [Test]
    public void Exposing_reads_as_imaging_the_target() {
        var activity = StatusBar.Compose([], "Sequence", new SequenceState { Running = true, TargetName = "M 31" }, new CameraState { State = "exposing" });
        Assert.That(activity.State, Is.EqualTo(ActivityStates.Imaging));
        Assert.That(activity.Headline, Is.EqualTo("Imaging M 31"));
    }

    [Test]
    public void Outside_a_sequence_other_work_is_shown() {
        var lines = new StatusBar().Read([new RawStatus { Source = "Autofocus", Status = "Measuring HFR", Progress = 0.5 }], Now);
        var activity = StatusBar.Compose(lines, "Sequence", new SequenceState { Running = false }, null);
        Assert.That(activity.State, Is.EqualTo(ActivityStates.Busy));
        Assert.That(activity.Headline, Is.EqualTo("Measuring HFR"));
        Assert.That(activity.Detail, Is.EqualTo("Autofocus"));
        Assert.That(activity.Progress, Is.EqualTo(0.5));
    }

    [Test]
    public void A_line_that_only_says_idle_is_not_activity() {
        var lines = new StatusBar().Read([new RawStatus { Source = "Sync Service", Status = "Idle" }], Now);
        var activity = StatusBar.Compose(lines, "Sequence", new SequenceState { Running = false }, null);
        Assert.That(activity.State, Is.EqualTo(ActivityStates.Idle));
        Assert.That(activity.Lines, Has.Count.EqualTo(1));
    }
}

public class ThumbnailPolicyTests {
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 21, 0, 0, TimeSpan.Zero);

    [Test]
    public void Every_sub_of_three_minutes_or_more_gets_one() {
        var policy = new NightManager.Core.Analysis.ThumbnailPolicy();
        for (var i = 0; i < 20; i++) {
            Assert.That(policy.ShouldSend(Start.AddSeconds(i * 190), 180, "M31", "L", "accept"), Is.True);
        }
    }

    [Test]
    public void Short_subs_are_sampled_every_three_minutes() {
        var policy = new NightManager.Core.Analysis.ThumbnailPolicy();
        var sent = Enumerable.Range(0, 60).Count(i => policy.ShouldSend(Start.AddSeconds(i * 34), 30, "M31", "L", "accept"));
        Assert.That(sent, Is.EqualTo(10)); // subs every 34 s: the first, then one at least 3 min after the last (every 204 s)
    }

    [Test]
    public void A_new_filter_and_a_couple_of_rejects_an_hour_always_show() {
        var policy = new NightManager.Core.Analysis.ThumbnailPolicy();
        Assert.That(policy.ShouldSend(Start, 10, "M31", "L", "accept"), Is.True);
        Assert.That(policy.ShouldSend(Start.AddSeconds(14), 10, "M31", "Ha", "accept"), Is.True);
        Assert.That(policy.ShouldSend(Start.AddSeconds(28), 10, "M31", "Ha", "reject"), Is.True);
        Assert.That(policy.ShouldSend(Start.AddSeconds(42), 10, "M31", "Ha", "reject"), Is.True);
        Assert.That(policy.ShouldSend(Start.AddSeconds(56), 10, "M31", "Ha", "reject"), Is.False);
    }
}
