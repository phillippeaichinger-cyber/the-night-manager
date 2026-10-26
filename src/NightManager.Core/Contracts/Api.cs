namespace NightManager.Core.Contracts;

public static class ApiPaths {
    public const string Ingest = "/v2/ingest";
    public const string PairStart = "/v2/pair/start";
    public const string PairPoll = "/v2/pair/poll";
}

/// <summary>Body of <c>POST /v2/ingest</c>. Signed with the observatory credential.</summary>
public sealed record IngestBatch {
    public int SchemaVersion { get; init; } = 2;
    public required string BatchId { get; init; }
    public required DateTimeOffset SentAt { get; init; }
    public required ClientInfo Client { get; init; }
    public required IReadOnlyList<IngestItem> Items { get; init; }
    /// <summary>Ask for waiting commands (after a nudge, or when the live lane is down). Null = always (old behaviour).</summary>
    public bool? CheckCommands { get; init; }
}

public sealed record ClientInfo {
    /// <summary>
    /// The rig this batch describes: the NINA profile id. One PC (= one observatory credential) can
    /// run several NINA instances, one per rig; all items and commands in a batch belong to this rig.
    /// </summary>
    public required string RigId { get; init; }
    /// <summary>The NINA profile name, used as the rig's default display name.</summary>
    public string? RigName { get; init; }
    /// <summary>
    /// The setup (mount + its optical paths) this rig belongs to: the main rig's id. A main rig
    /// reports its own id; linked rigs report their main rig's id, so the app shows them together.
    /// </summary>
    public required string SetupId { get; init; }
    public string? SetupName { get; init; }
    /// <summary>main | linked</summary>
    public required string RigRole { get; init; }
    public required string PluginVersion { get; init; }
    public string? NinaVersion { get; init; }
    public string? MachineName { get; init; }
    /// <summary>Remote command types this installation currently allows (owner-controlled in NINA).</summary>
    public IReadOnlyList<string> AllowedCommands { get; init; } = [];
    /// <summary>What this build supports; "live" = the live lane (nudged commands, signature-gated snapshots).</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }
    /// <summary>Optional data switched off in this NINA's options (<see cref="Settings.DataFeatures"/>).</summary>
    public IReadOnlyList<string>? DataOff { get; init; }
    /// <summary>
    /// The optics and sensor from the NINA profile (beta research: focus and tilt models per kind of scope). Numbers
    /// and equipment names only; the server stores it on the rig when it changes.
    /// </summary>
    public EquipmentInfo? Equipment { get; init; }
    /// <summary>How phones on this PC's home network reach it directly (docs/LOCAL-LINK.md); null when it's off.</summary>
    public LocalLinkInfo? LocalLink { get; init; }
    /// <summary>This PC's wired and Wi-Fi network cards: what a linked Night Manager Guard wakes and watches.</summary>
    public PcNetworkInfo? PcNetwork { get; init; }
}

/// <summary>The PC's physical network cards (never VPN adapters): MAC addresses and private IPv4 addresses.</summary>
public sealed record PcNetworkInfo {
    /// <summary>"aa:bb:cc:dd:ee:ff", wired cards first (Wake-on-LAN rarely works over Wi-Fi).</summary>
    public required IReadOnlyList<string> Macs { get; init; }
    public required IReadOnlyList<string> Addresses { get; init; }
}

/// <summary>The local link as the observatory's members' phones need it: where, which certificate, which key.</summary>
public sealed record LocalLinkInfo {
    public required int Port { get; init; }
    /// <summary>SHA-256 of the PC's certificate (lowercase hex): the phone accepts no other.</summary>
    public required string CertSha256 { get; init; }
    /// <summary>The PC's private addresses on its physical adapters (never VPN ones).</summary>
    public required IReadOnlyList<string> Addresses { get; init; }
    /// <summary>The request signing key (hex), for the observatory's members only (the server keeps it on the rig).</summary>
    public required string Key { get; init; }
    /// <summary>
    /// Signs commands sent straight to this PC (hex). The server keeps it apart from the rig and gives it only to
    /// members who may control the observatory, never to viewers.
    /// </summary>
    public string? ControlKey { get; init; }
}

/// <summary>What the rig is, from the active NINA profile and the connected devices.</summary>
public sealed record EquipmentInfo {
    public string? TelescopeName { get; init; }
    public double? FocalLengthMm { get; init; }
    public double? FocalRatio { get; init; }
    public double? ApertureMm { get; init; }
    public string? CameraName { get; init; }
    public double? PixelSizeUm { get; init; }
    public int? SensorWidthPx { get; init; }
    public int? SensorHeightPx { get; init; }
    public string? FocuserName { get; init; }
    public int? FocuserStepSize { get; init; }
    /// <summary>False for one-shot-colour rigs: their autofocus runs have no filter, and that's right.</summary>
    public bool? HasFilterWheel { get; init; }
    public IReadOnlyList<string>? Filters { get; init; }
}

/// <summary>Response of <c>POST /v2/ingest</c>.</summary>
public sealed record IngestResponse {
    public string? ObservatoryId { get; init; }
    public string? ObservatoryName { get; init; }
    /// <summary>Signed upload targets for frame thumbnails in this batch.</summary>
    public IReadOnlyList<BlobUpload> Uploads { get; init; } = [];
    /// <summary>Pending remote commands, piggy-backed on every ingest so no separate polling is needed.</summary>
    public IReadOnlyList<RemoteCommand> Commands { get; init; } = [];
    /// <summary>Server-suggested seconds between heartbeats while idle. Null keeps the local default.</summary>
    public int? HeartbeatSeconds { get; init; }
    /// <summary>True when this rig is beyond the account's plan limit: its data is not stored until the plan is upgraded.</summary>
    public bool? RigLocked { get; init; }
    public DateTimeOffset? ServerTime { get; init; }
    /// <summary>The observatory's plan includes cameras (Pro). False or missing: the plugin takes no pictures.</summary>
    public bool? Cameras { get; init; }
    /// <summary>Optional data the owner switched off in the app: not collected or sent. Null: an older backend.</summary>
    public IReadOnlyList<string>? DataOff { get; init; }
    /// <summary>Goes up when a member is removed: make a new home Wi-Fi link key. Null: an older backend.</summary>
    public int? LocalLinkEpoch { get; init; }
}

public sealed record BlobUpload {
    public required string ItemId { get; init; }
    public required string Url { get; init; }
    public string Method { get; init; } = "PUT";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

public static class CommandTypes {
    /// <summary>Harmless: forces an immediate snapshot. Always allowed.</summary>
    public const string Ping = "ping";
    public const string AbortSequence = "abortSequence";
    public const string StopGuiding = "stopGuiding";
    public const string ParkMount = "parkMount";
    public const string CloseDome = "closeDome";
    public const string WarmCamera = "warmCamera";
    /// <summary>Abort sequence → stop guiding → park mount → close dome → warm camera.</summary>
    public const string SafeShutdown = "safeShutdown";

    /// <summary>Answers a pending "Ask my phone" / "Wait for go" prompt. Args: promptId, answer (yes | no | go).</summary>
    public const string Answer = "answer";
    /// <summary>Stops a "Wake me up" alarm. Args: alarmId (optional: all).</summary>
    public const string Acknowledge = "acknowledge";
    /// <summary>Ends "Loop until finished from phone" loops so the end-of-sequence block still runs.</summary>
    public const string FinishNight = "finishNight";
    /// <summary>Holds the sequence at the next "Pause point" until resumed.</summary>
    public const string Pause = "pause";
    public const string Resume = "resume";

    /// <summary>Syncthing on the observatory PC: pause / resume uploads, sync now (args: hours), set the upload window.</summary>
    public const string SyncPause = "syncPause";
    public const string SyncResume = "syncResume";
    public const string SyncNow = "syncNow";
    /// <summary>Args: enabled, start, end (HH:mm), outside (pause | limit), limitKbps.</summary>
    public const string SyncSchedule = "syncSchedule";

    public static readonly IReadOnlyList<string> Sync = [SyncPause, SyncResume, SyncNow, SyncSchedule];

    /// <summary>Load a plan from the phone (payload: the plan; args: mode, start). Checked against the plugin's own settings.</summary>
    public const string LoadSequence = "loadSequence";
    /// <summary>Send the loaded sequence back as a plan ("Import from NINA").</summary>
    public const string ReadSequence = "readSequence";

    /// <summary>Build a sequencer template from the phone (payload: the tree; args: mode, start).</summary>
    public const string ApplyTemplate = "applyTemplate";
    /// <summary>Send a NINA template (args: name) or the loaded sequence (no name) back as a tree.</summary>
    public const string ReadTemplate = "readTemplate";
    /// <summary>Send the catalog of buildable items again.</summary>
    public const string ReadCatalog = "readCatalog";

    public static readonly IReadOnlyList<string> Plans = [LoadSequence, ReadSequence, ApplyTemplate, ReadTemplate, ReadCatalog];

    /// <summary>Send past imaging (Target Scheduler's records) again, for Year in Review. Harmless: read-only on the PC.</summary>
    public const string ImportHistory = "importHistory";

    /// <summary>Run the readiness check now and report it. Harmless: it only looks.</summary>
    public const string CheckReadiness = "checkReadiness";

    /// <summary>Work out the sub-length advice now and send it (Pro). Harmless: it only looks.</summary>
    public const string AdviseExposure = "adviseExposure";

    /// <summary>The filters chosen in the app (args: NINA filter name → bandwidth in nm). Stored on the PC.</summary>
    public const string SetFilterBandwidths = "setFilterBandwidths";

    /// <summary>Work out the focus coach's view now and send it (Pro). Harmless: it only looks.</summary>
    public const string AdviseFocus = "adviseFocus";

    /// <summary>Commands that steer only the owner's own Night Manager instructions.</summary>
    public static readonly IReadOnlyList<string> PhoneControl = [Answer, Acknowledge, FinishNight, Pause, Resume];

    /// <summary>
    /// Hands-on equipment control from the phone (args: action, then the action's own values; see
    /// <see cref="Commands.EquipmentActions"/>): camera, focuser, filter wheel, guider, rotator, flat panel, switches,
    /// connecting devices. Allowed only when the owner ticks "Equipment control from the app" in NINA.
    /// </summary>
    public const string Equipment = "equipment";
    /// <summary>
    /// Things that move the telescope or the building: slew, centre, unpark, home, dome, polar alignment, starting the
    /// sequence. Its own tick in NINA ("Move the mount and dome from the app"), off by default.
    /// </summary>
    public const string Move = "move";

    public static readonly IReadOnlyList<string> All = [Ping, AbortSequence, StopGuiding, ParkMount, CloseDome, WarmCamera, SafeShutdown, Answer, Acknowledge, FinishNight, Pause, Resume, SyncPause, SyncResume, SyncNow, SyncSchedule, LoadSequence, ReadSequence, ApplyTemplate, ReadTemplate, ReadCatalog, Equipment, Move];
}

public sealed record RemoteCommand {
    public required string Id { get; init; }
    /// <summary>Target rig. Null = observatory-wide (every rig that allows the command acts on it).</summary>
    public string? RigId { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public string? IssuedBy { get; init; }
    public IReadOnlyDictionary<string, string>? Args { get; init; }
    /// <summary>Structured data for the command (a sequence plan for loadSequence).</summary>
    public System.Text.Json.JsonElement? Payload { get; init; }
}

/// <summary>Body of <c>POST /v2/pair/start</c>. Unauthenticated; rate-limited server side.</summary>
public sealed record PairStartRequest {
    public required string InstallationId { get; init; }
    public required string SuggestedName { get; init; }
    public required string PluginVersion { get; init; }
    public string? NinaVersion { get; init; }
}

public sealed record PairStartResponse {
    public required string PairingId { get; init; }
    /// <summary>Short code the user types into the app, e.g. "K7P-4QX".</summary>
    public required string UserCode { get; init; }
    /// <summary>Secret that proves this installation started the pairing. Never shown to the user.</summary>
    public required string PollToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public int PollIntervalSeconds { get; init; } = 3;
    /// <summary>Web page where the code can be entered instead of the app (optional).</summary>
    public string? VerificationUrl { get; init; }
}

public sealed record PairPollRequest {
    public required string PairingId { get; init; }
    public required string PollToken { get; init; }
}

public static class PairStatuses {
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Expired = "expired";
    public const string Denied = "denied";
}

public sealed record PairPollResponse {
    public required string Status { get; init; }
    /// <summary>Present once, when <see cref="Status"/> is approved.</summary>
    public string? ApiKey { get; init; }
    public string? ObservatoryId { get; init; }
    public string? ObservatoryName { get; init; }
    /// <summary>True when this PC already had an observatory on that account: it carries on with its history.</summary>
    public bool? Reconnected { get; init; }
}
