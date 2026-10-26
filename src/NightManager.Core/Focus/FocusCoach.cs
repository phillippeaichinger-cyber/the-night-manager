using System.Globalization;
using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Focus;

/// <summary>One autofocus run, as the focus coach remembers it (kept on the PC).</summary>
public sealed record FocusRun {
    public required long At { get; init; }
    public required string Filter { get; init; }
    public required double Temperature { get; init; }
    public required double Position { get; init; }
    public double? Hfr { get; init; }
    /// <summary>How far focus can be off (steps) before stars grow 5%, from this run's V-curve.</summary>
    public double? ToleranceSteps { get; init; }
}

/// <summary>Every autofocus run on this rig: imported once from NINA's autofocus reports, then added as runs finish.</summary>
public sealed class FocusHistory {
    public const int Kept = 3000;

    public List<FocusRun> Runs { get; init; } = [];

    /// <summary>A run from NINA's report; false when it's already known or unusable (no temperature, no result).</summary>
    public bool Add(AutofocusResult result) {
        if (result.Timestamp is not { } at || result.Temperature is not double t || !double.IsFinite(t) || result.Final is not { } final || final.Position <= 0) {
            return false;
        }
        var unix = at.ToUnixTimeSeconds();
        if (Runs.Any(r => Math.Abs(r.At - unix) < 30)) {
            return false;
        }
        Runs.Add(new FocusRun {
            At = unix, Filter = string.IsNullOrWhiteSpace(result.Filter) ? "No filter" : result.Filter!.Trim(), Temperature = Math.Round(t, 2),
            Position = Math.Round(final.Position, 1), Hfr = result.FinalHfr ?? final.Value, ToleranceSteps = Tolerance(result.Points),
        });
        Runs.Sort((a, b) => a.At.CompareTo(b.At));
        if (Runs.Count > Kept) Runs.RemoveRange(0, Runs.Count - Kept);
        return true;
    }

    /// <summary>
    /// The focuser travel that makes stars 5% bigger than at best focus: a parabola through the V-curve's lowest points
    /// (HFR ≈ m + k(x − p)², so Δ = √(0.05·m / k)).
    /// </summary>
    public static double? Tolerance(IReadOnlyList<FocusPoint> points) {
        var lowest = points.Where(p => p.Value > 0).OrderBy(p => p.Value).Take(5).ToList();
        if (lowest.Count < 4) return null;
        var x0 = lowest.Average(p => p.Position);
        double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        foreach (var p in lowest) {
            var x = p.Position - x0;
            s0 += 1; s1 += x; s2 += x * x; s3 += x * x * x; s4 += x * x * x * x;
            t0 += p.Value; t1 += x * p.Value; t2 += x * x * p.Value;
        }
        // Normal equations for y = a + b x + c x².
        var m = new[,] { { s0, s1, s2, t0 }, { s1, s2, s3, t1 }, { s2, s3, s4, t2 } };
        for (var i = 0; i < 3; i++) {
            var pivot = m[i, i];
            if (Math.Abs(pivot) < 1e-12) return null;
            for (var j = i; j < 4; j++) m[i, j] /= pivot;
            for (var r = 0; r < 3; r++) {
                if (r == i) continue;
                var f = m[r, i];
                for (var j = i; j < 4; j++) m[r, j] -= f * m[i, j];
            }
        }
        double a = m[0, 3], b = m[1, 3], c = m[2, 3];
        if (c <= 0) return null;
        var min = a - b * b / (4 * c);
        if (min <= 0) return null;
        var delta = Math.Sqrt(0.05 * min / c);
        var span = points.Max(p => p.Position) - points.Min(p => p.Position);
        return delta > 0 && delta < span ? Math.Round(delta, 1) : null;
    }

    public static FocusHistory Load(string path) {
        try {
            return File.Exists(path) ? NmJson.Deserialize<FocusHistory>(File.ReadAllText(path)) ?? new() : new();
        } catch (Exception) {
            return new();
        }
    }

    public void Save(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, NmJson.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>
/// The focus coach. Like a wellness ring, it learns first: it needs a week of nights and runs across a real spread of
/// temperatures before it says anything, and until then it reports what it's still waiting for. Then, from the whole
/// record: how far focus moves per °C (a robust fit that shrugs off a bad run), each filter's offset, how much cooling
/// it takes before stars swell (from the V-curves), how often that means refocusing on a typical night, and the night
/// by night trend, so a sudden shift (spacing, a camera moved) stands out from the drift of the seasons.
/// </summary>
public static class FocusCoach {
    public const int NightsNeeded = 7;
    public const int RunsNeeded = 10;
    public const double SpanNeeded = 4;

    private static string NightOf(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix - 12 * 3600).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static FocusAdviceItem Assess(IReadOnlyList<FocusRun> all, DateTimeOffset now, Func<string> newId) {
        var runs = all.Where(r => now.ToUnixTimeSeconds() - r.At < 400L * 86400).ToList();
        var nights = runs.Select(r => NightOf(r.At)).Distinct().Count();
        var byFilter = runs.GroupBy(r => r.Filter).OrderByDescending(g => g.Count()).ToList();
        var main = byFilter.FirstOrDefault();
        var span = main is null ? 0 : main.Max(r => r.Temperature) - main.Min(r => r.Temperature);
        var mainRuns = main?.Count() ?? 0;
        var progress = Math.Min(1, Math.Min(Math.Min(nights / (double)NightsNeeded, mainRuns / (double)RunsNeeded), span / SpanNeeded));
        var ready = nights >= NightsNeeded && mainRuns >= RunsNeeded && span >= SpanNeeded;

        var item = new FocusAdviceItem {
            Id = newId(), Timestamp = now, Status = ready ? FocusAdviceStatus.Ready : FocusAdviceStatus.Learning,
            Nights = nights, NightsNeeded = NightsNeeded, Runs = runs.Count, MainFilter = main?.Key, MainFilterRuns = mainRuns, RunsNeeded = RunsNeeded,
            TemperatureSpan = Math.Round(span, 1), SpanNeeded = SpanNeeded, Progress = Math.Round(progress, 2),
        };
        if (!ready || main is null) {
            return item with { Messages = [Waiting(nights, mainRuns, span, main?.Key)] };
        }

        // Per filter: robust slope (median of pairwise slopes across ≥ 0.5 °C) and the line's position at 0 °C.
        var fits = new Dictionary<string, (double Slope, double Intercept, int Runs)>();
        foreach (var group in byFilter) {
            var list = group.ToList();
            var slopes = new List<double>();
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                    if (Math.Abs(list[j].Temperature - list[i].Temperature) >= 0.5)
                        slopes.Add((list[j].Position - list[i].Position) / (list[j].Temperature - list[i].Temperature));
            fits[group.Key] = (slopes.Count >= 3 ? Median(slopes) : double.NaN, double.NaN, list.Count);
        }
        // The coefficient: the main filter's, steadied by the others that have enough runs.
        var coefficient = Median(fits.Where(f => f.Value.Runs >= 5 && double.IsFinite(f.Value.Slope)).Select(f => f.Value.Slope).DefaultIfEmpty(fits[main.Key].Slope));
        foreach (var key in fits.Keys.ToList()) {
            var list = byFilter.First(g => g.Key == key).ToList();
            fits[key] = (fits[key].Slope, Median(list.Select(r => r.Position - coefficient * r.Temperature)), list.Count);
        }
        var reference = fits[main.Key].Intercept;
        var offsets = fits.Where(f => f.Key != main.Key && f.Value.Runs >= 3)
            .Select(f => new FocusOffset { Filter = f.Key, Steps = Math.Round(f.Value.Intercept - reference), Runs = f.Value.Runs })
            .OrderBy(o => o.Filter, StringComparer.OrdinalIgnoreCase).ToList();

        var tolerance = Median(runs.Where(r => r.ToleranceSteps is > 0).Select(r => r.ToleranceSteps!.Value).DefaultIfEmpty(double.NaN));
        double? refocus = double.IsFinite(tolerance) && Math.Abs(coefficient) > 0.01 ? Math.Max(0.3, Math.Floor(tolerance / Math.Abs(coefficient) * 10) / 10) : null;

        // Night by night: runs, the drop in temperature, star size, and how far focus sat from the baseline line.
        var nightly = runs.GroupBy(r => NightOf(r.At)).OrderBy(g => g.Key).TakeLast(60).Select(g => new FocusNight {
            Date = g.Key,
            Runs = g.Count(),
            TemperatureDrop = Math.Round(g.Max(r => r.Temperature) - g.Min(r => r.Temperature), 1),
            Hfr = Round(Median(g.Where(r => r.Hfr is > 0).Select(r => r.Hfr!.Value).DefaultIfEmpty(double.NaN)), 2),
            Residual = Round(Median(g.Select(r => r.Position - (fits[r.Filter].Intercept + coefficient * r.Temperature))), 0),
        }).ToList();
        var typicalDrop = Median(nightly.Where(n => n.Runs >= 2).Select(n => n.TemperatureDrop).DefaultIfEmpty(double.NaN));
        var runsPerNight = Median(nightly.Select(n => (double)n.Runs));
        double? suggestedRuns = refocus is double r2 && double.IsFinite(typicalDrop) ? Math.Max(1, Math.Ceiling(typicalDrop / r2) + 1) : null;

        var messages = new List<string>();
        if (refocus is double degrees) {
            messages.Add($"Focus moves about {Math.Abs(coefficient):0.#} steps per °C, and stars start to swell after about {tolerance:0} steps: refocus every {degrees:0.#} °C of cooling.");
            if (suggestedRuns is double s && runsPerNight > s + 1) {
                messages.Add($"Your nights usually cool {typicalDrop:0.#} °C. With NINA's \"Autofocus after temperature change\" set to {degrees:0.#} °C that's about {s:0} runs a night, against your {runsPerNight:0} now.");
            }
        }
        if (offsets.Count > 0 && double.IsFinite(tolerance)) {
            var small = offsets.Where(o => Math.Abs(o.Steps) <= tolerance).Select(o => o.Filter).ToList();
            if (small.Count == offsets.Count) {
                messages.Add($"Your filters focus within {tolerance:0} steps of {main.Key}: autofocus on a filter change isn't needed.");
            } else {
                messages.Add($"Enter the filter offsets in NINA (Options → Equipment → Filter wheel), then autofocus on {main.Key} only.");
            }
        }
        // A sudden shift: the last two nights sit well away from where focus has been.
        var residuals = nightly.Where(n => n.Residual is not null).ToList();
        if (residuals.Count >= NightsNeeded + 2 && double.IsFinite(tolerance)) {
            var recent = Median(residuals.TakeLast(2).Select(n => n.Residual!.Value));
            var before = Median(residuals.SkipLast(2).Select(n => n.Residual!.Value));
            if (Math.Abs(recent - before) > 3 * tolerance) {
                messages.Add($"Focus has shifted about {Math.Abs(recent - before):0} steps since {residuals[^2].Date}, more than temperature explains: something in the imaging train may have moved (spacing, camera, a filter).");
            }
        }

        return item with {
            Coefficient = Math.Round(coefficient, 1), ToleranceSteps = Round(tolerance, 0), RefocusDegrees = refocus,
            TypicalNightDrop = Round(typicalDrop, 1), RunsPerNight = Math.Round(runsPerNight, 1), SuggestedRunsPerNight = suggestedRuns,
            Offsets = offsets, Nightly = nightly, Messages = messages,
        };
    }

    private static string Waiting(int nights, int runs, double span, string? filter) {
        var needs = new List<string>();
        if (nights < NightsNeeded) needs.Add($"{NightsNeeded - nights} more night{(NightsNeeded - nights == 1 ? "" : "s")} with autofocus");
        if (runs < RunsNeeded) needs.Add($"{RunsNeeded - runs} more run{(RunsNeeded - runs == 1 ? "" : "s")}{(filter is null ? "" : $" on {filter}")}");
        if (span < SpanNeeded) needs.Add($"runs across {SpanNeeded - span:0.#} °C more of temperature");
        return "Learning your focuser. Still needs " + string.Join(", ", needs) + ": a baseline from many nights is worth more than one night's guess.";
    }

    private static double Median(IEnumerable<double> values) {
        var list = values.Where(double.IsFinite).OrderBy(v => v).ToList();
        return list.Count == 0 ? double.NaN : list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }

    private static double? Round(double value, int digits) => double.IsFinite(value) ? Math.Round(value, digits) : null;
}
