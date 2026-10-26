using NightManager.Core.Settings;
using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Live;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// The live lane: while someone has the app open on this observatory, exact telemetry (guide steps, temperatures,
    /// mount, NINA's status) goes out every second; with nobody watching nothing is streamed. Commands arrive by
    /// nudge within a second, so check-ins only keep the record fresh (every 1–2 minutes).
    /// </summary>
    public sealed partial class NightManagerService {
        private static readonly TimeSpan LiveTick = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan LiveKeepAlive = TimeSpan.FromSeconds(10);
        /// <summary>Even with the lane up, look for commands this often (a nudge could be missed).</summary>
        private static readonly TimeSpan CommandSafetyNet = TimeSpan.FromMinutes(2);

        private readonly LiveTelemetry telemetry = new();
        private LiveLink live;
        private CancellationTokenSource liveLifetime;
        private Timer liveTimer;
        private int livePublishing;
        private volatile bool nudgePending = true;
        private DateTimeOffset lastCommandCheck = DateTimeOffset.MinValue;

        /// <summary>Someone has the app open on this observatory right now.</summary>
        public bool IsWatched => live?.IsWatched == true;

        private void StartLive(CancellationToken token) {
            if (!Settings.Enabled) {
                return;
            }
            live = new LiveLink(LiveSessionAsync, NinaLog.Instance);
            live.WatchedChanged += OnWatchedChanged;
            live.Nudged += OnNudged;
            sync.CheckCommands = ShouldCheckCommands;
            liveLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var lane = live;
            _ = Task.Run(async () => {
                // Wait for pairing and the first check-in (the backend only issues lane tokens to known rigs).
                while (!liveLifetime.IsCancellationRequested && (CurrentCredential() is null || sync.ObservatoryId is null)) {
                    await Task.Delay(TimeSpan.FromSeconds(10), liveLifetime.Token).ConfigureAwait(false);
                }
                var observatory = sync.ObservatoryId;
                if (observatory is not null) {
                    await lane.RunAsync(observatory, RigId, liveLifetime.Token).ConfigureAwait(false);
                }
            }, liveLifetime.Token);
        }

        private void StopLive() {
            liveTimer?.Dispose();
            liveTimer = null;
            liveLifetime?.Cancel();
            if (live is not null) {
                live.WatchedChanged -= OnWatchedChanged;
                live.Nudged -= OnNudged;
                live.Dispose();
                live = null;
            }
            if (sync is not null) {
                sync.CheckCommands = null;
            }
        }

        private async Task<LiveSession> LiveSessionAsync(CancellationToken token) {
            var current = CurrentCredential();
            if (current is null) {
                return null;
            }
            var body = NmJson.Serialize(new { rigId = RigId });
            var result = await transport.PostJsonAsync("/v2/live/session", body, current, token).ConfigureAwait(false);
            if (!result.IsSuccess || result.Body is null) {
                NinaLog.Instance.Debug($"Live lane session refused ({result.StatusCode}).");
                return null;
            }
            return NmJson.Deserialize<LiveSession>(result.Body);
        }

        private void OnWatchedChanged(object sender, bool watched) {
            NinaLog.Instance.Info(watched ? "Someone is watching: streaming live telemetry." : "Nobody watching: live telemetry paused.");
            if (watched && DataOn(DataFeatures.Live)) {
                telemetry.Reset();
                liveTimer ??= new Timer(_ => PublishLive(), null, TimeSpan.Zero, LiveTick);
            } else {
                liveTimer?.Dispose();
                liveTimer = null;
            }
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnNudged(object sender, EventArgs e) {
            nudgePending = true;
            sync?.Wake();
        }

        /// <summary>Look for commands after a nudge, while the lane is down, and every couple of minutes anyway.</summary>
        private bool ShouldCheckCommands() {
            var now = DateTimeOffset.UtcNow;
            if (nudgePending || live?.IsConnected != true || now - lastCommandCheck >= CommandSafetyNet) {
                nudgePending = false;
                lastCommandCheck = now;
                return true;
            }
            return false;
        }

        private void PublishLive() {
            var snapshot = lastSnapshot;
            var lane = live;
            if (snapshot is null || lane is null || !lane.IsWatched || !DataOn(DataFeatures.Live) || Interlocked.Exchange(ref livePublishing, 1) == 1) {
                return;
            }
            _ = Task.Run(async () => {
                try {
                    var update = telemetry.NextUpdate(snapshot, DateTimeOffset.UtcNow, LiveKeepAlive);
                    if (update is not null) {
                        await lane.PublishAsync(update, liveLifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);
                    }
                } catch (Exception ex) {
                    NinaLog.Instance.Debug($"Live telemetry not sent: {ex.Message}");
                } finally {
                    Volatile.Write(ref livePublishing, 0);
                }
            });
        }

        /// <summary>Guide steps for the live graph, in arcseconds when the guider's pixel scale is known.</summary>
        private void AddLiveGuideStep(double raPixels, double decPixels, double raPulseMs, double decPulseMs) {
            var cloud = live?.IsWatched == true;
            var local = localLink?.LiveClients > 0;
            if (!(cloud || local) || !DataOn(DataFeatures.Guiding)) {
                return;
            }
            var scale = collector.GuideSteps.PixelScale > 0 ? collector.GuideSteps.PixelScale : 1;
            var step = new LiveGuideStep(DateTimeOffset.UtcNow, raPixels * scale, decPixels * scale,
                double.IsFinite(raPulseMs) ? raPulseMs : 0, double.IsFinite(decPulseMs) ? decPulseMs : 0);
            if (cloud) telemetry.AddGuideStep(step);
            if (local) AddLocalGuideStep(step);
        }
    }
}
