using NightManager.Core.Analysis;

namespace NightManager.Core.Tests;

public class ThumbnailStretchTests {
    /// <summary>A linear sub: sky background around 2% of full scale with noise, a few bright stars.</summary>
    private static ushort[] LinearFrame(int count = 40_000) {
        var random = new Random(7);
        var pixels = new ushort[count];
        for (var i = 0; i < count; i++) {
            pixels[i] = (ushort)Math.Clamp(1300 + random.NextDouble() * 200 - 100, 0, 65535);
        }
        for (var i = 0; i < count; i += 997) {
            pixels[i] = 60000;
        }
        return pixels;
    }

    [Test]
    public void A_linear_frame_is_stretched_to_a_visible_background() {
        var output = ThumbnailStretch.To8Bit(LinearFrame(), channels: 1);
        var sorted = output.OrderBy(b => b).ToArray();
        var median = sorted[sorted.Length / 2];
        Assert.That(median, Is.InRange(35, 75), "background near 20% grey instead of black");
        Assert.That(output.Max(), Is.EqualTo(255), "stars stay bright");
    }

    [Test]
    public void An_already_stretched_frame_is_left_alone() {
        var pixels = Enumerable.Range(0, 1000).Select(i => (ushort)(13000 + i)).ToArray();
        var output = ThumbnailStretch.To8Bit(pixels, channels: 1);
        Assert.That(output[0], Is.EqualTo(13000 >> 8));
    }

    [Test]
    public void Colour_channels_are_stretched_together() {
        var mono = LinearFrame(30_000);
        var rgb = new ushort[mono.Length * 3];
        for (var i = 0; i < mono.Length; i++) {
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = mono[i];
        }
        var output = ThumbnailStretch.To8Bit(rgb, channels: 3);
        Assert.That(output[3], Is.EqualTo(output[4]).And.EqualTo(output[5]), "linked stretch keeps grey grey");
        Assert.That(output.OrderBy(b => b).ElementAt(output.Length / 2), Is.GreaterThan(30));
    }
}
