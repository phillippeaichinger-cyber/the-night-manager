using System.Globalization;
using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Outbox;

public sealed record OutboxEntry(IngestItem Item, string ItemPath, string? BlobPath);

public sealed record OutboxLimits {
    public int MaxItems { get; init; } = 20_000;
    public long MaxBlobBytes { get; init; } = 256L * 1024 * 1024;
}

/// <summary>
/// Durable, ordered, bounded queue of items waiting to reach the backend.
/// Each item is one JSON file (atomic write via rename); thumbnails are side-car blobs.
/// Survives NINA restarts and multi-hour internet outages. When limits are hit, thumbnails
/// are evicted first, then low-value items, so metadata and incidents survive the longest.
/// </summary>
public sealed class DurableOutbox {
    private const string ItemExtension = ".json";

    // Eviction preference when the item limit is reached: cheapest information first.
    private static readonly string[] EvictionOrder = ["snapshot", "event-info", "frame", "event-warning", "session", "commandResult", "event-critical"];

    private readonly object gate = new();
    private readonly string itemsDirectory;
    private readonly string blobsDirectory;
    private readonly OutboxLimits limits;
    private long nextOrder;

    public DurableOutbox(string rootDirectory, OutboxLimits? limits = null) {
        this.limits = limits ?? new OutboxLimits();
        itemsDirectory = Path.Combine(rootDirectory, "items");
        blobsDirectory = Path.Combine(rootDirectory, "blobs");
        Directory.CreateDirectory(itemsDirectory);
        Directory.CreateDirectory(blobsDirectory);
        CleanupTemporaryFiles();
        nextOrder = ReadMaxOrder() + 1;
    }

    public int Count {
        get {
            lock (gate) {
                return Directory.EnumerateFiles(itemsDirectory, "*" + ItemExtension).Count();
            }
        }
    }

    /// <summary>Persists an item (and optional thumbnail) before returning.</summary>
    public void Enqueue(IngestItem item, ReadOnlySpan<byte> blob = default) {
        var json = NmJson.Serialize<IngestItem>(item);

        lock (gate) {
            if (!blob.IsEmpty) {
                WriteAtomic(BlobPath(item.Id), blob);
            }

            var fileName = string.Create(CultureInfo.InvariantCulture, $"{nextOrder++:D16}-{EvictionClass(item)}-{SafeId(item.Id)}{ItemExtension}");
            WriteAtomic(Path.Combine(itemsDirectory, fileName), System.Text.Encoding.UTF8.GetBytes(json));
            EnforceLimits();
        }
    }

    /// <summary>Oldest-first entries, up to the given count and approximate serialized size.</summary>
    public IReadOnlyList<OutboxEntry> Peek(int maxItems, long maxBytes) {
        lock (gate) {
            var entries = new List<OutboxEntry>();
            long bytes = 0;

            foreach (var path in OrderedItemFiles()) {
                if (entries.Count >= maxItems) {
                    break;
                }

                var length = new FileInfo(path).Length;

                if (entries.Count > 0 && bytes + length > maxBytes) {
                    break;
                }

                var item = TryRead(path);

                if (item is null) {
                    // Corrupt file (e.g. power loss before rename on an exotic filesystem): drop it.
                    TryDelete(path);
                    continue;
                }

                var blobPath = BlobPath(item.Id);
                entries.Add(new OutboxEntry(item, path, File.Exists(blobPath) ? blobPath : null));
                bytes += length;
            }

            return entries;
        }
    }

    /// <summary>Removes delivered entries and their blobs.</summary>
    public void Complete(IEnumerable<OutboxEntry> entries) {
        lock (gate) {
            foreach (var entry in entries) {
                TryDelete(entry.ItemPath);

                if (entry.BlobPath is not null) {
                    TryDelete(entry.BlobPath);
                }
            }
        }
    }

    /// <summary>Removes a thumbnail that was delivered (or will never be) while keeping its item.</summary>
    public void DropBlob(string itemId) {
        lock (gate) {
            TryDelete(BlobPath(itemId));
        }
    }

    private void EnforceLimits() {
        var blobs = new DirectoryInfo(blobsDirectory).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
        var blobBytes = blobs.Sum(f => f.Length);

        foreach (var blob in blobs) {
            if (blobBytes <= limits.MaxBlobBytes) {
                break;
            }

            blobBytes -= blob.Length;
            TryDelete(blob.FullName);
        }

        var items = OrderedItemFiles().ToList();
        var excess = items.Count - limits.MaxItems;

        if (excess <= 0) {
            return;
        }

        foreach (var evictionClass in EvictionOrder) {
            foreach (var path in items.Where(p => ClassOf(p) == evictionClass).ToList()) {
                if (excess <= 0) {
                    return;
                }

                TryDelete(path);
                items.Remove(path);
                excess--;
            }
        }
    }

    private static string EvictionClass(IngestItem item) => item switch {
        SnapshotItem => "snapshot",
        EventItem e => "event-" + (e.Severity is Severity.Warning or Severity.Critical ? e.Severity : Severity.Info),
        FrameItem => "frame",
        SessionItem => "session",
        CommandResultItem => "commandResult",
        _ => "event-info"
    };

    private static string ClassOf(string path) {
        var name = Path.GetFileNameWithoutExtension(path);
        var parts = name.Split('-');
        // order-class[-severity]-id
        return parts.Length >= 4 && parts[1] == "event" ? $"{parts[1]}-{parts[2]}" : parts.Length >= 2 ? parts[1] : string.Empty;
    }

    private IEnumerable<string> OrderedItemFiles() =>
        Directory.EnumerateFiles(itemsDirectory, "*" + ItemExtension).Order(StringComparer.Ordinal);

    private long ReadMaxOrder() {
        var last = OrderedItemFiles().LastOrDefault();

        if (last is null) {
            return 0;
        }

        var orderText = Path.GetFileName(last).Split('-')[0];
        return long.TryParse(orderText, NumberStyles.None, CultureInfo.InvariantCulture, out var order) ? order : 0;
    }

    private string BlobPath(string itemId) => Path.Combine(blobsDirectory, SafeId(itemId) + ".bin");

    private static string SafeId(string id) => new(id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());

    private static IngestItem? TryRead(string path) {
        try {
            return NmJson.Deserialize<IngestItem>(File.ReadAllText(path));
        } catch (Exception) {
            return null;
        }
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> content) {
        var temporary = path + ".tmp";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private void CleanupTemporaryFiles() {
        foreach (var temporary in Directory.EnumerateFiles(itemsDirectory, "*.tmp").Concat(Directory.EnumerateFiles(blobsDirectory, "*.tmp"))) {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
