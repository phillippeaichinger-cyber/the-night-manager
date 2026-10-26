using NightManager.Core.Contracts;

namespace NightManager.Core.Readiness;

/// <summary>What the rig has, so the check only asks about what matters (no dithering advice without a guider).</summary>
public sealed record SequenceContext {
    public bool HasGuider { get; init; }
    public bool HasSafetyMonitor { get; init; }
    public bool HasCooledCamera { get; init; }
    /// <summary>The sequence powers its own equipment on: "not connected" warnings before then are expected.</summary>
    public bool PowersUp { get; init; }
}

/// <summary>
/// Looks over the loaded sequence for the usual gaps before a night: nothing handling meridian flips, no autofocus,
/// no cooling, nothing ending the night safely, no safety monitor in charge, no dithering. It asks "is anything
/// covering this?" rather than "is NINA's own instruction there?", so other plugins' instructions count; and when the
/// sequence uses instructions it doesn't recognise, it says so instead of claiming something's missing. It also passes
/// on the problems NINA itself shows in red. Advice only: nothing is changed.
/// </summary>
public static class SequenceCheck {
    /// <summary>Instruction families we know (namespaces of NINA and the common plugins).</summary>
    private static readonly string[] KnownPrefixes = [
        "NINA.Sequencer.", "NINA.Plugin.TargetScheduler", "WhenPlugin.", "DaleGhent.", "NINA.Joko.", "Joko.", "nina.plugin.phd2tools",
        "NightManager.", "NINA.Photometry", "Photometry.", "NINA.Plugins.", "ExposureCalculator", "ThreePointPolarAlignment",
    ];

    private sealed record Capability(string Key, string Title, string Detail, Func<SequenceContext, bool> Applies, string[] Words, string[]? Kinds = null);

    private static readonly Capability[] Capabilities = [
        new("flip", "Nothing handles meridian flips",
            "If your mount needs to flip, add a Meridian Flip trigger (inside Target Scheduler's container, if you use it).",
            _ => true, ["MeridianFlip"], [SequenceStep.Trigger, SequenceStep.Item]),
        new("autofocus", "No autofocus in the sequence",
            "Focus drifts as the night cools: an autofocus at the start and a trigger on temperature or star size keep stars tight.",
            _ => true, ["Autofocus", "AutoFocus"]),
        new("cooling", "The camera isn't cooled by the sequence",
            "A Cool Camera step near the start keeps every sub at the same temperature as your darks.",
            c => c.HasCooledCamera, ["CoolCamera", "Cool Camera"]),
        new("end", "Nothing ends the night safely",
            "Park the mount (and warm the camera) at the end, so dawn doesn't find the scope tracking into the sun.",
            _ => true, ["ParkScope", "Park", "WarmCamera", "DisconnectAll", "SafeShutdown"]),
        new("safety", "The safety monitor isn't in charge",
            "A safety condition or Wait Until Safe stops imaging (and closes up) when the weather turns.",
            c => c.HasSafetyMonitor, ["Safe", "Unsafe", "SafetyMonitor"]),
        new("dither", "No dithering",
            "Dithering every few subs moves the stars a little between subs, so stacking removes hot pixels and walking noise.",
            c => c.HasGuider, ["Dither"], [SequenceStep.Trigger, SequenceStep.Item]),
    ];

    public static IReadOnlyList<ReadinessItem> Evaluate(IReadOnlyList<SequenceStep> steps, SequenceContext context) {
        var items = new List<ReadinessItem>();
        if (steps.Count == 0) {
            return items;
        }
        var unknown = steps
            .Where(s => s.Kind != SequenceStep.Container && !KnownPrefixes.Any(p => s.TypeName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(s => string.IsNullOrWhiteSpace(s.Name) ? PowerPlan.Short(s.TypeName) : s.Name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var cap in Capabilities.Where(c => c.Applies(context))) {
            var covered = steps.Any(s => (cap.Kinds is null || cap.Kinds.Contains(s.Kind))
                && cap.Words.Any(w => s.TypeName.Contains(w, StringComparison.OrdinalIgnoreCase) || (s.Name?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)));
            if (covered) continue;
            var detail = cap.Detail;
            if (unknown.Count > 0) {
                var names = string.Join(", ", unknown.Take(3)) + (unknown.Count > 3 ? $" and {unknown.Count - 3} more" : "");
                detail += $" (Your sequence also uses {names}, which The Night Manager doesn't know: if one of these handles it, all's well.)";
            }
            items.Add(new ReadinessItem { Key = "sequence." + cap.Key, Status = ReadinessStatus.Tip, Title = cap.Title, Detail = detail });
        }

        // NINA's own red warnings. Before a sequence powers its kit on, "not connected" is expected, so it's left out.
        var problems = steps
            .Where(s => s.Issues is { Count: > 0 })
            .SelectMany(s => s.Issues!.Select(issue => (Name: string.IsNullOrWhiteSpace(s.Name) ? PowerPlan.Short(s.TypeName) : s.Name!, Issue: issue)))
            .Where(p => !(context.PowersUp && p.Issue.Contains("connect", StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
        if (problems.Count > 0) {
            items.Add(new ReadinessItem {
                Key = "sequence.issues", Status = ReadinessStatus.Warning,
                Title = problems.Count == 1 ? "NINA reports a problem in the sequence" : $"NINA reports {problems.Count} problems in the sequence",
                Detail = string.Join(" · ", problems.Take(3).Select(p => $"{p.Name}: {p.Issue}")),
            });
        }
        return items;
    }
}
