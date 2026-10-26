namespace NightManager.Core.Contracts;

public static class FocusAdviceStatus {
    /// <summary>Still building the baseline: not enough nights, runs or temperature range to say anything yet.</summary>
    public const string Learning = "learning";
    public const string Ready = "ready";
}

/// <summary>
/// The focus coach's view of this rig, from every autofocus run it has seen (a baseline, not one night). Sent once a
/// night (or on request): `rigs/{rig}/advice/focus`.
/// </summary>
public sealed record FocusAdviceItem : IngestItem {
    public required string Status { get; init; }
    public int Nights { get; init; }
    public int NightsNeeded { get; init; }
    public int Runs { get; init; }
    public string? MainFilter { get; init; }
    public int MainFilterRuns { get; init; }
    public int RunsNeeded { get; init; }
    /// <summary>Temperature range covered by the main filter's runs, °C.</summary>
    public double TemperatureSpan { get; init; }
    public double SpanNeeded { get; init; }
    /// <summary>How far through learning (0–1).</summary>
    public double Progress { get; init; }
    /// <summary>Focuser steps per °C (sign: the direction focus moves as it cools is the negative of this).</summary>
    public double? Coefficient { get; init; }
    /// <summary>Steps of defocus before stars grow 5%.</summary>
    public double? ToleranceSteps { get; init; }
    /// <summary>Degrees of cooling between refocuses.</summary>
    public double? RefocusDegrees { get; init; }
    public double? TypicalNightDrop { get; init; }
    public double? RunsPerNight { get; init; }
    public double? SuggestedRunsPerNight { get; init; }
    public IReadOnlyList<FocusOffset> Offsets { get; init; } = [];
    public IReadOnlyList<FocusNight> Nightly { get; init; } = [];
    public IReadOnlyList<string> Messages { get; init; } = [];
}

public sealed record FocusOffset {
    public required string Filter { get; init; }
    /// <summary>Steps from the main filter at the same temperature.</summary>
    public double Steps { get; init; }
    public int Runs { get; init; }
}

public sealed record FocusNight {
    /// <summary>The evening's date (yyyy-MM-dd).</summary>
    public required string Date { get; init; }
    public int Runs { get; init; }
    public double TemperatureDrop { get; init; }
    public double? Hfr { get; init; }
    /// <summary>How far focus sat from the baseline line that night (steps): a jump means something moved.</summary>
    public double? Residual { get; init; }
}
