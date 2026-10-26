using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Sequencing;
using NightManager.Core.Sync;
using NightManager.Plugin.Sequencer;
using NINA.Core.Enum;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using System.Runtime.CompilerServices;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Failure reporting and phone control for the Advanced Sequencer. Every failure in NINA (instructions,
    /// triggers, conditions) is raised on the sequence root's FailureEvent, once per failed attempt; the plugin
    /// hooks the root of whatever sequence runs, so users get failure alerts without adding anything.
    /// </summary>
    public sealed partial class NightManagerService {
        private readonly FailureWatch failures = new(SyncEngine.NewId);
        private readonly object failureGate = new();
        private readonly Dictionary<long, WeakReference<ISequenceItem>> retrying = new();
        private ISequenceRootContainer failureRoot;
        private ObservatorySnapshot lastSnapshot;
        private int lightFramesThisSequence;

        /// <summary>Questions, alarms, pause and finish for the sequence's Night Manager instructions.</summary>
        public PhoneControl Phone { get; } = new();

        public string LatestFrameId { get; private set; }

        public ObservatorySnapshot CurrentSnapshot => lastSnapshot;

        public int LightFramesThisSequence => Volatile.Read(ref lightFramesThisSequence);

        public void SendEvent(string kind, string severity, string title, string message, IReadOnlyDictionary<string, object> data = null) {
            Enqueue(Event(kind, severity, title, message, data is null ? null : new Dictionary<string, object>(data)));
            sync?.Wake();
        }

        // MARK: Sequence lifecycle

        private void SequenceStartedForControl() {
            failures.Reset();
            failures.SessionId = sessions?.CurrentSessionId;
            Phone.ResetForNewSequence();
            lock (analysisGate) {
                watch.NightEnded = false;
            }
            Interlocked.Exchange(ref lightFramesThisSequence, 0);
            ResetSavedBytes();
            lock (failureGate) {
                retrying.Clear();
            }
        }

        /// <summary>Completed, stopped, or aborted after a failure: the last one is critical (and pushed).</summary>
        private EventItem SequenceEndEvent() {
            var title = sequenceState.Title;
            var abort = failures.LastAbort;
            if (abort is { } a && DateTimeOffset.UtcNow - a.At < TimeSpan.FromMinutes(15)) {
                return Event(EventKinds.SequenceFinished, Severity.Critical, $"Sequence aborted: {a.Name} failed",
                    a.Error is { Length: > 0 } error ? $"{error} The end-of-sequence steps may not have run; check the observatory." : "The end-of-sequence steps may not have run; check the observatory.",
                    new Dictionary<string, object> { ["reason"] = "aborted", ["item"] = a.Name, ["title"] = title });
            }

            var completed = false;
            try {
                completed = failureRoot?.Items is { Count: > 0 } items && items.All(i => i.Status is SequenceEntityStatus.FINISHED or SequenceEntityStatus.SKIPPED or SequenceEntityStatus.DISABLED);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Sequence end state unavailable: {ex.Message}");
            }
            return completed
                ? Event(EventKinds.SequenceFinished, Severity.Info, "Sequence completed", title, new Dictionary<string, object> { ["reason"] = "completed" })
                : Event(EventKinds.SequenceFinished, Severity.Info, "Sequence stopped", title, new Dictionary<string, object> { ["reason"] = "stopped" });
        }

        // MARK: Failure hook

        private void AttachFailureRoot(IEnumerable<ISequenceItem> runningItems) {
            ISequenceRootContainer root = null;
            try {
                root = runningItems?.Select(i => i?.Parent).Where(p => p is not null).Select(ItemUtility.GetRootContainer).FirstOrDefault(r => r is not null);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Sequence root unavailable: {ex.Message}");
            }
            if (root is null) {
                return;
            }
            lock (failureGate) {
                if (ReferenceEquals(root, failureRoot)) {
                    return;
                }
                if (failureRoot is not null) {
                    failureRoot.FailureEvent -= OnSequenceFailure;
                }
                failureRoot = root;
                failureRoot.FailureEvent += OnSequenceFailure;
            }
            NinaLog.Instance.Info("Night Manager is watching the sequence for failures.");
        }

        private void DetachFailureRoot() {
            lock (failureGate) {
                if (failureRoot is not null) {
                    failureRoot.FailureEvent -= OnSequenceFailure;
                    failureRoot = null;
                }
                retrying.Clear();
            }
        }

        private Task OnSequenceFailure(object sender, SequenceEntityFailureEventArgs args) {
            try {
                var entity = args?.Entity;
                // Containers report their own failure on top of the child's; the child is the useful one.
                // "No" to Ask my phone / no Go is the user's choice steering the sequence, not a fault.
                if (entity is null || entity is ISequenceContainer || entity is FailuresToPhoneTrigger
                    || entity is AskPhoneInstruction || entity is WaitForGoInstruction) {
                    return Task.CompletedTask;
                }

                var report = ToReport(entity, args.Exception);
                var events = failures.OnFailure(report, DateTimeOffset.UtcNow);
                if (entity is ISequenceItem item && failures.Retrying.Contains(report.Key)) {
                    lock (failureGate) {
                        retrying[report.Key] = new WeakReference<ISequenceItem>(item);
                    }
                }
                EnqueueAll(events);
                if (events.Any(e => e.Data?["notify"] is true)) {
                    sync?.Wake();
                }
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Failure report could not be built: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        private FailureReport ToReport(ISequenceEntity entity, Exception exception) {
            var kind = entity switch {
                ISequenceTrigger => "trigger",
                ISequenceCondition => "condition",
                _ => "instruction",
            };
            var issues = entity is IValidatable { Issues.Count: > 0 } v ? string.Join(" ", v.Issues.Where(i => !string.IsNullOrWhiteSpace(i))) : null;
            var error = string.IsNullOrWhiteSpace(issues) ? exception?.Message : issues;
            var policy = PolicyFor(entity);
            // The flip has its own alert (Meridian flip FAILED); don't send a second one.
            if (kind == "trigger" && entity.Name?.Contains("Meridian", StringComparison.OrdinalIgnoreCase) == true) {
                policy = (policy ?? new FailurePolicy()) with { Notify = false };
            }
            return new FailureReport {
                Key = RuntimeHelpers.GetHashCode(entity),
                Name = string.IsNullOrWhiteSpace(entity.Name) ? entity.GetType().Name : entity.Name,
                Kind = kind,
                Parent = BlockName(entity),
                Attempts = entity is ISequenceItem item ? Math.Max(1, item.Attempts) : 1,
                ErrorBehavior = entity is ISequenceItem withBehavior ? withBehavior.ErrorBehavior.ToString() : null,
                Error = error,
                Policy = policy,
            };
        }

        /// <summary>The target's name when inside a target block, otherwise the containing block's name.</summary>
        private static string BlockName(ISequenceEntity entity) {
            for (var parent = entity.Parent; parent is not null; parent = parent.Parent) {
                if (parent is IDeepSkyObjectContainer dso) {
                    var name = dso.Target?.TargetName;
                    if (!string.IsNullOrWhiteSpace(name)) {
                        return name;
                    }
                }
                if (parent is ISequenceRootContainer) {
                    break;
                }
            }
            return entity.Parent is ISequenceRootContainer ? null : entity.Parent?.Name;
        }

        /// <summary>The nearest "Failures to my phone" trigger in an enclosing block decides for this failure.</summary>
        private static FailurePolicy PolicyFor(ISequenceEntity entity) {
            for (var parent = entity.Parent; parent is not null; parent = parent.Parent) {
                if (parent is ITriggerable triggerable) {
                    foreach (var trigger in triggerable.GetTriggersSnapshot()) {
                        if (trigger is FailuresToPhoneTrigger phoneTrigger && trigger.Status != SequenceEntityStatus.DISABLED) {
                            return phoneTrigger.ToPolicy();
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>Retried items that later finished are a recovery; ones that stopped otherwise are forgotten.</summary>
        private void TickFailures(DateTimeOffset now) {
            EnqueueAll(failures.Tick(now));
            List<(long Key, ISequenceItem Item)> check;
            lock (failureGate) {
                check = retrying.Select(r => (r.Key, r.Value.TryGetTarget(out var item) ? item : null)).ToList();
            }
            foreach (var (key, item) in check) {
                if (!failures.Retrying.Contains(key)) {
                    lock (failureGate) { retrying.Remove(key); }
                    continue;
                }
                var status = item?.Status;
                if (status == SequenceEntityStatus.RUNNING) {
                    continue;
                }
                if (status == SequenceEntityStatus.FINISHED) {
                    EnqueueAll(failures.OnRecovered(key, item.Name, now));
                } else {
                    failures.Forget(key);
                }
                lock (failureGate) { retrying.Remove(key); }
            }
        }

        // MARK: Classic sequencer

        /// <summary>Set by NINA's start event when it isn't the Advanced Sequencer starting (cleared on finish).</summary>
        private volatile bool classicStarted;

        /// <summary>The classic sequencer's root, found through its targets.</summary>
        private ISequenceRootContainer ClassicRoot() {
            try {
                var first = sequence.GetAllTargetsInSimpleSequence()?.FirstOrDefault(t => t?.Parent is not null);
                return first is null ? null : ItemUtility.GetRootContainer(first.Parent);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Classic sequence unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Running per its root; between the start event and the engine starting (validation can show a dialog)
        /// the start event's flag counts. A finished root clears a flag whose finish event was missed.
        /// </summary>
        private bool IsClassicRunning(ISequenceRootContainer root) {
            var status = root?.Status;
            if (status == SequenceEntityStatus.RUNNING) {
                return true;
            }
            if (classicStarted && status is SequenceEntityStatus.FINISHED or SequenceEntityStatus.FAILED) {
                classicStarted = false;
            }
            return classicStarted;
        }

        // MARK: Phone control

        private void OnPhoneChanged(object sender, EventArgs e) {
            PushSnapshot();
            sync?.Wake();
        }

        private void OnPhoneRequested(object sender, (string Kind, string Title, string Message) request) =>
            Enqueue(Event(request.Kind, Severity.Info, request.Title, request.Message));
    }
}
