using System.Globalization;
using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Exposure;

/// <summary>One light sub, as the advisor remembers it (kept on the PC only).</summary>
public sealed record SubSample {
    public required DateTimeOffset At { get; init; }
    public required string Filter { get; init; }
    public required double Seconds { get; init; }
    /// <summary>Camera settings that change read noise: "gain|offset|bin|readout".</summary>
    public required string Setting { get; init; }
    public int? Gain { get; init; }
    /// <summary>Background noise of the sub, ADU (1σ).</summary>
    public required double NoiseAdu { get; init; }
    public double? GuidingRms { get; init; }
    public bool Rejected { get; init; }
}

/// <summary>
/// Read noise per camera setting, from bias frames, dark flats and short darks (same raw units as the subs, so the
/// camera's gain in electrons is never needed: Dr Glover's criterion is a ratio of the two noises).
/// </summary>
public sealed class ExposureHistory {
    public const int SamplesKept = 600;

    public const int GuidingKept = 20000;

    public Dictionary<string, List<double>> ReadNoise { get; init; } = [];
    public List<SubSample> Subs { get; init; } = [];
    /// <summary>Every light sub's guiding, compact ([unix seconds, exposure, RMS″]), for the mount's yearly record.</summary>
    public List<double[]> Guiding { get; init; } = [];

    public void AddGuiding(DateTimeOffset at, double seconds, double rms) {
        if (!(rms > 0) || !(seconds > 0)) return;
        Guiding.Add([at.ToUnixTimeSeconds(), Math.Round(seconds, 1), Math.Round(rms, 2)]);
        if (Guiding.Count > GuidingKept) Guiding.RemoveRange(0, Guiding.Count - GuidingKept);
    }

    public IEnumerable<GuidingSample> GuidingSamples() =>
        Guiding.Where(g => g.Length == 3).Select(g => new GuidingSample((long)g[0], g[1], g[2]));

    public static string SettingKey(int? gain, int? offset, int bin, string? readout) =>
        string.Join("|", gain?.ToString(CultureInfo.InvariantCulture) ?? "-", offset?.ToString(CultureInfo.InvariantCulture) ?? "-", bin.ToString(CultureInfo.InvariantCulture), readout ?? "");

    public void AddReadNoise(string setting, double noiseAdu) {
        if (!double.IsFinite(noiseAdu) || noiseAdu <= 0) return;
        if (!ReadNoise.TryGetValue(setting, out var list)) ReadNoise[setting] = list = [];
        list.Add(noiseAdu);
        if (list.Count > 9) list.RemoveAt(0);
    }

    /// <summary>The median of the last few measurements (one bias can be off).</summary>
    public double? ReadNoiseFor(string setting) {
        if (!ReadNoise.TryGetValue(setting, out var list) || list.Count == 0) return null;
        var sorted = list.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    public void AddSub(SubSample sample) {
        Subs.Add(sample);
        if (Subs.Count > SamplesKept) Subs.RemoveRange(0, Subs.Count - SamplesKept);
    }

    public static ExposureHistory Load(string path) {
        try {
            return File.Exists(path) ? NmJson.Deserialize<ExposureHistory>(File.ReadAllText(path)) ?? new() : new();
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
/// Dr Robin Glover's method for sub length. A sub's noise is the sky's own (shot) noise plus the camera's read noise,
/// added in quadrature. Read noise is paid once per sub, so short subs pay it often; once the sky's noise is large
/// next to it, longer subs gain almost nothing. Allowing read noise to add at most a share p of extra noise:
///   sky variance ≥ C × read variance, with C = 1 / ((1 + p)² − 1)   (p = 5% → C ≈ 9.8)
/// and as sky variance grows in proportion to time, the shortest good sub is
///   t_min = C × t × σ_read² / (σ_sub² − σ_read²)
/// with both noises measured in the same raw units at the same camera setting.
/// </summary>
public static class ExposureAdvisor {
    private static readonly double[] NiceSeconds = [5, 10, 15, 20, 30, 45, 60, 90, 120, 150, 180, 240, 300, 360, 420, 480, 600, 720, 900, 1200, 1800];

    public static double CFactor(double allowedPercent) => 1 / (Math.Pow(1 + allowedPercent / 100, 2) - 1);

    /// <summary>The extra noise read noise adds, percent: √(1 + σ_read² / σ_sky²) − 1.</summary>
    public static double ReadNoisePercent(double skyVariance, double readVariance) =>
        (Math.Sqrt(1 + readVariance / Math.Max(skyVariance, 1e-9)) - 1) * 100;

    /// <summary>The highest chance of losing a sub to a guiding spike that a longer suggestion may carry.</summary>
    public const double AcceptableGuidingLoss = 0.10;

    public static ExposureAdviceItem Advise(ExposureHistory history, IReadOnlyDictionary<string, double> bandwidths, double? imageScale,
        DateTimeOffset now, Func<string> newId, double allowedPercent = 5, GuidingCapability? guiding = null) {
        var c = CFactor(allowedPercent);
        var recent = history.Subs.Where(s => now - s.At < TimeSpan.FromDays(30)).ToList();
        var filters = new List<FilterAdvice>();

        foreach (var group in recent.GroupBy(s => s.Filter)) {
            // The setting and length used most recently with this filter.
            var last = group.MaxBy(s => s.At)!;
            var same = group.Where(s => s.Setting == last.Setting && Math.Abs(s.Seconds - last.Seconds) < 0.5).ToList();
            // The mount's record this year speaks for its guiding better than one night; recent subs when there's no record.
            var rms = guiding?.MedianRms ?? Median(same.Where(s => s.GuidingRms is > 0).Select(s => s.GuidingRms!.Value));
            double? guidingRatio = rms is double r && imageScale is double scale && scale > 0 ? r / scale : null;
            double? rejectRate = same.Count >= 5 ? same.Count(s => s.Rejected) / (double)same.Count : null;
            double? bandwidth = bandwidths.TryGetValue(group.Key, out var bw) ? bw : null;

            if (history.ReadNoiseFor(last.Setting) is not double read) {
                filters.Add(new FilterAdvice {
                    Filter = group.Key, BandwidthNm = bandwidth, Status = SubLengthStatus.NeedsCalibration, CurrentSeconds = last.Seconds,
                    Gain = last.Gain, Subs = same.Count, GuidingRatio = Round(guidingRatio, 2), RejectRate = Round(rejectRate, 3),
                    Summary = "Take a few bias frames or dark flats at this gain in NINA, so the camera's read noise can be measured.",
                });
                continue;
            }
            var noise = Median(same.Select(s => s.NoiseAdu))!.Value;
            var readVariance = read * read;
            var skyVariance = noise * noise - readVariance;
            if (same.Count < 3 || skyVariance <= readVariance * 0.05) {
                // Too few subs, or the background noise is barely above read noise: no reliable sky measurement.
                filters.Add(new FilterAdvice {
                    Filter = group.Key, BandwidthNm = bandwidth, Status = same.Count < 3 ? SubLengthStatus.NeedsCalibration : SubLengthStatus.TooShort,
                    CurrentSeconds = last.Seconds, Gain = last.Gain, Subs = same.Count, GuidingRatio = Round(guidingRatio, 2), RejectRate = Round(rejectRate, 3),
                    ReadNoisePercent = same.Count < 3 ? null : 100,
                    Summary = same.Count < 3 ? "A few more subs with this filter and the advice is ready."
                        : "Read noise is most of the noise in these subs: they're far too short for this filter (or the gain is too low).",
                });
                continue;
            }
            var percent = ReadNoisePercent(skyVariance, readVariance);
            var minimum = c * last.Seconds * readVariance / skyVariance;
            filters.Add(Decide(group.Key, bandwidth, last, same.Count, percent, minimum, guidingRatio, rejectRate, allowedPercent, guiding));
        }

        // Filters chosen in the app but not imaged yet: predicted from a measured one by bandwidth (sky ∝ bandwidth).
        var measured = filters.Where(f => f.MinimumSeconds is not null && f.BandwidthNm is > 0).ToList();
        foreach (var (name, bandwidth) in bandwidths) {
            if (filters.Any(f => f.Filter == name) || measured.Count == 0 || bandwidth <= 0) continue;
            var reference = measured.OrderBy(f => Math.Abs(Math.Log(f.BandwidthNm!.Value / bandwidth))).First();
            var minimum = reference.MinimumSeconds!.Value * reference.BandwidthNm!.Value / bandwidth;
            var suggested = Nice(minimum);
            var cap = guiding?.MaxSeconds(AcceptableGuidingLoss);
            var capped = cap is double c2 && suggested > c2;
            if (capped) suggested = Math.Max(NiceDown(cap!.Value), NiceSeconds[0]);
            filters.Add(new FilterAdvice {
                Filter = name, BandwidthNm = bandwidth, Status = SubLengthStatus.Good, CurrentSeconds = 0, Gain = reference.Gain, Subs = 0,
                MinimumSeconds = Round(minimum, 0), SuggestedSeconds = suggested, Predicted = true,
                GuidingLoss = guiding is null ? null : Round(guiding.LossAt(suggested), 3),
                Summary = capped
                    ? $"Not imaged yet. Your sky with {reference.Filter} says {Duration(Nice(minimum))}, but your mount's guiding this {guiding!.Period} holds about {Duration(suggested)} before spikes cost more than 1 sub in 10. A higher gain would make {Duration(suggested)} enough."
                    : $"Not imaged yet. From your sky with {reference.Filter} and the bandwidths, about {Duration(suggested)} at the same gain.",
            });
        }

        return new ExposureAdviceItem {
            Id = newId(), Timestamp = now, AllowedNoisePercent = allowedPercent, ImageScale = Round(imageScale, 3),
            Filters = filters.OrderBy(f => f.Predicted).ThenBy(f => f.Filter, StringComparer.OrdinalIgnoreCase).ToList(),
            Guiding = guiding is null ? null : new GuidingRecordSummary {
                MedianRms = guiding.MedianRms, Subs = guiding.Subs, Nights = guiding.Nights, Period = guiding.Period, SpikesPerHour = guiding.SpikesPerHour,
                MaxSeconds = double.IsFinite(guiding.MaxSeconds(AcceptableGuidingLoss)) ? Math.Round(guiding.MaxSeconds(AcceptableGuidingLoss)) : null,
            },
        };
    }

    private static FilterAdvice Decide(string filter, double? bandwidth, SubSample last, int subs, double percent, double minimum,
        double? guidingRatio, double? rejectRate, double allowed, GuidingCapability? guiding) {
        var current = last.Seconds;
        // Guiding coarser than a pixel elongates every sub whatever its length; many rejects say the night can't hold more.
        var guidingTight = guidingRatio is > 1.0 || rejectRate is > 0.15;
        var cap = guiding?.MaxSeconds(AcceptableGuidingLoss);
        var record = guiding is null ? "" : $"your mount's guiding this {guiding.Period} ({guiding.MedianRms:0.00}\u2033 median over {guiding.Nights} nights)";
        string status;
        double suggested;
        string summary;
        if (percent > allowed * 1.6) {
            suggested = Nice(minimum);
            if (guidingTight) {
                status = SubLengthStatus.LimitedByGuiding;
                suggested = current;
                summary = $"Read noise adds {percent:0}% at {Duration(current)}; {Duration(Nice(minimum))} would bring it to {allowed:0}%, "
                    + (guidingRatio is > 1.0 ? "but guiding is coarser than a pixel, so longer subs would only add more elongated stars. A higher gain lowers read noise instead." : "but too many subs are already rejected at this length. A higher gain lowers read noise instead.");
            } else if (cap is double c && suggested > c) {
                var held = NiceDown(c);
                if (held > current) {
                    status = SubLengthStatus.TooShort;
                    summary = $"Read noise adds {percent:0}% to every sub. {Duration(Nice(minimum))} would bring it to {allowed:0}%, but {record} holds about {Duration(held)} before spikes cost more than 1 sub in 10. {Duration(held)} is the better step; a higher gain would do the rest.";
                    suggested = held;
                } else {
                    status = SubLengthStatus.LimitedByGuiding;
                    summary = $"Read noise adds {percent:0}% at {Duration(current)}, but {record} doesn't hold longer subs without losing more than 1 in 10 to spikes. A higher gain lowers read noise instead.";
                    suggested = current;
                }
            } else {
                status = SubLengthStatus.TooShort;
                summary = $"Read noise adds {percent:0}% to every sub. About {Duration(suggested)} brings it to {allowed:0}%, for a cleaner stack in the same time."
                    + (guiding is null ? "" : $" From {record}, about {guiding.LossAt(suggested) * 100:0}% of {Duration(suggested)} subs would catch a guiding spike.");
            }
        } else if (minimum < current / 2.5 && current >= 60) {
            status = SubLengthStatus.LongerThanNeeded;
            suggested = Nice(Math.Max(minimum, current / 4));
            summary = $"The sky already drowns the read noise ({percent:0.0}% extra). {Duration(suggested)} subs would be as clean, and a satellite, gust or cloud would cost less of the night."
                + (guiding is null ? "" : $" Guiding spikes would spoil about {guiding.LossAt(suggested) * 100:0}% of them instead of {guiding.LossAt(current) * 100:0}%.");
        } else {
            status = SubLengthStatus.Good;
            suggested = current;
            summary = $"Read noise adds {percent:0.0}%: the sky's own noise is in charge. {Duration(current)} is a good length here.";
        }
        return new FilterAdvice {
            Filter = filter, BandwidthNm = bandwidth, Status = status, CurrentSeconds = current, Gain = last.Gain, Subs = subs,
            ReadNoisePercent = Round(percent, 1), MinimumSeconds = Round(minimum, 0), SuggestedSeconds = suggested,
            GuidingRatio = Round(guidingRatio, 2), RejectRate = Round(rejectRate, 3), Predicted = false,
            GuidingLoss = guiding is null ? null : Round(guiding.LossAt(suggested), 3), Summary = summary,
        };
    }

    /// <summary>The nearest sensible length at or below the given one.</summary>
    public static double NiceDown(double seconds) => NiceSeconds.LastOrDefault(n => n <= seconds * 1.05, NiceSeconds[0]);

    /// <summary>The nearest sensible length at or above the given one.</summary>
    public static double Nice(double seconds) => NiceSeconds.FirstOrDefault(n => n >= seconds * 0.95, NiceSeconds[^1]);

    private static string Duration(double seconds) => seconds >= 120 && seconds % 60 == 0 ? $"{seconds / 60:0} min" : $"{seconds:0} s";

    private static double? Median(IEnumerable<double> values) {
        var list = values.OrderBy(v => v).ToList();
        return list.Count == 0 ? null : list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }

    private static double? Round(double? value, int digits) => value is double v && double.IsFinite(v) ? Math.Round(v, digits) : null;
}
