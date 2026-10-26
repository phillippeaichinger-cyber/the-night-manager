using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Focus;

namespace NightManager.Core.Tests;

public class FocusCoachTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Nights of runs cooling from 14 °C to 9 °C, focus moving 9 steps per °C, Hα 20 steps from OIII.</summary>
    private static List<FocusRun> Season(int nights, double shiftLastTwo = 0) {
        var runs = new List<FocusRun>();
        for (var n = 0; n < nights; n++) {
            for (var k = 0; k < 6; k++) {
                var t = 14 - k;
                var filter = k % 3 == 0 ? "Ha" : "OIII";
                var shift = n >= nights - 2 ? shiftLastTwo : 0;
                runs.Add(new FocusRun { At = Now.AddDays(-nights + n).AddHours(9 + k).ToUnixTimeSeconds(), Filter = filter, Temperature = t,
                    Position = 12000 - 9 * t + (filter == "Ha" ? 20 : 0) + (n % 2 == 0 ? 2 : -2) + shift, Hfr = 1.5, ToleranceSteps = 18 });
            }
        }
        return runs;
    }

    [Test]
    public void It_Learns_Before_It_Advises() {
        var advice = FocusCoach.Assess(Season(3), Now, () => "id");
        Assert.Multiple(() => {
            Assert.That(advice.Status, Is.EqualTo(FocusAdviceStatus.Learning));
            Assert.That(advice.Coefficient, Is.Null);
            Assert.That(advice.Messages.Single(), Does.Contain("4 more nights"));
            Assert.That(advice.Progress, Is.EqualTo(3 / 7.0).Within(0.01));
        });
    }

    [Test]
    public void From_A_Week_It_Gives_The_Coefficient_Offsets_And_Refocus_Interval() {
        var advice = FocusCoach.Assess(Season(8), Now, () => "id");
        Assert.Multiple(() => {
            Assert.That(advice.Status, Is.EqualTo(FocusAdviceStatus.Ready));
            Assert.That(advice.Coefficient, Is.EqualTo(-9).Within(0.5));
            Assert.That(advice.Offsets.Single().Filter, Is.EqualTo("Ha"));
            Assert.That(advice.Offsets.Single().Steps, Is.EqualTo(20).Within(2));
            Assert.That(advice.RefocusDegrees, Is.EqualTo(2.0).Within(0.1));
            Assert.That(advice.Nightly, Has.Count.EqualTo(8));
        });
    }

    [Test]
    public void A_Sudden_Shift_Stands_Out_From_Temperature_Drift() {
        var advice = FocusCoach.Assess(Season(10, shiftLastTwo: 120), Now, () => "id");
        Assert.That(advice.Messages, Has.Some.Contains("shifted about 120 steps"));
    }

    [Test]
    public void Tolerance_Comes_From_The_V_Curve() {
        // HFR = 1.5 + 0.0002 (x − 10000)²: 5% bigger at √(0.075 / 0.0002) ≈ 19.4 steps.
        var points = Enumerable.Range(-5, 11).Select(i => new FocusPoint { Position = 10000 + i * 15, Value = 1.5 + 0.0002 * Math.Pow(i * 15, 2) }).ToList();
        Assert.That(FocusHistory.Tolerance(points), Is.EqualTo(19.4).Within(0.5));
    }
}
