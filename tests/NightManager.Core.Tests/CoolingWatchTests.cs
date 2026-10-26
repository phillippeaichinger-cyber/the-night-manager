using NightManager.Core.Analysis;
using NightManager.Core.Contracts;

namespace NightManager.Core.Tests;

public class CoolingWatchTests {
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 16, 12, 0, TimeSpan.Zero);

    private static ObservatorySnapshot Camera(double temperature, bool coolerOn = true, double target = -10, double power = 40) => new() {
        Camera = new CameraState {
            Connected = true, Name = "ZWO ASI1600MM Pro", State = "idle",
            Temperature = temperature, TargetTemperature = target, CoolerOn = coolerOn, CoolerPower = power,
        },
    };

    /// <summary>Feeds one snapshot every 10 s (the plugin's tick) along a temperature curve.</summary>
    private static List<EventItem> Run(NightWatch watch, Func<double, double> temperatureAtMinute, double minutes, DateTimeOffset from, Func<double, bool>? coolerOn = null) {
        var events = new List<EventItem>();
        for (var t = 0.0; t <= minutes * 60; t += 10) {
            var minute = t / 60;
            events.AddRange(watch.ObserveSnapshot(Camera(temperatureAtMinute(minute), coolerOn?.Invoke(minute) ?? true), from.AddSeconds(t)));
        }
        return events;
    }

    [Test]
    public void Normal_cooldown_raises_no_alarm_and_reports_ready_once() {
        var watch = new NightWatch();
        // Like the observatory on 12 Oct: 13.5 °C → −10 °C in about 14 minutes, then holding.
        var events = Run(watch, m => Math.Max(-10, 13.5 - m * 1.7), 40, Start);

        Assert.That(events.Where(e => e.Kind == EventKinds.CoolingLost), Is.Empty, "cooling down is not 'can't hold temperature'");
        var ready = events.Single(e => e.Kind == EventKinds.CameraCooled);
        Assert.That(ready.Title, Is.EqualTo("Camera at -10.0°C"));
        Assert.That(ready.Message, Does.StartWith("Reached its set point in 15 min"), "reached at 14 min, held for a minute");
        Assert.That(ready.IncidentKey, Is.Null, "a one-off notice, not an incident");
    }

    [Test]
    public void Drifting_after_reaching_the_set_point_is_still_an_alarm() {
        var watch = new NightWatch();
        Run(watch, m => Math.Max(-10, 13.5 - m * 1.7), 20, Start);
        var drift = Run(watch, _ => -6.5, 5, Start.AddMinutes(21));

        Assert.That(drift.Single(e => e.IncidentPhase == "open").Kind, Is.EqualTo(EventKinds.CoolingLost));
        var back = Run(watch, _ => -10, 5, Start.AddMinutes(27));
        Assert.That(back.Single(e => e.IncidentPhase == "resolve").Title, Does.StartWith("Camera back at -10.0"));
    }

    [Test]
    public void A_camera_that_cant_get_down_warns_after_twenty_minutes_and_clears_when_it_arrives() {
        var watch = new NightWatch();
        // Warm night: stalls at −6 °C for a long time, finally gets there after 40 min.
        var events = Run(watch, m => m < 40 ? Math.Max(-6, 13.5 - m * 1.7) : -10, 45, Start);

        var slow = events.Single(e => e.Kind == EventKinds.CoolingSlow && e.IncidentPhase == "open");
        Assert.That(slow.Timestamp, Is.EqualTo(Start.AddMinutes(20)));
        Assert.That(slow.Title, Is.EqualTo("Camera can't reach -10.0°C"));
        Assert.That(slow.Message, Does.Contain("-6.0°C with the cooler at 40%"));
        Assert.That(events.Single(e => e.Kind == EventKinds.CoolingSlow && e.IncidentPhase == "resolve").Title, Is.EqualTo("Camera reached -10.0°C"));
        Assert.That(events.Where(e => e.Kind == EventKinds.CameraCooled), Is.Empty, "the resolve already says it arrived");
        Assert.That(events.Where(e => e.Kind == EventKinds.CoolingLost), Is.Empty);
    }

    [Test]
    public void Starting_with_the_camera_already_cold_says_nothing() {
        var watch = new NightWatch();
        var events = Run(watch, _ => -10.1, 10, Start);
        Assert.That(events, Is.Empty);
    }

    [Test]
    public void Switching_the_cooler_off_clears_open_cooling_alerts_and_starts_over() {
        var watch = new NightWatch();
        Run(watch, m => Math.Max(-6, 13.5 - m * 1.7), 25, Start);
        var off = watch.ObserveSnapshot(Camera(-5, coolerOn: false), Start.AddMinutes(26));
        Assert.That(off.Single().Title, Is.EqualTo("Camera cooler switched off"));

        // Next evening's cooldown is judged afresh.
        var again = Run(watch, m => Math.Max(-10, 12 - m * 2), 15, Start.AddHours(24));
        Assert.That(again.Single().Kind, Is.EqualTo(EventKinds.CameraCooled));
    }

    /// <summary>
    /// A remote rig's sequence on 12 Oct: NINA's Cool Camera ramps the set point 21 → −10 °C one degree about every
    /// 30 s, the sensor following closely. That produced a "Camera at X °C" for every step; it must be one.
    /// </summary>
    [Test]
    public void A_ramped_cooldown_reports_once_at_the_final_target() {
        var watch = new NightWatch();
        var events = new List<EventItem>();
        for (var t = 0; t <= 40 * 60; t += 10) {
            var setPoint = Math.Max(-10, 21 - t / 30);
            var sensor = Math.Max(-10, 21.3 - t / 30.0);
            events.AddRange(watch.ObserveSnapshot(Camera(sensor, target: setPoint), Start.AddSeconds(t)));
        }

        var ready = events.Where(e => e.Kind == EventKinds.CameraCooled).ToList();
        Assert.That(ready, Has.Count.EqualTo(1), string.Join(", ", ready.Select(e => e.Title)));
        Assert.That(ready[0].Title, Is.EqualTo("Camera at -10.0°C"));
        // Ramp ends at 15.5 min; settled 3 min later, held 1 min: about 19 min after the cooler came on.
        Assert.That(ready[0].Timestamp, Is.EqualTo(Start.AddMinutes(18.5)).Within(TimeSpan.FromMinutes(1)));
        Assert.That(events.Where(e => e.Kind is EventKinds.CoolingLost or EventKinds.CoolingSlow), Is.Empty);
    }

    [Test]
    public void A_long_ramp_does_not_count_against_the_twenty_minute_limit() {
        var watch = new NightWatch();
        // 30-minute ramp to −15 °C, then it gets there 5 minutes after the ramp ends.
        var events = new List<EventItem>();
        for (var t = 0; t <= 45 * 60; t += 10) {
            var setPoint = Math.Max(-15, 15 - t / 60);
            var sensor = t < 30 * 60 ? setPoint + 0.4 : Math.Max(-15, -15 + 3 - (t - 30 * 60) / 100.0);
            events.AddRange(watch.ObserveSnapshot(Camera(sensor, target: setPoint), Start.AddSeconds(t)));
        }
        Assert.That(events.Where(e => e.Kind == EventKinds.CoolingSlow), Is.Empty);
        Assert.That(events.Count(e => e.Kind == EventKinds.CameraCooled), Is.EqualTo(1));
    }

    [Test]
    public void Ready_notice_can_be_switched_off() {
        var watch = new NightWatch(new NightWatchOptions { ReportCooledDown = false });
        Assert.That(Run(watch, m => Math.Max(-10, 13.5 - m * 1.7), 20, Start), Is.Empty);
    }
}
