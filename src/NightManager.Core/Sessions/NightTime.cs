namespace NightManager.Core.Sessions;

/// <summary>
/// Where the night went: every few seconds of darkness is put in one bucket, from what NINA is doing. Totals ride along
/// with the session's own updates (no extra traffic), so the app can say "autofocus took 52 minutes".
/// </summary>
public static class NightTime {
    public const string Imaging = "imaging";
    public const string Autofocus = "autofocus";
    public const string Flip = "flip";
    /// <summary>Slewing, plate solving and centring.</summary>
    public const string Slewing = "slewing";
    /// <summary>Starting guiding, calibrating, settling after a dither or a slew.</summary>
    public const string Guiding = "guiding";
    /// <summary>The sequence waiting on purpose: for dark, a time, a target to rise.</summary>
    public const string Waiting = "waiting";
    /// <summary>Unsafe (clouds, rain, wind) or waiting until safe.</summary>
    public const string Unsafe = "unsafe";
    /// <summary>Downloading, changing filters and everything else between exposures.</summary>
    public const string Overhead = "overhead";
    /// <summary>Dark, and no sequence running.</summary>
    public const string Idle = "idle";

    public const string AutofocusRuns = "autofocusRuns";
    public const string Flips = "flips";
    public const string Dithers = "dithers";

    /// <summary>What NINA is doing right now, in plain inputs (the plugin reads them from its snapshot).</summary>
    public sealed record Moment {
        public bool SequenceRunning { get; init; }
        public bool AutofocusRunning { get; init; }
        public bool Flipping { get; init; }
        public bool Unsafe { get; init; }
        public bool Slewing { get; init; }
        /// <summary>idle | imaging | waiting | busy (the status bar).</summary>
        public string? Activity { get; init; }
        public string? Waiting { get; init; }
        /// <summary>The status bar's lines: who reports, and what ("Guider", "Settling").</summary>
        public IReadOnlyList<(string Source, string Text)> Lines { get; init; } = [];
    }

    public static string Classify(Moment m) {
        if (m.AutofocusRunning) return Autofocus;
        if (!m.SequenceRunning) return Idle;
        if (m.Flipping) return Flip;
        if (m.Unsafe || m.Waiting?.Contains("safe", StringComparison.OrdinalIgnoreCase) == true) return Unsafe;
        bool Line(string word) => m.Lines.Any(l => l.Source.Contains(word, StringComparison.OrdinalIgnoreCase) || l.Text.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (m.Slewing || Line("Plate") || Line("Solv") || Line("Center") || Line("Centr") || Line("Slew")) return Slewing;
        if (m.Lines.Any(l => l.Source.Contains("Guid", StringComparison.OrdinalIgnoreCase)
                && (l.Text.Contains("Settl", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("Dither", StringComparison.OrdinalIgnoreCase)
                    || l.Text.Contains("Calibrat", StringComparison.OrdinalIgnoreCase) || l.Text.Contains("Start", StringComparison.OrdinalIgnoreCase)))) return Guiding;
        return m.Activity switch {
            "imaging" => Imaging,
            "waiting" => Waiting,
            _ => Overhead,
        };
    }
}
