using System.Text.Json.Serialization;

namespace NightManager.Core.Contracts;

/// <summary>
/// One unit of data sent to the backend. Every item has a globally unique id so the
/// backend can de-duplicate retries (ingest is idempotent per item id).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SnapshotItem), "snapshot")]
[JsonDerivedType(typeof(EventItem), "event")]
[JsonDerivedType(typeof(FrameItem), "frame")]
[JsonDerivedType(typeof(SessionItem), "session")]
[JsonDerivedType(typeof(CommandResultItem), "commandResult")]
[JsonDerivedType(typeof(SchedulerPlanItem), "schedulerPlan")]
[JsonDerivedType(typeof(SequenceExportItem), "sequenceExport")]
[JsonDerivedType(typeof(SequencerCatalogItem), "sequencerCatalog")]
[JsonDerivedType(typeof(SequenceTreeItem), "sequenceTree")]
[JsonDerivedType(typeof(SchedulerForecastItem), "schedulerForecast")]
[JsonDerivedType(typeof(CameraFrameItem), "cameraFrame")]
[JsonDerivedType(typeof(HistoryImportItem), "historyImport")]
[JsonDerivedType(typeof(ExposureAdviceItem), "exposureAdvice")]
[JsonDerivedType(typeof(FocusAdviceItem), "focusAdvice")]
public abstract record IngestItem {
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed record SnapshotItem : IngestItem {
    public required ObservatorySnapshot Snapshot { get; init; }
    /// <summary>Hash of what matters (no ticking values): the backend rewrites the rig's state only when it changes.</summary>
    public string? Signature { get; init; }
}

public static class Severity {
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>A discrete thing that happened. Drives the night timeline, alerts and the AI report.</summary>
public sealed record EventItem : IngestItem {
    /// <summary>One of <see cref="EventKinds"/>.</summary>
    public required string Kind { get; init; }
    public string Severity { get; init; } = Contracts.Severity.Info;
    public required string Title { get; init; }
    public string? Message { get; init; }
    public string? SessionId { get; init; }
    /// <summary>
    /// Groups the open/resolve pair of an incident (e.g. "clouds", "guiding-lost").
    /// The backend raises one alert per open incident and closes it on resolve.
    /// </summary>
    public string? IncidentKey { get; init; }
    /// <summary>open | resolve | null for one-off events.</summary>
    public string? IncidentPhase { get; init; }
    public IReadOnlyDictionary<string, object?>? Data { get; init; }
}

public static class EventKinds {
    public const string PluginStarted = "plugin.started";
    public const string PluginStopping = "plugin.stopping";
    public const string DeviceConnected = "device.connected";
    public const string DeviceDisconnected = "device.disconnected";
    public const string SequenceStarted = "sequence.started";
    public const string SequenceFinished = "sequence.finished";
    public const string SequenceItemFailed = "sequence.itemFailed";
    public const string TargetChanged = "sequence.targetChanged";
    public const string FilterChanged = "filter.changed";
    public const string MeridianFlipStarted = "mount.flipStarted";
    public const string MeridianFlipFinished = "mount.flipFinished";
    public const string MountSlewed = "mount.slewed";
    public const string MountParked = "mount.parked";
    public const string MountUnparked = "mount.unparked";
    public const string GuidingStarted = "guider.started";
    public const string GuidingStopped = "guider.stopped";
    public const string Dithered = "guider.dithered";
    public const string AutofocusStarted = "focus.autofocusStarted";
    public const string AutofocusFinished = "focus.autofocusFinished";
    /// <summary>Is the rig ready for tonight (data: the <see cref="ReadinessReport"/> and whether to notify).</summary>
    public const string ReadinessReport = "readiness.report";
    public const string SafetyChanged = "safety.changed";
    public const string DomeOpened = "dome.opened";
    public const string DomeClosed = "dome.closed";
    public const string ImageSaveFailed = "image.saveFailed";
    public const string DownloadTimeout = "camera.downloadTimeout";
    public const string UserNotification = "user.notification";
    /// <summary>Several instructions failed in quick succession: one summary instead of many alerts.</summary>
    public const string SequenceFailureBurst = "sequence.failureBurst";
    /// <summary>A failed instruction succeeded on a later attempt (timeline only).</summary>
    public const string SequenceItemRecovered = "sequence.itemRecovered";
    /// <summary>"Ask my phone" / "Wait for go from phone": a question with buttons.</summary>
    public const string UserQuestion = "user.question";
    public const string UserAnswered = "user.answered";
    /// <summary>"Wake me up": repeated critical alarm until acknowledged.</summary>
    public const string UserAlarm = "user.alarm";
    public const string UserImage = "user.image";
    public const string UserProgress = "user.progress";
    public const string UserNote = "user.note";
    public const string UserTrackStart = "user.trackStart";
    public const string SequencePaused = "sequence.paused";
    public const string SequenceResumed = "sequence.resumed";
    public const string FinishRequested = "sequence.finishRequested";
    // Incidents detected by Night Watch (open/resolve pairs).
    public const string Clouds = "watch.clouds";
    public const string FocusDrift = "watch.focusDrift";
    public const string DewRisk = "watch.dewRisk";
    public const string GuidingDegraded = "watch.guidingDegraded";
    public const string CoolingLost = "watch.coolingLost";
    public const string NoFrames = "watch.noFrames";
    public const string CoolingSlow = "watch.coolingSlow";
    /// <summary>The camera reached its cooling set point (one-off, notifies the user).</summary>
    public const string CameraCooled = "camera.cooled";
    // Target Scheduler.
    public const string SchedulerWaiting = "scheduler.waiting";
    public const string SchedulerTargetComplete = "scheduler.targetComplete";
    public const string SchedulerStopped = "scheduler.stopped";
    /// <summary>An observatory camera's picture taken because of a kit error (sent once the picture is uploaded).</summary>
    public const string CameraSnapshot = "camera.snapshot";
}

/// <summary>
/// A still from an observatory camera (Pro). The picture goes to the one slot the camera has in the cloud,
/// replacing the previous one: there is never a history. Kilobytes, not megabytes (see CameraPolicy).
/// </summary>
public sealed record CameraFrameItem : IngestItem {
    public required string CameraId { get; init; }
    public required string Name { get; init; }
    /// <summary>watch | schedule | alert</summary>
    public required string Reason { get; init; }
    /// <summary>The kit error behind an alert picture.</summary>
    public string? Cause { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long Bytes { get; init; }
    public string ContentType { get; init; } = "image/jpeg";
}

/// <summary>The sequence loaded in NINA as a plan, plus the user's target templates (for the phone's builder).</summary>
public sealed record SequenceExportItem : IngestItem {
    public string? Title { get; init; }
    public bool Running { get; init; }
    public required SequencePlan Plan { get; init; }
    public IReadOnlyList<string> Templates { get; init; } = [];
}

/// <summary>One saved image, with quality metrics and the Night Manager grade.</summary>
public sealed record FrameItem : IngestItem {
    public string? SessionId { get; init; }
    /// <summary>Tilt, field curvature and collimation measured from this light frame's stars (Pro in the app).</summary>
    public NightManager.Core.Analysis.Optics.OpticsReport? Optics { get; init; }
    public required string FileName { get; init; }
    /// <summary>LIGHT | DARK | FLAT | BIAS | DARKFLAT | SNAPSHOT</summary>
    public string? ImageType { get; init; }
    public string? Target { get; init; }
    public string? Filter { get; init; }
    public double? ExposureSeconds { get; init; }
    public int? Gain { get; init; }
    public int? Offset { get; init; }
    public string? Binning { get; init; }
    public double? CameraTemperature { get; init; }
    public double? Hfr { get; init; }
    public double? HfrStdDev { get; init; }
    public int? Stars { get; init; }
    /// <summary>Only present when a star detector that measures FWHM is selected (e.g. Hocus Focus).</summary>
    public double? Fwhm { get; init; }
    public double? Eccentricity { get; init; }
    public double? Median { get; init; }
    public double? Mean { get; init; }
    public double? StdDev { get; init; }
    public double? GuidingRmsTotal { get; init; }
    public double? GuidingRmsRa { get; init; }
    public double? GuidingRmsDec { get; init; }
    public double? Altitude { get; init; }
    public double? Airmass { get; init; }
    public double? RotatorAngle { get; init; }
    public int? FocuserPosition { get; init; }
    public double? FocuserTemperature { get; init; }
    public FrameGrade? Grade { get; init; }
    public ThumbnailInfo? Thumbnail { get; init; }
}

public sealed record ThumbnailInfo {
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long Bytes { get; init; }
    public string ContentType { get; init; } = "image/jpeg";
}

public static class Verdicts {
    public const string Accept = "accept";
    public const string Marginal = "marginal";
    public const string Reject = "reject";
    /// <summary>Calibration frames and frames without metrics are not graded.</summary>
    public const string NotGraded = "notGraded";
}

public sealed record FrameGrade {
    /// <summary>0 (unusable) to 100 (best frame of the night quality).</summary>
    public required int Score { get; init; }
    public required string Verdict { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    /// <summary>Baseline the frame was compared against (rolling median of good frames, same target/filter/exposure).</summary>
    public double? BaselineHfr { get; init; }
    public double? BaselineStars { get; init; }
}

public static class SessionPhases {
    public const string Started = "started";
    public const string Updated = "updated";
    public const string Ended = "ended";
}

public sealed record SessionItem : IngestItem {
    public required string SessionId { get; init; }
    public required string Phase { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>sequenceFinished | dawn | idle | manual | pluginStopped</summary>
    public string? EndReason { get; init; }
    public required SessionTotals Totals { get; init; }
}

public sealed record SessionTotals {
    public int Frames { get; init; }
    public int LightFrames { get; init; }
    public int Accepted { get; init; }
    public int Marginal { get; init; }
    public int Rejected { get; init; }
    public double IntegrationSeconds { get; init; }
    public double AcceptedIntegrationSeconds { get; init; }
    public double? BestHfr { get; init; }
    public double? MedianHfr { get; init; }
    public double? MedianGuidingRms { get; init; }
    public IReadOnlyList<TargetTotals> Targets { get; init; } = [];
    /// <summary>Where the dark hours went: seconds per bucket (imaging, autofocus, flip, slewing, guiding, waiting, unsafe, overhead, idle).</summary>
    public IReadOnlyDictionary<string, double>? Time { get; init; }
    /// <summary>Autofocus runs, flips and dithers this session.</summary>
    public IReadOnlyDictionary<string, int>? Counts { get; init; }
}

public sealed record TargetTotals {
    public required string Target { get; init; }
    public IReadOnlyList<FilterTotals> Filters { get; init; } = [];
}

public sealed record FilterTotals {
    public required string Filter { get; init; }
    public int Frames { get; init; }
    public int Accepted { get; init; }
    public double IntegrationSeconds { get; init; }
    public double AcceptedIntegrationSeconds { get; init; }
}

public static class CommandOutcomes {
    public const string Executed = "executed";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

/// <summary>Reports what happened to a remote command, so the app can confirm it to the user.</summary>
public sealed record CommandResultItem : IngestItem {
    public required string CommandId { get; init; }
    public required string CommandType { get; init; }
    public required string Outcome { get; init; }
    public string? Message { get; init; }
}
