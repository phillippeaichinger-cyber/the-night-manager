using NightManager.Core.Contracts;
using NightManager.Core.Outbox;

namespace NightManager.Core.Tests;

public class OutboxTests {
    private string root = null!;

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "nm-outbox-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(root)) {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Peek_ReturnsItemsInEnqueueOrder() {
        var outbox = new DurableOutbox(root);

        for (var i = 0; i < 5; i++) {
            outbox.Enqueue(Event($"e{i}"));
        }

        var ids = outbox.Peek(10, long.MaxValue).Select(e => e.Item.Id);

        Assert.That(ids, Is.EqualTo(new[] { "e0", "e1", "e2", "e3", "e4" }));
    }

    [Test]
    public void Items_SurviveRestart_AndOrderingContinues() {
        var first = new DurableOutbox(root);
        first.Enqueue(Event("before1"));
        first.Enqueue(Event("before2"));

        var second = new DurableOutbox(root);
        second.Enqueue(Event("after"));

        Assert.That(second.Peek(10, long.MaxValue).Select(e => e.Item.Id), Is.EqualTo(new[] { "before1", "before2", "after" }));
    }

    [Test]
    public void Complete_RemovesItemsAndBlobs() {
        var outbox = new DurableOutbox(root);
        outbox.Enqueue(Frame("f1"), new byte[] { 1, 2, 3 });
        outbox.Enqueue(Event("e1"));

        var entries = outbox.Peek(1, long.MaxValue);
        Assert.That(entries[0].BlobPath, Is.Not.Null);
        Assert.That(File.ReadAllBytes(entries[0].BlobPath!), Is.EqualTo(new byte[] { 1, 2, 3 }));

        outbox.Complete(entries);

        Assert.That(outbox.Count, Is.EqualTo(1));
        Assert.That(File.Exists(entries[0].BlobPath!), Is.False);
        Assert.That(outbox.Peek(10, long.MaxValue).Single().Item.Id, Is.EqualTo("e1"));
    }

    [Test]
    public void Peek_RespectsByteBudget_ButAlwaysReturnsAtLeastOne() {
        var outbox = new DurableOutbox(root);
        outbox.Enqueue(Event("big", new string('x', 5000)));
        outbox.Enqueue(Event("next"));

        Assert.That(outbox.Peek(10, 100).Select(e => e.Item.Id), Is.EqualTo(new[] { "big" }));
    }

    [Test]
    public void ItemLimit_EvictsInfoEventsBeforeWarningsAndFrames() {
        var outbox = new DurableOutbox(root, new OutboxLimits { MaxItems = 3 });
        outbox.Enqueue(Event("warning", severity: Severity.Warning));
        outbox.Enqueue(Frame("frame"));
        outbox.Enqueue(Event("info1"));
        outbox.Enqueue(Event("info2"));

        var remaining = outbox.Peek(10, long.MaxValue).Select(e => e.Item.Id).ToList();

        Assert.That(remaining, Is.EquivalentTo(new[] { "warning", "frame", "info2" }));
    }

    [Test]
    public void BlobLimit_DropsOldestThumbnailsButKeepsMetadata() {
        var outbox = new DurableOutbox(root, new OutboxLimits { MaxBlobBytes = 2500 });
        outbox.Enqueue(Frame("old"), new byte[1000]);
        Thread.Sleep(20);
        outbox.Enqueue(Frame("mid"), new byte[1000]);
        Thread.Sleep(20);
        outbox.Enqueue(Frame("new"), new byte[1000]);

        var entries = outbox.Peek(10, long.MaxValue);

        Assert.That(entries.Select(e => e.Item.Id), Is.EqualTo(new[] { "old", "mid", "new" }));
        Assert.That(entries.Single(e => e.Item.Id == "old").BlobPath, Is.Null);
        Assert.That(entries.Single(e => e.Item.Id == "new").BlobPath, Is.Not.Null);
    }

    [Test]
    public void CorruptItemFiles_AreSkippedAndRemoved() {
        var outbox = new DurableOutbox(root);
        outbox.Enqueue(Event("good"));
        File.WriteAllText(Path.Combine(root, "items", "0000000000000000-event-info-broken.json"), "{ not json");

        var entries = outbox.Peek(10, long.MaxValue);

        Assert.That(entries.Select(e => e.Item.Id), Is.EqualTo(new[] { "good" }));
        Assert.That(outbox.Count, Is.EqualTo(1));
    }

    internal static EventItem Event(string id, string? message = null, string severity = Severity.Info) => new() {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        Kind = EventKinds.UserNotification,
        Title = id,
        Message = message,
        Severity = severity
    };

    internal static FrameItem Frame(string id) => new() {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        FileName = id + ".fits",
        Thumbnail = new ThumbnailInfo { Width = 10, Height = 10, Bytes = 3 }
    };
}
