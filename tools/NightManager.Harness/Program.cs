using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightManager.Core.Commands;
using NightManager.Core.Contracts;
using NightManager.Core.Outbox;
using NightManager.Core.Pairing;
using NightManager.Core.Security;
using NightManager.Core.Sync;

// Usage: NightManager.Harness [apiBase] (default: Firebase emulator)
var project = "demo-nightmanager";
var apiBase = args.Length > 0 ? args[0] : $"http://127.0.0.1:5001/{project}/europe-west1/api";
var authBase = "http://127.0.0.1:9099";
var firestoreBase = $"http://127.0.0.1:8080/v1/projects/{project}/databases/(default)/documents";
using var http = new HttpClient();
var failures = 0;

void Check(bool condition, string what) {
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {what}");
    if (!condition) {
        failures++;
    }
}

// --- The "app": a signed-in user (Auth emulator)
var signUp = await http.PostAsJsonAsync($"{authBase}/identitytoolkit.googleapis.com/v1/accounts:signUp?key=fake", new { email = $"harness{Guid.NewGuid():N}@example.com", password = "correct-horse-battery", returnSecureToken = true });
var account = (await signUp.Content.ReadFromJsonAsync<JsonObject>())!;
var idToken = account["idToken"]!.GetValue<string>();

// Twin-scope setups need a paid plan (free = 1 rig); set it as the RevenueCat webhook would.
using (var grant = new HttpRequestMessage(HttpMethod.Patch, $"{firestoreBase}/users/{account["localId"]!.GetValue<string>()}") {
    Content = JsonContent.Create(new { fields = new { plan = new { stringValue = "pro" }, planExpiresAt = new { timestampValue = DateTime.UtcNow.AddDays(1).ToString("O") } } })
}) {
    grant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
    (await http.SendAsync(grant)).EnsureSuccessStatusCode();
}

// --- Pairing, exactly as the plugin does it; the "app" claims the code when it appears
using var transport = new HttpApiTransport(new Uri(apiBase), "NightManager-Harness/1.0");
var claimTask = Task.CompletedTask;
var progress = new SynchronousProgress<PairingProgress>(p => {
    Console.WriteLine($"      pairing: {p.State} {p.UserCode} {p.Message}");
    if (p.State == PairingState.WaitingForApproval && p.UserCode is { } code) {
        claimTask = AppPost($"/v2/pair/claim", new { userCode = code, observatoryName = "Harness observatory" });
    }
});
var paired = await new PairingClient(transport).PairAsync(new PairStartRequest { InstallationId = Guid.NewGuid().ToString("N"), SuggestedName = "HARNESS-PC", PluginVersion = "2.0.0.0", NinaVersion = "3.2.0.9001" }, progress, CancellationToken.None);
await claimTask;
Check(paired is not null && ApiCredential.TryParse(paired.ApiKey, out _), "pairing returned a credential the plugin accepts");
ApiCredential.TryParse(paired!.ApiKey, out var credential);
var obsId = paired.ObservatoryId!;

// --- Sync engine with a real on-disk outbox; two rigs: main A + linked B (twin scopes)
var root = Path.Combine(Path.GetTempPath(), "nm-harness-" + Guid.NewGuid().ToString("N"));
SyncEngine Engine(string rig, string role) => new(
    new DurableOutbox(Path.Combine(root, rig)),
    transport,
    () => credential,
    () => new ClientInfo { RigId = rig, RigName = $"RedCat {rig}", SetupId = "A", SetupName = "Twin RedCat", RigRole = role, PluginVersion = "2.0.0.0", NinaVersion = "3.2.0.9001", AllowedCommands = role == "main" ? ["ping", "abortSequence", "parkMount", "safeShutdown"] : ["ping", "abortSequence", "safeShutdown"] },
    log: new ConsoleLog());
var main = Engine("A", "main");
var linked = Engine("B", "linked");

var now = DateTimeOffset.UtcNow;
var frameId = Guid.NewGuid().ToString("N");
main.UpdateSnapshot(new ObservatorySnapshot { Guider = new GuiderState { Connected = true, State = "guiding", RmsTotal = 0.64 }, Sequence = new SequenceState { Running = true, TargetName = "Sh2 132" } });
main.Enqueue(new EventItem { Id = Guid.NewGuid().ToString("N"), Timestamp = now, Kind = EventKinds.Clouds, Severity = Severity.Warning, Title = "Possible clouds", IncidentKey = "clouds", IncidentPhase = "open" });
main.Enqueue(new FrameItem { Id = frameId, Timestamp = now, FileName = "Sh2_132_O_0001.fits", ImageType = "LIGHT", Target = "Sh2 132", Filter = "O", ExposureSeconds = 300, Hfr = 1.5, Stars = 3500, Grade = new FrameGrade { Score = 97, Verdict = Verdicts.Accept }, Thumbnail = new ThumbnailInfo { Width = 2, Height = 2, Bytes = 4 } }, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
Check(await main.FlushOnceAsync(CancellationToken.None) == FlushOutcome.Sent, "main rig flush (snapshot, incident, frame + thumbnail)");
Check(main.Status.ObservatoryName == "Harness observatory", "backend returned the observatory name");
Check(await linked.FlushOnceAsync(CancellationToken.None) is FlushOutcome.Sent or FlushOutcome.NothingToSend, "linked rig flush");
linked.UpdateSnapshot(new ObservatorySnapshot());
Check(await linked.FlushOnceAsync(CancellationToken.None) == FlushOutcome.Sent, "linked rig snapshot");

var frame = await FirestoreGet($"observatories/{obsId}/frames/{frameId}");
Check(frame?["fields"]?["grade"] is not null, "frame stored with grade");
var thumbPath = frame?["fields"]?["thumbnailPath"]?["stringValue"]?.GetValue<string>();
var buckets = JsonNode.Parse(await http.GetStringAsync("http://127.0.0.1:9199/b?project=" + project));
var bucketName = buckets?["items"]?.AsArray().Select(b => b?["id"]?.GetValue<string>() ?? b?["name"]?.GetValue<string>()).FirstOrDefault(b => b is not null) ?? $"{project}.appspot.com";
// Unauthenticated reads must be refused (thumbnails are members-only); the emulator's admin token bypasses rules.
var anonymous = await http.GetAsync($"http://127.0.0.1:9199/v0/b/{bucketName}/o/{Uri.EscapeDataString(thumbPath ?? "x")}?alt=media");
Check(anonymous.StatusCode == System.Net.HttpStatusCode.Forbidden, "thumbnail is not publicly readable");
using var adminRead = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:9199/v0/b/{bucketName}/o/{Uri.EscapeDataString(thumbPath ?? "x")}?alt=media");
adminRead.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
var thumb = await http.SendAsync(adminRead);
Console.WriteLine($"      thumbnail GET {bucketName}/{thumbPath} -> {(int)thumb.StatusCode}");
Check(thumb.IsSuccessStatusCode && (await thumb.Content.ReadAsByteArrayAsync()).Length == 4, "thumbnail uploaded by the plugin's transport");
var setup = await FirestoreGet($"observatories/{obsId}/setups/A");
Check(setup?["fields"]?["mainRigId"]?["stringValue"]?.GetValue<string>() == "A", "setup A has main rig A");
Check(setup?["fields"]?["rigIds"]?["arrayValue"]?["values"]?.AsArray().Count == 2, "setup A groups both optical paths");

// --- Remote command through the app API → delivered to both paths → executed by the plugin's CommandProcessor
var issued = await AppPost($"/v2/observatories/{obsId}/commands", new { type = "safeShutdown", setupId = "A" });
var commandId = (await issued.Content.ReadFromJsonAsync<JsonObject>())!["commandId"]!.GetValue<string>();
foreach (var (engine, ownsMount) in new[] { (main, true), (linked, false) }) {
    var executor = new RecordingExecutor();
    var processor = new CommandProcessor(executor, () => new HashSet<string> { CommandTypes.SafeShutdown }, controlsMount: () => ownsMount);
    var handled = new TaskCompletionSource();
    engine.CommandsReceived += async (_, commands) => {
        foreach (var result in await processor.ProcessAsync(commands, CancellationToken.None)) {
            engine.Enqueue(result);
        }
        handled.TrySetResult();
    };
    // Commands ride on the next request (the plugin's heartbeat); force one.
    engine.Enqueue(new EventItem { Id = Guid.NewGuid().ToString("N"), Timestamp = DateTimeOffset.UtcNow, Kind = EventKinds.DeviceConnected, Title = "Camera connected" });
    await engine.FlushOnceAsync(CancellationToken.None);
    await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Check(executor.Calls.Contains("park") == ownsMount, $"{(ownsMount ? "main" : "linked")} rig executed safe shutdown ({string.Join(", ", executor.Calls)})");
    await engine.FlushOnceAsync(CancellationToken.None);
}
var command = await FirestoreGet($"observatories/{obsId}/commands/{commandId}");
Check(command?["fields"]?["status"]?["stringValue"]?.GetValue<string>() == "completed", "command completed by both paths");

// --- Wrong secret is rejected and reported as Unauthorized by the sync engine
ApiCredential.TryParse($"nm2_{credential!.KeyId}_{new string('x', 43)}", out var forged);
var intruder = new SyncEngine(new DurableOutbox(Path.Combine(root, "x")), transport, () => forged, () => new ClientInfo { RigId = "X", SetupId = "X", RigRole = "main", PluginVersion = "2" });
intruder.UpdateSnapshot(new ObservatorySnapshot());
Check(await intruder.FlushOnceAsync(CancellationToken.None) == FlushOutcome.Unauthorized, "forged secret rejected");

Directory.Delete(root, recursive: true);
Console.WriteLine(failures == 0 ? "ALL END-TO-END CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

async Task<HttpResponseMessage> AppPost(string path, object body) {
    using var request = new HttpRequestMessage(HttpMethod.Post, apiBase + path) { Content = JsonContent.Create(body) };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
    var response = await http.SendAsync(request);
    if (!response.IsSuccessStatusCode) {
        Console.WriteLine($"      app call {path} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }
    return response;
}

async Task<JsonNode?> FirestoreGet(string path) {
    using var request = new HttpRequestMessage(HttpMethod.Get, $"{firestoreBase}/{path}");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
    var response = await http.SendAsync(request);
    return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync()) : null;
}

sealed class ConsoleLog : NightManager.Core.Logging.INmLog {
    public void Debug(string message) => Console.WriteLine("      debug: " + message);
    public void Info(string message) => Console.WriteLine("      info: " + message);
    public void Warning(string message) => Console.WriteLine("      warn: " + message);
    public void Error(string message, Exception? exception = null) => Console.WriteLine("      error: " + message + " " + exception?.Message);
}

sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T> {
    public void Report(T value) => report(value);
}

sealed class RecordingExecutor : ICommandExecutor {
    public List<string> Calls { get; } = [];
    public Task AbortSequenceAsync(CancellationToken token) => Add("abort");
    public Task StopGuidingAsync(CancellationToken token) => Add("stopGuiding");
    public Task ParkMountAsync(CancellationToken token) => Add("park");
    public Task CloseDomeAsync(CancellationToken token) => Add("closeDome");
    public Task WarmCameraAsync(CancellationToken token) => Add("warm");
    public bool IsAvailable(string commandType) => true;
    private Task Add(string name) {
        Calls.Add(name);
        return Task.CompletedTask;
    }
}
