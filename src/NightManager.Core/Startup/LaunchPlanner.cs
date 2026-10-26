namespace NightManager.Core.Startup;

/// <summary>
/// Turns the app's startup plan into the NINA instances to start, in order: the master profile first (it controls the
/// mount), then the others in the plan's order, each a fixed delay after the one before. A sequence is started only
/// when the plan asks (or the PC went down mid-night while imaging) and this PC allows starting sequences from the app.
/// </summary>
public static class LaunchPlanner {
    /// <summary>In <see cref="Context.Running"/>: a NINA is open whose profile can't be told (started by hand).</summary>
    public const string UnknownProfile = "?";

    public sealed record Context {
        public required StartupPlan Plan { get; init; }
        public required IReadOnlyList<ProfileInfo> Profiles { get; init; }
        /// <summary>Profile ids already open in a running NINA.</summary>
        public IReadOnlySet<string> Running { get; init; } = new HashSet<string>();
        /// <summary>Profile ids whose rig allows starting a sequence from the app.</summary>
        public IReadOnlySet<string> SequenceStartAllowed { get; init; } = new HashSet<string>();
        /// <summary>Profile ids whose sequence should be restarted (the PC went down mid-night while imaging).</summary>
        public IReadOnlySet<string> Resume { get; init; } = new HashSet<string>();
        public int DefaultDelaySeconds { get; init; } = StartupDefaults.DelaySeconds;
        /// <summary>Whether a file exists (the sequence may have been moved since it was chosen).</summary>
        public Func<string, bool> FileExists { get; init; } = File.Exists;
    }

    public static IReadOnlyList<LaunchStep> Plan(Context c) {
        if (!c.Plan.Enabled) return [];
        var delay = TimeSpan.FromSeconds(Math.Clamp(c.Plan.DelaySeconds ?? c.DefaultDelaySeconds, StartupDefaults.MinDelaySeconds, StartupDefaults.MaxDelaySeconds));
        var wanted = c.Plan.Profiles.Where(p => p.Start).GroupBy(p => p.ProfileId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        // The master first; the rest keep the plan's order.
        var ordered = wanted.OrderBy(p => string.Equals(p.ProfileId, c.Plan.MasterProfileId, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

        var steps = new List<LaunchStep>();
        var startedBefore = false;
        foreach (var p in ordered) {
            var profile = c.Profiles.FirstOrDefault(x => string.Equals(x.Id, p.ProfileId, StringComparison.OrdinalIgnoreCase));
            var name = profile?.Name ?? p.ProfileId;
            // Someone already opened NINA by hand: it may hold this profile's equipment, so nothing more is started.
            if (c.Running.Contains(UnknownProfile)) {
                steps.Add(new LaunchStep { ProfileId = p.ProfileId, ProfileName = name, Skipped = "NINA was already open on this PC, so nothing more was started." });
                continue;
            }
            if (profile is null) {
                steps.Add(new LaunchStep { ProfileId = p.ProfileId, ProfileName = name, Skipped = "This profile isn't on this PC any more." });
                continue;
            }
            if (c.Running.Contains(profile.Id)) {
                steps.Add(new LaunchStep { ProfileId = profile.Id, ProfileName = name, Skipped = "NINA is already running with this profile." });
                continue;
            }

            string? note = null;
            var sequence = string.IsNullOrWhiteSpace(p.SequencePath) ? null : p.SequencePath;
            if (sequence is not null && !c.FileExists(sequence)) {
                note = $"The sequence {Path.GetFileName(sequence)} wasn't found, so NINA starts without it.";
                sequence = null;
            }
            var resuming = c.Resume.Contains(profile.Id);
            var wantsRun = (p.RunSequence || resuming) && sequence is not null;
            var allowed = c.SequenceStartAllowed.Contains(profile.Id);
            if (wantsRun && !allowed) note = "The sequence is loaded but not started: tick \"Allow starting the sequence from the app\" in NINA on this PC.";
            if (resuming && sequence is null) note ??= "It was imaging when the PC went down, but no sequence is set for this profile, so NINA starts without one.";
            var run = wantsRun && allowed;

            var args = new List<string> { "--profileid", profile.Id };
            if (sequence is not null) { args.Add("--sequencefile"); args.Add(sequence); }
            if (run) args.Add("--runsequence");

            steps.Add(new LaunchStep {
                ProfileId = profile.Id, ProfileName = name, DelayBefore = startedBefore ? delay : TimeSpan.Zero,
                Arguments = args, SequencePath = sequence, RunSequence = run, Resuming = resuming && run, Note = note,
            });
            startedBefore = true;
        }
        return steps;
    }

    /// <summary>
    /// Which profiles to restart: imaging when the PC went quiet, and still before that night's dawn. Without the
    /// backend (no network at boot) the plugin's own open session stands in, with the dawn from the last plan.
    /// </summary>
    public static IReadOnlySet<string> ResumeSet(IEnumerable<RigResumeInfo> rigs, DateTimeOffset now) =>
        rigs.Where(r => r.LostWhileImaging && r.NightEndsAt is { } dawn && now < dawn)
            .Select(r => r.ProfileId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>One command line, quoted for Windows (paths with spaces, as NINA's install folder has).</summary>
    public static string CommandLine(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    private static string Quote(string arg) {
        if (arg.Length > 0 && arg.All(ch => !char.IsWhiteSpace(ch) && ch != '"')) return arg;
        // Backslashes before a quote are doubled, then the quote escaped (Windows argument rules).
        var escaped = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in arg) {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { escaped.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            escaped.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        escaped.Append('\\', slashes * 2).Append('"');
        return escaped.ToString();
    }
}
