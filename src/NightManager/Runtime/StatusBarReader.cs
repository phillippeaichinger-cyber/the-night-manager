using NightManager.Core.Analysis;
using NINA.Core.Model;
using NINA.WPF.Base.Interfaces.Mediator;
using System.Collections;
using System.Reflection;

namespace NightManager.Plugin.Runtime {
    /// <summary>
    /// Reads NINA's status bar. NINA lets only its own status panel listen to status updates, so the panel's list
    /// is read instead: the mediator's handler (ApplicationStatusVM) holds it as "ApplicationStatus".
    /// </summary>
    internal sealed class StatusBarReader {
        private readonly IApplicationStatusMediator mediator;
        private IEnumerable list;
        private RawStatus[] last = [];

        public StatusBarReader(IApplicationStatusMediator mediator) {
            this.mediator = mediator;
        }

        /// <summary>False until NINA's status panel exists (or if a NINA update renamed things).</summary>
        public bool Attached => list is not null;

        public IReadOnlyList<RawStatus> Read() {
            if (list is null && !TryAttach()) {
                return [];
            }

            // The panel's list changes on NINA's UI thread; a copy taken mid-change is retried, then the last good one kept.
            for (var attempt = 0; attempt < 3; attempt++) {
                try {
                    last = list.OfType<ApplicationStatus>().Select(s => new RawStatus {
                        Source = s.Source ?? "",
                        Status = s.Status,
                        Progress = s.Progress,
                        MaxProgress = s.MaxProgress,
                        IsPercent = s.ProgressType == ApplicationStatus.StatusProgressType.Percent,
                        Status2 = s.Status2,
                        Status3 = s.Status3,
                    }).ToArray();
                    return last;
                } catch (InvalidOperationException) {
                } catch (ArgumentException) {
                }
            }
            return last;
        }

        private bool TryAttach() {
            try {
                var handler = Field(mediator?.GetType(), "handler")?.GetValue(mediator);
                list = handler?.GetType().GetProperty("ApplicationStatus", BindingFlags.Public | BindingFlags.Instance)?.GetValue(handler) as IEnumerable;
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"NINA status bar not readable: {ex.Message}");
            }
            return list is not null;
        }

        private static FieldInfo Field(Type type, string name) {
            for (; type is not null; type = type.BaseType) {
                if (type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) is { } field) {
                    return field;
                }
            }
            return null;
        }
    }
}
