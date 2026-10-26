using NightManager.Core.Analysis.Optics;
using NUnit.Framework;

namespace NightManager.Core.Tests;

public class FocusSurfaceTests {
    /// <summary>
    /// A simulated autofocus run: 9 positions 50 steps apart around 10,000; each cell's HFR follows a hyperbola
    /// around its own best focus, given by <paramref name="bestOffset"/> from its normalised position.
    /// </summary>
    private static FocusSurface Run(Func<double, double, double> bestOffset, double aspect = 1, Func<double, double, double>? sharpest = null, bool junk = false) {
        var surface = new FocusSurface();
        for (var i = -4; i <= 4; i++) {
            var position = 10_000 + i * 50;
            var cells = new double?[FocusSurface.Grid * FocusSurface.Grid];
            for (var c = 0; c < cells.Length; c++) {
                double x = 2.0 * (c % 5 + 0.5) / 5 - 1, y = 2.0 * (c / 5 + 0.5) / 5 - 1;
                var best = 10_000 + bestOffset(x, y);
                cells[c] = 1.6 * (sharpest?.Invoke(x, y) ?? 1) * Math.Sqrt(1 + Math.Pow((position - best) / 60.0, 2));
                // Far from focus, a few cells only find hot pixels: tiny "stars".
                if (junk && Math.Abs(i) >= 3 && c % 4 == 0) cells[c] = 0.4;
            }
            surface.Add(position, cells, aspect);
        }
        return surface;
    }

    [Test]
    public void A_Square_Sensor_Has_No_Tilt() {
        var r = Run((_, _) => 0).Compute(50, 5, null)!;
        Assert.That(r.TiltSteps, Is.LessThan(2));
        Assert.That(Math.Abs(r.CurvatureSteps), Is.LessThan(2));
    }

    [Test]
    public void Measures_Tilt_In_Steps_And_Microns() {
        // The right edge focuses 40 steps further out than the left: 40 steps edge to edge.
        var r = Run((x, _) => 20 * x).Compute(50, 5, 2.5)!;
        Assert.That(r.TiltSteps, Is.EqualTo(40).Within(2));
        Assert.That(r.TiltAngle, Is.LessThan(10).Or.GreaterThan(350));
        Assert.That(r.TiltMicrons, Is.EqualTo(r.TiltSteps * 2.5).Within(0.5));
        Assert.That(r.TopRight, Is.GreaterThan(r.TopLeft));
        Assert.That(r.CriticalFocusMicrons, Is.EqualTo(67.1).Within(0.5)); // f/5
    }

    [Test]
    public void Measures_Field_Curvature() {
        var r = Run((x, y) => 15 * (x * x + y * y)).Compute(50, 5, null)!;
        Assert.That(r.CurvatureSteps, Is.EqualTo(30).Within(4));
        Assert.That(r.TiltSteps, Is.LessThan(3));
    }

    [Test]
    public void A_Diagonal_Tilt_On_A_3_2_Sensor_Keeps_Its_Angle_And_Corner_Spread() {
        // 45° on the sensor itself: focus moves 10 steps per tenth of the width, along x and y alike.
        // The cells span x -1…1 and y -1…1 of a frame 1.5 times as wide as it is tall.
        var r = Run((x, y) => 20 * x + 20 * y / 1.5, aspect: 1.5).Compute(50, 5, null)!;
        Assert.That(r.TiltAngle, Is.EqualTo(45).Within(3));
        Assert.That(r.TiltSteps, Is.EqualTo(20 * 2 + 20 / 1.5 * 2).Within(2)); // top-left corner to bottom-right
    }

    [Test]
    public void Cells_That_Only_Found_Hot_Pixels_Do_Not_Bend_The_Surface() {
        var r = Run((x, _) => 20 * x, junk: true).Compute(50, 5, null)!;
        Assert.That(r.TiltSteps, Is.EqualTo(40).Within(3));
        Assert.That(r.TiltUncertaintySteps, Is.Not.Null.And.LessThan(5));
    }

    [Test]
    public void Softness_Focus_Cannot_Fix_Is_Kept_Apart_From_Tilt() {
        // The right side never gets as sharp (collimation, a decentred lens), but every cell focuses at the same place.
        var r = Run((_, _) => 0, sharpest: (x, _) => 1 + 0.3 * (x + 1) / 2).Compute(50, 5, null)!;
        Assert.That(r.TiltSteps, Is.LessThan(3));
        Assert.That(r.UnfocusableSoftnessPercent, Is.GreaterThan(20));
        Assert.That(r.UnfocusableSoftnessAngle, Is.LessThan(15).Or.GreaterThan(345));
    }

    [Test]
    public void Needs_A_Real_Run() {
        var surface = new FocusSurface();
        surface.Add(10_000, new double?[25]);
        Assert.That(surface.Compute(50, 5, null), Is.Null);
    }
}
