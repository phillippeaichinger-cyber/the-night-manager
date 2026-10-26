using NightManager.Core.Contracts;

namespace NightManager.Core.Readiness;

/// <summary>Everything the readiness check looks at, gathered by the plugin.</summary>
public sealed record ReadinessInput {
    public required string Phase { get; init; }
    public required DateTimeOffset Now { get; init; }
    /// <summary>Device kinds connected now ("camera", "mount", "focuser"…).</summary>
    public IReadOnlySet<string> Connected { get; init; } = new HashSet<string>();
    /// <summary>What came up on recent nights, and on how many of them.</summary>
    public IReadOnlyList<(string Device, int Nights)> Expected { get; init; } = [];
    public bool SequenceLoaded { get; init; }
    public string? SequenceTitle { get; init; }
    public bool SequenceRunning { get; init; }
    /// <summary>The wait the sequence is in ("until 20:01", "until safe").</summary>
    public string? Waiting { get; init; }
    public PowerPlan Power { get; init; } = new();
    public IReadOnlyList<string> MissingScripts { get; init; } = [];
    public long? FreeBytes { get; init; }
    /// <summary>Roughly what tonight's planned subs take on disk, when known.</summary>
    public long? NeededBytes { get; init; }
    public bool SchedulerInstalled { get; init; }
    /// <summary>Targets in Target Scheduler's plan for tonight; null when its plan can't be read.</summary>
    public int? SchedulerTargetsTonight { get; init; }
    public bool SafetyConnected { get; init; }
    public bool? IsSafe { get; init; }
    /// <summary>The sequence check's findings (tips, and NINA's own warnings).</summary>
    public IReadOnlyList<ReadinessItem> SequenceFindings { get; init; } = [];
}

/// <summary>
/// Is the rig ready for tonight? Only flags what is actually wrong: a sequence that powers its own equipment on later
/// isn't "disconnected", it's "powered up by the sequence"; a wait for safe conditions isn't a fault.
/// </summary>
public static class ReadinessCheck {
    private const long Gigabyte = 1L << 30;

    private static readonly Dictionary<string, string> DeviceNames = new() {
        ["camera"] = "Camera", ["mount"] = "Mount", ["guider"] = "Guider", ["focuser"] = "Focuser",
        ["filterWheel"] = "Filter wheel", ["rotator"] = "Rotator", ["dome"] = "Dome", ["safety"] = "Safety monitor",
        ["weather"] = "Weather", ["flatDevice"] = "Flat panel",
    };

    public static string DeviceName(string kind) => DeviceNames.TryGetValue(kind, out var name) ? name : kind;

    public static ReadinessReport Evaluate(ReadinessInput input) {
        var items = new List<ReadinessItem>();
        var afterPowerUp = input.Phase == ReadinessPhases.AfterPowerUp;
        var beforeImaging = input.Phase is ReadinessPhases.BeforeDark or ReadinessPhases.LateStart;

        // --- equipment
        var expected = input.Expected.Where(e => e.Device != "weather").ToList();
        var missing = expected.Where(e => !input.Connected.Contains(e.Device)).ToList();
        if (beforeImaging && input.Power.PowersUp && (input.Connected.Count == 0 || missing.Count > 0)) {
            var usual = expected.Count > 0 ? $" {expected.Count} devices came up on previous nights." : "";
            items.Add(new ReadinessItem {
                Key = "equipment", Status = ReadinessStatus.Later, Title = "Equipment is powered up by the sequence",
                Detail = input.Power.Describe() + usual,
            });
        } else if (input.Connected.Count == 0 && !input.Power.PowersUp) {
            items.Add(new ReadinessItem {
                Key = "equipment", Status = ReadinessStatus.Problem, Title = "No equipment connected",
                Detail = input.SequenceLoaded ? "And the loaded sequence doesn't connect it." : "Connect it in NINA, or load a sequence that does.",
            });
        } else if (missing.Count > 0) {
            foreach (var (device, nights) in missing) {
                items.Add(new ReadinessItem {
                    Key = "device." + device, Status = ReadinessStatus.Problem,
                    Title = afterPowerUp ? $"{DeviceName(device)} didn't come up" : $"{DeviceName(device)} isn't connected",
                    Detail = nights > 1 ? $"It was connected on the last {nights} nights." : "It was connected last night.",
                });
            }
        } else if (input.Connected.Count > 0) {
            var count = input.Connected.Count;
            items.Add(new ReadinessItem {
                Key = "equipment", Status = ReadinessStatus.Ok,
                Title = afterPowerUp ? (count == 1 ? "1 device came up" : $"All {count} devices came up") : (count == 1 ? "1 device connected" : $"{count} devices connected"),
                Detail = string.Join(", ", input.Connected.OrderBy(d => d).Select(DeviceName)),
            });
        }

        // --- scripts the sequence runs
        foreach (var script in input.MissingScripts) {
            items.Add(new ReadinessItem {
                Key = "scripts", Status = ReadinessStatus.Problem, Title = "A script the sequence runs is missing",
                Detail = script,
            });
        }

        // --- the sequence
        if (!input.SequenceLoaded) {
            items.Add(new ReadinessItem { Key = "sequence", Status = ReadinessStatus.Warning, Title = "No sequence loaded" });
        } else if (input.SequenceRunning) {
            items.Add(new ReadinessItem {
                Key = "sequence", Status = ReadinessStatus.Ok,
                Title = input.Waiting is { Length: > 0 } wait ? $"Sequence running, waiting {Lower(wait)}" : "Sequence running",
                Detail = input.SequenceTitle,
            });
        } else {
            items.Add(new ReadinessItem {
                Key = "sequence", Status = beforeImaging ? ReadinessStatus.Later : ReadinessStatus.Warning,
                Title = "Sequence loaded, not started", Detail = input.SequenceTitle,
            });
        }

        // --- Target Scheduler
        if (input.SchedulerInstalled && input.SchedulerTargetsTonight is int targets) {
            items.Add(targets == 0
                ? new ReadinessItem { Key = "scheduler", Status = ReadinessStatus.Warning, Title = "Target Scheduler has nothing to image tonight", Detail = "No active target is up and dark enough, or every exposure plan is complete." }
                : new ReadinessItem { Key = "scheduler", Status = ReadinessStatus.Ok, Title = targets == 1 ? "Target Scheduler: 1 target tonight" : $"Target Scheduler: {targets} targets tonight" });
        }

        // --- disk space
        if (input.FreeBytes is long free) {
            var needed = input.NeededBytes ?? 0;
            var status = free < needed * 12 / 10 || free < 2 * Gigabyte ? ReadinessStatus.Problem
                : free < 20 * Gigabyte ? ReadinessStatus.Warning : ReadinessStatus.Ok;
            items.Add(new ReadinessItem {
                Key = "storage", Status = status,
                Title = status == ReadinessStatus.Ok ? $"{Size(free)} free" : $"Only {Size(free)} free",
                Detail = needed > 0 ? $"Tonight's plan needs about {Size(needed)}." : null,
            });
        }

        // --- safety
        if (input.SafetyConnected && input.IsSafe == false) {
            var waitsForSafe = input.Waiting?.Contains("safe", StringComparison.OrdinalIgnoreCase) == true;
            items.Add(new ReadinessItem {
                Key = "safety", Status = waitsForSafe ? ReadinessStatus.Later : ReadinessStatus.Warning,
                Title = "Unsafe right now", Detail = waitsForSafe ? "The sequence waits until it's safe." : null,
            });
        }

        items.AddRange(input.SequenceFindings);

        var order = new[] { ReadinessStatus.Problem, ReadinessStatus.Warning, ReadinessStatus.Later, ReadinessStatus.Ok, ReadinessStatus.Tip };
        items = items.OrderBy(i => Array.IndexOf(order, i.Status)).ToList();
        var problems = items.Count(i => i.Status == ReadinessStatus.Problem);
        var warnings = items.Count(i => i.Status == ReadinessStatus.Warning);
        var worst = problems > 0 ? ReadinessStatus.Problem : warnings > 0 ? ReadinessStatus.Warning : ReadinessStatus.Ok;
        return new ReadinessReport {
            Phase = input.Phase,
            At = input.Now,
            Status = worst,
            Headline = Headline(input.Phase, problems, warnings, items),
            Items = items,
            PoweredBySequence = input.Power.PowersUp,
        };
    }

    private static string Headline(string phase, int problems, int warnings, List<ReadinessItem> items) {
        if (phase == ReadinessPhases.AfterPowerUp) {
            if (problems == 1 && items[0].Key.StartsWith("device.", StringComparison.Ordinal)) return items[0].Title;
            if (problems > 0) return problems == 1 ? "1 problem after power-up" : $"{problems} problems after power-up";
            return "Everything came up";
        }
        var body = problems > 0 ? (problems == 1 ? "1 problem before tonight" : $"{problems} problems before tonight")
            : warnings > 0 ? (warnings == 1 ? "ready, with 1 thing to look at" : $"ready, with {warnings} things to look at")
            : "ready for tonight";
        return phase == ReadinessPhases.LateStart ? "Started late: " + body : char.ToUpperInvariant(body[0]) + body[1..];
    }

    private static string Lower(string text) => text.Length > 0 && char.IsUpper(text[0]) && (text.Length == 1 || !char.IsUpper(text[1])) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    private static string Size(long bytes) =>
        bytes >= 1024 * Gigabyte ? $"{bytes / (1024.0 * Gigabyte):0.0} TB" : $"{Math.Max(1, bytes / Gigabyte)} GB";
}
