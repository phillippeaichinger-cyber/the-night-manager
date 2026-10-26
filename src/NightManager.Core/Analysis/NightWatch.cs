using System.Globalization;
using NightManager.Core.Contracts;

namespace NightManager.Core.Analysis;

public sealed record NightWatchOptions {
    /// <summary>Absolute guiding RMS (arcsec) considered degraded. Null uses only the relative rule.</summary>
    public double? GuidingRmsLimit { get; init; } = 2.0;
    /// <summary>Guiding is degraded when RMS exceeds this multiple of the night's median.</summary>
    public double GuidingRelativeLimit { get; init; } = 2.0;
    public TimeSpan GuidingSustain { get; init; } = TimeSpan.FromSeconds(60);
    public double DewSpreadOpen { get; init; } = 2.0;
    public double DewSpreadClear { get; init; } = 3.0;
    public TimeSpan DewSustain { get; init; } = TimeSpan.FromMinutes(5);
    public double CoolingTolerance { get; init; } = 2.0;
    public TimeSpan CoolingSustain { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>The sensor counts as at its set point within this many °C.</summary>
    public double CoolingReachedTolerance { get; init; } = 0.5;
    /// <summary>Warn when the camera hasn't reached a settled set point after this long.</summary>
    public TimeSpan CooldownLimit { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>
    /// The set point must stay unchanged this long before it counts as "the target". NINA's Cool Camera
    /// instruction ramps the set point down in steps (20, 19, 18 … °C); those steps are not targets.
    /// </summary>
    public TimeSpan SetPointSettle { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>The sensor must hold within tolerance of the settled target this long to count as reached.</summary>
    public TimeSpan ReachedSustain { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Tell the user when the camera reaches its set point ("ready to image").</summary>
    public bool ReportCooledDown { get; init; } = true;
    public TimeSpan MinimumNoFrameWindow { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Turns raw frames and snapshots into incidents (open/resolve pairs) that are worth waking
/// someone up for. Each incident has hysteresis so it opens once and closes once, instead of
/// v1's "alert on every payload over a threshold". Not thread-safe; call from one thread.
/// </summary>
public sealed class NightWatch {
    private NightWatchOptions options;
    private readonly Func<string> newId;
    private readonly Incident clouds = new("clouds");
    private readonly Incident focusDrift = new("focus-drift");
    private readonly Incident dew = new("dew-risk");
    private readonly Incident guiding = new("guiding-degraded");
    private readonly Incident cooling = new("cooling-lost");
    private readonly Incident coolingSlow = new("cooling-slow");
    // Cooling state (see ObserveCooling).
    private double? setPoint;
    private DateTimeOffset setPointSince;
    private DateTimeOffset? cooldownStartedAt;
    private DateTimeOffset? withinSince;
    private double? reportedTarget;
    private bool atSetPoint;
    private readonly Incident noFrames = new("no-frames");
    private readonly List<double> nightGuiding = [];
    private int lowStarStreak;
    private int goodStarStreak;
    private int highHfrStreak;
    private DateTimeOffset? lastFrameAt;
    /// <summary>
    /// The run's first light frame has been saved. Before it, a sequence is still getting ready (cooling, slewing,
    /// centering, starting guiding, autofocus, waiting for the target to rise), which can take any length of time,
    /// so "no images" is never raised until imaging has actually begun.
    /// </summary>
    private bool imagingStarted;
    private double? lastExposureSeconds;

    public NightWatch(NightWatchOptions? options = null, Func<string>? newId = null) {
        this.options = options ?? new NightWatchOptions();
        this.newId = newId ?? (() => Guid.NewGuid().ToString("N"));
    }

    public string? SessionId { get; set; }

    /// <summary>
    /// The night is over ("Night ending" or "Send night report" ran): the sequence may still be parking, taking
    /// flats or warming, so not saving light frames is expected, not a stall. Cleared when a sequence starts.
    /// </summary>
    public bool NightEnded { get; set; }

    /// <summary>
    /// Whether frames should be arriving: a running sequence that isn't waiting (wait instruction or Target
    /// Scheduler between targets), parked, unsafe, or past its end of night.
    /// </summary>
    public bool ImagingExpected(ObservatorySnapshot snapshot) =>
        snapshot.Sequence?.Running == true
        && !NightEnded
        && snapshot.Sequence.Waiting is null
        && snapshot.Scheduler?.Status != SchedulerStatus.Waiting
        && snapshot.Mount?.AtPark != true
        && snapshot.Safety is not { Connected: true, IsSafe: false }
        // NINA's status bar says it's busy (slewing, centering, focusing, flipping) or waiting: not a stall.
        && snapshot.Activity?.State is not (ActivityStates.Waiting or ActivityStates.Busy);

    /// <summary>Applies changed alert settings without losing tonight's state.</summary>
    public void SetOptions(NightWatchOptions updated) => options = updated;

    /// <summary>Evaluate a freshly graded light frame.</summary>
    public IReadOnlyList<EventItem> ObserveFrame(FrameMetrics frame, GradeAnalysis analysis) {
        var events = new List<EventItem>();
        lastFrameAt = frame.Timestamp;
        lastExposureSeconds = frame.ExposureSeconds ?? lastExposureSeconds;
        if (FrameGrader.IsLight(frame.ImageType)) {
            imagingStarted = true;
        }

        if (noFrames.IsOpen) {
            events.Add(Resolve(noFrames, frame.Timestamp, EventKinds.NoFrames, "Images are being saved again"));
        }

        if (!FrameGrader.IsLight(frame.ImageType)) {
            return events;
        }

        // Clouds: star count collapses over consecutive frames, recovers over consecutive frames.
        var starsLow = frame.Stars is 0 || (analysis.StarsRatio is double sr && sr < 0.4);
        var starsGood = analysis.StarsRatio is double sg && sg > 0.7;
        lowStarStreak = starsLow ? lowStarStreak + 1 : 0;
        goodStarStreak = starsGood ? goodStarStreak + 1 : 0;

        if (!clouds.IsOpen && lowStarStreak >= 2) {
            var detail = analysis.StarsRatio is double ratio
                ? Invariant($"Star count is down {(1 - ratio) * 100:0}% over the last {lowStarStreak} frames.")
                : "No stars in the last frames.";
            events.Add(Open(clouds, frame.Timestamp, EventKinds.Clouds, Severity.Warning, "Possible clouds", detail));
        } else if (clouds.IsOpen && goodStarStreak >= 2) {
            events.Add(Resolve(clouds, frame.Timestamp, EventKinds.Clouds, "Sky looks clear again"));
        }

        // Focus drift: stars still there but getting bigger, frame after frame.
        var hfrHigh = analysis.HfrRatio is double hr && hr > 1.2 && analysis.StarsRatio is double fs && fs > 0.7;
        highHfrStreak = hfrHigh ? highHfrStreak + 1 : 0;

        if (!focusDrift.IsOpen && highHfrStreak >= 3) {
            events.Add(Open(focusDrift, frame.Timestamp, EventKinds.FocusDrift, Severity.Warning, "Focus is drifting",
                Invariant($"HFR is {(analysis.HfrRatio!.Value - 1) * 100:0}% above baseline for {highHfrStreak} frames. Consider an autofocus trigger on temperature or HFR.")));
        } else if (focusDrift.IsOpen && analysis.HfrRatio is double back && back < 1.1) {
            events.Add(Resolve(focusDrift, frame.Timestamp, EventKinds.FocusDrift, "Focus is back on target"));
        }

        return events;
    }

    /// <summary>Autofocus changes the optical state; drift is resolved by definition.</summary>
    public IReadOnlyList<EventItem> ObserveAutofocus(DateTimeOffset now) {
        highHfrStreak = 0;
        return focusDrift.IsOpen ? [Resolve(focusDrift, now, EventKinds.FocusDrift, "Autofocus completed")] : [];
    }

    /// <summary>Evaluate the latest state. Call on every snapshot and at least once a minute.</summary>
    public IReadOnlyList<EventItem> ObserveSnapshot(ObservatorySnapshot snapshot, DateTimeOffset now) {
        var events = new List<EventItem>();
        var sequenceRunning = snapshot.Sequence?.Running == true;

        // Guiding: sustained RMS above an absolute limit or well above tonight's normal.
        var guider = snapshot.Guider;

        if (guider is { Connected: true, State: "guiding", RmsTotal: double rms } && double.IsFinite(rms) && rms > 0) {
            var median = FrameGrader.Median(nightGuiding.Select(v => (double?)v));
            var bad = (options.GuidingRmsLimit is double limit && rms > limit)
                || (median is double m && nightGuiding.Count >= 30 && rms > m * options.GuidingRelativeLimit);

            if (!bad) {
                nightGuiding.Add(rms);

                if (nightGuiding.Count > 2000) {
                    nightGuiding.RemoveRange(0, 1000);
                }
            }

            switch (guiding.Update(bad, now, options.GuidingSustain, options.GuidingSustain)) {
                case Transition.Opened:
                    events.Add(Open(guiding, now, EventKinds.GuidingDegraded, Severity.Warning, "Guiding degraded",
                        Invariant($"Total RMS {rms:0.00}\" for over {options.GuidingSustain.TotalSeconds:0} s (wind, clouds or a lost star?)."),
                        new Dictionary<string, object?> { ["rmsTotal"] = Math.Round(rms, 2) }));
                    break;
                case Transition.Resolved:
                    events.Add(Resolve(guiding, now, EventKinds.GuidingDegraded, Invariant($"Guiding recovered ({rms:0.00}\")")));
                    break;
            }
        } else if (guiding.IsOpen && (guider is null || !guider.Connected || guider.State != "guiding")) {
            // Guiding stopped on purpose (flip, AF, end of sequence): close quietly.
            events.Add(Resolve(guiding, now, EventKinds.GuidingDegraded, "Guiding stopped"));
        }

        // Dew: ambient temperature close to the dew point.
        if (snapshot.Weather is { Connected: true, Temperature: double temperature, DewPoint: double dewPoint }) {
            var spread = temperature - dewPoint;
            var bad = dew.IsOpen ? spread < options.DewSpreadClear : spread < options.DewSpreadOpen;

            switch (dew.Update(bad, now, options.DewSustain, options.DewSustain)) {
                case Transition.Opened:
                    events.Add(Open(dew, now, EventKinds.DewRisk, Severity.Warning, "Dew risk",
                        Invariant($"Air temperature {temperature:0.0}°C is only {spread:0.0}°C above the dew point. Check dew heaters.")));
                    break;
                case Transition.Resolved:
                    events.Add(Resolve(dew, now, EventKinds.DewRisk, Invariant($"Dew risk cleared ({spread:0.0}°C spread)")));
                    break;
            }
        }

        ObserveCooling(snapshot.Camera, now, events);

        // Stalled sequence: running, but nothing saved for much longer than an exposure takes. Only while frames
        // are expected: waiting, parked, unsafe or end-of-night steps don't save light frames, and that's fine.
        var expectFrames = ImagingExpected(snapshot);
        if (!expectFrames && sequenceRunning) {
            if (noFrames.IsOpen) {
                events.Add(Resolve(noFrames, now, EventKinds.NoFrames, NightEnded ? "Night ended" : "Not imaging right now"));
            }
            // The wait doesn't count against the next stall check.
            lastFrameAt = now;
        }
        if (expectFrames && imagingStarted && lastFrameAt is DateTimeOffset last && !noFrames.IsOpen) {
            var expected = TimeSpan.FromSeconds((lastExposureSeconds ?? 0) * 3);
            var window = expected > options.MinimumNoFrameWindow ? expected : options.MinimumNoFrameWindow;

            if (now - last > window) {
                events.Add(Open(noFrames, now, EventKinds.NoFrames, Severity.Critical, "No images are being saved",
                    Invariant($"The sequence is running but the last image was saved {(now - last).TotalMinutes:0} minutes ago.")));
            }
        }

        if (!sequenceRunning) {
            if (noFrames.IsOpen) {
                events.Add(Resolve(noFrames, now, EventKinds.NoFrames, "Sequence stopped"));
            }

            lastFrameAt = null;
            imagingStarted = false;
        }

        return events;
    }

    /// <summary>
    /// Cooling, judged against a SETTLED set point. While the set point is still moving (NINA's Cool Camera
    /// ramps it down in steps) nothing is reported. Once it has held for <see cref="NightWatchOptions.SetPointSettle"/>:
    /// one "Camera at X °C" when the sensor has held within tolerance for <see cref="NightWatchOptions.ReachedSustain"/>,
    /// or one "can't reach" warning if it still isn't there <see cref="NightWatchOptions.CooldownLimit"/> later.
    /// Only after reaching it does drifting away count as "can't hold temperature". Cooler off resets everything.
    /// </summary>
    private void ObserveCooling(CameraState? camera, DateTimeOffset now, List<EventItem> events) {
        if (camera is not { Connected: true, CoolerOn: true, Temperature: double sensor, TargetTemperature: double target }
            || !double.IsFinite(sensor) || !double.IsFinite(target)) {
            if (camera is null || !camera.Connected || camera.CoolerOn == false) {
                if (cooling.IsOpen) {
                    events.Add(Resolve(cooling, now, EventKinds.CoolingLost, "Camera cooler switched off"));
                }
                if (coolingSlow.IsOpen) {
                    events.Add(Resolve(coolingSlow, now, EventKinds.CoolingSlow, "Camera cooler switched off"));
                }
                setPoint = null;
                cooldownStartedAt = null;
                withinSince = null;
                reportedTarget = null;
                atSetPoint = false;
            }
            return;
        }

        var within = Math.Abs(sensor - target) <= options.CoolingReachedTolerance || sensor < target;

        // A new set point (cooler just on, a ramp step, or a new target): wait for it to settle.
        if (setPoint is null || Math.Abs(setPoint.Value - target) > 0.01) {
            var first = setPoint is null;
            setPoint = target;
            setPointSince = now;
            withinSince = within ? now : null;
            atSetPoint = false;
            if (first) {
                // Plugin started (or cooler switched on) with the camera already there: say nothing.
                reportedTarget = within ? target : null;
                atSetPoint = within;
                cooldownStartedAt = within ? null : now;
            } else {
                cooldownStartedAt ??= now;
            }
            if (coolingSlow.IsOpen && reportedTarget is null) {
                events.Add(Resolve(coolingSlow, now, EventKinds.CoolingSlow, Invariant($"New set point {target:0.0}°C")));
            }
            return;
        }

        withinSince = within ? withinSince ?? now : null;
        var settled = now - setPointSince >= options.SetPointSettle;

        if (!atSetPoint) {
            if (settled && withinSince is DateTimeOffset since && now - since >= options.ReachedSustain) {
                atSetPoint = true;
                var alreadyReported = reportedTarget is double r && Math.Abs(r - target) < 0.5;
                reportedTarget = target;
                var minutes = cooldownStartedAt is DateTimeOffset started ? (now - started).TotalMinutes : (double?)null;
                cooldownStartedAt = null;

                if (coolingSlow.IsOpen) {
                    events.Add(Resolve(coolingSlow, now, EventKinds.CoolingSlow, Invariant($"Camera reached {target:0.0}°C")));
                } else if (options.ReportCooledDown && !alreadyReported) {
                    events.Add(new EventItem {
                        Id = newId(),
                        Timestamp = now,
                        Kind = EventKinds.CameraCooled,
                        Severity = Severity.Info,
                        Title = Invariant($"Camera at {target:0.0}°C"),
                        Message = minutes is double m && m >= 1
                            ? Invariant($"Reached its set point in {m:0} min. Ready to image.")
                            : "At its set point. Ready to image.",
                        SessionId = SessionId,
                        Data = new Dictionary<string, object?> { ["target"] = target, ["minutes"] = minutes is null ? null : Math.Round(minutes.Value) },
                    });
                }
            } else if (settled && !within && !coolingSlow.IsOpen && now - setPointSince >= options.CooldownLimit) {
                var power = camera.CoolerPower is double p ? Invariant($" with the cooler at {p:0}%") : "";
                events.Add(Open(coolingSlow, now, EventKinds.CoolingSlow, Severity.Warning, Invariant($"Camera can't reach {target:0.0}°C"),
                    Invariant($"After {(now - setPointSince).TotalMinutes:0} minutes at this set point the sensor is at {sensor:0.0}°C{power}. It may be too warm tonight for this set point; darks won't match until it gets there.")));
            }
            return;
        }

        // At the set point: drifting away and staying away is a real problem.
        var bad = sensor > target + options.CoolingTolerance;
        switch (cooling.Update(bad, now, options.CoolingSustain, options.CoolingSustain)) {
            case Transition.Opened:
                events.Add(Open(cooling, now, EventKinds.CoolingLost, Severity.Warning, "Camera cooling can't hold temperature",
                    Invariant($"Sensor is {sensor:0.0}°C, set point {target:0.0}°C. Darks may not match.")));
                break;
            case Transition.Resolved:
                events.Add(Resolve(cooling, now, EventKinds.CoolingLost, Invariant($"Camera back at {sensor:0.0}°C")));
                break;
        }
    }

    /// <summary>Resolve everything that is open, e.g. at session end, so incidents never dangle.</summary>
    public IReadOnlyList<EventItem> ResolveAll(DateTimeOffset now, string reason) {
        var events = new List<EventItem>();

        foreach (var (incident, kind) in new[] { (clouds, EventKinds.Clouds), (focusDrift, EventKinds.FocusDrift), (dew, EventKinds.DewRisk), (guiding, EventKinds.GuidingDegraded), (cooling, EventKinds.CoolingLost), (coolingSlow, EventKinds.CoolingSlow), (noFrames, EventKinds.NoFrames) }) {
            if (incident.IsOpen) {
                events.Add(Resolve(incident, now, kind, reason));
            }
        }

        lowStarStreak = goodStarStreak = highHfrStreak = 0;
        nightGuiding.Clear();
        return events;
    }

    private EventItem Open(Incident incident, DateTimeOffset now, string kind, string severity, string title, string message, IReadOnlyDictionary<string, object?>? data = null) {
        incident.ForceOpen();
        return new EventItem {
            Id = newId(),
            Timestamp = now,
            Kind = kind,
            Severity = severity,
            Title = title,
            Message = message,
            SessionId = SessionId,
            IncidentKey = incident.Key,
            IncidentPhase = "open",
            Data = data
        };
    }

    private EventItem Resolve(Incident incident, DateTimeOffset now, string kind, string title) {
        incident.ForceClose();
        return new EventItem {
            Id = newId(),
            Timestamp = now,
            Kind = kind,
            Severity = Severity.Info,
            Title = title,
            SessionId = SessionId,
            IncidentKey = incident.Key,
            IncidentPhase = "resolve"
        };
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private enum Transition {
        None,
        Opened,
        Resolved
    }

    /// <summary>An open/close state with time-based hysteresis in both directions.</summary>
    private sealed class Incident(string key) {
        private DateTimeOffset? badSince;
        private DateTimeOffset? goodSince;

        public string Key { get; } = key;

        public bool IsOpen { get; private set; }

        public Transition Update(bool bad, DateTimeOffset now, TimeSpan openAfter, TimeSpan clearAfter) {
            if (bad) {
                goodSince = null;
                badSince ??= now;

                if (!IsOpen && now - badSince.Value >= openAfter) {
                    IsOpen = true;
                    return Transition.Opened;
                }
            } else {
                badSince = null;
                goodSince ??= now;

                if (IsOpen && now - goodSince.Value >= clearAfter) {
                    IsOpen = false;
                    return Transition.Resolved;
                }
            }

            return Transition.None;
        }

        public void ForceOpen() {
            IsOpen = true;
            goodSince = null;
        }

        public void ForceClose() {
            IsOpen = false;
            badSince = null;
            goodSince = null;
        }
    }
}
