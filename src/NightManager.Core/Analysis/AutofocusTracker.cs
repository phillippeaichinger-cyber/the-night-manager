using NightManager.Core.Contracts;

namespace NightManager.Core.Analysis;

/// <summary>
/// Live state of an autofocus run for the app's "watch it focus" view. NINA's own autofocus reports each point
/// as it is measured; Hocus Focus doesn't, so for it the focuser's moves are counted until its report file
/// arrives with the full curve. Finished/failed runs stay visible for a while, then drop out of the snapshot.
/// </summary>
public sealed class AutofocusTracker {
    private readonly object gate = new();
    private AutofocusState? state;
    private int? lastPosition;

    public static readonly TimeSpan KeepAfterFinish = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(15);

    public AutofocusState? Current {
        get { lock (gate) { return state; } }
    }

    public bool IsRunning {
        get { lock (gate) { return state?.Status == AutofocusStatus.Running; } }
    }

    /// <summary>A run started. Returns false if one is already running (both signals can fire for one run).</summary>
    public bool Start(DateTimeOffset now, string? engine, string? filter, double? temperature, int? focuserPosition) {
        lock (gate) {
            if (state?.Status == AutofocusStatus.Running) {
                state = state with { Engine = state.Engine ?? engine, Filter = state.Filter ?? filter };
                return false;
            }
            state = new AutofocusState {
                Status = AutofocusStatus.Running,
                StartedAt = now,
                UpdatedAt = now,
                Engine = engine,
                Filter = filter,
                Temperature = temperature,
                FocuserPosition = focuserPosition,
            };
            lastPosition = focuserPosition;
            return true;
        }
    }

    /// <summary>A measured point (NINA's own autofocus).</summary>
    public void AddPoint(double position, double value, DateTimeOffset now) {
        if (!double.IsFinite(position) || !double.IsFinite(value)) {
            return;
        }
        lock (gate) {
            if (state?.Status != AutofocusStatus.Running) {
                return;
            }
            var points = state.Points.Where(p => Math.Abs(p.Position - position) > 0.5).Append(new FocusPoint { Position = position, Value = Math.Round(value, 4) })
                .OrderBy(p => p.Position).ToList();
            state = state with { Points = points, UpdatedAt = now };
        }
    }

    /// <summary>The focuser moved; while focusing, every move is a step towards the curve.</summary>
    public bool FocuserMoved(int position, DateTimeOffset now) {
        lock (gate) {
            if (state?.Status != AutofocusStatus.Running || lastPosition == position) {
                lastPosition = position;
                return false;
            }
            lastPosition = position;
            state = state with { FocuserPosition = position, Steps = state.Steps + 1, UpdatedAt = now };
            return true;
        }
    }

    /// <summary>The sensor's focus surface measured during the run (tilt and curvature in focuser steps).</summary>
    public AutofocusState? AttachSensor(NightManager.Core.Analysis.Optics.SensorFocusReport report) {
        lock (gate) {
            if (state is null) return null;
            state = state with { Sensor = report };
            return state;
        }
    }

    /// <summary>The run's report arrived: the full curve, fit and result.</summary>
    public AutofocusState Complete(AutofocusResult result, DateTimeOffset now) {
        lock (gate) {
            var started = state?.StartedAt ?? (result.DurationSeconds is double d ? now.AddSeconds(-d) : now);
            state = new AutofocusState {
                Status = AutofocusStatus.Finished,
                StartedAt = started,
                FinishedAt = now,
                UpdatedAt = now,
                Engine = result.Engine ?? state?.Engine,
                Filter = result.Filter ?? state?.Filter,
                Temperature = result.Temperature ?? state?.Temperature,
                FocuserPosition = result.Final is { } f ? (int)Math.Round(f.Position) : state?.FocuserPosition,
                Points = result.Points.Count > 0 ? result.Points : state?.Points ?? [],
                Steps = Math.Max(state?.Steps ?? 0, result.Points.Count),
                Final = result.Final,
                FinalHfr = result.FinalHfr,
                Previous = result.Previous,
                Fit = result.Fit,
                DurationSeconds = result.DurationSeconds ?? Math.Round((now - started).TotalSeconds),
            };
            return state;
        }
    }

    /// <summary>NINA said the run finished but no report file was found: keep what we saw live.</summary>
    public AutofocusState? CompleteWithoutReport(int position, double? temperature, string? filter, DateTimeOffset now) {
        lock (gate) {
            if (state?.Status != AutofocusStatus.Running) {
                return null;
            }
            state = state with {
                Status = AutofocusStatus.Finished,
                FinishedAt = now,
                UpdatedAt = now,
                FocuserPosition = position,
                Final = new FocusPoint { Position = position, Value = state.Points.Count > 0 ? state.Points.Min(p => p.Value) : 0 },
                Temperature = temperature ?? state.Temperature,
                Filter = filter ?? state.Filter,
                DurationSeconds = Math.Round((now - state.StartedAt).TotalSeconds),
            };
            return state;
        }
    }

    /// <summary>Periodic: give up on runs that never reported, and clear old results from the snapshot.</summary>
    public AutofocusState? Tick(DateTimeOffset now, bool stillRunningInSequence) {
        lock (gate) {
            if (state is null) {
                return null;
            }
            if (state.Status == AutofocusStatus.Running && !stillRunningInSequence && now - state.UpdatedAt > GiveUpAfter) {
                state = state with { Status = AutofocusStatus.Failed, FinishedAt = now, UpdatedAt = now };
                return state;
            }
            if (state.Status != AutofocusStatus.Running && state.FinishedAt is DateTimeOffset done && now - done > KeepAfterFinish) {
                state = null;
            }
            return null;
        }
    }
}
