using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using NightManager.Core.Contracts;
using NightManager.Core.Live;

namespace NightManager.Core.Tests;

public class SnapshotSignatureTests {
    private static ObservatorySnapshot Snap(double temp, double alt, string? filter = "Ha") => new() {
        Camera = new CameraState { Connected = true, Temperature = temp, CoolerPower = 41.3 },
        Mount = new MountState { Connected = true, Altitude = alt, Azimuth = 181.2, RightAscension = 5.5912, Declination = 22.01 },
        FilterWheel = new FilterWheelState { Connected = true, CurrentFilter = filter },
    };

    [Test]
    public void Ticking_values_do_not_change_it() {
        Assert.That(SnapshotSignature.Compute(Snap(-10.02, 41.3)), Is.EqualTo(SnapshotSignature.Compute(Snap(-9.97, 43.8))));
    }

    [Test]
    public void Changes_a_person_would_notice_do() {
        var baseline = SnapshotSignature.Compute(Snap(-10, 41));
        Assert.That(SnapshotSignature.Compute(Snap(-10, 41, "OIII")), Is.Not.EqualTo(baseline));
        Assert.That(SnapshotSignature.Compute(Snap(-5, 41)), Is.Not.EqualTo(baseline));
    }
}

public class LiveTelemetryTests {
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 21, 0, 0, TimeSpan.Zero);

    private static ObservatorySnapshot Snap(double temp) => new() {
        Camera = new CameraState { Connected = true, State = "exposing", Temperature = temp },
        Guider = new GuiderState { Connected = true, State = "guiding", RmsTotal = 0.61 },
    };

    [Test]
    public void Sends_everything_first_then_only_what_changed() {
        var telemetry = new LiveTelemetry();
        var first = telemetry.NextUpdate(Snap(-10), Now, TimeSpan.FromSeconds(10))!;
        Assert.That(first.ContainsKey("cam") && first.ContainsKey("gd") && first.ContainsKey("t"));

        Assert.That(telemetry.NextUpdate(Snap(-10), Now.AddSeconds(1), TimeSpan.FromSeconds(10)), Is.Null, "nothing new, keep-alive not due");

        var second = telemetry.NextUpdate(Snap(-9.5), Now.AddSeconds(2), TimeSpan.FromSeconds(10))!;
        Assert.That(second.ContainsKey("cam"), Is.True);
        Assert.That(second.ContainsKey("gd"), Is.False);

        var keepAlive = telemetry.NextUpdate(Snap(-9.5), Now.AddSeconds(13), TimeSpan.FromSeconds(10))!;
        Assert.That(keepAlive.Select(p => p.Key), Is.EqualTo(new[] { "t" }));
    }

    [Test]
    public void Carries_the_guide_steps_since_the_last_send() {
        var telemetry = new LiveTelemetry();
        telemetry.NextUpdate(Snap(-10), Now, TimeSpan.FromSeconds(10));
        telemetry.AddGuideStep(new LiveGuideStep(Now.AddSeconds(1), 0.31, -0.12));
        telemetry.AddGuideStep(new LiveGuideStep(Now.AddSeconds(3), -0.2, 0.05));
        var update = telemetry.NextUpdate(Snap(-10), Now.AddSeconds(4), TimeSpan.FromSeconds(10))!;
        Assert.That(update["gs"]!.AsArray(), Has.Count.EqualTo(2));
        Assert.That(update["gs"]![0]![1]!.GetValue<double>(), Is.EqualTo(0.31));
    }

    [Test]
    public void After_a_reset_everything_goes_again() {
        var telemetry = new LiveTelemetry();
        telemetry.NextUpdate(Snap(-10), Now, TimeSpan.FromSeconds(10));
        telemetry.Reset();
        Assert.That(telemetry.NextUpdate(Snap(-10), Now.AddSeconds(1), TimeSpan.FromSeconds(10))!.ContainsKey("cam"), Is.True);
    }
}

public class LiveLinkTests {
    private sealed class FakeFirebase : HttpMessageHandler {
        public readonly List<(string Method, string Url, string Body)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            var url = request.RequestUri!.ToString();
            if (url.Contains("signInWithCustomToken")) {
                return Json("""{"idToken":"id-1","refreshToken":"refresh-1","expiresIn":"3600"}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static LiveLink Link(FakeFirebase firebase, Func<int>? onSession = null) => new(_ => {
        onSession?.Invoke();
        return Task.FromResult<LiveSession?>(new LiveSession { CustomToken = "custom", DatabaseUrl = "https://db.example.firebasedatabase.app", ApiKey = "key" });
    }, handler: firebase);

    [Test]
    public void Knows_when_someone_is_watching() {
        using var link = Link(new FakeFirebase());
        var changes = new List<bool>();
        link.WatchedChanged += (_, watched) => changes.Add(watched);

        link.OnWatchersEvent("put", JsonNode.Parse("""{"path":"/","data":null}"""));
        link.OnWatchersEvent("put", JsonNode.Parse("""{"path":"/uid-1","data":{"at":1}}"""));
        link.OnWatchersEvent("put", JsonNode.Parse("""{"path":"/uid-2","data":{"at":2}}"""));
        link.OnWatchersEvent("put", JsonNode.Parse("""{"path":"/uid-1/at","data":3}"""));
        link.OnWatchersEvent("put", JsonNode.Parse("""{"path":"/uid-1","data":null}"""));
        Assert.That(link.IsWatched, Is.True);
        link.OnWatchersEvent("put", JsonNode.Parse("""{"path":"/uid-2","data":null}"""));

        Assert.That(changes, Is.EqualTo(new[] { true, false }));
        Assert.That(link.IsWatched, Is.False);
    }

    [Test]
    public void Only_new_nudges_count() {
        using var link = Link(new FakeFirebase());
        var nudges = 0;
        link.Nudged += (_, _) => nudges++;
        var old = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();
        link.OnNudgeEvent("put", JsonNode.Parse($$$"""{"path":"/","data":{"at":{{{old}}},"reason":"command"}}"""));
        Assert.That(nudges, Is.Zero, "the last nudge from hours ago isn't news");
        link.OnNudgeEvent("put", JsonNode.Parse($$$"""{"path":"/","data":{"at":{{{old + 5000}}},"reason":"command"}}"""));
        Assert.That(nudges, Is.EqualTo(1));
    }

    [Test]
    public async Task Signs_in_with_the_custom_token_and_publishes_quietly() {
        var firebase = new FakeFirebase();
        var sessions = 0;
        using var link = Link(firebase, () => ++sessions);
        typeof(LiveLink).GetProperty(nameof(LiveLink.ObservatoryId))!.SetValue(link, "obs1");
        typeof(LiveLink).GetProperty(nameof(LiveLink.RigId))!.SetValue(link, "rig1");

        Assert.That(await link.PublishAsync(new JsonObject { ["t"] = 1 }, CancellationToken.None), Is.True);
        Assert.That(await link.PublishAsync(new JsonObject { ["t"] = 2 }, CancellationToken.None), Is.True);

        Assert.That(sessions, Is.EqualTo(1), "the token is reused");
        var patches = firebase.Requests.Where(r => r.Method == "PATCH").ToList();
        Assert.That(patches, Has.Count.EqualTo(2));
        Assert.That(patches[0].Url, Does.StartWith("https://db.example.firebasedatabase.app/live/obs1/rig1.json?auth=id-1&print=silent"));
    }
}
