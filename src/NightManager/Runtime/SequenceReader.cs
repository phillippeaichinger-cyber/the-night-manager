using NightManager.Core.Contracts;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.FilterWheel;
using NINA.Sequencer.SequenceItem.Imaging;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Reads the loaded Advanced Sequence for people who don't use Target Scheduler: every target with its
    /// exposures (filter, length, done vs planned), and what a running wait instruction is waiting for.
    /// Only reads; never changes the sequence. Any surprise in a plugin's container is skipped, not fatal.
    /// </summary>
    internal static class SequenceReader {
        private const int MaxTargets = 30;
        private const int MaxDepth = 12;

        public static IReadOnlyList<SequenceTarget> Targets(IEnumerable<IDeepSkyObjectContainer> containers) {
            var result = new List<SequenceTarget>();
            foreach (var container in containers ?? []) {
                if (result.Count >= MaxTargets) {
                    break;
                }
                try {
                    if (container is null || container.Status == SequenceEntityStatus.DISABLED) {
                        continue;
                    }
                    var name = container.Target?.TargetName;
                    if (string.IsNullOrWhiteSpace(name)) {
                        name = container.Target?.DeepSkyObject?.Name;
                    }
                    if (string.IsNullOrWhiteSpace(name)) {
                        name = container.Name;
                    }
                    var coordinates = container.Target?.InputCoordinates?.Coordinates?.Transform(Epoch.J2000);
                    var exposures = new List<SequenceExposure>();
                    Collect(container, null, exposures, 0);
                    result.Add(new SequenceTarget {
                        Name = name,
                        RightAscension = coordinates is null || (coordinates.RA == 0 && coordinates.Dec == 0) ? null : Math.Round(coordinates.RA, 5),
                        Declination = coordinates is null || (coordinates.RA == 0 && coordinates.Dec == 0) ? null : Math.Round(coordinates.Dec, 4),
                        Status = container.Status switch {
                            SequenceEntityStatus.RUNNING => SequenceTargetStatus.Running,
                            SequenceEntityStatus.FINISHED => SequenceTargetStatus.Done,
                            SequenceEntityStatus.SKIPPED or SequenceEntityStatus.FAILED => SequenceTargetStatus.Skipped,
                            _ => SequenceTargetStatus.Pending,
                        },
                        Exposures = Merge(exposures),
                    });
                } catch (Exception ex) {
                    NinaLog.Instance.Debug($"Sequence target skipped: {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>Walks a container; the filter in effect is the last Switch Filter seen before an exposure.</summary>
        private static string Collect(ISequenceContainer container, string filter, List<SequenceExposure> found, int depth) {
            if (depth > MaxDepth) {
                return filter;
            }
            var planned = LoopIterations(container);
            foreach (var item in container.GetItemsSnapshot()) {
                if (item is null || item.Status == SequenceEntityStatus.DISABLED) {
                    continue;
                }
                switch (item) {
                    case SwitchFilter switchFilter:
                        filter = switchFilter.Filter?.Name ?? filter;
                        break;
                    case TakeExposure exposure:
                        found.Add(new SequenceExposure {
                            Filter = filter,
                            Seconds = Math.Round(exposure.ExposureTime, 2),
                            ImageType = exposure.ImageType,
                            Done = exposure.ExposureCount,
                            Planned = planned,
                        });
                        break;
                    case ISequenceContainer child:
                        // A Smart Exposure / Take Many Exposures keeps its filter inside; a plain block passes ours on.
                        var inner = Collect(child, filter, found, depth + 1);
                        if (child.GetType().Name is not ("SmartExposure" or "TakeManyExposures" or "SimpleExposure")) {
                            filter = inner;
                        }
                        break;
                }
            }
            return filter;
        }

        /// <summary>A "Loop for N iterations" on the block gives the planned count; other loops have none.</summary>
        private static int? LoopIterations(ISequenceContainer container) {
            if (container is not IConditionable conditionable) {
                return 1;
            }
            var conditions = conditionable.GetConditionsSnapshot();
            if (conditions.Count == 0) {
                return 1;
            }
            return conditions.OfType<LoopCondition>().FirstOrDefault() is { } loop ? loop.Iterations : null;
        }

        /// <summary>Same filter, length and type in one target count as one line (e.g. split by dithers).</summary>
        private static IReadOnlyList<SequenceExposure> Merge(List<SequenceExposure> exposures) =>
            exposures
                .GroupBy(e => (e.Filter, e.Seconds, e.ImageType))
                .Select(g => g.First() with {
                    Done = g.Sum(e => e.Done),
                    Planned = g.All(e => e.Planned is not null) ? g.Sum(e => e.Planned!.Value) : null,
                })
                .ToList();

        /// <summary>The instructions running now in a sequence NINA's mediator doesn't report on (the classic sequencer).</summary>
        public static IReadOnlyCollection<ISequenceItem> RunningItems(ISequenceRootContainer root) {
            var found = new List<ISequenceItem>();
            void Walk(ISequenceContainer container, int depth) {
                if (depth > MaxDepth) {
                    return;
                }
                foreach (var item in container.GetItemsSnapshot()) {
                    if (item?.Status != SequenceEntityStatus.RUNNING) {
                        continue;
                    }
                    if (item is ISequenceContainer child) {
                        Walk(child, depth + 1);
                    } else {
                        found.Add(item);
                    }
                }
            }
            try {
                if (root is not null) {
                    Walk(root, 0);
                }
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Running items unavailable: {ex.Message}");
            }
            return found;
        }

        /// <summary>What a running wait instruction is waiting for, in words, and roughly until when.</summary>
        public static (string Label, DateTimeOffset? Until)? Waiting(IEnumerable<ISequenceItem> runningItems, DateTimeOffset now) {
            foreach (var item in runningItems ?? []) {
                var label = item?.GetType().Name switch {
                    "WaitForTime" or "WaitUntil" => "Waiting for the start time",
                    "WaitForTimeSpan" => "Waiting",
                    "WaitForAltitude" or "WaitUntilAboveHorizon" => "Waiting for the target to rise",
                    "WaitForSunAltitude" => "Waiting for dark",
                    "WaitForMoonAltitude" => "Waiting for the Moon",
                    "WaitUntilSafe" => "Waiting until safe",
                    _ => item?.Name?.StartsWith("Wait", StringComparison.OrdinalIgnoreCase) == true ? item.Name : null,
                };
                if (label is null) {
                    continue;
                }
                DateTimeOffset? until = null;
                try {
                    var remaining = item.GetEstimatedDuration();
                    if (remaining > TimeSpan.FromSeconds(30) && remaining < TimeSpan.FromHours(20)) {
                        var end = now + remaining;
                        // Whole minutes, so the snapshot doesn't change on every refresh.
                        until = new DateTimeOffset(end.Year, end.Month, end.Day, end.Hour, end.Minute, 0, TimeSpan.Zero);
                    }
                } catch {
                    // Some wait items can't estimate; the label alone is still useful.
                }
                return (label, until);
            }
            return null;
        }
    }
}
