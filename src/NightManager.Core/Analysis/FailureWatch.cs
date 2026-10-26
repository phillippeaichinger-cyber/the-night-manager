using NightManager.Core.Contracts;

namespace NightManager.Core.Analysis;

/// <summary>One failure NINA reported through the sequence root's failure event.</summary>
public sealed record FailureReport {
    /// <summary>Identifies this run of this item (the adapter uses the object's identity).</summary>
    public required long Key { get; init; }
    public required string Name { get; init; }
    /// <summary>instruction | trigger | condition</summary>
    public string Kind { get; init; } = "instruction";
    /// <summary>The containing block or target, e.g. "M31".</summary>
    public string? Parent { get; init; }
    /// <summary>Attempts NINA allows the item (1 when not an instruction).</summary>
    public int Attempts { get; init; } = 1;
    /// <summary>NINA's InstructionErrorBehavior name for the item, when it has one.</summary>
    public string? ErrorBehavior { get; init; }
    public string? Error { get; init; }
    /// <summary>A "Failures to my phone" trigger in an enclosing block, if any.</summary>
    public FailurePolicy? Policy { get; init; }
}

/// <summary>Per-block settings from the "Failures to my phone" trigger.</summary>
public sealed record FailurePolicy {
    public bool Notify { get; init; } = true;
    /// <summary>Overrides the severity derived from the error behaviour: info | warning | critical.</summary>
    public string? Level { get; init; }
    /// <summary>Instruction names (or parts of them) that never notify.</summary>
    public IReadOnlyList<string> Ignore { get; init; } = [];
    /// <summary>Notify on every failed attempt instead of only when NINA gives up.</summary>
    public bool EveryAttempt { get; init; }
}

/// <summary>
/// Turns NINA's per-attempt failure events into what a person wants to hear: retries go quietly to the timeline,
/// the final failure is a notification saying what NINA does next, and a cascade (camera gone, six instructions
/// fail) becomes one alert followed by one summary.
/// </summary>
public sealed class FailureWatch {
    private readonly object gate = new();
    private readonly Func<string> newId;
    private readonly Dictionary<long, int> attempts = new();
    private readonly List<string> suppressed = new();
    private DateTimeOffset? burstUntil;

    public static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(2);

    public FailureWatch(Func<string>? newId = null) {
        this.newId = newId ?? (() => Guid.NewGuid().ToString("N"));
    }

    public string? SessionId { get; set; }

    /// <summary>When the last failure that aborts the sequence happened, and what failed.</summary>
    public (DateTimeOffset At, string Name, string? Error)? LastAbort { get; private set; }

    /// <summary>Items that failed an attempt and may still succeed on a retry (the adapter checks them later).</summary>
    public IReadOnlyCollection<long> Retrying {
        get { lock (gate) { return attempts.Keys.ToList(); } }
    }

    public IReadOnlyList<EventItem> OnFailure(FailureReport report, DateTimeOffset now) {
        lock (gate) {
            var attempt = attempts.TryGetValue(report.Key, out var seen) ? seen + 1 : 1;
            var allowed = Math.Max(1, report.Attempts);
            var final = attempt >= allowed;
            if (final) {
                attempts.Remove(report.Key);
            } else {
                attempts[report.Key] = attempt;
            }

            var next = final ? NextStep(report) : $"Retrying (attempt {attempt + 1} of {allowed}).";
            var severity = Severity(report, final);
            if (final && report.ErrorBehavior is "AbortOnError" or "SkipToSequenceEndInstructions") {
                LastAbort = (now, report.Name, report.Error);
            }

            var policy = report.Policy ?? new FailurePolicy();
            var ignored = policy.Ignore.Any(i => !string.IsNullOrWhiteSpace(i) && report.Name.Contains(i.Trim(), StringComparison.OrdinalIgnoreCase));
            var wantsPush = policy.Notify && !ignored && (final || policy.EveryAttempt);

            var notify = false;
            if (wantsPush) {
                if (burstUntil is { } until && now < until) {
                    suppressed.Add(report.Name);
                } else {
                    notify = true;
                    burstUntil = now + BurstWindow;
                    suppressed.Clear();
                }
            }

            var where = string.IsNullOrWhiteSpace(report.Parent) ? "" : $" in {report.Parent}";
            var title = final
                ? $"{report.Name} failed{where}"
                : $"{report.Name} failed{where}, retrying";
            var message = string.Join(" ", new[] { Sentence(report.Error), next }.Where(s => !string.IsNullOrEmpty(s)));

            return [new EventItem {
                Id = newId(),
                Timestamp = now,
                Kind = EventKinds.SequenceItemFailed,
                Severity = final ? severity : Contracts.Severity.Info,
                Title = title,
                Message = message,
                SessionId = SessionId,
                Data = new Dictionary<string, object?> {
                    ["item"] = report.Name,
                    ["itemKind"] = report.Kind,
                    ["parent"] = report.Parent,
                    ["attempt"] = attempt,
                    ["attempts"] = allowed,
                    ["final"] = final,
                    ["behavior"] = report.ErrorBehavior,
                    ["error"] = report.Error,
                    ["notify"] = notify,
                },
            }];
        }
    }

    /// <summary>An item that had failed an attempt finished successfully.</summary>
    public IReadOnlyList<EventItem> OnRecovered(long key, string name, DateTimeOffset now) {
        lock (gate) {
            if (!attempts.Remove(key, out var failed)) {
                return [];
            }
            return [new EventItem {
                Id = newId(),
                Timestamp = now,
                Kind = EventKinds.SequenceItemRecovered,
                Severity = Contracts.Severity.Info,
                Title = $"{name} succeeded after {failed} failed attempt{(failed == 1 ? "" : "s")}",
                SessionId = SessionId,
                Data = new Dictionary<string, object?> { ["item"] = name, ["failedAttempts"] = failed },
            }];
        }
    }

    /// <summary>The item stopped without recovering (skipped, cancelled): stop tracking it.</summary>
    public void Forget(long key) {
        lock (gate) {
            attempts.Remove(key);
        }
    }

    /// <summary>Closes a burst window: one summary of the failures that were held back.</summary>
    public IReadOnlyList<EventItem> Tick(DateTimeOffset now) {
        lock (gate) {
            if (burstUntil is not { } until || now < until) {
                return [];
            }
            burstUntil = null;
            if (suppressed.Count == 0) {
                return [];
            }
            var names = suppressed.GroupBy(n => n).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList();
            var count = suppressed.Count;
            suppressed.Clear();
            return [new EventItem {
                Id = newId(),
                Timestamp = now,
                Kind = EventKinds.SequenceFailureBurst,
                Severity = Contracts.Severity.Critical,
                Title = $"{count} more instruction{(count == 1 ? "" : "s")} failed",
                Message = string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" and {names.Count - 6} more" : "") + ". Something may need attention.",
                SessionId = SessionId,
                Data = new Dictionary<string, object?> { ["count"] = count, ["items"] = names, ["notify"] = true },
            }];
        }
    }

    public void Reset() {
        lock (gate) {
            attempts.Clear();
            suppressed.Clear();
            burstUntil = null;
            LastAbort = null;
        }
    }

    private static string NextStep(FailureReport report) => report.Kind switch {
        "trigger" => "The trigger didn't complete; the sequence carries on.",
        "condition" => "The condition couldn't be checked.",
        _ => report.ErrorBehavior switch {
            "AbortOnError" => "NINA is aborting the sequence.",
            "SkipInstructionSetOnError" => "NINA skips the rest of this block.",
            "SkipToSequenceEndInstructions" => "NINA skips to the end-of-sequence instructions.",
            _ => "The sequence carries on.",
        },
    };

    private static string Severity(FailureReport report, bool final) {
        if (report.Policy?.Level is { } level && level is Contracts.Severity.Info or Contracts.Severity.Warning or Contracts.Severity.Critical) {
            return level;
        }
        if (!final) {
            return Contracts.Severity.Info;
        }
        if (report.Kind == "trigger" && report.Name.Contains("Meridian", StringComparison.OrdinalIgnoreCase)) {
            return Contracts.Severity.Critical;
        }
        return report.ErrorBehavior switch {
            "AbortOnError" or "SkipToSequenceEndInstructions" => Contracts.Severity.Critical,
            _ => Contracts.Severity.Warning,
        };
    }

    private static string? Sentence(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }
        var t = text.Trim();
        if (t.Length > 240) {
            t = t[..237] + "…";
        }
        return t.EndsWith('.') || t.EndsWith('!') || t.EndsWith('?') || t.EndsWith('…') ? t : t + ".";
    }
}
