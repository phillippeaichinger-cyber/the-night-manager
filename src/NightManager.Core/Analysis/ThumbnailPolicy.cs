namespace NightManager.Core.Analysis;

/// <summary>
/// Which subs get a thumbnail. Subs of 3 minutes or longer always do (the usual 180–300 s); shorter ones at most one
/// every 3 minutes, plus the first sub of a new target or filter and up to two rejected subs an hour, so 10 s
/// imagers don't send thousands of pictures a night. Every sub still sends its numbers (HFR, stars, grade).
/// </summary>
public sealed class ThumbnailPolicy {
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(3);
    public const int RejectsPerHour = 2;

    private readonly Queue<DateTimeOffset> rejects = new();
    private DateTimeOffset lastSent = DateTimeOffset.MinValue;
    private string? lastKey;

    public bool ShouldSend(DateTimeOffset at, double exposureSeconds, string? target, string? filter, string? verdict) {
        var key = $"{target}|{filter}";
        var send = exposureSeconds >= Interval.TotalSeconds
            || !string.Equals(key, lastKey, StringComparison.OrdinalIgnoreCase)
            || at - lastSent >= Interval;

        while (rejects.Count > 0 && at - rejects.Peek() > TimeSpan.FromHours(1)) {
            rejects.Dequeue();
        }
        if (!send && verdict == "reject" && rejects.Count < RejectsPerHour) {
            rejects.Enqueue(at);
            send = true;
        }

        lastKey = key;
        if (send) {
            lastSent = at;
        }
        return send;
    }
}
