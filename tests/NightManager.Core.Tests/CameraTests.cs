using System.Net;
using NightManager.Core.Cameras;
using NightManager.Core.Contracts;
using NightManager.Core.Settings;
using NUnit.Framework;

namespace NightManager.Core.Tests;

public class CameraTests {
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 22, 0, 0, TimeSpan.Zero);

    private static CameraSettings Camera(CameraCaptureMode mode = CameraCaptureMode.WhenWatched, int minutes = 3) =>
        new CameraSettings { Id = "cam1", Name = "Dome", Mode = mode, IntervalMinutes = minutes }.Normalized();

    [Test]
    public void Settings_never_go_below_three_minutes_or_above_100_KB() {
        var camera = new CameraSettings { IntervalMinutes = 0, MaxKilobytes = 5000, Width = 5000 }.Normalized();
        Assert.That(camera.IntervalMinutes, Is.EqualTo(3));
        Assert.That(camera.MaxKilobytes, Is.EqualTo(100));
        Assert.That(camera.Width, Is.EqualTo(1024));
        Assert.That((new CameraSettings { MaxKilobytes = 1 }).Normalized().MaxKilobytes, Is.EqualTo(10));
    }

    [Test]
    public void Installation_settings_keep_cameras_normalized() {
        var settings = new InstallationSettings { Cameras = [new CameraSettings { Id = "a", IntervalMinutes = 1 }, new CameraSettings { Id = "a" }] }.Normalized();
        Assert.That(settings.Cameras, Has.Count.EqualTo(1));
        Assert.That(settings.Cameras[0].IntervalMinutes, Is.EqualTo(3));
    }

    [Test]
    public void Watched_camera_captures_only_while_watched_and_never_faster_than_its_interval() {
        var scheduler = new CameraScheduler();
        var camera = Camera();
        Assert.That(scheduler.Due(camera, T0, watched: false), Is.Null);
        Assert.That(scheduler.Due(camera, T0, watched: true), Is.EqualTo(CaptureReason.Watch));
        scheduler.Captured(camera, CaptureReason.Watch, T0);
        Assert.That(scheduler.Due(camera, T0.AddMinutes(2.9), watched: true), Is.Null);
        Assert.That(scheduler.Due(camera, T0.AddMinutes(3), watched: true), Is.EqualTo(CaptureReason.Watch));
    }

    [Test]
    public void Scheduled_camera_captures_without_watchers() {
        var scheduler = new CameraScheduler();
        var camera = Camera(CameraCaptureMode.Scheduled, 10);
        Assert.That(scheduler.Due(camera, T0, watched: false), Is.EqualTo(CaptureReason.Schedule));
        scheduler.Captured(camera, CaptureReason.Schedule, T0);
        Assert.That(scheduler.Due(camera, T0.AddMinutes(9), watched: false), Is.Null);
        Assert.That(scheduler.Due(camera, T0.AddMinutes(10), watched: false), Is.EqualTo(CaptureReason.Schedule));
    }

    [Test]
    public void Kit_error_forces_a_capture_at_once_and_restarts_the_interval() {
        var scheduler = new CameraScheduler();
        var camera = Camera(CameraCaptureMode.Scheduled, 10);
        scheduler.Captured(camera, CaptureReason.Schedule, T0);

        // One minute later (well inside the interval) a kit error forces a picture.
        scheduler.RaiseAlert("Mount disconnected", T0.AddMinutes(1));
        Assert.That(scheduler.Due(camera, T0.AddMinutes(1), watched: false), Is.EqualTo(CaptureReason.Alert));
        scheduler.Captured(camera, CaptureReason.Alert, T0.AddMinutes(1));
        scheduler.SettleAlert([camera]);

        // The interval now counts from the alert picture, not the earlier one.
        Assert.That(scheduler.Due(camera, T0.AddMinutes(10), watched: false), Is.Null);
        Assert.That(scheduler.Due(camera, T0.AddMinutes(11), watched: false), Is.EqualTo(CaptureReason.Schedule));
        Assert.That(scheduler.PendingAlertCause, Is.Null);
    }

    [Test]
    public void All_sky_camera_skips_kit_errors_but_answers_a_safety_change() {
        var scheduler = new CameraScheduler();
        var roof = Camera(CameraCaptureMode.Scheduled, 10);
        var sky = Camera(CameraCaptureMode.Scheduled, 10) with { Id = "allsky1", AllSky = true };
        scheduler.Captured(roof, CaptureReason.Schedule, T0);
        scheduler.Captured(sky, CaptureReason.Schedule, T0);

        // A failed download: the roof camera is forced, the all-sky camera isn't, and the alert still settles.
        scheduler.RaiseAlert("Download timed out", T0.AddMinutes(1));
        Assert.That(scheduler.Due(sky, T0.AddMinutes(1), watched: false), Is.Null);
        Assert.That(scheduler.Due(roof, T0.AddMinutes(1), watched: false), Is.EqualTo(CaptureReason.Alert));
        scheduler.Captured(roof, CaptureReason.Alert, T0.AddMinutes(1));
        scheduler.SettleAlert([roof, sky]);
        Assert.That(scheduler.PendingAlertCause, Is.Null);

        // Turning unsafe: both are forced.
        scheduler.RaiseAlert("Unsafe", T0.AddMinutes(5), safety: true);
        Assert.That(scheduler.Due(sky, T0.AddMinutes(5), watched: false), Is.EqualTo(CaptureReason.Alert));
        Assert.That(scheduler.Due(roof, T0.AddMinutes(5), watched: false), Is.EqualTo(CaptureReason.Alert));
    }

    [Test]
    public void Alert_also_fires_for_a_watched_camera_nobody_is_watching() {
        var scheduler = new CameraScheduler();
        var camera = Camera();
        scheduler.RaiseAlert("Camera cooling lost", T0);
        Assert.That(scheduler.Due(camera, T0, watched: false), Is.EqualTo(CaptureReason.Alert));
    }

    [Test]
    public void A_burst_of_kit_errors_gives_one_picture() {
        var scheduler = new CameraScheduler();
        var camera = Camera();
        scheduler.RaiseAlert("Guider lost", T0);
        scheduler.Captured(camera, CaptureReason.Alert, T0);
        scheduler.RaiseAlert("Sequence failed", T0.AddSeconds(20));
        Assert.That(scheduler.Due(camera, T0.AddSeconds(20), watched: false), Is.Null);
        // A new error after the burst window does force another.
        scheduler.RaiseAlert("Mount disconnected", T0.AddMinutes(2));
        Assert.That(scheduler.Due(camera, T0.AddMinutes(2), watched: false), Is.EqualTo(CaptureReason.Alert));
    }

    [Test]
    public void Failing_camera_waits_a_minute_before_retrying() {
        var scheduler = new CameraScheduler();
        var camera = Camera(CameraCaptureMode.Scheduled);
        scheduler.Failed(camera, CaptureReason.Schedule, T0);
        Assert.That(scheduler.Due(camera, T0.AddSeconds(30), watched: false), Is.Null);
        Assert.That(scheduler.Due(camera, T0.AddMinutes(1), watched: false), Is.EqualTo(CaptureReason.Schedule));
    }

    [Test]
    public void Disabled_camera_never_captures_even_on_alerts() {
        var scheduler = new CameraScheduler();
        var camera = Camera() with { Enabled = false };
        scheduler.RaiseAlert("Mount disconnected", T0);
        Assert.That(scheduler.Due(camera, T0, watched: true), Is.Null);
    }

    [TestCase(EventKinds.DeviceDisconnected, Severity.Warning, null, true)]
    [TestCase(EventKinds.DeviceDisconnected, Severity.Info, null, false)]
    [TestCase(EventKinds.SequenceFailureBurst, Severity.Warning, null, true)]
    [TestCase(EventKinds.CoolingLost, Severity.Warning, "open", true)]
    [TestCase(EventKinds.CoolingLost, Severity.Info, "resolve", false)]
    [TestCase(EventKinds.ImageSaveFailed, Severity.Warning, null, true)]
    [TestCase(EventKinds.Clouds, Severity.Warning, "open", false)]
    [TestCase(EventKinds.UserAlarm, Severity.Critical, null, false)]
    [TestCase(EventKinds.CameraSnapshot, Severity.Critical, null, false)]
    [TestCase("anything.else", Severity.Critical, null, true)]
    public void Kit_errors_are_recognised(string kind, string severity, string? phase, bool expected) {
        var e = new EventItem { Id = "e", Timestamp = T0, Kind = kind, Severity = severity, Title = "t", IncidentPhase = phase };
        Assert.That(CameraScheduler.IsKitError(e), Is.EqualTo(expected));
    }

    [Test]
    public void Lock_needs_the_same_secret_and_protects_the_settings() {
        var camera = CameraLock.Lock(Camera(), CameraLock.Secret("admin", "s3cret"));
        Assert.That(camera.Locked, Is.True);
        Assert.That(CameraLock.Matches(camera, CameraLock.Secret("admin", "s3cret")), Is.True);
        Assert.That(CameraLock.Matches(camera, CameraLock.Secret("admin", "guess")), Is.False);
        Assert.That(camera.LockHash, Does.Not.Contain("s3cret"));

        Assert.That(CameraLock.ChangesProtectedFields(camera, camera with { Name = "Roof cam" }), Is.False);
        Assert.That(CameraLock.ChangesProtectedFields(camera, camera with { IntervalMinutes = 4 }), Is.True);
        Assert.That(CameraLock.ChangesProtectedFields(camera, camera with { MaxKilobytes = 90 }), Is.True);
        Assert.That(CameraLock.ChangesProtectedFields(camera, camera with { Enabled = false }), Is.True);
        Assert.That(CameraLock.Unlock(camera).Locked, Is.False);
    }

    [Test]
    public void Lock_survives_a_save_and_load() {
        var locked = CameraLock.Lock(Camera(), "passcode");
        var reloaded = (new InstallationSettings { Cameras = [locked] }).Normalized().Cameras[0];
        Assert.That(reloaded.Locked, Is.True);
        Assert.That(CameraLock.Matches(reloaded, "passcode"), Is.True);
    }

    [Test]
    public void Folder_gives_the_newest_recent_picture() {
        var dir = Directory.CreateTempSubdirectory("nm-cam-");
        try {
            var old = Path.Combine(dir.FullName, "a.jpg");
            var fresh = Path.Combine(dir.FullName, "b.jpg");
            File.WriteAllBytes(old, [1, 2, 3]);
            File.WriteAllBytes(fresh, [4, 5, 6, 7]);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddMinutes(-1));
            var result = CameraGrabber.NewestInFolder(dir.FullName, DateTimeOffset.UtcNow);
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Image, Is.EqualTo(new byte[] { 4, 5, 6, 7 }));

            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
            Assert.That(CameraGrabber.NewestInFolder(dir.FullName, DateTimeOffset.UtcNow).IsSuccess, Is.False);
        } finally {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public void Folder_follows_date_subfolders_to_the_newest_picture() {
        var dir = Directory.CreateTempSubdirectory("nm-cam-");
        try {
            // Remote-site style: one folder per day; and year\month\day under a second camera's folder.
            var yesterday = Directory.CreateDirectory(Path.Combine(dir.FullName, "2026-10-14"));
            var today = Directory.CreateDirectory(Path.Combine(dir.FullName, "2026-10-15"));
            var old = Path.Combine(yesterday.FullName, "night.jpg");
            var fresh = Path.Combine(today.FullName, "dusk.jpg");
            File.WriteAllBytes(old, [1]);
            File.WriteAllBytes(fresh, [2, 2]);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-20));
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddMinutes(-2));
            Directory.SetLastWriteTimeUtc(yesterday.FullName, DateTime.UtcNow.AddHours(-20));
            Directory.SetLastWriteTimeUtc(today.FullName, DateTime.UtcNow.AddMinutes(-2));
            Assert.That(CameraGrabber.NewestInFolder(dir.FullName, DateTimeOffset.UtcNow).Image, Is.EqualTo(new byte[] { 2, 2 }));

            // A picture straight in the top folder that's newer still wins (e.g. a "latest.jpg" next to the archive).
            var latest = Path.Combine(dir.FullName, "latest.jpg");
            File.WriteAllBytes(latest, [3, 3, 3]);
            File.SetLastWriteTimeUtc(latest, DateTime.UtcNow.AddSeconds(-10));
            Assert.That(CameraGrabber.NewestInFolder(dir.FullName, DateTimeOffset.UtcNow).Image, Is.EqualTo(new byte[] { 3, 3, 3 }));

            var nested = Directory.CreateDirectory(Path.Combine(dir.FullName, "cam2", "2026", "10", "15"));
            var deep = Path.Combine(nested.FullName, "frame.png");
            File.WriteAllBytes(deep, [4, 4, 4, 4]);
            Assert.That(CameraGrabber.NewestInFolder(Path.Combine(dir.FullName, "cam2"), DateTimeOffset.UtcNow).Image, Is.EqualTo(new byte[] { 4, 4, 4, 4 }));
        } finally {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Snapshot_address_returns_the_picture_or_a_clear_error() {
        var camera = Camera() with { Kind = CameraSourceKind.Snapshot, Address = "http://192.168.1.20/snap.jpg", UserName = "admin" };
        var ok = new CameraGrabber(() => new FakeHandler(HttpStatusCode.OK, "image/jpeg", [9, 9, 9]));
        var result = await ok.GrabAsync(camera, "pw", T0, CancellationToken.None);
        Assert.That(result.Image, Is.EqualTo(new byte[] { 9, 9, 9 }));

        var refused = new CameraGrabber(() => new FakeHandler(HttpStatusCode.Unauthorized, "text/html", []));
        var denied = await refused.GrabAsync(camera, "wrong", T0, CancellationToken.None);
        Assert.That(denied.IsSuccess, Is.False);
        Assert.That(denied.Error, Does.Contain("refused"));

        var html = new CameraGrabber(() => new FakeHandler(HttpStatusCode.OK, "text/html", [1]));
        Assert.That((await html.GrabAsync(camera, "pw", T0, CancellationToken.None)).Error, Does.Contain("not a picture"));
    }

    [Test]
    public async Task Rtsp_goes_to_the_hosts_decoder_with_the_login_kept_out_of_the_address() {
        var camera = Camera() with { Kind = CameraSourceKind.Rtsp, Address = "rtsp://192.168.1.20:554/stream1", UserName = "admin", Width = 800 };
        (string Address, string User, string? Password, int Width)? seen = null;
        var grabber = new CameraGrabber(rtsp: (address, user, password, width, _) => {
            seen = (address, user, password, width);
            return Task.FromResult(GrabResult.Ok([1, 2]));
        });
        var result = await grabber.GrabAsync(camera, "pw", T0, CancellationToken.None);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(seen, Is.EqualTo(("rtsp://192.168.1.20:554/stream1", "admin", (string?)"pw", 800)));

        var embedded = camera with { Address = "rtsp://admin:pw@192.168.1.20/stream1" };
        Assert.That((await grabber.GrabAsync(embedded, "pw", T0, CancellationToken.None)).Error, Does.Contain("username and password boxes"));
        Assert.That((await new CameraGrabber().GrabAsync(camera, "pw", T0, CancellationToken.None)).Error, Does.Contain("RTSP"));
        Assert.That((await grabber.GrabAsync(camera with { Address = "http://x/y" }, "pw", T0, CancellationToken.None)).Error, Does.Contain("rtsp://"));
    }

    [Test]
    public void Summary_never_carries_the_address_or_credentials() {
        var camera = Camera() with { Address = "http://10.0.0.5/snap?user=admin&pwd=x", UserName = "admin" };
        var json = Json.NmJson.Serialize(camera.Summary());
        Assert.That(json, Does.Not.Contain("10.0.0.5"));
        Assert.That(json, Does.Not.Contain("admin"));
    }

    private sealed class FakeHandler(HttpStatusCode status, string type, byte[] body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }
}
