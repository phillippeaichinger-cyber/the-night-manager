using NightManager.Core.Settings;
using NightManager.Core.Analysis.Optics;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.Image.Interfaces;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Tilt and curvature in focuser steps from autofocus runs (NINA's own or Hocus Focus): every autofocus exposure
    /// passes NINA's image pipeline, so its stars are measured per region against the focuser position; when the run
    /// finishes, each region's own V-curve gives where it comes to focus.
    /// </summary>
    public sealed partial class NightManagerService {
        private readonly FocusSurface focusSurface = new();
        private readonly List<Task> focusSamples = [];

        private void AttachImagePrepared() {
            var ev = imaging.GetType().GetEvent("ImagePrepared") ?? throw new InvalidOperationException("NINA has no ImagePrepared event");
            var method = typeof(NightManagerService).GetMethod(nameof(OnImagePreparedRaw), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            imagePrepared = Delegate.CreateDelegate(ev.EventHandlerType!, this, method);
            ev.AddEventHandler(imaging, imagePrepared);
        }

        private void DetachImagePrepared() {
            if (imagePrepared is null) return;
            imaging.GetType().GetEvent("ImagePrepared")?.RemoveEventHandler(imaging, imagePrepared);
            imagePrepared = null;
        }

        private void OnImagePreparedRaw(object sender, EventArgs e) {
            if (!Settings.OpticsAnalysis || !collector.Focusing.IsRunning) return;
            if (e.GetType().GetProperty("RenderedImage")?.GetValue(e) is IRenderedImage rendered) {
                OnImagePrepared(rendered);
            }
        }

        private void OnImagePrepared(IRenderedImage rendered) {
            if (!DataOn(DataFeatures.Optics) || !collector.Focusing.IsRunning) {
                return;
            }
            try {
                var image = rendered?.RawImageData;
                if (image is null) return;
                var position = focuser.GetInfo()?.Position ?? 0;
                if (position <= 0) return;
                // A cropped autofocus region can't show the whole sensor: skip it.
                var full = camera.GetInfo();
                var bin = Math.Max(1, image.MetaData?.Camera?.BinX ?? 1);
                if (full is { XSize: > 0 } && image.Properties.Width * bin < full.XSize * 0.9) return;
                var pixels = image.Data.FlatArray;
                var props = image.Properties;
                var task = Task.Run(() => {
                    if (OpticsAnalyzer.HfrGrid(pixels, props.Width, props.Height, props.BitDepth, props.IsBayered, FocusSurface.Grid) is { } cells) {
                        focusSurface.Add(position, cells, (double)props.Width / props.Height);
                    }
                });
                lock (focusSamples) focusSamples.Add(task);
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Focus surface sample skipped: {ex.Message}");
            }
        }

        private void ResetFocusSurface() {
            focusSurface.Reset();
            lock (focusSamples) focusSamples.Clear();
        }

        /// <summary>The run's sensor focus surface, once its samples are measured (null when it didn't give enough).</summary>
        private SensorFocusReport ComputeFocusSurface() {
            Task[] pending;
            lock (focusSamples) pending = focusSamples.ToArray();
            Task.WaitAll(pending, TimeSpan.FromSeconds(20));
            var profile = profiles.ActiveProfile;
            var report = focusSurface.Compute(profile?.FocuserSettings?.AutoFocusStepSize,
                profile?.TelescopeSettings?.FocalRatio is double f && f > 0 ? f : null,
                Settings.FocuserStepMicrons);
            if (report is not null) {
                NinaLog.Instance.Info($"Sensor focus from autofocus: tilt {report.TiltSteps} steps towards {report.TiltAngle}°, curvature {report.CurvatureSteps} steps ({report.Positions} positions)");
            }
            ResetFocusSurface();
            return report;
        }
    }
}
