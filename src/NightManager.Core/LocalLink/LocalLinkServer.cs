using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NightManager.Core.Logging;

namespace NightManager.Core.LocalLink;

/// <summary>What a phone on the home network can ask for. Read-only: commands still go through the server.</summary>
public interface ILocalLinkSource {
    /// <summary>Who this is (observatory and rig ids, names, plugin version): sent as JSON to /v1/hello.</summary>
    string HelloJson();
    /// <summary>The newest sub as a capped JPEG preview (≤ 2560 px), with its id; null if there's none yet.</summary>
    (string FrameId, byte[] Jpeg)? LatestPreview();
    /// <summary>A native-resolution crop (≤ 1024 px) of a recent sub, centred on fractions 0–1 of the frame.</summary>
    byte[]? Crop(string frameId, double x, double y);
    /// <summary>
    /// A hands-on command straight from a phone on this network (field mode: no internet needed). Only reached with
    /// the control key, which the server gives to members who may control the observatory. Same checks as from the
    /// server (the owner's ticks in NINA, a running sequence…).
    /// </summary>
    Task<(bool Ok, string Message)> Command(string type, IReadOnlyDictionary<string, string> args) =>
        Task.FromResult((false, "This plugin doesn't take commands over the home network."));
}

/// <summary>
/// The local link's server (docs/LOCAL-LINK.md): HTTPS and a WebSocket on the home network only, for phones that
/// are on the same Wi-Fi as this PC. Every request is signed with the link key the observatory's members get through
/// the server; the address rules keep it off VPNs. Built on plain sockets so NINA needs no admin rights to listen.
/// </summary>
public sealed class LocalLinkServer : IDisposable {
    public const int DefaultPort = 47950;
    /// <summary>How far a request's time may be from this PC's clock (seconds).</summary>
    public const int ClockSkewSeconds = 120;
    private const int MaxHeaderBytes = 8 * 1024;
    private const int MaxLiveClients = 8;

    private readonly X509Certificate2 certificate;
    private readonly byte[] key;
    private readonly byte[]? controlKey;
    /// <summary>Command signatures already used (a captured request can't be sent again).</summary>
    private readonly ConcurrentDictionary<string, long> usedCommands = new();
    private readonly ILocalLinkSource source;
    private readonly Func<IPAddress, IPAddress, bool> allows;
    private readonly INmLog? log;
    private readonly List<TcpListener> listeners = [];
    private readonly ConcurrentDictionary<Guid, WebSocket> liveClients = new();
    private readonly CancellationTokenSource lifetime = new();

    /// <param name="allows">Whether a request from the first address, arriving on the second, may be answered.</param>
    public LocalLinkServer(X509Certificate2 certificate, byte[] key, ILocalLinkSource source, Func<IPAddress, IPAddress, bool> allows, INmLog? log = null, byte[]? controlKey = null) {
        this.certificate = certificate;
        this.key = key;
        this.controlKey = controlKey;
        this.source = source;
        this.allows = allows;
        this.log = log;
    }

    /// <summary>The port listened on (the first free one from <see cref="DefaultPort"/>).</summary>
    public int Port { get; private set; }

    /// <summary>Phones streaming live data right now.</summary>
    public int LiveClients => liveClients.Count;

    /// <summary>Listens on the given addresses (already filtered by <see cref="AddressPolicy.Listenable"/>).</summary>
    public void Start(IEnumerable<IPAddress> addresses, int firstPort = DefaultPort) {
        var list = addresses.ToList();
        for (var port = firstPort; port < firstPort + 20 && listeners.Count == 0; port++) {
            try {
                foreach (var address in list) {
                    var listener = new TcpListener(address, port);
                    listener.Start();
                    listeners.Add(listener);
                }
                Port = port;
            } catch (SocketException) {
                foreach (var l in listeners) l.Stop();
                listeners.Clear();
            }
        }
        foreach (var listener in listeners) {
            _ = Task.Run(() => AcceptLoop(listener));
        }
        if (listeners.Count > 0) log?.Info($"Local link listening on {string.Join(", ", list)} port {Port}.");
    }

    /// <summary>Sends a live update (the same JSON as the live lane) to every phone streaming locally.</summary>
    public async Task PublishAsync(string json) {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var (id, socket) in liveClients) {
            try {
                if (socket.State == WebSocketState.Open) {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                } else {
                    liveClients.TryRemove(id, out _);
                }
            } catch {
                liveClients.TryRemove(id, out _);
            }
        }
    }

    private async Task AcceptLoop(TcpListener listener) {
        while (!lifetime.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
            } catch {
                return;
            }
            _ = Task.Run(() => Handle(client));
        }
    }

    private async Task Handle(TcpClient client) {
        var keepOpen = false;
        try {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var local = (client.Client.LocalEndPoint as IPEndPoint)?.Address;
            if (remote is null || local is null || !allows(remote, local)) {
                return; // not our network: drop without a word
            }
            client.ReceiveTimeout = 10_000;
            var ssl = new SslStream(client.GetStream(), false);
            using var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, handshake.Token).ConfigureAwait(false);
            var request = await ReadRequest(ssl).ConfigureAwait(false);
            if (request is null) return;
            if (!Authorised(request)) {
                await Respond(ssl, 401, "text/plain", "Unauthorised"u8.ToArray()).ConfigureAwait(false);
                return;
            }
            keepOpen = await Route(ssl, client, request).ConfigureAwait(false);
        } catch (Exception ex) {
            log?.Debug($"Local link request failed: {ex.Message}");
        } finally {
            if (!keepOpen) client.Dispose();
        }
    }

    private async Task<bool> Route(SslStream ssl, TcpClient client, Request request) {
        var path = request.Path;
        var query = request.Query;
        switch (request.Method, path) {
            case ("GET", "/v1/hello"):
                await Respond(ssl, 200, "application/json", Encoding.UTF8.GetBytes(source.HelloJson())).ConfigureAwait(false);
                return false;
            case ("GET", "/v1/preview"):
                if (source.LatestPreview() is { } preview) {
                    await Respond(ssl, 200, "image/jpeg", preview.Jpeg, ("X-NM-Frame", preview.FrameId)).ConfigureAwait(false);
                } else {
                    await Respond(ssl, 404, "text/plain", "No sub yet"u8.ToArray()).ConfigureAwait(false);
                }
                return false;
            case ("GET", "/v1/crop"):
                var crop = query.TryGetValue("frame", out var frame)
                    && double.TryParse(query.GetValueOrDefault("x"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(query.GetValueOrDefault("y"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)
                    ? source.Crop(frame, x, y) : null;
                await (crop is null ? Respond(ssl, 404, "text/plain", "No such sub"u8.ToArray()) : Respond(ssl, 200, "image/jpeg", crop)).ConfigureAwait(false);
                return false;
            case ("GET", "/v1/live") when request.Headers.TryGetValue("sec-websocket-key", out var wsKey):
                if (liveClients.Count >= MaxLiveClients) {
                    await Respond(ssl, 503, "text/plain", "Too many phones"u8.ToArray()).ConfigureAwait(false);
                    return false;
                }
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                var head = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
                client.ReceiveTimeout = 0;
                var socket = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(15) });
                var id = Guid.NewGuid();
                liveClients[id] = socket;
                _ = Task.Run(() => DrainUntilClosed(id, socket, client));
                return true;
            case ("GET", "/v1/command"):
                // Signed twice: the link key (above) and the control key, which viewers never get.
                if (!Controls(request, out var ts)) {
                    await Respond(ssl, 403, "application/json", Json(false, "Not allowed to control this observatory.")).ConfigureAwait(false);
                    return false;
                }
                var type = query.GetValueOrDefault("type") ?? "";
                var args = query.Where(q => q.Key != "type").ToDictionary(q => q.Key, q => q.Value);
                var (ok, message) = await source.Command(type, args).ConfigureAwait(false);
                await Respond(ssl, ok ? 200 : 409, "application/json", Json(ok, message)).ConfigureAwait(false);
                return false;
            default:
                await Respond(ssl, 404, "text/plain", "Not found"u8.ToArray()).ConfigureAwait(false);
                return false;
        }
    }

    /// <summary>Phones don't send anything on the live socket; read until it closes, then forget it.</summary>
    private async Task DrainUntilClosed(Guid id, WebSocket socket, TcpClient client) {
        var buffer = new byte[1024];
        try {
            while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested) {
                var result = await socket.ReceiveAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        } catch {
            // gone
        } finally {
            liveClients.TryRemove(id, out _);
            socket.Dispose();
            client.Dispose();
        }
    }

    /// <summary>"Authorization: NM-Local ts=…,sig=…": HMAC-SHA256 of "ts\nMETHOD\n/path?query" with the link key.</summary>
    private bool Authorised(Request request) {
        if (!request.Headers.TryGetValue("authorization", out var auth) || !auth.StartsWith("NM-Local ", StringComparison.Ordinal)) return false;
        var parts = auth["NM-Local ".Length..].Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!parts.TryGetValue("ts", out var tsText) || !long.TryParse(tsText, out var ts) || !parts.TryGetValue("sig", out var sig)) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > ClockSkewSeconds) return false;
        var expected = Sign(key, ts, request.Method, request.Target);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig.ToLowerInvariant()));
    }

    /// <summary>"X-NM-Control: sig": the same message signed with the control key, used once.</summary>
    private bool Controls(Request request, out long ts) {
        ts = 0;
        if (controlKey is null || !request.Headers.TryGetValue("x-nm-control", out var sig)) return false;
        var auth = request.Headers["authorization"];
        var tsPart = auth["NM-Local ".Length..].Split(',').Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == "ts");
        if (tsPart is null || !long.TryParse(tsPart[1].Trim(), out ts)) return false;
        var expected = Sign(controlKey, ts, request.Method, request.Target);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(sig.Trim().ToLowerInvariant()))) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var old in usedCommands.Where(u => now - u.Value > ClockSkewSeconds * 2).Select(u => u.Key).ToList()) usedCommands.TryRemove(old, out _);
        return usedCommands.TryAdd(expected, now);
    }

    private static byte[] Json(bool ok, string message) =>
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { ok, message }));

    /// <summary>The signature a phone sends (also used by tests and documented for the apps).</summary>
    public static string Sign(byte[] key, long unixSeconds, string method, string target) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{unixSeconds}\n{method}\n{target}"))).ToLowerInvariant();

    private sealed record Request(string Method, string Target, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers);

    private static async Task<Request?> ReadRequest(Stream stream) {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1)).ConfigureAwait(false);
            if (read == 0) return null;
            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) return null;
        var target = first[1];
        var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => l.Split(':', 2))
            .GroupBy(p => p[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? new Dictionary<string, string>() : target[(q + 1)..].Split('&').Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2).GroupBy(p => Uri.UnescapeDataString(p[0])).ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1]));
        return new Request(first[0].ToUpperInvariant(), target, path, query, headers);
    }

    private static async Task Respond(Stream stream, int status, string contentType, byte[] body, params (string Name, string Value)[] extra) {
        var reason = status switch { 200 => "OK", 401 => "Unauthorized", 404 => "Not Found", 503 => "Service Unavailable", _ => "Error" };
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n");
        foreach (var (name, value) in extra) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose() {
        lifetime.Cancel();
        foreach (var l in listeners) l.Stop();
        foreach (var socket in liveClients.Values) socket.Dispose();
        liveClients.Clear();
    }
}

/// <summary>The link's identity: a self-signed certificate (the phone pins its fingerprint) and the signing key.</summary>
public static class LocalLinkIdentity {
    public static X509Certificate2 CreateCertificate(string name) {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", ecdsa, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // Round-trip through PFX so the private key is usable by SslStream on Windows.
#pragma warning disable SYSLIB0057 // the loader that replaces it is .NET 9+; Core targets .NET 8 (NINA 3.2)
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
    }

    public static byte[] CreateKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>SHA-256 of the certificate, lowercase hex: what the phone checks the PC's certificate against.</summary>
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
}
