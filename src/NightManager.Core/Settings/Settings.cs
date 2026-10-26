using System.Text.Json;
using NightManager.Core.Analysis;
using NightManager.Core.Commands;
using NightManager.Core.Json;

namespace NightManager.Core.Settings;

/// <summary>
/// Settings shared by every NINA instance on this PC (one PC = one observatory): where the
/// service is and which installation this is. Stored at the root of the data folder. The
/// credential is stored next to it, encrypted by the host, and is shared by all instances too.
/// </summary>
public sealed record InstallationSettings {
    /// <summary>The public testers' server (Firebase Hosting; a custom domain before public release, PLAN.md M4).</summary>
    public const string LiveServiceUrl = "https://nightmanager-77ad3.web.app";
    /// <summary>The developers' dev server: only the dev build of the plugin talks to it.</summary>
    public const string DevServiceUrl = "https://nightmanager-dev.web.app";
#if NM_DEV_CHANNEL
    public const string DefaultServiceUrl = DevServiceUrl;
    public const bool IsDevChannel = true;
#else
    public const string DefaultServiceUrl = LiveServiceUrl;
    public const bool IsDevChannel = false;
#endif

    public int SchemaVersion { get; init; } = 1;
    public string ServiceUrl { get; init; } = DefaultServiceUrl;
    /// <summary>Stable id of this PC installation, used for pairing. Generated once.</summary>
    public string InstallationId { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Syncthing on this PC: one per PC, shared by every NINA instance.</summary>
    public SyncthingSettings Syncthing { get; init; } = new();

    /// <summary>Observatory cameras: one owner per PC, like Syncthing. Passwords are stored separately, encrypted.</summary>
    public IReadOnlyList<global::NightManager.Core.Cameras.CameraSettings> Cameras { get; init; } = [];

    /// <summary>Starting NINA when this PC's user logs on, with the app's startup plan: one Launcher per PC.</summary>
    public Startup.LauncherSettings Launcher { get; init; } = new();

    /// <summary>
    /// A remote observatory (a hosted pier, a site away from home): the phone is never on this PC's network, so the
    /// local Wi-Fi link stays off and doesn't even listen. Everything goes through the server.
    /// </summary>
    public bool RemoteObservatory { get; init; }

    /// <summary>
    /// The server this build talks to. A valid custom address (the emulators on localhost) is kept; the other
    /// channel's server is swapped for this one, so installing the dev plugin moves a PC to the dev server and
    /// installing the normal plugin moves it back. (Each server has its own pairings: pair once on each.)
    /// </summary>
    public static string ChannelServiceUrl(string? url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback)) {
            return DefaultServiceUrl;
        }
        var trimmed = url!.TrimEnd('/');
        var other = IsDevChannel ? LiveServiceUrl : DevServiceUrl;
        return string.Equals(trimmed, other, StringComparison.OrdinalIgnoreCase) ? DefaultServiceUrl : trimmed;
    }

    public static InstallationSettings Load(string path) => SettingsFile.Load(path, () => new InstallationSettings()).Normalized();

    public void Save(string path) => SettingsFile.Save(path, this);

    public InstallationSettings Normalized() => this with {
        ServiceUrl = ChannelServiceUrl(ServiceUrl),
        InstallationId = string.IsNullOrWhiteSpace(InstallationId) ? Guid.NewGuid().ToString("N") : InstallationId,
        Syncthing = (Syncthing ?? new SyncthingSettings()).Normalized(),
        Cameras = (Cameras ?? []).Select(c => c.Normalized()).GroupBy(c => c.Id).Select(g => g.First()).Take(8).ToList(),
        Launcher = (Launcher ?? new Startup.LauncherSettings()).Normalized(),
    };
}

public sealed record SyncthingSettings {
    public bool Enabled { get; init; }
    public string Url { get; init; } = "http://127.0.0.1:8384";
    /// <summary>Empty: read from Syncthing's own config.xml on this PC.</summary>
    public string ApiKey { get; init; } = string.Empty;
    /// <summary>Let the app pause / resume Syncthing and change the upload window.</summary>
    public bool AllowRemoteControl { get; init; } = true;
    public Contracts.SyncWindow Window { get; init; } = new();
    /// <summary>Devices the window applies to (names or ids). Empty: every other device.</summary>
    public IReadOnlyList<string> Devices { get; init; } = [];

    public SyncthingSettings Normalized() => this with {
        Url = Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? Url.TrimEnd('/') : "http://127.0.0.1:8384",
        ApiKey = ApiKey?.Trim() ?? string.Empty,
        Window = Window is null ? new() : Window with {
            Start = Syncthing.SyncSchedule.ParseTime(Window.Start) is { } s ? s.ToString(@"hh\:mm") : "00:00",
            End = Syncthing.SyncSchedule.ParseTime(Window.End) is { } e ? e.ToString(@"hh\:mm") : "08:00",
            Outside = Window.Outside == "limit" ? "limit" : "pause",
            LimitKbps = Math.Clamp(Window.LimitKbps, 10, 1_000_000),
        },
        Devices = (Devices ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct().ToList(),
    };
}

/// <summary>
/// Optional data, each of which can be switched off in NINA (this rig's options) or by the owner in the app (the whole
/// observatory). Off means it is never collected or sent; the backend also drops it. Status, equipment, weather,
/// safety, alerts, sessions and remote control are the core and always on. Names match the backend's DATA_FEATURES.
/// </summary>
public static class DataFeatures {
    /// <summary>Each sub's figures and grade (the subs list, reports, Year in Review).</summary>
    public const string Frames = "frames";
    public const string Thumbnails = "thumbnails";
    public const string Optics = "optics";
    /// <summary>Second-by-second updates while the app is open (otherwise the app shows each check-in).</summary>
    public const string Live = "live";
    /// <summary>Every guide step for the live guiding graph (RMS still arrives with each check-in).</summary>
    public const string Guiding = "guiding";
    /// <summary>Target Scheduler's projects, plan, forecast and history.</summary>
    public const string Scheduler = "scheduler";
    /// <summary>The loaded sequence and NINA's sequencer catalog, for building plans on the phone.</summary>
    public const string Sequence = "sequence";
    public const string Cameras = "cameras";
}

/// <summary>
/// How a rig relates to its mount. A <b>main</b> rig is a setup of its own and controls the mount
/// and dome; a <b>linked</b> rig is an additional optical path on a main rig's mount (e.g. the second
/// scope of a twin setup run by its own NINA instance).
/// </summary>
public enum RigRole {
    Main,
    Linked
}

/// <summary>
/// Settings of one rig (one NINA profile). Several NINA instances on one PC, e.g. four scopes on
/// one or four mounts, each have their own rig settings, so remote actions can be allowed only on
/// the instance that actually controls the mount or dome.
/// </summary>
public sealed record RigSettings {
    public int SchemaVersion { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    /// <summary>Send each sub's figures and grade. Off: alerts and the night's totals still work, but no list of subs.</summary>
    public bool SendSubReports { get; init; } = true;
    public bool UploadThumbnails { get; init; } = true;
    /// <summary>Long edge in pixels: 600 px (~30 KB) looks the same on a phone and keeps the cloud relay cheap.</summary>
    public int ThumbnailMaxDimension { get; init; } = 600;
    public int ThumbnailQuality { get; init; } = 78;
    /// <summary>Send file names (not paths) with frame metadata. Off = names are replaced by a frame number.</summary>
    public bool SendFileNames { get; init; } = true;
    /// <summary>Measure tilt, field curvature and collimation from every light frame's stars (about half a second each).</summary>
    public bool OpticsAnalysis { get; init; } = true;
    /// <summary>Second-by-second updates while someone has the app open.</summary>
    public bool LiveUpdates { get; init; } = true;
    /// <summary>Every guide step for the app's live guiding graph.</summary>
    public bool LiveGuideGraph { get; init; } = true;
    /// <summary>Send Target Scheduler's projects, plan, forecast and history.</summary>
    public bool ShareTargetScheduler { get; init; } = true;
    /// <summary>Where the calibration library is, if not under NINA's image folder (searched for bias frames and dark flats).</summary>
    public string? CalibrationFolder { get; init; }
    /// <summary>Bandwidth (nm) of each NINA filter, as chosen in the app: labels the sub-length advice and predicts filters not imaged yet.</summary>
    public IReadOnlyDictionary<string, double> FilterBandwidths { get; init; } = new Dictionary<string, double>();
    /// <summary>How far one focuser step moves the focus, in microns (optional): tilt and curvature are then shown in microns too.</summary>
    public double? FocuserStepMicrons { get; init; }
    public GradingSensitivity GradingSensitivity { get; init; } = GradingSensitivity.Normal;
    public bool WriteCullList { get; init; } = true;
    public double? GuidingAlertArcsec { get; init; } = 2.0;
    /// <summary>Notify when the camera reaches its cooling set point.</summary>
    public bool NotifyCameraCooled { get; init; } = true;

    /// <summary>Push a notification when an autofocus run starts (tap to watch the curve).</summary>
    public bool NotifyAutofocus { get; init; } = true;

    /// <summary>Plans built on the phone may be added to this NINA's sequence or library.</summary>
    public bool AllowPlansFromApp { get; init; } = true;

    /// <summary>The phone may start the Advanced Sequence after loading a plan (off unless you tick it).</summary>
    public bool AllowStartFromApp { get; init; }
    public IReadOnlyList<string> AllowedCommands { get; init; } = CommandProcessor.DefaultAllowed.ToList();
    public RigRole Role { get; init; } = RigRole.Main;
    /// <summary>Main rigs: the setup's display name (defaults to the NINA profile name).</summary>
    public string? SetupName { get; init; }
    /// <summary>Linked rigs: the rig id (NINA profile id) of the main rig whose mount this path rides on.</summary>
    public string? MainRigId { get; init; }

    /// <summary>The optional data switched off in these settings (see <see cref="DataFeatures"/>).</summary>
    public IReadOnlyList<string> DataOff() {
        var off = new List<string>();
        if (!SendSubReports) off.Add(DataFeatures.Frames);
        if (!UploadThumbnails) off.Add(DataFeatures.Thumbnails);
        if (!OpticsAnalysis) off.Add(DataFeatures.Optics);
        if (!LiveUpdates) off.Add(DataFeatures.Live);
        if (!LiveGuideGraph) off.Add(DataFeatures.Guiding);
        if (!ShareTargetScheduler) off.Add(DataFeatures.Scheduler);
        if (!AllowPlansFromApp) off.Add(DataFeatures.Sequence);
        return off;
    }

    /// <summary>Loads the rig's settings; a new rig starts from <paramref name="seedPath"/> (e.g. pre-rig settings) when present.</summary>
    public static RigSettings Load(string path, string? seedPath = null) {
        var factory = () => seedPath is not null && File.Exists(seedPath) ? SettingsFile.Load(seedPath, () => new RigSettings()) : new RigSettings();
        return SettingsFile.Load(path, factory).Normalized();
    }

    public void Save(string path) => SettingsFile.Save(path, this);

    public RigSettings Normalized() => this with {
        ThumbnailMaxDimension = Math.Clamp(ThumbnailMaxDimension, 256, 600),
        ThumbnailQuality = Math.Clamp(ThumbnailQuality, 50, 95),
        AllowedCommands = (AllowedCommands ?? []).Distinct().ToList(),
        SetupName = string.IsNullOrWhiteSpace(SetupName) ? null : SetupName.Trim(),
        MainRigId = Role == RigRole.Linked && !string.IsNullOrWhiteSpace(MainRigId) ? MainRigId : null
    };
}

internal static class SettingsFile {
    private static readonly JsonSerializerOptions Indented = new(NmJson.Options) { WriteIndented = true };

    public static T Load<T>(string path, Func<T> createDefault) where T : class {
        try {
            if (File.Exists(path) && NmJson.Deserialize<T>(File.ReadAllText(path)) is { } loaded) {
                return loaded;
            }
        } catch (Exception) {
            // Corrupt settings: keep a copy for support and fall back to defaults.
            try {
                File.Copy(path, path + ".corrupt", overwrite: true);
            } catch (IOException) {
            }
        }

        var created = createDefault();
        Save(path, created);
        return created;
    }

    public static void Save<T>(string path, T value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Indented));
        File.Move(temporary, path, overwrite: true);
    }
}
