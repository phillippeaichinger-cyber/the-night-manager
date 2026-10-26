using System.Text.Json;

namespace NightManager.Core.Contracts;

/// <summary>
/// A sequence plan from the phone (docs/SEQUENCE-PLANNER.md): small and NINA-version-neutral. The plugin builds
/// real Advanced Sequencer targets from it on the PC. RA in hours, Dec in degrees, J2000.
/// </summary>
public sealed record SequencePlan {
    public int V { get; init; } = 1;
    public required string Name { get; init; }
    public string? Notes { get; init; }
    /// <summary>One of the user's NINA target templates, by name; each target is built from it.</summary>
    public string? TargetTemplate { get; init; }
    public AutofocusRules? Autofocus { get; init; }
    public IReadOnlyList<PlanTarget> Targets { get; init; } = [];

    public const int MaxTargets = 30;
    public const int MaxLines = 12;

    public static SequencePlan? FromPayload(JsonElement? payload) =>
        payload is { ValueKind: JsonValueKind.Object } p ? Json.NmJson.Deserialize<SequencePlan>(p.GetRawText()) : null;

    /// <summary>Problems that stop the plan from loading, in plain words. Empty: fine.</summary>
    public IReadOnlyList<string> Problems(IReadOnlyCollection<string>? filters = null) {
        var problems = new List<string>();
        if (Targets.Count == 0) {
            problems.Add("The plan has no targets.");
        }
        if (Targets.Count > MaxTargets) {
            problems.Add($"At most {MaxTargets} targets per plan.");
        }
        foreach (var t in Targets) {
            var who = string.IsNullOrWhiteSpace(t.Name) ? "A target" : t.Name;
            if (t.Ra is < 0 or >= 24 || t.Dec is < -90 or > 90 || !double.IsFinite(t.Ra) || !double.IsFinite(t.Dec)) {
                problems.Add($"{who} has impossible coordinates.");
            }
            if (t.Exposures.Count == 0) {
                problems.Add($"{who} has no exposures.");
            }
            if (t.Exposures.Count > MaxLines) {
                problems.Add($"{who}: at most {MaxLines} exposure lines.");
            }
            foreach (var e in t.Exposures) {
                if (e.Seconds <= 0 || e.Seconds > 3600 || e.Count < 1 || e.Count > 5000) {
                    problems.Add($"{who}: {e.Count} × {e.Seconds} s isn't a valid exposure line.");
                }
                if (filters is { Count: > 0 } && !string.IsNullOrWhiteSpace(e.Filter) && !filters.Contains(e.Filter, StringComparer.OrdinalIgnoreCase)) {
                    problems.Add($"{who}: filter \"{e.Filter}\" isn't in this NINA's filter wheel ({string.Join(", ", filters)}).");
                }
            }
        }
        return problems.Distinct().ToList();
    }

    public double TotalSeconds => Targets.Sum(t => t.Exposures.Sum(e => e.Seconds * e.Count));
}

public sealed record PlanTarget {
    public required string Name { get; init; }
    public double Ra { get; init; }
    public double Dec { get; init; }
    /// <summary>Position angle in degrees, when a rotator is used.</summary>
    public double? Rotation { get; init; }
    public IReadOnlyList<ExposureLine> Exposures { get; init; } = [];
}

public sealed record ExposureLine {
    public string? Filter { get; init; }
    public double Seconds { get; init; }
    public int Count { get; init; }
    public int? Gain { get; init; }
    public int? Offset { get; init; }
    public int? Bin { get; init; }
    /// <summary>Dither every N subs; 0 = never; null = the template's default.</summary>
    public int? DitherEvery { get; init; }
}

public sealed record AutofocusRules {
    public bool? OnFilterChange { get; init; }
    public double? EveryMinutes { get; init; }
    public double? TemperatureDelta { get; init; }
}

public static class LoadModes {
    /// <summary>NINA's Targets library (sidebar): nothing in the sequence changes. Allowed while imaging.</summary>
    public const string Library = "library";
    /// <summary>At the end of the targets; runs when reached, even if the sequence is running.</summary>
    public const string Append = "append";
    /// <summary>Straight after the target running now.</summary>
    public const string Next = "next";
    /// <summary>Clears the targets (start and end blocks stay) after backing the sequence up. Not while running.</summary>
    public const string Replace = "replace";

    public static readonly IReadOnlyList<string> All = [Library, Append, Next, Replace];
}
