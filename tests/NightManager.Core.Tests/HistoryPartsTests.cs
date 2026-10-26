using NightManager.Core.Contracts;
using NightManager.Core.History;
using NUnit.Framework;

namespace NightManager.Core.Tests;

public class HistoryPartsTests {
    private static HistorySub Sub(DateTimeOffset at, string target) => new() { T = at.ToUnixTimeSeconds(), K = target, F = "Ha", E = 300 };

    [Test]
    public void Splits_By_Profile_And_Year_And_Keeps_Parts_Small() {
        var start = new DateTimeOffset(2025, 12, 31, 20, 0, 0, TimeSpan.Zero);
        var subs = Enumerable.Range(0, HistoryParts.MaxSubsPerPart + 10)
            .Select(i => ("p1", Sub(start.AddMinutes(i), i % 2 == 0 ? "1" : "2")))
            .Append(("p2", Sub(start, "3")))
            .ToList();
        var targets = new Dictionary<string, HistoryTarget> {
            ["1"] = new() { Key = "1", Name = "M31" }, ["2"] = new() { Key = "2", Name = "M33" }, ["3"] = new() { Key = "3", Name = "IC 1396" },
        };
        var id = 0;
        var items = HistoryParts.Build("targetScheduler", subs, targets, () => $"id{id++}", start);

        Assert.That(items.All(i => i.Subs.Count <= HistoryParts.MaxSubsPerPart));
        Assert.That(items.Sum(i => i.Subs.Count), Is.EqualTo(subs.Count));
        // 2025 (the first 240 minutes up to midnight) and 2026 for p1, 2025 for p2.
        Assert.That(items.Select(i => (i.ProfileId, i.Year)).Distinct().Count(), Is.EqualTo(3));
        Assert.That(items.Where(i => i.ProfileId == "p1" && i.Year == 2026).Select(i => i.Parts).Distinct().Single(), Is.EqualTo(1));
        // Each part lists only the targets its subs use.
        var p2 = items.Single(i => i.ProfileId == "p2");
        Assert.That(p2.Targets.Select(t => t.Name), Is.EqualTo(new[] { "IC 1396" }));
    }

    [Test]
    public void Maps_Target_Scheduler_Grading() {
        Assert.That(HistoryParts.Verdict(1), Is.EqualTo("accept"));
        Assert.That(HistoryParts.Verdict(2), Is.EqualTo("reject"));
        Assert.That(HistoryParts.Verdict(0), Is.EqualTo("pending"));
    }
}
