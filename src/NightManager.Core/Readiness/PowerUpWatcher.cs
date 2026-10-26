namespace NightManager.Core.Readiness;

/// <summary>
/// Notices the evening's equipment coming up, whoever brings it up (the sequence's switches and scripts, or a person),
/// and says once per night when it has settled: nothing new connected for a few minutes, or imaging started.
/// Rigs that stay connected all day settle as soon as the night's window opens.
/// </summary>
public sealed class PowerUpWatcher {
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromMinutes(5);

    private string? night;
    private HashSet<string> last = [];
    private DateTimeOffset lastChange;
    private bool done;

    /// <summary>
    /// Feed what's connected now. Returns the settled set once per night (inside the night's window), else null.
    /// </summary>
    public IReadOnlySet<string>? Tick(DateTimeOffset now, string nightKey, bool inNightWindow, IReadOnlySet<string> connected, bool imaging) {
        if (nightKey != night) {
            night = nightKey;
            done = false;
            last = [];
            lastChange = now;
        }
        if (done || !inNightWindow) {
            if (!inNightWindow) {
                // Before the window: keep following, so a rig that's been on all afternoon settles at once.
                Follow(now, connected);
            }
            return null;
        }
        Follow(now, connected);
        if (last.Count == 0) {
            return null;
        }
        if (imaging || now - lastChange >= SettleTime) {
            done = true;
            return last;
        }
        return null;
    }

    /// <summary>Forget tonight's result (a re-run from the app, or the equipment going off and on again).</summary>
    public void Reset() => done = false;

    private void Follow(DateTimeOffset now, IReadOnlySet<string> connected) {
        if (!last.SetEquals(connected)) {
            last = [.. connected];
            lastChange = now;
        }
    }
}
