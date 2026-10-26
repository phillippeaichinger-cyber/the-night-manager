using NightManager.Core.Settings;
using NUnit.Framework;

namespace NightManager.Core.Tests;

/// <summary>Runs in both builds: the normal plugin (live) and the dev plugin (-p:NmChannel=dev).</summary>
public class ChannelTests {
    private static string Mine => InstallationSettings.IsDevChannel ? InstallationSettings.DevServiceUrl : InstallationSettings.LiveServiceUrl;
    private static string Other => InstallationSettings.IsDevChannel ? InstallationSettings.LiveServiceUrl : InstallationSettings.DevServiceUrl;

    [Test]
    public void Installing_a_channel_moves_the_PC_to_its_server() {
        Assert.That(InstallationSettings.ChannelServiceUrl(Other), Is.EqualTo(Mine));
        Assert.That(InstallationSettings.ChannelServiceUrl(Other + "/"), Is.EqualTo(Mine));
        Assert.That(InstallationSettings.ChannelServiceUrl(Mine), Is.EqualTo(Mine));
        Assert.That(new InstallationSettings { ServiceUrl = Other }.Normalized().ServiceUrl, Is.EqualTo(Mine));
    }

    [Test]
    public void A_custom_server_is_kept_and_junk_falls_back() {
        Assert.That(InstallationSettings.ChannelServiceUrl("http://localhost:5001"), Is.EqualTo("http://localhost:5001"));
        Assert.That(InstallationSettings.ChannelServiceUrl("http://example.com"), Is.EqualTo(Mine)); // not https
        Assert.That(InstallationSettings.ChannelServiceUrl(null), Is.EqualTo(Mine));
    }
}
