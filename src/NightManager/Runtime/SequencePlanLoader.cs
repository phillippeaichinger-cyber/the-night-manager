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
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.Sequencer.Trigger.Guider;
using NINA.Sequencer.Utility;
using System.IO;
using System.Reflection;
using System.Windows;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Turns a plan from the phone into real Advanced Sequencer targets, using NINA's own sequencer factory so the
    /// result is exactly what dragging items in by hand would give. NINA doesn't hand the factory to plugins; like
    /// the ninaAPI plugin, it's read from the sequencer screen (two private fields stable across NINA 3.x).
    /// </summary>
    internal sealed class SequencePlanLoader {
        private readonly ISequenceMediator sequence;
        private readonly IProfileService profiles;
        private ISequencerFactory factory;

        public SequencePlanLoader(ISequenceMediator sequence, IProfileService profiles) {
            this.sequence = sequence;
            this.profiles = profiles;
        }

        public IReadOnlyList<string> Filters =>
            profiles.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? [];

        public IReadOnlyList<string> TemplateNames() =>
            OnUi(() => sequence.GetDeepSkyObjectContainerTemplates()?.Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList() ?? []);

        // MARK: Load

        /// <summary>Loads a plan; returns what happened, in words. Throws with a plain message when it can't.</summary>
        public string Load(SequencePlan plan, string mode, bool start, bool startAllowed) {
            var problems = plan.Problems(Filters);
            if (problems.Count > 0) {
                throw new InvalidOperationException(string.Join(" ", problems.Take(4)));
            }
            var running = sequence.IsAdvancedSequenceRunning();
            if (running && mode is LoadModes.Replace) {
                throw new InvalidOperationException("A sequence is running: choose Append, Run next or Save to library, or stop it first.");
            }
            if (!running && mode is LoadModes.Next) {
                mode = LoadModes.Append;
            }
            if (start && (!startAllowed || running || mode == LoadModes.Library)) {
                throw new InvalidOperationException(!startAllowed
                    ? "Starting sequences from the app is turned off in NINA (Options → Plugins → The Night Manager)."
                    : running ? "A sequence is already running." : "A plan saved to the library can't be started.");
            }

            var names = string.Join(", ", plan.Targets.Select(t => t.Name));
            return OnUi(() => {
                var containers = plan.Targets.Select(t => BuildTarget(plan, t)).ToList();
                switch (mode) {
                    case LoadModes.Library:
                        SaveToLibrary(containers);
                        return $"Saved {Count(containers)} to NINA's Targets library: {names}.";
                    case LoadModes.Next:
                        InsertNext(containers);
                        return $"Added {Count(containers)} to run next: {names}.";
                    case LoadModes.Replace:
                        var backup = Backup();
                        ReplaceTargets(containers);
                        StartIfAsked(start);
                        return $"Replaced the sequence's targets with {names}. The previous sequence is saved as {Path.GetFileName(backup)}." + (start ? " Started." : "");
                    default:
                        foreach (var container in containers) {
                            sequence.AddAdvancedTarget(container);
                        }
                        StartIfAsked(start);
                        return $"Added {Count(containers)} to the end of the sequence: {names}." + (start ? " Started." : "");
                }
            });
        }

        private static string Count(IReadOnlyCollection<IDeepSkyObjectContainer> c) => c.Count == 1 ? "1 target" : $"{c.Count} targets";

        private void StartIfAsked(bool start) {
            if (start) {
                // Runs for the whole night: don't wait for it. Validation was done on the plan.
                _ = sequence.StartAdvancedSequence(true);
            }
        }

        private IDeepSkyObjectContainer BuildTarget(SequencePlan plan, PlanTarget target) {
            var factory = Factory();
            IDeepSkyObjectContainer container = null;
            if (!string.IsNullOrWhiteSpace(plan.TargetTemplate)) {
                var template = sequence.GetDeepSkyObjectContainerTemplates()
                    ?.FirstOrDefault(t => string.Equals(t.Name, plan.TargetTemplate, StringComparison.OrdinalIgnoreCase));
                container = template?.Clone() as IDeepSkyObjectContainer
                    ?? throw new InvalidOperationException($"NINA has no target template called \"{plan.TargetTemplate}\".");
            }
            var fromTemplate = container is not null;
            if (container is null) {
                container = factory.GetContainer<DeepSkyObjectContainer>();
                if (factory.GetItem<Center>() is { } center) {
                    container.Add(center);
                }
            }

            container.Name = target.Name;
            container.Target.TargetName = target.Name;
            container.Target.InputCoordinates.Coordinates = new Coordinates(Angle.ByHours(target.Ra), Angle.ByDegree(target.Dec), Epoch.J2000);
            if (target.Rotation is double rotation) {
                container.Target.PositionAngle = rotation;
            }

            // The template's own exposures make way for the plan's, in the same place.
            var items = container.GetItemsSnapshot().ToList();
            var at = items.FindIndex(i => i is SmartExposure or TakeManyExposures or TakeExposure);
            foreach (var old in items.Where(i => i is SmartExposure or TakeManyExposures or TakeExposure)) {
                container.Remove(old);
            }
            if (at < 0) {
                at = container.GetItemsSnapshot().Count;
            }
            foreach (var line in target.Exposures) {
                Insert(container, at++, BuildExposure(factory, line));
            }

            AddAutofocus(factory, container, plan.Autofocus, fromTemplate);
            return container;
        }

        private ISequenceItem BuildExposure(ISequencerFactory factory, ExposureLine line) {
            var smart = factory.GetItem<SmartExposure>() ?? throw new InvalidOperationException("NINA's Smart Exposure isn't available.");
            if (!string.IsNullOrWhiteSpace(line.Filter)) {
                smart.GetSwitchFilter().Filter = profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters
                    .First(f => string.Equals(f.Name, line.Filter, StringComparison.OrdinalIgnoreCase));
            }
            var take = smart.GetTakeExposure();
            take.ExposureTime = line.Seconds;
            if (line.Gain is int gain) {
                take.Gain = gain;
            }
            if (line.Offset is int offset) {
                take.Offset = offset;
            }
            if (line.Bin is int bin && bin > 1) {
                take.Binning = new BinningMode((short)bin, (short)bin);
            }
            smart.GetLoopCondition().Iterations = line.Count;
            if (line.DitherEvery is int every) {
                var dither = smart.GetDitherAfterExposures();
                if (every <= 0) {
                    smart.Remove(dither);
                } else {
                    dither.AfterExposures = every;
                }
            }
            return smart;
        }

        /// <summary>Autofocus rules go on the target unless the template already has that kind of trigger.</summary>
        private static void AddAutofocus(ISequencerFactory factory, IDeepSkyObjectContainer container, AutofocusRules rules, bool fromTemplate) {
            if (rules is null || container is not ITriggerable triggerable) {
                return;
            }
            var existing = triggerable.GetTriggersSnapshot();
            if (rules.OnFilterChange == true && !existing.OfType<AutofocusAfterFilterChange>().Any() && factory.GetTrigger<AutofocusAfterFilterChange>() is { } onFilter) {
                triggerable.Add(onFilter);
            }
            if (rules.EveryMinutes is double minutes && !existing.OfType<AutofocusAfterTimeTrigger>().Any() && factory.GetTrigger<AutofocusAfterTimeTrigger>() is { } timed) {
                timed.Amount = minutes;
                triggerable.Add(timed);
            }
            if (rules.TemperatureDelta is double delta && !existing.OfType<AutofocusAfterTemperatureChangeTrigger>().Any() && factory.GetTrigger<AutofocusAfterTemperatureChangeTrigger>() is { } temperature) {
                temperature.Amount = delta;
                triggerable.Add(temperature);
            }
        }

        // MARK: Modes

        private ISequenceRootContainer Root() {
            var first = sequence.GetAllTargetsInAdvancedSequence()?.FirstOrDefault(t => t?.Parent is not null);
            if (first is not null) {
                return ItemUtility.GetRootContainer(first.Parent);
            }
            // An empty sequence: reach the root through the sequencer screen.
            var nav = Field(sequence, "sequenceNavigation");
            var vm = nav?.GetType().GetProperty("Sequence2VM")?.GetValue(nav);
            var sequencer = vm?.GetType().GetProperty("Sequencer")?.GetValue(vm);
            return sequencer?.GetType().GetProperty("MainContainer")?.GetValue(sequencer) as ISequenceRootContainer
                ?? throw new InvalidOperationException("NINA's Advanced Sequencer isn't open yet.");
        }

        private static ISequenceContainer TargetArea(ISequenceRootContainer root) =>
            root.GetItemsSnapshot().OfType<TargetAreaContainer>().FirstOrDefault()
            ?? throw new InvalidOperationException("The sequence has no target area.");

        private void InsertNext(IReadOnlyList<IDeepSkyObjectContainer> containers) {
            var area = TargetArea(Root());
            var items = area.GetItemsSnapshot().ToList();
            var running = items.FindIndex(i => i.Status == SequenceEntityStatus.RUNNING);
            var at = running < 0 ? items.Count : running + 1;
            foreach (var container in containers) {
                Insert(area, at++, container);
            }
        }

        private void ReplaceTargets(IReadOnlyList<IDeepSkyObjectContainer> containers) {
            var area = TargetArea(Root());
            foreach (var item in area.GetItemsSnapshot().ToList()) {
                area.Remove(item);
            }
            foreach (var container in containers) {
                area.Add(container);
            }
        }

        /// <summary>Saves the current sequence next to NINA's own sequences before it's replaced.</summary>
        private string Backup() {
            var root = Root();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A", "NightManager backups");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Before phone plan {DateTime.Now:yyyy-MM-dd HH-mm-ss}.json");
            sequence.SaveContainer(root, path, CancellationToken.None).GetAwaiter().GetResult();
            return path;
        }

        /// <summary>NINA watches its Targets folder: a file saved there appears in the sidebar, with no dialog on the PC.</summary>
        private void SaveToLibrary(IReadOnlyList<IDeepSkyObjectContainer> containers) {
            var folder = profiles.ActiveProfile.SequenceSettings.SequencerTargetsFolder;
            Directory.CreateDirectory(folder);
            foreach (var container in containers) {
                var safe = string.Concat(container.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var path = Path.Combine(folder, $"{safe} (from phone).json");
                for (var i = 2; File.Exists(path); i++) {
                    path = Path.Combine(folder, $"{safe} (from phone {i}).json");
                }
                sequence.SaveContainer(container, path, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        // MARK: Export (Import from NINA on the phone)

        /// <summary>The loaded Advanced Sequence as a plan: targets, coordinates, rotation and exposure lines.</summary>
        public SequencePlan Export(string title) => OnUi(() => {
            var targets = new List<PlanTarget>();
            foreach (var container in sequence.GetAllTargetsInAdvancedSequence() ?? []) {
                if (container is null || container.Status == SequenceEntityStatus.DISABLED || targets.Count >= SequencePlan.MaxTargets) {
                    continue;
                }
                var coordinates = container.Target?.InputCoordinates?.Coordinates?.Transform(Epoch.J2000);
                var lines = new List<ExposureLine>();
                CollectLines(container, null, lines, 0);
                targets.Add(new PlanTarget {
                    Name = string.IsNullOrWhiteSpace(container.Target?.TargetName) ? container.Name : container.Target.TargetName,
                    Ra = coordinates is null ? 0 : Math.Round(coordinates.RA, 6),
                    Dec = coordinates is null ? 0 : Math.Round(coordinates.Dec, 5),
                    Rotation = container.Target is { PositionAngle: var pa } && pa != 0 ? Math.Round(pa, 2) : null,
                    Exposures = lines.Take(SequencePlan.MaxLines).ToList(),
                });
            }
            return new SequencePlan { Name = string.IsNullOrWhiteSpace(title) ? "Loaded in NINA" : title, Targets = targets };
        });

        private static string CollectLines(ISequenceContainer container, string filter, List<ExposureLine> lines, int depth) {
            if (depth > 12) {
                return filter;
            }
            int? count = container is IConditionable c ? c.GetConditionsSnapshot().OfType<LoopCondition>().FirstOrDefault()?.Iterations : null;
            int? dither = container is ITriggerable t ? t.GetTriggersSnapshot().OfType<DitherAfterExposures>().FirstOrDefault()?.AfterExposures : null;
            foreach (var item in container.GetItemsSnapshot()) {
                switch (item) {
                    case SwitchFilter s:
                        filter = s.Filter?.Name ?? filter;
                        break;
                    case TakeExposure e when (e.ImageType ?? "LIGHT") == "LIGHT":
                        lines.Add(new ExposureLine {
                            Filter = filter,
                            Seconds = Math.Round(e.ExposureTime, 3),
                            Count = Math.Max(1, count ?? 1),
                            Gain = e.Gain >= 0 ? e.Gain : null,
                            Offset = e.Offset >= 0 ? e.Offset : null,
                            Bin = e.Binning is { X: > 1 } b ? b.X : null,
                            DitherEvery = dither,
                        });
                        break;
                    case ISequenceContainer child:
                        var inner = CollectLines(child, filter, lines, depth + 1);
                        if (child is not (SmartExposure or TakeManyExposures)) {
                            filter = inner;
                        }
                        break;
                }
            }
            return filter;
        }

        // MARK: Plumbing

        /// <summary>Insert at a position (NINA's containers support it; the interface only has Add).</summary>
        private static void Insert(ISequenceContainer container, int index, ISequenceItem item) {
            if (container is SequenceContainer concrete) {
                concrete.InsertIntoSequenceBlocks(index, item);
            } else {
                container.Add(item);
            }
        }

        /// <summary>NINA's sequencer factory: its items (Centre, Run Autofocus, other plugins' instructions), ready to run.</summary>
        internal ISequencerFactory Factory() {
            if (factory is not null) {
                return factory;
            }
            var nav = Field(sequence, "sequenceNavigation");
            factory = Field(nav, "factory") as ISequencerFactory
                ?? throw new InvalidOperationException("Loading plans needs an update of The Night Manager for this NINA version (sequencer factory not found).");
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

        /// <summary>The sequencer lives on NINA's UI thread.</summary>
        private static T OnUi<T>(Func<T> work) {
            var dispatcher = Application.Current?.Dispatcher;
            return dispatcher is null || dispatcher.CheckAccess() ? work() : dispatcher.Invoke(work);
        }
    }
}
