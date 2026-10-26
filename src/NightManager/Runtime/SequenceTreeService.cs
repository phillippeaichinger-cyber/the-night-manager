
using NightManager.Core.Contracts;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Serialization;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Utility.DateTimeProvider;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using JsonProperty = Newtonsoft.Json.JsonPropertyAttribute;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// The phone's Advanced Sequencer editor, on the PC side: a catalog of everything this NINA can build (its own
    /// items and every installed plugin's), building real sequences and templates from the phone's compact trees,
    /// and turning NINA templates or the loaded sequence back into trees. Generic by design: settings are found by
    /// reflection on what NINA itself saves, so new NINA versions and plugins work without changes here.
    /// </summary>
    internal sealed class SequenceTreeService {
        private static readonly HashSet<string> Hidden = [
            "Status", "IsExpanded", "Name", "Description", "Category", "Icon", "Parent", "Issues", "ShowMenu",
            "ExposureCount", "CompletedIterations", "ProgressExposures", "Identifier", "SymbolBroker",
        ];

        private static readonly HashSet<string> Structural = [
            typeof(SequenceRootContainer).FullName, typeof(StartAreaContainer).FullName,
            typeof(TargetAreaContainer).FullName, typeof(EndAreaContainer).FullName,
        ];

        private readonly ISequenceMediator sequence;
        private readonly IProfileService profiles;
        private ISequencerFactory factory;

        public SequenceTreeService(ISequenceMediator sequence, IProfileService profiles) {
            this.sequence = sequence;
            this.profiles = profiles;
        }

        // MARK: Catalog

        public SeqCatalog BuildCatalog() => OnUi(() => {
            var f = Factory();
            var entities = new List<SeqEntity>();
            void AddAll(IEnumerable<ISequenceEntity> prototypes, Func<ISequenceEntity, string> kind) {
                foreach (var p in prototypes) {
                    if (p is null || Structural.Contains(p.GetType().FullName) || entities.Any(e => e.T == p.GetType().FullName)) {
                        continue;
                    }
                    try {
                        entities.Add(new SeqEntity {
                            T = p.GetType().FullName,
                            K = kind(p),
                            N = p.Name ?? p.GetType().Name,
                            Cat = p.Category,
                            D = Trim(p.Description, 240),
                            P = Editable(p.GetType()).Select(prop => Describe(prop, p)).Where(d => d is not null).ToList(),
                            Def = p is ISequenceContainer c ? DefaultContents(c) : null,
                        });
                    } catch (Exception ex) {
                        NinaLog.Instance.Debug($"Catalog skipped {p.GetType().Name}: {ex.Message}");
                    }
                }
            }
            AddAll(f.Container, _ => "container");
            AddAll(f.Items, p => p is ISequenceContainer ? "container" : "item");
            AddAll(f.Triggers, _ => "trigger");
            AddAll(f.Conditions, _ => "condition");

            var providers = f.DateTimeProviders?.Select(p => new SeqChoice { Id = p.GetType().Name, N = p.Name }).ToList() ?? [];
            var filters = profiles.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.Select(x => x.Name).ToList() ?? [];
            var templates = TemplateFiles().Select(Path.GetFileName).Select(n => n[..^".template.json".Length]).ToList();
            var body = JsonSerializer.Serialize(new { entities, filters, providers, templates });
            return new SeqCatalog {
                Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16],
                NinaVersion = NINA.Core.Utility.CoreUtil.Version,
                Entities = entities,
                Filters = filters,
                Providers = providers,
                Templates = templates,
            };
        });

        /// <summary>A container's own default contents (Smart Exposure's filter, exposure, loop and dither…).</summary>
        private SeqNode DefaultContents(ISequenceContainer container) {
            var node = Export(container);
            return node.I is { Count: > 0 } || node.C is { Count: > 0 } || node.G is { Count: > 0 } ? node with { P = null } : null;
        }

        /// <summary>What NINA saves for a type, in kinds the phone can edit.</summary>
        private static IEnumerable<PropertyInfo> Editable(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0)
                .Where(p => !Hidden.Contains(p.Name))
                .Where(p => p.GetCustomAttribute<JsonProperty>(true) is not null || p.GetCustomAttributes(true).Any(a => a.GetType().Name == "IsExpressionAttribute"))
                .Where(p => KindOf(p) is not null)
                .GroupBy(p => p.Name).Select(g => g.First());

        private static string KindOf(PropertyInfo p) {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (p.Name == "ImageType" && t == typeof(string)) return "imageType";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(int) || t == typeof(short) || t == typeof(long)) return "int";
            if (t == typeof(double) || t == typeof(float)) return "double";
            if (t == typeof(string)) return "string";
            if (t.IsEnum) return "enum";
            if (t == typeof(FilterInfo)) return "filter";
            if (t == typeof(BinningMode)) return "binning";
            if (t == typeof(InputTarget)) return "target";
            if (typeof(IDateTimeProvider).IsAssignableFrom(t)) return "provider";
            return null;
        }

        private static SeqProp Describe(PropertyInfo p, object prototype) {
            var kind = KindOf(p);
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            return new SeqProp {
                N = p.Name,
                K = kind,
                Def = Read(p, prototype),
                Opts = kind switch {
                    "enum" => Enum.GetNames(t),
                    "imageType" => ["LIGHT", "FLAT", "DARK", "BIAS", "SNAPSHOT"],
                    _ => null,
                },
            };
        }

        // MARK: Values

        private static JsonElement? Read(PropertyInfo p, object owner) {
            object value;
            try {
                value = p.GetValue(owner);
            } catch (Exception) {
                return null;
            }
            object plain = value switch {
                null => null,
                Enum e => e.ToString(),
                FilterInfo f => f.Name,
                BinningMode b => $"{b.X}x{b.Y}",
                InputTarget it => new {
                    name = it.TargetName,
                    ra = it.InputCoordinates?.Coordinates is { } c ? Math.Round(c.Transform(Epoch.J2000).RA, 6) : 0,
                    dec = it.InputCoordinates?.Coordinates is { } d ? Math.Round(d.Transform(Epoch.J2000).Dec, 5) : 0,
                    pa = Math.Round(it.PositionAngle, 2),
                },
                IDateTimeProvider provider => provider.GetType().Name,
                double d when !double.IsFinite(d) => null,
                float f when !float.IsFinite(f) => null,
                _ => value,
            };
            return plain is null ? null : JsonSerializer.SerializeToElement(plain);
        }

        private void Write(PropertyInfo p, object owner, JsonElement value) {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            switch (KindOf(p)) {
                // Lenient: Firestore can't always tell 1 from true, so either form is accepted.
                case "bool": p.SetValue(owner, value.ValueKind == JsonValueKind.Number ? value.GetDouble() != 0 : value.GetBoolean()); break;
                case "int": p.SetValue(owner, Convert.ChangeType(Math.Round(Number(value)), t)); break;
                case "double": p.SetValue(owner, Convert.ChangeType(Number(value), t)); break;
                case "string" or "imageType": p.SetValue(owner, value.GetString()); break;
                case "enum": p.SetValue(owner, Enum.Parse(t, value.GetString() ?? "", ignoreCase: true)); break;
                case "filter":
                    var name = value.GetString();
                    var filter = profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters
                        .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Filter \"{name}\" isn't in this NINA's filter wheel.");
                    p.SetValue(owner, filter);
                    break;
                case "binning":
                    var parts = (value.ValueKind == JsonValueKind.Number ? $"{value.GetInt32()}x{value.GetInt32()}" : value.GetString() ?? "1x1").Split('x');
                    p.SetValue(owner, new BinningMode(short.Parse(parts[0]), short.Parse(parts.Length > 1 ? parts[1] : parts[0])));
                    break;
                case "target":
                    var target = (InputTarget)p.GetValue(owner);
                    if (target is null) break;
                    if (value.TryGetProperty("name", out var n)) target.TargetName = n.GetString();
                    if (value.TryGetProperty("ra", out var ra) && value.TryGetProperty("dec", out var dec)) {
                        target.InputCoordinates.Coordinates = new Coordinates(Angle.ByHours(ra.GetDouble()), Angle.ByDegree(dec.GetDouble()), Epoch.J2000);
                    }
                    if (value.TryGetProperty("pa", out var pa)) target.PositionAngle = pa.GetDouble();
                    break;
                case "provider":
                    var id = value.GetString();
                    var match = Factory().DateTimeProviders?.FirstOrDefault(x => x.GetType().Name == id)
                        ?? throw new InvalidOperationException($"This NINA has no time source \"{id}\".");
                    p.SetValue(owner, match);
                    break;
            }
        }

        private static double Number(JsonElement value) => value.ValueKind switch {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String => double.Parse(value.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture),
            _ => value.GetDouble(),
        };

        // MARK: Build

        private ISequenceEntity Create(SeqNode node) {
            var f = Factory();
            var prototype = f.Container.Cast<ISequenceEntity>().Concat(f.Items).Concat(f.Triggers).Concat(f.Conditions)
                .FirstOrDefault(p => p.GetType().FullName == node.T)
                ?? throw new InvalidOperationException($"This NINA doesn't have {node.T[(node.T.LastIndexOf('.') + 1)..]} (a plugin may be missing).");
            var entity = (ISequenceEntity)prototype.Clone();
            var props = Editable(entity.GetType()).ToDictionary(p => p.Name);
            foreach (var (name, value) in node.P ?? []) {
                if (props.TryGetValue(name, out var prop)) {
                    try {
                        Write(prop, entity, value);
                    } catch (Exception ex) when (ex is not InvalidOperationException) {
                        throw new InvalidOperationException($"{entity.Name}: \"{name}\" couldn't be set ({ex.Message}).");
                    }
                }
            }
            if (entity is ISequenceContainer container) {
                if (node.I is not null) {
                    foreach (var old in container.GetItemsSnapshot().ToList()) container.Remove(old);
                    foreach (var child in node.I) container.Add((ISequenceItem)Create(child));
                }
                if (node.C is not null && container is IConditionable conditionable) {
                    foreach (var old in conditionable.GetConditionsSnapshot().ToList()) conditionable.Remove(old);
                    foreach (var child in node.C) conditionable.Add((ISequenceCondition)Create(child));
                }
                if (node.G is not null && container is ITriggerable triggerable) {
                    foreach (var old in triggerable.GetTriggersSnapshot().ToList()) triggerable.Remove(old);
                    foreach (var child in node.G) triggerable.Add((ISequenceTrigger)Create(child));
                }
            }
            if (node.Off == true && entity is ISequenceItem item) {
                item.Status = SequenceEntityStatus.DISABLED;
            }
            return entity;
        }

        /// <summary>Builds and places a template; returns what happened, in words.</summary>
        public string Apply(SeqTemplate template, string mode, bool start, bool startAllowed) {
            var catalog = BuildCatalog();
            var problems = template.Problems(catalog.Entities.Select(e => e.T).Concat(Structural).ToHashSet());
            if (problems.Count > 0) {
                throw new InvalidOperationException(string.Join(" ", problems.Take(3)));
            }
            var running = sequence.IsAdvancedSequenceRunning();
            if (start && (!startAllowed || running || mode != TemplateModes.Load)) {
                throw new InvalidOperationException(!startAllowed
                    ? "Starting sequences from the app is turned off in NINA (Options → Plugins → The Night Manager)."
                    : running ? "A sequence is already running." : "Only a loaded sequence can be started.");
            }

            return OnUi(() => {
                if (template.Kind == SeqTemplateKinds.Sequence) {
                    var root = BuildRoot(template);
                    switch (mode) {
                        case TemplateModes.Load:
                            if (running) {
                                throw new InvalidOperationException("A sequence is running: stop it first, or save this as a sequence file instead.");
                            }
                            var backup = Backup();
                            sequence.SetAdvancedSequence(root);
                            if (start) {
                                _ = sequence.StartAdvancedSequence(true);
                            }
                            return $"Loaded \"{template.Name}\" as the sequence. The previous one is saved as {Path.GetFileName(backup)}." + (start ? " Started." : "");
                        default:
                            var path = SaveFile(root, SequenceFolder(), template.Name, ".json");
                            return $"Saved \"{template.Name}\" as a sequence file: {Path.GetFileName(path)} (File → Load in NINA's Advanced Sequencer).";
                    }
                }

                var block = (ISequenceContainer)Create(template.Block);
                block.Name = template.Name;
                switch (mode) {
                    case TemplateModes.Library when block is IDeepSkyObjectContainer:
                        var target = SaveFile(block, profiles.ActiveProfile.SequenceSettings.SequencerTargetsFolder, template.Name, ".json");
                        return $"Saved \"{template.Name}\" to NINA's Targets library ({Path.GetFileName(target)}).";
                    case TemplateModes.Append:
                        if (block is IDeepSkyObjectContainer dso) {
                            sequence.AddAdvancedTarget(dso);
                        } else {
                            var area = RootTargetArea();
                            area.Add(block);
                        }
                        return $"Added \"{template.Name}\" at the end of the sequence's targets.";
                    default:
                        var file = SaveFile(block, profiles.ActiveProfile.SequenceSettings.SequencerTemplatesFolder, template.Name, ".template.json");
                        return $"Saved \"{template.Name}\" to NINA's templates ({Path.GetFileName(file)}). It appears in the Advanced Sequencer's Templates list.";
                }
            });
        }

        private ISequenceRootContainer BuildRoot(SeqTemplate template) {
            var f = Factory();
            var root = f.GetContainer<SequenceRootContainer>();
            root.Name = template.Name;
            ISequenceContainer Area<T>(List<SeqNode> nodes) where T : ISequenceContainer {
                var area = f.GetContainer<T>();
                foreach (var node in nodes ?? []) {
                    area.Add((ISequenceItem)Create(node));
                }
                return area;
            }
            root.Add(Area<StartAreaContainer>(template.Start));
            root.Add(Area<TargetAreaContainer>(template.Targets));
            root.Add(Area<EndAreaContainer>(template.End));
            return root;
        }

        // MARK: Export (edit NINA's templates or the loaded sequence on the phone)

        /// <summary>A NINA template file by name, or the loaded sequence when name is empty.</summary>
        public SeqTemplate Read(string name) => OnUi(() => {
            if (string.IsNullOrWhiteSpace(name)) {
                var root = LoadedRoot();
                var areas = root.GetItemsSnapshot();
                List<SeqNode> Area<T>() => areas.OfType<T>().Cast<ISequenceContainer>().FirstOrDefault()?.GetItemsSnapshot().Select(i => Export(i)).ToList() ?? [];
                return new SeqTemplate {
                    Name = Path.GetFileNameWithoutExtension(sequence.GetAdvancedSequencerSavePath()) is { Length: > 0 } title ? title : "Loaded sequence",
                    Kind = SeqTemplateKinds.Sequence,
                    Start = Area<StartAreaContainer>(),
                    Targets = Area<TargetAreaContainer>(),
                    End = Area<EndAreaContainer>(),
                };
            }
            var path = TemplateFiles().FirstOrDefault(p => string.Equals(Path.GetFileName(p), name + ".template.json", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"NINA has no template called \"{name}\".");
            var container = new SequenceJsonConverter(Factory()).Deserialize(File.ReadAllText(path));
            return new SeqTemplate { Name = name, Kind = SeqTemplateKinds.Block, Block = Export(container) };
        });

        /// <summary>A NINA object as a node: its type, the settings that differ from the type's defaults, its contents.</summary>
        private SeqNode Export(ISequenceEntity entity) {
            var f = Factory();
            var prototype = f.Container.Cast<ISequenceEntity>().Concat(f.Items).Concat(f.Triggers).Concat(f.Conditions)
                .FirstOrDefault(p => p.GetType() == entity.GetType());
            var props = new Dictionary<string, JsonElement>();
            foreach (var p in Editable(entity.GetType())) {
                var value = Read(p, entity);
                var standard = prototype is null ? null : Read(p, prototype);
                if (value is { } v && (standard is null || v.GetRawText() != standard.Value.GetRawText())) {
                    props[p.Name] = v;
                }
            }
            List<SeqNode> items = null, conditions = null, triggers = null;
            if (entity is ISequenceContainer container) {
                items = container.GetItemsSnapshot().Select(i => Export(i)).ToList();
                conditions = container is IConditionable c ? c.GetConditionsSnapshot().Select(x => Export(x)).ToList() : null;
                triggers = container is ITriggerable t ? t.GetTriggersSnapshot().Select(x => Export(x)).ToList() : null;
            }
            return new SeqNode {
                T = entity.GetType().FullName,
                P = props.Count > 0 ? props : null,
                I = items,
                C = conditions is { Count: > 0 } ? conditions : items is null ? null : [],
                G = triggers is { Count: > 0 } ? triggers : items is null ? null : [],
                Off = entity is ISequenceItem { Status: SequenceEntityStatus.DISABLED } ? true : null,
            };
        }

        // MARK: Files and roots

        private IEnumerable<string> TemplateFiles() {
            var folder = profiles.ActiveProfile?.SequenceSettings?.SequencerTemplatesFolder;
            return string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
                ? []
                : Directory.EnumerateFiles(folder, "*.template.json", SearchOption.AllDirectories).OrderBy(p => p);
        }

        private string SequenceFolder() {
            var folder = profiles.ActiveProfile?.SequenceSettings?.DefaultSequenceFolder;
            return string.IsNullOrWhiteSpace(folder) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A") : folder;
        }

        /// <summary>Saves without overwriting anything: a clash gets " (from phone 2)".</summary>
        private string SaveFile(ISequenceContainer container, string folder, string name, string extension) {
            Directory.CreateDirectory(folder);
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            var path = Path.Combine(folder, safe + extension);
            for (var i = 2; File.Exists(path); i++) {
                path = Path.Combine(folder, $"{safe} (from phone{(i > 2 ? $" {i - 1}" : "")}){extension}");
            }
            sequence.SaveContainer(container, path, CancellationToken.None).GetAwaiter().GetResult();
            return path;
        }

        private string Backup() {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A", "NightManager backups");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Before phone sequence {DateTime.Now:yyyy-MM-dd HH-mm-ss}.json");
            sequence.SaveContainer(LoadedRoot(), path, CancellationToken.None).GetAwaiter().GetResult();
            return path;
        }

        /// <summary>
        /// Every instruction of the loaded sequence (not its triggers or conditions), with its simple settings, for the
        /// readiness check. Null when no Advanced Sequence is loaded.
        /// </summary>
        public IReadOnlyList<NightManager.Core.Readiness.SequenceStep> Steps() {
            ISequenceRootContainer root;
            try {
                root = LoadedRoot();
            } catch (Exception) {
                return null;
            }
            var steps = new List<NightManager.Core.Readiness.SequenceStep>();
            static IReadOnlyList<string> IssuesOf(object entity) {
                try {
                    return entity is NINA.Sequencer.Validations.IValidatable v && v.Issues is { Count: > 0 } issues ? issues.Where(i => !string.IsNullOrWhiteSpace(i)).Take(5).ToList() : null;
                } catch (Exception) {
                    return null;
                }
            }
            void Add(object entity, string name, string kind) =>
                steps.Add(new NightManager.Core.Readiness.SequenceStep(entity.GetType().FullName ?? entity.GetType().Name, name, Settings(entity), kind, IssuesOf(entity)));
            void Walk(ISequenceItem item, int depth) {
                if (item is null || depth > 16 || steps.Count > 3000) return;
                if (item is ISequenceContainer container) {
                    Add(container, container.Name, NightManager.Core.Readiness.SequenceStep.Container);
                    if (container is NINA.Sequencer.Trigger.ITriggerable triggerable) {
                        foreach (var trigger in triggerable.GetTriggersSnapshot()) Add(trigger, trigger.Name, NightManager.Core.Readiness.SequenceStep.Trigger);
                    }
                    if (container is NINA.Sequencer.Conditions.IConditionable conditionable) {
                        foreach (var condition in conditionable.GetConditionsSnapshot()) Add(condition, condition.Name, NightManager.Core.Readiness.SequenceStep.Condition);
                    }
                    foreach (var child in container.GetItemsSnapshot()) Walk(child, depth + 1);
                    return;
                }
                Add(item, item.Name, NightManager.Core.Readiness.SequenceStep.Item);
            }
            Walk(root, 0);
            return steps;
        }

        /// <summary>An entity's simple settings (strings, numbers, switches), for reading the sequence generically.</summary>
        private static Dictionary<string, string> Settings(object item) {
                var settings = new Dictionary<string, string>();
                foreach (var property in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0 || Hidden.Contains(property.Name)) continue;
                    var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                    if (type != typeof(string) && !type.IsPrimitive && !type.IsEnum) continue;
                    try {
                        if (property.GetValue(item) is { } value && value.ToString() is { Length: > 0 and < 500 } text) settings[property.Name] = text;
                    } catch (Exception) {
                        // A property that throws when read is just skipped.
                    }
                }
                return settings;
        }

        private ISequenceRootContainer LoadedRoot() {
            var first = sequence.GetAllTargetsInAdvancedSequence()?.FirstOrDefault(t => t?.Parent is not null);
            if (first is not null) {
                return ItemUtility.GetRootContainer(first.Parent);
            }
            var nav = Field(sequence, "sequenceNavigation");
            var vm = nav?.GetType().GetProperty("Sequence2VM")?.GetValue(nav);
            var sequencer = vm?.GetType().GetProperty("Sequencer")?.GetValue(vm);
            return sequencer?.GetType().GetProperty("MainContainer")?.GetValue(sequencer) as ISequenceRootContainer
                ?? throw new InvalidOperationException("NINA's Advanced Sequencer isn't open yet.");
        }

        private ISequenceContainer RootTargetArea() =>
            LoadedRoot().GetItemsSnapshot().OfType<TargetAreaContainer>().FirstOrDefault()
            ?? throw new InvalidOperationException("The sequence has no target area.");

        private ISequencerFactory Factory() {
            if (factory is not null) {
                return factory;
            }
            factory = Field(Field(sequence, "sequenceNavigation"), "factory") as ISequencerFactory
                ?? throw new InvalidOperationException("Sequencer templates need an update of The Night Manager for this NINA version (sequencer factory not found).");
            return factory;
        }

        private static object Field(object owner, string name) {
            for (var type = owner?.GetType(); type is not null; type = type.BaseType) {
                if (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) is { } field) {
                    return field.GetValue(owner);
                }
            }
            return null;
        }

        private static string Trim(string text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..(max - 1)] + "…";

        private static T OnUi<T>(Func<T> work) {
            var dispatcher = Application.Current?.Dispatcher;
            return dispatcher is null || dispatcher.CheckAccess() ? work() : dispatcher.Invoke(work);
        }
    }
}
