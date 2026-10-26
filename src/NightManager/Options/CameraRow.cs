using CommunityToolkit.Mvvm.ComponentModel;
using NightManager.Core.Cameras;
using System.Windows;
using System.Windows.Controls;

namespace NightManager.Plugin.Options {
    /// <summary>One observatory camera being edited on the options page.</summary>
    public sealed partial class CameraRow : ObservableObject {
        public static IReadOnlyList<string> KindLabels { get; } = ["Snapshot address (http)", "Folder (newest picture)", "RTSP stream", "ZWO camera on this PC (USB)"];

        /// <summary>The sources this camera's section allows: the ZWO driver is for pier cameras only.</summary>
        public IReadOnlyList<string> SourceLabels => AllSky ? KindLabels.Take(3).ToList() : KindLabels;
        public static IReadOnlyList<string> ModeLabels { get; } = ["Only while someone's watching", "On a schedule"];
        public static IReadOnlyList<int> Widths { get; } = [480, 640, 800, 1024];

        public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];

        [ObservableProperty]
        private string name = "Observatory camera";

        [ObservableProperty]
        private bool enabled = true;

        /// <summary>Set when the camera is added (the section it's added in); not changed afterwards.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SourceLabels))]
        private bool allSky;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AddressHint), nameof(IsNetwork), nameof(IsZwo), nameof(HasAddress))]
        private int kindIndex;

        /// <summary>ZWO: daylight and darkness gain, empty for the default (lowest by day, half way up after dark).</summary>
        [ObservableProperty]
        private string dayGain = string.Empty;

        [ObservableProperty]
        private string nightGain = string.Empty;

        [ObservableProperty]
        private double maxNightExposureSeconds = PierExposure.DefaultMaxNightSeconds;

        [ObservableProperty]
        private string address = string.Empty;

        [ObservableProperty]
        private string userName = string.Empty;

        /// <summary>Typed here only; stored encrypted by the service. Empty keeps the saved one.</summary>
        [ObservableProperty]
        private string password = string.Empty;

        [ObservableProperty]
        private bool hasPassword;

        [ObservableProperty]
        private int modeIndex;

        [ObservableProperty]
        private int intervalMinutes = CameraPolicy.MinimumIntervalMinutes;

        [ObservableProperty]
        private int maxKilobytes = CameraPolicy.DefaultKilobytes;

        [ObservableProperty]
        private int width = 640;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Editable), nameof(LockText))]
        private bool locked;

        public bool Editable => !Locked;

        public bool IsNetwork => (CameraSourceKind)KindIndex is CameraSourceKind.Snapshot or CameraSourceKind.Rtsp;

        public bool IsZwo => (CameraSourceKind)KindIndex == CameraSourceKind.Zwo;

        public bool HasAddress => !IsZwo;

        public string LockText => Locked
            ? "Locked: changing these settings needs the camera's login (or the passcode for a folder or ZWO camera)."
            : "Unlocked: anyone using NINA on this PC can change these settings.";

        public string AddressHint => (CameraSourceKind)KindIndex switch {
            CameraSourceKind.Folder => @"Folder where another program saves pictures, e.g. C:\AllSky\images. Date folders inside it (a new one each day) are followed to the newest picture.",
            CameraSourceKind.Rtsp => "rtsp://192.168.1.20:554/stream1 (without the login: it goes below)",
            _ => "http://192.168.1.20/cgi-bin/snapshot.cgi (the camera's single-picture address)",
        };

        public string ListLabel => $"{Name} · {KindLabels[KindIndex].Split(' ')[0]} · every {IntervalMinutes} min · {MaxKilobytes} KB{(Locked ? " · locked" : "")}{(Enabled ? "" : " · off")}";

        public static CameraRow From(CameraSettings c, bool hasPassword) => new() {
            Id = c.Id, Name = c.Name, Enabled = c.Enabled, AllSky = c.AllSky, KindIndex = (int)c.Kind, Address = c.Address, UserName = c.UserName,
            HasPassword = hasPassword, ModeIndex = (int)c.Mode, IntervalMinutes = c.IntervalMinutes, MaxKilobytes = c.MaxKilobytes,
            Width = c.Width, Locked = c.Locked,
            DayGain = c.DayGain?.ToString() ?? string.Empty, NightGain = c.NightGain?.ToString() ?? string.Empty,
            MaxNightExposureSeconds = c.MaxNightExposureSeconds,
        };

        public CameraSettings ToSettings() => new CameraSettings {
            Id = Id, Name = Name, Enabled = Enabled, AllSky = AllSky, Kind = (CameraSourceKind)KindIndex, Address = Address, UserName = UserName,
            Mode = (CameraCaptureMode)ModeIndex, IntervalMinutes = IntervalMinutes, MaxKilobytes = MaxKilobytes, Width = Width,
            DayGain = int.TryParse(DayGain, out var day) ? day : null, NightGain = int.TryParse(NightGain, out var night) ? night : null,
            MaxNightExposureSeconds = MaxNightExposureSeconds,
        }.Normalized();

        public void Refresh() => OnPropertyChanged(nameof(ListLabel));
    }

    /// <summary>Lets a PasswordBox take part in binding (WPF doesn't expose its Password for safety reasons).</summary>
    public static class PasswordBinding {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
            // Default null, not "": the bound passwords start as "", and WPF only calls OnValueChanged (which hooks the
            // box's PasswordChanged) when the value differs from the default. With "" the box was never hooked and
            // whatever was typed never reached the settings: cameras were tried, and saved, without a password.
            "Value", typeof(string), typeof(PasswordBinding), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(PasswordBinding));

        public static string GetValue(DependencyObject d) => (string)d.GetValue(ValueProperty);

        public static void SetValue(DependencyObject d, string value) => d.SetValue(ValueProperty, value);

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            if (d is not PasswordBox box) {
                return;
            }
            if (!(bool)box.GetValue(HookedProperty)) {
                box.SetValue(HookedProperty, true);
                box.PasswordChanged += (_, _) => SetValue(box, box.Password);
            }
            var value = e.NewValue as string ?? string.Empty;
            if (box.Password != value) {
                box.Password = value;
            }
        }
    }
}
