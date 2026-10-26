using NightManager.Core.Contracts;

namespace NightManager.Core.Cameras;

public enum CaptureReason {
    /// <summary>Someone has the app open.</summary>
    Watch,
    /// <summary>The camera's own schedule.</summary>
    Schedule,
    /// <summary>A kit error: captured at once, whatever the interval; the interval restarts from it.</summary>
    Alert
}

/// <summary>
/// Decides when each camera takes a picture. Never more often than the camera's interval (itself never below
/// three minutes), except for a kit error, which forces a picture at once; after that the interval starts again.
/// </summary>
public sealed class CameraScheduler {
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> lastCapture = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> retryAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> lastAlertCapture = new(StringComparer.Ordinal);
    private (string Cause, DateTimeOffset At, bool Safety)? pendingAlert;
    private readonly HashSet<string> alertDone = new(StringComparer.Ordinal);

    /// <summary>The cause of the kit error waiting to be pictured, if any.</summary>
    public string? PendingAlertCause {
        get { lock (gate) { return pendingAlert?.Cause; } }
    }

    /// <summary>
    /// A kit error happened: every enabled camera takes a picture on its next check, except all-sky cameras, which
    /// only answer a safety change (the sky is the story then; for a stuck mount or a failed download it isn't).
    /// </summary>
    public void RaiseAlert(string cause, DateTimeOffset now, bool safety = false) {
        lock (gate) {
            // A safety alert still pending stays a safety alert when a kit error joins it.
            var keepSafety = pendingAlert is { Safety: true };
            pendingAlert = (string.IsNullOrWhiteSpace(cause) ? "Kit error" : cause, now, safety || keepSafety);
            alertDone.Clear();
        }
    }

    /// <summary>Whether this camera should take a picture now, and why.</summary>
    public CaptureReason? Due(CameraSettings camera, DateTimeOffset now, bool watched) {
        if (!camera.Enabled) {
            return null;
        }
        lock (gate) {
            if (retryAt.TryGetValue(camera.Id, out var retry) && now < retry) {
                return null;
            }
            if (pendingAlert is { } alert && !alertDone.Contains(camera.Id)) {
                // A burst of errors gives one picture: skip if this camera was forced very recently.
                if (camera.AllSky && !alert.Safety) {
                    alertDone.Add(camera.Id);
                } else if (lastAlertCapture.TryGetValue(camera.Id, out var lastAlert) && now - lastAlert < CameraPolicy.AlertCoalesce) {
                    alertDone.Add(camera.Id);
                } else {
                    return CaptureReason.Alert;
                }
            }
            var interval = TimeSpan.FromMinutes(Math.Max(camera.IntervalMinutes, CameraPolicy.MinimumIntervalMinutes));
            if (lastCapture.TryGetValue(camera.Id, out var last) && now - last < interval) {
                return null;
            }
            return camera.Mode switch {
                CameraCaptureMode.Scheduled => CaptureReason.Schedule,
                _ when watched => CaptureReason.Watch,
                _ => null,
            };
        }
    }

    /// <summary>A picture was taken (and queued): the interval starts again from now.</summary>
    public void Captured(CameraSettings camera, CaptureReason reason, DateTimeOffset now) {
        lock (gate) {
            lastCapture[camera.Id] = now;
            retryAt.Remove(camera.Id);
            if (reason == CaptureReason.Alert) {
                lastAlertCapture[camera.Id] = now;
                alertDone.Add(camera.Id);
            }
        }
    }

    /// <summary>The camera couldn't be reached: try again in a minute (an alert is not retried).</summary>
    public void Failed(CameraSettings camera, CaptureReason reason, DateTimeOffset now) {
        lock (gate) {
            retryAt[camera.Id] = now + CameraPolicy.RetryAfterFailure;
            if (reason == CaptureReason.Alert) {
                alertDone.Add(camera.Id);
            }
        }
    }

    /// <summary>Every enabled camera has dealt with the pending alert: forget it.</summary>
    public void SettleAlert(IEnumerable<CameraSettings> cameras) {
        lock (gate) {
            if (pendingAlert is not null && cameras.Where(c => c.Enabled).All(c => alertDone.Contains(c.Id))) {
                pendingAlert = null;
            }
        }
    }

    public DateTimeOffset? LastCapture(string cameraId) {
        lock (gate) {
            return lastCapture.TryGetValue(cameraId, out var at) ? at : null;
        }
    }

    /// <summary>
    /// Kit errors that force a picture: equipment lost, failed instructions, images not saved, cooling lost,
    /// the observatory turning unsafe, and anything the plugin marks critical. Phone instructions and the
    /// camera's own events never count.
    /// </summary>
    public static bool IsSafetyAlert(EventItem e) => e.Kind == EventKinds.SafetyChanged;

    public static bool IsKitError(EventItem e) {
        if (e.Kind.StartsWith("user.", StringComparison.Ordinal) || e.Kind == EventKinds.CameraSnapshot) {
            return false;
        }
        if (e.IncidentPhase == "resolve") {
            return false;
        }
        return e.Severity == Severity.Critical || e.Kind switch {
            EventKinds.DeviceDisconnected => e.Severity != Severity.Info,
            EventKinds.SequenceItemFailed or EventKinds.SequenceFailureBurst => e.Severity != Severity.Info,
            EventKinds.ImageSaveFailed or EventKinds.DownloadTimeout or EventKinds.CoolingLost => true,
            EventKinds.SafetyChanged => e.Severity != Severity.Info,
            _ => false,
        };
    }
}
