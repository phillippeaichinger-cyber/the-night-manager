namespace NightManager.Core.Cameras;

/// <summary>Where a picture comes from.</summary>
public enum CameraSourceKind {
    /// <summary>The camera's own snapshot address (ONVIF / vendor CGI): it returns one JPEG, no video is decoded.</summary>
    Snapshot,
    /// <summary>The newest picture another program saves in a folder (an all-sky camera, an observatory's RTSP tool).</summary>
    Folder,
    /// <summary>One frame from an RTSP stream, decoded in memory by the plugin: connect, grab, disconnect.</summary>
    Rtsp,
    /// <summary>
    /// A ZWO camera plugged into this PC (not NINA's imaging or guide camera), through ZWO's driver that NINA
    /// installs: a short exposure in memory, shrunk and sent, never saved. Pier cameras only.
    /// </summary>
    Zwo
}

public enum CameraCaptureMode {
    /// <summary>Only while someone has the app open on this observatory (and on kit errors).</summary>
    WhenWatched,
    /// <summary>Every interval, whether or not anyone is watching (and on kit errors).</summary>
    Scheduled
}

/// <summary>
/// One observatory camera (PC-wide, shared by every NINA on the PC). Video never leaves the observatory: the
/// plugin sends a small still at most every <see cref="IntervalMinutes"/> (never less than three minutes).
/// The password is not stored here: it is kept encrypted by the host, per camera id.
/// Once <see cref="Locked"/>, the settings can only be changed by someone who proves they hold the camera's
/// credentials (or, for a folder, the lock passcode).
/// </summary>
public sealed record CameraSettings {
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; init; } = "Observatory camera";
    public bool Enabled { get; init; } = true;
    /// <summary>
    /// Which section it's in. All-sky: pictures the sky (usually its own all-sky software writes them), so kit errors
    /// don't force a picture from it; only the observatory turning unsafe does. Otherwise a pier camera, watching the
    /// equipment (pictures on kit errors too).
    /// </summary>
    public bool AllSky { get; init; }
    public CameraSourceKind Kind { get; init; } = CameraSourceKind.Snapshot;
    /// <summary>Snapshot URL, folder path, rtsp:// URL (without credentials), or a ZWO camera's name.</summary>
    public string Address { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public CameraCaptureMode Mode { get; init; } = CameraCaptureMode.WhenWatched;
    public int IntervalMinutes { get; init; } = CameraPolicy.MinimumIntervalMinutes;
    /// <summary>Largest picture sent, in kilobytes.</summary>
    public int MaxKilobytes { get; init; } = CameraPolicy.DefaultKilobytes;
    /// <summary>Width the picture is shrunk to before sending.</summary>
    public int Width { get; init; } = 640;
    /// <summary>ZWO: gain in daylight (null: the camera's lowest).</summary>
    public int? DayGain { get; init; }
    /// <summary>ZWO: gain after dark (null: half way up the camera's range).</summary>
    public int? NightGain { get; init; }
    /// <summary>ZWO: the longest exposure after dark, in seconds. Kept short: it's for seeing the kit, not the sky.</summary>
    public double MaxNightExposureSeconds { get; init; } = PierExposure.DefaultMaxNightSeconds;
    public bool Locked { get; init; }
    public string? LockSalt { get; init; }
    public string? LockHash { get; init; }

    public CameraSettings Normalized() => this with {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N")[..12] : new string(Id.Where(char.IsLetterOrDigit).Take(24).ToArray()),
        Name = string.IsNullOrWhiteSpace(Name) ? "Observatory camera" : Name.Trim()[..Math.Min(Name.Trim().Length, 60)],
        Address = Address?.Trim() ?? string.Empty,
        UserName = UserName?.Trim() ?? string.Empty,
        IntervalMinutes = Math.Clamp(IntervalMinutes, CameraPolicy.MinimumIntervalMinutes, 24 * 60),
        MaxKilobytes = Math.Clamp(MaxKilobytes, CameraPolicy.MinimumKilobytes, CameraPolicy.MaximumKilobytes),
        Width = Math.Clamp(Width, 320, 1024),
        // The ZWO driver is for pier cameras only: all-sky cameras keep their own software.
        AllSky = AllSky && Kind != CameraSourceKind.Zwo,
        DayGain = DayGain is int day ? Math.Max(0, day) : null,
        NightGain = NightGain is int night ? Math.Max(0, night) : null,
        MaxNightExposureSeconds = double.IsFinite(MaxNightExposureSeconds)
            ? Math.Clamp(MaxNightExposureSeconds, PierExposure.MinNightCapSeconds, PierExposure.MaxNightCapSeconds)
            : PierExposure.DefaultMaxNightSeconds,
        Locked = Locked && !string.IsNullOrEmpty(LockHash) && !string.IsNullOrEmpty(LockSalt),
    };

    /// <summary>What the phone may show about this camera (never the address or credentials).</summary>
    public CameraSummary Summary() => new() {
        Id = Id, Name = Name, Kind = Kind.ToString().ToLowerInvariant(), Mode = Mode == CameraCaptureMode.Scheduled ? "scheduled" : "watched",
        IntervalMinutes = IntervalMinutes, MaxKilobytes = MaxKilobytes, Width = Width, Locked = Locked, Enabled = Enabled,
        AllSky = AllSky,
    };
}

/// <summary>Hard limits, enforced in code: no setting can go below or above them.</summary>
public static class CameraPolicy {
    public const int MinimumIntervalMinutes = 3;
    public const int DefaultKilobytes = 40;
    public const int MinimumKilobytes = 10;
    public const int MaximumKilobytes = 100;
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(MinimumIntervalMinutes);
    /// <summary>Several kit errors in a burst give one forced picture, not one each.</summary>
    public static readonly TimeSpan AlertCoalesce = TimeSpan.FromSeconds(60);
    /// <summary>A failing camera is retried after this, not on every tick.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(1);
    /// <summary>A folder's newest picture older than this is stale (the program writing it has stopped).</summary>
    public static readonly TimeSpan FolderMaxAge = TimeSpan.FromMinutes(30);
}

/// <summary>A camera as the app sees it, inside the rig snapshot of the NINA that owns the cameras.</summary>
public sealed record CameraSummary {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Mode { get; init; }
    public int IntervalMinutes { get; init; }
    public int MaxKilobytes { get; init; }
    public int Width { get; init; }
    public bool Locked { get; init; }
    public bool Enabled { get; init; }
    public bool AllSky { get; init; }
    /// <summary>Why the last capture failed, if it did ("Camera refused the password").</summary>
    public string? Error { get; init; }
}
