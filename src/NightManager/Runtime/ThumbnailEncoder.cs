using NightManager.Core.Analysis;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NightManager.Plugin.Runtime {
    internal sealed record EncodedThumbnail(byte[] Bytes, int Width, int Height);

    /// <summary>
    /// Builds the phone thumbnail from NINA's own rendered (stretched, debayered) preview.
    /// That bitmap exists for every format NINA can save, FITS, XISF, TIFF or RAW alike, so no
    /// image decoder or native library is needed here. It is downscaled first (600 px on the long side by
    /// default), stretched if NINA handed over a linear image, and kept to tens of kilobytes.
    /// </summary>
    internal static class ThumbnailEncoder {
        /// <summary>Above this the JPEG is re-encoded at lower quality: thumbnails travel over observatory links.</summary>
        public const int MaxBytes = 60 * 1024;

        /// <summary>Never above this: the server's upload links refuse anything over 256 KB.</summary>
        public const int HardMaxBytes = 200 * 1024;

        public static EncodedThumbnail Encode(BitmapSource source, int maxDimension, int quality) {
            if (source is null || source.PixelWidth == 0 || source.PixelHeight == 0) {
                return null;
            }

            BitmapSource image = source;
            var scale = Math.Min(1.0, (double)maxDimension / Math.Max(source.PixelWidth, source.PixelHeight));

            if (scale < 1.0) {
                image = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            }

            var display = ToDisplay(image);
            var bytes = Jpeg(display, quality);
            for (var q = quality - 15; bytes.Length > MaxBytes && q >= 45; q -= 15) {
                bytes = Jpeg(display, q);
            }
            // Still too big (a very noisy frame): shrink until it fits the server's upload limit (256 KB) with room
            // to spare. The sensor size never matters: every frame is scaled to at most 600 px first.
            while (bytes.Length > HardMaxBytes && display.PixelWidth > 200) {
                display = new TransformedBitmap(display, new ScaleTransform(0.75, 0.75));
                bytes = Jpeg(display, 45);
            }
            return new EncodedThumbnail(bytes, display.PixelWidth, display.PixelHeight);
        }

        /// <summary>
        /// The local link's preview (no size budget: it never leaves the home network): scaled so the long edge is at
        /// most <paramref name="maxEdge"/>, stretched like the thumbnail.
        /// </summary>
        public static byte[] EncodePreview(BitmapSource source, int maxEdge, int quality) {
            if (source is null || source.PixelWidth == 0 || source.PixelHeight == 0) return null;
            var (w, _) = NightManager.Core.LocalLink.PreviewPolicy.PreviewSize(source.PixelWidth, source.PixelHeight, maxEdge);
            var scale = (double)w / source.PixelWidth;
            BitmapSource image = scale < 1.0 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            return Jpeg(ToDisplay(image), quality);
        }

        /// <summary>A native-resolution crop for zooming in (the local link only).</summary>
        public static byte[] EncodeCrop(BitmapSource source, NightManager.Core.LocalLink.PixelRect rect, int quality) {
            if (source is null) return null;
            var crop = new CroppedBitmap(source, new System.Windows.Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
            return Jpeg(ToDisplay(crop), quality);
        }

        /// <summary>8-bit gray or BGR for JPEG, with a screen stretch applied when the image is still linear.</summary>
        private static BitmapSource ToDisplay(BitmapSource image) {
            var mono = image.Format == PixelFormats.Gray8 || image.Format == PixelFormats.Gray16 || image.Format == PixelFormats.Gray32Float
                || image.Format.Masks.Count == 1;
            var wide = new FormatConvertedBitmap(image, mono ? PixelFormats.Gray16 : PixelFormats.Rgb48, null, 0);
            var channels = mono ? 1 : 3;
            var width = wide.PixelWidth;
            var height = wide.PixelHeight;
            var pixels = new ushort[width * height * channels];
            wide.CopyPixels(pixels, width * channels * 2, 0);

            var display = ThumbnailStretch.To8Bit(pixels, channels);
            if (!mono) {
                // Rgb48 → Bgr24 (what the JPEG encoder takes).
                for (var i = 0; i < display.Length; i += 3) {
                    (display[i], display[i + 2]) = (display[i + 2], display[i]);
                }
            }

            var result = BitmapSource.Create(width, height, 96, 96, mono ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, display, width * channels);
            result.Freeze();
            return result;
        }

        private static byte[] Jpeg(BitmapSource image, int quality) {
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
