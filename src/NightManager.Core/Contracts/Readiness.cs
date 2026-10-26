namespace NightManager.Core.Contracts;

/// <summary>When a readiness check ran.</summary>
public static class ReadinessPhases {
    /// <summary>About an hour before the night's first planned start, with NINA already on.</summary>
    public const string BeforeDark = "beforeDark";
    /// <summary>NINA came on after that point (or after dark): checked a few minutes after it started.</summary>
    public const string LateStart = "lateStart";
    /// <summary>The evening's equipment came up (the sequence powered it on): compared with previous nights.</summary>
    public const string AfterPowerUp = "afterPowerUp";
    /// <summary>Asked for from the app.</summary>
    public const string OnRequest = "onRequest";
}

public static class ReadinessStatus {
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Problem = "problem";
    /// <summary>Not a fault: something the sequence takes care of later (powered up at 20:01, waiting until safe).</summary>
    public const string Later = "later";
    /// <summary>A suggestion (the sequence check): shown, but not counted against being ready.</summary>
    public const string Tip = "tip";
}

/// <summary>Is the rig ready for tonight: one line per thing checked, worst first.</summary>
public sealed record ReadinessReport {
    public required string Phase { get; init; }
    public required DateTimeOffset At { get; init; }
    /// <summary>ok | warning | problem (the worst item).</summary>
    public required string Status { get; init; }
    /// <summary>One line: "Ready for tonight", "2 problems before tonight".</summary>
    public required string Headline { get; init; }
    public IReadOnlyList<ReadinessItem> Items { get; init; } = [];
    /// <summary>The loaded sequence powers the equipment on itself (switches, scripts, connect steps).</summary>
    public bool PoweredBySequence { get; init; }
}

public sealed record ReadinessItem {
    /// <summary>Stable key: "equipment", "sequence", "storage", "scheduler", "safety", "scripts", "device.camera"…</summary>
    public required string Key { get; init; }
    public required string Status { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
}
