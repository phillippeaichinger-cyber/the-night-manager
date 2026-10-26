using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NINA.WPF.Base.Interfaces.Mediator;
using System.IO;

namespace NightManager.Plugin.Runtime {
    /// <summary>Translates NINA's image-saved event into host-independent Core types.</summary>
    internal static class FrameMapper {
        public static FrameMetrics ToMetrics(ImageSavedEventArgs e, DateTimeOffset timestamp) {
            var meta = e.MetaData;
            var stars = e.StarDetectionAnalysis;
            var imageType = (meta?.Image?.ImageType ?? "LIGHT").ToUpperInvariant();
            int? starCount = null;
            double? hfr = null;

            if (stars is not null) {
                hfr = Positive(stars.HFR);
                // For light frames NINA always runs star detection; zero stars with no HFR means none found.
                starCount = stars.DetectedStars > 0 || FrameGrader.IsLight(imageType) ? stars.DetectedStars : null;
            }

            return new FrameMetrics {
                Timestamp = timestamp,
                ImageType = imageType,
                Target = Text(meta?.Target?.Name),
                Filter = Text(e.Filter) ?? Text(meta?.FilterWheel?.Filter),
                ExposureSeconds = Positive(meta?.Image?.ExposureTime ?? e.Duration),
                Binning = Text(meta?.Image?.Binning) ?? Text(meta?.Camera?.Binning),
                Hfr = hfr,
                Stars = starCount,
                Median = e.Statistics is null ? null : StateCollector.Finite(e.Statistics.Median),
                GuidingRmsTotal = GuidingArcsec(meta?.Image?.RecordedRMS, r => r.Total)
            };
        }

        public static FrameItem ToItem(
            string id,
            ImageSavedEventArgs e,
            FrameMetrics metrics,
            FrameGrade grade,
            string sessionId,
            string fileName,
            EncodedThumbnail thumbnail) {
            var meta = e.MetaData;
            var rms = meta?.Image?.RecordedRMS;

            return new FrameItem {
                Id = id,
                Timestamp = metrics.Timestamp,
                SessionId = sessionId,
                FileName = fileName,
                ImageType = metrics.ImageType,
                Target = metrics.Target,
                Filter = metrics.Filter,
                ExposureSeconds = metrics.ExposureSeconds,
                Gain = meta?.Camera is { Gain: >= 0 } camera ? camera.Gain : null,
                Offset = meta?.Camera is { Offset: >= 0 } offsetCamera ? offsetCamera.Offset : null,
                Binning = metrics.Binning,
                CameraTemperature = meta?.Camera is null ? null : StateCollector.Finite(meta.Camera.Temperature),
                Hfr = metrics.Hfr,
                HfrStdDev = e.StarDetectionAnalysis is null ? null : Positive(e.StarDetectionAnalysis.HFRStDev),
                Stars = metrics.Stars,
#if NINA33
                // Star FWHM and eccentricity are exposed by NINA 3.3+ (filled when the detector measures them).
                Fwhm = e.StarDetectionAnalysis is null ? null : Positive(e.StarDetectionAnalysis.FWHM),
                Eccentricity = e.StarDetectionAnalysis is null ? null : Positive(e.StarDetectionAnalysis.Eccentricity),
#endif
                Median = metrics.Median,
                Mean = e.Statistics is null ? null : StateCollector.Finite(e.Statistics.Mean),
                StdDev = e.Statistics is null ? null : StateCollector.Finite(e.Statistics.StDev),
                GuidingRmsTotal = metrics.GuidingRmsTotal,
                GuidingRmsRa = GuidingArcsec(rms, r => r.RA),
                GuidingRmsDec = GuidingArcsec(rms, r => r.Dec),
                Altitude = meta?.Telescope is null ? null : StateCollector.Finite(meta.Telescope.Altitude),
                Airmass = meta?.Telescope is null ? null : Positive(meta.Telescope.Airmass),
                RotatorAngle = meta?.Rotator is null ? null : StateCollector.Finite(meta.Rotator.Position),
                FocuserPosition = meta?.Focuser?.Position,
                FocuserTemperature = meta?.Focuser is null ? null : StateCollector.Finite(meta.Focuser.Temperature),
                Grade = grade,
                Thumbnail = thumbnail is null ? null : new ThumbnailInfo { Width = thumbnail.Width, Height = thumbnail.Height, Bytes = thumbnail.Bytes.Length }
            };
        }

        public static string FileName(Uri path) {
            if (path is null) {
                return "image";
            }

            try {
                return Path.GetFileName(path.IsFile ? path.LocalPath : path.ToString());
            } catch (ArgumentException) {
                return "image";
            }
        }

        public static string LocalPath(Uri path) => path is { IsFile: true } ? path.LocalPath : null;

        private static double? GuidingArcsec(NINA.Core.Model.RMS rms, Func<NINA.Core.Model.RMS, double> select) {
            if (rms is null || rms.DataPoints == 0) {
                return null;
            }

            var value = select(rms) * rms.Scale;
            return double.IsFinite(value) && value > 0 ? Math.Round(value, 3) : null;
        }

        private static double? Positive(double value) => double.IsFinite(value) && value > 0 ? Math.Round(value, 4) : null;

        private static string Text(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
