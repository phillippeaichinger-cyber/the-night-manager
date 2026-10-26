using NightManager.Core.Settings;
using System.Collections.Concurrent;
using NightManager.Core.Analysis.Optics;
using NINA.WPF.Base.Interfaces.Mediator;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Optics health from every light frame: the raw pixels arrive before the file is written, so the stars are
    /// measured on a background thread while NINA saves, and the result rides along with that frame's report.
    /// </summary>
    public sealed partial class NightManagerService {
        /// <summary>Analyses in flight, by exposure start (the same image's saved event looks it up).</summary>
        private readonly ConcurrentDictionary<long, Task<OpticsReport>> opticsInFlight = new();

        private void StartOpticsAnalysis(BeforeImageSavedEventArgs e) {
            try {
                var image = e.Image;
                var meta = image?.MetaData;
                // Optics ride on the sub reports: without those there's nowhere to send them.
                if (!DataOn(DataFeatures.Optics) || !DataOn(DataFeatures.Frames) || image is null || meta is null || !string.Equals(meta.Image.ImageType, "LIGHT", StringComparison.OrdinalIgnoreCase)) {
                    return;
                }
                var key = meta.Image.ExposureStart.ToUniversalTime().Ticks;
                var pixels = image.Data.FlatArray;
                var props = image.Properties;
                var bin = Math.Max(1, meta.Camera.BinX);
                double? pixelSize = meta.Camera.PixelSize > 0 ? meta.Camera.PixelSize * bin : null;
                double? focalLength = meta.Telescope.FocalLength > 0 ? meta.Telescope.FocalLength : null;
                foreach (var old in opticsInFlight.Keys.Where(k => k < key - TimeSpan.FromHours(1).Ticks).ToList()) {
                    opticsInFlight.TryRemove(old, out _);
                }
                // One at a time: with short subs or a slow PC, skip frames rather than queue analyses (each holds a frame).
                if (opticsInFlight.Values.Any(t => !t.IsCompleted)) {
                    return;
                }
                double? rotator = double.IsFinite(meta.Rotator.MechanicalPosition) ? Math.Round(meta.Rotator.MechanicalPosition, 1) : null;
                var pier = meta.Telescope.SideOfPier switch {
                    NINA.Core.Enum.PierSide.pierEast => "East",
                    NINA.Core.Enum.PierSide.pierWest => "West",
                    _ => null,
                };
                double? altitude = double.IsFinite(meta.Telescope.Altitude) ? Math.Round(meta.Telescope.Altitude, 1) : null;
                opticsInFlight[key] = Task.Run(() => OpticsAnalyzer.Analyze(pixels, props.Width, props.Height, props.BitDepth, props.IsBayered, pixelSize, focalLength) is { } report
                    ? report with { RotatorAngle = rotator, PierSide = pier, Altitude = altitude }
                    : null);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Optics analysis not started: {ex.Message}");
            }
        }

        /// <summary>This frame's optics, waiting a few seconds at most (it normally finishes before the file is written).</summary>
        private OpticsReport TakeOptics(ImageSavedEventArgs e) {
            if (e.MetaData is null || !opticsInFlight.TryRemove(e.MetaData.Image.ExposureStart.ToUniversalTime().Ticks, out var task)) {
                return null;
            }
            try {
                return task.Wait(TimeSpan.FromSeconds(10)) ? task.Result : null;
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Optics analysis failed: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }
    }
}
