namespace NightManager.Core.Contracts;

public static class SubLengthStatus {
    /// <summary>Read noise still adds noticeably: longer subs (or a higher gain) would give a cleaner stack.</summary>
    public const string TooShort = "tooShort";
    /// <summary>Within the target: the sky's own noise already drowns the read noise.</summary>
    public const string Good = "good";
    /// <summary>Well past what's needed: shorter subs lose almost nothing and risk less per sub.</summary>
    public const string LongerThanNeeded = "longerThanNeeded";
    /// <summary>Longer would help with read noise, but guiding or rejected subs say it's not worth the risk.</summary>
    public const string LimitedByGuiding = "limitedByGuiding";
    /// <summary>No read noise measured yet at this gain (needs a bias, dark flat or short dark from NINA).</summary>
    public const string NeedsCalibration = "needsCalibration";
}

/// <summary>
/// How long each filter's subs should be on this rig, by Dr Robin Glover's method: expose until the sky's own noise
/// drowns the camera's read noise (here: read noise adds no more than the allowed share), and no longer than guiding
/// and the rejected-sub rate make sensible. Measured from the rig's own subs, sent once a night (or on request).
/// </summary>
public sealed record ExposureAdviceItem : IngestItem {
    /// <summary>The allowed extra noise from read noise, in percent (5 by default).</summary>
    public double AllowedNoisePercent { get; init; } = 5;
    /// <summary>Arcseconds per pixel, when the focal length and pixel size are known.</summary>
    public double? ImageScale { get; init; }
    public IReadOnlyList<FilterAdvice> Filters { get; init; } = [];
    /// <summary>What the mount's guiding has sustained this year (or the last 12 months), when there's enough record.</summary>
    public GuidingRecordSummary? Guiding { get; init; }
}

public sealed record GuidingRecordSummary {
    public double MedianRms { get; init; }
    public int Subs { get; init; }
    public int Nights { get; init; }
    /// <summary>"year" or "12 months".</summary>
    public required string Period { get; init; }
    public double SpikesPerHour { get; init; }
    /// <summary>The longest sub that keeps the chance of a guiding spike at or under 10%; null when spikes are too rare to matter.</summary>
    public double? MaxSeconds { get; init; }
}

public sealed record FilterAdvice {
    public required string Filter { get; init; }
    /// <summary>Bandwidth in nm (from the filter chosen in the app), when known.</summary>
    public double? BandwidthNm { get; init; }
    public required string Status { get; init; }
    /// <summary>The sub length used most recently, seconds.</summary>
    public double CurrentSeconds { get; init; }
    public int? Gain { get; init; }
    public int Subs { get; init; }
    /// <summary>How much read noise adds to each sub's noise at the current length, percent. Null without calibration.</summary>
    public double? ReadNoisePercent { get; init; }
    /// <summary>The shortest sub that keeps read noise within the allowed share, seconds.</summary>
    public double? MinimumSeconds { get; init; }
    /// <summary>The suggestion, rounded to a sensible length.</summary>
    public double? SuggestedSeconds { get; init; }
    /// <summary>Median guiding RMS (arcsec) over the image scale: above 0.5–1 longer subs get riskier.</summary>
    public double? GuidingRatio { get; init; }
    /// <summary>The chance a sub of the suggested length catches a guiding spike, from the mount's record (0–1).</summary>
    public double? GuidingLoss { get; init; }
    /// <summary>Share of rejected subs at the current length (0–1).</summary>
    public double? RejectRate { get; init; }
    /// <summary>Predicted from another filter's sky and the bandwidths, not measured with this one.</summary>
    public bool Predicted { get; init; }
    /// <summary>One or two sentences in plain words.</summary>
    public required string Summary { get; init; }
}
