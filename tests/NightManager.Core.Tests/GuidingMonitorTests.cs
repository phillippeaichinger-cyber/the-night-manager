using NightManager.Core.Analysis;

namespace NightManager.Core.Tests;

public class GuidingMonitorTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);

    [Test]
    public void Computes_rms_and_peaks_in_arcseconds() {
        // FF130 RIGHT's guide camera: 0.8251″/px.
        var monitor = new GuidingMonitor { PixelScale = 0.8251 };
        double[] ra = [0.5, -0.5, 0.5, -0.5];
        double[] dec = [0.3, -0.3, 0.3, -0.3];
        for (var i = 0; i < ra.Length; i++) {
            monitor.AddStep(ra[i], dec[i], T0.AddSeconds(i * 2));
        }

        var stats = monitor.Current(T0.AddSeconds(8))!;
        Assert.Multiple(() => {
            Assert.That(stats.RmsRa, Is.EqualTo(0.413).Within(0.001));
            Assert.That(stats.RmsDec, Is.EqualTo(0.248).Within(0.001));
            Assert.That(stats.RmsTotal, Is.EqualTo(Math.Sqrt(0.41255 * 0.41255 + 0.24753 * 0.24753)).Within(0.002));
            Assert.That(stats.PeakRa, Is.EqualTo(0.413).Within(0.001));
            Assert.That(stats.Steps, Is.EqualTo(4));
        });
    }

    [Test]
    public void Only_recent_steps_count_and_stale_guiding_reads_as_none() {
        var monitor = new GuidingMonitor(TimeSpan.FromMinutes(2)) { PixelScale = 1 };
        monitor.AddStep(5, 5, T0);              // a bad patch long ago
        monitor.AddStep(0.2, 0.1, T0.AddMinutes(3));
        monitor.AddStep(-0.2, -0.1, T0.AddMinutes(3).AddSeconds(2));

        var stats = monitor.Current(T0.AddMinutes(3).AddSeconds(2))!;
        Assert.That(stats.RmsRa, Is.EqualTo(0.2).Within(0.001), "the old step fell out of the window");
        Assert.That(monitor.Current(T0.AddMinutes(10)), Is.Null, "no corrections for minutes: not guiding");
    }

    [Test]
    public void Needs_a_pixel_scale_and_resets_when_guiding_stops() {
        var monitor = new GuidingMonitor();
        monitor.AddStep(1, 1, T0);
        monitor.AddStep(1, 1, T0.AddSeconds(2));
        Assert.That(monitor.Current(T0.AddSeconds(2)), Is.Null, "no scale yet: don't report pixels as arcseconds");

        monitor.PixelScale = 1.2;
        Assert.That(monitor.Current(T0.AddSeconds(2)), Is.Not.Null);
        monitor.Reset();
        Assert.That(monitor.Current(T0.AddSeconds(2)), Is.Null);
    }
}
