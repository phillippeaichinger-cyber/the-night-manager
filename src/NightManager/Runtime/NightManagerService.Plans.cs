using NightManager.Core.Settings;
using System.IO;
using NightManager.Core.Contracts;
using NightManager.Core.Sync;

namespace NightManager.Plugin.Runtime {

    /// <summary>Sequence plans from the phone (free tier): load them into NINA, and send the loaded sequence back.</summary>
    public sealed partial class NightManagerService {
        private static readonly TimeSpan ExportEvery = TimeSpan.FromMinutes(30);
        private SequencePlanLoader planLoader;
        private SequenceTreeService trees;
        private static readonly TimeSpan CatalogEvery = TimeSpan.FromHours(6);
        private DateTimeOffset nextCatalog = DateTimeOffset.MinValue;
        private int cataloguing;

        private string CatalogHashPath => Path.Combine(dataDirectory, "catalog-hash.txt");
        private DateTimeOffset nextExport = DateTimeOffset.MinValue;
        private int exporting;

        private string HandlePlanCommand(RemoteCommand command) {
            if (!Settings.AllowPlansFromApp) {
                throw new InvalidOperationException("Plans from the app are turned off in NINA (Options → Plugins → The Night Manager).");
            }
            if (!DataOn(DataFeatures.Sequence)) {
                throw new InvalidOperationException("Sequence sharing is switched off in the app (Settings → the observatory → Data from NINA).");
            }
            if (!SequencerReady()) {
                throw new InvalidOperationException("NINA's sequencer isn't ready yet.");
            }
            switch (command.Type) {
                case CommandTypes.ReadCatalog:
                    var catalog = SendCatalog(force: true);
                    return $"Sent the sequencer catalog: {catalog.Entities.Count} items.";
                case CommandTypes.ReadTemplate:
                    var name = command.Args is { } n && n.TryGetValue("name", out var v) ? v : null;
                    var tree = trees.Read(name);
                    Enqueue(new SequenceTreeItem { Id = SyncEngine.NewId(), Timestamp = DateTimeOffset.UtcNow, Source = string.IsNullOrWhiteSpace(name) ? null : name, Template = tree });
                    sync?.Wake();
                    return string.IsNullOrWhiteSpace(name) ? "Sent the loaded sequence." : $"Sent template \"{name}\".";
                case CommandTypes.ApplyTemplate:
                    var template = SeqTemplate.FromPayload(command.Payload) ?? throw new InvalidOperationException("The template didn't arrive. Send it again.");
                    var templateMode = command.Args is { } a2 && a2.TryGetValue("mode", out var m2) && TemplateModes.All.Contains(m2) ? m2 : TemplateModes.NinaTemplate;
                    var startIt = command.Args is { } b2 && b2.TryGetValue("start", out var s2) && s2 == "true";
                    var result = trees.Apply(template, templateMode, startIt, Settings.AllowStartFromApp);
                    var appliedBy = command.IssuedBy is { Length: > 0 } issuer ? $" by {issuer}" : "";
                    Enqueue(Event("sequence.templateApplied", Severity.Info, $"Template \"{template.Name}\" from the app{appliedBy}", result,
                        new Dictionary<string, object> { ["mode"] = templateMode, ["start"] = startIt }));
                    RefreshSequenceState();
                    SendCatalog(force: false);
                    PushSnapshot();
                    return result;
            }

            if (command.Type == CommandTypes.ReadSequence) {
                var sent = ExportSequence();
                return $"Sent the loaded sequence: {sent.Targets.Count} target{(sent.Targets.Count == 1 ? "" : "s")}.";
            }

            var plan = SequencePlan.FromPayload(command.Payload) ?? throw new InvalidOperationException("The plan didn't arrive. Send it again.");
            var mode = command.Args is { } a && a.TryGetValue("mode", out var m) && LoadModes.All.Contains(m) ? m : LoadModes.Append;
            var start = command.Args is { } b && b.TryGetValue("start", out var s) && s == "true";
            var message = planLoader.Load(plan, mode, start, Settings.AllowStartFromApp);

            var by = command.IssuedBy is { Length: > 0 } who ? $" by {who}" : "";
            Enqueue(Event("sequence.planLoaded", Severity.Info, $"Plan \"{plan.Name}\" loaded from the app{by}", message,
                new Dictionary<string, object> { ["mode"] = mode, ["targets"] = plan.Targets.Select(t => t.Name).ToList(), ["start"] = start }));
            RefreshSequenceState();
            ExportSequence();
            PushSnapshot();
            return message;
        }

        /// <summary>The loaded sequence and the user's templates, for the phone's builder; also every half hour.</summary>
        private SequencePlan ExportSequence() {
            var plan = planLoader.Export(sequenceState.Title);
            Enqueue(new SequenceExportItem {
                Id = SyncEngine.NewId(),
                Timestamp = DateTimeOffset.UtcNow,
                Title = sequenceState.Title,
                Running = sequenceState.Running,
                Plan = plan,
                Templates = planLoader.TemplateNames(),
            });
            nextExport = DateTimeOffset.UtcNow + ExportEvery;
            sync?.Wake();
            return plan;
        }

        /// <summary>Everything this NINA can build, for the phone's editor; only sent when it changed.</summary>
        private SeqCatalog SendCatalog(bool force) {
            var catalog = trees.BuildCatalog();
            var last = File.Exists(CatalogHashPath) ? File.ReadAllText(CatalogHashPath).Trim() : null;
            if (force || last != catalog.Hash) {
                Enqueue(new SequencerCatalogItem { Id = SyncEngine.NewId(), Timestamp = DateTimeOffset.UtcNow, Catalog = catalog });
                File.WriteAllText(CatalogHashPath, catalog.Hash);
                sync?.Wake();
                NinaLog.Instance.Info($"Sequencer catalog sent: {catalog.Entities.Count} items, {catalog.Templates.Count} templates.");
            }
            nextCatalog = DateTimeOffset.UtcNow + CatalogEvery;
            return catalog;
        }

        private void MaybeSendCatalog(DateTimeOffset now) {
            if (sync?.DataOffKnown != true || !DataOn(DataFeatures.Sequence) || trees is null || now < nextCatalog || !SequencerReady() || Interlocked.Exchange(ref cataloguing, 1) == 1) {
                return;
            }
            _ = Task.Run(() => {
                try {
                    SendCatalog(force: false);
                } catch (Exception ex) {
                    nextCatalog = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
                    NinaLog.Instance.Debug($"Sequencer catalog skipped: {ex.Message}");
                } finally {
                    Volatile.Write(ref cataloguing, 0);
                }
            });
        }

        private void MaybeExportSequence(DateTimeOffset now) {
            if (sync?.DataOffKnown != true || !DataOn(DataFeatures.Sequence) || planLoader is null || now < nextExport || !SequencerReady() || Interlocked.Exchange(ref exporting, 1) == 1) {
                return;
            }
            // The export reads the sequencer on NINA's UI thread: keep it off the tick.
            _ = Task.Run(() => {
                try {
                    ExportSequence();
                } catch (Exception ex) {
                    nextExport = DateTimeOffset.UtcNow + ExportEvery;
                    NinaLog.Instance.Debug($"Sequence export skipped: {ex.Message}");
                } finally {
                    Volatile.Write(ref exporting, 0);
                }
            });
        }
    }
}
