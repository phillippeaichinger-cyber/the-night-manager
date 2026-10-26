using Newtonsoft.Json;
using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Sequencing;
using NightManager.Plugin.Runtime;
using NINA.Core.Model;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using System.ComponentModel.Composition;

namespace NightManager.Plugin.Sequencer {

    /// <summary>
    /// Failures are reported automatically for the whole sequence. This trigger only changes how for its block:
    /// silence it, ignore some instructions, raise the level, or hear about every failed attempt.
    /// </summary>
    [Export(typeof(ISequenceTrigger))]
    [ExportMetadata("Name", "Failures to my phone")]
    [ExportMetadata("Description", "Failures are already sent to your phone. Put this in a block to change how: ignore instructions, set the level, or notify on every attempt.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "ExclamationCircledSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class FailuresToPhoneTrigger : SequenceTrigger {
        private bool notify = true;
        private string level = "auto";
        private string ignore = string.Empty;
        private bool everyAttempt;

        [ImportingConstructor]
        public FailuresToPhoneTrigger() {
        }

        private FailuresToPhoneTrigger(FailuresToPhoneTrigger source) : this() {
            CopyMetaData(source);
            Notify = source.Notify;
            Level = source.Level;
            Ignore = source.Ignore;
            EveryAttempt = source.EveryAttempt;
        }

        public static IReadOnlyList<string> Levels { get; } = ["auto", Severity.Info, Severity.Warning, Severity.Critical];

        [JsonProperty] public bool Notify { get => notify; set { notify = value; RaisePropertyChanged(); } }
        [JsonProperty] public string Level { get => level; set { level = Levels.Contains(value) ? value : "auto"; RaisePropertyChanged(); } }
        /// <summary>Comma-separated instruction names (or parts) that never notify.</summary>
        [JsonProperty] public string Ignore { get => ignore; set { ignore = value ?? string.Empty; RaisePropertyChanged(); } }
        [JsonProperty] public bool EveryAttempt { get => everyAttempt; set { everyAttempt = value; RaisePropertyChanged(); } }

        public FailurePolicy ToPolicy() => new() {
            Notify = Notify,
            Level = Level == "auto" ? null : Level,
            Ignore = Ignore.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            EveryAttempt = EveryAttempt,
        };

        // Never runs anything itself: the failure watch reads its settings.
        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => false;

        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) => Task.CompletedTask;

        public override object Clone() => new FailuresToPhoneTrigger(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(FailuresToPhoneTrigger)}, Notify: {Notify}, Level: {Level}";
    }

    /// <summary>A quiet progress push every N light frames and/or every N minutes.</summary>
    [Export(typeof(ISequenceTrigger))]
    [ExportMetadata("Name", "Progress to my phone")]
    [ExportMetadata("Description", "Sends a quiet progress notification every N subs and/or every N minutes.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "HFRHistorySVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class ProgressToPhoneTrigger : SequenceTrigger {
        private int everySubs = 10;
        private int everyMinutes;
        private string message = "{target}: {accepted}/{subs} subs, {usable} usable, HFR {hfr}";
        private int lastFrames;
        private DateTimeOffset lastSent = DateTimeOffset.UtcNow;

        [ImportingConstructor]
        public ProgressToPhoneTrigger() {
        }

        private ProgressToPhoneTrigger(ProgressToPhoneTrigger source) : this() {
            CopyMetaData(source);
            EverySubs = source.EverySubs;
            EveryMinutes = source.EveryMinutes;
            Message = source.Message;
        }

        [JsonProperty] public int EverySubs { get => everySubs; set { everySubs = Math.Clamp(value, 0, 1000); RaisePropertyChanged(); } }
        [JsonProperty] public int EveryMinutes { get => everyMinutes; set { everyMinutes = Math.Clamp(value, 0, 24 * 60); RaisePropertyChanged(); } }
        [JsonProperty] public string Message { get => message; set { message = value; RaisePropertyChanged(); } }

        public static string PlaceholderHelp => "Placeholders: " + MessageTokens.Help;

        public override void SequenceBlockInitialize() {
            lastFrames = NightManagerRuntime.Current?.LightFramesThisSequence ?? 0;
            lastSent = DateTimeOffset.UtcNow;
            base.SequenceBlockInitialize();
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            var frames = NightManagerRuntime.Current?.LightFramesThisSequence ?? 0;
            var bySubs = EverySubs > 0 && frames - lastFrames >= EverySubs;
            var byTime = EveryMinutes > 0 && DateTimeOffset.UtcNow - lastSent >= TimeSpan.FromMinutes(EveryMinutes);
            return bySubs || byTime;
        }

        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var runtime = NightManagerRuntime.Current;
            lastFrames = runtime?.LightFramesThisSequence ?? 0;
            lastSent = DateTimeOffset.UtcNow;
            runtime?.SendEvent(EventKinds.UserProgress, Severity.Info, "Progress",
                MessageTokens.Resolve(Message, runtime.CurrentSnapshot, runtime.RigName));
            return Task.CompletedTask;
        }

        public override object Clone() => new ProgressToPhoneTrigger(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(ProgressToPhoneTrigger)}, EverySubs: {EverySubs}, EveryMinutes: {EveryMinutes}";
    }

    /// <summary>
    /// Keeps a loop going until you tap "Finish night" in the app. Unlike aborting, the rest of the sequence
    /// (park, warm, close) still runs. The current instruction finishes first.
    /// </summary>
    [Export(typeof(ISequenceCondition))]
    [ExportMetadata("Name", "Loop until finished from phone")]
    [ExportMetadata("Description", "Loops until you tap \"Finish night\" in the app. The end-of-sequence steps (park, warm, close) still run, unlike Abort.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "LoopSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class LoopUntilFinishedFromPhoneCondition : SequenceCondition {
        [ImportingConstructor]
        public LoopUntilFinishedFromPhoneCondition() {
        }

        private LoopUntilFinishedFromPhoneCondition(LoopUntilFinishedFromPhoneCondition source) : this() {
            CopyMetaData(source);
        }

        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) =>
            NightManagerRuntime.Current?.Phone.FinishRequested != true;

        public override object Clone() => new LoopUntilFinishedFromPhoneCondition(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(LoopUntilFinishedFromPhoneCondition)}";
    }
}
