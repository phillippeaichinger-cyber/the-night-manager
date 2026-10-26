using Newtonsoft.Json;
using NightManager.Core.Contracts;
using NightManager.Plugin.Runtime;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using System.ComponentModel.Composition;

namespace NightManager.Plugin.Sequencer {
    /// <summary>Sequence step that sends a push notification to the owner's phone.</summary>
    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Notify my phone")]
    [ExportMetadata("Description", "Sends a notification to The Night Manager app, e.g. \"Flats done, closing up\".")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "MessageBoxSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class NotifyPhoneInstruction : SequenceItem, IValidatable {
        private string title = "Observatory update";
        private string message = string.Empty;
        private string severity = Core.Contracts.Severity.Info;
        private bool attachImage;
        private IList<string> issues = new List<string>();

        [ImportingConstructor]
        public NotifyPhoneInstruction() {
        }

        private NotifyPhoneInstruction(NotifyPhoneInstruction source) : base(source) {
            Title = source.Title;
            Message = source.Message;
            Severity = source.Severity;
            AttachImage = source.AttachImage;
        }

        /// <summary>Attach the newest light frame to the notification.</summary>
        [JsonProperty]
        public bool AttachImage {
            get => attachImage;
            set {
                attachImage = value;
                RaisePropertyChanged();
            }
        }

        public static string PlaceholderHelp => "Placeholders: " + Core.Sequencing.MessageTokens.Help;

        public static IReadOnlyList<string> Severities { get; } = [Core.Contracts.Severity.Info, Core.Contracts.Severity.Warning, Core.Contracts.Severity.Critical];

        [JsonProperty]
        public string Title {
            get => title;
            set {
                title = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public string Message {
            get => message;
            set {
                message = value;
                RaisePropertyChanged();
            }
        }

        /// <summary>info | warning | critical. Critical notifications can break through Do Not Disturb on the phone.</summary>
        [JsonProperty]
        public string Severity {
            get => severity;
            set {
                severity = Severities.Contains(value) ? value : Core.Contracts.Severity.Info;
                RaisePropertyChanged();
            }
        }

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public bool Validate() {
            var found = new List<string>();
            var valid = true;

            if (string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Message)) {
                found.Add("Enter a title or message.");
                valid = false;
            }

            if (NightManagerRuntime.Current is not { IsPaired: true }) {
                // Shown in NINA's pre-start checklist, but never a reason to fail: the message is
                // queued and delivered once the observatory is paired.
                found.Add("The Night Manager is not paired with the app yet; this notification will be delivered after pairing.");
            }

            Issues = found;
            return valid;
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            // Queued durably, so it is delivered even if the internet is down right now. Never fails the sequence.
            var runtime = NightManagerRuntime.Current;
            if (runtime is null) {
                return Task.CompletedTask;
            }
            var title = Core.Sequencing.MessageTokens.Resolve(Title, runtime.CurrentSnapshot, runtime.RigName);
            var message = Core.Sequencing.MessageTokens.Resolve(Message, runtime.CurrentSnapshot, runtime.RigName);
            if (AttachImage && runtime.LatestFrameId is { } frameId) {
                runtime.SendEvent(EventKinds.UserImage, Severity, string.IsNullOrWhiteSpace(title) ? "Message from your sequence" : title, message,
                    new Dictionary<string, object> { ["frameId"] = frameId });
            } else {
                runtime.SendUserNotification(title, message, Severity);
            }
            return Task.CompletedTask;
        }

        public override void AfterParentChanged() {
            base.AfterParentChanged();
            Validate();
        }

        public override object Clone() => new NotifyPhoneInstruction(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(NotifyPhoneInstruction)}, Title: {Title}, Severity: {Severity}";
    }
}
