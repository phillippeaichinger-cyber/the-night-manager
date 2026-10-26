using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Sessions;

/// <summary>A frame as remembered by the session (local path stays on this machine; used for the cull list).</summary>
public sealed record SessionFrame {
    public required string FrameId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string FileName { get; init; }
    public string? LocalPath { get; init; }
    public required string ImageType { get; init; }
    public string? Target { get; init; }
    public string? Filter { get; init; }
    public double ExposureSeconds { get; init; }
    public double? Hfr { get; init; }
    public int? Stars { get; init; }
    public double? GuidingRms { get; init; }
    public required int Score { get; init; }
    public required string Verdict { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

public sealed record SessionOptions {
    /// <summary>End the session when nothing happened (no frames, no sequence) for this long.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(45);
    /// <summary>After a sequence finishes, wait this long for a restart before ending the session.</summary>
    public TimeSpan SequenceFinishedGrace { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>After dawn the night ends once no light frame has come in for this long...</summary>
    public TimeSpan AfterLastLight { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>...and no frame at all (dawn flats) for this long.</summary>
    public TimeSpan AfterLastFrame { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>No night is longer than this: an older session ends at the next quiet spell, whatever the sky says.</summary>
    public TimeSpan MaxNight { get; init; } = TimeSpan.FromHours(20);
    /// <summary>Emit a progress update item at most this often.</summary>
    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>Persisted state so a NINA restart mid-night continues the same session.</summary>
public sealed record SessionData {
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastActivity { get; set; }
    public DateTimeOffset? SequenceFinishedAt { get; set; }
    public bool SequenceRunning { get; set; }
    public DateTimeOffset LastUpdateEmitted { get; set; }
    public List<SessionFrame> Frames { get; init; } = [];
    /// <summary>Seconds of darkness per <see cref="NightTime"/> bucket.</summary>
    public Dictionary<string, double> Time { get; init; } = [];
    /// <summary>Autofocus runs, flips and dithers.</summary>
    public Dictionary<string, int> Counts { get; init; } = [];
}

public sealed record SessionEnded(SessionItem Item, IReadOnlyList<SessionFrame> Frames);

/// <summary>
/// Owns the definition of an imaging session ("a night"): starts on sequence start or the
/// first light frame, ends on sequence finish (after a grace period), dawn, inactivity or on
/// request. Thread-safe.
/// </summary>
public sealed class SessionTracker {
    private readonly object gate = new();
    private readonly string? statePath;
    private readonly SessionOptions options;
    private readonly Func<string> newId;
    private SessionData? current;
    /// <summary>A sequence that ran on past dawn (a Target Scheduler loop): the next night's session carries it on.</summary>
    private bool sequenceCarriesOver;

    public SessionTracker(string? statePath, SessionOptions? options = null, Func<string>? newId = null) {
        this.statePath = statePath;
        this.options = options ?? new SessionOptions();
        this.newId = newId ?? (() => Guid.NewGuid().ToString("N"));
        current = Load();
    }

    /// <summary>Raised (outside the lock) when a session ends, with every frame for the cull list.</summary>
    public event EventHandler<SessionEnded>? Ended;

    public string? CurrentSessionId {
        get {
            lock (gate) {
                return current?.Id;
            }
        }
    }

    public SessionSummary? Summary {
        get {
            lock (gate) {
                if (current is null) {
                    return null;
                }

                var lights = current.Frames.Where(f => FrameGrader.IsLight(f.ImageType)).ToList();
                return new SessionSummary {
                    Id = current.Id,
                    StartedAt = current.StartedAt,
                    Frames = current.Frames.Count,
                    Accepted = lights.Count(f => f.Verdict is Verdicts.Accept or Verdicts.Marginal),
                    Rejected = lights.Count(f => f.Verdict == Verdicts.Reject),
                    IntegrationSeconds = lights.Sum(f => f.ExposureSeconds),
                    Last = lights.Count == 0 ? null : lights[^1] is var last ? new LastFrame {
                        At = last.Timestamp, Filter = last.Filter, ExposureSeconds = last.ExposureSeconds,
                        Hfr = last.Hfr, Score = last.Score, Verdict = last.Verdict,
                    } : null
                };
            }
        }
    }

    /// <summary>Items to enqueue (start marker, etc.).</summary>
    public IReadOnlyList<IngestItem> OnSequenceStarted(DateTimeOffset now) {
        lock (gate) {
            var items = new List<IngestItem>();
            EnsureStarted(now, items);
            current!.SequenceRunning = true;
            current.SequenceFinishedAt = null;
            current.LastActivity = now;
            Save();
            return items;
        }
    }

    public void OnSequenceFinished(DateTimeOffset now) {
        lock (gate) {
            if (current is null) {
                return;
            }

            current.SequenceRunning = false;
            current.SequenceFinishedAt = now;
            current.LastActivity = now;
            Save();
        }
    }

    /// <summary>Records a frame. Light frames start a session if none is active.</summary>
    public IReadOnlyList<IngestItem> OnFrame(SessionFrame frame) {
        lock (gate) {
            var items = new List<IngestItem>();

            if (current is null && !FrameGrader.IsLight(frame.ImageType)) {
                // Calibration frames outside a night don't make a session.
                return items;
            }

            EnsureStarted(frame.Timestamp, items);
            current!.Frames.Add(frame);
            current.LastActivity = frame.Timestamp;
            Save();
            return items;
        }
    }

    /// <summary>Periodic evaluation (call about once a minute). Returns items to enqueue.</summary>
    public IReadOnlyList<IngestItem> Tick(DateTimeOffset now, NightState? night) {
        SessionEnded? ended = null;
        var items = new List<IngestItem>();

        lock (gate) {
            if (current is null) {
                return items;
            }

            string? endReason = null;

            if (current.SequenceFinishedAt is DateTimeOffset finished && !current.SequenceRunning && now - finished >= options.SequenceFinishedGrace) {
                endReason = "sequenceFinished";
            } else if (Dawn(night) is DateTimeOffset dawn && now >= dawn && current.StartedAt < dawn
                       && now - LastLight(current) >= options.AfterLastLight && now - current.LastActivity >= options.AfterLastFrame) {
                // The night ends at dawn once the last subs (and any dawn flats) are in, even while a sequence "runs": a
                // Target Scheduler loop waiting for the next night would otherwise keep one session open for days.
                // The same rule as the server's (lib/night.ts), so the app's card, summary and report follow at once.
                endReason = "dawn";
            } else if (!current.SequenceRunning && now - current.LastActivity >= options.IdleTimeout) {
                endReason = "idle";
            } else if (now - current.StartedAt >= options.MaxNight && now - current.LastActivity >= options.IdleTimeout) {
                endReason = "dawn";
            }

            if (endReason is not null) {
                ended = EndLocked(now, endReason);
                items.Add(ended.Item);
            } else if (now - current.LastUpdateEmitted >= options.UpdateInterval) {
                current.LastUpdateEmitted = now;
                items.Add(BuildItem(current, SessionPhases.Updated, now, null, null));
                Save();
            }
        }

        if (ended is not null) {
            Ended?.Invoke(this, ended);
        }

        return items;
    }

    /// <summary>Astronomical dawn; without astronomical darkness (summer) nautical dawn, else sunrise.</summary>
    internal static DateTimeOffset? Dawn(NightState? night) => night?.AstronomicalDawn ?? night?.NauticalDawn ?? night?.Sunrise;

    private static DateTimeOffset LastLight(SessionData data) =>
        data.Frames.LastOrDefault(f => FrameGrader.IsLight(f.ImageType))?.Timestamp ?? data.StartedAt;

    /// <summary>End now (sequence instruction, plugin teardown, user action). Null if no session.</summary>
    public SessionEnded? End(DateTimeOffset now, string reason) {
        SessionEnded? ended;

        lock (gate) {
            if (current is null) {
                return null;
            }

            ended = EndLocked(now, reason);
        }

        Ended?.Invoke(this, ended);
        return ended;
    }

    private void EnsureStarted(DateTimeOffset now, List<IngestItem> items) {
        if (current is not null) {
            return;
        }

        current = new SessionData { Id = newId(), StartedAt = now, LastActivity = now, LastUpdateEmitted = now, SequenceRunning = sequenceCarriesOver };
        sequenceCarriesOver = false;
        items.Add(BuildItem(current, SessionPhases.Started, now, null, null));
    }

    private SessionEnded EndLocked(DateTimeOffset now, string reason) {
        var data = current!;
        var item = BuildItem(data, SessionPhases.Ended, now, now, reason);
        sequenceCarriesOver = data.SequenceRunning;
        current = null;
        Save();
        return new SessionEnded(item, data.Frames.ToList());
    }

    private SessionItem BuildItem(SessionData data, string phase, DateTimeOffset now, DateTimeOffset? endedAt, string? reason) => new() {
        Id = newId(),
        Timestamp = now,
        SessionId = data.Id,
        Phase = phase,
        StartedAt = data.StartedAt,
        EndedAt = endedAt,
        EndReason = reason,
        Totals = ComputeTotals(data.Frames) with {
            Time = data.Time.Count == 0 ? null : data.Time.ToDictionary(t => t.Key, t => Math.Round(t.Value)),
            Counts = data.Counts.Count == 0 ? null : new Dictionary<string, int>(data.Counts),
        }
    };

    /// <summary>Adds seconds of darkness to a bucket (kept in memory; saved with the next frame or update).</summary>
    public void AddTime(string bucket, double seconds) {
        if (seconds <= 0) return;
        lock (gate) {
            if (current is null) return;
            current.Time[bucket] = current.Time.GetValueOrDefault(bucket) + seconds;
        }
    }

    public void Count(string what) {
        lock (gate) {
            if (current is null) return;
            current.Counts[what] = current.Counts.GetValueOrDefault(what) + 1;
        }
    }

    public static SessionTotals ComputeTotals(IReadOnlyCollection<SessionFrame> frames) {
        var lights = frames.Where(f => FrameGrader.IsLight(f.ImageType)).ToList();
        static bool Usable(SessionFrame f) => f.Verdict is Verdicts.Accept or Verdicts.Marginal or Verdicts.NotGraded;

        var targets = lights
            .GroupBy(f => f.Target ?? "Unknown target", StringComparer.OrdinalIgnoreCase)
            .Select(target => new TargetTotals {
                Target = target.Key,
                Filters = target
                    .GroupBy(f => f.Filter ?? "No filter", StringComparer.OrdinalIgnoreCase)
                    .Select(filter => new FilterTotals {
                        Filter = filter.Key,
                        Frames = filter.Count(),
                        Accepted = filter.Count(Usable),
                        IntegrationSeconds = filter.Sum(f => f.ExposureSeconds),
                        AcceptedIntegrationSeconds = filter.Where(Usable).Sum(f => f.ExposureSeconds)
                    })
                    .OrderBy(f => f.Filter, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderBy(t => t.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SessionTotals {
            Frames = frames.Count,
            LightFrames = lights.Count,
            Accepted = lights.Count(f => f.Verdict == Verdicts.Accept),
            Marginal = lights.Count(f => f.Verdict == Verdicts.Marginal),
            Rejected = lights.Count(f => f.Verdict == Verdicts.Reject),
            IntegrationSeconds = lights.Sum(f => f.ExposureSeconds),
            AcceptedIntegrationSeconds = lights.Where(Usable).Sum(f => f.ExposureSeconds),
            BestHfr = lights.Where(f => f.Hfr is > 0).Select(f => f.Hfr).Min(),
            MedianHfr = FrameGrader.Median(lights.Select(f => f.Hfr)),
            MedianGuidingRms = FrameGrader.Median(lights.Select(f => f.GuidingRms)),
            Targets = targets
        };
    }

    private SessionData? Load() {
        if (statePath is null || !File.Exists(statePath)) {
            return null;
        }

        try {
            return NmJson.Deserialize<SessionData>(File.ReadAllText(statePath));
        } catch (Exception) {
            return null;
        }
    }

    private void Save() {
        if (statePath is null) {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);

        if (current is null) {
            File.Delete(statePath);
            return;
        }

        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, NmJson.Serialize(current));
        File.Move(temporary, statePath, overwrite: true);
    }
}
