using NightManager.Core.Json;

namespace NightManager.Core.Readiness;

/// <summary>
/// What usually comes up on this rig: the devices connected once the equipment settled, for the last few nights.
/// Lets the check say "the focuser didn't come up (it did on the last 5 nights)" without knowing what the
/// sequence's scripts or switches do.
/// </summary>
public sealed class PowerBaseline {
    public const int NightsKept = 7;

    public sealed record Night(string Date, IReadOnlyList<string> Devices);

    public List<Night> Nights { get; init; } = [];

    /// <summary>Devices that came up on most recent nights, with how many of the last nights they did.</summary>
    public IReadOnlyList<(string Device, int Nights)> Expected() {
        var recent = Nights.TakeLast(5).ToList();
        if (recent.Count == 0) {
            return [];
        }
        var needed = recent.Count >= 3 ? recent.Count - 1 : recent.Count;
        return recent.SelectMany(n => n.Devices).GroupBy(d => d)
            .Select(g => (g.Key, g.Count()))
            .Where(d => d.Item2 >= needed)
            .OrderBy(d => d.Key)
            .ToList();
    }

    /// <summary>Remembers tonight's devices (replacing an earlier record for the same night).</summary>
    public void Record(string date, IEnumerable<string> devices) {
        Nights.RemoveAll(n => n.Date == date);
        Nights.Add(new Night(date, devices.Distinct().OrderBy(d => d).ToList()));
        Nights.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        while (Nights.Count > NightsKept) {
            Nights.RemoveAt(0);
        }
    }

    public static PowerBaseline Load(string path) {
        try {
            return File.Exists(path) ? NmJson.Deserialize<PowerBaseline>(File.ReadAllText(path)) ?? new() : new();
        } catch (Exception) {
            return new();
        }
    }

    public void Save(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, NmJson.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}
