using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NightManager.Core.Analysis.Optics;
using NUnit.Framework;

namespace NightManager.Core.Tests;

/// <summary>Runs the optics analysis on a real 16-bit FITS sub when NM_FITS points at one (skipped otherwise).</summary>
public class OpticsRealFrameTests {
    [Test]
    public void Analyses_A_Real_Sub() {
        var path = Environment.GetEnvironmentVariable("NM_FITS");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) Assert.Ignore("Set NM_FITS to a FITS file to run.");
        var bytes = File.ReadAllBytes(path);
        var header = new Dictionary<string, string>();
        var offset = 0;
        for (var done = false; !done; offset += 2880) {
            for (var i = 0; i < 36; i++) {
                var card = Encoding.ASCII.GetString(bytes, offset + i * 80, 80);
                if (card.StartsWith("END")) { done = true; break; }
                if (card.Length > 10 && card[8] == '=') header[card[..8].Trim()] = card[10..].Split('/')[0].Trim().Trim('\'').Trim();
            }
        }
        int w = int.Parse(header["NAXIS1"]), h = int.Parse(header["NAXIS2"]);
        var bzero = header.TryGetValue("BZERO", out var z) ? double.Parse(z) : 0;
        var pixels = new ushort[w * h];
        for (var i = 0; i < pixels.Length; i++) {
            var raw = (short)((bytes[offset + 2 * i] << 8) | bytes[offset + 2 * i + 1]);
            pixels[i] = (ushort)Math.Clamp(raw + bzero, 0, 65535);
        }
        double? pix = header.TryGetValue("XPIXSZ", out var p) ? double.Parse(p) : null;
        double? fl = header.TryGetValue("FOCALLEN", out var f) ? double.Parse(f) : null;
        var watch = Stopwatch.StartNew();
        var report = OpticsAnalyzer.Analyze(pixels, w, h, 16, false, pix, fl);
        TestContext.Out.WriteLine($"{w}×{h} in {watch.ElapsedMilliseconds} ms");
        TestContext.Out.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Assert.That(report, Is.Not.Null);
    }
}
