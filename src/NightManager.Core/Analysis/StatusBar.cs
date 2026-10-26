using System.Globalization;
using System.Text.RegularExpressions;
using NightManager.Core.Contracts;

namespace NightManager.Core.Analysis;

/// <summary>One line of NINA's status bar as NINA holds it (ApplicationStatus).</summary>
public sealed record RawStatus {
    public required string Source { get; init; }
    public string? Status { get; init; }
    public double Progress { get; init; } = -1;
    public int MaxProgress { get; init; } = 1;
    public bool IsPercent { get; init; } = true;
    public string? Status2 { get; init; }
    public string? Status3 { get; init; }
}

/// <summary>
/// Mirrors NINA's status bar for the app. NINA rewrites countdowns ten times a second ("Waiting 30:18",
/// "Waiting 01:02 / 05:00"); here the timer is cut off and turned into an end time, held steady between reads, so
/// the app counts down by itself and nothing needs sending until NINA moves on.
/// </summary>
public sealed partial class StatusBar {
    /// <summary>An end time that moves less than this between reads is kept, so the snapshot stays unchanged.</summary>
    private static readonly TimeSpan Jitter = TimeSpan.FromSeconds(4);

    private readonly Dictionary<string, DateTimeOffset> untils = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^(?<text>.*?)\s*(?<a>\d{1,3}:\d{2}(?::\d{2})?)\s*/\s*(?<b>\d{1,3}:\d{2}(?::\d{2})?)$")]
    private static partial Regex ClockOfClock();

    [GeneratedRegex(@"^(?<text>.*?)\s*(?<a>\d+(?:[.,]\d+)?)\s*s\s*/\s*(?<b>\d+(?:[.,]\d+)?)\s*s$")]
    private static partial Regex SecondsOfSeconds();

    [GeneratedRegex(@"^(?<text>.*?)\s*(?<a>\d{1,3}:\d{2}(?::\d{2})?)$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?<text>.*?)\s+(?<a>\d+)\s*s$")]
    private static partial Regex Seconds();

    /// <summary>The status bar's lines, timers turned into end times.</summary>
    public IReadOnlyList<ActivityLine> Read(IEnumerable<RawStatus> raw, DateTimeOffset now) {
        var lines = new List<ActivityLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var status in raw) {
            if (string.IsNullOrWhiteSpace(status.Status)) {
                continue;
            }

            var (text, remaining) = Split(status.Status.Trim());
            var key = status.Source + "\u001f" + text;
            DateTimeOffset? until = null;

            if (remaining is { } left) {
                var estimate = now + left;
                until = untils.TryGetValue(key, out var held) && (held - estimate).Duration() < Jitter ? held : Whole(estimate);
                untils[key] = until.Value;
                seen.Add(key);
            }

            lines.Add(new ActivityLine {
                Source = status.Source,
                Text = text.Length == 0 ? "Waiting" : text,
                Detail = Detail(status.Status2, status.Status3),
                Progress = ProgressOf(status),
                Count = !status.IsPercent && status.MaxProgress > 1 && status.Progress >= 0
                    ? $"{Math.Round(status.Progress).ToString(CultureInfo.InvariantCulture)} / {status.MaxProgress}" : null,
                Until = until,
            });
        }

        foreach (var stale in untils.Keys.Where(k => !seen.Contains(k)).ToList()) {
            untils.Remove(stale);
        }

        return lines;
    }

    /// <summary>
    /// The one-line summary: what the sequence waits for (with when), what it is doing, or whatever else NINA
    /// reports outside a sequence; "Idle" when the status bar is empty and nothing runs.
    /// </summary>
    public static NinaActivity Compose(IReadOnlyList<ActivityLine> lines, string sequenceSource, SequenceState? sequence, CameraState? camera) {
        var sequenceLine = lines.FirstOrDefault(l => string.Equals(l.Source, sequenceSource, StringComparison.OrdinalIgnoreCase));
        // Lines that only say "Idle" (NINA's sync service, for one) aren't activity.
        var others = lines.Where(l => l != sequenceLine && !string.Equals(l.Text, "Idle", StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = lines.Count > 0 ? lines : null;

        if (sequence?.Running == true) {
            if (sequence.Waiting is { } waiting) {
                return new NinaActivity {
                    State = ActivityStates.Waiting,
                    Headline = waiting,
                    Detail = sequenceLine is null || IsPlainWait(sequenceLine.Text) ? null : sequenceLine.Text,
                    Until = sequenceLine?.Until ?? sequence.WaitingUntil,
                    Progress = sequenceLine?.Progress,
                    Lines = shown,
                };
            }

            var exposing = camera?.State == "exposing";
            var step = sequenceLine ?? others.FirstOrDefault();
            var stepIsExposure = step is null || step.Text.StartsWith("Exposing", StringComparison.OrdinalIgnoreCase);
            var target = string.IsNullOrWhiteSpace(sequence.TargetName) ? null : sequence.TargetName;

            if (step is not null && !stepIsExposure) {
                // A wait NINA's sequence reader didn't recognise (a plugin's own wait, e.g. Target Scheduler's).
                var waits = step.Text.StartsWith("Wait", StringComparison.OrdinalIgnoreCase);
                return new NinaActivity {
                    State = waits ? ActivityStates.Waiting : exposing ? ActivityStates.Imaging : ActivityStates.Busy,
                    Headline = step.Text,
                    Detail = step.Detail ?? target,
                    Until = step.Until,
                    Progress = step.Progress,
                    Lines = shown,
                };
            }

            return new NinaActivity {
                State = exposing ? ActivityStates.Imaging : ActivityStates.Busy,
                Headline = exposing ? (target is null ? "Imaging" : $"Imaging {target}") : target is null ? "Sequence running" : $"Sequence running · {target}",
                Lines = shown,
            };
        }

        if (others.Count > 0 || sequenceLine is not null) {
            var first = others.FirstOrDefault() ?? sequenceLine!;
            return new NinaActivity {
                State = ActivityStates.Busy,
                Headline = first.Text,
                Detail = first.Detail ?? (string.Equals(first.Text, first.Source, StringComparison.OrdinalIgnoreCase) ? null : first.Source),
                Until = first.Until,
                Progress = first.Progress,
                Lines = shown,
            };
        }

        return new NinaActivity { State = ActivityStates.Idle, Headline = "Idle", Lines = shown };
    }

    /// <summary>Cuts a trailing timer off: "Waiting 30:18" → ("Waiting", 30 min 18 s left).</summary>
    public static (string Text, TimeSpan? Remaining) Split(string status) {
        if (ClockOfClock().Match(status) is { Success: true } cc && ParseClock(cc.Groups["a"].Value) is { } elapsed && ParseClock(cc.Groups["b"].Value) is { } total) {
            return (Tidy(cc.Groups["text"].Value), Max(total - elapsed));
        }
        if (SecondsOfSeconds().Match(status) is { Success: true } ss && ParseNumber(ss.Groups["a"].Value) is { } done && ParseNumber(ss.Groups["b"].Value) is { } all) {
            return (Tidy(ss.Groups["text"].Value), Max(TimeSpan.FromSeconds(all - done)));
        }
        if (Clock().Match(status) is { Success: true } c && ParseClock(c.Groups["a"].Value) is { } left) {
            return (Tidy(c.Groups["text"].Value), left);
        }
        if (Seconds().Match(status) is { Success: true } s && int.TryParse(s.Groups["a"].Value, out var secs)) {
            return (Tidy(s.Groups["text"].Value), TimeSpan.FromSeconds(secs));
        }
        return (status, null);
    }

    private static bool IsPlainWait(string text) =>
        text.Equals("Waiting", StringComparison.OrdinalIgnoreCase) || text.Equals("Waiting...", StringComparison.OrdinalIgnoreCase) || text.Equals("Waiting…", StringComparison.OrdinalIgnoreCase);

    private static string? Detail(string? status2, string? status3) {
        var parts = new[] { status2, status3 }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Split(s!.Trim()).Text).Where(s => s.Length > 0).ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static double? ProgressOf(RawStatus status) {
        if (status.Progress < 0) {
            return null;
        }
        var fraction = status.IsPercent ? status.Progress : status.MaxProgress > 0 ? status.Progress / status.MaxProgress : -1;
        if (fraction < 0 || double.IsNaN(fraction)) {
            return null;
        }
        // 5% steps: enough for a bar, and the snapshot doesn't change on every read.
        return Math.Round(Math.Clamp(fraction, 0, 1) * 20) / 20;
    }

    private static TimeSpan? ParseClock(string value) {
        var parts = value.Split(':');
        if (!parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _))) {
            return null;
        }
        var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 3 ? new TimeSpan(n[0], n[1], n[2]) : new TimeSpan(0, n[0], n[1]);
    }

    private static double? ParseNumber(string value) =>
        double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static TimeSpan Max(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static string Tidy(string text) => text.Trim().TrimEnd(':', '-', '·').Trim();

    private static DateTimeOffset Whole(DateTimeOffset value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Offset);
}
