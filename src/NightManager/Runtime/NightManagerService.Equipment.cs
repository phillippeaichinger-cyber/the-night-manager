using NightManager.Core.Contracts;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// What the rig is, for the beta research (focus and tilt models per kind of scope): the telescope, sensor and
    /// focuser from the active NINA profile and the connected devices. Built for each check-in; the server only writes
    /// it when it changes. Equipment names and numbers only, the same names the status already shows.
    /// </summary>
    public sealed partial class NightManagerService {
        private EquipmentInfo? BuildEquipment() {
            try {
                var profile = profiles.ActiveProfile;
                if (profile is null) return null;
                var scope = profile.TelescopeSettings;
                double? Positive(double v) => double.IsFinite(v) && v > 0 ? Math.Round(v, 2) : null;
                var focalLength = Positive(scope.FocalLength);
                var focalRatio = Positive(scope.FocalRatio);
                var cameraInfo = camera.GetInfo();
                var focuserInfo = focuser.GetInfo();
                var wheelInfo = filterWheel.GetInfo();
                var filters = profile.FilterWheelSettings.FilterWheelFilters?.Select(f => f.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(20).ToList();
                return new EquipmentInfo {
                    TelescopeName = Trim(scope.Name),
                    FocalLengthMm = focalLength,
                    FocalRatio = focalRatio,
                    ApertureMm = focalLength is double fl && focalRatio is double fr ? Math.Round(fl / fr, 1) : null,
                    CameraName = cameraInfo?.Connected == true ? Trim(cameraInfo.Name) : null,
                    PixelSizeUm = Positive(cameraInfo?.Connected == true && cameraInfo.PixelSize > 0 ? cameraInfo.PixelSize : profile.CameraSettings.PixelSize),
                    SensorWidthPx = cameraInfo?.Connected == true && cameraInfo.XSize > 0 ? cameraInfo.XSize : null,
                    SensorHeightPx = cameraInfo?.Connected == true && cameraInfo.YSize > 0 ? cameraInfo.YSize : null,
                    FocuserName = focuserInfo?.Connected == true ? Trim(focuserInfo.Name) : null,
                    FocuserStepSize = profile.FocuserSettings.AutoFocusStepSize > 0 ? profile.FocuserSettings.AutoFocusStepSize : null,
                    // Only known once something is connected: a one-shot-colour rig has a camera and no wheel.
                    HasFilterWheel = wheelInfo?.Connected == true ? true : cameraInfo?.Connected == true ? false : null,
                    Filters = filters is { Count: > 0 } ? filters : null,
                };
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Debug($"Night Manager: equipment not read: {ex.Message}");
                return null;
            }
        }

        private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(s.Trim().Length, 80)];
    }
}
