using NightManager.Core.Contracts;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using System.Collections.Generic;

namespace NightManager.Plugin.Runtime {
    internal sealed record DeviceTransition(string Device, string Name, bool Connected);

    /// <summary>
    /// Receives pushed device state from every NINA equipment mediator and keeps the latest
    /// mapped value for each. Strongly typed against NINA's info classes (no reflection), and
    /// NaN values from drivers become nulls instead of fake zeros.
    /// </summary>
    internal sealed class StateCollector :
        ICameraConsumer, ITelescopeConsumer, IGuiderConsumer, IFocuserConsumer, IFilterWheelConsumer,
        IRotatorConsumer, IWeatherDataConsumer, ISafetyMonitorConsumer, IDomeConsumer, IFlatDeviceConsumer, ISwitchConsumer {

        private readonly object gate = new();
        private readonly Dictionary<string, bool> connected = new();
        private CameraState camera;
        private MountState mount;
        private GuiderState guider;
        private FocuserState focuser;
        private FilterWheelState filterWheel;
        private RotatorState rotator;
        private WeatherState weather;
        private SafetyState safety;
        private DomeState dome;
        private FlatDeviceState flatDevice;
        private SwitchesState switches;

        /// <summary>The phone's control job (set by the equipment controller).</summary>
        public Func<ControlState> Control { get; set; } = () => null;
        private bool guiding;

        /// <summary>Any device state changed.</summary>
        public event EventHandler Changed;

        /// <summary>A device connected or disconnected.</summary>
        public event EventHandler<DeviceTransition> DeviceTransition;

        /// <summary>The safety monitor changed between safe and unsafe.</summary>
        public event EventHandler<bool> SafetyChanged;

        /// <summary>Autofocus finished; carries the new focus position, filter and temperature.</summary>
        public event EventHandler<AutoFocusInfo> AutofocusFinished;

        public event EventHandler AutofocusStarting;

        public Func<bool> DomeFollowing { get; set; } = () => false;

        /// <summary>RMS from individual guide steps (NINA 3.2 doesn't push the guider's RMS to plugins while guiding).</summary>
        public NightManager.Core.Analysis.GuidingMonitor GuideSteps { get; } = new();

        /// <summary>The current or last autofocus run, for the app's live focus view.</summary>
        public NightManager.Core.Analysis.AutofocusTracker Focusing { get; } = new();

        public bool Guiding {
            get {
                lock (gate) {
                    return guiding;
                }
            }

            set {
                lock (gate) {
                    guiding = value;
                    guider = guider is null ? null : guider with { State = GuiderStateText(guider.Connected) };
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public ObservatorySnapshot Build(SequenceState sequence, NightState night, SessionSummary session, SchedulerState scheduler = null) {
            lock (gate) {
                return new ObservatorySnapshot {
                    Camera = camera,
                    Mount = mount,
                    Guider = WithLiveRms(guider),
                    Focuser = focuser,
                    FilterWheel = filterWheel,
                    Rotator = rotator,
                    Weather = weather,
                    Safety = safety,
                    Dome = dome is null ? null : dome with { FollowingScope = dome.Connected && DomeFollowing() },
                    FlatDevice = flatDevice,
                    Switches = switches,
                    Control = Control(),
                    Sequence = sequence,
                    Scheduler = scheduler,
                    Night = night,
                    Session = session,
                    Autofocus = Focusing.Current
                };
            }
        }

        public void UpdateDeviceInfo(SwitchInfo info) {
            Set(ref switches, info, "switch", () => new SwitchesState {
                Connected = info.Connected,
                Name = info.Name,
                Items = !info.Connected ? [] : (info.WritableSwitches ?? new System.Collections.ObjectModel.ReadOnlyCollection<NINA.Equipment.Interfaces.IWritableSwitch>([]))
                    .Select(w => new SwitchItem { Index = w.Id, Name = w.Name, Value = Finite(w.Value), Writable = true, Min = Finite(w.Minimum), Max = Finite(w.Maximum), Step = Finite(w.StepSize) })
                    .Concat((info.ReadonlySwitches ?? new System.Collections.ObjectModel.ReadOnlyCollection<NINA.Equipment.Interfaces.ISwitch>([]))
                        .Select(r => new SwitchItem { Index = r.Id, Name = r.Name, Value = Finite(r.Value), Writable = false }))
                    .ToList()
            });
        }

        public void UpdateDeviceInfo(CameraInfo info) {
            var (exposureStart, exposureEnd) = ExposureTimes(info);
            Set(ref camera, info, "camera", () => new CameraState {
                Connected = info.Connected,
                Name = info.Name,
                State = info.Connected ? info.CameraState.ToString().ToLowerInvariant() : "disconnected",
                Temperature = Finite(info.Temperature),
                TargetTemperature = info.CanSetTemperature ? Finite(info.TemperatureSetPoint) : null,
                CoolerOn = info.CanSetTemperature ? info.CoolerOn : null,
                CoolerPower = info.CanSetTemperature ? Finite(info.CoolerPower) : null,
                Gain = info.CanGetGain ? info.Gain : null,
                Offset = info.CanSetOffset ? info.Offset : null,
                BinX = info.BinX,
                BinY = info.BinY,
                ReadoutMode = ReadoutModeName(info),
                DewHeaterOn = info.HasDewHeater ? info.DewHeaterOn : null,
                BatteryLevel = info.HasBattery ? info.Battery : null,
                ExposureStartedAt = exposureStart,
                ExposureEndsAt = exposureEnd
            });
        }

        private DateTimeOffset? exposureStartedAt;

        /// <summary>
        /// NINA sets the expected end of the running exposure on the camera info; the start is when we first saw
        /// it exposing (rounded to the second so repeated updates don't count as changes).
        /// </summary>
        private (DateTimeOffset? Start, DateTimeOffset? End) ExposureTimes(CameraInfo info) {
            lock (gate) {
                if (!info.Connected || !info.IsExposing) {
                    exposureStartedAt = null;
                    return (null, null);
                }
                var now = DateTimeOffset.UtcNow;
                exposureStartedAt ??= new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
                DateTimeOffset? end = null;
                if (info.ExposureEndTime > DateTime.MinValue.AddDays(1)) {
                    var utc = info.ExposureEndTime.Kind == DateTimeKind.Utc ? info.ExposureEndTime : info.ExposureEndTime.ToUniversalTime();
                    var candidate = new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
                    // Ignore a stale end time left over from the previous exposure.
                    if (candidate > exposureStartedAt) {
                        end = candidate;
                    }
                }
                return (exposureStartedAt, end);
            }
        }

        public void UpdateDeviceInfo(TelescopeInfo info) {
            Set(ref mount, info, "mount", () => new MountState {
                Connected = info.Connected,
                Name = info.Name,
                RightAscension = Finite(info.RightAscension),
                Declination = Finite(info.Declination),
                Altitude = Finite(info.Altitude),
                Azimuth = Finite(info.Azimuth),
                Tracking = info.TrackingEnabled,
                TrackingRate = info.TrackingRate.TrackingMode.ToString(),
                Slewing = info.Slewing,
                AtPark = info.AtPark,
                AtHome = info.AtHome,
                SideOfPier = info.SideOfPier switch {
                    NINA.Core.Enum.PierSide.pierEast => "east",
                    NINA.Core.Enum.PierSide.pierWest => "west",
                    _ => "unknown"
                },
                HoursToMeridian = Finite(info.TimeToMeridianFlip),
                SiderealTime = Finite(info.SiderealTime)
            });
        }

        public void UpdateDeviceInfo(GuiderInfo info) {
            Set(ref guider, info, "guider", () => {
                var rms = info.RMSError;
                return new GuiderState {
                    Connected = info.Connected,
                    Name = info.Name,
                    State = GuiderStateText(info.Connected),
                    RmsRa = Arcsec(rms?.RA),
                    RmsDec = Arcsec(rms?.Dec),
                    RmsTotal = Arcsec(rms?.Total),
                    PeakRa = Arcsec(rms?.PeakRA),
                    PeakDec = Arcsec(rms?.PeakDec),
                    PixelScale = Finite(info.PixelScale)
                };
            });
            if (Finite(info.PixelScale) is double scale && scale > 0) {
                GuideSteps.PixelScale = scale;
            }
            if (!info.Connected) {
                GuideSteps.Reset();
            }
        }

        public void UpdateDeviceInfo(FocuserInfo info) {
            if (info.Connected) {
                Focusing.FocuserMoved(info.Position, DateTimeOffset.UtcNow);
            }
            Set(ref focuser, info, "focuser", () => new FocuserState {
                Connected = info.Connected,
                Name = info.Name,
                Position = info.Connected ? info.Position : null,
                Temperature = Finite(info.Temperature),
                Moving = info.IsMoving,
                TempComp = info.TempCompAvailable ? info.TempComp : null
            });
        }

        public void UpdateEndAutoFocusRun(AutoFocusInfo info) => AutofocusFinished?.Invoke(this, info);

        public void UpdateUserFocused(FocuserInfo info) {
        }

        public void AutoFocusRunStarting() => AutofocusStarting?.Invoke(this, EventArgs.Empty);

        /// <summary>NINA's own autofocus measured a point (Hocus Focus doesn't broadcast these).</summary>
        public void NewAutoFocusPoint(OxyPlot.DataPoint point) {
            Focusing.AddPoint(point.X, point.Y, DateTimeOffset.UtcNow);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateDeviceInfo(FilterWheelInfo info) {
            Set(ref filterWheel, info, "filterWheel", () => new FilterWheelState {
                Connected = info.Connected,
                Name = info.Name,
                CurrentFilter = info.SelectedFilter?.Name
            });
        }

        public void UpdateDeviceInfo(RotatorInfo info) {
            Set(ref rotator, info, "rotator", () => new RotatorState {
                Connected = info.Connected,
                Name = info.Name,
                Position = Finite(info.Position),
                MechanicalPosition = Finite(info.MechanicalPosition),
                Moving = info.IsMoving
            });
        }

        public void UpdateDeviceInfo(WeatherDataInfo info) {
            Set(ref weather, info, "weather", () => new WeatherState {
                Connected = info.Connected,
                Name = info.Name,
                Temperature = Finite(info.Temperature),
                Humidity = Finite(info.Humidity),
                DewPoint = Finite(info.DewPoint),
                Pressure = Finite(info.Pressure),
                WindSpeed = Finite(info.WindSpeed),
                WindGust = Finite(info.WindGust),
                WindDirection = Finite(info.WindDirection),
                CloudCover = Finite(info.CloudCover),
                RainRate = Finite(info.RainRate),
                SkyBrightness = Finite(info.SkyBrightness),
                SkyQuality = Finite(info.SkyQuality),
                SkyTemperature = Finite(info.SkyTemperature)
            });
        }

        public void UpdateDeviceInfo(SafetyMonitorInfo info) {
            bool? previous;

            lock (gate) {
                previous = safety?.Connected == true ? safety.IsSafe : null;
            }

            Set(ref safety, info, "safety", () => new SafetyState {
                Connected = info.Connected,
                Name = info.Name,
                IsSafe = info.Connected ? info.IsSafe : null
            });

            if (info.Connected && previous is bool was && was != info.IsSafe) {
                SafetyChanged?.Invoke(this, info.IsSafe);
            }
        }

        public void UpdateDeviceInfo(DomeInfo info) {
            Set(ref dome, info, "dome", () => new DomeState {
                Connected = info.Connected,
                Name = info.Name,
                Shutter = info.ShutterStatus switch {
                    NINA.Equipment.Interfaces.ShutterState.ShutterOpen => "open",
                    NINA.Equipment.Interfaces.ShutterState.ShutterClosed => "closed",
                    NINA.Equipment.Interfaces.ShutterState.ShutterOpening => "opening",
                    NINA.Equipment.Interfaces.ShutterState.ShutterClosing => "closing",
                    NINA.Equipment.Interfaces.ShutterState.ShutterError => "error",
                    _ => "unknown"
                },
                Azimuth = Finite(info.Azimuth),
                Slewing = info.Slewing,
                AtPark = info.AtPark
            });
        }

        public void UpdateDeviceInfo(FlatDeviceInfo info) {
            Set(ref flatDevice, info, "flatDevice", () => new FlatDeviceState {
                Connected = info.Connected,
                Name = info.Name,
                CoverState = info.SupportsOpenClose ? info.CoverState.ToString().ToLowerInvariant() : null,
                LightOn = info.SupportsOnOff ? info.LightOn : null,
                Brightness = info.Connected ? info.Brightness : null
            });
        }

        public void Dispose() {
        }

        private void Set<T>(ref T field, DeviceInfo info, string device, Func<T> map) where T : class {
            T mapped;

            try {
                mapped = info is null ? null : map();
            } catch (Exception ex) {
                // A misbehaving driver must never break monitoring of the other devices.
                NinaLog.Instance.Warning($"Could not read {device} state: {ex.Message}");
                return;
            }

            DeviceTransition transition = null;

            lock (gate) {
                if (Equals(field, mapped)) {
                    return;
                }

                field = mapped;
                var isConnected = info?.Connected == true;

                if (connected.TryGetValue(device, out var was) && was != isConnected) {
                    transition = new DeviceTransition(device, info?.Name, isConnected);
                }

                connected[device] = isConnected;
            }

            if (transition is not null) {
                DeviceTransition?.Invoke(this, transition);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Prefer RMS computed from recent guide steps; the guider's own figures stay 0 on NINA 3.2.</summary>
        private GuiderState WithLiveRms(GuiderState state) {
            if (state is not { Connected: true } || GuideSteps.Current(DateTimeOffset.UtcNow) is not { } live) {
                return state;
            }
            return state with {
                State = "guiding",
                RmsRa = live.RmsRa,
                RmsDec = live.RmsDec,
                RmsTotal = live.RmsTotal,
                PeakRa = live.PeakRa,
                PeakDec = live.PeakDec
            };
        }

        private string GuiderStateText(bool isConnected) => !isConnected ? "disconnected" : guiding ? "guiding" : "idle";

        private static string ReadoutModeName(CameraInfo info) {
            var modes = info.ReadoutModes?.ToList();
            return modes is { Count: > 1 } && info.ReadoutMode >= 0 && info.ReadoutMode < modes.Count ? modes[info.ReadoutMode] : null;
        }

        private static double? Arcsec(RMSUnit unit) => unit is null ? null : Finite(unit.Arcseconds);

        internal static double? Finite(double value) => double.IsFinite(value) ? Math.Round(value, 4) : null;

        internal static double? Finite(float value) => float.IsFinite(value) ? Math.Round(value, 4) : null;
    }
}
