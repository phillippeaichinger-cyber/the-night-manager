namespace NightManager.Core.Cameras;

/// <summary>
/// Turns a pier camera's raw 8-bit frame (grey, or BGR from the driver's colour conversion) into a small picture:
/// lone hot pixels removed, shrunk to the width wanted, and stretched so the kit can be seen. Also reports how bright
/// the frame was, for the next exposure. Pure arithmetic: the host encodes the result as a JPEG.
/// </summary>
public static class PierFrame {
    public sealed record Result(byte[] Pixels, int Width, int Height, int Channels, double Median, double Saturated);

    public static Result Process(byte[] raw, int width, int height, int channels, int maxWidth) {
        if (channels is not (1 or 3) || raw.Length < width * height * channels) {
            throw new ArgumentException("Frame size doesn't match its dimensions");
        }
        // Brightness of the frame as taken (before any stretch), for the exposure.
        var (median, saturated) = Brightness(raw, width, height, channels);
        var clean = RemoveHotPixels(raw, width, height, channels);
        var factor = Math.Max(1, (int)Math.Ceiling((double)width / Math.Max(64, maxWidth)));
        var (small, w, h) = Shrink(clean, width, height, channels, factor);
        Stretch(small, channels);
        return new Result(small, w, h, channels, median, saturated);
    }

    /// <summary>Median luminance (0–255) and the share of burnt-out pixels, from a sparse sample.</summary>
    public static (double Median, double Saturated) Brightness(byte[] raw, int width, int height, int channels) {
        var histogram = new int[256];
        var step = Math.Max(1, (int)Math.Sqrt((double)width * height / 40_000));
        long count = 0, burnt = 0;
        for (var y = 0; y < height; y += step) {
            for (var x = 0; x < width; x += step) {
                var v = Luma(raw, (y * width + x) * channels, channels);
                histogram[v]++;
                count++;
                if (v >= 250) burnt++;
            }
        }
        long seen = 0;
        var median = 0;
        for (var i = 0; i < 256; i++) {
            seen += histogram[i];
            if (seen * 2 >= count) { median = i; break; }
        }
        return (median, count == 0 ? 0 : (double)burnt / count);
    }

    private static int Luma(byte[] p, int i, int channels) =>
        channels == 1 ? p[i] : (p[i] * 29 + p[i + 1] * 150 + p[i + 2] * 77) >> 8; // B, G, R

    /// <summary>A pixel far brighter than all eight neighbours (an uncooled sensor's hot pixel) takes their brightest value.</summary>
    public static byte[] RemoveHotPixels(byte[] raw, int width, int height, int channels) {
        var output = (byte[])raw.Clone();
        for (var y = 1; y < height - 1; y++) {
            for (var x = 1; x < width - 1; x++) {
                for (var c = 0; c < channels; c++) {
                    var i = (y * width + x) * channels + c;
                    var max = 0;
                    for (var dy = -1; dy <= 1; dy++) {
                        for (var dx = -1; dx <= 1; dx++) {
                            if (dx == 0 && dy == 0) continue;
                            max = Math.Max(max, raw[((y + dy) * width + x + dx) * channels + c]);
                        }
                    }
                    if (raw[i] > max + 40) output[i] = (byte)max;
                }
            }
        }
        return output;
    }

    /// <summary>Average of each factor×factor block.</summary>
    public static (byte[] Pixels, int Width, int Height) Shrink(byte[] raw, int width, int height, int channels, int factor) {
        if (factor <= 1) return (raw, width, height);
        int w = width / factor, h = height / factor;
        var output = new byte[w * h * channels];
        var area = factor * factor;
        for (var y = 0; y < h; y++) {
            for (var x = 0; x < w; x++) {
                for (var c = 0; c < channels; c++) {
                    var sum = 0;
                    for (var dy = 0; dy < factor; dy++) {
                        var row = ((y * factor + dy) * width + x * factor) * channels + c;
                        for (var dx = 0; dx < factor; dx++) sum += raw[row + dx * channels];
                    }
                    output[(y * w + x) * channels + c] = (byte)(sum / area);
                }
            }
        }
        return (output, w, h);
    }

    /// <summary>
    /// Levels from the darkest 0.5% to the brightest 0.5%, in place. A flat, dark frame isn't stretched more than
    /// 4× (that only shows noise); colours are stretched together, so they don't shift.
    /// </summary>
    public static void Stretch(byte[] pixels, int channels) {
        var histogram = new int[256];
        for (var i = 0; i < pixels.Length; i += channels) histogram[Luma(pixels, i, channels)]++;
        var total = pixels.Length / channels;
        int Percentile(double p) {
            long seen = 0;
            for (var i = 0; i < 256; i++) {
                seen += histogram[i];
                if (seen >= p * total) return i;
            }
            return 255;
        }
        var black = Percentile(0.005);
        var white = Math.Max(Percentile(0.995), black + 64);
        var lut = new byte[256];
        for (var v = 0; v < 256; v++) lut[v] = (byte)Math.Clamp((v - black) * 255 / (white - black), 0, 255);
        for (var i = 0; i < pixels.Length; i++) pixels[i] = lut[pixels[i]];
    }
}
