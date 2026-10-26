using NightManager.Core.Contracts;
using NightManager.Core.Json;
using NightManager.Core.Pairing;

namespace NightManager.Core.Tests;

public class PairingMessageTests {
    [Test]
    public void Shows_the_web_page_without_the_code_query() {
        var message = PairingClient.WaitingMessage("https://nightmanager-77ad3.web.app/pair?code=K7P-4QX");
        Assert.That(message, Does.Contain("https://nightmanager-77ad3.web.app/pair in any browser"));
        Assert.That(message, Does.Not.Contain("code="));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("http://insecure.example/pair")]
    [TestCase("not a url")]
    public void Falls_back_to_the_app_only_message(string? url) {
        Assert.That(PairingClient.WaitingMessage(url), Is.EqualTo((global::NightManager.Core.Settings.InstallationSettings.IsDevChannel ? "Open the NM Dev app (not Night Manager) → " : "Open The Night Manager app → ") + "Add observatory, and enter this code."));
    }

    [Test]
    public void Reads_verification_url_from_pair_start() {
        var response = NmJson.Deserialize<PairStartResponse>("""
            {"pairingId":"p","userCode":"K7P-4QX","pollToken":"t","expiresAt":"2026-10-12T21:00:00Z","pollIntervalSeconds":3,"verificationUrl":"https://x.example/pair?code=K7P-4QX"}
            """);
        Assert.That(response!.VerificationUrl, Is.EqualTo("https://x.example/pair?code=K7P-4QX"));
    }
}
