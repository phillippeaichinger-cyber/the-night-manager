using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Sequencing;

namespace NightManager.Core.Tests;

public class FailureWatchTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 23, 0, 0, TimeSpan.Zero);

    private static FailureReport Center(long key = 1, int attempts = 3, string behavior = "SkipInstructionSetOnError") => new() {
        Key = key, Name = "Slew, center and rotate", Parent = "M31", Attempts = attempts, ErrorBehavior = behavior,
        Error = "Plate solve failed: no stars found",
    };

    private static bool Notifies(EventItem e) => e.Data?["notify"] is true;

    [Test]
    public void Retries_are_quiet_and_the_final_failure_notifies_with_what_happens_next() {
        var watch = new FailureWatch();
        var first = watch.OnFailure(Center(), T0).Single();
        var second = watch.OnFailure(Center(), T0.AddMinutes(1)).Single();
        var final = watch.OnFailure(Center(), T0.AddMinutes(2)).Single();

        Assert.Multiple(() => {
            Assert.That(Notifies(first), Is.False);
            Assert.That(first.Title, Is.EqualTo("Slew, center and rotate failed in M31, retrying"));
            Assert.That(first.Message, Does.EndWith("Retrying (attempt 2 of 3)."));
            Assert.That(Notifies(second), Is.False);
            Assert.That(Notifies(final), Is.True);
            Assert.That(final.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(final.Message, Is.EqualTo("Plate solve failed: no stars found. NINA skips the rest of this block."));
        });
    }

    [Test]
    public void A_retry_that_works_is_a_recovery_not_an_alert() {
        var watch = new FailureWatch();
        watch.OnFailure(Center(), T0);
        var recovered = watch.OnRecovered(1, "Slew, center and rotate", T0.AddMinutes(1)).Single();
        Assert.That(recovered.Kind, Is.EqualTo(EventKinds.SequenceItemRecovered));
        Assert.That(recovered.Title, Is.EqualTo("Slew, center and rotate succeeded after 1 failed attempt"));
        Assert.That(watch.Retrying, Is.Empty);
    }

    [Test]
    public void Aborting_failures_are_critical_and_remembered_for_the_end_of_sequence_message() {
        var watch = new FailureWatch();
        var e = watch.OnFailure(Center(attempts: 1, behavior: "AbortOnError"), T0).Single();
        Assert.That(e.Severity, Is.EqualTo(Severity.Critical));
        Assert.That(e.Message, Does.EndWith("NINA is aborting the sequence."));
        Assert.That(watch.LastAbort?.Name, Is.EqualTo("Slew, center and rotate"));
    }

    [Test]
    public void A_cascade_sends_one_alert_then_one_summary() {
        var watch = new FailureWatch();
        var events = new List<EventItem>();
        for (var i = 0; i < 6; i++) {
            events.AddRange(watch.OnFailure(new FailureReport { Key = 10 + i, Name = i % 2 == 0 ? "Take exposure" : "Dither", Error = "Camera disconnected" }, T0.AddSeconds(i * 5)));
        }
        Assert.That(events.Count(Notifies), Is.EqualTo(1), "only the first failure notifies right away");
        Assert.That(watch.Tick(T0.AddMinutes(1)), Is.Empty, "window still open");
        var summary = watch.Tick(T0.AddMinutes(2)).Single();
        Assert.That(summary.Kind, Is.EqualTo(EventKinds.SequenceFailureBurst));
        Assert.That(summary.Title, Is.EqualTo("5 more instructions failed"));
        Assert.That(summary.Message, Does.StartWith("Dither ×3, Take exposure ×2"));
    }

    [Test]
    public void A_block_policy_can_ignore_or_raise_failures() {
        var watch = new FailureWatch();
        var ignored = watch.OnFailure(Center(attempts: 1) with { Policy = new FailurePolicy { Ignore = ["center"] } }, T0).Single();
        Assert.That(Notifies(ignored), Is.False);

        var loud = watch.OnFailure(Center(key: 2, attempts: 3) with { Policy = new FailurePolicy { EveryAttempt = true, Level = Severity.Critical } }, T0.AddHours(1)).Single();
        Assert.That(Notifies(loud), Is.True, "every attempt notifies when asked to");
    }

    [Test]
    public void A_failed_meridian_flip_trigger_is_critical() {
        var watch = new FailureWatch();
        var e = watch.OnFailure(new FailureReport { Key = 5, Name = "Meridian Flip", Kind = "trigger", Error = "Slew failed" }, T0).Single();
        Assert.That(e.Severity, Is.EqualTo(Severity.Critical));
        Assert.That(Notifies(e), Is.True);
    }
}

public class PhoneControlTests {
    [Test]
    public async Task An_answer_from_the_phone_releases_the_waiting_instruction() {
        var phone = new PhoneControl();
        var prompt = phone.Ask("ask", "Open the roof?", null, "Open", "Skip", TimeSpan.FromMinutes(10), "no");
        Assert.That(phone.State.Prompts.Single().Id, Is.EqualTo(prompt.Id));

        var waiting = phone.WaitForAnswerAsync(prompt.Id, CancellationToken.None);
        var message = phone.Handle(new RemoteCommand {
            Id = "c1", Type = CommandTypes.Answer, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Args = new Dictionary<string, string> { ["promptId"] = prompt.Id, ["answer"] = "yes" },
        });
        var (answer, timedOut) = await waiting;
        Assert.Multiple(() => {
            Assert.That(message, Is.EqualTo("Answer received."));
            Assert.That(answer, Is.EqualTo("yes"));
            Assert.That(timedOut, Is.False);
            Assert.That(phone.State.Prompts, Is.Empty);
        });
    }

    [Test]
    public async Task No_answer_in_time_uses_the_default() {
        var phone = new PhoneControl();
        var prompt = phone.Ask("ask", "Open the roof?", null, "Open", "Skip", TimeSpan.FromMilliseconds(50), "no");
        var (answer, timedOut) = await phone.WaitForAnswerAsync(prompt.Id, CancellationToken.None);
        Assert.That(answer, Is.EqualTo("no"));
        Assert.That(timedOut, Is.True);
    }

    [Test]
    public async Task A_pause_point_holds_until_resumed_and_finish_releases_it_too() {
        var phone = new PhoneControl();
        Assert.That(await phone.HoldIfPausedAsync(null, CancellationToken.None), Is.False, "not paused: pass straight through");

        phone.Pause();
        var holding = phone.HoldIfPausedAsync(null, CancellationToken.None);
        await Task.Delay(30);
        Assert.That(holding.IsCompleted, Is.False);
        Assert.That(phone.State.HoldingAtPause, Is.True);
        phone.RequestFinish();
        Assert.That(await holding, Is.True);
        Assert.That(phone.FinishRequested, Is.True);

        phone.ResetForNewSequence();
        Assert.That(phone.FinishRequested, Is.False);
    }

    [Test]
    public void Acknowledging_stops_every_alarm() {
        var phone = new PhoneControl();
        var a = phone.RaiseAlarm("Roof didn't close", null);
        phone.RaiseAlarm("Rain", null);
        Assert.That(phone.Acknowledge(null), Is.EqualTo(2));
        Assert.That(phone.Acknowledged(a.Id).IsCompleted, Is.True);
    }
}

public class MessageTokensTests {
    [Test]
    public void Fills_in_known_placeholders_and_leaves_others() {
        var s = new ObservatorySnapshot {
            Sequence = new SequenceState { Running = true, TargetName = "M31" },
            FilterWheel = new FilterWheelState { Connected = true, CurrentFilter = "Ha" },
            Session = new SessionSummary { Id = "s", Frames = 24, Accepted = 22, IntegrationSeconds = 6300, Last = new LastFrame { Verdict = "accept", Hfr = 2.186 } },
        };
        var text = MessageTokens.Resolve("{target} in {filter}: {accepted}/{subs} subs, {usable}, HFR {hfr} {unknown}", s);
        Assert.That(text, Is.EqualTo("M31 in Ha: 22/24 subs, 1h 45m, HFR 2.19 {unknown}"));
    }
}
