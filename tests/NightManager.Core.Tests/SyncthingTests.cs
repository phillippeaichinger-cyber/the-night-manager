using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Settings;
using NightManager.Core.Syncthing;

namespace NightManager.Core.Tests;

public class SyncScheduleTests {
    private static readonly SyncWindow AfterMidnight = new() { Enabled = true, Start = "00:00", End = "08:00" };
    private static readonly SyncWindow Overnight = new() { Enabled = true, Start = "22:00", End = "06:00" };

    [TestCase("2026-10-12T23:59", false)]
    [TestCase("2026-10-13T00:00", true)]
    [TestCase("2026-10-13T07:59", true)]
    [TestCase("2026-10-13T08:00", false)]
    public void After_midnight_window(string at, bool open) =>
        Assert.That(SyncSchedule.InWindow(AfterMidnight, DateTime.Parse(at)), Is.EqualTo(open));

    [TestCase("2026-10-12T21:59", false)]
    [TestCase("2026-10-12T22:00", true)]
    [TestCase("2026-10-13T05:59", true)]
    [TestCase("2026-10-13T06:00", false)]
    public void Window_across_midnight(string at, bool open) =>
        Assert.That(SyncSchedule.InWindow(Overnight, DateTime.Parse(at)), Is.EqualTo(open));

    [Test]
    public void Next_change_is_the_opening_when_closed_and_the_closing_when_open() {
        Assert.That(SyncSchedule.NextChange(AfterMidnight, DateTime.Parse("2026-10-12T21:30")), Is.EqualTo(DateTime.Parse("2026-10-13T00:00")));
        Assert.That(SyncSchedule.NextChange(AfterMidnight, DateTime.Parse("2026-10-13T03:00")), Is.EqualTo(DateTime.Parse("2026-10-13T08:00")));
    }
}

public class SyncthingMonitorTests {
    private sealed class FakeSyncthing : ISyncthingApi {
        public List<StDevice> Devices = [
            new("MYSELF-ID", "Observatory PC", false),
            new("OBSPC1-XXXX", "Home PC", false),
            new("LAPTOP2-XXXX", "Friend's Mac Studio", false),
        ];
        public Dictionary<string, StConnection> Connections = new() {
            ["OBSPC1-XXXX"] = new(true, false, 0, 0),
            ["LAPTOP2-XXXX"] = new(true, false, 0, 0),
        };
        public long OutTotal;
        public Dictionary<string, long> Need = new() { ["OBSPC1-XXXX"] = 23L * 1024 * 1024 * 1024, ["LAPTOP2-XXXX"] = 0 };
        public int MaxSendKbps;
        public List<string> Calls = [];

        public Task<string?> VersionAsync(CancellationToken token) => Task.FromResult<string?>("v2.1.6");
        public Task<string> MyIdAsync(CancellationToken token) => Task.FromResult("MYSELF-ID");
        public Task<IReadOnlyList<StDevice>> DevicesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<StDevice>>(Devices.ToList());
        public Task<IReadOnlyList<StFolder>> FoldersAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StFolder>>([new StFolder("lights", "Imaging_Folder", false, ["OBSPC1-XXXX"])]);
        public Task<StConnections> ConnectionsAsync(CancellationToken token) => Task.FromResult(new StConnections(Connections, 0, OutTotal));
        public Task<StCompletion> CompletionAsync(string deviceId, CancellationToken token) =>
            Task.FromResult(new StCompletion(Need[deviceId] == 0 ? 100 : 70, Need[deviceId], Need[deviceId] == 0 ? 0 : 400));
        public Task<StFolderStatus> FolderStatusAsync(string folderId, CancellationToken token) => Task.FromResult(new StFolderStatus("idle", 222L << 30, 0, 0));
        public Task<int> MaxSendKbpsAsync(CancellationToken token) => Task.FromResult(MaxSendKbps);

        public Task SetDevicePausedAsync(string deviceId, bool paused, CancellationToken token) {
            Calls.Add($"{(paused ? "pause" : "resume")} {deviceId}");
            var i = Devices.FindIndex(d => d.DeviceId == deviceId);
            Devices[i] = Devices[i] with { Paused = paused };
            return Task.CompletedTask;
        }

        public Task SetMaxSendKbpsAsync(int kbps, CancellationToken token) {
            Calls.Add($"limit {kbps}");
            MaxSendKbps = kbps;
            return Task.CompletedTask;
        }
    }

    private static (SyncthingMonitor Monitor, FakeSyncthing Api, FakeTimeProvider Time, Func<SyncthingSettings> Settings) Make(
        SyncWindow window, string localStart, ISyncthingApi? apiOverride = null) {
        var api = new FakeSyncthing();
        var time = new FakeTimeProvider(DateTimeOffset.Parse(localStart + "Z"));
        var settings = new SyncthingSettings { Enabled = true, Window = window };
        var path = Path.Combine(Path.GetTempPath(), "nm-st-" + Guid.NewGuid().ToString("N") + ".json");
        var monitor = new SyncthingMonitor(() => apiOverride ?? api, () => settings, s => settings = s, path, time, zone: () => TimeZoneInfo.Utc);
        return (monitor, api, time, () => settings);
    }

    [Test]
    public async Task Outside_the_window_it_pauses_the_other_devices_and_resumes_only_those_when_it_opens() {
        var (monitor, api, time, _) = Make(new SyncWindow { Enabled = true, Start = "00:00", End = "08:00" }, "2026-10-12T21:00");
        // You paused the friend's Mac yourself: Night Manager must not resume it later.
        await api.SetDevicePausedAsync("LAPTOP2-XXXX", true, CancellationToken.None);
        api.Calls.Clear();

        var closed = await monitor.PollAsync(CancellationToken.None);
        Assert.That(api.Calls, Is.EqualTo(new[] { "pause OBSPC1-XXXX" }));
        Assert.That(closed.Single().Title, Is.EqualTo("Syncthing paused"));
        Assert.That(closed.Single().Message, Does.StartWith("Uploads resume at 00:00"));
        Assert.That(monitor.State!.Status, Is.EqualTo(SyncthingStatus.Paused));
        Assert.That(monitor.State.BySchedule, Is.True);

        api.Calls.Clear();
        time.SetUtcNow(DateTimeOffset.Parse("2026-10-13T00:00:30Z"));
        var opened = await monitor.PollAsync(CancellationToken.None);
        Assert.That(api.Calls, Is.EqualTo(new[] { "resume OBSPC1-XXXX" }), "your own pause of the friend's Mac is left alone");
        Assert.That(opened.Single().Title, Is.EqualTo("Syncthing uploading"));
    }

    [Test]
    public async Task Limit_mode_caps_the_upload_and_restores_the_old_limit() {
        var (monitor, api, time, _) = Make(new SyncWindow { Enabled = true, Start = "00:00", End = "08:00", Outside = "limit", LimitKbps = 500 }, "2026-10-12T12:00");
        await monitor.PollAsync(CancellationToken.None);
        Assert.That(api.MaxSendKbps, Is.EqualTo(500));
        Assert.That(monitor.State!.Status, Is.EqualTo(SyncthingStatus.Limited));

        time.SetUtcNow(DateTimeOffset.Parse("2026-10-13T01:00Z"));
        await monitor.PollAsync(CancellationToken.None);
        Assert.That(api.MaxSendKbps, Is.EqualTo(0), "back to unlimited, as it was");
    }

    [Test]
    public async Task Sync_now_overrides_the_window_until_it_expires() {
        var (monitor, api, time, _) = Make(new SyncWindow { Enabled = true, Start = "00:00", End = "08:00" }, "2026-10-12T14:00");
        await monitor.PollAsync(CancellationToken.None);
        api.Calls.Clear();

        var message = await monitor.HandleAsync(Command(CommandTypes.SyncNow, ("hours", "2")), CancellationToken.None);
        Assert.That(message, Does.StartWith("Syncing now for 2 h"));
        Assert.That(api.Calls, Is.EqualTo(new[] { "resume OBSPC1-XXXX", "resume LAPTOP2-XXXX" }));

        api.Calls.Clear();
        time.SetUtcNow(DateTimeOffset.Parse("2026-10-12T16:01Z"));
        await monitor.PollAsync(CancellationToken.None);
        Assert.That(api.Calls, Is.EqualTo(new[] { "pause OBSPC1-XXXX", "pause LAPTOP2-XXXX" }), "override over: back to the window");
    }

    [Test]
    public async Task Pause_from_the_app_holds_until_the_window_next_opens() {
        var (monitor, api, time, _) = Make(new SyncWindow { Enabled = true, Start = "00:00", End = "08:00" }, "2026-10-13T02:00");
        await monitor.PollAsync(CancellationToken.None);
        await monitor.HandleAsync(Command(CommandTypes.SyncPause), CancellationToken.None);
        Assert.That(monitor.State!.Status, Is.EqualTo(SyncthingStatus.Paused));
        Assert.That(monitor.State.BySchedule, Is.False);
        Assert.That(monitor.State.PausedUntil, Is.EqualTo(DateTimeOffset.Parse("2026-10-14T00:00Z")));

        await monitor.HandleAsync(Command(CommandTypes.SyncResume), CancellationToken.None);
        Assert.That(api.Devices.Single(d => d.Name == "Home PC").Paused, Is.False);
    }

    [Test]
    public async Task Pause_from_the_app_works_even_when_the_window_was_already_held_with_nothing_to_pause() {
        // A remote rig, 23 Oct: an old script had paused everything, so the window "held" with nothing paused here; then the
        // device was resumed. Pause from the app must still pause it (it used to do nothing).
        var (monitor, api, _, _) = Make(new SyncWindow { Enabled = true, Start = "00:00", End = "08:00" }, "2026-10-13T12:00");
        await api.SetDevicePausedAsync("OBSPC1-XXXX", true, CancellationToken.None);
        await api.SetDevicePausedAsync("LAPTOP2-XXXX", true, CancellationToken.None);
        await monitor.PollAsync(CancellationToken.None);
        await api.SetDevicePausedAsync("OBSPC1-XXXX", false, CancellationToken.None);
        api.Calls.Clear();

        await monitor.HandleAsync(Command(CommandTypes.SyncPause), CancellationToken.None);
        Assert.That(api.Calls, Does.Contain("pause OBSPC1-XXXX"));
        Assert.That(monitor.State!.Status, Is.EqualTo(SyncthingStatus.Paused));
    }

    [Test]
    public async Task Window_changes_from_the_app_are_saved() {
        var (monitor, _, _, settings) = Make(new SyncWindow(), "2026-10-12T14:00");
        var message = await monitor.HandleAsync(Command(CommandTypes.SyncSchedule, ("enabled", "true"), ("start", "1:00"), ("end", "07:30"), ("outside", "limit"), ("limitKbps", "2000")), CancellationToken.None);
        Assert.That(message, Is.EqualTo("Upload window set to 01:00–07:30, 2000 kbit/s outside it."));
        Assert.That(settings().Window, Is.EqualTo(new SyncWindow { Enabled = true, Start = "01:00", End = "07:30", Outside = "limit", LimitKbps = 2000 }));
    }

    [Test]
    public async Task Reports_upload_left_rate_and_devices() {
        var (monitor, api, time, _) = Make(new SyncWindow(), "2026-10-12T14:00");
        await monitor.PollAsync(CancellationToken.None);
        api.OutTotal = 30L * 1024 * 1024;
        time.Advance(TimeSpan.FromSeconds(30));
        await monitor.PollAsync(CancellationToken.None);
        var s = monitor.State!;
        Assert.Multiple(() => {
            Assert.That(s.Status, Is.EqualTo(SyncthingStatus.Syncing));
            Assert.That(s.UploadBytesPerSecond, Is.EqualTo(1024 * 1024).Within(1));
            Assert.That(s.ToUploadBytes, Is.EqualTo(23L * 1024 * 1024 * 1024));
            Assert.That(s.Devices.Select(d => d.Name), Is.EquivalentTo(new[] { "Home PC", "Friend's Mac Studio" }));
            Assert.That(s.Folders.Single().Label, Is.EqualTo("Imaging_Folder"));
        });
    }

    [Test]
    public async Task Says_when_uploads_finish_and_when_syncthing_goes_quiet() {
        var (monitor, api, time, _) = Make(new SyncWindow(), "2026-10-12T14:00");
        await monitor.PollAsync(CancellationToken.None);
        api.Need["OBSPC1-XXXX"] = 0;
        time.Advance(TimeSpan.FromMinutes(1));
        var done = await monitor.PollAsync(CancellationToken.None);
        Assert.That(done.Single().Title, Is.EqualTo("Uploads finished"));

        var (down, _, downTime, _) = Make(new SyncWindow(), "2026-10-12T14:00", new BrokenSyncthing());
        Assert.That(await down.PollAsync(CancellationToken.None), Is.Empty, "a blip isn't an alarm");
        Assert.That(down.State!.Status, Is.EqualTo(SyncthingStatus.Unreachable));
        downTime.Advance(TimeSpan.FromMinutes(6));
        Assert.That((await down.PollAsync(CancellationToken.None)).Single().Title, Is.EqualTo("Syncthing isn't responding"));
    }

    [Test]
    public async Task The_app_cant_control_it_when_the_owner_turned_that_off() {
        var api = new FakeSyncthing();
        var path = Path.Combine(Path.GetTempPath(), "nm-st-" + Guid.NewGuid().ToString("N") + ".json");
        var monitor = new SyncthingMonitor(() => api, () => new SyncthingSettings { Enabled = true, AllowRemoteControl = false }, _ => { }, path);
        Assert.That(async () => await monitor.HandleAsync(Command(CommandTypes.SyncPause), CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("turned off in NINA"));
    }

    private sealed class BrokenSyncthing : ISyncthingApi {
        private static Task<T> Fail<T>() => Task.FromException<T>(new HttpRequestException("Connection refused (127.0.0.1:8384)"));
        public Task<string?> VersionAsync(CancellationToken token) => Fail<string?>();
        public Task<string> MyIdAsync(CancellationToken token) => Fail<string>();
        public Task<IReadOnlyList<StDevice>> DevicesAsync(CancellationToken token) => Fail<IReadOnlyList<StDevice>>();
        public Task<IReadOnlyList<StFolder>> FoldersAsync(CancellationToken token) => Fail<IReadOnlyList<StFolder>>();
        public Task<StConnections> ConnectionsAsync(CancellationToken token) => Fail<StConnections>();
        public Task<StCompletion> CompletionAsync(string deviceId, CancellationToken token) => Fail<StCompletion>();
        public Task<StFolderStatus> FolderStatusAsync(string folderId, CancellationToken token) => Fail<StFolderStatus>();
        public Task<int> MaxSendKbpsAsync(CancellationToken token) => Fail<int>();
        public Task SetDevicePausedAsync(string deviceId, bool paused, CancellationToken token) => Fail<bool>();
        public Task SetMaxSendKbpsAsync(int kbps, CancellationToken token) => Fail<bool>();
    }

    private static RemoteCommand Command(string type, params (string Key, string Value)[] args) => new() {
        Id = Guid.NewGuid().ToString("N"), Type = type, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        Args = args.ToDictionary(a => a.Key, a => a.Value),
    };
}

public class SyncthingClientTests {
    private sealed class Canned(Dictionary<string, string> responses) : HttpMessageHandler {
        public List<(string Method, string Path, string? Body, string? Key)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method.Method, path, request.Content is null ? null : await request.Content.ReadAsStringAsync(token),
                request.Headers.TryGetValues("X-API-Key", out var k) ? k.Single() : null));
            return responses.TryGetValue(path, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    [Test]
    public async Task Reads_real_shaped_responses_and_patches_with_the_key() {
        var handler = new Canned(new() {
            ["/rest/system/connections"] = """{"connections":{"AAA":{"connected":true,"paused":false,"inBytesTotal":5,"outBytesTotal":7}},"total":{"inBytesTotal":8032516567,"outBytesTotal":53817504450}}""",
            ["/rest/db/completion?device=AAA"] = """{"completion":69.71,"globalBytes":1,"needBytes":516086848318,"needItems":21646,"remoteState":"valid"}""",
            ["/rest/config/devices"] = """[{"deviceID":"AAA","name":"Home PC","paused":false,"maxSendKbps":0}]""",
        });
        using var client = new SyncthingClient("http://127.0.0.1:8384", "secret-key", handler);

        var connections = await client.ConnectionsAsync(CancellationToken.None);
        var completion = await client.CompletionAsync("AAA", CancellationToken.None);
        var devices = await client.DevicesAsync(CancellationToken.None);
        await client.SetDevicePausedAsync("AAA", true, CancellationToken.None);

        Assert.Multiple(() => {
            Assert.That(connections.OutBytesTotal, Is.EqualTo(53817504450));
            Assert.That(connections.Devices["AAA"].Connected, Is.True);
            Assert.That(completion.NeedBytes, Is.EqualTo(516086848318));
            Assert.That(devices.Single().Name, Is.EqualTo("Home PC"));
            Assert.That(handler.Requests.Last(), Is.EqualTo(("PATCH", "/rest/config/devices/AAA", "{\"paused\":true}", "secret-key")));
        });
    }
}

public class StorageWatchTests {
    private static StorageState Drive(double freeGb, double totalGb = 2000, double sessionGb = 0) => new() {
        Path = @"E:\Astro", FreeBytes = (long)(freeGb * StorageWatch.Gb), TotalBytes = (long)(totalGb * StorageWatch.Gb), SessionBytes = (long)(sessionGb * StorageWatch.Gb),
    };

    [Test]
    public void Warns_then_escalates_then_resolves() {
        var watch = new StorageWatch();
        var now = DateTimeOffset.UtcNow;
        Assert.That(watch.Observe(Drive(500), now), Is.Empty);
        Assert.That(watch.Observe(Drive(45), now).Single().Severity, Is.EqualTo(Severity.Warning));
        Assert.That(watch.Observe(Drive(44), now), Is.Empty, "no repeat while nothing changes");
        Assert.That(watch.Observe(Drive(8), now).Single().Title, Is.EqualTo("Image drive almost full"));
        Assert.That(watch.Observe(Drive(600), now).Single().IncidentPhase, Is.EqualTo("resolve"));
    }

    [Test]
    public void Tonights_rate_filling_the_drive_before_dawn_is_critical() {
        var watch = new StorageWatch();
        // 60 GB free, 20 GB saved in the first 2 h, 8 h of night left: needs ~80 GB more.
        var e = watch.Observe(Drive(60, sessionGb: 20), DateTimeOffset.UtcNow, nightLeft: TimeSpan.FromHours(8), sessionElapsed: TimeSpan.FromHours(2)).Single();
        Assert.That(e.Severity, Is.EqualTo(Severity.Critical));
        Assert.That(e.Message, Does.Contain("fills before dawn"));
    }
}

public class SystemDriveWatchTests {
    [Test]
    public void A_nearly_full_system_drive_alerts_once_and_resolves() {
        var watch = new StorageWatch();
        var now = DateTimeOffset.UtcNow;
        DriveSpace C(double freeGb) => new() { Name = "C:", System = true, FreeBytes = (long)(freeGb * StorageWatch.Gb), TotalBytes = 930 * StorageWatch.Gb };
        Assert.That(watch.ObserveSystem(C(365), now), Is.Empty);
        Assert.That(watch.ObserveSystem(C(12), now).Single().Severity, Is.EqualTo(Severity.Warning));
        Assert.That(watch.ObserveSystem(C(11), now), Is.Empty);
        Assert.That(watch.ObserveSystem(C(3), now).Single().Title, Is.EqualTo("System drive C: almost full"));
        Assert.That(watch.ObserveSystem(C(100), now).Single().IncidentPhase, Is.EqualTo("resolve"));
        Assert.That(watch.ObserveSystem(C(3) with { Images = true }, now), Is.Empty, "the image-drive alert covers that");
    }
}
