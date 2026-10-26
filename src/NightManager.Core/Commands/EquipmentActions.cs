using System.Globalization;
using NightManager.Core.Contracts;

namespace NightManager.Core.Commands;

/// <summary>
/// The hands-on actions the phone can ask for (docs/CONTROL.md). Each belongs to one command type (equipment or
/// move: separate ticks in NINA), says whether it may run while a sequence is running, and whether it needs the
/// mount (linked optical paths leave the mount and dome to their main rig).
/// </summary>
public static class EquipmentActions {
    public sealed record Action(string Name, string Type, bool WhileSequenceRuns, bool NeedsMount = false);

    private static readonly Action[] List = [
        // Whatever the phone set going (a connect, a slew, a loop): always allowed.
        new("job.stop", CommandTypes.Equipment, true),
        // Devices
        new("connect", CommandTypes.Equipment, false),
        new("disconnect", CommandTypes.Equipment, false),
        // Camera: cooling and the dew heater are fine mid-sequence; shots and loops are not.
        new("camera.cool", CommandTypes.Equipment, true),
        new("camera.warm", CommandTypes.Equipment, true),
        new("camera.dewHeater", CommandTypes.Equipment, true),
        new("camera.shot", CommandTypes.Equipment, false),
        new("camera.loop", CommandTypes.Equipment, false),
        new("camera.stop", CommandTypes.Equipment, true),
        // Focus and filters
        new("focuser.move", CommandTypes.Equipment, false),
        new("focuser.nudge", CommandTypes.Equipment, false),
        new("focuser.autofocus", CommandTypes.Equipment, false),
        new("focuser.stop", CommandTypes.Equipment, true),
        new("filter.set", CommandTypes.Equipment, false),
        // Guiding
        new("guider.start", CommandTypes.Equipment, false),
        new("guider.calibrate", CommandTypes.Equipment, false),
        new("guider.stop", CommandTypes.Equipment, true),
        // Rotator, flat panel, power switches
        new("rotator.move", CommandTypes.Equipment, false),
        new("flat.light", CommandTypes.Equipment, false),
        new("flat.brightness", CommandTypes.Equipment, false),
        new("flat.cover", CommandTypes.Equipment, false),
        new("switch.set", CommandTypes.Equipment, true),
        // Mount (moves the telescope)
        new("mount.unpark", CommandTypes.Move, false, NeedsMount: true),
        new("mount.park", CommandTypes.Move, false, NeedsMount: true),
        new("mount.home", CommandTypes.Move, false, NeedsMount: true),
        new("mount.slew", CommandTypes.Move, false, NeedsMount: true),
        new("mount.centre", CommandTypes.Move, false, NeedsMount: true),
        new("mount.tracking", CommandTypes.Move, false, NeedsMount: true),
        new("mount.stop", CommandTypes.Move, true, NeedsMount: true),
        // Dome (closing is always allowed: it's the safe way)
        new("dome.open", CommandTypes.Move, false, NeedsMount: true),
        new("dome.close", CommandTypes.Move, true, NeedsMount: true),
        new("dome.park", CommandTypes.Move, false, NeedsMount: true),
        new("dome.follow", CommandTypes.Move, true, NeedsMount: true),
        // Polar alignment (Three Point Polar Alignment plugin) and the sequence itself
        new("polar.start", CommandTypes.Move, false, NeedsMount: true),
        new("polar.pause", CommandTypes.Move, true, NeedsMount: true),
        new("polar.resume", CommandTypes.Move, true, NeedsMount: true),
        new("polar.stop", CommandTypes.Move, true, NeedsMount: true),
        new("sequence.start", CommandTypes.Move, false),
    ];

    public static readonly IReadOnlyDictionary<string, Action> ByName = List.ToDictionary(a => a.Name, StringComparer.Ordinal);

    /// <summary>Why this command can't run here, or null when it may.</summary>
    public static string? Refusal(RemoteCommand command, bool sequenceRunning, bool controlsMount) {
        var name = Arg(command, "action");
        if (name is null || !ByName.TryGetValue(name, out var action)) {
            return $"Unknown action \"{name}\". Update the Night Manager plugin.";
        }
        if (action.Type != command.Type) {
            return "This action was sent with the wrong permission.";
        }
        if (action.NeedsMount && !controlsMount) {
            return "This rig is a linked optical path: its main rig controls the mount and dome.";
        }
        if (sequenceRunning && !action.WhileSequenceRuns) {
            return "A sequence is running. Stop it (or wait for it to finish) first.";
        }
        return null;
    }

    public static string? Arg(RemoteCommand command, string key) =>
        command.Args is { } args && args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static double? Number(RemoteCommand command, string key) =>
        Arg(command, key) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    public static bool? Flag(RemoteCommand command, string key) => Arg(command, key) switch {
        "true" or "on" or "1" or "open" => true,
        "false" or "off" or "0" or "close" => false,
        _ => null
    };
}
