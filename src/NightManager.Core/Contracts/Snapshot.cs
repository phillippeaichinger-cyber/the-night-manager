using NightManager.Core.Analysis;

namespace NightManager.Core.Contracts;

// Units used throughout API v2:
//   RA in hours (J2000), Dec/Alt/Az/angles in degrees, temperatures in °C,
//   wind speed in m/s (ASCOM convention), pressure in hPa, durations in seconds,
//   all timestamps UTC. Null always means "unknown / not reported", never zero.

/// <summary>Point-in-time state of the whole observatory. Sent coalesced: only the latest one matters.</summary>
public sealed record ObservatorySnapshot {
    public CameraState? Camera { get; init; }
    public MountState? Mount { get; init; }
    public GuiderState? Guider { get; init; }
    public FocuserState? Focuser { get; init; }
    public FilterWheelState? FilterWheel { get; init; }
    public RotatorState? Rotator { get; init; }
    public WeatherState? Weather { get; init; }
    public SafetyState? Safety { get; init; }
    public DomeState? Dome { get; init; }
    public FlatDeviceState? FlatDevice { get; init; }
    /// <summary>Power boxes and other switches (the app's power toggles).</summary>
    public SwitchesState? Switches { get; init; }
    /// <summary>What the phone asked the equipment to do and how it's going (docs/CONTROL.md).</summary>
    public ControlState? Control { get; init; }
    public SequenceState? Sequence { get; init; }
    /// <summary>Target Scheduler's live state, when TS is installed and has announced something.</summary>
    public SchedulerState? Scheduler { get; init; }
    /// <summary>The current (or just finished) autofocus run, for the app's live autofocus view.</summary>
    public AutofocusState? Autofocus { get; init; }
    public NightState? Night { get; init; }
    public SessionSummary? Session { get; init; }
    /// <summary>Questions, alarms, pause and finish requests from the sequence's Night Manager instructions.</summary>
    public PhoneState? Phone { get; init; }
    /// <summary>Syncthing on this PC (only the NINA instance that owns the Syncthing link reports it).</summary>
    public SyncthingState? Syncthing { get; init; }
    /// <summary>Free space where NINA saves images.</summary>
    public StorageState? Storage { get; init; }
    /// <summary>What NINA is doing right now, as its status bar shows it ("Idle", "Waiting for dark", "Slewing").</summary>
    public NinaActivity? Activity { get; init; }
    /// <summary>Tonight's latest readiness check.</summary>
    public ReadinessReport? Readiness { get; init; }
    /// <summary>Observatory cameras (only the NINA instance that owns them reports them; Pro).</summary>
    public IReadOnlyList<global::NightManager.Core.Cameras.CameraSummary>? Cameras { get; init; }
}

public static class ActivityStates {
    public const string Idle = "idle";
    public const string Imaging = "imaging";
    public const string Waiting = "waiting";
    public const string Busy = "busy";
}

/// <summary>
/// NINA's status bar, mirrored. Countdowns are sent as an end time, not as ticking text, so the app counts down
/// on its own and the snapshot only changes when NINA moves on to something else.
/// </summary>
public sealed record NinaActivity {
    /// <summary>idle | imaging | waiting | busy</summary>
    public required string State { get; init; }
    /// <summary>One line for a card: "Idle", "Waiting for dark", "Centering", "Imaging M 31".</summary>
    public required string Headline { get; init; }
    /// <summary>NINA's own words when they add something ("Sun altitude -9.4° · waiting for < -12°").</summary>
    public string? Detail { get; init; }
    /// <summary>When the current wait or step ends, when known.</summary>
    public DateTimeOffset? Until { get; init; }
    /// <summary>Progress of the current step, 0–1, in 5% steps.</summary>
    public double? Progress { get; init; }
    /// <summary>Every line of NINA's status bar.</summary>
    public IReadOnlyList<ActivityLine>? Lines { get; init; }
}

public sealed record ActivityLine {
    /// <summary>Who reports it, in NINA's words: "Sequence", "Camera", "Telescope", "Autofocus"…</summary>
    public required string Source { get; init; }
    /// <summary>The status without its ticking timer: "Waiting", "Exposing", "Settle".</summary>
    public required string Text { get; init; }
    public string? Detail { get; init; }
    public double? Progress { get; init; }
    /// <summary>Count-style progress in words, e.g. "3 / 10".</summary>
    public string? Count { get; init; }
    public DateTimeOffset? Until { get; init; }
}

public static class SyncthingStatus {
    public const string Syncing = "syncing";
    public const string Idle = "idle";
    public const string Paused = "paused";
    public const string Limited = "limited";
    /// <summary>No other device connected.</summary>
    public const string Offline = "offline";
    public const string Unreachable = "unreachable";
}

public sealed record SyncthingState {
    /// <summary>syncing | idle | paused | limited | offline | unreachable</summary>
    public required string Status { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }
    /// <summary>Paused or limited because it's outside the upload window (not by hand).</summary>
    public bool BySchedule { get; init; }
    /// <summary>"Sync now" from the app keeps syncing until then, whatever the window says.</summary>
    public DateTimeOffset? OverrideUntil { get; init; }
    /// <summary>Paused from the app until then (the next window change).</summary>
    public DateTimeOffset? PausedUntil { get; init; }
    public SyncWindow? Window { get; init; }
    /// <summary>When the window next opens or closes.</summary>
    public DateTimeOffset? NextChange { get; init; }
    public double? UploadBytesPerSecond { get; init; }
    public double? DownloadBytesPerSecond { get; init; }
    /// <summary>The most any connected device still needs from this PC.</summary>
    public long ToUploadBytes { get; init; }
    /// <summary>What this PC still needs to receive.</summary>
    public long ToDownloadBytes { get; init; }
    public IReadOnlyList<SyncthingDevice> Devices { get; init; } = [];
    public IReadOnlyList<SyncthingFolder> Folders { get; init; } = [];
    /// <summary>The owner allowed pause / resume / window changes from the app (NINA options).</summary>
    public bool ControlAllowed { get; init; }
}

public sealed record SyncWindow {
    public bool Enabled { get; init; }
    /// <summary>Local time of the observatory PC, "HH:mm". May cross midnight (e.g. 00:00–08:00 or 22:00–06:00).</summary>
    public string Start { get; init; } = "00:00";
    public string End { get; init; } = "08:00";
    /// <summary>Outside the window: pause | limit</summary>
    public string Outside { get; init; } = "pause";
    /// <summary>Upload limit outside the window when <see cref="Outside"/> is "limit".</summary>
    public int LimitKbps { get; init; } = 1000;
}

public sealed record SyncthingDevice {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool Connected { get; init; }
    public bool Paused { get; init; }
    /// <summary>0–100: how complete this device is with what we share.</summary>
    public double? Completion { get; init; }
    public long NeedBytes { get; init; }
    public long NeedItems { get; init; }
}

public sealed record SyncthingFolder {
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>Syncthing's folder state: idle, scanning, syncing, sync-waiting, error…</summary>
    public string? State { get; init; }
    public bool Paused { get; init; }
    public long GlobalBytes { get; init; }
    public long NeedBytes { get; init; }
    public int Errors { get; init; }
}

public sealed record StorageState {
    /// <summary>NINA's image folder.</summary>
    public required string Path { get; init; }
    public long FreeBytes { get; init; }
    public long TotalBytes { get; init; }
    /// <summary>Bytes NINA saved since the session started (to estimate nights left).</summary>
    public long SessionBytes { get; init; }
    /// <summary>Every drive on the imaging PC (local disks and mapped network drives).</summary>
    public IReadOnlyList<DriveSpace> Drives { get; init; } = [];
}

public sealed record DriveSpace {
    /// <summary>e.g. "C:" or "\\nas\astro".</summary>
    public required string Name { get; init; }
    public string? Label { get; init; }
    public long FreeBytes { get; init; }
    public long TotalBytes { get; init; }
    /// <summary>NINA saves images here.</summary>
    public bool Images { get; init; }
    /// <summary>Windows is installed here.</summary>
    public bool System { get; init; }
    public bool Network { get; init; }
}

public sealed record PhoneState {
    public bool Paused { get; init; }
    /// <summary>The sequence reached a Pause point and is holding there.</summary>
    public bool HoldingAtPause { get; init; }
    public bool FinishRequested { get; init; }
    public IReadOnlyList<PhonePrompt> Prompts { get; init; } = [];
    public IReadOnlyList<PhoneAlarm> Alarms { get; init; } = [];
}

public sealed record PhonePrompt {
    public required string Id { get; init; }
    /// <summary>ask | go</summary>
    public required string Kind { get; init; }
    public required string Question { get; init; }
    public string? Detail { get; init; }
    public required string Yes { get; init; }
    public string? No { get; init; }
    public DateTimeOffset AskedAt { get; init; }
    public DateTimeOffset? TimeoutAt { get; init; }
    /// <summary>What happens on timeout: yes | no | go.</summary>
    public required string DefaultAnswer { get; init; }
}

public sealed record PhoneAlarm {
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset RaisedAt { get; init; }
}

public sealed record CameraState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    /// <summary>idle | waiting | exposing | reading | download | error | disconnected</summary>
    public string? State { get; init; }
    public double? Temperature { get; init; }
    public double? TargetTemperature { get; init; }
    public bool? CoolerOn { get; init; }
    public double? CoolerPower { get; init; }
    public int? Gain { get; init; }
    public int? Offset { get; init; }
    public int? BinX { get; init; }
    public int? BinY { get; init; }
    public string? ReadoutMode { get; init; }
    public bool? DewHeaterOn { get; init; }
    public int? BatteryLevel { get; init; }
    /// <summary>While exposing: when this exposure began and when NINA expects it to end (drives live countdowns).</summary>
    public DateTimeOffset? ExposureStartedAt { get; init; }
    public DateTimeOffset? ExposureEndsAt { get; init; }
}

public sealed record MountState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public double? RightAscension { get; init; }
    public double? Declination { get; init; }
    public double? Altitude { get; init; }
    public double? Azimuth { get; init; }
    public bool? Tracking { get; init; }
    public string? TrackingRate { get; init; }
    public bool? Slewing { get; init; }
    public bool? AtPark { get; init; }
    public bool? AtHome { get; init; }
    /// <summary>east | west | unknown</summary>
    public string? SideOfPier { get; init; }
    public double? HoursToMeridian { get; init; }
    public double? SiderealTime { get; init; }
}

public sealed record GuiderState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    /// <summary>guiding | idle | disconnected (NINA does not expose finer PHD2 states through the mediator).</summary>
    public string? State { get; init; }
    public double? RmsRa { get; init; }
    public double? RmsDec { get; init; }
    public double? RmsTotal { get; init; }
    public double? PeakRa { get; init; }
    public double? PeakDec { get; init; }
    public double? PixelScale { get; init; }
}

public sealed record FocuserState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public int? Position { get; init; }
    public double? Temperature { get; init; }
    public bool? Moving { get; init; }
    public bool? TempComp { get; init; }
}

public sealed record FilterWheelState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public string? CurrentFilter { get; init; }
    public IReadOnlyList<string>? Filters { get; init; }
}

public sealed record RotatorState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public double? Position { get; init; }
    public double? MechanicalPosition { get; init; }
    public bool? Moving { get; init; }
}

public sealed record WeatherState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public double? Temperature { get; init; }
    public double? Humidity { get; init; }
    public double? DewPoint { get; init; }
    public double? Pressure { get; init; }
    public double? WindSpeed { get; init; }
    public double? WindGust { get; init; }
    public double? WindDirection { get; init; }
    public double? CloudCover { get; init; }
    public double? RainRate { get; init; }
    public double? SkyBrightness { get; init; }
    public double? SkyQuality { get; init; }
    public double? SkyTemperature { get; init; }
}

public sealed record SafetyState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public bool? IsSafe { get; init; }
}

public sealed record DomeState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    /// <summary>open | closed | opening | closing | error | unknown</summary>
    public string? Shutter { get; init; }
    public double? Azimuth { get; init; }
    public bool? Slewing { get; init; }
    public bool? AtPark { get; init; }
    public bool? FollowingScope { get; init; }
}

public sealed record FlatDeviceState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public string? CoverState { get; init; }
    public bool? LightOn { get; init; }
    public int? Brightness { get; init; }
}

public sealed record SequenceState {
    public bool Running { get; init; }
    public string? Title { get; init; }
    public string? TargetName { get; init; }
    /// <summary>Target coordinates from the running target container (not the mount position).</summary>
    public double? TargetRightAscension { get; init; }
    public double? TargetDeclination { get; init; }
    public IReadOnlyList<string>? RunningItems { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>A wait instruction is running (Wait for time / altitude / safe…): what it waits for, in words.</summary>
    public string? Waiting { get; init; }
    /// <summary>When the wait is expected to end, when NINA can tell.</summary>
    public DateTimeOffset? WaitingUntil { get; init; }
    /// <summary>
    /// The targets in the loaded Advanced Sequence with their exposures done vs planned: tonight's plan for
    /// people who don't use Target Scheduler. Sent while a sequence is loaded, running or not.
    /// </summary>
    public IReadOnlyList<SequenceTarget>? Targets { get; init; }
}

public static class SequenceTargetStatus {
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Skipped = "skipped";
}

public sealed record SequenceTarget {
    public required string Name { get; init; }
    public double? RightAscension { get; init; }
    public double? Declination { get; init; }
    /// <summary>pending | running | done | skipped</summary>
    public required string Status { get; init; }
    public IReadOnlyList<SequenceExposure> Exposures { get; init; } = [];
}

public sealed record SequenceExposure {
    public string? Filter { get; init; }
    public double Seconds { get; init; }
    /// <summary>LIGHT, FLAT, DARK…</summary>
    public string? ImageType { get; init; }
    public int Done { get; init; }
    /// <summary>Null when the loop has no fixed count (e.g. loops until a time or altitude).</summary>
    public int? Planned { get; init; }
}

/// <summary>Ephemeris for the observatory's current night, computed by NINA from the profile location.</summary>
public sealed record NightState {
    public DateTimeOffset? Sunset { get; init; }
    public DateTimeOffset? NauticalDusk { get; init; }
    public DateTimeOffset? AstronomicalDusk { get; init; }
    public DateTimeOffset? AstronomicalDawn { get; init; }
    public DateTimeOffset? NauticalDawn { get; init; }
    public DateTimeOffset? Sunrise { get; init; }
    public double? MoonIllumination { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// <summary>Height above sea level in metres, from the profile (the Guard's Sun sums).</summary>
    public double? Elevation { get; init; }
    /// <summary>The observatory PC's current offset from UTC in minutes (includes daylight saving), for local times in reports.</summary>
    public int? UtcOffsetMinutes { get; init; }
}

public sealed record SessionSummary {
    public required string Id { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public int Frames { get; init; }
    public int Accepted { get; init; }
    public int Rejected { get; init; }
    public double IntegrationSeconds { get; init; }
    /// <summary>The latest light frame, for at-a-glance quality (Live Activity, cards).</summary>
    public LastFrame? Last { get; init; }
}

public sealed record LastFrame {
    public DateTimeOffset At { get; init; }
    public string? Filter { get; init; }
    public double ExposureSeconds { get; init; }
    public double? Hfr { get; init; }
    public int Score { get; init; }
    public required string Verdict { get; init; }
}

public static class AutofocusStatus {
    public const string Running = "running";
    public const string Finished = "finished";
    public const string Failed = "failed";
}

/// <summary>An autofocus run as the app shows it: live while running, the full curve and fit when finished.</summary>
public sealed record AutofocusState {
    /// <summary>running | finished | failed</summary>
    public required string Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>e.g. "NINA" or "Hocus Focus".</summary>
    public string? Engine { get; init; }
    public string? Filter { get; init; }
    public double? Temperature { get; init; }
    public int? FocuserPosition { get; init; }
    /// <summary>Measured points, sorted by position (live for NINA's autofocus; from the report otherwise).</summary>
    public IReadOnlyList<FocusPoint> Points { get; init; } = [];
    /// <summary>Focuser moves seen so far (progress when points aren't available live).</summary>
    public int Steps { get; init; }
    public FocusPoint? Final { get; init; }
    public double? FinalHfr { get; init; }
    public FocusPoint? Previous { get; init; }
    public FocusFit? Fit { get; init; }
    public double? DurationSeconds { get; init; }
    /// <summary>Tilt and curvature from this run's per-region V-curves (when the run gave enough).</summary>
    public NightManager.Core.Analysis.Optics.SensorFocusReport? Sensor { get; init; }
}

/// <summary>A switch hub (power ports, dew heaters): every switch, the writable ones settable from the phone.</summary>
public sealed record SwitchesState {
    public bool Connected { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<SwitchItem> Items { get; init; } = [];
}

public sealed record SwitchItem {
    /// <summary>NINA's switch id (what <c>switch.set</c> takes).</summary>
    public int Index { get; init; }
    public required string Name { get; init; }
    public double? Value { get; init; }
    public bool Writable { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
}

/// <summary>The phone's hands-on control: the job running now, the last outcome, live polar alignment.</summary>
public sealed record ControlState {
    /// <summary>The action running now (mount.slew, focuser.autofocus, camera.loop…), null when idle.</summary>
    public string? Action { get; init; }
    /// <summary>What it says on the phone: "Slewing to M31", "Looping 5 s, Ha".</summary>
    public string? Job { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    /// <summary>How the last job ended (shown once on the phone).</summary>
    public string? LastAction { get; init; }
    public string? LastResult { get; init; }
    public bool? LastFailed { get; init; }
    public DateTimeOffset? LastAt { get; init; }
    public PolarState? Polar { get; init; }
}

/// <summary>Three Point Polar Alignment, as its messages report it (arcminutes).</summary>
public sealed record PolarState {
    public bool Running { get; init; }
    public bool Paused { get; init; }
    public double? AzimuthArcmin { get; init; }
    public double? AltitudeArcmin { get; init; }
    public double? TotalArcmin { get; init; }
    public double? ToleranceArcmin { get; init; }
    public string? Progress { get; init; }
    public DateTimeOffset? At { get; init; }
}
