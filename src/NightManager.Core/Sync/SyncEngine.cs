using System.Threading.Channels;
using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Logging;
using NightManager.Core.Outbox;
using NightManager.Core.Security;

namespace NightManager.Core.Sync;

public enum SyncState {
    /// <summary>No credential yet: the user still has to pair.</summary>
    NotPaired,
    Connecting,
    Online,
    /// <summary>Backend unreachable; data is queued on disk and will be sent later.</summary>
    Offline,
    /// <summary>The credential was revoked or is invalid. Re-pairing is required.</summary>
    Unauthorized
}

public sealed record SyncStatus(
    SyncState State,
    DateTimeOffset? LastSuccess,
    string? LastError,
    int PendingItems,
    string? ObservatoryName);

public sealed record SyncOptions {
    /// <summary>Snapshot sent at least this often while connected, even when nothing changed.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Minimum spacing between flushes, so bursts of events share one request.</summary>
    public TimeSpan MinFlushInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Minimum spacing between snapshot-only flushes caused by state changes.</summary>
    public TimeSpan MinSnapshotInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxItemsPerBatch { get; init; } = 200;
    public long MaxBatchBytes { get; init; } = 512 * 1024;
}

public enum FlushOutcome {
    NothingToSend,
    Sent,
    NotPaired,
    TransientFailure,
    Unauthorized,
    Rejected
}

/// <summary>
/// Moves data from the durable outbox (plus the latest coalesced snapshot) to the backend.
/// One signed batch per flush; thumbnails uploaded to signed URLs from the response;
/// remote commands arrive piggy-backed on every response.
/// </summary>
public sealed class SyncEngine {
    private readonly DurableOutbox outbox;
    private readonly IApiTransport transport;
    private readonly TimeProvider time;
    private readonly Func<ApiCredential?> credentialProvider;
    private readonly Func<ClientInfo> clientInfo;
    private readonly SyncOptions options;
    private readonly INmLog log;
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly object gate = new();
    private readonly Random jitter = new();

    private ObservatorySnapshot? latestSnapshot;
    private string? latestSignature;
    private long snapshotVersion;
    private long sentSnapshotVersion;
    private DateTimeOffset lastSnapshotSent = DateTimeOffset.MinValue;
    private DateTimeOffset lastFlush = DateTimeOffset.MinValue;
    private int consecutiveFailures;
    private TimeSpan? retryAfter;
    private SyncStatus status;

    public SyncEngine(
        DurableOutbox outbox,
        IApiTransport transport,
        Func<ApiCredential?> credentialProvider,
        Func<ClientInfo> clientInfo,
        SyncOptions? options = null,
        TimeProvider? time = null,
        INmLog? log = null) {
        this.outbox = outbox;
        this.transport = transport;
        this.credentialProvider = credentialProvider;
        this.clientInfo = clientInfo;
        this.options = options ?? new SyncOptions();
        this.time = time ?? TimeProvider.System;
        this.log = log ?? NullLog.Instance;
        Heartbeat = this.options.Heartbeat;
        status = new SyncStatus(credentialProvider() is null ? SyncState.NotPaired : SyncState.Connecting, null, null, outbox.Count, null);
    }

    public event EventHandler<SyncStatus>? StatusChanged;

    public event EventHandler<IReadOnlyList<RemoteCommand>>? CommandsReceived;

    /// <summary>A side-car picture finished uploading (item id, success). Camera alerts wait for this before notifying.</summary>
    public event EventHandler<(string ItemId, bool Success)>? BlobUploaded;

    /// <summary>The observatory's plan includes cameras, as the backend last said (null until it has answered).</summary>
    public bool? CamerasAllowed { get; private set; }

    /// <summary>Optional data the owner switched off in the app (empty until the backend has answered).</summary>
    public IReadOnlySet<string> DataOff { get; private set; } = new HashSet<string>();

    /// <summary>The observatory's member-removal count, as the backend last said (null: not yet / older backend).</summary>
    public int? LocalLinkEpoch { get; private set; }

    /// <summary>The backend has answered at least once, so <see cref="DataOff"/> is known.</summary>
    public bool DataOffKnown { get; private set; }

    public SyncStatus Status {
        get {
            lock (gate) {
                return status;
            }
        }
    }

    public TimeSpan Heartbeat { get; private set; }

    /// <summary>The observatory this PC is paired to, as the backend last confirmed it.</summary>
    public string? ObservatoryId { get; private set; }

    /// <summary>
    /// Replace the latest observatory state. Cheap; only the newest snapshot is ever sent. With a signature (see
    /// <see cref="Live.SnapshotSignature"/>) it only counts as a change when what matters changed: values that tick
    /// (temperatures, mount position) then ride along with the next heartbeat instead of causing a send.
    /// </summary>
    public void UpdateSnapshot(ObservatorySnapshot snapshot, string? signature = null) {
        bool changed;
        lock (gate) {
            latestSnapshot = snapshot;
            changed = signature is null || signature != latestSignature;
            latestSignature = signature;
            if (changed) {
                snapshotVersion++;
            }
        }

        if (changed) {
            Wake();
        }
    }

    /// <summary>Asked before each send: whether to look for waiting commands. Null = always (old behaviour).</summary>
    public Func<bool>? CheckCommands { get; set; }

    /// <summary>Persist an item for delivery. Safe to call from any thread.</summary>
    public void Enqueue(IngestItem item, ReadOnlySpan<byte> thumbnail = default) {
        outbox.Enqueue(item, thumbnail);
        Wake();
    }

    /// <summary>Call after pairing or credential changes to retry immediately.</summary>
    public void CredentialsChanged() {
        lock (gate) {
            consecutiveFailures = 0;
            retryAfter = null;
        }

        SetStatus(credentialProvider() is null ? SyncState.NotPaired : SyncState.Connecting, null);
        Wake();
    }

    /// <summary>Ask for an immediate flush (e.g. after a command, so the app sees the result quickly).</summary>
    public void Wake() => wake.Writer.TryWrite(true);

    public async Task RunAsync(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            FlushOutcome outcome;

            try {
                outcome = await FlushOnceAsync(token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                log.Error("Night Manager sync loop error", ex);
                outcome = FlushOutcome.TransientFailure;
            }

            var delay = outcome switch {
                FlushOutcome.Sent when outbox.Count > 0 => options.MinFlushInterval,
                FlushOutcome.TransientFailure => NextBackoff(),
                FlushOutcome.Unauthorized or FlushOutcome.NotPaired => TimeSpan.FromMinutes(10),
                _ => Heartbeat
            };

            try {
                await WaitAsync(delay, outcome, token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                break;
            }
        }
    }

    /// <summary>One send attempt. Public for tests and for "send now" actions.</summary>
    public async Task<FlushOutcome> FlushOnceAsync(CancellationToken token) {
        var credential = credentialProvider();

        if (credential is null) {
            SetStatus(SyncState.NotPaired, null);
            return FlushOutcome.NotPaired;
        }

        var now = time.GetUtcNow();
        var entries = outbox.Peek(options.MaxItemsPerBatch, options.MaxBatchBytes);
        var (snapshotItem, version) = TakeSnapshotIfDue(now, entries.Count > 0);

        if (entries.Count == 0 && snapshotItem is null) {
            return FlushOutcome.NothingToSend;
        }

        var items = new List<IngestItem>(entries.Count + 1);
        items.AddRange(entries.Select(e => e.Item));

        if (snapshotItem is not null) {
            items.Add(snapshotItem);
        }

        var batch = new IngestBatch {
            BatchId = NewId(),
            SentAt = now,
            Client = clientInfo(),
            Items = items,
            CheckCommands = CheckCommands?.Invoke()
        };

        lastFlush = now;
        var result = await transport.PostJsonAsync(ApiPaths.Ingest, NmJson.Serialize(batch), credential, token).ConfigureAwait(false);

        if (result.IsSuccess) {
            var response = TryParse(result.Body);
            await UploadThumbnailsAsync(entries, response, token).ConfigureAwait(false);
            outbox.Complete(entries);

            lock (gate) {
                if (snapshotItem is not null) {
                    sentSnapshotVersion = version;
                    lastSnapshotSent = now;
                }

                consecutiveFailures = 0;
                retryAfter = null;

                if (response?.ObservatoryId is { Length: > 0 } observatoryId) {
                    ObservatoryId = observatoryId;
                }

                if (response?.HeartbeatSeconds is int heartbeat && heartbeat >= 10) {
                    Heartbeat = TimeSpan.FromSeconds(heartbeat);
                }

                if (response?.Cameras is bool cameras) {
                    CamerasAllowed = cameras;
                }

                if (response is not null) {
                    DataOff = new HashSet<string>(response.DataOff ?? []);
                    DataOffKnown = true;
                    LocalLinkEpoch = response.LocalLinkEpoch ?? LocalLinkEpoch;
                }
            }

            SetStatus(SyncState.Online, response?.RigLocked == true ? RigLockedMessage : null, now, response?.ObservatoryName);

            if (response is { Commands.Count: > 0 }) {
                CommandsReceived?.Invoke(this, response.Commands);
            }

            return FlushOutcome.Sent;
        }

        if (result.IsUnauthorized) {
            log.Warning($"Night Manager credential rejected ({result.StatusCode}). Re-pair the observatory.");
            SetStatus(SyncState.Unauthorized, "This observatory is no longer linked. Pair again from the Night Manager options.");
            return FlushOutcome.Unauthorized;
        }

        if (StatusCodes.IsBadRequest(result.StatusCode)) {
            // Never let one malformed item wedge the queue forever.
            log.Error($"Night Manager backend rejected a batch of {entries.Count} items ({result.StatusCode}): {Truncate(result.Body)}. Items dropped.");
            outbox.Complete(entries);
            SetStatus(SyncState.Online, $"Some data was rejected by the server ({result.StatusCode}).");
            return FlushOutcome.Rejected;
        }

        lock (gate) {
            consecutiveFailures++;
            retryAfter = result.RetryAfter;
        }

        var error = result.Error ?? $"HTTP {result.StatusCode}";
        log.Debug($"Night Manager sync failed: {error}");
        SetStatus(SyncState.Offline, error);
        return FlushOutcome.TransientFailure;
    }

    private (SnapshotItem? Item, long Version) TakeSnapshotIfDue(DateTimeOffset now, bool hasOtherItems) {
        lock (gate) {
            if (latestSnapshot is null) {
                return (null, 0);
            }

            var changed = snapshotVersion != sentSnapshotVersion;
            var heartbeatDue = now - lastSnapshotSent >= Heartbeat;
            var changeDue = changed && now - lastSnapshotSent >= options.MinSnapshotInterval;

            // Ride along with other items whenever state changed, otherwise respect the pacing.
            if (heartbeatDue || changeDue || (changed && hasOtherItems)) {
                return (new SnapshotItem { Id = NewId(), Timestamp = now, Snapshot = latestSnapshot, Signature = latestSignature }, snapshotVersion);
            }

            return (null, 0);
        }
    }

    private async Task UploadThumbnailsAsync(IReadOnlyList<OutboxEntry> entries, IngestResponse? response, CancellationToken token) {
        if (response is null || response.Uploads.Count == 0) {
            return;
        }

        foreach (var upload in response.Uploads) {
            var entry = entries.FirstOrDefault(e => e.Item.Id == upload.ItemId);

            if (entry?.BlobPath is null || entry.Item is not (FrameItem or CameraFrameItem)) {
                continue;
            }

            var contentType = entry.Item switch {
                FrameItem frame => frame.Thumbnail?.ContentType ?? "image/jpeg",
                CameraFrameItem camera => camera.ContentType,
                _ => "image/jpeg",
            };
            var label = entry.Item is FrameItem f ? f.FileName : $"camera {((CameraFrameItem)entry.Item).Name}";
            var result = await transport.UploadBlobAsync(upload, entry.BlobPath, contentType, token).ConfigureAwait(false);

            if (!result.IsSuccess && result.IsTransient) {
                // One quick retry; thumbnails are nice-to-have and the metadata is already delivered.
                result = await transport.UploadBlobAsync(upload, entry.BlobPath, contentType, token).ConfigureAwait(false);
            }

            if (!result.IsSuccess) {
                log.Warning($"Picture upload failed for {label}: {result.Error ?? "HTTP " + result.StatusCode}");
            }

            BlobUploaded?.Invoke(this, (upload.ItemId, result.IsSuccess));
        }
    }

    private TimeSpan NextBackoff() {
        lock (gate) {
            if (retryAfter is { } serverDelay && serverDelay > TimeSpan.Zero) {
                return serverDelay < options.MaxBackoff ? serverDelay : options.MaxBackoff;
            }

            var exponent = Math.Min(consecutiveFailures, 10);
            var seconds = Math.Min(options.MaxBackoff.TotalSeconds, 2 * Math.Pow(2, exponent - 1));
            // ±20% jitter so a fleet of observatories doesn't reconnect in lockstep after an outage.
            seconds *= 0.8 + (jitter.NextDouble() * 0.4);
            return TimeSpan.FromSeconds(Math.Max(1, seconds));
        }
    }

    private async Task WaitAsync(TimeSpan delay, FlushOutcome outcome, CancellationToken token) {
        if (outcome == FlushOutcome.TransientFailure) {
            // New data must not cut a backoff short; it is safely queued on disk meanwhile.
            // Only a credential change (handled by resetting the failure count) shortens it next time.
            await Task.Delay(delay, time, token).ConfigureAwait(false);
            return;
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var delayTask = Task.Delay(delay, time, delayCts.Token);
        var wakeTask = wake.Reader.WaitToReadAsync(delayCts.Token).AsTask();

        try {
            var completed = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);

            if (completed == wakeTask) {
                wake.Reader.TryRead(out _);
                // Batch bursts: never flush more often than the minimum interval.
                var sinceLast = time.GetUtcNow() - lastFlush;

                if (sinceLast < options.MinFlushInterval) {
                    await Task.Delay(options.MinFlushInterval - sinceLast, time, token).ConfigureAwait(false);
                }
            }
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
        } finally {
            delayCts.Cancel();
        }
    }

    private void SetStatus(SyncState state, string? error, DateTimeOffset? success = null, string? observatoryName = null) {
        SyncStatus updated;

        lock (gate) {
            updated = status with {
                State = state,
                LastError = error,
                LastSuccess = success ?? status.LastSuccess,
                PendingItems = outbox.Count,
                ObservatoryName = observatoryName ?? status.ObservatoryName
            };

            if (updated == status) {
                return;
            }

            status = updated;
        }

        StatusChanged?.Invoke(this, updated);
    }

    private static IngestResponse? TryParse(string? body) {
        if (string.IsNullOrWhiteSpace(body)) {
            return null;
        }

        try {
            return NmJson.Deserialize<IngestResponse>(body);
        } catch (System.Text.Json.JsonException) {
            return null;
        }
    }

    private static string Truncate(string? text) => text is null ? string.Empty : text.Length <= 300 ? text : text[..300];

    public const string RigLockedMessage = "This rig is beyond your plan's rig limit, so its data is not being stored. Upgrade in The Night Manager app to include it.";

    public static string NewId() => Guid.NewGuid().ToString("N");
}
