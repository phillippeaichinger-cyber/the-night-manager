using System.Globalization;
using System.Text.Json;
using NightManager.Core.Contracts;
using NightManager.Core.Logging;
using NightManager.Core.Settings;

namespace NightManager.Core.Syncthing;

/// <summary>What the monitor itself changed in Syncthing, kept on disk so a NINA restart undoes only that.</summary>
public sealed record SyncthingChanges {
    /// <summary>Devices Night Manager paused (never resumes ones you paused yourself).</summary>
    public List<string> PausedDevices { get; init; } = [];
    /// <summary>The upload limit before Night Manager set one, to restore.</summary>
    public int? SavedMaxSendKbps { get; init; }
    public DateTimeOffset? OverrideUntil { get; init; }
    public DateTimeOffset? PausedUntil { get; init; }
    /// <summary>true: uploads allowed; false: held back; null: Night Manager hasn't acted.</summary>
    public bool? Applied { get; init; }

    public static SyncthingChanges Load(string path) {
        try {
            return File.Exists(path) ? JsonSerializer.Deserialize<SyncthingChanges>(File.ReadAllText(path)) ?? new() : new();
        } catch (Exception) {
            return new();
        }
    }

    public void Save(string path) {
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        } catch (Exception) {
            // Best effort: losing it only means a restart can't tell its own pauses from yours.
        }
    }
}

/// <summary>
/// Watches Syncthing on the observatory PC and applies the upload window: outside it, uploads to the chosen
/// devices are paused (or limited); inside, they run. Changes happen only when the window opens or closes or
/// the app asks, so anything done by hand in Syncthing in between is respected.
/// </summary>
public sealed class SyncthingMonitor {
    private static readonly TimeSpan FolderRefresh = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DownAlarmAfter = TimeSpan.FromMinutes(5);
    private const long CaughtUpThreshold = 500L * 1024 * 1024;

    private readonly Func<ISyncthingApi?> api;
    private readonly Func<SyncthingSettings> settings;
    private readonly Action<SyncthingSettings> saveSettings;
    private readonly string changesPath;
    private readonly TimeProvider time;
    private readonly Func<string> newId;
    private readonly INmLog log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<TimeZoneInfo> zone;

    private SyncthingChanges changes;
    private string? myId;
    private string? version;
    private (DateTimeOffset At, long Out, long In)? lastTotals;
    private DateTimeOffset lastFolderRefresh = DateTimeOffset.MinValue;
    private IReadOnlyList<SyncthingFolder> folders = [];
    private DateTimeOffset? unreachableSince;
    private bool downIncidentOpen;
    private long lastToUpload;

    public SyncthingMonitor(Func<ISyncthingApi?> api, Func<SyncthingSettings> settings, Action<SyncthingSettings> saveSettings,
        string changesPath, TimeProvider? time = null, Func<string>? newId = null, INmLog? log = null, Func<TimeZoneInfo>? zone = null) {
        this.api = api;
        this.settings = settings;
        this.saveSettings = saveSettings;
        this.changesPath = changesPath;
        this.time = time ?? TimeProvider.System;
        this.newId = newId ?? (() => Guid.NewGuid().ToString("N"));
        this.log = log ?? NullLog.Instance;
        this.zone = zone ?? (() => TimeZoneInfo.Local);
        changes = SyncthingChanges.Load(changesPath);
    }

    public SyncthingState? State { get; private set; }

    public string? SessionId { get; set; }

    public async Task<IReadOnlyList<EventItem>> PollAsync(CancellationToken token) {
        var events = new List<EventItem>();
        var options = settings();
        if (!options.Enabled) {
            State = null;
            return events;
        }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            var now = time.GetUtcNow();
            var client = api();
            if (client is null) {
                MarkUnreachable(now, "No Syncthing API key: open Syncthing → Actions → Settings → GUI and paste the key into NINA.", events, options);
                return events;
            }
            try {
                version ??= await client.VersionAsync(token).ConfigureAwait(false);
                myId ??= await client.MyIdAsync(token).ConfigureAwait(false);
                await ApplyWindowAsync(client, options, now, events, token).ConfigureAwait(false);
                State = await ReadStateAsync(client, options, now, token).ConfigureAwait(false);
                if (downIncidentOpen) {
                    downIncidentOpen = false;
                    events.Add(Event(now, "sync.unreachable", Severity.Info, "Syncthing is responding again", null, incident: "syncthing", phase: "resolve"));
                }
                unreachableSince = null;
                CaughtUp(now, events);
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException) {
                if (token.IsCancellationRequested) {
                    throw;
                }
                MarkUnreachable(now, ex.Message, events, options);
            }
            return events;
        } finally {
            gate.Release();
        }
    }

    // MARK: Window

    private async Task ApplyWindowAsync(ISyncthingApi client, SyncthingSettings options, DateTimeOffset now, List<EventItem> events, CancellationToken token) {
        var window = options.Window;
        var local = TimeZoneInfo.ConvertTime(now, zone()).DateTime;

        if (changes.PausedUntil is { } until && now >= until) {
            changes = changes with { PausedUntil = null };
        }
        if (changes.OverrideUntil is { } over && now >= over) {
            changes = changes with { OverrideUntil = null };
        }

        bool? desired = changes.PausedUntil is not null ? false
            : changes.OverrideUntil is not null ? true
            : window.Enabled ? SyncSchedule.InWindow(window, local)
            : changes.Applied == false ? true // window switched off while held back: let it run
            : null;

        if (desired is not bool allow || allow == changes.Applied) {
            if (desired is null && changes.Applied is not null) {
                changes = changes with { Applied = null };
                changes.Save(changesPath);
            }
            return;
        }

        var manual = changes.PausedUntil is not null;
        if (allow) {
            await ReleaseAsync(client, token).ConfigureAwait(false);
            if (!manual && changes.OverrideUntil is null && window.Enabled) {
                events.Add(Event(now, "sync.windowOpened", Severity.Info, "Syncthing uploading",
                    $"Upload window {window.Start}–{window.End} is open.", null, null));
            }
        } else {
            await HoldAsync(client, options, pauseAlways: manual, token).ConfigureAwait(false);
            if (!manual) {
                var next = SyncSchedule.NextChange(window, local);
                events.Add(Event(now, "sync.windowClosed", Severity.Info,
                    window.Outside == "limit" ? $"Syncthing limited to {window.LimitKbps} kbit/s" : "Syncthing paused",
                    $"Uploads resume at {next:HH:mm} (window {window.Start}–{window.End}).", null, null));
            }
        }
        changes = changes with { Applied = allow };
        changes.Save(changesPath);
    }

    private async Task HoldAsync(ISyncthingApi client, SyncthingSettings options, bool pauseAlways, CancellationToken token) {
        if (!pauseAlways && options.Window.Outside == "limit") {
            var current = await client.MaxSendKbpsAsync(token).ConfigureAwait(false);
            if (current != options.Window.LimitKbps) {
                changes = changes with { SavedMaxSendKbps = changes.SavedMaxSendKbps ?? current };
                await client.SetMaxSendKbpsAsync(options.Window.LimitKbps, token).ConfigureAwait(false);
            }
            return;
        }
        var paused = new List<string>(changes.PausedDevices);
        foreach (var device in await TargetDevicesAsync(client, options, token).ConfigureAwait(false)) {
            if (!device.Paused) {
                await client.SetDevicePausedAsync(device.DeviceId, true, token).ConfigureAwait(false);
                paused.Add(device.DeviceId);
            }
        }
        changes = changes with { PausedDevices = paused.Distinct().ToList() };
        log.Info($"Syncthing: paused {paused.Count} device(s) for the upload window.");
    }

    /// <summary>Undo only what Night Manager did: resume its paused devices, restore the previous limit.</summary>
    private async Task ReleaseAsync(ISyncthingApi client, CancellationToken token) {
        foreach (var id in changes.PausedDevices) {
            await client.SetDevicePausedAsync(id, false, token).ConfigureAwait(false);
        }
        if (changes.SavedMaxSendKbps is int saved) {
            await client.SetMaxSendKbpsAsync(saved, token).ConfigureAwait(false);
        }
        changes = changes with { PausedDevices = [], SavedMaxSendKbps = null };
    }

    private async Task<IReadOnlyList<StDevice>> TargetDevicesAsync(ISyncthingApi client, SyncthingSettings options, CancellationToken token) {
        var devices = (await client.DevicesAsync(token).ConfigureAwait(false)).Where(d => d.DeviceId != myId).ToList();
        if (options.Devices.Count == 0) {
            return devices;
        }
        return devices.Where(d => options.Devices.Any(n =>
            string.Equals(n, d.Name, StringComparison.OrdinalIgnoreCase) || d.DeviceId.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // MARK: State

    private async Task<SyncthingState> ReadStateAsync(ISyncthingApi client, SyncthingSettings options, DateTimeOffset now, CancellationToken token) {
        var devices = (await client.DevicesAsync(token).ConfigureAwait(false)).Where(d => d.DeviceId != myId).ToList();
        var connections = await client.ConnectionsAsync(token).ConfigureAwait(false);

        double? up = null, down = null;
        if (lastTotals is { } last && now > last.At) {
            var seconds = (now - last.At).TotalSeconds;
            up = Math.Max(0, (connections.OutBytesTotal - last.Out) / seconds);
            down = Math.Max(0, (connections.InBytesTotal - last.In) / seconds);
        }
        lastTotals = (now, connections.OutBytesTotal, connections.InBytesTotal);

        var deviceStates = new List<SyncthingDevice>();
        foreach (var d in devices) {
            var connected = connections.Devices.TryGetValue(d.DeviceId, out var c) && c.Connected;
            StCompletion? completion = null;
            try {
                completion = await client.CompletionAsync(d.DeviceId, token).ConfigureAwait(false);
            } catch (HttpRequestException) {
                // Older Syncthing or a device without shared folders.
            }
            deviceStates.Add(new SyncthingDevice {
                Id = d.DeviceId.Length > 7 ? d.DeviceId[..7] : d.DeviceId,
                Name = string.IsNullOrWhiteSpace(d.Name) ? d.DeviceId[..Math.Min(7, d.DeviceId.Length)] : d.Name,
                Connected = connected,
                Paused = d.Paused,
                Completion = completion is null ? null : Math.Round(completion.Completion, 1),
                NeedBytes = completion?.NeedBytes ?? 0,
                NeedItems = completion?.NeedItems ?? 0,
            });
        }

        if (now - lastFolderRefresh >= FolderRefresh) {
            var list = new List<SyncthingFolder>();
            foreach (var f in await client.FoldersAsync(token).ConfigureAwait(false)) {
                StFolderStatus? folderStatus = null;
                if (!f.Paused) {
                    try {
                        folderStatus = await client.FolderStatusAsync(f.Id, token).ConfigureAwait(false);
                    } catch (HttpRequestException) {
                    }
                }
                list.Add(new SyncthingFolder {
                    Id = f.Id, Label = f.Label, Paused = f.Paused, State = f.Paused ? "paused" : folderStatus?.State,
                    GlobalBytes = folderStatus?.GlobalBytes ?? 0, NeedBytes = folderStatus?.NeedBytes ?? 0, Errors = folderStatus?.PullErrors ?? 0,
                });
            }
            folders = list;
            lastFolderRefresh = now;
        }

        var targets = (await TargetDevicesAsync(client, options, token).ConfigureAwait(false)).Select(d => d.DeviceId[..Math.Min(7, d.DeviceId.Length)]).ToHashSet();
        var targetStates = deviceStates.Where(d => targets.Contains(d.Id)).ToList();
        var held = changes.Applied == false;
        var limited = held && changes.PausedDevices.Count == 0 && changes.SavedMaxSendKbps is not null;
        var allPaused = targetStates.Count > 0 && targetStates.All(d => d.Paused);
        var busy = (up ?? 0) > 2048 || (down ?? 0) > 2048 || folders.Any(f => f.State is "syncing" or "sync-preparing");

        var status = allPaused ? SyncthingStatus.Paused
            : limited ? SyncthingStatus.Limited
            : busy ? SyncthingStatus.Syncing
            : deviceStates.Any(d => d.Connected) ? SyncthingStatus.Idle
            : SyncthingStatus.Offline;

        var local = TimeZoneInfo.ConvertTime(now, zone()).DateTime;
        DateTimeOffset? nextChange = options.Window.Enabled
            ? new DateTimeOffset(SyncSchedule.NextChange(options.Window, local), zone().GetUtcOffset(local)).ToUniversalTime()
            : null;

        return new SyncthingState {
            Status = status,
            Version = version,
            BySchedule = held && changes.PausedUntil is null,
            OverrideUntil = changes.OverrideUntil,
            PausedUntil = changes.PausedUntil == DateTimeOffset.MaxValue ? null : changes.PausedUntil,
            Window = options.Window,
            NextChange = nextChange,
            UploadBytesPerSecond = up is null ? null : Math.Round(up.Value),
            DownloadBytesPerSecond = down is null ? null : Math.Round(down.Value),
            ToUploadBytes = deviceStates.Where(d => d.Connected).Select(d => d.NeedBytes).DefaultIfEmpty(0).Max(),
            ToDownloadBytes = folders.Sum(f => f.NeedBytes),
            Devices = deviceStates.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList(),
            Folders = folders,
            ControlAllowed = options.AllowRemoteControl,
        };
    }

    private void CaughtUp(DateTimeOffset now, List<EventItem> events) {
        var toUpload = State?.ToUploadBytes ?? 0;
        if (lastToUpload >= CaughtUpThreshold && toUpload == 0 && State?.Devices.Any(d => d.Connected) == true) {
            var names = string.Join(", ", State.Devices.Where(d => d.Connected).Select(d => d.Name));
            events.Add(Event(now, "sync.caughtUp", Severity.Info, "Uploads finished", $"Everything is on {names}.", null, null,
                new Dictionary<string, object?> { ["notify"] = true, ["sentBytes"] = lastToUpload }));
        }
        lastToUpload = toUpload;
    }

    private void MarkUnreachable(DateTimeOffset now, string error, List<EventItem> events, SyncthingSettings options) {
        unreachableSince ??= now;
        State = new SyncthingState { Status = SyncthingStatus.Unreachable, Error = error, Version = version, Window = options.Window, ControlAllowed = options.AllowRemoteControl };
        lastTotals = null;
        if (!downIncidentOpen && now - unreachableSince >= DownAlarmAfter) {
            downIncidentOpen = true;
            events.Add(Event(now, "sync.unreachable", Severity.Warning, "Syncthing isn't responding", error, incident: "syncthing", phase: "open"));
        }
    }

    // MARK: Commands from the app

    public async Task<string> HandleAsync(RemoteCommand command, CancellationToken token) {
        var options = settings();
        if (!options.Enabled) {
            throw new InvalidOperationException("Syncthing isn't set up in NINA (Options → Plugins → The Night Manager → Syncthing).");
        }
        if (!options.AllowRemoteControl) {
            throw new InvalidOperationException("Controlling Syncthing from the app is turned off in NINA.");
        }
        string Arg(string name) => command.Args is { } a && a.TryGetValue(name, out var v) ? v : string.Empty;
        var now = time.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, zone()).DateTime;
        var by = command.IssuedBy is { Length: > 0 } who ? $" by {who}" : "";
        string message;

        await gate.WaitAsync(token).ConfigureAwait(false);
        try {
            switch (command.Type) {
                case CommandTypes.SyncPause:
                    // Until the window next opens; with no window, until resumed.
                    var until = options.Window.Enabled
                        ? new DateTimeOffset(SyncSchedule.InWindow(options.Window, local) ? SyncSchedule.NextChange(options.Window, SyncSchedule.NextChange(options.Window, local)) : SyncSchedule.NextChange(options.Window, local), zone().GetUtcOffset(local)).ToUniversalTime()
                        : DateTimeOffset.MaxValue;
                    // Applied = null: act on it now, even if the window was already being held (nothing paused yet).
                    changes = changes with { PausedUntil = until, OverrideUntil = null, Applied = null };
                    message = until == DateTimeOffset.MaxValue ? $"Syncthing paused{by} until resumed." : $"Syncthing paused{by} until the window opens.";
                    break;
                case CommandTypes.SyncResume:
                    changes = changes with { PausedUntil = null, Applied = null };
                    if (options.Window.Enabled && !SyncSchedule.InWindow(options.Window, local)) {
                        changes = changes with { OverrideUntil = new DateTimeOffset(SyncSchedule.NextChange(options.Window, local), zone().GetUtcOffset(local)).ToUniversalTime() };
                    }
                    message = $"Syncthing resumed{by}.";
                    break;
                case CommandTypes.SyncNow:
                    var hours = double.TryParse(Arg("hours"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? Math.Clamp(h, 0.25, 24) : 2;
                    changes = changes with { PausedUntil = null, OverrideUntil = now.AddHours(hours), Applied = null };
                    message = $"Syncing now for {hours:0.##} h{by}, whatever the window says.";
                    break;
                case CommandTypes.SyncSchedule:
                    var window = options.Window with {
                        Enabled = Arg("enabled") is { Length: > 0 } e ? e == "true" : options.Window.Enabled,
                        Start = Arg("start") is { Length: > 0 } s ? s : options.Window.Start,
                        End = Arg("end") is { Length: > 0 } en ? en : options.Window.End,
                        Outside = Arg("outside") is { Length: > 0 } o ? o : options.Window.Outside,
                        LimitKbps = int.TryParse(Arg("limitKbps"), out var k) ? k : options.Window.LimitKbps,
                    };
                    var updated = (options with { Window = window }).Normalized();
                    saveSettings(updated);
                    message = updated.Window.Enabled
                        ? $"Upload window set to {updated.Window.Start}–{updated.Window.End}{(updated.Window.Outside == "limit" ? $", {updated.Window.LimitKbps} kbit/s outside it" : ", paused outside it")}."
                        : "Upload window turned off.";
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported command {command.Type}.");
            }
            changes.Save(changesPath);
        } finally {
            gate.Release();
        }
        // Apply straight away rather than at the next poll.
        await PollAsync(token).ConfigureAwait(false);
        return message;
    }

    private EventItem Event(DateTimeOffset now, string kind, string severity, string title, string? message, string? incident, string? phase,
        IReadOnlyDictionary<string, object?>? data = null) => new() {
        Id = newId(),
        Timestamp = now,
        Kind = kind,
        Severity = severity,
        Title = title,
        Message = message,
        SessionId = SessionId,
        IncidentKey = incident,
        IncidentPhase = phase,
        Data = data,
    };
}
