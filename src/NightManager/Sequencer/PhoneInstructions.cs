using Newtonsoft.Json;
using NightManager.Core.Contracts;
using NightManager.Core.Sequencing;
using NightManager.Plugin.Runtime;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using System.ComponentModel.Composition;

namespace NightManager.Plugin.Sequencer {

    /// <summary>Shared plumbing: issues list, the "not paired yet" note, and placeholder resolution.</summary>
    public abstract class PhoneInstructionBase : SequenceItem, IValidatable {
        private IList<string> issues = new List<string>();

        protected PhoneInstructionBase() {
        }

        protected PhoneInstructionBase(PhoneInstructionBase source) : base(source) {
        }

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public static string PlaceholderHelp => "Placeholders: " + MessageTokens.Help;

        protected static INightManagerRuntime Runtime => NightManagerRuntime.Current;

        protected static string Resolve(string text) => MessageTokens.Resolve(text, Runtime?.CurrentSnapshot, Runtime?.RigName);

        public bool Validate() {
            var found = new List<string>();
            var valid = ValidateFields(found);
            if (Runtime is not { IsPaired: true }) {
                found.Add("The Night Manager is not paired with the app yet.");
            }
            Issues = found;
            return valid;
        }

        protected virtual bool ValidateFields(List<string> issues) => true;

        public override void AfterParentChanged() {
            base.AfterParentChanged();
            Validate();
        }
    }

    // MARK: Ask my phone

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Ask my phone")]
    [ExportMetadata("Description", "Asks a yes/no question on your phone and waits for the answer. \"No\" fails this instruction, so with \"Skip instruction set\" on error it skips the rest of its block. No answer in time uses the default.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "QuestionSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class AskPhoneInstruction : PhoneInstructionBase {
        private string question = "Sky looks clear. Open the roof?";
        private string yesLabel = "Yes";
        private string noLabel = "No";
        private int timeoutMinutes = 10;
        private string onTimeout = "no";

        [ImportingConstructor]
        public AskPhoneInstruction() {
            ErrorBehavior = InstructionErrorBehavior.SkipInstructionSetOnError;
        }

        private AskPhoneInstruction(AskPhoneInstruction source) : base(source) {
            Question = source.Question;
            YesLabel = source.YesLabel;
            NoLabel = source.NoLabel;
            TimeoutMinutes = source.TimeoutMinutes;
            OnTimeout = source.OnTimeout;
        }

        public static IReadOnlyList<string> Answers { get; } = ["yes", "no"];

        [JsonProperty] public string Question { get => question; set { question = value; RaisePropertyChanged(); } }
        [JsonProperty] public string YesLabel { get => yesLabel; set { yesLabel = string.IsNullOrWhiteSpace(value) ? "Yes" : value; RaisePropertyChanged(); } }
        [JsonProperty] public string NoLabel { get => noLabel; set { noLabel = string.IsNullOrWhiteSpace(value) ? "No" : value; RaisePropertyChanged(); } }
        /// <summary>0 = wait for ever.</summary>
        [JsonProperty] public int TimeoutMinutes { get => timeoutMinutes; set { timeoutMinutes = Math.Clamp(value, 0, 24 * 60); RaisePropertyChanged(); } }
        [JsonProperty] public string OnTimeout { get => onTimeout; set { onTimeout = Answers.Contains(value) ? value : "no"; RaisePropertyChanged(); } }

        protected override bool ValidateFields(List<string> issues) {
            if (string.IsNullOrWhiteSpace(Question)) {
                issues.Add("Enter the question to ask.");
                return false;
            }
            return true;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var runtime = Runtime;
            var text = Resolve(Question);
            string answer;
            if (runtime is not { IsPaired: true }) {
                answer = OnTimeout;
            } else {
                var prompt = runtime.Phone.Ask("ask", text, null, YesLabel, NoLabel, TimeoutMinutes > 0 ? TimeSpan.FromMinutes(TimeoutMinutes) : null, OnTimeout);
                runtime.SendEvent(EventKinds.UserQuestion, Severity.Warning, text,
                    TimeoutMinutes > 0 ? $"{YesLabel} or {NoLabel}? If there's no answer in {TimeoutMinutes} min: {(OnTimeout == "yes" ? YesLabel : NoLabel)}." : $"{YesLabel} or {NoLabel}? The sequence waits for your answer.",
                    new Dictionary<string, object> { ["promptId"] = prompt.Id, ["kind"] = "ask", ["yes"] = YesLabel, ["no"] = NoLabel, ["timeoutAt"] = prompt.TimeoutAt });
                progress?.Report(new ApplicationStatus { Status = $"Waiting for an answer on the phone: {text}" });
                var (given, timedOut) = await runtime.Phone.WaitForAnswerAsync(prompt.Id, token);
                answer = given;
                if (timedOut) {
                    runtime.SendEvent(EventKinds.UserAnswered, Severity.Info, $"No answer: went with \"{(answer == "yes" ? YesLabel : NoLabel)}\"", text);
                }
            }
            if (answer != "yes") {
                throw new SequenceEntityFailedException($"Answered \"{NoLabel}\" on the phone.");
            }
        }

        public override object Clone() => new AskPhoneInstruction(this);
        public override string ToString() => $"Category: {Category}, Item: {nameof(AskPhoneInstruction)}, Question: {Question}";
    }

    // MARK: Wait for go from phone

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Wait for go from phone")]
    [ExportMetadata("Description", "Holds the sequence until you tap Go in The Night Manager app (or the time runs out).")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "HourglassSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class WaitForGoInstruction : PhoneInstructionBase {
        private string message = "Ready to start imaging {target}";
        private int timeoutMinutes = 0;
        private bool continueOnTimeout = true;

        [ImportingConstructor]
        public WaitForGoInstruction() {
        }

        private WaitForGoInstruction(WaitForGoInstruction source) : base(source) {
            Message = source.Message;
            TimeoutMinutes = source.TimeoutMinutes;
            ContinueOnTimeout = source.ContinueOnTimeout;
        }

        [JsonProperty] public string Message { get => message; set { message = value; RaisePropertyChanged(); } }
        /// <summary>0 = wait for ever.</summary>
        [JsonProperty] public int TimeoutMinutes { get => timeoutMinutes; set { timeoutMinutes = Math.Clamp(value, 0, 24 * 60); RaisePropertyChanged(); } }
        /// <summary>On timeout: carry on (true) or fail this instruction (false, e.g. to skip the block).</summary>
        [JsonProperty] public bool ContinueOnTimeout { get => continueOnTimeout; set { continueOnTimeout = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var runtime = Runtime;
            if (runtime is not { IsPaired: true }) {
                return;
            }
            var text = Resolve(Message);
            var prompt = runtime.Phone.Ask("go", text, null, "Go", null, TimeoutMinutes > 0 ? TimeSpan.FromMinutes(TimeoutMinutes) : null, ContinueOnTimeout ? "go" : "no");
            runtime.SendEvent(EventKinds.UserQuestion, Severity.Warning, "Waiting for your go", text,
                new Dictionary<string, object> { ["promptId"] = prompt.Id, ["kind"] = "go", ["yes"] = "Go", ["timeoutAt"] = prompt.TimeoutAt });
            progress?.Report(new ApplicationStatus { Status = "Waiting for Go from the phone" });
            var (answer, timedOut) = await runtime.Phone.WaitForAnswerAsync(prompt.Id, token);
            if (timedOut) {
                runtime.SendEvent(EventKinds.UserAnswered, Severity.Info, ContinueOnTimeout ? "No go in time: carrying on" : "No go in time: skipping", text);
            }
            if (answer is not ("go" or "yes")) {
                throw new SequenceEntityFailedException("No go from the phone.");
            }
        }

        public override object Clone() => new WaitForGoInstruction(this);
        public override string ToString() => $"Category: {Category}, Item: {nameof(WaitForGoInstruction)}";
    }

    // MARK: Wake me up

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Wake me up")]
    [ExportMetadata("Description", "Critical alert to your phone, repeated until you tap \"I'm up\" in the app. Optionally holds the sequence until then.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "ExclamationCircledSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class WakeMeUpInstruction : PhoneInstructionBase {
        private string title = "Observatory needs you";
        private string message = string.Empty;
        private int repeatMinutes = 2;
        private int maxRepeats = 10;
        private bool waitForAcknowledgement;

        [ImportingConstructor]
        public WakeMeUpInstruction() {
        }

        private WakeMeUpInstruction(WakeMeUpInstruction source) : base(source) {
            Title = source.Title;
            Message = source.Message;
            RepeatMinutes = source.RepeatMinutes;
            MaxRepeats = source.MaxRepeats;
            WaitForAcknowledgement = source.WaitForAcknowledgement;
        }

        [JsonProperty] public string Title { get => title; set { title = value; RaisePropertyChanged(); } }
        [JsonProperty] public string Message { get => message; set { message = value; RaisePropertyChanged(); } }
        [JsonProperty] public int RepeatMinutes { get => repeatMinutes; set { repeatMinutes = Math.Clamp(value, 1, 60); RaisePropertyChanged(); } }
        [JsonProperty] public int MaxRepeats { get => maxRepeats; set { maxRepeats = Math.Clamp(value, 1, 60); RaisePropertyChanged(); } }
        [JsonProperty] public bool WaitForAcknowledgement { get => waitForAcknowledgement; set { waitForAcknowledgement = value; RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var runtime = Runtime;
            if (runtime is not { IsPaired: true }) {
                return;
            }
            var title = Resolve(string.IsNullOrWhiteSpace(Title) ? "Observatory needs you" : Title);
            var body = Resolve(Message);
            var alarm = runtime.Phone.RaiseAlarm(title, body);
            var ring = Ring(runtime, alarm.Id, title, body, WaitForAcknowledgement ? token : CancellationToken.None);
            if (WaitForAcknowledgement) {
                progress?.Report(new ApplicationStatus { Status = "Waiting for \"I'm up\" on the phone" });
                await ring;
            }
        }

        private async Task Ring(INightManagerRuntime runtime, string alarmId, string title, string body, CancellationToken token) {
            for (var i = 0; i < MaxRepeats && runtime.Phone.IsActive(alarmId); i++) {
                runtime.SendEvent(EventKinds.UserAlarm, Severity.Critical, title, string.IsNullOrWhiteSpace(body) ? "Tap \"I'm up\" to stop this alarm." : body,
                    new Dictionary<string, object> { ["alarmId"] = alarmId, ["repeat"] = i + 1 });
                await Task.WhenAny(runtime.Phone.Acknowledged(alarmId), Task.Delay(TimeSpan.FromMinutes(RepeatMinutes), token));
                token.ThrowIfCancellationRequested();
            }
            // Gave up ringing: stop showing it as active.
            runtime.Phone.Acknowledge(alarmId);
        }

        public override object Clone() => new WakeMeUpInstruction(this);
        public override string ToString() => $"Category: {Category}, Item: {nameof(WakeMeUpInstruction)}, Title: {Title}";
    }

    // MARK: Send latest image

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Send latest image")]
    [ExportMetadata("Description", "Sends a notification with the newest light frame attached.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "PictureSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class SendLatestImageInstruction : PhoneInstructionBase {
        private string title = "{target} · latest sub";
        private string message = "{filter} · HFR {hfr} · {accepted}/{subs} subs";

        [ImportingConstructor]
        public SendLatestImageInstruction() {
        }

        private SendLatestImageInstruction(SendLatestImageInstruction source) : base(source) {
            Title = source.Title;
            Message = source.Message;
        }

        [JsonProperty] public string Title { get => title; set { title = value; RaisePropertyChanged(); } }
        [JsonProperty] public string Message { get => message; set { message = value; RaisePropertyChanged(); } }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var runtime = Runtime;
            if (runtime is null) {
                return Task.CompletedTask;
            }
            var frameId = runtime.LatestFrameId;
            runtime.SendEvent(EventKinds.UserImage, Severity.Info, Resolve(Title),
                frameId is null ? "No light frame yet tonight." : Resolve(Message),
                frameId is null ? null : new Dictionary<string, object> { ["frameId"] = frameId });
            return Task.CompletedTask;
        }

        public override object Clone() => new SendLatestImageInstruction(this);
        public override string ToString() => $"Category: {Category}, Item: {nameof(SendLatestImageInstruction)}";
    }

    // MARK: Note to timeline

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Note to timeline")]
    [ExportMetadata("Description", "Adds your note to tonight's timeline in the app and to the morning report. No notification.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "PenSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class NoteToTimelineInstruction : PhoneInstructionBase {
        private string text = "Starting {target}";

        [ImportingConstructor]
        public NoteToTimelineInstruction() {
        }

        private NoteToTimelineInstruction(NoteToTimelineInstruction source) : base(source) {
            Text = source.Text;
        }

        [JsonProperty] public string Text { get => text; set { text = value; RaisePropertyChanged(); } }

        protected override bool ValidateFields(List<string> issues) {
            if (string.IsNullOrWhiteSpace(Text)) {
                issues.Add("Enter the note.");
                return false;
            }
            return true;
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Runtime?.SendEvent(EventKinds.UserNote, Severity.Info, Resolve(Text), null);
            return Task.CompletedTask;
        }

        public override object Clone() => new NoteToTimelineInstruction(this);
        public override string ToString() => $"Category: {Category}, Item: {nameof(NoteToTimelineInstruction)}";
    }

    // MARK: Start Lock Screen tracking

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Start Lock Screen tracking")]
    [ExportMetadata("Description", "Starts the Live Activity (Lock Screen and Dynamic Island) on the phones following this setup.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "SatelliteDishSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class StartLockScreenInstruction : PhoneInstructionBase {
        [ImportingConstructor]
        public StartLockScreenInstruction() {
        }

        private StartLockScreenInstruction(StartLockScreenInstruction source) : base(source) {
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            Runtime?.SendEvent(EventKinds.UserTrackStart, Severity.Info, "Lock Screen tracking started", null);
            return Task.CompletedTask;
        }

        public override object Clone() => new StartLockScreenInstruction(this);
        public override string ToString() => $"Category: {Category}, Item: {nameof(StartLockScreenInstruction)}";
    }

    // MARK: Pause point

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Pause point")]
    [ExportMetadata("Description", "If you tapped Pause in the app, the sequence holds here until you tap Resume. Otherwise it passes straight through.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "PauseSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class PausePointInstruction : PhoneInstructionBase {
        private int maxWaitMinutes;

        [ImportingConstructor]
        public PausePointInstruction() {
        }

        private PausePointInstruction(PausePointInstruction source) : base(source) {
            MaxWaitMinutes = source.MaxWaitMinutes;
        }

        /// <summary>0 = hold until resumed.</summary>
        [JsonProperty] public int MaxWaitMinutes { get => maxWaitMinutes; set { maxWaitMinutes = Math.Clamp(value, 0, 24 * 60); RaisePropertyChanged(); } }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var runtime = Runtime;
            if (runtime is null || !runtime.Phone.Paused) {
                return;
            }
            runtime.SendEvent(EventKinds.SequencePaused, Severity.Info, "Paused at a Pause point", "Tap Resume in the app to carry on.");
            progress?.Report(new ApplicationStatus { Status = "Paused from the phone" });
            await runtime.Phone.HoldIfPausedAsync(MaxWaitMinutes > 0 ? TimeSpan.FromMinutes(MaxWaitMinutes) : null, token);
            runtime.SendEvent(EventKinds.SequenceResumed, Severity.Info, "Sequence resumed", null);
        }

        public override object Clone() => new PausePointInstruction(this);
        public override string ToString() => $"Category: {Category}, Item: {nameof(PausePointInstruction)}";
    }
}
