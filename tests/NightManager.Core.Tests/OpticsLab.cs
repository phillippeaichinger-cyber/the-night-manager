using System.Text;
using NightManager.Core.Analysis.Optics;
using NUnit.Framework;

namespace NightManager.Core.Tests;

/// <summary>
/// Research harness, not a test: runs the optics analysis on real subs in $OPTICS_LAB_IN (every .fits below it) and
/// writes one line per sub plus each sub's star list (CSV) to $OPTICS_LAB_OUT.
/// </summary>
[Explicit]
public class OpticsLab {
    internal static (ushort[] Pixels, int W, int H, Dictionary<string, string> Header) ReadFits(string path) {
        var bytes = File.ReadAllBytes(path);
        var header = new Dictionary<string, string>();
        var offset = 0;
        for (var done = false; !done; offset += 2880) {
            for (var i = 0; i < 36; i++) {
                var card = Encoding.ASCII.GetString(bytes, offset + i * 80, 80);
                if (card.StartsWith("END")) { done = true; break; }
                if (card[8] == '=') header[card[..8].Trim()] = card[10..].Split('/')[0].Trim().Trim('\'').Trim();
            }
        }
        int w = int.Parse(header["NAXIS1"]), h = int.Parse(header["NAXIS2"]);
        var bz = header.TryGetValue("BZERO", out var z) ? double.Parse(z) : 0;
        var px = new ushort[w * h];
        for (var i = 0; i < px.Length; i++) {
            var raw = (short)((bytes[offset + 2 * i] << 8) | bytes[offset + 2 * i + 1]);
            px[i] = (ushort)Math.Clamp(raw + bz, 0, 65535);
        }
        return (px, w, h, header);
    }

    [Test]
    public void Run() {
        var input = Environment.GetEnvironmentVariable("OPTICS_LAB_IN")!;
        var output = Environment.GetEnvironmentVariable("OPTICS_LAB_OUT")!;
        Directory.CreateDirectory(output);
        var lines = new List<string> { "file,w,h,stars,hfr,tilt,angle,curv,sx,sy,radial,edge_ecc,centre_ecc,tilt_err,glow_stars" };
        foreach (var file in Directory.EnumerateFiles(input, "*.fits", SearchOption.AllDirectories).OrderBy(f => f)) {
            var (px, w, h, _) = ReadFits(file);
            var id = Path.GetRelativePath(input, file).Replace('/', '|');
            if (OpticsAnalyzer.Stars(px, w, h) is not var (stars, mw, mh)) { lines.Add($"\"{id}\",{w},{h},0"); continue; }
            var r = OpticsAnalyzer.Summarize(stars, mw, mh, null);
            lines.Add($"\"{id}\",{mw},{mh},{r.Stars},{r.Hfr},{r.TiltPercent},{r.TiltAngle},{r.CurvaturePercent},{r.SweetSpotX},{r.SweetSpotY},{r.Radial},{r.EdgeEccentricity},{r.CenterEccentricity},{r.TiltUncertaintyPercent},{r.GlowStars}");
            var csv = new StringBuilder("x,y,hfr,flux,ecc,angle,glow,snr\n");
            foreach (var s in stars) csv.Append($"{s.X:F2},{s.Y:F2},{s.Hfr:F3},{s.Flux:F0},{s.Eccentricity:F3},{s.Angle:F3},{s.Glow:F2},{s.Snr:F1}\n");
            File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(file) + ".stars.csv"), csv.ToString());
            TestContext.Out.WriteLine(lines[^1]);
        }
        File.WriteAllLines(Path.Combine(output, "summary.csv"), lines);
    }

    /// <summary>Every autofocus run folder in $OPTICS_LAB_AF: our per-cell HFRs per frame and the focus surface.</summary>
    [Test]
    public void AutofocusRuns() {
        foreach (var run in Directory.EnumerateDirectories(Environment.GetEnvironmentVariable("OPTICS_LAB_AF")!).OrderBy(d => d)) {
            var surface = new FocusSurface();
            TestContext.Out.WriteLine(Path.GetFileName(run));
            foreach (var file in Directory.EnumerateFiles(run, "*.fits", SearchOption.AllDirectories).OrderBy(f => f)) {
                var position = double.Parse(System.Text.RegularExpressions.Regex.Match(file, @"Focuser(\d+)").Groups[1].Value);
                var (px, w, h, _) = ReadFits(file);
                var cells = OpticsAnalyzer.HfrGrid(px, w, h, grid: FocusSurface.Grid);
                var stars = OpticsAnalyzer.Stars(px, w, h);
                TestContext.Out.WriteLine($"  {position} stars {stars?.Stars.Count ?? 0} median HFR {(stars is { } st ? st.Stars.Select(x => x.Hfr).OrderBy(v => v).ElementAt(st.Stars.Count / 2) : 0):F2} cells {(cells == null ? "none" : string.Join(" ", cells.Select(c => c?.ToString("F1") ?? "-")))}");
                if (cells != null) surface.Add(position, cells, (double)w / h);
            }
            var r = surface.Compute(50, 4.0, null);
            TestContext.Out.WriteLine(r == null ? "  no surface" : $"  SURFACE tilt {r.TiltSteps} ± {r.TiltUncertaintySteps} steps @{r.TiltAngle} curvature {r.CurvatureSteps} unfocusable {r.UnfocusableSoftnessPercent}% @{r.UnfocusableSoftnessAngle} corners TL {r.TopLeft} TR {r.TopRight} BL {r.BottomLeft} BR {r.BottomRight} cells {string.Join(" ", r.Cells.Select(c => c?.ToString("F0") ?? "-"))}");
        }
    }
}
