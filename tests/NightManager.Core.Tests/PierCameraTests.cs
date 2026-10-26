using NightManager.Core.Cameras;
using NUnit.Framework;

namespace NightManager.Core.Tests;

public class PierCameraTests {
    [Test]
    public void The_ZWO_driver_is_for_pier_cameras_only_and_night_exposures_stay_short() {
        var zwo = new CameraSettings { Kind = CameraSourceKind.Zwo, AllSky = true, MaxNightExposureSeconds = 60 }.Normalized();
        Assert.That(zwo.AllSky, Is.False); // moved to the pier section
        Assert.That(zwo.MaxNightExposureSeconds, Is.EqualTo(PierExposure.MaxNightCapSeconds));
        var folder = new CameraSettings { Kind = CameraSourceKind.Folder, AllSky = true }.Normalized();
        Assert.That(folder.AllSky, Is.True); // all-sky cameras keep working as they do
        Assert.That(new CameraSettings { Kind = CameraSourceKind.Zwo, MaxNightExposureSeconds = double.NaN }.Normalized().MaxNightExposureSeconds,
            Is.EqualTo(PierExposure.DefaultMaxNightSeconds));
    }

    [Test]
    public void Exposure_finds_the_light_and_never_passes_the_night_cap() {
        // Dark pier, no lamp: keeps lengthening, but stops at the cap.
        var us = PierExposure.StartMicroseconds(dark: true, 2);
        for (var i = 0; i < 6; i++) us = PierExposure.Next(us, median: 3, saturated: 0, dark: true, 2);
        Assert.That(us, Is.EqualTo(2_000_000));
        // An IR lamp comes on and the frame burns out: cuts hard.
        Assert.That(PierExposure.Next(us, median: 250, saturated: 0.6, dark: true, 2), Is.EqualTo(400_000));
        // Daylight: from 1 ms down to tens of microseconds, never below the floor.
        us = PierExposure.StartMicroseconds(dark: false, 2);
        for (var i = 0; i < 8; i++) us = PierExposure.Next(us, median: 255, saturated: 0.9, dark: false, 2);
        Assert.That(us, Is.EqualTo(PierExposure.DayMinMicroseconds));
        // On target: about the same again.
        Assert.That(PierExposure.Next(10_000, median: 70, saturated: 0, dark: false, 2), Is.EqualTo(10_000));
        Assert.That(PierExposure.Good(70, 0), Is.True);
        Assert.That(PierExposure.Good(5, 0), Is.False);
    }

    [Test]
    public void A_frame_is_cleaned_shrunk_and_stretched() {
        const int w = 400, h = 300;
        var raw = new byte[w * h];
        var rng = new Random(1);
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(20 + rng.Next(4)); // a dim, flat pier
        for (var y = 100; y < 200; y++) for (var x = 150; x < 250; x++) raw[y * w + x] = 60; // the scope, a little brighter
        raw[50 * w + 50] = 255; // a hot pixel
        var r = PierFrame.Process(raw, w, h, 1, maxWidth: 200);
        Assert.That(r.Width, Is.EqualTo(200));
        Assert.That(r.Height, Is.EqualTo(150));
        Assert.That(r.Median, Is.EqualTo(21).Within(2));
        Assert.That(r.Saturated, Is.LessThan(0.001)); // only the hot pixel
        // Stretched: the scope now stands out clearly against the pier.
        var scope = r.Pixels[75 * 200 + 100];
        var pier = r.Pixels[20 * 200 + 20];
        Assert.That(scope - pier, Is.GreaterThan(100));
        // The hot pixel is gone (its block isn't brighter than its surroundings).
        Assert.That(r.Pixels[25 * 200 + 25], Is.LessThanOrEqualTo(pier + 30));
    }

    [Test]
    public void Colour_frames_keep_their_three_channels() {
        const int w = 64, h = 48;
        var raw = new byte[w * h * 3];
        for (var i = 0; i < raw.Length; i += 3) { raw[i] = 30; raw[i + 1] = 80; raw[i + 2] = 120; }
        var r = PierFrame.Process(raw, w, h, 3, maxWidth: 640);
        Assert.That(r.Channels, Is.EqualTo(3));
        Assert.That(r.Pixels.Length, Is.EqualTo(w * h * 3));
        Assert.That(r.Pixels[2], Is.GreaterThan(r.Pixels[0])); // red stays redder than blue
    }
}
