using NightManager.Core.Analysis;
using NightManager.Core.Contracts;

namespace NightManager.Core.Tests;

public class AutofocusTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 4, 8, 12, TimeSpan.Zero);

    private static string RealReport() => File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Resources", "hocusfocus-report.json"));

    [Test]
    public void Parses_a_real_hocus_focus_report_from_the_observatory() {
        // A home rig, 12 Oct 2026 06:10 CEST: O filter, 9 points, hyperbolic fit.
        var r = AutofocusReport.Parse(RealReport());

        Assert.Multiple(() => {
            Assert.That(r.Engine, Is.EqualTo("Hocus Focus"));
            Assert.That(r.Filter, Is.EqualTo("O"));
            Assert.That(r.Temperature, Is.EqualTo(10.6).Within(0.01));
            Assert.That(r.Points, Has.Count.EqualTo(9));
            Assert.That(r.Points[0].Position, Is.EqualTo(11825));
            Assert.That(r.Points[0].Value, Is.EqualTo(7.3249).Within(0.0001));
            Assert.That(r.Points.Select(p => p.Position), Is.Ordered);
            Assert.That(r.Final!.Position, Is.EqualTo(12016.152).Within(0.001));
            Assert.That(r.FinalHfr, Is.EqualTo(1.4924).Within(0.0001));
            Assert.That(r.DurationSeconds, Is.EqualTo(162));
            Assert.That(r.Fit!.Type, Is.EqualTo("hyperbolic"));
            Assert.That(r.Fit.A, Is.EqualTo(2.144));
            Assert.That(r.Fit.B, Is.EqualTo(54.121));
            Assert.That(r.Fit.C, Is.EqualTo(12016.152));
            Assert.That(r.Fit.D, Is.EqualTo(-0.639));
            Assert.That(r.Fit.RSquared, Is.EqualTo(0.9984).Within(0.0001));
        });

        // The fitted curve passes through its own minimum at the calculated focus point.
        var f = r.Fit!;
        var atBest = f.A / f.B * Math.Sqrt(Math.Pow(f.C - f.C, 2) + f.B * f.B) + f.D;
        Assert.That(atBest, Is.EqualTo(r.Final!.Value).Within(0.01));
    }

    [Test]
    public void Parses_a_quadratic_fit() {
        var r = AutofocusReport.Parse("""
            {"Filter":"L","Fitting":"PARABOLIC","MeasurePoints":[{"Position":100,"Value":3.0,"Error":0.1}],
             "CalculatedFocusPoint":{"Position":120,"Value":1.9},
             "Fittings":{"Quadratic":"y = 1.5E-05 * x² + -0.0036 * x + 2.116","Hyperbolic":""},"RSquares":{"Quadratic":0.97},
             "Duration":"00:01:05"}
            """);
        Assert.That(r.Fit!.Type, Is.EqualTo("quadratic"));
        Assert.That(r.Fit.A, Is.EqualTo(1.5e-5));
        Assert.That(r.Fit.C, Is.EqualTo(2.116));
        Assert.That(r.DurationSeconds, Is.EqualTo(65));
    }

    [Test]
    public void Nina_autofocus_shows_points_live_then_the_report_finishes_it() {
        var tracker = new AutofocusTracker();
        Assert.That(tracker.Start(T0, "NINA", "O", 10.6, 12025), Is.True);
        Assert.That(tracker.Start(T0.AddSeconds(1), "NINA", null, null, null), Is.False, "the sequencer signal for the same run");

        tracker.AddPoint(11825, 7.32, T0.AddSeconds(20));
        tracker.AddPoint(11875, 5.31, T0.AddSeconds(38));
        var live = tracker.Current!;
        Assert.That(live.Status, Is.EqualTo(AutofocusStatus.Running));
        Assert.That(live.Points.Select(p => p.Value), Is.EqualTo(new[] { 7.32, 5.31 }));

        var done = tracker.Complete(AutofocusReport.Parse(RealReport()), T0.AddSeconds(162));
        Assert.That(done.Status, Is.EqualTo(AutofocusStatus.Finished));
        Assert.That(done.Points, Has.Count.EqualTo(9), "the report's full set replaces the live points");
        Assert.That(done.Fit, Is.Not.Null);
        Assert.That(done.StartedAt, Is.EqualTo(T0));
    }

    [Test]
    public void Hocus_focus_runs_count_focuser_steps_until_the_report_arrives() {
        var tracker = new AutofocusTracker();
        tracker.Start(T0, "Hocus Focus", "O", 10.6, 12025);
        foreach (var pos in new[] { 11825, 11875, 11925, 11925, 11975 }) {
            tracker.FocuserMoved(pos, T0.AddSeconds(10));
        }
        Assert.That(tracker.Current!.Steps, Is.EqualTo(4), "a repeated position is not a new step");
        Assert.That(tracker.Current.FocuserPosition, Is.EqualTo(11975));
        Assert.That(tracker.Current.Points, Is.Empty);
    }

    [Test]
    public void Runs_that_never_report_fail_and_old_results_drop_out() {
        var tracker = new AutofocusTracker();
        tracker.Start(T0, "NINA", "Ha", null, null);
        Assert.That(tracker.Tick(T0.AddMinutes(10), stillRunningInSequence: false), Is.Null);
        var failed = tracker.Tick(T0.AddMinutes(16), stillRunningInSequence: false);
        Assert.That(failed!.Status, Is.EqualTo(AutofocusStatus.Failed));

        tracker.Tick(T0.AddMinutes(32), stillRunningInSequence: false);
        Assert.That(tracker.Current, Is.Null, "results are shown for 15 minutes");
    }

    [Test]
    public void Without_a_report_the_end_signal_still_finishes_the_run() {
        var tracker = new AutofocusTracker();
        tracker.Start(T0, "NINA", "Ha", 9.0, 5000);
        tracker.AddPoint(4900, 3.1, T0.AddSeconds(10));
        tracker.AddPoint(5000, 2.2, T0.AddSeconds(20));
        var done = tracker.CompleteWithoutReport(4980, 9.1, "Ha", T0.AddSeconds(60))!;
        Assert.That(done.Status, Is.EqualTo(AutofocusStatus.Finished));
        Assert.That(done.Final!.Position, Is.EqualTo(4980));
        Assert.That(done.DurationSeconds, Is.EqualTo(60));
    }
}
