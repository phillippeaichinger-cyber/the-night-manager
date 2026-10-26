namespace NightManager.Core.Contracts;

/// <summary>
/// Past imaging from another program's records (Target Scheduler's database), so a new install's Year in Review
/// starts with everything already taken. Sent in parts; the backend stores each part under a fixed key, so sending
/// the same history again (a second NINA on the PC, "Sync history now") replaces it rather than adding to it.
/// </summary>
public sealed record HistoryImportItem : IngestItem {
    /// <summary>Where it came from: "targetScheduler".</summary>
    public required string Source { get; init; }
    /// <summary>The NINA profile the subs were taken with (the same id as a rig, when that profile is a rig).</summary>
    public required string ProfileId { get; init; }
    public required int Year { get; init; }
    public required int Part { get; init; }
    public required int Parts { get; init; }
    public IReadOnlyList<HistoryTarget> Targets { get; init; } = [];
    public IReadOnlyList<HistorySub> Subs { get; init; } = [];
}

public sealed record HistoryTarget {
    /// <summary>Key the subs refer to (the program's own target id).</summary>
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Project { get; init; }
    /// <summary>J2000, RA in hours, Dec in degrees.</summary>
    public double? Ra { get; init; }
    public double? Dec { get; init; }
}

/// <summary>One light frame, compact (a year of subs stays well under a database document).</summary>
public sealed record HistorySub {
    /// <summary>Start of the exposure, Unix seconds.</summary>
    public required long T { get; init; }
    /// <summary>Target key.</summary>
    public required string K { get; init; }
    public string? F { get; init; }
    /// <summary>Exposure, seconds.</summary>
    public double? E { get; init; }
    public double? H { get; init; }
    public double? W { get; init; }
    public int? S { get; init; }
    public double? X { get; init; }
    /// <summary>Guiding RMS, arcseconds.</summary>
    public double? R { get; init; }
    public double? A { get; init; }
    public int? G { get; init; }
    /// <summary>accept | reject | pending (Target Scheduler's own grading).</summary>
    public string? V { get; init; }
}
