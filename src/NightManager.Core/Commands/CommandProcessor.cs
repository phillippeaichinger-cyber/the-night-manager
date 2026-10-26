using NightManager.Core.Contracts;
using NightManager.Core.Logging;

namespace NightManager.Core.Commands;

/// <summary>Primitive actions the host (NINA adapter) can perform. Each throws on failure.</summary>
public interface ICommandExecutor {
    Task AbortSequenceAsync(CancellationToken token);

    Task StopGuidingAsync(CancellationToken token);

    Task ParkMountAsync(CancellationToken token);

    Task CloseDomeAsync(CancellationToken token);

    Task WarmCameraAsync(CancellationToken token);

    /// <summary>Which primitives have the equipment to act on right now (e.g. no dome connected).</summary>
    bool IsAvailable(string commandType);
}

/// <summary>
/// Executes remote commands safely: one at a time, each at most once, never after expiry,
/// and only when the observatory owner enabled that command type in NINA.
/// </summary>
public sealed class CommandProcessor {
    private const int RememberedIds = 500;

    private readonly ICommandExecutor executor;
    private readonly Func<IReadOnlySet<string>> allowedCommands;
    private readonly TimeProvider time;
    private readonly INmLog log;
    private readonly string? processedIdsPath;
    private readonly Func<bool> controlsMount;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly LinkedList<string> processedOrder = new();
    private readonly HashSet<string> processed = new(StringComparer.Ordinal);

    public CommandProcessor(
        ICommandExecutor executor,
        Func<IReadOnlySet<string>> allowedCommands,
        string? processedIdsPath = null,
        TimeProvider? time = null,
        INmLog? log = null,
        Func<bool>? controlsMount = null) {
        this.executor = executor;
        this.controlsMount = controlsMount ?? (() => true);
        this.allowedCommands = allowedCommands;
        this.processedIdsPath = processedIdsPath;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? NullLog.Instance;
        LoadProcessedIds();
    }

    /// <summary>Mount and dome actions belong to the main rig of a setup; linked optical paths never perform them.</summary>
    public static readonly IReadOnlySet<string> MountLevel = new HashSet<string> { CommandTypes.ParkMount, CommandTypes.CloseDome };

    /// <summary>Command types that are always permitted because they cannot affect the equipment.</summary>
    /// <summary>Syncthing commands are checked by the Syncthing settings ("allow control from the app"), not this list.</summary>
    public static readonly IReadOnlySet<string> AlwaysAllowed = new HashSet<string>(CommandTypes.PhoneControl.Concat(CommandTypes.Sync).Concat(CommandTypes.Plans)) { CommandTypes.Ping, CommandTypes.ImportHistory, CommandTypes.CheckReadiness, CommandTypes.AdviseExposure, CommandTypes.SetFilterBandwidths, CommandTypes.AdviseFocus };

    public static IReadOnlySet<string> DefaultAllowed => new HashSet<string> { CommandTypes.Ping, CommandTypes.AbortSequence };

    /// <summary>Handles answers, acknowledgements, pause/resume and finish (the phone side of the sequence's own instructions).</summary>
    public Func<RemoteCommand, CancellationToken, Task<string>>? PhoneControl { get; set; }

    /// <summary>
    /// Hands-on equipment and movement (<see cref="CommandTypes.Equipment"/>, <see cref="CommandTypes.Move"/>). Long jobs
    /// (slews, autofocus, loops) must start in the background and return at once, so a stop or an abort sent next
    /// is never stuck behind them.
    /// </summary>
    public Func<RemoteCommand, CancellationToken, Task<string>>? EquipmentControl { get; set; }

    /// <summary>Raised when a ping or command wants the latest state sent immediately.</summary>
    public event EventHandler? SnapshotRequested;

    public async Task<IReadOnlyList<CommandResultItem>> ProcessAsync(IEnumerable<RemoteCommand> commands, CancellationToken token) {
        var results = new List<CommandResultItem>();

        await serial.WaitAsync(token).ConfigureAwait(false);

        try {
            foreach (var command in commands.OrderBy(c => c.IssuedAt)) {
                if (!Remember(command.Id)) {
                    continue;
                }

                results.Add(await ExecuteOneAsync(command, token).ConfigureAwait(false));
            }

            SaveProcessedIds();
        } finally {
            serial.Release();
        }

        if (results.Count > 0) {
            SnapshotRequested?.Invoke(this, EventArgs.Empty);
        }

        return results;
    }

    private async Task<CommandResultItem> ExecuteOneAsync(RemoteCommand command, CancellationToken token) {
        var now = time.GetUtcNow();

        if (now > command.ExpiresAt) {
            return Result(command, CommandOutcomes.Expired, "The command expired before the observatory received it.");
        }

        if (!CommandTypes.All.Contains(command.Type)) {
            return Result(command, CommandOutcomes.Rejected, $"Unknown command '{command.Type}'. Update the Night Manager plugin.");
        }

        if (!AlwaysAllowed.Contains(command.Type) && !allowedCommands().Contains(command.Type)) {
            return Result(command, CommandOutcomes.Rejected, "This remote action is disabled on the observatory PC (NINA → Options → Plugins → The Night Manager).");
        }

        if (MountLevel.Contains(command.Type) && !controlsMount()) {
            return Result(command, CommandOutcomes.Rejected, "This rig is a linked optical path; the mount and dome are controlled by its main rig.");
        }

        log.Info($"Night Manager executing remote command {command.Type} ({command.Id}) issued by {command.IssuedBy ?? "unknown"}");

        try {
            var message = command.Type switch {
                CommandTypes.Ping => "Observatory is responding.",
                _ when CommandTypes.PhoneControl.Contains(command.Type) || CommandTypes.Sync.Contains(command.Type) || CommandTypes.Plans.Contains(command.Type)
                    || command.Type == CommandTypes.ImportHistory || command.Type == CommandTypes.CheckReadiness
                    || command.Type == CommandTypes.AdviseExposure || command.Type == CommandTypes.SetFilterBandwidths
                    || command.Type == CommandTypes.AdviseFocus =>
                    PhoneControl is { } handle ? await handle(command, token).ConfigureAwait(false)
                        : throw new InvalidOperationException("The Night Manager isn't ready for this yet."),
                CommandTypes.Equipment or CommandTypes.Move =>
                    EquipmentControl is { } control ? await control(command, token).ConfigureAwait(false)
                        : throw new InvalidOperationException("Equipment control isn't ready yet. Restart NINA once the plugin has updated."),
                CommandTypes.SafeShutdown => await SafeShutdownAsync(token).ConfigureAwait(false),
                _ => await RunPrimitiveAsync(command.Type, token).ConfigureAwait(false)
            };

            return Result(command, CommandOutcomes.Executed, message);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            log.Error($"Night Manager remote command {command.Type} failed", ex);
            return Result(command, CommandOutcomes.Failed, ex.Message);
        }
    }

    private async Task<string> RunPrimitiveAsync(string type, CancellationToken token) {
        if (!executor.IsAvailable(type)) {
            throw new InvalidOperationException(type switch {
                CommandTypes.StopGuiding => "No guider is connected.",
                CommandTypes.ParkMount => "No mount is connected.",
                CommandTypes.CloseDome => "No dome is connected.",
                CommandTypes.WarmCamera => "No camera is connected.",
                _ => "This action is not available right now."
            });
        }

        switch (type) {
            case CommandTypes.AbortSequence:
                await executor.AbortSequenceAsync(token).ConfigureAwait(false);
                return "Sequence aborted.";
            case CommandTypes.StopGuiding:
                await executor.StopGuidingAsync(token).ConfigureAwait(false);
                return "Guiding stopped.";
            case CommandTypes.ParkMount:
                await executor.ParkMountAsync(token).ConfigureAwait(false);
                return "Mount parked.";
            case CommandTypes.CloseDome:
                await executor.CloseDomeAsync(token).ConfigureAwait(false);
                return "Dome closed.";
            case CommandTypes.WarmCamera:
                await executor.WarmCameraAsync(token).ConfigureAwait(false);
                return "Camera warming started.";
            default:
                throw new InvalidOperationException($"Unsupported command {type}.");
        }
    }

    /// <summary>
    /// Every step is attempted even if an earlier one fails, because a half-finished shutdown
    /// (e.g. mount parked but dome left open) is worse than a best effort. Steps without
    /// equipment are skipped. Fails if any attempted step failed.
    /// </summary>
    private async Task<string> SafeShutdownAsync(CancellationToken token) {
        var steps = new (string Type, string Label, Func<CancellationToken, Task> Run)[] {
            (CommandTypes.AbortSequence, "abort sequence", executor.AbortSequenceAsync),
            (CommandTypes.StopGuiding, "stop guiding", executor.StopGuidingAsync),
            (CommandTypes.ParkMount, "park mount", executor.ParkMountAsync),
            (CommandTypes.CloseDome, "close dome", executor.CloseDomeAsync),
            (CommandTypes.WarmCamera, "warm camera", executor.WarmCameraAsync)
        };
        var done = new List<string>();
        var failed = new List<string>();

        var ownsMount = controlsMount();

        foreach (var step in steps) {
            // Linked paths leave parking and the dome to the main rig of the setup.
            if (!executor.IsAvailable(step.Type) || (MountLevel.Contains(step.Type) && !ownsMount)) {
                continue;
            }

            try {
                await step.Run(token).ConfigureAwait(false);
                done.Add(step.Label);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                log.Error($"Night Manager safe shutdown step '{step.Label}' failed", ex);
                failed.Add($"{step.Label} ({ex.Message})");
            }
        }

        var summary = done.Count > 0 ? "Done: " + string.Join(", ", done) + "." : "Nothing to do.";

        if (failed.Count > 0) {
            throw new InvalidOperationException(summary + " Failed: " + string.Join(", ", failed) + ".");
        }

        return summary;
    }

    private CommandResultItem Result(RemoteCommand command, string outcome, string message) => new() {
        Id = Guid.NewGuid().ToString("N"),
        Timestamp = time.GetUtcNow(),
        CommandId = command.Id,
        CommandType = command.Type,
        Outcome = outcome,
        Message = message
    };

    private bool Remember(string id) {
        if (!processed.Add(id)) {
            return false;
        }

        processedOrder.AddLast(id);

        while (processedOrder.Count > RememberedIds) {
            processed.Remove(processedOrder.First!.Value);
            processedOrder.RemoveFirst();
        }

        return true;
    }

    private void LoadProcessedIds() {
        if (processedIdsPath is null || !File.Exists(processedIdsPath)) {
            return;
        }

        foreach (var id in File.ReadAllLines(processedIdsPath).Where(l => l.Length > 0)) {
            Remember(id);
        }
    }

    private void SaveProcessedIds() {
        if (processedIdsPath is null) {
            return;
        }

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(processedIdsPath)!);
            File.WriteAllLines(processedIdsPath, processedOrder);
        } catch (IOException ex) {
            log.Warning($"Could not persist processed command ids: {ex.Message}");
        }
    }
}
