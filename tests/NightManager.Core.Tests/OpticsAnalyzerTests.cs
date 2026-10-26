using NightManager.Core.Analysis.Optics;
using NUnit.Framework;

namespace NightManager.Core.Tests;

public class OpticsAnalyzerTests {
    private const int W = 1200, H = 800;

    /// <summary>
    /// A synthetic sub: a grid of Gaussian stars of varied brightness on a 1,000 ADU sky with noise. <paramref name="sigma"/> gives each
    /// star's size from its normalised position; <paramref name="stretch"/> elongates it along the radius (coma-like).
    /// </summary>
    private static ushort[] Field(Func<double, double, double> sigma, double stretch = 1) {
        var img = new double[W * H];
        var rng = new Random(42);
        for (var i = 0; i < img.Length; i++) img[i] = 1000 + rng.NextDouble() * 20;
        for (var gy = 40; gy < H - 40; gy += 45) {
            for (var gx = 40; gx < W - 40; gx += 45) {
                double nx = 2.0 * gx / (W - 1) - 1, ny = 2.0 * gy / (H - 1) - 1;
                var s = sigma(nx, ny);
                var phi = Math.Atan2(ny, nx);
                double sa = s * stretch, sb = s; // long axis along the radius
                // A star's light doesn't depend on its size (a blurred star is fainter at its peak, not brighter
                // overall); brightnesses vary from star to star.
                var amplitude = 160_000 * Math.Exp(rng.NextDouble() * 1.5) / (2 * Math.PI * sa * sb);
                for (var y = gy - 12; y <= gy + 12; y++) {
                    for (var x = gx - 12; x <= gx + 12; x++) {
                        double dx = x - gx, dy = y - gy;
                        double u = dx * Math.Cos(phi) + dy * Math.Sin(phi), v = -dx * Math.Sin(phi) + dy * Math.Cos(phi);
                        img[y * W + x] += amplitude * Math.Exp(-(u * u / (2 * sa * sa) + v * v / (2 * sb * sb)));
                    }
                }
            }
        }
        return img.Select(v => (ushort)Math.Min(65535, v)).ToArray();
    }

    [Test]
    public void A_Good_Flat_Field_Shows_No_Tilt_Or_Curvature() {
        var r = OpticsAnalyzer.Analyze(Field((_, _) => 1.6), W, H)!;
        Assert.That(r.Stars, Is.GreaterThanOrEqualTo(150)); // the brighter half of the clean stars
        Assert.That(r.TiltPercent, Is.LessThan(5));
        Assert.That(Math.Abs(r.CurvaturePercent), Is.LessThan(5));
        Assert.That(r.Regions, Has.Count.EqualTo(9));
        Assert.That(r.Regions.All(g => g.Stars > 10));
    }

    [Test]
    public void Finds_Tilt_And_Its_Direction() {
        // Stars 60% bigger on the right than on the left.
        var r = OpticsAnalyzer.Analyze(Field((x, _) => 1.5 * (1 + 0.3 * (x + 1) / 2 * 2)), W, H)!;
        Assert.That(r.TiltPercent, Is.GreaterThan(25));
        Assert.That(r.TiltAngle, Is.LessThan(20).Or.GreaterThan(340)); // towards the right
        Assert.That(r.Regions[5].Hfr, Is.GreaterThan(r.Regions[3].Hfr)); // right middle > left middle
    }

    [Test]
    public void Finds_Field_Curvature_With_The_Best_Point_In_The_Middle() {
        var r = OpticsAnalyzer.Analyze(Field((x, y) => 1.4 * (1 + 0.25 * (x * x + y * y))), W, H)!;
        Assert.That(r.CurvaturePercent, Is.GreaterThan(20));
        Assert.That(Math.Abs(r.SweetSpotX!.Value), Is.LessThan(0.2));
        Assert.That(Math.Abs(r.SweetSpotY!.Value), Is.LessThan(0.2));
    }

    [Test]
    public void An_Off_Centre_Sweet_Spot_Points_To_Collimation() {
        var r = OpticsAnalyzer.Analyze(Field((x, y) => 1.4 * (1 + 0.3 * ((x - 0.5) * (x - 0.5) + y * y))), W, H)!;
        Assert.That(r.SweetSpotX!.Value, Is.EqualTo(0.5).Within(0.2));
        Assert.That(Math.Abs(r.SweetSpotY!.Value), Is.LessThan(0.2));
    }

    [Test]
    public void Radially_Stretched_Edge_Stars_Read_As_Coma() {
        var r = OpticsAnalyzer.Analyze(Field((x, y) => 1.5, stretch: 1.6), W, H)!;
        Assert.That(r.Radial, Is.GreaterThan(0.6));
        Assert.That(r.EdgeEccentricity, Is.GreaterThan(0.5));
    }

    /// <summary>A star's size at a focus error <paramref name="d"/> (in units of the in-focus size): blur adds in quadrature.</summary>
    private static double Blur(double d) => 1.6 * Math.Sqrt(1 + d * d);

    [TestCase(0.8)]
    [TestCase(1.0)]
    [TestCase(2.0)]
    [TestCase(4.0)]
    public void Measures_Star_Size_Accurately_From_Undersampled_To_Bloated(double sigma) {
        // Half-flux radius of a Gaussian spread over square pixels: 1.1774 × √(σ² + 1/12).
        var expected = 1.1774 * Math.Sqrt(sigma * sigma + 1.0 / 12);
        var r = OpticsAnalyzer.Analyze(Field((_, _) => sigma), W, H)!;
        Assert.That(r.Hfr, Is.EqualTo(expected).Within(expected * 0.1)); // was 20% small for tight stars, 17% for bloated
    }

    [Test]
    public void A_Tilted_Sensor_Focused_In_The_Middle_Fakes_Neither_A_Gradient_Nor_Curvature() {
        // After autofocus the middle is sharp and both the left and right edges are soft. One frame can't show which
        // way the sensor leans (that takes an autofocus run); it mustn't be mistaken for curvature or a one-sided gradient.
        var r = OpticsAnalyzer.Analyze(Field((x, _) => Blur(x)), W, H)!;
        TestContext.Out.WriteLine($"tilt {r.TiltPercent} ± {r.TiltUncertaintyPercent} @{r.TiltAngle} curvature {r.CurvaturePercent}");
        Assert.That(r.TiltPercent, Is.LessThan(8));
        Assert.That(Math.Abs(r.CurvaturePercent), Is.LessThan(8));
    }

    [Test]
    public void A_Diagonal_Gradient_Keeps_Its_Angle_On_A_Wide_Sensor() {
        // Softer towards the bottom right, at 45° on the sensor itself, which is 3:2 here.
        var r = OpticsAnalyzer.Analyze(Field((x, y) => Blur(0.5 * (x * W + y * H) / W + 0.9)), W, H)!;
        Assert.That(r.TiltAngle, Is.EqualTo(45).Within(8));
        Assert.That(r.TiltPercent, Is.GreaterThan(3 * r.TiltUncertaintyPercent!.Value));
    }

    [Test]
    public void Round_Field_Curvature_Reads_As_Curvature() {
        double Bowl(double x, double y) => (x * x + y * y * 4 / 9) / (1 + 4.0 / 9);
        var r = OpticsAnalyzer.Analyze(Field((x, y) => Blur(Bowl(x, y))), W, H)!;
        Assert.That(r.CurvaturePercent, Is.GreaterThan(10)); // a bowl through the cells, which stop short of the corners
        Assert.That(r.TiltPercent, Is.LessThan(5));
    }

    [Test]
    public void A_Galaxy_In_One_Corner_Does_Not_Read_As_Tilt() {
        // A bright glow with knots (clusters, HII regions: bigger than stars) over the top-left of a sharp, even field.
        var px = Field((_, _) => 1.6);
        var img = px.Select(v => (double)v).ToArray();
        var rng = new Random(3);
        for (var y = 0; y < H; y++) {
            for (var x = 0; x < W; x++) {
                var d = Math.Sqrt(Math.Pow((x - 150.0) / 380, 2) + Math.Pow((y - 120.0) / 260, 2));
                img[y * W + x] += 2500 * Math.Exp(-2.5 * d);
            }
        }
        for (var k = 0; k < 160; k++) {
            double kx = 60 + rng.NextDouble() * 480, ky = 40 + rng.NextDouble() * 320, ks = 3 + rng.NextDouble() * 2;
            for (var y = (int)ky - 14; y <= (int)ky + 14; y++) {
                for (var x = (int)kx - 14; x <= (int)kx + 14; x++) {
                    img[y * W + x] += 1500 * Math.Exp(-((x - kx) * (x - kx) + (y - ky) * (y - ky)) / (2 * ks * ks));
                }
            }
        }
        var r = OpticsAnalyzer.Analyze(img.Select(v => (ushort)Math.Min(65535, v)).ToArray(), W, H)!;
        Assert.That(r.GlowStars, Is.GreaterThan(20));
        Assert.That(r.TiltPercent, Is.LessThan(6));
    }

    [Test]
    public void Fainter_Stars_On_One_Side_Do_Not_Read_As_Tilt() {
        // Vignetting or a star-poor side: the same stars, ten times fainter on the right than on the left.
        var img = new double[W * H];
        var rng = new Random(5);
        for (var i = 0; i < img.Length; i++) img[i] = 1000 + rng.NextDouble() * 40;
        for (var gy = 40; gy < H - 40; gy += 30) {
            for (var gx = 40; gx < W - 40; gx += 30) {
                var amplitude = 6000 * Math.Pow(0.1, (double)gx / W) * (0.5 + rng.NextDouble());
                for (var y = gy - 12; y <= gy + 12; y++) {
                    for (var x = gx - 12; x <= gx + 12; x++) {
                        img[y * W + x] += amplitude * Math.Exp(-((x - gx) * (x - gx) + (y - gy) * (y - gy)) / (2 * 1.6 * 1.6));
                    }
                }
            }
        }
        var r = OpticsAnalyzer.Analyze(img.Select(v => (ushort)Math.Min(65535, v)).ToArray(), W, H)!;
        Assert.That(r.TiltPercent, Is.LessThan(Math.Max(5, 2 * r.TiltUncertaintyPercent!.Value)));
    }

    [Test]
    public void Gives_Up_On_An_Empty_Frame() {
        var sky = Enumerable.Repeat((ushort)1000, W * H).ToArray();
        Assert.That(OpticsAnalyzer.Analyze(sky, W, H), Is.Null);
    }
}
