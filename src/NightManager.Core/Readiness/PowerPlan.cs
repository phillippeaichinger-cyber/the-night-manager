namespace NightManager.Core.Readiness;

/// <summary>
/// One part of the loaded sequence, flattened: its type, display name and simple settings; whether it's an instruction,
/// a trigger, a condition or a container; and the problems NINA itself reports for it (its red warnings).
/// </summary>
public sealed record SequenceStep(string TypeName, string? Name, IReadOnlyDictionary<string, string> Settings,
    string Kind = SequenceStep.Item, IReadOnlyList<string>? Issues = null) {
    public const string Item = "item";
    public const string Trigger = "trigger";
    public const string Condition = "condition";
    public const string Container = "container";
}

/// <summary>
/// How the loaded sequence brings the equipment up, read generically so instructions from plugins we've never seen
/// still count: anything that connects equipment, sets a switch, runs a script or sends a request (smart plugs) is
/// a power-up step, whichever plugin it comes from.
/// </summary>
public sealed record PowerPlan {
    /// <summary>The sequence connects equipment itself (Connect All, Connect Equipment, or a plugin's equivalent).</summary>
    public bool ConnectsEquipment { get; init; }
    /// <summary>Connect Equipment steps name these devices ("Safety Monitor", "Switch"); empty with Connect All.</summary>
    public IReadOnlyList<string> ConnectedDevices { get; init; } = [];
    public bool ConnectsAll { get; init; }
    public int SwitchSteps { get; init; }
    public int RequestSteps { get; init; }
    /// <summary>Script files the sequence runs (External Script, a plugin's script step).</summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];

    public bool PowersUp => ConnectsEquipment || SwitchSteps > 0 || Scripts.Count > 0 || RequestSteps > 0;

    /// <summary>"Powers up 6 switch ports and runs 2 scripts, then connects all equipment."</summary>
    public string? Describe() {
        if (!PowersUp) {
            return null;
        }
        var parts = new List<string>();
        if (SwitchSteps > 0) parts.Add(SwitchSteps == 1 ? "sets 1 switch" : $"sets {SwitchSteps} switches");
        if (Scripts.Count > 0) parts.Add(Scripts.Count == 1 ? "runs 1 script" : $"runs {Scripts.Count} scripts");
        if (RequestSteps > 0) parts.Add(RequestSteps == 1 ? "sends 1 web request" : $"sends {RequestSteps} web requests");
        if (ConnectsAll) parts.Add("connects all equipment");
        else if (ConnectsEquipment) parts.Add($"connects {string.Join(", ", ConnectedDevices.Take(4))}");
        var text = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        return "The sequence " + text + ".";
    }

    private static readonly string[] ScriptKeys = ["Script", "ScriptPath", "Path", "FileName", "File", "Executable"];

    public static PowerPlan From(IEnumerable<SequenceStep> steps) {
        var devices = new List<string>();
        var scripts = new List<string>();
        bool connects = false, all = false;
        int switches = 0, requests = 0;
        foreach (var step in steps.Where(s => s.Kind == SequenceStep.Item)) {
            var type = Short(step.TypeName);
            if (type.Contains("Disconnect", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            if (type.Contains("Connect", StringComparison.OrdinalIgnoreCase) && !type.Contains("Reconnect", StringComparison.OrdinalIgnoreCase)) {
                connects = true;
                if (type.Contains("All", StringComparison.OrdinalIgnoreCase)) {
                    all = true;
                } else if (Setting(step, "SelectedDevice", "Device") is { } device) {
                    devices.Add(device);
                }
            } else if (type.Contains("Switch", StringComparison.OrdinalIgnoreCase) && type.Contains("Set", StringComparison.OrdinalIgnoreCase)) {
                switches++;
            } else if (type.Contains("Script", StringComparison.OrdinalIgnoreCase)) {
                if (ScriptKeys.Select(k => Setting(step, k)).FirstOrDefault(v => v is not null) is { } script) {
                    scripts.Add(script);
                }
            } else if (type.Contains("Http", StringComparison.OrdinalIgnoreCase) || type.Contains("Webhook", StringComparison.OrdinalIgnoreCase)) {
                requests++;
            }
        }
        return new PowerPlan {
            ConnectsEquipment = connects,
            ConnectsAll = all,
            ConnectedDevices = devices.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SwitchSteps = switches,
            RequestSteps = requests,
            Scripts = scripts.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>"NINA.Sequencer.SequenceItem.Switch.SetSwitchValue, NINA.Sequencer" → "SetSwitchValue".</summary>
    internal static string Short(string typeName) {
        var name = typeName.Split(',')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static string? Setting(SequenceStep step, params string[] keys) {
        foreach (var key in keys) {
            if (step.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) {
                return value.Trim();
            }
        }
        return null;
    }
}
