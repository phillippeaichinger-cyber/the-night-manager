using System.Text;
using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Security;

namespace NightManager.Core.Tests;

public class SecurityAndJsonTests {
    [Test]
    public void Credential_ParsesKeyIdAndSecret() {
        Assert.That(ApiCredential.TryParse("nm2_ab12cd34_0123456789abcdef0123456789abcdefXYZ", out var credential), Is.True);
        Assert.That(credential!.KeyId, Is.EqualTo("ab12cd34"));
        Assert.That(credential.Secret, Is.EqualTo("0123456789abcdef0123456789abcdefXYZ"));
        Assert.That(credential.Redacted, Does.Not.Contain(credential.Secret));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("nm_live_0123456789abcdef0123456789abcdef0123456789abcdef")]
    [TestCase("nm2_ab12cd34_tooShort")]
    [TestCase("nm2__0123456789abcdef0123456789abcdefXYZ")]
    [TestCase("nm2_ab12-cd34_0123456789abcdef0123456789abcdefXYZ")]
    public void Credential_RejectsMalformedValues(string? value) {
        Assert.That(ApiCredential.TryParse(value, out _), Is.False);
    }

    [Test]
    public void Signature_MatchesIndependentReferenceVector() {
        // Expected value computed with Python's hmac/hashlib from the documented canonical string.
        var signature = RequestSigner.ComputeSignature(
            "0123456789abcdef0123456789abcdefXYZ",
            "post",
            "/v2/ingest",
            "2026-10-12T21:04:05.123Z",
            Encoding.UTF8.GetBytes("{\"hello\":\"world\"}"));

        Assert.That(signature, Is.EqualTo("b39024e94862cf8778ed4423900cfed6adc1cc823e4ee27fe954cfe2a9a0f79f"));
    }

    [Test]
    public void Signature_ChangesWithPathAndBody() {
        var body = Encoding.UTF8.GetBytes("{}");
        var a = RequestSigner.ComputeSignature("secret", "POST", "/v2/ingest", "t", body);
        var b = RequestSigner.ComputeSignature("secret", "POST", "/v2/pair/poll", "t", body);
        var c = RequestSigner.ComputeSignature("secret", "POST", "/v2/ingest", "t", Encoding.UTF8.GetBytes("{ }"));

        Assert.That(new[] { a, b, c }, Is.Unique);
    }

    [TestCase("/v2/ingest", "/v2/ingest")]
    [TestCase("/api/v2/ingest", "/v2/ingest")]
    [TestCase("/demo-nightmanager/europe-west1/api/v2/pair/poll", "/v2/pair/poll")]
    public void CanonicalPath_StartsAtVersionSegment(string absolute, string expected) {
        Assert.That(RequestSigner.CanonicalPath(absolute), Is.EqualTo(expected));
    }

    [Test]
    public void Timestamps_AreUtcWithMilliseconds() {
        var local = new DateTimeOffset(2026, 10, 12, 23, 4, 5, 123, TimeSpan.FromHours(2)).AddTicks(4567);

        Assert.That(Timestamps.Format(local), Is.EqualTo("2026-10-12T21:04:05.123Z"));
    }

    [Test]
    public void IngestItems_RoundTripWithTypeDiscriminator() {
        IngestItem[] items = [
            new EventItem { Id = "e1", Timestamp = DateTimeOffset.UnixEpoch, Kind = EventKinds.Clouds, Title = "Possible clouds", Severity = Severity.Warning, IncidentKey = "clouds", IncidentPhase = "open" },
            new FrameItem { Id = "f1", Timestamp = DateTimeOffset.UnixEpoch, FileName = "M42_L_001.fits", Hfr = 2.1, Grade = new FrameGrade { Score = 90, Verdict = Verdicts.Accept } },
            new SnapshotItem { Id = "s1", Timestamp = DateTimeOffset.UnixEpoch, Snapshot = new ObservatorySnapshot { Camera = new CameraState { Connected = true, Temperature = -10 } } }
        ];

        var json = NmJson.Serialize(items);
        var back = NmJson.Deserialize<IngestItem[]>(json)!;

        Assert.That(json, Does.Contain("\"type\":\"event\"").And.Contain("\"type\":\"frame\"").And.Contain("\"type\":\"snapshot\""));
        Assert.That(json, Does.Not.Contain("null"), "Nulls must be omitted on the wire.");
        Assert.That(back[0], Is.TypeOf<EventItem>().And.Property("IncidentPhase").EqualTo("open"));
        Assert.That(((FrameItem)back[1]).Grade!.Verdict, Is.EqualTo(Verdicts.Accept));
        Assert.That(((SnapshotItem)back[2]).Snapshot.Camera!.Temperature, Is.EqualTo(-10));
    }

    [Test]
    public void Json_UsesCamelCase() {
        var json = NmJson.Serialize(new CameraState { Connected = true, CoolerPower = 42 });

        Assert.That(json, Is.EqualTo("{\"connected\":true,\"coolerPower\":42}"));
    }
}
