namespace NightManager.Core.Exposure;

/// <summary>
/// The random noise in a frame's background, in the camera's own units (ADU). Measured from the difference between
/// neighbouring pixels of the same colour, so smooth light (nebulosity, gradients, vignetting) cancels out and only
/// pixel-to-pixel noise is left; a median of many small patches keeps stars out of it. Fast: a few hundred patches.
/// </summary>
public static class BackgroundNoise {
    private const int Patch = 48;
    private const int PatchesAcross = 16;
    private const int PatchesDown = 12;

    /// <summary>Noise (1σ, ADU) and the median level (ADU), or null for a frame too small to measure.</summary>
    public static (double Noise, double Median)? Measure(ushort[] pixels, int width, int height, bool bayered) {
        // Colour sensors: one colour of the Bayer pattern (every other pixel both ways), so the pattern isn't "noise".
        var step = bayered ? 2 : 1;
        var planeWidth = width / step;
        var planeHeight = height / step;
        if (planeWidth < Patch * 2 || planeHeight < Patch * 2 || pixels.Length < width * height) {
            return null;
        }

        var noises = new List<double>(PatchesAcross * PatchesDown);
        var medians = new List<double>(PatchesAcross * PatchesDown);
        var diffs = new double[Patch * (Patch - 1)];
        var values = new double[Patch * Patch];
        for (var py = 0; py < PatchesDown; py++) {
            for (var px = 0; px < PatchesAcross; px++) {
                var x0 = (int)((planeWidth - Patch) * (px + 0.5) / PatchesAcross);
                var y0 = (int)((planeHeight - Patch) * (py + 0.5) / PatchesDown);
                int d = 0, v = 0;
                for (var y = 0; y < Patch; y++) {
                    var row = (y0 + y) * step * width;
                    for (var x = 0; x < Patch; x++) {
                        var value = pixels[row + (x0 + x) * step];
                        values[v++] = value;
                        if (x > 0) {
                            diffs[d++] = value - pixels[row + (x0 + x - 1) * step];
                        }
                    }
                }
                var median = Median(values, v);
                // Saturated patches (a bright star filling it) say nothing about the background.
                if (median >= 65000) {
                    continue;
                }
                medians.Add(median);
                // MAD of the differences: σ_diff = 1.4826·MAD, and a difference of two pixels has √2 × the noise.
                var mad = MedianAbs(diffs, d);
                noises.Add(1.4826 * mad / Math.Sqrt(2));
            }
        }
        if (noises.Count < 10) {
            return null;
        }
        return (Median(noises.ToArray(), noises.Count), Median(medians.ToArray(), medians.Count));
    }

    private static double Median(double[] values, int count) {
        Array.Sort(values, 0, count);
        return count % 2 == 1 ? values[count / 2] : (values[count / 2 - 1] + values[count / 2]) / 2;
    }

    private static double MedianAbs(double[] values, int count) {
        var centre = Median(values, count);
        for (var i = 0; i < count; i++) {
            values[i] = Math.Abs(values[i] - centre);
        }
        return Median(values, count);
    }
}
