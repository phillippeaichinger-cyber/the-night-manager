using System.Globalization;
using NightManager.Core.Contracts;

namespace NightManager.Core.Syncthing;

/// <summary>The daily upload window in the observatory PC's local time; it may cross midnight.</summary>
public static class SyncSchedule {
    public static TimeSpan? ParseTime(string? text) =>
        TimeSpan.TryParseExact(text?.Trim(), [@"hh\:mm", @"h\:mm"], CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;

    /// <summary>Whether uploads are allowed at <paramref name="localNow"/>. A window with equal start and end is always open.</summary>
    public static bool InWindow(SyncWindow window, DateTime localNow) {
        var start = ParseTime(window.Start) ?? TimeSpan.Zero;
        var end = ParseTime(window.End) ?? TimeSpan.Zero;
        var t = localNow.TimeOfDay;
        if (start == end) {
            return true;
        }
        return start < end ? t >= start && t < end : t >= start || t < end;
    }

    /// <summary>The next moment the window opens or closes, in local time.</summary>
    public static DateTime NextChange(SyncWindow window, DateTime localNow) {
        var start = ParseTime(window.Start) ?? TimeSpan.Zero;
        var end = ParseTime(window.End) ?? TimeSpan.Zero;
        var boundary = InWindow(window, localNow) ? end : start;
        var candidate = localNow.Date + boundary;
        return candidate > localNow ? candidate : candidate.AddDays(1);
    }
}
