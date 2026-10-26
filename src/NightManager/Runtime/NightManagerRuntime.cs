namespace NightManager.Plugin.Runtime {
    /// <summary>What sequence instructions may ask of the running Night Manager.</summary>
    public interface INightManagerRuntime {
        bool IsPaired { get; }

        void SendUserNotification(string title, string message, string severity);

        /// <summary>Queues any event for the app (and a push, for the kinds the backend notifies about).</summary>
        void SendEvent(string kind, string severity, string title, string message, IReadOnlyDictionary<string, object> data = null);

        /// <summary>Questions, alarms, pause and finish between the sequence and the phone.</summary>
        NightManager.Core.Sequencing.PhoneControl Phone { get; }

        /// <summary>The latest state sent to the app (for message placeholders).</summary>
        NightManager.Core.Contracts.ObservatorySnapshot CurrentSnapshot { get; }

        string RigName { get; }

        /// <summary>The newest light frame's id, for "Send latest image".</summary>
        string LatestFrameId { get; }

        int LightFramesThisSequence { get; }

        /// <summary>
        /// Ends the session once NINA's image-save queue has drained, so frames still being saved
        /// when the sequence reaches its end area are counted in tonight's session and cull list.
        /// </summary>
        Task EndSessionAsync(string reason, IProgress<string> progress, CancellationToken token);

        /// <summary>Tells the app the night is over and a shutdown may follow (no alarm when the PC goes quiet).</summary>
        void AnnounceNightEnding(string message);
    }

    /// <summary>
    /// NINA composes the plugin manifest and its sequence items in separate MEF containers, so
    /// instructions cannot import the running service. The service publishes itself here instead.
    /// </summary>
    public static class NightManagerRuntime {
        private static volatile INightManagerRuntime current;

        public static INightManagerRuntime Current {
            get => current;
            internal set => current = value;
        }
    }
}
