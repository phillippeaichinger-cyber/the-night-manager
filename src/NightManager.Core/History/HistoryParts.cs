using NightManager.Core.Contracts;

namespace NightManager.Core.History;

/// <summary>Splits imported subs into upload parts: per profile and calendar year (UTC), at most <see cref="MaxSubsPerPart"/> each.</summary>
public static class HistoryParts {
    /// <summary>About 110 KB of JSON per part: comfortably inside one request and one database document.</summary>
    public const int MaxSubsPerPart = 2_000;

    public static IReadOnlyList<HistoryImportItem> Build(string source, IEnumerable<(string ProfileId, HistorySub Sub)> subs,
        IReadOnlyDictionary<string, HistoryTarget> targets, Func<string> newId, DateTimeOffset now) {
        var items = new List<HistoryImportItem>();
        foreach (var group in subs.GroupBy(s => (s.ProfileId, Year: DateTimeOffset.FromUnixTimeSeconds(s.Sub.T).UtcDateTime.Year))
                     .OrderBy(g => g.Key.ProfileId, StringComparer.Ordinal).ThenBy(g => g.Key.Year)) {
            var ordered = group.Select(s => s.Sub).OrderBy(s => s.T).ToList();
            var parts = (ordered.Count + MaxSubsPerPart - 1) / MaxSubsPerPart;
            for (var part = 0; part < parts; part++) {
                var slice = ordered.Skip(part * MaxSubsPerPart).Take(MaxSubsPerPart).ToList();
                var keys = slice.Select(s => s.K).ToHashSet();
                items.Add(new HistoryImportItem {
                    Id = newId(),
                    Timestamp = now,
                    Source = source,
                    ProfileId = group.Key.ProfileId,
                    Year = group.Key.Year,
                    Part = part,
                    Parts = parts,
                    Targets = targets.Values.Where(t => keys.Contains(t.Key)).ToList(),
                    Subs = slice,
                });
            }
        }
        return items;
    }

    /// <summary>Target Scheduler's grading status: 0 pending, 1 accepted, 2 rejected.</summary>
    public static string? Verdict(int gradingStatus) => gradingStatus switch {
        1 => "accept",
        2 => "reject",
        0 => "pending",
        _ => null,
    };
}
