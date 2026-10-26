namespace NightManager.Core.Contracts;

/// <summary>
/// Live state of Target Scheduler on this rig, from its message-broker announcements. TS picks targets
/// dynamically and clears the sequencer's target between plans, so this is the reliable "what's happening".
/// Sent inside every snapshot (small).
/// </summary>
public sealed record SchedulerState {
    /// <summary>imaging | waiting | stopped</summary>
    public required string Status { get; init; }
    public string? ProjectName { get; init; }
    public string? TargetName { get; init; }
    /// <summary>J2000, hours.</summary>
    public double? TargetRightAscension { get; init; }
    /// <summary>J2000, degrees.</summary>
    public double? TargetDeclination { get; init; }
    public double? Rotation { get; init; }
    public string? Filter { get; init; }
    public double? ExposureSeconds { get; init; }
    public string? Gain { get; init; }
    public string? Offset { get; init; }
    public string? Binning { get; init; }
    /// <summary>When TS plans to leave the current target.</summary>
    public DateTimeOffset? PlannedEnd { get; init; }
    /// <summary>While waiting: when the next target becomes available.</summary>
    public DateTimeOffset? WaitUntil { get; init; }
    /// <summary>While waiting: the target TS is waiting for, when it says.</summary>
    public string? NextTargetName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public static class SchedulerStatus {
    public const string Imaging = "imaging";
    public const string Waiting = "waiting";
    public const string Stopped = "stopped";
}

/// <summary>
/// Target Scheduler's projects (with per-filter progress) and tonight's plan from its own planner.
/// Sent as a separate item only when it changes.
/// </summary>
public sealed record SchedulerPlanItem : IngestItem {
    public string Source { get; init; } = "targetScheduler";
    public string? SchedulerVersion { get; init; }
    /// <summary>True when tonight's plan could be read (needs TS's API enabled for this profile).</summary>
    public bool PreviewAvailable { get; init; }
    /// <summary>Why there's no plan, in words the user can act on.</summary>
    public string? PreviewUnavailableReason { get; init; }
    public DateTimeOffset? PreviewGeneratedAt { get; init; }
    public IReadOnlyList<PlanEntry> Plan { get; init; } = [];
    public IReadOnlyList<SchedulerProject> Projects { get; init; } = [];
    /// <summary>TS grades a target only once this % of its desired subs are acquired (0 = grade immediately).</summary>
    public double? DelayGradingPercent { get; init; }
    /// <summary>The NINA profile's custom horizon, altitude (°) every <see cref="HorizonStepDegrees"/>° of azimuth from north. Null: none.</summary>
    public IReadOnlyList<double>? Horizon { get; init; }

    public const int HorizonStepDegrees = 2;
}

/// <summary>One stretch of tonight's plan: a target with its exposures, or a wait.</summary>
public sealed record PlanEntry {
    public bool Wait { get; init; }
    public string? TargetId { get; init; }
    public string? TargetName { get; init; }
    public string? ProjectName { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public IReadOnlyList<PlanExposure> Exposures { get; init; } = [];
}

public sealed record PlanExposure {
    public required string Filter { get; init; }
    public double ExposureSeconds { get; init; }
    public int Count { get; init; }
}

public sealed record SchedulerProject {
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>draft | active | inactive | closed</summary>
    public required string State { get; init; }
    /// <summary>low | normal | high</summary>
    public required string Priority { get; init; }
    /// <summary>
    /// The altitude rules TS images by: with the custom horizon the limit is max(horizon(azimuth) + offset, minimum),
    /// otherwise the minimum altitude. A maximum of 0 means none.
    /// </summary>
    public double? MinimumAltitude { get; init; }
    public double? MaximumAltitude { get; init; }
    public bool? UseCustomHorizon { get; init; }
    public double? HorizonOffset { get; init; }
    public double? MinimumTimeMinutes { get; init; }
    public double? MeridianWindowMinutes { get; init; }
    public IReadOnlyList<SchedulerTarget> Targets { get; init; } = [];
}

public sealed record SchedulerTarget {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Active { get; init; }
    /// <summary>J2000, hours.</summary>
    public double RightAscension { get; init; }
    /// <summary>J2000, degrees.</summary>
    public double Declination { get; init; }
    public double Rotation { get; init; }
    public IReadOnlyList<SchedulerExposurePlan> Exposures { get; init; } = [];
}

public sealed record SchedulerExposurePlan {
    public required string Filter { get; init; }
    public double ExposureSeconds { get; init; }
    public int Desired { get; init; }
    public int Acquired { get; init; }
    public int Accepted { get; init; }
    public bool Enabled { get; init; } = true;
}


/// <summary>
/// The next nights according to Target Scheduler (one preview per night from that night's dusk), so the app still
/// shows what's coming after the PC shuts down for the day.
/// </summary>
public sealed record SchedulerForecastItem : IngestItem {
    public IReadOnlyList<ForecastNight> Nights { get; init; } = [];
}

public sealed record ForecastNight {
    /// <summary>The evening's local date, yyyy-MM-dd.</summary>
    public required string Date { get; init; }
    public DateTimeOffset? Dusk { get; init; }
    public DateTimeOffset? Dawn { get; init; }
    public double? MoonIllumination { get; init; }
    public IReadOnlyList<PlanEntry> Plan { get; init; } = [];
}
