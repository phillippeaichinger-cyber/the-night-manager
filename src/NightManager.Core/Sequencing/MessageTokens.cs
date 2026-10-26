using System.Globalization;
using System.Text.RegularExpressions;
using NightManager.Core.Contracts;

namespace NightManager.Core.Sequencing;

/// <summary>
/// Placeholders for messages typed into Night Manager instructions, e.g. "{target}: {subs} subs, HFR {hfr}".
/// Unknown placeholders are left as typed; known ones without a value become "–".
/// </summary>
public static class MessageTokens {
    public const string Help = "{target} {filter} {subs} {accepted} {usable} {hfr} {rms} {temp} {time} {rig}";

    private static readonly Regex Token = new(@"\{(\w+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Resolve(string? text, ObservatorySnapshot? s, string? rigName = null, DateTimeOffset? now = null, TimeZoneInfo? zone = null) {
        if (string.IsNullOrEmpty(text)) {
            return text ?? string.Empty;
        }
        var at = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, zone ?? TimeZoneInfo.Local);
        return Token.Replace(text, m => m.Groups[1].Value.ToLowerInvariant() switch {
            "target" => s?.Sequence?.TargetName ?? s?.Scheduler?.TargetName ?? "–",
            "filter" => s?.FilterWheel?.CurrentFilter ?? "–",
            "subs" => s?.Session?.Frames.ToString(CultureInfo.InvariantCulture) ?? "0",
            "accepted" => s?.Session?.Accepted.ToString(CultureInfo.InvariantCulture) ?? "0",
            "usable" => Duration(s?.Session?.IntegrationSeconds),
            "hfr" => s?.Session?.Last?.Hfr is double h ? h.ToString("0.00", CultureInfo.InvariantCulture) : "–",
            "rms" => s?.Guider?.RmsTotal is double r ? r.ToString("0.00", CultureInfo.InvariantCulture) + "″" : "–",
            "temp" => s?.Camera?.Temperature is double t ? t.ToString("0.0", CultureInfo.InvariantCulture) + "°C" : "–",
            "time" => at.ToString("HH:mm", CultureInfo.InvariantCulture),
            "rig" => rigName ?? "–",
            _ => m.Value,
        });
    }

    private static string Duration(double? seconds) {
        if (seconds is not double sec || sec <= 0) {
            return "0m";
        }
        var span = TimeSpan.FromSeconds(sec);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";
    }
}
