namespace NightManager.Core.Startup;

/// <summary>
/// This PC's Launcher settings (in the installation settings, shared by every NINA instance): whether NINA starts when
/// Windows logs on, where NINA and its profiles are, and where else to look for sequences. Empty folders mean NINA's
/// own defaults (each profile's sequence and Templates folders).
/// </summary>
public sealed record LauncherSettings {
    /// <summary>Start NINA (with the app's startup plan) when this PC's user logs on.</summary>
    public bool Enabled { get; init; }
    /// <summary>NINA.exe; empty: the standard install folder.</summary>
    public string NinaPath { get; init; } = string.Empty;
    /// <summary>NINA's profiles; empty: %LOCALAPPDATA%\NINA\Profiles.</summary>
    public string ProfilesFolder { get; init; } = string.Empty;
    /// <summary>More folders with sequences, besides each profile's own sequence and Templates folders.</summary>
    public IReadOnlyList<string> SequenceFolders { get; init; } = [];
    /// <summary>Seconds between starting each NINA instance (master first). The app's plan can change it.</summary>
    public int DelaySeconds { get; init; } = StartupDefaults.DelaySeconds;

    public LauncherSettings Normalized() => this with {
        NinaPath = NinaPath?.Trim() ?? string.Empty,
        ProfilesFolder = ProfilesFolder?.Trim() ?? string.Empty,
        SequenceFolders = (SequenceFolders ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        DelaySeconds = Math.Clamp(DelaySeconds, StartupDefaults.MinDelaySeconds, StartupDefaults.MaxDelaySeconds),
    };
}

public static class StartupDefaults {
    public const int DelaySeconds = 60;
    public const int MinDelaySeconds = 15;
    public const int MaxDelaySeconds = 600;
    /// <summary>How long a NINA instance has to come up before it's reported as not started.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    public const string NinaFolderName = "N.I.N.A. - Nighttime Imaging 'N' Astronomy";
    /// <summary>The root of a whole Advanced Sequence (what <c>--sequencefile</c> loads); templates are other containers.</summary>
    public const string SequenceRootType = "NINA.Sequencer.Container.SequenceRootContainer";
}

/// <summary>One NINA profile on this PC, as its profile file says.</summary>
public sealed record ProfileInfo {
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Where this profile keeps sequences (NINA's "Default sequence folder").</summary>
    public string? SequenceFolder { get; init; }
    /// <summary>Where this profile keeps sequencer templates; people keep whole sequences here too.</summary>
    public string? TemplatesFolder { get; init; }
}

/// <summary>A whole Advanced Sequence file that NINA can load at start.</summary>
public sealed record SequenceFile {
    public required string Path { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

/// <summary>What's on this PC to choose from in the app: sent to the backend, shown on the Startup page.</summary>
public sealed record StartupInventory {
    public IReadOnlyList<ProfileInfo> Profiles { get; init; } = [];
    public IReadOnlyList<SequenceFile> Sequences { get; init; } = [];
    public bool NinaFound { get; init; }
    public string NinaPath { get; init; } = string.Empty;
    /// <summary>Profiles whose rig allows starting a sequence from the app (ticked in NINA on this PC).</summary>
    public IReadOnlyList<string> SequenceStartAllowed { get; init; } = [];
    public bool LauncherEnabled { get; init; }
}

/// <summary>One profile in the app's startup plan.</summary>
public sealed record PlanProfile {
    public required string ProfileId { get; init; }
    /// <summary>Start this profile's NINA when the PC starts.</summary>
    public bool Start { get; init; } = true;
    /// <summary>The sequence to load (a path on this PC), or none.</summary>
    public string? SequencePath { get; init; }
    /// <summary>Start the loaded sequence straight away (only where this PC allows it).</summary>
    public bool RunSequence { get; init; }
}

/// <summary>The startup plan saved in the app (backend document <c>observatories/{id}/startup/plan</c>).</summary>
public sealed record StartupPlan {
    public bool Enabled { get; init; }
    /// <summary>The profile that controls the mount: started first.</summary>
    public string? MasterProfileId { get; init; }
    public int? DelaySeconds { get; init; }
    public IReadOnlyList<PlanProfile> Profiles { get; init; } = [];
}

/// <summary>What the backend knows about each profile's rig at boot: imaging when contact was lost, and tonight's dawn.</summary>
public sealed record RigResumeInfo {
    public required string ProfileId { get; init; }
    /// <summary>The PC went quiet while this rig was imaging (the backend's "lostWhileImaging").</summary>
    public bool LostWhileImaging { get; init; }
    /// <summary>Astronomical dawn of the night it was imaging, if known.</summary>
    public DateTimeOffset? NightEndsAt { get; init; }
}

/// <summary>The backend's answer at boot.</summary>
public sealed record LauncherPlanResponse {
    public StartupPlan Plan { get; init; } = new();
    public IReadOnlyList<RigResumeInfo> Rigs { get; init; } = [];
}

/// <summary>One NINA instance to start (or not, and why).</summary>
public sealed record LaunchStep {
    public required string ProfileId { get; init; }
    public required string ProfileName { get; init; }
    /// <summary>Wait this long before starting (the gap after the previous instance).</summary>
    public TimeSpan DelayBefore { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? SequencePath { get; init; }
    public bool RunSequence { get; init; }
    /// <summary>Restarting a sequence after the PC went down mid-night.</summary>
    public bool Resuming { get; init; }
    /// <summary>Not started, with the reason ("already running", "profile not on this PC").</summary>
    public string? Skipped { get; init; }
    /// <summary>Something the owner should know (the sequence wasn't started, the file is missing).</summary>
    public string? Note { get; init; }
}

/// <summary>Progress sent to the backend (<c>observatories/{id}/startup/status</c>) and turned into notifications.</summary>
public sealed record LauncherStatus {
    /// <summary>booted | launching | started | failed | disabled</summary>
    public required string Phase { get; init; }
    /// <summary>boot | resume: why NINA is being started.</summary>
    public string Reason { get; init; } = "boot";
    public IReadOnlyList<InstanceStatus> Instances { get; init; } = [];
    public string? Message { get; init; }
    public string LauncherVersion { get; init; } = string.Empty;
}

public sealed record InstanceStatus {
    public required string ProfileId { get; init; }
    public required string Name { get; init; }
    /// <summary>waiting | starting | started | failed | skipped</summary>
    public required string State { get; init; }
    public string? Sequence { get; init; }
    public bool RunSequence { get; init; }
    public string? Note { get; init; }
}
