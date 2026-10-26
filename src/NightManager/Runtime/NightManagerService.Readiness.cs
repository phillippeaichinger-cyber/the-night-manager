using System.IO;
using System.Globalization;
using NightManager.Core.Contracts;
using NightManager.Core.Readiness;

namespace NightManager.Plugin.Runtime {

    /// <summary>
    /// Is the rig ready for tonight? Checked about an hour before the night's first planned start (or a few minutes after
    /// NINA starts, if that's later), and again once the evening's equipment has come up, compared with what came up
    /// on previous nights. Sequences that power their own equipment on (switches, scripts, smart plugs) are expected
    /// to have it off until then.
    /// </summary>
    public sealed partial class NightManagerService {
        private static readonly TimeSpan ReadinessLead = TimeSpan.FromMinutes(60);
        /// <summary>After NINA starts, give the equipment a moment before judging it.</summary>
        private static readonly TimeSpan ReadinessSettle = TimeSpan.FromMinutes(3);

        private readonly DateTimeOffset readinessStartedAt = DateTimeOffset.UtcNow;
        private readonly PowerUpWatcher powerUps = new();
        private PowerBaseline powerBaseline;
        private ReadinessReport readiness;
        private string readinessNight;
        private bool readinessSent;
        private int readinessRunning;
        private SchedulerPlanItem lastSchedulerPlan;
        private long lastSubBytes = -1;

        private string PowerBaselinePath => Path.Combine(dataDirectory, "power-baseline.json");
        private string SubSizePath => Path.Combine(dataDirectory, "last-sub-bytes.txt");

        private void TickReadiness(DateTimeOffset now) {
            var night = nightState;
            if (night?.Sunset is not { } sunset || dataDirectory is null) {
                return;
            }
            var key = NightKey(night);
            var dawn = night.AstronomicalDawn ?? night.Sunrise ?? sunset.AddHours(12);
            if (key != readinessNight) {
                readinessNight = key;
                readinessSent = false;
            }

            // Before dark, or late: once a night, a few minutes after NINA started.
            var checkAt = PlannedStart(night) - ReadinessLead;
            if (!readinessSent && now >= checkAt && now < dawn && now - readinessStartedAt >= ReadinessSettle) {
                readinessSent = true;
                var late = readinessStartedAt > checkAt + TimeSpan.FromMinutes(10);
                Run(late ? ReadinessPhases.LateStart : ReadinessPhases.BeforeDark, notify: true);
            }

            // After the equipment came up (from three hours before sunset until dawn).
            var inWindow = now >= sunset.AddHours(-3) && now < dawn;
            var imaging = lastSnapshot?.Activity?.State == ActivityStates.Imaging;
            if (powerUps.Tick(now, key, inWindow, ConnectedKinds(), imaging) is { } settled) {
                powerBaseline ??= PowerBaseline.Load(PowerBaselinePath);
                // The very first night only learns: there's nothing to compare with yet.
                if (powerBaseline.Nights.Count > 0) {
                    Run(ReadinessPhases.AfterPowerUp, notify: true);
                }
                powerBaseline.Record(key, settled);
                try {
                    powerBaseline.Save(PowerBaselinePath);
                } catch (Exception ex) {
                    NinaLog.Instance.Debug($"Power baseline not saved: {ex.Message}");
                }
            }
        }

        /// <summary>"Check now" from the app: the result shows on the setup screen (no notification).</summary>
        private string CheckReadinessNow() {
            var report = Run(ReadinessPhases.OnRequest, notify: false);
            return report?.Headline ?? "The readiness check couldn't run yet.";
        }

        private ReadinessReport Run(string phase, bool notify) {
            if (Interlocked.Exchange(ref readinessRunning, 1) == 1) {
                return readiness;
            }
            try {
                var report = ReadinessCheck.Evaluate(Gather(phase));
                readiness = report;
                // After power-up only problems are worth a notification; before dark the report itself is the point.
                var worthTelling = notify && (phase != ReadinessPhases.AfterPowerUp || report.Status != ReadinessStatus.Ok);
                var message = string.Join(" · ", report.Items.Where(i => i.Status is ReadinessStatus.Problem or ReadinessStatus.Warning).Take(3).Select(i => i.Title));
                if (message.Length == 0) {
                    message = report.Items.FirstOrDefault(i => i.Status == ReadinessStatus.Later)?.Title ?? report.Items.FirstOrDefault()?.Title ?? "";
                }
                Enqueue(Event(EventKinds.ReadinessReport,
                    report.Status == ReadinessStatus.Problem ? Severity.Warning : Severity.Info,
                    report.Headline, message,
                    new Dictionary<string, object> { ["phase"] = phase, ["status"] = report.Status, ["notify"] = worthTelling }));
                NinaLog.Instance.Info($"Readiness ({phase}): {report.Headline}");
                PushSnapshot();
                sync?.Wake();
                return report;
            } catch (Exception ex) {
                NinaLog.Instance.Warning($"Readiness check failed: {ex.Message}");
                return null;
            } finally {
                Volatile.Write(ref readinessRunning, 0);
            }
        }

        private ReadinessInput Gather(string phase) {
            powerBaseline ??= PowerBaseline.Load(PowerBaselinePath);
            var steps = SequencerReady() ? trees?.Steps() : null;
            var power = steps is null ? new PowerPlan() : PowerPlan.From(steps);
            var plan = lastSchedulerPlan;
            int? targetsTonight = plan is { PreviewAvailable: true }
                ? plan.Plan.Where(p => !p.Wait && p.End > DateTimeOffset.UtcNow).Select(p => p.TargetId ?? p.TargetName).Distinct().Count()
                : null;
            var subsTonight = plan?.Plan.Where(p => !p.Wait && p.End > DateTimeOffset.UtcNow).SelectMany(p => p.Exposures).Sum(e => e.Count) ?? 0;
            var subBytes = SubSize();
            var snapshot = lastSnapshot;
            var expected = powerBaseline.Expected().Select(e => e.Device).ToHashSet();
            var connected = ConnectedKinds();
            var findings = steps is { Count: > 0 } && phase != ReadinessPhases.AfterPowerUp
                ? SequenceCheck.Evaluate(steps, new SequenceContext {
                    HasGuider = connected.Contains("guider") || expected.Contains("guider"),
                    HasSafetyMonitor = connected.Contains("safety") || expected.Contains("safety"),
                    HasCooledCamera = snapshot?.Camera?.CoolerOn is not null || snapshot?.Camera?.TargetTemperature is not null,
                    PowersUp = power.PowersUp,
                })
                : [];
            return new ReadinessInput {
                SequenceFindings = findings,
                Phase = phase,
                Now = DateTimeOffset.UtcNow,
                Connected = ConnectedKinds(),
                Expected = powerBaseline.Expected(),
                SequenceLoaded = steps is { Count: > 0 } || sequenceState.Title is not null,
                SequenceTitle = sequenceState.Title,
                SequenceRunning = sequenceState.Running,
                Waiting = sequenceState.Waiting,
                Power = power,
                MissingScripts = power.Scripts.Where(ScriptMissing).ToList(),
                FreeBytes = storageState?.FreeBytes,
                NeededBytes = subsTonight > 0 && subBytes > 0 ? subsTonight * subBytes : null,
                SchedulerInstalled = TargetSchedulerBridge.IsInstalled,
                SchedulerTargetsTonight = targetsTonight,
                SafetyConnected = snapshot?.Safety?.Connected == true,
                IsSafe = snapshot?.Safety?.IsSafe,
            };
        }

        private IReadOnlySet<string> ConnectedKinds() {
            var s = lastSnapshot;
            var kinds = new HashSet<string>();
            if (s is null) return kinds;
            if (s.Camera?.Connected == true) kinds.Add("camera");
            if (s.Mount?.Connected == true) kinds.Add("mount");
            if (s.Guider?.Connected == true) kinds.Add("guider");
            if (s.Focuser?.Connected == true) kinds.Add("focuser");
            if (s.FilterWheel?.Connected == true) kinds.Add("filterWheel");
            if (s.Rotator?.Connected == true) kinds.Add("rotator");
            if (s.Dome?.Connected == true) kinds.Add("dome");
            if (s.Safety?.Connected == true) kinds.Add("safety");
            if (s.Weather?.Connected == true) kinds.Add("weather");
            if (s.FlatDevice?.Connected == true) kinds.Add("flatDevice");
            return kinds;
        }

        /// <summary>The night's first planned start: Target Scheduler's first stretch, or astronomical dusk.</summary>
        private DateTimeOffset PlannedStart(NightState night) {
            var dusk = night.AstronomicalDusk ?? night.NauticalDusk ?? night.Sunset!.Value.AddMinutes(90);
            var first = lastSchedulerPlan?.Plan.Where(p => !p.Wait && p.Start > night.Sunset).Select(p => (DateTimeOffset?)p.Start).Min();
            return first is { } start && start < dusk ? start : dusk;
        }

        /// <summary>The evening's local date at the observatory ("2026-10-15" for the night of the 15th to the 16th).</summary>
        private static string NightKey(NightState night) {
            var offset = TimeSpan.FromMinutes(night.UtcOffsetMinutes ?? 0);
            return night.Sunset!.Value.ToOffset(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>Only flags a script given as a full path that isn't there (a bare command may be on the PATH).</summary>
        private static bool ScriptMissing(string script) {
            try {
                var text = Environment.ExpandEnvironmentVariables(script.Trim());
                string path;
                if (text.StartsWith('"')) {
                    var end = text.IndexOf('"', 1);
                    path = end > 1 ? text[1..end] : text.Trim('"');
                } else if (File.Exists(text)) {
                    return false;
                } else {
                    var known = new[] { ".exe", ".bat", ".cmd", ".ps1", ".py", ".vbs", ".js" }
                        .Select(ext => text.IndexOf(ext + " ", StringComparison.OrdinalIgnoreCase)).Where(i => i > 0).DefaultIfEmpty(-1).Min();
                    path = known > 0 ? text[..(text.IndexOf(' ', known) is var space && space > 0 ? space : text.Length)] : text;
                }
                return Path.IsPathRooted(path) && !File.Exists(path);
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>The size of the last sub saved (kept across restarts), to estimate tonight's disk use.</summary>
        private void RememberSubSize(long bytes) {
            if (bytes <= 0 || bytes == lastSubBytes) return;
            lastSubBytes = bytes;
            try {
                File.WriteAllText(SubSizePath, bytes.ToString(CultureInfo.InvariantCulture));
            } catch (Exception) {
                // An estimate only.
            }
        }

        private long SubSize() {
            if (lastSubBytes < 0) {
                lastSubBytes = File.Exists(SubSizePath) && long.TryParse(File.ReadAllText(SubSizePath).Trim(), out var stored) ? stored : 0;
            }
            return lastSubBytes;
        }
    }
}
