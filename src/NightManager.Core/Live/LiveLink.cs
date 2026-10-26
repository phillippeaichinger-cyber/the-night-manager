using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightManager.Core.Logging;

namespace NightManager.Core.Live;

/// <summary>What the backend hands a rig for the live lane (<c>POST /v2/live/session</c>).</summary>
public sealed record LiveSession {
    public required string CustomToken { get; init; }
    public required string DatabaseUrl { get; init; }
    public required string ApiKey { get; init; }
    public string? Uid { get; init; }
}

/// <summary>
/// The plugin's end of the live lane (Firebase Realtime Database over its REST and streaming API; there's no
/// .NET SDK). Holds two streams open: <c>watchers/{obs}</c> (is anyone looking?) and <c>nudge/{obs}/{rig}</c>
/// (a command is waiting), and writes <c>live/{obs}/{rig}</c> while someone watches. Reconnects with backoff and
/// renews its token before it expires; the rest of the plugin works the same if the lane is down.
/// </summary>
public sealed class LiveLink : IDisposable {
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);

    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<LiveSession?>> sessionProvider;
    private readonly INmLog log;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly HashSet<string> watchers = new(StringComparer.Ordinal);

    private LiveSession? session;
    private string? idToken;
    private string? refreshToken;
    private DateTimeOffset tokenAt = DateTimeOffset.MinValue;
    private long lastNudgeAt;
    private bool watchersConnected;
    private bool nudgeConnected;

    public LiveLink(Func<CancellationToken, Task<LiveSession?>> sessionProvider, INmLog? log = null, HttpMessageHandler? handler = null, TimeProvider? time = null) {
        this.sessionProvider = sessionProvider;
        this.log = log ?? NullLog.Instance;
        this.time = time ?? TimeProvider.System;
        http = handler is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Someone has the app open on this observatory.</summary>
    public event EventHandler<bool>? WatchedChanged;

    /// <summary>The backend asked this rig to check in now (a command is waiting).</summary>
    public event EventHandler? Nudged;

    public bool IsWatched {
        get { lock (gate) { return watchers.Count > 0; } }
    }

    /// <summary>Both streams are up: commands will arrive by nudge, so check-ins needn't look for them.</summary>
    public bool IsConnected {
        get { lock (gate) { return watchersConnected && nudgeConnected; } }
    }

    public string? ObservatoryId { get; private set; }
    public string? RigId { get; private set; }

    /// <summary>Runs until cancelled: keeps both streams open, reconnecting with backoff.</summary>
    public async Task RunAsync(string observatoryId, string rigId, CancellationToken token) {
        ObservatoryId = observatoryId;
        RigId = rigId;
        var watchersTask = KeepStreaming($"watchers/{observatoryId}", OnWatchersEvent, connected => SetConnected(watchers: connected), token);
        var nudgeTask = KeepStreaming($"nudge/{observatoryId}/{rigId}", OnNudgeEvent, connected => SetConnected(nudge: connected), token);
        await Task.WhenAll(watchersTask, nudgeTask).ConfigureAwait(false);
    }

    /// <summary>Merges these fields into <c>live/{obs}/{rig}</c>. False when the lane is down (nothing is retried).</summary>
    public async Task<bool> PublishAsync(JsonObject fields, CancellationToken token) {
        if (ObservatoryId is null || RigId is null) {
            return false;
        }
        try {
            var auth = await EnsureTokenAsync(token).ConfigureAwait(false);
            if (auth is null || session is null) {
                return false;
            }
            using var request = new HttpRequestMessage(HttpMethod.Patch, Url($"live/{ObservatoryId}/{RigId}", auth, "print=silent")) {
                Content = new StringContent(fields.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
                ExpireToken();
            }
            return response.IsSuccessStatusCode;
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            return false;
        } catch (HttpRequestException ex) {
            log.Debug($"Live publish failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => http.Dispose();

    // MARK: Streams

    private async Task KeepStreaming(string path, Action<string, JsonNode?> onEvent, Action<bool> connected, CancellationToken token) {
        var backoff = TimeSpan.FromSeconds(2);
        while (!token.IsCancellationRequested) {
            try {
                var auth = await EnsureTokenAsync(token).ConfigureAwait(false);
                if (auth is not null) {
                    await StreamOnce(path, auth, onEvent, connected, token).ConfigureAwait(false);
                    backoff = TimeSpan.FromSeconds(2);
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                log.Debug($"Live stream {path} dropped: {ex.Message}");
            }
            connected(false);
            try {
                await Task.Delay(backoff, time, token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                break;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(120, backoff.TotalSeconds * 2));
        }
        connected(false);
    }

    /// <summary>One streaming connection (server-sent events) until it ends, the token expires or rules deny it.</summary>
    private async Task StreamOnce(string path, string auth, Action<string, JsonNode?> onEvent, Action<bool> connected, CancellationToken token) {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(path, auth));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
                ExpireToken();
            }
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        }
        connected(true);
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        var renewAt = tokenAt + TokenLifetime;
        while (!token.IsCancellationRequested) {
            if (time.GetUtcNow() >= renewAt) {
                return; // reconnect with a fresh token
            }
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) {
                return;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal)) {
                eventName = line[6..].Trim();
            } else if (line.StartsWith("data:", StringComparison.Ordinal)) {
                data.Append(line[5..].Trim());
            } else if (line.Length == 0 && eventName is not null) {
                var name = eventName;
                var payload = data.ToString();
                eventName = null;
                data.Clear();
                switch (name) {
                    case "put":
                    case "patch":
                        var node = payload.Length == 0 || payload == "null" ? null : JsonNode.Parse(payload);
                        onEvent(name, node);
                        break;
                    case "auth_revoked":
                        ExpireToken();
                        return;
                    case "cancel":
                        throw new HttpRequestException($"Stream {path} cancelled: {payload}");
                }
            }
        }
    }

    /// <summary>watchers/{obs}: the full map first ("put" at "/"), then single changes ("put" at "/{uid}").</summary>
    internal void OnWatchersEvent(string kind, JsonNode? message) {
        var path = message?["path"]?.GetValue<string>() ?? "/";
        var data = message?["data"];
        bool before, after;
        lock (gate) {
            before = watchers.Count > 0;
            if (path == "/") {
                if (kind == "put") {
                    watchers.Clear();
                }
                if (data is JsonObject all) {
                    foreach (var (uid, value) in all) {
                        if (value is null) watchers.Remove(uid); else watchers.Add(uid);
                    }
                }
            } else {
                var uid = path.Trim('/').Split('/')[0];
                if (path.Trim('/').Contains('/')) {
                    watchers.Add(uid); // a field of a watcher changed (its "at")
                } else if (data is null) {
                    watchers.Remove(uid);
                } else {
                    watchers.Add(uid);
                }
            }
            after = watchers.Count > 0;
        }
        if (before != after) {
            WatchedChanged?.Invoke(this, after);
        }
    }

    /// <summary>nudge/{obs}/{rig}: {at, reason}. The first message is the last nudge ever sent: only newer ones count.</summary>
    internal void OnNudgeEvent(string kind, JsonNode? message) {
        var data = message?["data"];
        var at = data?["at"]?.GetValue<long>() ?? (message?["path"]?.GetValue<string>() == "/at" ? data?.GetValue<long>() ?? 0 : 0);
        if (at <= 0) {
            return;
        }
        bool fire;
        lock (gate) {
            fire = lastNudgeAt != 0 && at > lastNudgeAt;
            if (lastNudgeAt == 0 && at > time.GetUtcNow().ToUnixTimeMilliseconds() - 60_000) {
                fire = true; // a command issued while we were (re)connecting
            }
            lastNudgeAt = Math.Max(lastNudgeAt, at);
        }
        if (fire) {
            Nudged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetConnected(bool? watchers = null, bool? nudge = null) {
        bool lostWatchers;
        lock (gate) {
            if (watchers is { } w) watchersConnected = w;
            if (nudge is { } n) nudgeConnected = n;
            // While the watchers stream is down nobody is known to watch: stop streaming telemetry.
            lostWatchers = watchers == false && this.watchers.Count > 0;
            if (lostWatchers) this.watchers.Clear();
        }
        if (lostWatchers) {
            WatchedChanged?.Invoke(this, false);
        }
    }

    // MARK: Auth

    private string Url(string path, string auth, string? extra = null) {
        var baseUrl = session!.DatabaseUrl.TrimEnd('/');
        return $"{baseUrl}/{path}.json?auth={Uri.EscapeDataString(auth)}" + (extra is null ? "" : "&" + extra);
    }

    private void ExpireToken() {
        lock (gate) {
            tokenAt = DateTimeOffset.MinValue;
            idToken = null;
        }
    }

    private readonly SemaphoreSlim tokenGate = new(1, 1);

    /// <summary>A valid ID token for the rig's live identity: renewed with the refresh token, or a new session.</summary>
    private async Task<string?> EnsureTokenAsync(CancellationToken token) {
        lock (gate) {
            if (idToken is not null && time.GetUtcNow() - tokenAt < TokenLifetime) {
                return idToken;
            }
        }
        await tokenGate.WaitAsync(token).ConfigureAwait(false);
        try {
            lock (gate) {
                if (idToken is not null && time.GetUtcNow() - tokenAt < TokenLifetime) {
                    return idToken;
                }
            }
            if (session is not null && refreshToken is not null && await RefreshAsync(token).ConfigureAwait(false)) {
                return idToken;
            }
            session = await sessionProvider(token).ConfigureAwait(false);
            if (session is null) {
                return null;
            }
            var body = JsonSerializer.Serialize(new { token = session.CustomToken, returnSecureToken = true });
            using var response = await http.PostAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken?key={Uri.EscapeDataString(session.ApiKey)}",
                new StringContent(body, Encoding.UTF8, "application/json"), token).ConfigureAwait(false);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (!response.IsSuccessStatusCode || json?["idToken"] is null) {
                log.Warning($"Live lane sign-in failed ({(int)response.StatusCode}).");
                return null;
            }
            lock (gate) {
                idToken = json["idToken"]!.GetValue<string>();
                refreshToken = json["refreshToken"]?.GetValue<string>();
                tokenAt = time.GetUtcNow();
                return idToken;
            }
        } finally {
            tokenGate.Release();
        }
    }

    private async Task<bool> RefreshAsync(CancellationToken token) {
        try {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken! });
            using var response = await http.PostAsync($"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(session!.ApiKey)}", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                return false;
            }
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var fresh = json?["id_token"]?.GetValue<string>();
            if (fresh is null) {
                return false;
            }
            lock (gate) {
                idToken = fresh;
                refreshToken = json?["refresh_token"]?.GetValue<string>() ?? refreshToken;
                tokenAt = time.GetUtcNow();
            }
            return true;
        } catch (HttpRequestException) {
            return false;
        }
    }
}
