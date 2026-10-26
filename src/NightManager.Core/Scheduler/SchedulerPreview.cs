using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Scheduler;

/// <summary>
/// Reads the response of Target Scheduler's <c>GET /ts/v0/profiles/{id}/preview</c> (TS 5.x): its own
/// planner's schedule for the night, as consecutive target stretches and waits.
/// </summary>
public static class SchedulerPreview {
    /// <param name="json">The response body: an array of { Id, Name, WaitPeriod, StartTime, EndTime, ExposurePlan[] }.</param>
    /// <param name="projectOfTarget">Target GUID → project name, from the TS database.</param>
    /// <param name="local">Interprets times without an offset (TS serialises local DateTimes).</param>
    public static IReadOnlyList<PlanEntry> Parse(string json, IReadOnlyDictionary<string, string> projectOfTarget, Func<DateTime, DateTimeOffset>? local = null) {
        local ??= dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local));
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) {
            throw new FormatException("Target Scheduler preview is not a list");
        }

        var entries = new List<PlanEntry>();
        foreach (var item in doc.RootElement.EnumerateArray()) {
            var start = Time(item, "StartTime", local);
            var end = Time(item, "EndTime", local);
            if (start is null || end is null || end < start) {
                continue;
            }

            var wait = item.TryGetProperty("WaitPeriod", out var w) && w.ValueKind == JsonValueKind.True;
            var id = Str(item, "Id");
            var exposures = new List<PlanExposure>();

            if (item.TryGetProperty("ExposurePlan", out var plans) && plans.ValueKind == JsonValueKind.Array) {
                foreach (var plan in plans.EnumerateArray()) {
                    var filter = Str(plan, "FilterName");
                    var count = plan.TryGetProperty("Count", out var c) && c.TryGetInt32(out var n) ? n : 0;
                    if (filter is null || count <= 0) {
                        continue;
                    }
                    // Merge consecutive blocks of the same filter and exposure (TS may alternate filters).
                    var seconds = plan.TryGetProperty("Exposure", out var e) && e.TryGetDouble(out var s) ? s : 0;
                    var existing = exposures.FindIndex(x => x.Filter == filter && Math.Abs(x.ExposureSeconds - seconds) < 0.001);
                    if (existing >= 0) {
                        exposures[existing] = exposures[existing] with { Count = exposures[existing].Count + count };
                    } else {
                        exposures.Add(new PlanExposure { Filter = filter, ExposureSeconds = seconds, Count = count });
                    }
                }
            }

            entries.Add(new PlanEntry {
                Wait = wait,
                TargetId = wait ? null : id,
                TargetName = wait ? null : Str(item, "Name"),
                ProjectName = !wait && id is not null && projectOfTarget.TryGetValue(id, out var project) ? project : null,
                Start = start.Value,
                End = end.Value,
                Exposures = exposures,
            });
        }

        return entries;
    }

    /// <summary>Content fingerprint that ignores ids and generation times, so unchanged plans aren't re-sent.</summary>
    public static string Signature(SchedulerPlanItem item) {
        var stable = item with { Id = "-", Timestamp = DateTimeOffset.UnixEpoch, PreviewGeneratedAt = null };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(NmJson.Serialize<IngestItem>(stable)));
        return Convert.ToHexString(bytes);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name, Func<DateTime, DateTimeOffset> local) {
        var text = Str(element, name);
        if (text is null) {
            return null;
        }
        // With an offset (Local/Utc kinds) it's unambiguous; without one it's the observatory PC's local time.
        if (text.EndsWith('Z') || HasOffset(text)) {
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto : null;
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? local(dt) : null;
    }

    private static bool HasOffset(string text) {
        var t = text.IndexOf('T');
        return t > 0 && (text.IndexOf('+', t) > 0 || text.IndexOf('-', t) > 0);
    }
}
