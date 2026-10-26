using System.Net;
using NightManager.Core.LocalLink;

namespace NightManager.Core.Tests;

public class LocalLinkAddressTests {
    private static LocalAddress Nic(string ip, int prefix = 24, AdapterKind kind = AdapterKind.Ethernet, string name = "Ethernet", string description = "Intel(R) Ethernet") =>
        new(name, description, kind, IPAddress.Parse(ip), prefix);

    [Test]
    public void ListensOnlyOnPhysicalPrivateAdapters() {
        var all = new[] {
            Nic("192.168.1.20"),
            Nic("192.168.50.7", kind: AdapterKind.Wireless, name: "Wi-Fi", description: "Intel Wi-Fi 6"),
            Nic("100.101.102.103", 10, AdapterKind.Other, "Tailscale", "Tailscale Tunnel"),
            Nic("10.8.0.2", kind: AdapterKind.Ethernet, name: "OpenVPN Data Channel Offload", description: "OpenVPN DCO"),
            Nic("10.147.17.5", kind: AdapterKind.Ethernet, name: "ZeroTier One [abc]", description: "ZeroTier Virtual Port"),
            Nic("172.20.0.1", kind: AdapterKind.Tunnel, name: "wg0", description: "WireGuard Tunnel"),
            Nic("127.0.0.1", 8, AdapterKind.Loopback, "Loopback", "Loopback"),
            Nic("81.2.3.4", kind: AdapterKind.Ethernet),
        };
        Assert.That(AddressPolicy.Listenable(all).Select(a => a.Address.ToString()), Is.EqualTo(new[] { "192.168.1.20", "192.168.50.7" }));
    }

        [TestCase("192.168.1.42", true)]      // a phone on the same home Wi-Fi
    [TestCase("192.168.2.42", false)]     // another subnet (a different router, a guest network)
    [TestCase("100.88.1.9", false)]       // Tailscale
    [TestCase("10.0.0.5", false)]         // private, but not this network
    [TestCase("81.2.3.4", false)]         // the internet
    [TestCase("127.0.0.1", false)]
    [TestCase("::ffff:192.168.1.42", true)]
    public void AnswersOnlyTheSameHomeNetwork(string remote, bool allowed) =>
        Assert.That(AddressPolicy.Allows(IPAddress.Parse(remote), Nic("192.168.1.20")), Is.EqualTo(allowed));

    [Test]
    public void NeverAnswersOnAVpnAdapter() =>
        Assert.That(!AddressPolicy.Allows(IPAddress.Parse("100.64.0.9"), Nic("100.64.0.1", 10, AdapterKind.Other, "Tailscale", "Tailscale Tunnel")));

    [Test]
    public void Ipv6LinkLocalOnTheSameLink() {
        var nic = Nic("fe80::1", 64);
        Assert.That(AddressPolicy.Allows(IPAddress.Parse("fe80::abcd"), nic));
        Assert.That(!AddressPolicy.Allows(IPAddress.Parse("2a02:a45c::1"), nic));
    }
}

public class LocalLinkPreviewTests {
    [Test]
    public void PreviewsAreCappedAtTheLongEdgeAndNeverEnlarged() {
        Assert.That(PreviewPolicy.PreviewSize(6248, 4176), Is.EqualTo((2560, 1711)));   // IMX571 (26 MP)
        Assert.That(PreviewPolicy.PreviewSize(4176, 6248), Is.EqualTo((1711, 2560)));   // portrait
        Assert.That(PreviewPolicy.PreviewSize(1920, 1080), Is.EqualTo((1920, 1080)));   // small sensors stay as they are
        Assert.That(PreviewPolicy.PreviewSize(9576, 6388, maxEdge: 10_000), Is.EqualTo((2560, 1708))); // the cap can't be raised
    }

    [Test]
    public void ZoomCropsAreNativePixelsInsideTheFrame() {
        Assert.That(PreviewPolicy.Crop(6248, 4176, 0.5, 0.5), Is.EqualTo(new PixelRect(2612, 1576, 1024, 1024)));
        Assert.That(PreviewPolicy.Crop(6248, 4176, 0, 0), Is.EqualTo(new PixelRect(0, 0, 1024, 1024)));            // corner: kept inside
        Assert.That(PreviewPolicy.Crop(6248, 4176, 1, 1), Is.EqualTo(new PixelRect(5224, 3152, 1024, 1024)));
        Assert.That(PreviewPolicy.Crop(800, 600, 0.5, 0.5), Is.EqualTo(new PixelRect(0, 0, 800, 600)));           // small frame: all of it
        Assert.That(PreviewPolicy.Crop(6248, 4176, 0.5, 0.5, edge: 5000)!.Value.Width, Is.EqualTo(1024));         // the cap can't be raised
        Assert.That(PreviewPolicy.Crop(6248, 4176, double.NaN, 0.5), Is.Null);
    }
}

public class LocalLinkServerTests {
    private sealed class Source : ILocalLinkSource {
        public string HelloJson() => "{\"rigId\":\"R1\"}";
        public (string FrameId, byte[] Jpeg)? LatestPreview() => ("f1", [0xFF, 0xD8, 0xFF]);
        public byte[]? Crop(string frameId, double x, double y) => frameId == "f1" && x is >= 0 and <= 1 ? [1, 2, 3] : null;
        public List<(string Type, IReadOnlyDictionary<string, string> Args)> Commands { get; } = [];
        public Task<(bool Ok, string Message)> Command(string type, IReadOnlyDictionary<string, string> args) {
            Commands.Add((type, args));
            return Task.FromResult((true, "Test shot 5 s…"));
        }
    }

    private static readonly byte[] Key = LocalLinkIdentity.CreateKey();
    private static readonly byte[] ControlKey = LocalLinkIdentity.CreateKey();

    private static (LocalLinkServer Server, string Fingerprint) Start(Func<System.Net.IPAddress, System.Net.IPAddress, bool>? allows = null, Source? source = null) {
        var cert = LocalLinkIdentity.CreateCertificate("test-pc");
        var server = new LocalLinkServer(cert, Key, source ?? new Source(), allows ?? ((_, _) => true), controlKey: ControlKey);
        server.Start([System.Net.IPAddress.Loopback], 48950 + Random.Shared.Next(0, 1000));
        return (server, LocalLinkIdentity.Fingerprint(cert));
    }

    /// <summary>A phone: pins the certificate by fingerprint and signs the request.</summary>
    private static HttpClient Phone(string fingerprint) => new(new HttpClientHandler {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && LocalLinkIdentity.Fingerprint(cert) == fingerprint,
    });

    private static HttpRequestMessage Signed(int port, string target, byte[]? key = null, long? ts = null) {
        var when = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}{target}");
        request.Headers.TryAddWithoutValidation("Authorization", $"NM-Local ts={when},sig={LocalLinkServer.Sign(key ?? Key, when, "GET", target)}");
        return request;
    }

    [Test]
    public async Task AnswersSignedRequestsOnly() {
        var (server, fingerprint) = Start();
        using var _ = server;
        using var phone = Phone(fingerprint);
        var hello = await phone.SendAsync(Signed(server.Port, "/v1/hello"));
        Assert.That((int)hello.StatusCode, Is.EqualTo(200));
        Assert.That(await hello.Content.ReadAsStringAsync(), Does.Contain("R1"));

        Assert.That((int)(await phone.SendAsync(Signed(server.Port, "/v1/hello", key: LocalLinkIdentity.CreateKey()))).StatusCode, Is.EqualTo(401));
        Assert.That((int)(await phone.SendAsync(Signed(server.Port, "/v1/hello", ts: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600))).StatusCode, Is.EqualTo(401));
        Assert.That((int)(await phone.GetAsync($"https://127.0.0.1:{server.Port}/v1/hello")).StatusCode, Is.EqualTo(401));

        var preview = await phone.SendAsync(Signed(server.Port, "/v1/preview"));
        Assert.That(preview.Headers.GetValues("X-NM-Frame").Single(), Is.EqualTo("f1"));
        Assert.That((await phone.SendAsync(Signed(server.Port, "/v1/crop?frame=f1&x=0.5&y=0.5"))).IsSuccessStatusCode);
        Assert.That((int)(await phone.SendAsync(Signed(server.Port, "/v1/crop?frame=nope&x=0.5&y=0.5"))).StatusCode, Is.EqualTo(404));
    }

    [Test]
    public async Task TakesCommandsOnlyWithTheControlKeyAndOnlyOnce() {
        var source = new Source();
        var (server, fingerprint) = Start(source: source);
        using var _ = server;
        using var phone = Phone(fingerprint);
        const string target = "/v1/command?type=equipment&action=camera.shot&seconds=5";

        // A viewer has the link key but not the control key.
        Assert.That((int)(await phone.SendAsync(Signed(server.Port, target))).StatusCode, Is.EqualTo(403));

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        HttpRequestMessage Controlled() {
            var request = Signed(server.Port, target, ts: ts);
            request.Headers.TryAddWithoutValidation("X-NM-Control", LocalLinkServer.Sign(ControlKey, ts, "GET", target));
            return request;
        }
        var ok = await phone.SendAsync(Controlled());
        Assert.That((int)ok.StatusCode, Is.EqualTo(200));
        Assert.That(source.Commands.Single().Type, Is.EqualTo("equipment"));
        Assert.That(source.Commands.Single().Args["action"], Is.EqualTo("camera.shot"));
        Assert.That(source.Commands.Single().Args["seconds"], Is.EqualTo("5"));
        // The same request again (captured on the network) is refused.
        Assert.That((int)(await phone.SendAsync(Controlled())).StatusCode, Is.EqualTo(403));
        Assert.That(source.Commands, Has.Count.EqualTo(1));
    }

    [Test]
    public void DropsConnectionsFromOtherNetworks() {
        var (server, fingerprint) = Start((_, _) => false);
        using var _ = server;
        using var phone = Phone(fingerprint);
        Assert.ThrowsAsync<HttpRequestException>(() => phone.SendAsync(Signed(server.Port, "/v1/hello")));
    }

    [Test]
    public async Task StreamsLiveUpdatesOverAWebSocket() {
        var (server, fingerprint) = Start();
        using var _ = server;
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && LocalLinkIdentity.Fingerprint(new System.Security.Cryptography.X509Certificates.X509Certificate2(cert)) == fingerprint;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        socket.Options.SetRequestHeader("Authorization", $"NM-Local ts={ts},sig={LocalLinkServer.Sign(Key, ts, "GET", "/v1/live")}");
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{server.Port}/v1/live"), CancellationToken.None);
        for (var i = 0; i < 50 && server.LiveClients == 0; i++) await Task.Delay(20);
        await server.PublishAsync("{\"t\":1}");
        var buffer = new byte[256];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.That(System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count), Is.EqualTo("{\"t\":1}"));
    }
}
