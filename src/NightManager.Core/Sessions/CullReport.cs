using System.Globalization;
using System.Text;
using NightManager.Core.Contracts;

namespace NightManager.Core.Sessions;

/// <summary>
/// Morning cull list written next to the images: a CSV with every graded light frame, and a
/// plain list of rejected file paths that can be fed to scripts or blink/subframe tools.
/// The plugin never deletes or moves images itself.
/// </summary>
public static class CullReport {
    public static string BuildCsv(IEnumerable<SessionFrame> frames) {
        var builder = new StringBuilder();
        builder.AppendLine("timestamp_utc,file,target,filter,exposure_s,hfr,stars,guiding_rms_arcsec,score,verdict,reasons");

        foreach (var frame in frames.Where(f => Analysis.FrameGrader.IsLight(f.ImageType)).OrderBy(f => f.Timestamp)) {
            builder.AppendJoin(',',
                Csv(frame.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(frame.LocalPath ?? frame.FileName),
                Csv(frame.Target),
                Csv(frame.Filter),
                Number(frame.ExposureSeconds),
                Number(frame.Hfr),
                frame.Stars?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Number(frame.GuidingRms),
                frame.Score.ToString(CultureInfo.InvariantCulture),
                frame.Verdict,
                Csv(string.Join(" | ", frame.Reasons)));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string BuildRejectList(IEnumerable<SessionFrame> frames) {
        var builder = new StringBuilder();

        foreach (var frame in frames.Where(f => f.Verdict == Verdicts.Reject).OrderBy(f => f.Timestamp)) {
            builder.AppendLine(frame.LocalPath ?? frame.FileName);
        }

        return builder.ToString();
    }

    /// <summary>Base file name for this night's reports, e.g. "NightManager_2026-10-12_a1b2c3d4".</summary>
    public static string BaseName(string sessionId, DateTimeOffset startedAt) =>
        $"NightManager_{startedAt.UtcDateTime:yyyy-MM-dd}_{sessionId[..Math.Min(8, sessionId.Length)]}";

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Csv(string? value) {
        if (string.IsNullOrEmpty(value)) {
            return string.Empty;
        }

        // Neutralise spreadsheet formula injection from user-controlled target names.
        if (value[0] is '=' or '+' or '-' or '@') {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
