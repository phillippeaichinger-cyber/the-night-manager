using NightManager.Core.Json;
using NightManager.Core.Logging;
using NightManager.Core.Security;
using NightManager.Core.Sync;

namespace NightManager.Core.Startup;

/// <summary>What the Launcher needs from Windows: NINA's processes, starting one, and waiting.</summary>
public interface ILauncherHost {
    /// <summary>Profile ids of the NINA instances running now (from their command lines).</summary>
    IReadOnlySet<string> RunningProfiles();
    /// <summary>Starts NINA with these arguments; false if Windows couldn't start it.</summary>
    bool Start(string ninaPath, IReadOnlyList<string> arguments);
    Task Delay(TimeSpan delay, CancellationToken token);
    DateTimeOffset Now { get; }
}

/// <summary>
/// Runs once when Windows logs on: says "PC up", fetches the startup plan saved in the app (or uses the last one when
/// there's no network), starts the master profile's NINA first and the others a delay apart, each with its sequence,
/// and reports every step. NINA's plugin takes over from there, as it always does.
/// </summary>
public sealed class LauncherRunner {
    public const string PlanPath = "/v2/launcher/plan";
    public const string StatusPath = "/v2/launcher/status";

    private readonly ILauncherHost host;
    private readonly IApiTransport transport;
    private readonly Func<ApiCredential?> credential;
    private readonly string cachePath;
    private readonly INmLog log;
    private readonly string version;

    public LauncherRunner(ILauncherHost host, IApiTransport transport, Func<ApiCredential?> credential, string cachePath, INmLog? log = null, string version = "") {
        this.host = host;
        this.transport = transport;
        this.credential = credential;
        this.cachePath = cachePath;
        this.log = log ?? NullLog.Instance;
        this.version = version;
    }

    /// <summary>Everything the run needs from this PC, gathered by the host before it starts.</summary>
    public sealed record Inputs {
        public required LauncherSettings Settings { get; init; }
        public required StartupInventory Inventory { get; init; }
        /// <summary>Profiles whose plugin session was still open (the PC went down before the night ended).</summary>
        public IReadOnlySet<string> OpenSessions { get; init; } = new HashSet<string>();
        /// <summary>How long to keep trying to reach the backend after logon (the network may still be coming up).</summary>
        public TimeSpan NetworkWait { get; init; } = TimeSpan.FromMinutes(3);
    }

    public sealed record Result(LauncherStatus Status, IReadOnlyList<LaunchStep> Steps, bool UsedCachedPlan);

    public async Task<Result> RunAsync(Inputs input, CancellationToken token) {
        var response = await FetchPlanAsync(input, token);
        var usedCache = false;
        if (response is null) {
            response = LoadCache();
            usedCache = true;
            log.Warning(response is null ? "Launcher: no network and no saved plan; NINA isn't started." : "Launcher: no network; using the last saved plan.");
        } else {
            SaveCache(response);
        }
        if (response is null) return new Result(new LauncherStatus { Phase = "failed", Message = "No network and no saved startup plan.", LauncherVersion = version }, [], true);

        if (!response.Plan.Enabled || !input.Settings.Enabled) {
            var off = new LauncherStatus { Phase = "disabled", LauncherVersion = version, Message = input.Settings.Enabled ? "Startup plan is off in the app." : "Starting NINA at logon is off on this PC." };
            await ReportAsync(off, token);
            return new Result(off, [], usedCache);
        }

        // Mid-night restart: the backend's word when it was reached; without it, the plugin's own open session.
        var resume = usedCache
            ? input.OpenSessions.Where(id => response.Rigs.FirstOrDefault(r => string.Equals(r.ProfileId, id, StringComparison.OrdinalIgnoreCase))?.NightEndsAt is { } dawn && host.Now < dawn)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : LaunchPlanner.ResumeSet(response.Rigs, host.Now);

        var steps = LaunchPlanner.Plan(new LaunchPlanner.Context {
            Plan = response.Plan,
            Profiles = input.Inventory.Profiles,
            Running = host.RunningProfiles(),
            SequenceStartAllowed = input.Inventory.SequenceStartAllowed.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Resume = resume,
            DefaultDelaySeconds = input.Settings.DelaySeconds,
        });
        var reason = steps.Any(s => s.Resuming) ? "resume" : "boot";
        var states = steps.ToDictionary(s => s.ProfileId, s => s.Skipped is null ? "waiting" : "skipped", StringComparer.OrdinalIgnoreCase);

        LauncherStatus Status(string phase, string? message = null) => new() {
            Phase = phase, Reason = reason, LauncherVersion = version, Message = message,
            Instances = steps.Select(s => new InstanceStatus {
                ProfileId = s.ProfileId, Name = s.ProfileName, State = states[s.ProfileId],
                Sequence = s.SequencePath is null ? null : Path.GetFileNameWithoutExtension(s.SequencePath), RunSequence = s.RunSequence,
                Note = s.Skipped ?? s.Note,
            }).ToList(),
        };

        if (!input.Inventory.NinaFound) {
            var missing = Status("failed", $"NINA wasn't found at {input.Inventory.NinaPath}. Set its location in the plugin's Startup options.");
            await ReportAsync(missing, token);
            return new Result(missing, steps, usedCache);
        }

        await ReportAsync(Status("launching"), token);
        foreach (var step in steps.Where(s => s.Skipped is null)) {
            if (step.DelayBefore > TimeSpan.Zero) await host.Delay(step.DelayBefore, token);
            states[step.ProfileId] = "starting";
            log.Info($"Launcher: starting NINA with {step.ProfileName}: {LaunchPlanner.CommandLine(step.Arguments)}");
            if (!host.Start(input.Inventory.NinaPath, step.Arguments)) {
                states[step.ProfileId] = "failed";
                await ReportAsync(Status("launching"), token);
                continue;
            }
            // Give it a moment, then check the process is really there (a bad profile id closes NINA at once).
            await host.Delay(TimeSpan.FromSeconds(15), token);
            states[step.ProfileId] = host.RunningProfiles().Contains(step.ProfileId) ? "started" : "failed";
            await ReportAsync(Status("launching"), token);
        }

        var final = Status(states.Values.Any(v => v == "failed") ? "failed" : "started");
        await ReportAsync(final, token);
        return new Result(final, steps, usedCache);
    }

    /// <summary>"PC up" plus what's on this PC; the answer is the plan. Retries while the network comes up.</summary>
    private async Task<LauncherPlanResponse?> FetchPlanAsync(Inputs input, CancellationToken token) {
        var deadline = host.Now + input.NetworkWait;
        var body = NmJson.Serialize(new { phase = "booted", inventory = input.Inventory, launcherVersion = version });
        var wait = TimeSpan.FromSeconds(5);
        while (true) {
            var key = credential();
            if (key is null) { log.Warning("Launcher: this PC isn't paired."); return null; }
            try {
                var result = await transport.PostJsonAsync(PlanPath, body, key, token);
                if (result.StatusCode is >= 200 and < 300 && result.Body is { Length: > 0 } text) return NmJson.Deserialize<LauncherPlanResponse>(text);
                if (result.StatusCode is 401 or 403 or 404) { log.Warning($"Launcher: plan refused ({result.StatusCode})."); return null; }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                log.Info($"Launcher: backend not reachable yet ({ex.Message}).");
            }
            if (host.Now + wait > deadline) return null;
            await host.Delay(wait, token);
            wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 30));
        }
    }

    private async Task ReportAsync(LauncherStatus status, CancellationToken token) {
        var key = credential();
        if (key is null) return;
        try {
            await transport.PostJsonAsync(StatusPath, NmJson.Serialize(status), key, token);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            log.Info($"Launcher: status not sent ({ex.Message}).");
        }
    }

    private LauncherPlanResponse? LoadCache() {
        try {
            return File.Exists(cachePath) ? NmJson.Deserialize<LauncherPlanResponse>(File.ReadAllText(cachePath)) : null;
        } catch (Exception) {
            return null;
        }
    }

    private void SaveCache(LauncherPlanResponse response) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, NmJson.Serialize(response));
        } catch (Exception ex) {
            log.Warning($"Launcher: couldn't save the plan for offline starts ({ex.Message}).");
        }
    }
}
