using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Settings;

namespace NightManager.Core.Tests;

public class SettingsTests {
    private string root = null!;

    [SetUp]
    public void SetUp() {
        root = Path.Combine(Path.GetTempPath(), "nm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true);

    // Exactly what the first (single-rig) build wrote, including the numeric enum.
    private const string LegacyCombinedSettings = """
        {
          "schemaVersion": 1,
          "serviceUrl": "https://api.ninanightagent.com",
          "installationId": "3666a23494e44a63a19eae4626d543f2",
          "enabled": true,
          "uploadThumbnails": true,
          "thumbnailMaxDimension": 800,
          "thumbnailQuality": 80,
          "sendFileNames": false,
          "gradingSensitivity": 2,
          "writeCullList": true,
          "guidingAlertArcsec": 1.5,
          "allowedCommands": ["ping", "abortSequence", "parkMount"]
        }
        """;

    [Test]
    public void Installation_ReadsLegacyCombinedFile_KeepingInstallationId() {
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, LegacyCombinedSettings);

        var installation = InstallationSettings.Load(path);

        Assert.That(installation.InstallationId, Is.EqualTo("3666a23494e44a63a19eae4626d543f2"));
        Assert.That(installation.ServiceUrl, Is.EqualTo("https://api.ninanightagent.com"));
    }

    [Test]
    public void NewRig_IsSeededFromLegacySettings_ThenIndependent() {
        var legacy = Path.Combine(root, "settings.json");
        File.WriteAllText(legacy, LegacyCombinedSettings);
        var rigA = Path.Combine(root, "rigs", "a", "settings.json");
        var rigB = Path.Combine(root, "rigs", "b", "settings.json");

        var a = RigSettings.Load(rigA, legacy);
        Assert.That(a.SendFileNames, Is.False);
        Assert.That(a.GradingSensitivity, Is.EqualTo(GradingSensitivity.Strict));
        Assert.That(a.AllowedCommands, Is.EquivalentTo(new[] { CommandTypes.Ping, CommandTypes.AbortSequence, CommandTypes.ParkMount }));

        // Rig A disallows parking (e.g. it is a camera-only scope on a shared mount); rig B is unaffected.
        (a with { AllowedCommands = [CommandTypes.Ping] }).Save(rigA);
        var b = RigSettings.Load(rigB, legacy);

        Assert.That(RigSettings.Load(rigA, legacy).AllowedCommands, Is.EqualTo(new[] { CommandTypes.Ping }));
        Assert.That(b.AllowedCommands, Does.Contain(CommandTypes.ParkMount));
    }

    [Test]
    public void Installation_RejectsPlainHttpExceptLoopback() {
        Assert.That(new InstallationSettings { ServiceUrl = "http://example.com" }.Normalized().ServiceUrl, Is.EqualTo(InstallationSettings.DefaultServiceUrl));
        Assert.That(new InstallationSettings { ServiceUrl = "http://localhost:5001/api/" }.Normalized().ServiceUrl, Is.EqualTo("http://localhost:5001/api"));
    }

    [Test]
    public void CorruptRigSettings_AreBackedUpAndReset() {
        var path = Path.Combine(root, "rig", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var settings = RigSettings.Load(path);

        Assert.That(settings.Enabled, Is.True);
        Assert.That(File.Exists(path + ".corrupt"), Is.True);
    }

    [Test]
    public void Everything_Optional_Is_On_By_Default_And_Each_Switch_Names_Its_Data() {
        Assert.That(new RigSettings().DataOff(), Is.Empty);
        var off = new RigSettings {
            SendSubReports = false, UploadThumbnails = false, OpticsAnalysis = false, LiveUpdates = false,
            LiveGuideGraph = false, ShareTargetScheduler = false, AllowPlansFromApp = false,
        }.DataOff();
        Assert.That(off, Is.EquivalentTo(new[] {
            DataFeatures.Frames, DataFeatures.Thumbnails, DataFeatures.Optics, DataFeatures.Live,
            DataFeatures.Guiding, DataFeatures.Scheduler, DataFeatures.Sequence,
        }));
    }

    [Test]
    public void Older_Settings_Files_Keep_Everything_On() {
        var path = Path.Combine(root, "rig.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "uploadThumbnails": true }""");
        Assert.That(RigSettings.Load(path).DataOff(), Is.Empty);
    }
}
