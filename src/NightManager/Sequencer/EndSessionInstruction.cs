using Newtonsoft.Json;
using NightManager.Plugin.Runtime;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using System.ComponentModel.Composition;

namespace NightManager.Plugin.Sequencer {
    /// <summary>
    /// Closes tonight's session now: writes the cull list and lets the app build the morning report instead of
    /// waiting for the idle timeout. Put it in the End area wherever suits (e.g. while the camera warms).
    /// Keeps its original type name so sequences saved with "End night &amp; send report" still load.
    /// </summary>
    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Send night report")]
    [ExportMetadata("Description", "Closes tonight's session (waits for images still saving), writes the cull list and sends the morning report. Pair with \"Night ending\" to announce the shutdown.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "FlagFinishSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class EndSessionInstruction : SequenceItem {
        [ImportingConstructor]
        public EndSessionInstruction() {
        }

        private EndSessionInstruction(EndSessionInstruction source) : base(source) {
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();

            if (NightManagerRuntime.Current is { } runtime) {
                var status = new Progress<string>(text => progress?.Report(new ApplicationStatus { Status = text }));
                await runtime.EndSessionAsync("manual", status, token);
            }
        }

        public override object Clone() => new EndSessionInstruction(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(EndSessionInstruction)}";
    }

    /// <summary>
    /// Announces the end of the night straight away: the app gets "night ending", and when the PC then shuts
    /// down it reports "shut down safely" instead of an alarm. Doesn't close the session or send the report.
    /// </summary>
    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Night ending")]
    [ExportMetadata("Description", "Tells your phone the night is ending and a shutdown may follow, so the PC going quiet isn't an alarm. Put it at the start of the End area.")]
    [ExportMetadata("Category", "The Night Manager")]
    [ExportMetadata("Icon", "PowerSVG")]
    [JsonObject(MemberSerialization.OptIn)]
    public class NightEndingInstruction : SequenceItem {
        private string message = string.Empty;

        [ImportingConstructor]
        public NightEndingInstruction() {
        }

        private NightEndingInstruction(NightEndingInstruction source) : base(source) {
            Message = source.Message;
        }

        /// <summary>Optional text for the notification; empty: "Wrapping up: parking, warming and shutting down."</summary>
        [JsonProperty]
        public string Message {
            get => message;
            set {
                message = value;
                RaisePropertyChanged();
            }
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            NightManagerRuntime.Current?.AnnounceNightEnding(Message);
            return Task.CompletedTask;
        }

        public override object Clone() => new NightEndingInstruction(this);

        public override string ToString() => $"Category: {Category}, Item: {nameof(NightEndingInstruction)}";
    }
}
