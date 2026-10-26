using System.Globalization;
using NightManager.Core.Contracts;

namespace NightManager.Core.Scheduler;

/// <summary>
/// A Target Scheduler announcement, normalised by the NINA adapter (which turns TS's Coordinates
/// header into J2000 RA/Dec so this library stays free of NINA types).
/// </summary>
public sealed record SchedulerMessage {
    public required string Topic { get; init; }
    public object? Content { get; init; }
    public DateTimeOffset? Expiration { get; init; }
    public IReadOnlyDictionary<string, object?> Headers { get; init; } = new Dictionary<string, object?>();
    public double? RightAscension { get; init; }
    public double? Declination { get; init; }
}

/// <summary>A timeline entry worth recording for a scheduler transition.</summary>
public sealed record SchedulerEventInfo(string Kind, string Title, string? Message, IReadOnlyDictionary<string, object?> Data);

/// <summary>
/// Turns Target Scheduler's message-broker topics into live state. Topics (TS 4.8+/5.x, message version 2):
/// NewTargetStart, TargetStart (each plan), TargetComplete, WaitStart, ContainerStopped.
/// </summary>
public sealed class SchedulerTracker {
    public const string TopicPrefix = "TargetScheduler-";
    public const string NewTargetStart = TopicPrefix + "NewTargetStart";
    public const string TargetStart = TopicPrefix + "TargetStart";
    public const string TargetComplete = TopicPrefix + "TargetComplete";
    public const string WaitStart = TopicPrefix + "WaitStart";
    public const string ContainerStopped = TopicPrefix + "ContainerStopped";

    public static IReadOnlyList<string> Topics { get; } = [NewTargetStart, TargetStart, TargetComplete, WaitStart, ContainerStopped];

    private readonly object gate = new();

    public SchedulerState? Current { get; private set; }

    /// <summary>True once TS has announced anything in this NINA session.</summary>
    public bool HasSeenScheduler => Current is not null;

    /// <summary>Applies a message; returns an event for the timeline when the transition is meaningful.</summary>
    public SchedulerEventInfo? Apply(SchedulerMessage message, DateTimeOffset now) {
        lock (gate) {
            var previous = Current;
            var project = Text(message.Headers, "ProjectName");
            var target = Text(message.Headers, "TargetName") ?? Text(message.Content);

            switch (message.Topic) {
                case NewTargetStart:
                case TargetStart: {
                    var filter = Text(message.Headers, "ExposureFilterName");
                    Current = new SchedulerState {
                        Status = SchedulerStatus.Imaging,
                        ProjectName = project ?? previous?.ProjectName,
                        TargetName = target,
                        TargetRightAscension = message.RightAscension,
                        TargetDeclination = message.Declination,
                        Rotation = Number(message.Headers, "Rotation"),
                        // TargetStart (each plan) doesn't repeat the exposure; keep NewTargetStart's for the same target.
                        Filter = filter ?? (SameTarget(previous, target) ? previous!.Filter : null),
                        ExposureSeconds = Number(message.Headers, "ExposureLength") ?? (SameTarget(previous, target) ? previous!.ExposureSeconds : null),
                        Gain = Text(message.Headers, "ExposureGain") ?? (SameTarget(previous, target) ? previous!.Gain : null),
                        Offset = Text(message.Headers, "ExposureOffset") ?? (SameTarget(previous, target) ? previous!.Offset : null),
                        Binning = Text(message.Headers, "ExposureBinning") ?? (SameTarget(previous, target) ? previous!.Binning : null),
                        PlannedEnd = message.Expiration,
                        UpdatedAt = now,
                    };
                    // The sequencer reports the target change itself; nothing extra for the timeline.
                    return null;
                }

                case WaitStart: {
                    var until = message.Expiration ?? AsDate(message.Content);
                    Current = new SchedulerState {
                        Status = SchedulerStatus.Waiting,
                        ProjectName = project,
                        NextTargetName = target,
                        WaitUntil = until,
                        UpdatedAt = now,
                    };
                    if (previous?.Status == SchedulerStatus.Waiting && previous.WaitUntil == until) {
                        return null;
                    }
                    var title = target is null ? "Target Scheduler is waiting" : $"Waiting for {target}";
                    return new SchedulerEventInfo(EventKinds.SchedulerWaiting, title,
                        until is null ? null : $"Next target available at {until.Value.ToLocalTime():HH:mm}",
                        new Dictionary<string, object?> { ["target"] = target, ["project"] = project, ["until"] = until });
                }

                case TargetComplete: {
                    if (target is null) {
                        return null;
                    }
                    return new SchedulerEventInfo(EventKinds.SchedulerTargetComplete, $"{target} complete",
                        project is null ? "Target Scheduler finished this target's exposure plan." : $"Project {project}: every exposure plan for this target is done.",
                        new Dictionary<string, object?> { ["target"] = target, ["project"] = project });
                }

                case ContainerStopped: {
                    if (previous?.Status == SchedulerStatus.Stopped) {
                        return null;
                    }
                    Current = new SchedulerState { Status = SchedulerStatus.Stopped, ProjectName = previous?.ProjectName, UpdatedAt = now };
                    // Only worth a timeline entry if it was actually doing something.
                    return previous is null ? null : new SchedulerEventInfo(EventKinds.SchedulerStopped, "Target Scheduler stopped",
                        "No more targets are available, or the sequence moved past the scheduler.", new Dictionary<string, object?>());
                }

                default:
                    return null;
            }
        }
    }

    private static bool SameTarget(SchedulerState? previous, string? target) =>
        previous?.Status == SchedulerStatus.Imaging && string.Equals(previous.TargetName, target, StringComparison.OrdinalIgnoreCase);

    private static string? Text(IReadOnlyDictionary<string, object?> headers, string key) =>
        headers.TryGetValue(key, out var value) ? Text(value) : null;

    private static string? Text(object? value) {
        var text = value switch {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static double? Number(IReadOnlyDictionary<string, object?> headers, string key) {
        if (!headers.TryGetValue(key, out var value) || value is null) {
            return null;
        }
        return value switch {
            double d when double.IsFinite(d) => d,
            float f when float.IsFinite(f) => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? AsDate(object? value) => value switch {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Local) : dt),
        _ => null,
    };
}
