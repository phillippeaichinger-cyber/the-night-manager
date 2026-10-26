using NightManager.Core.Commands;
using NightManager.Core.Contracts;
using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Model;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.SequenceItem.Platesolving;
using System.Windows;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Hands-on control from the phone (docs/CONTROL.md): the same calls NINA's own buttons make. One job at a time
    /// (a slew, an autofocus, a loop) runs in the background so the next command, above all a stop, is never stuck
    /// behind it; quick actions (light on, a switch) run straight away. What's running shows in the snapshot.
    /// </summary>
    public sealed partial class NightManagerService {
        private const string PolarTypeName = "NINA.Plugins.PolarAlignment.Instructions.PolarAlignment";
        private const string PolarErrorTopic = "PolarAlignmentPlugin_PolarAlignment_AlignmentError";
        private const string PolarProgressTopic = "PolarAlignmentPlugin_PolarAlignment_Progress";
        private const string PolarPauseTopic = "PolarAlignmentPlugin_PolarAlignment_PauseAlignment";
        private const string PolarResumeTopic = "PolarAlignmentPlugin_PolarAlignment_ResumeAlignment";

        private readonly object controlGate = new();
        private CancellationTokenSource controlJob;
        private string controlAction;
        private string controlJobText;
        private DateTimeOffset? controlStarted;
        private string lastControlAction;
        private string lastControlResult;
        private bool? lastControlFailed;
        private DateTimeOffset? lastControlAt;
        private PolarState polarState;
        private PolarListener polarListener;

        private ControlState ControlNow() {
            lock (controlGate) {
                if (controlAction is null && lastControlAction is null && polarState is null) return null;
                return new ControlState {
                    Action = controlAction, Job = controlJobText, StartedAt = controlStarted,
                    LastAction = lastControlAction, LastResult = lastControlResult, LastFailed = lastControlFailed, LastAt = lastControlAt,
                    Polar = polarState
                };
            }
        }

        private bool SequenceRunningNow() {
            try {
                return SequencerReady() && sequence.IsAdvancedSequenceRunning() || ClassicRoot()?.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING;
            } catch {
                return false;
            }
        }

        private async Task<string> HandleEquipmentCommand(RemoteCommand command, CancellationToken token) {
            if (EquipmentActions.Refusal(command, SequenceRunningNow(), Setup?.ControlsMount ?? true) is { } why) {
                throw new InvalidOperationException(why);
            }
            var action = EquipmentActions.Arg(command, "action");
            if (NotConnected(action) is { } missing) {
                throw new InvalidOperationException($"The {missing} isn't connected. Connect it first (Rig → Connect all, or Connect under it).");
            }
            var progress = new Progress<ApplicationStatus>();
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(token);
            quick.CancelAfter(TimeSpan.FromSeconds(60));
            var q = quick.Token;

            switch (action) {
                // ---- whatever is running
                case "job.stop":
                    CancelJob("");
                    try { camera.AbortExposure(); } catch { /* nothing exposing */ }
                    return "Stopped.";

                // ---- devices
                case "connect":
                    return Job(action, $"Connecting {ControlDeviceLabel(Device(command))}", t => ConnectAsync(Device(command), true));
                case "disconnect":
                    return Job(action, $"Disconnecting {ControlDeviceLabel(Device(command))}", t => ConnectAsync(Device(command), false));

                // ---- camera
                case "camera.cool": {
                    var target = EquipmentActions.Number(command, "temperature") ?? throw new InvalidOperationException("Which temperature?");
                    var minutes = EquipmentActions.Number(command, "minutes") ?? 5;
                    Background($"Cooling to {target:0.#} °C", () => camera.CoolCamera(target, TimeSpan.FromMinutes(Math.Clamp(minutes, 0, 60)), progress, CancellationToken.None));
                    return $"Cooling to {target:0.#} °C over {minutes:0} min.";
                }
                case "camera.warm":
                    Background("Warming the camera", () => camera.WarmCamera(TimeSpan.FromMinutes(Math.Clamp(EquipmentActions.Number(command, "minutes") ?? 10, 0, 60)), progress, CancellationToken.None));
                    return "Warming started.";
                case "camera.dewHeater": {
                    var on = EquipmentActions.Flag(command, "on") ?? throw new InvalidOperationException("On or off?");
                    camera.SetDewHeater(on);
                    return on ? "Dew heater on." : "Dew heater off.";
                }
                case "camera.shot": {
                    var shot = Shot(command);
                    return Job(action, $"Test shot {shot.Label}", async t => {
                        await TakeShotAsync(shot, t);
                        return "Test shot taken.";
                    });
                }
                case "camera.loop": {
                    var shot = Shot(command);
                    return Job(action, $"Looping {shot.Label}", async t => {
                        var taken = 0;
                        while (!t.IsCancellationRequested) {
                            await TakeShotAsync(shot, t);
                            taken++;
                        }
                        return $"Loop stopped after {taken} shots.";
                    });
                }
                case "camera.stop":
                    CancelJob("camera.");
                    try { camera.AbortExposure(); } catch { /* nothing exposing */ }
                    return "Stopped.";

                // ---- focus and filters
                case "focuser.move": {
                    var position = (int)(EquipmentActions.Number(command, "position") ?? throw new InvalidOperationException("Which position?"));
                    return Job(action, $"Focuser to {position}", async t => $"Focuser at {await focuser.MoveFocuser(position, t)}.");
                }
                case "focuser.nudge": {
                    var steps = (int)(EquipmentActions.Number(command, "steps") ?? throw new InvalidOperationException("How many steps?"));
                    return Job(action, $"Focuser {(steps > 0 ? "out" : "in")} {Math.Abs(steps)}", async t => $"Focuser at {await focuser.MoveFocuserRelative(steps, t)}.");
                }
                case "focuser.autofocus":
                    return Job(action, "Autofocus", async t => {
                        var item = Instruction<RunAutofocus>();
                        await item.Execute(progress, t);
                        return "Autofocus finished.";
                    });
                case "focuser.stop":
                    CancelJob("focuser.");
                    return "Stopped.";
                case "filter.set": {
                    var name = EquipmentActions.Arg(command, "filter") ?? throw new InvalidOperationException("Which filter?");
                    var filter = Filter(name) ?? throw new InvalidOperationException($"NINA's filter wheel has no filter \"{name}\".");
                    return Job(action, $"Filter to {filter.Name}", async t => $"Filter {(await filterWheel.ChangeFilter(filter, t, progress))?.Name ?? filter.Name}.");
                }

                // ---- guiding
                case "guider.start":
                case "guider.calibrate": {
                    var calibrate = action == "guider.calibrate";
                    return Job(action, calibrate ? "Calibrating and guiding" : "Starting guiding", async t =>
                        await guider.StartGuiding(calibrate, progress, t) ? "Guiding." : throw new InvalidOperationException("The guider didn't start guiding."));
                }
                case "guider.stop":
                    CancelJob("guider.");
                    return await guider.StopGuiding(q) ? "Guiding stopped." : throw new InvalidOperationException("The guider didn't confirm it stopped.");

                // ---- rotator, flat panel, switches
                case "rotator.move": {
                    var angle = (float)(EquipmentActions.Number(command, "angle") ?? throw new InvalidOperationException("Which angle?"));
                    return Job(action, $"Rotator to {angle:0.#}°", async t => $"Rotator at {await rotator.Move(angle, t):0.#}°.");
                }
                case "flat.light": {
                    var on = EquipmentActions.Flag(command, "on") ?? throw new InvalidOperationException("On or off?");
                    await flatDevice.ToggleLight(on, progress, q);
                    return on ? "Panel light on." : "Panel light off.";
                }
                case "flat.brightness": {
                    var value = (int)(EquipmentActions.Number(command, "value") ?? throw new InvalidOperationException("Which brightness?"));
                    await flatDevice.SetBrightness(value, progress, q);
                    return $"Panel brightness {value}.";
                }
                case "flat.cover": {
                    var open = EquipmentActions.Flag(command, "open") ?? throw new InvalidOperationException("Open or close?");
                    return Job(action, open ? "Opening the cover" : "Closing the cover", async t => {
                        if (open) await flatDevice.OpenCover(progress, t); else await flatDevice.CloseCover(progress, t);
                        return open ? "Cover open." : "Cover closed.";
                    });
                }
                case "switch.set": {
                    var index = (short)(EquipmentActions.Number(command, "index") ?? throw new InvalidOperationException("Which switch?"));
                    var value = EquipmentActions.Number(command, "value") ?? throw new InvalidOperationException("Which value?");
                    await switchHub.SetSwitchValue(index, value, progress, q);
                    return "Switch set.";
                }

                // ---- mount
                case "mount.unpark":
                    return Job(action, "Unparking", async t => await telescope.UnparkTelescope(progress, t) ? "Unparked." : throw new InvalidOperationException("The mount didn't unpark."));
                case "mount.park":
                    return Job(action, "Parking", async t => await telescope.ParkTelescope(progress, t) ? "Parked." : throw new InvalidOperationException("The mount didn't report that it parked."));
                case "mount.home":
                    return Job(action, "Finding home", async t => await telescope.FindHome(progress, t) ? "At home." : throw new InvalidOperationException("The mount didn't find home."));
                case "mount.slew": {
                    var (coordinates, name) = Target(command);
                    return Job(action, $"Slewing to {name}", async t => {
                        await UnparkIfParked(progress, t);
                        return await telescope.SlewToCoordinatesAsync(coordinates, t) ? $"At {name}." : throw new InvalidOperationException("The mount didn't finish the slew.");
                    });
                }
                case "mount.centre": {
                    var (coordinates, name) = Target(command);
                    var rotation = EquipmentActions.Number(command, "rotation");
                    return Job(action, $"Slewing to {name} and centring", async t => {
                        await UnparkIfParked(progress, t);
                        Center item;
                        if (rotation is double angle && rotator.GetInfo()?.Connected == true) {
                            var turning = Instruction<CenterAndRotate>();
                            turning.PositionAngle = angle;
                            item = turning;
                        } else {
                            item = Instruction<Center>();
                        }
                        item.Coordinates.Coordinates = coordinates;
                        await item.Execute(progress, t);
                        return $"Centred on {name}.";
                    });
                }
                case "mount.tracking": {
                    var on = EquipmentActions.Flag(command, "on") ?? throw new InvalidOperationException("On or off?");
                    return telescope.SetTrackingEnabled(on) ? (on ? "Tracking on." : "Tracking off.") : throw new InvalidOperationException("The mount didn't change tracking.");
                }
                case "mount.stop":
                    CancelJob("mount.");
                    telescope.StopSlew();
                    return "Mount stopped.";

                // ---- dome
                case "dome.open":
                    return Job(action, "Opening the dome", async t => await dome.OpenShutter(t) ? "Dome open." : throw new InvalidOperationException("The dome didn't report that it opened."));
                case "dome.close":
                    // The safe way: never refused, never waits for a running job.
                    Background("Closing the dome", () => dome.CloseShutter(CancellationToken.None));
                    return "Closing the dome.";
                case "dome.park":
                    return Job(action, "Parking the dome", async t => await dome.Park(t) ? "Dome parked." : throw new InvalidOperationException("The dome didn't park."));
                case "dome.follow": {
                    var on = EquipmentActions.Flag(command, "on") ?? throw new InvalidOperationException("On or off?");
                    return (on ? await dome.EnableFollowing(q) : await dome.DisableFollowing(q)) ? (on ? "Dome following the scope." : "Dome following off.") : throw new InvalidOperationException("The dome didn't change following.");
                }

                // ---- polar alignment (Three Point Polar Alignment plugin)
                case "polar.start":
                    return Job(action, "Polar alignment", PolarAlignAsync);
                case "polar.pause":
                    await PublishPolar(PolarPauseTopic);
                    SetPolar(p => p with { Paused = true });
                    return "Paused: adjust the mount, then resume.";
                case "polar.resume":
                    await PublishPolar(PolarResumeTopic);
                    SetPolar(p => p with { Paused = false });
                    return "Measuring again.";
                case "polar.stop":
                    CancelJob("polar.");
                    return "Polar alignment stopped.";

                // ---- the sequence
                case "sequence.start":
                    if (!Settings.AllowStartFromApp) {
                        throw new InvalidOperationException("Starting the sequence from the app is off in NINA (Options → Plugins → The Night Manager).");
                    }
                    Application.Current?.Dispatcher.Invoke(() => { _ = sequence.StartAdvancedSequence(true); });
                    return "Sequence started.";
            }
            throw new InvalidOperationException($"Unknown action \"{action}\".");
        }

        /// <summary>The device an action needs, when it isn't connected (stops and connecting are exempt).</summary>
        private string NotConnected(string action) {
            if (action is null || action.EndsWith(".stop", StringComparison.Ordinal) || action is "connect" or "disconnect" or "job.stop" or "sequence.start" or "dome.close") return null;
            var prefix = action.Split('.')[0];
            return prefix switch {
                "camera" when camera.GetInfo()?.Connected != true => "camera",
                "focuser" when focuser.GetInfo()?.Connected != true => "focuser",
                "filter" when filterWheel.GetInfo()?.Connected != true => "filter wheel",
                "guider" when guider.GetInfo()?.Connected != true => "guider",
                "rotator" when rotator.GetInfo()?.Connected != true => "rotator",
                "flat" when flatDevice.GetInfo()?.Connected != true => "flat panel",
                "switch" when switchHub.GetInfo()?.Connected != true => "switch hub",
                "mount" or "polar" when telescope.GetInfo()?.Connected != true => "mount",
                "dome" when dome.GetInfo()?.Connected != true => "dome",
                _ => null
            };
        }

        // MARK: Jobs

        /// <summary>Starts a job in the background (one at a time) and answers at once; the snapshot shows how it goes.</summary>
        private string Job(string action, string text, Func<CancellationToken, Task<string>> work) {
            CancellationTokenSource cts;
            lock (controlGate) {
                if (controlJob is not null) {
                    throw new InvalidOperationException($"Busy: {controlJobText}. Stop it first.");
                }
                cts = controlJob = new CancellationTokenSource();
                controlAction = action;
                controlJobText = text;
                controlStarted = DateTimeOffset.UtcNow;
            }
            NinaLog.Instance.Info($"[NightManager] From the phone: {text}");
            PushSnapshot();
            _ = Task.Run(async () => {
                string result;
                var failed = false;
                try {
                    // A device that never answers (a driver waiting on a dialog) mustn't hold the job slot: Stop frees it.
                    result = await work(cts.Token).WaitAsync(cts.Token);
                } catch (OperationCanceledException) {
                    result = "Stopped.";
                } catch (Exception ex) {
                    result = ex.Message;
                    failed = true;
                    NinaLog.Instance.Warning($"[NightManager] {text} failed: {ex.Message}");
                }
                lock (controlGate) {
                    if (ReferenceEquals(controlJob, cts)) {
                        controlJob = null;
                        controlAction = null;
                        controlJobText = null;
                        controlStarted = null;
                    }
                    lastControlAction = action;
                    lastControlResult = result;
                    lastControlFailed = failed;
                    lastControlAt = DateTimeOffset.UtcNow;
                }
                cts.Dispose();
                PushSnapshot();
            });
            return $"{text}…";
        }

        /// <summary>Stops the running job if it's of this kind (or any job for an empty prefix).</summary>
        private void CancelJob(string prefix) {
            lock (controlGate) {
                if (controlJob is not null && (prefix.Length == 0 || controlAction?.StartsWith(prefix, StringComparison.Ordinal) == true)) {
                    controlJob.Cancel();
                }
            }
        }

        /// <summary>Fire and forget, for slow things that don't block other controls (cooling, closing the dome).</summary>
        private static void Background(string what, Func<Task> work) {
            _ = Task.Run(async () => {
                try {
                    await work();
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"[NightManager] {what} failed: {ex.Message}");
                }
            });
        }

        // MARK: Devices

        private static string Device(RemoteCommand command) => EquipmentActions.Arg(command, "device") ?? "all";

        private static string ControlDeviceLabel(string device) => device switch {
            "all" => "all equipment",
            "filters" => "the filter wheel",
            "flat" => "the flat panel",
            "switch" => "the switches",
            _ => $"the {device}"
        };

        private async Task<string> ConnectAsync(string device, bool connect) {
            // NINA's own order for "connect all": the camera first, the safety monitor last.
            // Connected already: left alone (NINA's connect would drop it and connect again, killing an exposure).
            var order = new (string Id, Func<bool> IsConnected, Func<Task<bool>> Connect, Func<Task> Disconnect)[] {
                ("camera", () => camera.GetInfo()?.Connected == true, () => camera.Connect(), () => camera.Disconnect()),
                ("filters", () => filterWheel.GetInfo()?.Connected == true, () => filterWheel.Connect(), () => filterWheel.Disconnect()),
                ("focuser", () => focuser.GetInfo()?.Connected == true, () => focuser.Connect(), () => focuser.Disconnect()),
                ("rotator", () => rotator.GetInfo()?.Connected == true, () => rotator.Connect(), () => rotator.Disconnect()),
                ("mount", () => telescope.GetInfo()?.Connected == true, () => telescope.Connect(), () => telescope.Disconnect()),
                ("guider", () => guider.GetInfo()?.Connected == true, () => guider.Connect(), () => guider.Disconnect()),
                ("flat", () => flatDevice.GetInfo()?.Connected == true, () => flatDevice.Connect(), () => flatDevice.Disconnect()),
                ("dome", () => dome.GetInfo()?.Connected == true, () => dome.Connect(), () => dome.Disconnect()),
                ("switch", () => switchHub.GetInfo()?.Connected == true, () => switchHub.Connect(), () => switchHub.Disconnect()),
                ("weather", () => weather.GetInfo()?.Connected == true, () => weather.Connect(), () => weather.Disconnect()),
                ("safety", () => safety.GetInfo()?.Connected == true, () => safety.Connect(), () => safety.Disconnect()),
            };
            var chosen = device == "all" ? order : order.Where(o => o.Id == device).ToArray();
            if (chosen.Length == 0) throw new InvalidOperationException($"Unknown device \"{device}\".");
            if (!connect && device == "all") Array.Reverse(chosen);
            var failed = new List<string>();
            var done = new List<string>();
            foreach (var d in chosen) {
                try {
                    if (connect == d.IsConnected()) continue;
                    // Some drivers never answer when nothing is chosen in the profile or a window pops up on the PC.
                    if (connect) {
                        if (await d.Connect().WaitAsync(TimeSpan.FromSeconds(45))) done.Add(d.Id); else failed.Add(d.Id);
                    } else {
                        await d.Disconnect().WaitAsync(TimeSpan.FromSeconds(30));
                        done.Add(d.Id);
                    }
                } catch (TimeoutException) {
                    NinaLog.Instance.Warning($"[NightManager] {d.Id} didn't answer within the time limit");
                    failed.Add(d.Id);
                } catch (Exception ex) {
                    NinaLog.Instance.Warning($"[NightManager] {(connect ? "Connecting" : "Disconnecting")} {d.Id}: {ex.Message}");
                    failed.Add(d.Id);
                }
            }
            if (failed.Count > 0 && device != "all") throw new InvalidOperationException($"NINA couldn't {(connect ? "connect" : "disconnect")} {ControlDeviceLabel(device)} (none chosen in the profile, or it didn't answer).");
            if (device != "all") return connect ? (done.Count == 0 ? "Already connected." : "Connected.") : (done.Count == 0 ? "Already disconnected." : "Disconnected.");
            return connect
                ? failed.Count == 0 ? (done.Count == 0 ? "Everything was already connected." : $"Connected: {string.Join(", ", done)}.")
                    : $"Connected: {(done.Count == 0 ? "nothing new" : string.Join(", ", done))}. Not connected: {string.Join(", ", failed)} (none chosen in the profile, or it didn't answer)."
                : "Disconnected.";
        }

        // MARK: Camera

        private sealed record ShotSettings(double Seconds, FilterInfo Filter, short Bin, int? Gain, string Label);

        private ShotSettings Shot(RemoteCommand command) {
            var seconds = Math.Clamp(EquipmentActions.Number(command, "seconds") ?? 5, 0.001, 3600);
            var filterName = EquipmentActions.Arg(command, "filter");
            var filter = filterName is null ? null : Filter(filterName) ?? throw new InvalidOperationException($"NINA's filter wheel has no filter \"{filterName}\".");
            var bin = (short)Math.Clamp(EquipmentActions.Number(command, "bin") ?? 1, 1, 4);
            var gain = EquipmentActions.Number(command, "gain") is double g ? (int?)g : null;
            var label = $"{seconds:0.###} s" + (filter is null ? "" : $", {filter.Name}") + (bin > 1 ? $", bin {bin}" : "");
            return new ShotSettings(seconds, filter, bin, gain, label);
        }

        /// <summary>
        /// A snapshot as NINA's Imaging tab takes one with "save" ticked: it lands in NINA's image view and is saved,
        /// so the usual path sends it to the phone (graded, with its thumbnail) and to the home Wi-Fi link.
        /// </summary>
        private async Task TakeShotAsync(ShotSettings shot, CancellationToken token) {
            var progress = new Progress<ApplicationStatus>();
            var capture = new CaptureSequence(shot.Seconds, CaptureSequence.ImageTypes.SNAPSHOT, shot.Filter, new BinningMode(shot.Bin, shot.Bin), 1);
            if (shot.Gain is int gain) capture.Gain = gain;
            var exposure = await imaging.CaptureImage(capture, token, progress);
            var data = await exposure.ToImageData(progress, token);
            var prepared = imaging.PrepareImage(data, new PrepareImageParameters(true, true), token);
            await imageSave.Enqueue(data, prepared, progress, token);
        }

        private FilterInfo Filter(string name) =>
            profiles.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        // MARK: Mount

        private static (Coordinates Coordinates, string Name) Target(RemoteCommand command) {
            var ra = EquipmentActions.Number(command, "ra") ?? throw new InvalidOperationException("Which RA?");
            var dec = EquipmentActions.Number(command, "dec") ?? throw new InvalidOperationException("Which Dec?");
            if (ra < 0 || ra >= 24 || dec < -90 || dec > 90) throw new InvalidOperationException("Those coordinates aren't on the sky.");
            var name = EquipmentActions.Arg(command, "name") ?? $"RA {ra:0.000}h Dec {dec:+0.00;-0.00}°";
            return (new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.J2000), name);
        }

        private async Task UnparkIfParked(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (telescope.GetInfo()?.AtPark == true && !await telescope.UnparkTelescope(progress, token)) {
                throw new InvalidOperationException("The mount is parked and didn't unpark.");
            }
        }

        /// <summary>A fresh copy of one of NINA's instructions (Centre, Run Autofocus…), ready to run on its own.</summary>
        private T Instruction<T>() where T : ISequenceItem =>
            planLoader.Factory().GetItem<T>() ?? throw new InvalidOperationException($"NINA's {typeof(T).Name} instruction isn't available.");

        // MARK: Polar alignment

        private async Task<string> PolarAlignAsync(CancellationToken token) {
            var template = planLoader.Factory().Items.FirstOrDefault(i => i.GetType().FullName == PolarTypeName)
                ?? throw new InvalidOperationException("Install the Three Point Polar Alignment plugin in NINA first.");
            var item = (ISequenceItem)template.Clone();
            polarListener ??= new PolarListener(this);
            polarListener.Listen(messageBroker);
            SetPolar(_ => new PolarState { Running = true, At = DateTimeOffset.UtcNow, Progress = "Taking the first exposure" });
            try {
                await item.Run(new Progress<ApplicationStatus>(s => SetPolar(p => p with { Progress = s.Status })), token);
                return "Polar alignment finished.";
            } finally {
                SetPolar(p => p with { Running = false, Paused = false });
                polarListener.Stop(messageBroker);
            }
        }

        private void SetPolar(Func<PolarState, PolarState> change) {
            lock (controlGate) {
                polarState = change(polarState ?? new PolarState());
            }
            PushSnapshot();
        }

        private Task PublishPolar(string topic) => messageBroker.Publish(new NmMessage(topic, new { }));

        /// <summary>Hears the polar alignment plugin's own messages (the same ones other apps listen to).</summary>
        private sealed class PolarListener(NightManagerService owner) : ISubscriber {
            public void Listen(IMessageBroker broker) {
                broker.Subscribe(PolarErrorTopic, this);
                broker.Subscribe(PolarProgressTopic, this);
            }

            public void Stop(IMessageBroker broker) {
                broker.Unsubscribe(PolarErrorTopic, this);
                broker.Unsubscribe(PolarProgressTopic, this);
            }

            public Task OnMessageReceived(IMessage message) {
                var content = message?.Content;
                if (content is null) return Task.CompletedTask;
                var type = content.GetType();
                double? Read(string name) => type.GetProperty(name)?.GetValue(content) is double d && double.IsFinite(d) ? d : null;
                if (message.Topic == PolarErrorTopic) {
                    // Degrees in the message; arcminutes on the phone.
                    var az = Read("AzimuthError") * 60;
                    var alt = Read("AltitudeError") * 60;
                    var total = Read("TotalError") is double t ? t * 60 : az is double a && alt is double b ? Math.Sqrt(a * a + b * b) : (double?)null;
                    owner.SetPolar(p => p with { AzimuthArcmin = az, AltitudeArcmin = alt, TotalArcmin = total, ToleranceArcmin = Read("AlignmentTolerance") ?? p.ToleranceArcmin, At = DateTimeOffset.UtcNow });
                } else if (message.Topic == PolarProgressTopic) {
                    var text = type.GetProperty("Status")?.GetValue(content)?.ToString() ?? content.ToString();
                    owner.SetPolar(p => p with { Progress = text });
                }
                return Task.CompletedTask;
            }
        }

        private sealed class NmMessage(string topic, object content) : IMessage {
            public Guid SenderId { get; } = Guid.Parse("6f1b6a3e-8c1d-4c55-9d1f-2b7e5a9c0e11");
            public string Sender => "The Night Manager";
            public DateTimeOffset SentAt { get; } = DateTimeOffset.UtcNow;
            public Guid MessageId { get; } = Guid.NewGuid();
            public DateTimeOffset? Expiration => null;
            public Guid? CorrelationId => null;
            public int Version => 1;
            public IDictionary<string, object> CustomHeaders { get; } = new Dictionary<string, object>();
            public string Topic { get; } = topic;
            public object Content { get; } = content;
        }
    }
}
