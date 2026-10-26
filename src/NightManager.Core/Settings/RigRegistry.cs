using NightManager.Core.Json;

namespace NightManager.Core.Settings;

/// <summary>What one rig on this PC publishes about itself so other NINA instances can link to it.</summary>
public sealed record RigRecord {
    public required string RigId { get; init; }
    public string? RigName { get; init; }
    public RigRole Role { get; init; } = RigRole.Main;
    public string? SetupName { get; init; }
    public string? MainRigId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Name shown for the setup: explicit setup name, else the profile name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplaySetupName => SetupName ?? RigName ?? RigId;

    /// <summary>Label for pickers: "Twin RedCat (profile RedCat A)", or just the profile name when they match.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PickerLabel => SetupName is null || string.Equals(SetupName, RigName, StringComparison.OrdinalIgnoreCase)
        ? DisplaySetupName
        : $"{SetupName} (profile {RigName})";
}

/// <summary>
/// File-based registry under <c>rigs/&lt;rigId&gt;/rig.json</c>, shared by all NINA instances on this
/// PC (they run as the same Windows user). Linked rigs read it to offer the main rigs to link to.
/// </summary>
public sealed class RigRegistry {
    private const string FileName = "rig.json";
    private readonly string rigsDirectory;

    public RigRegistry(string rigsDirectory) {
        this.rigsDirectory = rigsDirectory;
    }

    public void Publish(RigRecord record) => SettingsFile.Save(Path.Combine(rigsDirectory, record.RigId, FileName), record);

    public IReadOnlyList<RigRecord> All() {
        if (!Directory.Exists(rigsDirectory)) {
            return [];
        }

        var records = new List<RigRecord>();

        foreach (var directory in Directory.EnumerateDirectories(rigsDirectory)) {
            var path = Path.Combine(directory, FileName);

            try {
                if (File.Exists(path) && NmJson.Deserialize<RigRecord>(File.ReadAllText(path)) is { } record) {
                    records.Add(record);
                }
            } catch (Exception) {
                // A rig being rewritten by another instance right now; it will be read next time.
            }
        }

        return records.OrderBy(r => r.DisplaySetupName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Main rigs a given rig may link to (never itself).</summary>
    public IReadOnlyList<RigRecord> MainRigs(string exceptRigId) =>
        All().Where(r => r.Role == RigRole.Main && !string.Equals(r.RigId, exceptRigId, StringComparison.OrdinalIgnoreCase)).ToList();

    public RigRecord? Find(string rigId) => All().FirstOrDefault(r => string.Equals(r.RigId, rigId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The setup a rig belongs to, resolved from its settings and the registry.</summary>
public sealed record SetupIdentity(string SetupId, string SetupName, RigRole Role, string? Problem) {
    public bool ControlsMount => Role == RigRole.Main;

    public static SetupIdentity Resolve(string rigId, string? rigName, RigSettings settings, RigRegistry registry) {
        if (settings.Role == RigRole.Linked) {
            if (settings.MainRigId is null) {
                return new SetupIdentity(rigId, settings.SetupName ?? rigName ?? rigId, RigRole.Linked,
                    "Linked, but no main rig is selected yet: this rig is shown on its own until you pick one.");
            }

            var main = registry.Find(settings.MainRigId);

            if (main is not null && main.Role != RigRole.Main) {
                return new SetupIdentity(settings.MainRigId, main.DisplaySetupName, RigRole.Linked,
                    $"\"{main.RigName}\" is no longer a main rig. Pick another main rig.");
            }

            // An unknown main rig (not started on this PC yet) still groups correctly in the app.
            return new SetupIdentity(settings.MainRigId, main?.DisplaySetupName ?? "Linked setup", RigRole.Linked, null);
        }

        return new SetupIdentity(rigId, settings.SetupName ?? rigName ?? rigId, RigRole.Main, null);
    }
}
