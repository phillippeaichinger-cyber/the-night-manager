using NightManager.Core.Logging;
using NINA.Core.Utility;

namespace NightManager.Plugin.Runtime {
    /// <summary>Forwards Core logging to NINA's log so support only needs one log file.</summary>
    internal sealed class NinaLog : INmLog {
        public static readonly NinaLog Instance = new();

        public void Debug(string message) => Logger.Debug("[NightManager] " + message);

        public void Info(string message) => Logger.Info("[NightManager] " + message);

        public void Warning(string message) => Logger.Warning("[NightManager] " + message);

        public void Error(string message, Exception exception = null) {
            if (exception is null) {
                Logger.Error("[NightManager] " + message);
            } else {
                Logger.Error($"[NightManager] {message}: {exception}");
            }
        }
    }
}
