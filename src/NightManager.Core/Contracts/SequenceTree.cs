using System.Text.Json;

namespace NightManager.Core.Contracts;

/// <summary>
/// An Advanced Sequencer tree built on the phone. Compact: a node names a NINA type and carries only the settings
/// that differ from that type's defaults. The plugin builds real NINA objects from it with NINA's own factory.
/// </summary>
public sealed record SeqNode {
    /// <summary>The NINA type, e.g. "NINA.Sequencer.SequenceItem.Imaging.SmartExposure" (plugins' types too).</summary>
    public required string T { get; init; }
    /// <summary>Settings by property name (only those changed from the default).</summary>
    public Dictionary<string, JsonElement>? P { get; init; }
    /// <summary>Instructions inside a container. Absent: keep the type's own default children.</summary>
    public List<SeqNode>? I { get; init; }
    /// <summary>Conditions on a container (absent: defaults).</summary>
    public List<SeqNode>? C { get; init; }
    /// <summary>Triggers on a container (absent: defaults).</summary>
    public List<SeqNode>? G { get; init; }
    /// <summary>Disabled in NINA (greyed out, skipped).</summary>
    public bool? Off { get; init; }

    public IEnumerable<SeqNode> Children() => (I ?? []).Concat(C ?? []).Concat(G ?? []);
}

public static class SeqTemplateKinds {
    /// <summary>A whole sequence: start, target and end areas.</summary>
    public const string Sequence = "sequence";
    /// <summary>One container (e.g. a target template) for NINA's template library or to add to a sequence.</summary>
    public const string Block = "block";
}

public sealed record SeqTemplate {
    public int V { get; init; } = 1;
    public required string Name { get; init; }
    public string Kind { get; init; } = SeqTemplateKinds.Block;
    public SeqNode? Block { get; init; }
    public List<SeqNode>? Start { get; init; }
    public List<SeqNode>? Targets { get; init; }
    public List<SeqNode>? End { get; init; }

    public const int MaxNodes = 600;
    public const int MaxDepth = 14;

    public static SeqTemplate? FromPayload(JsonElement? payload) =>
        payload is { ValueKind: JsonValueKind.Object } p ? Json.NmJson.Deserialize<SeqTemplate>(p.GetRawText()) : null;

    public IEnumerable<SeqNode> Roots() => Kind == SeqTemplateKinds.Sequence
        ? (Start ?? []).Concat(Targets ?? []).Concat(End ?? [])
        : Block is null ? [] : [Block];

    /// <summary>Structural problems (sizes, depth, unknown types against a catalog). Empty: fine.</summary>
    public IReadOnlyList<string> Problems(ISet<string>? knownTypes = null) {
        var problems = new List<string>();
        if (Kind == SeqTemplateKinds.Block && Block is null) {
            problems.Add("The template is empty.");
        }
        var count = 0;
        var unknown = new SortedSet<string>();
        void Walk(SeqNode node, int depth) {
            count++;
            if (depth > MaxDepth) {
                problems.Add($"Nested deeper than {MaxDepth} levels.");
                return;
            }
            if (knownTypes is not null && !knownTypes.Contains(node.T)) {
                unknown.Add(node.T[(node.T.LastIndexOf('.') + 1)..]);
            }
            foreach (var child in node.Children()) {
                Walk(child, depth + 1);
            }
        }
        foreach (var root in Roots()) {
            Walk(root, 1);
        }
        if (count > MaxNodes) {
            problems.Add($"Too many items ({count}); at most {MaxNodes}.");
        }
        if (unknown.Count > 0) {
            problems.Add($"This NINA doesn't have: {string.Join(", ", unknown)} (a plugin may be missing).");
        }
        return problems.Distinct().ToList();
    }
}

/// <summary>What this NINA can build: every container, instruction, trigger and condition, with their settings.</summary>
public sealed record SeqCatalog {
    public int V { get; init; } = 1;
    public required string Hash { get; init; }
    public string? NinaVersion { get; init; }
    public IReadOnlyList<SeqEntity> Entities { get; init; } = [];
    public IReadOnlyList<string> Filters { get; init; } = [];
    /// <summary>Time sources for "Wait for time" and similar (sunset, dawn, meridian…).</summary>
    public IReadOnlyList<SeqChoice> Providers { get; init; } = [];
    /// <summary>The user's NINA templates (names).</summary>
    public IReadOnlyList<string> Templates { get; init; } = [];
}

public sealed record SeqEntity {
    public required string T { get; init; }
    /// <summary>item | container | trigger | condition</summary>
    public required string K { get; init; }
    public required string N { get; init; }
    public string? Cat { get; init; }
    public string? D { get; init; }
    public IReadOnlyList<SeqProp> P { get; init; } = [];
    /// <summary>For containers: their default contents (e.g. Smart Exposure's filter, exposure and loop).</summary>
    public SeqNode? Def { get; init; }
}

public sealed record SeqProp {
    public required string N { get; init; }
    /// <summary>bool | int | double | string | enum | filter | binning | target | provider | imageType</summary>
    public required string K { get; init; }
    public JsonElement? Def { get; init; }
    public IReadOnlyList<string>? Opts { get; init; }
}

public sealed record SeqChoice {
    public required string Id { get; init; }
    public required string N { get; init; }
}

/// <summary>Where a template goes in NINA.</summary>
public static class TemplateModes {
    /// <summary>Saved into NINA's templates library (a .template.json file).</summary>
    public const string NinaTemplate = "ninaTemplate";
    /// <summary>A target block into NINA's Targets library.</summary>
    public const string Library = "library";
    /// <summary>A block added at the end of the loaded sequence's targets.</summary>
    public const string Append = "append";
    /// <summary>A whole sequence loaded in place of the current one (backed up first). Not while running.</summary>
    public const string Load = "load";

    public static readonly IReadOnlyList<string> All = [NinaTemplate, Library, Append, Load];
}

/// <summary>Catalog item: everything this NINA can build. Sent when it changes.</summary>
public sealed record SequencerCatalogItem : IngestItem {
    public required SeqCatalog Catalog { get; init; }
}

/// <summary>A NINA template or the loaded sequence, as a tree for editing on the phone.</summary>
public sealed record SequenceTreeItem : IngestItem {
    /// <summary>The template's name, or null for the loaded sequence.</summary>
    public string? Source { get; init; }
    public required SeqTemplate Template { get; init; }
}
