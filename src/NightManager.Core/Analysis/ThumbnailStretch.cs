namespace NightManager.Core.Analysis;

/// <summary>
/// Safety net for phone thumbnails. NINA stretches light frames before plugins see them, but snapshots and some
/// third-party exposure items follow the user's own auto-stretch setting and can arrive linear (almost black).
/// A linear image gets the same screen stretch NINA uses (PixInsight-style midtones transfer, linked channels).
/// </summary>
public static class ThumbnailStretch {
    /// <summary>Below this median brightness (0–1) the image is treated as unstretched.</summary>
    public const double LinearMedian = 0.08;

    /// <summary>
    /// 16-bit interleaved pixels (1 or 3 channels) → 8-bit pixels, stretched when the image looks linear, or
    /// scaled straight down to 8 bits when it is already stretched.
    /// </summary>
    public static byte[] To8Bit(ushort[] pixels, int channels, double targetBackground = 0.2, double shadowsClipping = -2.8) {
        ArgumentNullException.ThrowIfNull(pixels);
        var output = new byte[pixels.Length];
        if (pixels.Length == 0) {
            return output;
        }

        var (median, mad) = MedianAndMad(pixels, channels);
        if (median >= LinearMedian) {
            for (var i = 0; i < pixels.Length; i++) {
                output[i] = (byte)(pixels[i] >> 8);
            }
            return output;
        }

        var c0 = Math.Clamp(median + shadowsClipping * 1.4826 * mad, 0, 1);
        var x = c0 < 1 ? (median - c0) / (1 - c0) : 0;
        var m = x <= 0 ? 0.5 : Mtf(targetBackground, x);

        // 65 536-entry lookup: one MTF evaluation per possible value instead of per pixel.
        var lut = new byte[65536];
        for (var v = 0; v < lut.Length; v++) {
            var normalized = v / 65535.0;
            var clipped = normalized <= c0 ? 0 : (normalized - c0) / (1 - c0);
            lut[v] = (byte)Math.Round(Math.Clamp(Mtf(m, clipped), 0, 1) * 255);
        }
        for (var i = 0; i < pixels.Length; i++) {
            output[i] = lut[pixels[i]];
        }
        return output;
    }

    /// <summary>Midtones transfer function.</summary>
    public static double Mtf(double m, double x) {
        if (x <= 0) {
            return 0;
        }
        if (x >= 1) {
            return 1;
        }
        if (m == x) {
            return 0.5;
        }
        return (m - 1) * x / ((2 * m - 1) * x - m);
    }

    /// <summary>Median and median absolute deviation (0–1) of the luminance, from a sample of pixels.</summary>
    public static (double Median, double Mad) MedianAndMad(ushort[] pixels, int channels) {
        channels = Math.Max(1, channels);
        var count = pixels.Length / channels;
        var step = Math.Max(1, count / 50_000);
        var sample = new List<double>(count / step + 1);
        for (var p = 0; p < count; p += step) {
            double sum = 0;
            for (var c = 0; c < channels; c++) {
                sum += pixels[p * channels + c];
            }
            sample.Add(sum / channels / 65535.0);
        }
        sample.Sort();
        var median = sample[sample.Count / 2];
        for (var i = 0; i < sample.Count; i++) {
            sample[i] = Math.Abs(sample[i] - median);
        }
        sample.Sort();
        return (median, sample[sample.Count / 2]);
    }
}
