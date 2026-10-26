using System.Text.RegularExpressions;
using System.Xml;

namespace NightManager.Core.Startup;

/// <summary>
/// Finds what the app can choose from: NINA's profiles (each profile file's own id and name, and its sequence and
/// Templates folders) and the whole sequences in those folders and any the owner added. Reads NINA's data files as
/// documents; nothing is changed.
/// </summary>
public static partial class StartupInventoryScanner {
    /// <summary>Sequences deeper than this below a folder are not looked for (keeps the scan quick).</summary>
    private const int MaxDepth = 3;
    private const int MaxSequences = 300;

    public static string DefaultProfilesFolder(string localAppData) => Path.Combine(localAppData, "NINA", "Profiles");

    public static string DefaultNinaPath(string programFiles) => Path.Combine(programFiles, StartupDefaults.NinaFolderName, "NINA.exe");

    /// <summary>
    /// Reads a file another program may have open: NINA locks the profile it's using (File.ReadAllText then fails), so
    /// the profile open in NINA used to be missing from the startup plan (seen on a remote rig, 23 Oct 2026).
    /// </summary>
    public static string ReadShared(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<ProfileInfo> Profiles(string folder) {
        if (!Directory.Exists(folder)) return [];
        var list = new List<ProfileInfo>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.profile")) {
            try {
                if (ReadProfile(ReadShared(file)) is { } profile) list.Add(profile);
            } catch (Exception) {
                // A damaged profile is skipped, not fatal.
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The profile's own id and name (children of the root), and its sequence folders (SequenceSettings).</summary>
    public static ProfileInfo? ReadProfile(string xml) {
        string? id = null, name = null, sequenceFolder = null, templatesFolder = null;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
        reader.MoveToContent();
        if (reader.LocalName != "Profile") return null;
        var rootDepth = reader.Depth;
        reader.Read();
        // ReadElementContentAsString already moves to the next node, so only Read() when nothing was taken.
        while (!reader.EOF) {
            if (reader.NodeType == XmlNodeType.Element) {
                var depth = reader.Depth - rootDepth;
                switch (reader.LocalName) {
                    case "Id" when depth == 1 && id is null: id = reader.ReadElementContentAsString(); continue;
                    case "Name" when depth == 1 && name is null: name = reader.ReadElementContentAsString(); continue;
                    case "DefaultSequenceFolder" when depth == 2: sequenceFolder = reader.ReadElementContentAsString(); continue;
                    case "SequencerTemplatesFolder" when depth == 2: templatesFolder = reader.ReadElementContentAsString(); continue;
                }
            }
            reader.Read();
        }
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out _)) return null;
        return new ProfileInfo {
            Id = id.Trim(), Name = string.IsNullOrWhiteSpace(name) ? id.Trim() : name.Trim(),
            SequenceFolder = Blank(sequenceFolder), TemplatesFolder = Blank(templatesFolder),
        };
    }

    /// <summary>Whole sequences (not templates or targets) in these folders, newest first, each listed once.</summary>
    public static IReadOnlyList<SequenceFile> Sequences(IEnumerable<string> folders) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<SequenceFile>();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Files(folder, 0)) {
                if (found.Count >= MaxSequences) break;
                var full = Path.GetFullPath(file);
                if (!seen.Add(full) || !IsWholeSequence(full)) continue;
                found.Add(new SequenceFile {
                    Path = full, Name = Path.GetFileNameWithoutExtension(full),
                    ModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero),
                });
            }
        }
        return found.OrderByDescending(s => s.ModifiedAt).ToList();
    }

    private static IEnumerable<string> Files(string folder, int depth) {
        IEnumerable<string> files, dirs;
        try {
            files = Directory.EnumerateFiles(folder, "*.json").ToList();
            dirs = depth < MaxDepth ? Directory.EnumerateDirectories(folder).ToList() : [];
        } catch (Exception) {
            yield break;
        }
        foreach (var f in files) yield return f;
        foreach (var d in dirs) foreach (var f in Files(d, depth + 1)) yield return f;
    }

    /// <summary>A sequence file's first <c>$type</c> is its root: only a SequenceRootContainer can be loaded with --sequencefile.</summary>
    public static bool IsWholeSequence(string path) {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            return IsWholeSequenceText(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        } catch (Exception) {
            return false;
        }
    }

    public static bool IsWholeSequenceText(string head) {
        var match = TypePattern().Match(head);
        return match.Success && match.Groups[1].Value.StartsWith(StartupDefaults.SequenceRootType, StringComparison.Ordinal);
    }

    [GeneratedRegex("\"\\$type\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex TypePattern();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Gathers this PC's startup inventory (shared by the plugin, which sends it, and the Launcher).</summary>
public static class StartupInventoryBuilder {
    /// <param name="root">The Night Manager folder (%LOCALAPPDATA%\NINA\NightManager): each rig's settings live under rigs\{profile id}.</param>
    public static StartupInventory Build(LauncherSettings settings, string root, string localAppData, string programFiles) {
        var profilesFolder = string.IsNullOrWhiteSpace(settings.ProfilesFolder) ? StartupInventoryScanner.DefaultProfilesFolder(localAppData) : settings.ProfilesFolder;
        var ninaPath = string.IsNullOrWhiteSpace(settings.NinaPath) ? StartupInventoryScanner.DefaultNinaPath(programFiles) : settings.NinaPath;
        var profiles = StartupInventoryScanner.Profiles(profilesFolder);
        var folders = profiles.SelectMany(p => new[] { p.SequenceFolder, p.TemplatesFolder }).OfType<string>().Concat(settings.SequenceFolders);
        return new StartupInventory {
            Profiles = profiles,
            Sequences = StartupInventoryScanner.Sequences(folders),
            NinaPath = ninaPath,
            NinaFound = File.Exists(ninaPath),
            SequenceStartAllowed = profiles.Where(p => AllowsStart(Path.Combine(root, "rigs", p.Id, "settings.json"))).Select(p => p.Id).ToList(),
            LauncherEnabled = settings.Enabled,
        };
    }

    /// <summary>Profiles whose plugin session was still open: the PC went down before that rig's night ended.</summary>
    public static IReadOnlySet<string> OpenSessions(string root, IEnumerable<ProfileInfo> profiles) =>
        profiles.Where(p => File.Exists(Path.Combine(root, "rigs", p.Id, "session.json"))).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rig's "Allow starting the sequence from the app", read without creating or changing the file.</summary>
    private static bool AllowsStart(string rigSettingsPath) {
        try {
            if (!File.Exists(rigSettingsPath)) return false;
            using var json = System.Text.Json.JsonDocument.Parse(StartupInventoryScanner.ReadShared(rigSettingsPath));
            foreach (var property in json.RootElement.EnumerateObject()) {
                if (string.Equals(property.Name, nameof(Settings.RigSettings.AllowStartFromApp), StringComparison.OrdinalIgnoreCase)) return property.Value.ValueKind == System.Text.Json.JsonValueKind.True;
            }
        } catch (Exception) {
        }
        return false;
    }
}
