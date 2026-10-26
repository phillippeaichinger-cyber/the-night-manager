namespace NightManager.Core.Exposure;

/// <summary>One sub's guiding: when, how long, and its RMS (arcsec).</summary>
public readonly record struct GuidingSample(long UnixSeconds, double Seconds, double Rms);

/// <summary>
/// What this mount's guiding has sustained over the year: the median RMS, and how often guiding spikes (a gust, a
/// periodic-error peak, a lost star) spoil a sub. Spikes are treated as random events in time, so the chance a sub of
/// length t catches one is 1 − e^(−λt), with λ measured from the record. That answers "can guiding hold 10-minute
/// subs?" from the mount's own history rather than one night.
/// </summary>
public sealed record GuidingCapability {
    public required double MedianRms { get; init; }
    public required int Subs { get; init; }
    public required int Nights { get; init; }
    /// <summary>"year" (this calendar year) or "12 months" (early in the year, when the year so far is too thin).</summary>
    public required string Period { get; init; }
    /// <summary>Guiding spikes per hour of exposure.</summary>
    public required double SpikesPerHour { get; init; }
    /// <summary>The RMS above which a sub counts as spoiled by guiding.</summary>
    public required double SpikeThreshold { get; init; }

    /// <summary>The chance (0–1) that a sub of this length catches a guiding spike.</summary>
    public double LossAt(double seconds) => 1 - Math.Exp(-SpikesPerHour * seconds / 3600);

    /// <summary>The longest sub that keeps the chance of a spoiled sub at or under <paramref name="loss"/>.</summary>
    public double MaxSeconds(double loss = 0.10) => SpikesPerHour <= 0 ? double.PositiveInfinity : -Math.Log(1 - loss) / SpikesPerHour * 3600;

    public const int MinimumSubs = 40;

    /// <summary>
    /// From the record: this calendar year, or the last 12 months while the year so far has too little. A spike is a sub
    /// with RMS above 1.5 × the median, or above the image scale when that's larger (below a pixel nobody sees it).
    /// </summary>
    public static GuidingCapability? Assess(IEnumerable<GuidingSample> samples, double? imageScale, DateTimeOffset now) {
        var all = samples.Where(s => s.Rms > 0 && s.Seconds > 0 && double.IsFinite(s.Rms)).ToList();
        var yearStart = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var year = all.Where(s => s.UnixSeconds >= yearStart && s.UnixSeconds <= now.ToUnixTimeSeconds()).ToList();
        var period = "year";
        if (year.Count < MinimumSubs * 5 || CountNights(year) < 5) {
            year = all.Where(s => s.UnixSeconds >= now.AddDays(-365).ToUnixTimeSeconds()).ToList();
            period = "12 months";
        }
        if (year.Count < MinimumSubs) {
            return null;
        }
        var sorted = year.Select(s => s.Rms).OrderBy(r => r).ToList();
        var median = sorted[sorted.Count / 2];
        var threshold = Math.Max(median * 1.5, imageScale ?? 0);
        var spoiled = year.Count(s => s.Rms > threshold);
        var hours = year.Sum(s => s.Seconds) / 3600;
        // Each spoiled sub is at least one spike; λ from the share spoiled, per hour of exposure.
        var meanHours = hours / year.Count;
        var share = Math.Min(spoiled / (double)year.Count, 0.999);
        var perHour = meanHours > 0 ? -Math.Log(1 - share) / meanHours : 0;
        return new GuidingCapability {
            MedianRms = Math.Round(median, 2), Subs = year.Count, Nights = CountNights(year), Period = period,
            SpikesPerHour = Math.Round(perHour, 3), SpikeThreshold = Math.Round(threshold, 2),
        };
    }

    /// <summary>Distinct nights (a night runs noon to noon UTC, near enough for counting).</summary>
    private static int CountNights(IEnumerable<GuidingSample> samples) => samples.Select(s => (s.UnixSeconds - 43200) / 86400).Distinct().Count();
}
