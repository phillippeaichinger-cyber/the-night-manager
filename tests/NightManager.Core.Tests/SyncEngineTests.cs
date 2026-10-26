using Microsoft.Extensions.Time.Testing;
using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Outbox;
using NightManager.Core.Security;
using NightManager.Core.Sync;

namespace NightManager.Core.Tests;

public class SyncEngineTests {
    private string root = null!;
    private FakeTimeProvider time = null!;
    private FakeTransport transport = null!;
    private DurableOutbox outbox = null!;
    private ApiCredential? credential;

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "nm-sync-" + Guid.NewGuid().ToString("N"));
        time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 22, 0, 0, TimeSpan.Zero));
        transport = new FakeTransport();
        outbox = new DurableOutbox(root);
        ApiCredential.TryParse("nm2_key1_0123456789abcdef0123456789abcdefXYZ", out credential);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    private SyncEngine CreateEngine() => new(
        outbox,
        transport,
        () => credential,
        () => new ClientInfo { RigId = "rig-1", RigName = "Pier 1", SetupId = "rig-1", RigRole = "main", PluginVersion = "2.0.0.0" },
        new SyncOptions(),
        time);

    [Test]
    public async Task NotPaired_SendsNothing() {
        credential = null;
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Event("e1"));

        var outcome = await engine.FlushOnceAsync(CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(FlushOutcome.NotPaired));
        Assert.That(transport.Requests, Is.Empty);
        Assert.That(engine.Status.State, Is.EqualTo(SyncState.NotPaired));
    }

    [Test]
    public async Task Success_SendsSignedBatchAndCompletesItems() {
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Event("e1"));
        engine.UpdateSnapshot(new ObservatorySnapshot { Safety = new SafetyState { Connected = true, IsSafe = true } });
        transport.NextResponses.Enqueue(new ApiResult(200, NmJson.Serialize(new IngestResponse { ObservatoryName = "Sierra Remote" })));

        var outcome = await engine.FlushOnceAsync(CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(FlushOutcome.Sent));
        var request = transport.Requests.Single();
        Assert.That(request.Path, Is.EqualTo(ApiPaths.Ingest));
        Assert.That(request.Credential, Is.SameAs(credential));
        var batch = NmJson.Deserialize<IngestBatch>(request.Json)!;
        Assert.That(batch.Items.Select(i => i.GetType()), Is.EqualTo(new[] { typeof(EventItem), typeof(SnapshotItem) }));
        Assert.That(outbox.Count, Is.Zero);
        Assert.That(engine.Status.State, Is.EqualTo(SyncState.Online));
        Assert.That(engine.Status.ObservatoryName, Is.EqualTo("Sierra Remote"));
    }

    [Test]
    public async Task LockedRig_IsReportedInStatus() {
        var engine = CreateEngine();
        engine.UpdateSnapshot(new ObservatorySnapshot());
        transport.NextResponses.Enqueue(new ApiResult(200, "{\"rigLocked\":true,\"observatoryName\":\"Home\"}"));

        await engine.FlushOnceAsync(CancellationToken.None);

        Assert.That(engine.Status.State, Is.EqualTo(SyncState.Online));
        Assert.That(engine.Status.LastError, Is.EqualTo(SyncEngine.RigLockedMessage));
    }

    [Test]
    public async Task ServerError_KeepsItemsForRetry() {
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Event("e1"));
        transport.NextResponses.Enqueue(new ApiResult(503, null));

        var outcome = await engine.FlushOnceAsync(CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(FlushOutcome.TransientFailure));
        Assert.That(outbox.Count, Is.EqualTo(1));
        Assert.That(engine.Status.State, Is.EqualTo(SyncState.Offline));

        transport.NextResponses.Enqueue(new ApiResult(200, "{}"));
        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.Sent));
        Assert.That(outbox.Count, Is.Zero);
    }

    [Test]
    public async Task NetworkFailure_IsTransient() {
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Event("e1"));
        transport.NextResponses.Enqueue(new ApiResult(0, null, null, "No such host"));

        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.TransientFailure));
        Assert.That(engine.Status.LastError, Is.EqualTo("No such host"));
        Assert.That(outbox.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Unauthorized_StopsAndKeepsData() {
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Event("e1"));
        transport.NextResponses.Enqueue(new ApiResult(401, "{\"error\":\"revoked\"}"));

        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.Unauthorized));
        Assert.That(engine.Status.State, Is.EqualTo(SyncState.Unauthorized));
        Assert.That(outbox.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task BadRequest_DropsBatchSoQueueNeverWedges() {
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Event("poison"));
        transport.NextResponses.Enqueue(new ApiResult(400, "{\"error\":\"bad\"}"));

        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.Rejected));
        Assert.That(outbox.Count, Is.Zero);
    }

    [Test]
    public async Task Thumbnails_AreUploadedToSignedUrlsFromResponse() {
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Frame("f1"), new byte[] { 9, 9, 9 });
        engine.Enqueue(OutboxTests.Frame("f2"), new byte[] { 8 });
        transport.NextResponses.Enqueue(new ApiResult(200, NmJson.Serialize(new IngestResponse {
            Uploads = [new BlobUpload { ItemId = "f2", Url = "https://storage.example/f2" }]
        })));

        await engine.FlushOnceAsync(CancellationToken.None);

        var upload = transport.Uploads.Single();
        Assert.That(upload.Upload.Url, Is.EqualTo("https://storage.example/f2"));
        Assert.That(upload.Bytes, Is.EqualTo(new byte[] { 8 }));
        Assert.That(outbox.Count, Is.Zero);
    }

    [Test]
    public async Task Commands_InResponseAreRaised() {
        var engine = CreateEngine();
        engine.UpdateSnapshot(new ObservatorySnapshot());
        IReadOnlyList<RemoteCommand>? received = null;
        engine.CommandsReceived += (_, commands) => received = commands;
        transport.NextResponses.Enqueue(new ApiResult(200, NmJson.Serialize(new IngestResponse {
            Commands = [new RemoteCommand { Id = "c1", Type = CommandTypes.AbortSequence, IssuedAt = time.GetUtcNow(), ExpiresAt = time.GetUtcNow().AddMinutes(5) }]
        })));

        await engine.FlushOnceAsync(CancellationToken.None);

        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received![0].Type, Is.EqualTo(CommandTypes.AbortSequence));
    }

    [Test]
    public async Task Snapshot_IsCoalescedAndPacedByHeartbeat() {
        var engine = CreateEngine();
        engine.UpdateSnapshot(new ObservatorySnapshot());
        transport.RespondOkForever = true;

        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.Sent));

        // Unchanged state right after sending: nothing to do.
        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.NothingToSend));

        // Many changes within the pacing window coalesce into a single snapshot.
        engine.UpdateSnapshot(new ObservatorySnapshot { Camera = new CameraState { Temperature = -5 } });
        engine.UpdateSnapshot(new ObservatorySnapshot { Camera = new CameraState { Temperature = -6 } });
        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.NothingToSend));

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.Sent));
        var snapshot = (SnapshotItem)NmJson.Deserialize<IngestBatch>(transport.Requests.Last().Json)!.Items.Single();
        Assert.That(snapshot.Snapshot.Camera!.Temperature, Is.EqualTo(-6));

        // With no changes, the heartbeat still proves the observatory is alive.
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.That(await engine.FlushOnceAsync(CancellationToken.None), Is.EqualTo(FlushOutcome.Sent));
    }

    [Test]
    public async Task RunLoop_RetriesAfterBackoffAndDeliversQueuedData() {
        var engine = CreateEngine();
        engine.Enqueue(OutboxTests.Event("e1"));
        transport.NextResponses.Enqueue(new ApiResult(503, null));
        transport.RespondOkForever = true;
        using var cts = new CancellationTokenSource();

        var loop = engine.RunAsync(cts.Token);
        await WaitUntil(() => transport.Requests.Count >= 1);
        Assert.That(outbox.Count, Is.EqualTo(1));

        // Advance fake time gradually: the loop registers its backoff timer asynchronously.
        await WaitUntil(() => {
            time.Advance(TimeSpan.FromSeconds(1));
            return outbox.Count == 0;
        });

        cts.Cancel();
        await loop;
        Assert.That(transport.Requests, Has.Count.GreaterThanOrEqualTo(2));
    }

    private static async Task WaitUntil(Func<bool> condition) {
        for (var i = 0; i < 200 && !condition(); i++) {
            await Task.Delay(10);
        }

        Assert.That(condition(), Is.True, "Condition not reached in time.");
    }

    internal sealed class FakeTransport : IApiTransport {
        public List<(string Path, string Json, ApiCredential? Credential)> Requests { get; } = [];

        public List<(BlobUpload Upload, byte[] Bytes)> Uploads { get; } = [];

        public Queue<ApiResult> NextResponses { get; } = new();

        public bool RespondOkForever { get; set; }

        public Task<ApiResult> PostJsonAsync(string path, string json, ApiCredential? credential, CancellationToken token) {
            lock (Requests) {
                Requests.Add((path, json, credential));
                var result = NextResponses.Count > 0 ? NextResponses.Dequeue() : RespondOkForever ? new ApiResult(200, "{}") : new ApiResult(0, null, null, "No response scripted");
                return Task.FromResult(result);
            }
        }

        public Task<ApiResult> UploadBlobAsync(BlobUpload upload, string filePath, string contentType, CancellationToken token) {
            Uploads.Add((upload, File.ReadAllBytes(filePath)));
            return Task.FromResult(new ApiResult(200, null));
        }
    }
}
