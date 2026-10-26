using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using NightManager.Core.Analysis;
using NightManager.Core.Contracts;
using NightManager.Core.Exposure;
using NightManager.Core.Settings;
using NightManager.Core.Sync;
using NINA.WPF.Base.Interfaces.Mediator;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Sub-length advice by Dr Robin Glover's method, measured on this rig: each light frame's background noise, and
    /// the camera's read noise from bias frames, dark flats and short darks NINA takes anyway (same raw units, so the
    /// camera's gain in electrons is never needed). Everything stays on the PC; one small summary goes out a night.
    /// </summary>
    public sealed partial class NightManagerService {
        /// <summary>Darks this short have next to no dark current: their noise is the read noise.</summary>
        private const double ReadNoiseMaxSeconds = 10;

        private readonly ConcurrentDictionary<long, Task<(double Noise, double Median)?>> noiseInFlight = new();
        private readonly object exposureGate = new();
        private ExposureHistory exposureHistory;
        private double? imageScale;

        private string ExposureHistoryPath => Path.Combine(dataDirectory, "exposure-history.json");

        private void StartNoiseMeasurement(BeforeImageSavedEventArgs e) {
            try {
                var image = e.Image;
                var meta = image?.MetaData;
                if (image is null || meta is null || !DataOn(DataFeatures.Frames)) {
                    return;
                }
                var type = meta.Image.ImageType?.ToUpperInvariant() ?? "";
                var calibration = type is "BIAS" or "DARKFLAT" || type == "DARK" && meta.Image.ExposureTime <= ReadNoiseMaxSeconds;
                if (type != "LIGHT" && !calibration) {
                    return;
                }
                var pixels = image.Data.FlatArray;
                var props = image.Properties;
                var bin = Math.Max(1, meta.Camera.BinX);
                var setting = ExposureHistory.SettingKey(meta.Camera.Gain >= 0 ? meta.Camera.Gain : null, meta.Camera.Offset >= 0 ? meta.Camera.Offset : null, bin, meta.Camera.ReadoutModeName);
                if (meta.Camera.PixelSize > 0 && meta.Telescope.FocalLength > 0) {
                    imageScale = 206.265 * meta.Camera.PixelSize * bin / meta.Telescope.FocalLength;
                }
                var key = meta.Image.ExposureStart.ToUniversalTime().Ticks;
                foreach (var old in noiseInFlight.Keys.Where(k => k < key - TimeSpan.FromHours(1).Ticks).ToList()) {
                    noiseInFlight.TryRemove(old, out _);
                }
                var task = Task.Run(() => BackgroundNoise.Measure(pixels, props.Width, props.Height, props.IsBayered));
                if (calibration) {
                    // A calibration frame: remember its noise as this setting's read noise.
                    _ = task.ContinueWith(t => {
                        if (t.Status == TaskStatus.RanToCompletion && t.Result is { } measured) {
                            lock (exposureGate) {
                                History().AddReadNoise(setting, measured.Noise);
                                SaveExposureHistory();
                            }
                            NinaLog.Instance.Debug($"Read noise at {setting}: {measured.Noise:0.0} ADU ({type})");
                        }
                    }, TaskScheduler.Default);
                } else {
                    noiseInFlight[key] = task;
                }
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Noise measurement not started: {ex.Message}");
            }
        }

        private void RecordSub(ImageSavedEventArgs e, FrameMetrics metrics, string verdict) {
            try {
                // Guiding for the mount's yearly record, whatever happens to the noise measurement.
                if (FrameGrader.IsLight(metrics.ImageType) && metrics.ExposureSeconds is double exposure && metrics.GuidingRmsTotal is double guidingRms) {
                    lock (exposureGate) History().AddGuiding(DateTimeOffset.UtcNow, exposure, guidingRms);
                }
                if (e.MetaData is null || !FrameGrader.IsLight(metrics.ImageType) || metrics.ExposureSeconds is not double seconds || seconds <= 0
                    || !noiseInFlight.TryRemove(e.MetaData.Image.ExposureStart.ToUniversalTime().Ticks, out var task)
                    || !task.Wait(TimeSpan.FromSeconds(10)) || task.Result is not { } measured) {
                    return;
                }
                var camera = e.MetaData.Camera;
                var bin = Math.Max(1, camera.BinX);
                var sample = new SubSample {
                    At = DateTimeOffset.UtcNow,
                    Filter = string.IsNullOrWhiteSpace(metrics.Filter) ? "No filter" : metrics.Filter,
                    Seconds = Math.Round(seconds, 2),
                    Setting = ExposureHistory.SettingKey(camera.Gain >= 0 ? camera.Gain : null, camera.Offset >= 0 ? camera.Offset : null, bin, camera.ReadoutModeName),
                    Gain = camera.Gain >= 0 ? camera.Gain : null,
                    NoiseAdu = Math.Round(measured.Noise, 2),
                    GuidingRms = metrics.GuidingRmsTotal,
                    Rejected = verdict == "reject",
                };
                lock (exposureGate) {
                    History().AddSub(sample);
                    SaveExposureHistory();
                }
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Sub not recorded for the sub-length advice: {ex.Message}");
            }
        }

        /// <summary>Works out the advice and sends it (one small item). Returns what was sent, in words.</summary>
        private string SendExposureAdvice() {
            try {
                ExposureAdviceItem advice;
                lock (exposureGate) {
                    if (History().Subs.Count == 0) {
                        return "No light frames measured yet: the advice comes after a night's imaging.";
                    }
                    var guiding = GuidingCapability.Assess(GuidingRecord(), imageScale, DateTimeOffset.UtcNow);
                    advice = ExposureAdvisor.Advise(History(), Settings.FilterBandwidths, imageScale, DateTimeOffset.UtcNow, SyncEngine.NewId, guiding: guiding);
                }
                if (!DataOn(DataFeatures.Frames)) {
                    return "Sub reports are switched off, so the advice isn't sent.";
                }
                Enqueue(advice);
                sync?.Wake();
                var summary = string.Join(", ", advice.Filters.Where(f => !f.Predicted).Select(f => $"{f.Filter}: {f.Status}"));
                NinaLog.Instance.Info($"Sub-length advice sent ({summary})");
                return $"Sub-length advice sent for {advice.Filters.Count} filter{(advice.Filters.Count == 1 ? "" : "s")}.";
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Sub-length advice failed: {ex.Message}");
                return "The advice couldn't be worked out: " + ex.Message;
            }
        }

        /// <summary>The app's filter choices: NINA filter name → bandwidth (nm). Kept with this rig's settings.</summary>
        private string SetFilterBandwidths(RemoteCommand command) {
            var bandwidths = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, value) in command.Args ?? new Dictionary<string, string>()) {
                if (!string.IsNullOrWhiteSpace(name) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nm) && nm > 0 && nm <= 1000) {
                    bandwidths[name.Trim()] = nm;
                }
            }
            ApplySettingsAsync(Settings with { FilterBandwidths = bandwidths }).GetAwaiter().GetResult();
            SendExposureAdvice();
            return $"Saved the bandwidths of {bandwidths.Count} filter{(bandwidths.Count == 1 ? "" : "s")}.";
        }

        /// <summary>
        /// The mount's guiding over the last year: Target Scheduler's records for this profile (so it's there from the first
        /// day), plus every sub seen since that it doesn't cover. Called inside the exposure lock.
        /// </summary>
        private IEnumerable<GuidingSample> GuidingRecord() {
            var own = History().GuidingSamples().ToList();
            List<GuidingSample> scheduler = [];
            if (File.Exists(TargetSchedulerBridge.DatabasePath)) {
                try {
                    var since = DateTimeOffset.UtcNow.AddDays(-400).ToUnixTimeSeconds();
                    scheduler = ReadSchedulerHistory().Subs
                        .Where(s => s.ProfileId == RigId && s.Sub.T >= since && s.Sub.R is > 0 && s.Sub.E is > 0)
                        .Select(s => new GuidingSample(s.Sub.T, s.Sub.E!.Value, s.Sub.R!.Value))
                        .ToList();
                } catch (Exception ex) {
                    NinaLog.Instance.Debug($"Target Scheduler's guiding record not read: {ex.Message}");
                }
            }
            if (scheduler.Count == 0) return own;
            var last = scheduler.Max(s => s.UnixSeconds);
            return scheduler.Concat(own.Where(s => s.UnixSeconds > last + 60));
        }

        private ExposureHistory History() => exposureHistory ??= ExposureHistory.Load(ExposureHistoryPath);

        private void SaveExposureHistory() {
            try {
                exposureHistory?.Save(ExposureHistoryPath);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Exposure history not saved: {ex.Message}");
            }
        }
    }
}
