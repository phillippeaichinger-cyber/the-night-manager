using NightManager.Core.Contracts;

namespace NightManager.Core.Analysis;

/// <summary>
/// Warns before NINA's image drive fills up: a warning below 50 GB or 5% free, critical below 10 GB or 1%, or when
/// tonight's rate would fill it before the night ends. One incident, resolved when space is freed. Absolute sizes
/// lead because percentages mislead on big drives (3% of 12 TB is still 360 GB).
/// </summary>
public sealed class StorageWatch {
    private readonly Func<string> newId;
    private string? openSeverity;
    private string? systemSeverity;

    public StorageWatch(Func<string>? newId = null) {
        this.newId = newId ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// The Windows drive: when C: fills, Windows, NINA's logs and Syncthing's database fail even if the image drive
    /// has room. Warning under 15 GB, critical under 5 GB. Skipped when it is also the image drive (covered above).
    /// </summary>
    public IReadOnlyList<EventItem> ObserveSystem(DriveSpace? drive, DateTimeOffset now) {
        if (drive is null || drive.Images || drive.TotalBytes <= 0) {
            return [];
        }
        var severity = drive.FreeBytes < 5 * Gb ? Severity.Critical : drive.FreeBytes < 15 * Gb ? Severity.Warning : null;
        if (severity == systemSeverity) {
            return [];
        }
        systemSeverity = severity;
        var free = FormatBytes(drive.FreeBytes);
        return [new EventItem {
            Id = newId(),
            Timestamp = now,
            Kind = "watch.systemDriveLow",
            Severity = severity ?? Severity.Info,
            Title = severity is null ? $"System drive {drive.Name} has space again" : $"System drive {drive.Name} almost full",
            Message = severity is null ? $"{free} free." : $"{free} free. Windows, NINA's logs and Syncthing need room on {drive.Name}; free some space.",
            SessionId = SessionId,
            IncidentKey = "system-drive-low",
            IncidentPhase = severity is null ? "resolve" : "open",
        }];
    }

    public string? SessionId { get; set; }

    public const long Gb = 1024L * 1024 * 1024;

    public IReadOnlyList<EventItem> Observe(StorageState? storage, DateTimeOffset now, TimeSpan? nightLeft = null, TimeSpan? sessionElapsed = null) {
        if (storage is null || storage.TotalBytes <= 0) {
            return [];
        }
        var fraction = (double)storage.FreeBytes / storage.TotalBytes;
        // Tonight's saving rate (after at least half an hour, to be meaningful) times the dark hours left.
        var outOfSpaceTonight = storage.SessionBytes > 0 && nightLeft is { } left && sessionElapsed is { } elapsed && elapsed >= TimeSpan.FromMinutes(30)
            && storage.SessionBytes / elapsed.TotalHours * left.TotalHours > storage.FreeBytes;
        var severity = storage.FreeBytes < 10 * Gb || fraction < 0.01 || outOfSpaceTonight ? Severity.Critical
            : storage.FreeBytes < 50 * Gb || fraction < 0.05 ? Severity.Warning
            : null;

        if (severity == openSeverity) {
            return [];
        }
        var free = FormatBytes(storage.FreeBytes);
        var events = new List<EventItem>();
        if (severity is null) {
            openSeverity = null;
            events.Add(Make(now, Severity.Info, "Image drive has space again", $"{free} free.", "resolve"));
            return events;
        }
        openSeverity = severity;
        events.Add(Make(now, severity, severity == Severity.Critical ? "Image drive almost full" : "Image drive getting full",
            outOfSpaceTonight ? $"{free} free: at tonight's rate it fills before dawn." : $"{free} free ({fraction:P0}). NINA stops saving images when it's full.",
            "open"));
        return events;
    }

    private EventItem Make(DateTimeOffset now, string severity, string title, string message, string phase) => new() {
        Id = newId(),
        Timestamp = now,
        Kind = "watch.storageLow",
        Severity = severity,
        Title = title,
        Message = message,
        SessionId = SessionId,
        IncidentKey = "storage-low",
        IncidentPhase = phase,
    };

    public static string FormatBytes(long bytes) => bytes >= Gb ? $"{bytes / (double)Gb:0.#} GB" : $"{bytes / (1024.0 * 1024):0} MB";
}
