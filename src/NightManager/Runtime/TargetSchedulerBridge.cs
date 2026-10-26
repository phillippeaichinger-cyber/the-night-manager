using NightManager.Core.Contracts;
using NightManager.Core.Scheduler;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Net.Http;

namespace NightManager.Plugin.Runtime {
    /// <summary>
    /// Everything Night Manager reads from the Target Scheduler plugin (TS 4.8+/5.x), without referencing it:
    /// live state from its message-broker topics, projects and progress from its database (read-only, through
    /// the SQLite library NINA ships), and tonight's plan from its own planner via its local API (when enabled).
    /// </summary>
    internal sealed class TargetSchedulerBridge : ISubscriber {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

        private readonly IMessageBroker broker;
        private readonly SchedulerTracker tracker = new();
        private readonly Action<SchedulerEventInfo> onEvent;
        private readonly Action onChanged;
        private bool subscribed;

        public TargetSchedulerBridge(IMessageBroker broker, Action<SchedulerEventInfo> onEvent, Action onChanged) {
            this.broker = broker;
            this.onEvent = onEvent;
            this.onChanged = onChanged;
        }

        public static string DatabasePath => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "SchedulerPlugin", "schedulerdb.sqlite");

        /// <summary>TS is installed for this NINA (its database exists or its assembly is loaded).</summary>
        public static bool IsInstalled => File.Exists(DatabasePath) || SchedulerAssemblyVersion() is not null;

        public SchedulerState Current => tracker.Current;

        /// <summary>Plain-words status for the options page.</summary>
        public string LastPlanNote { get; private set; }

        public void Start() {
            if (broker is null || subscribed) {
                return;
            }
            foreach (var topic in SchedulerTracker.Topics) {
                broker.Subscribe(topic, this);
            }
            subscribed = true;
        }

        public void Stop() {
            if (broker is null || !subscribed) {
                return;
            }
            foreach (var topic in SchedulerTracker.Topics) {
                try {
                    broker.Unsubscribe(topic, this);
                } catch (Exception) {
                }
            }
            subscribed = false;
        }

        public Task OnMessageReceived(IMessage message) {
            try {
                var headers = message.CustomHeaders?.ToDictionary(h => h.Key, h => (object)h.Value) ?? new Dictionary<string, object>();
                double? ra = null, dec = null;
                if (headers.TryGetValue("Coordinates", out var c) && c is Coordinates coordinates) {
                    var j2000 = coordinates.Transform(Epoch.J2000);
                    ra = Math.Round(j2000.RA, 5);
                    dec = Math.Round(j2000.Dec, 4);
                }

                var info = tracker.Apply(new SchedulerMessage {
                    Topic = message.Topic,
                    Content = message.Content,
                    Expiration = message.Expiration,
                    Headers = headers,
                    RightAscension = ra,
                    Declination = dec,
                }, DateTimeOffset.UtcNow);

                if (info is not null) {
                    onEvent(info);
                }
                onChanged();
            } catch (Exception ex) {
                NinaLog.Instance.Debug($"Target Scheduler message {message?.Topic} not understood: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        // MARK: Projects and tonight's plan

        /// <summary>TS's plan from a given local start time (null when its API is off or doesn't answer).</summary>
        public async Task<IReadOnlyList<PlanEntry>> PreviewAsync(string profileId, DateTime startLocal, CancellationToken token) {
            if (!File.Exists(DatabasePath)) {
                return null;
            }
            var data = ReadDatabase(profileId);
            if (!data.ApiEnabled) {
                return null;
            }
            var start = startLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            var url = $"http://localhost:{data.ApiPort}/ts/v0/profiles/{Uri.EscapeDataString(profileId)}/preview?startTime={Uri.EscapeDataString(start)}";
            var json = await Http.GetStringAsync(url, token);
            return SchedulerPreview.Parse(json, data.ProjectOfTarget);
        }

        /// <summary>Reads projects (with progress) for this profile and, if TS's API is on, tonight's plan.</summary>
        public async Task<SchedulerPlanItem> BuildPlanAsync(string profileId, CancellationToken token) {
            if (!File.Exists(DatabasePath)) {
                return null;
            }

            var data = ReadDatabase(profileId);
            IReadOnlyList<PlanEntry> plan = [];
            string reason = null;
            DateTimeOffset? generated = null;

            if (data.ApiEnabled) {
                try {
                    var start = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                    var url = $"http://localhost:{data.ApiPort}/ts/v0/profiles/{Uri.EscapeDataString(profileId)}/preview?startTime={Uri.EscapeDataString(start)}";
                    var json = await Http.GetStringAsync(url, token);
                    plan = SchedulerPreview.Parse(json, data.ProjectOfTarget);
                    generated = DateTimeOffset.UtcNow;
                } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) {
                    reason = $"Target Scheduler's API didn't answer on port {data.ApiPort}. Check it's enabled in Target Scheduler → Profile Preferences, then restart NINA.";
                    NinaLog.Instance.Debug($"Target Scheduler preview failed: {ex.Message}");
                }
            } else {
                reason = "Turn on Target Scheduler's API (Target Scheduler → Profile Preferences → API) to see tonight's plan.";
            }

            LastPlanNote = reason ?? (plan.Count == 0 ? "Tonight's plan is empty: no active target is available tonight." : $"Tonight's plan: {plan.Count(p => !p.Wait)} target stretch(es).");

            return new SchedulerPlanItem {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTimeOffset.UtcNow,
                SchedulerVersion = SchedulerAssemblyVersion(),
                PreviewAvailable = reason is null,
                PreviewUnavailableReason = reason,
                PreviewGeneratedAt = generated,
                Plan = plan,
                Projects = data.Projects,
                DelayGradingPercent = data.DelayGrading,
            };
        }

        private sealed record DatabaseData(bool ApiEnabled, int ApiPort, double? DelayGrading, IReadOnlyList<SchedulerProject> Projects, IReadOnlyDictionary<string, string> ProjectOfTarget);

        private static readonly string[] States = ["draft", "active", "inactive", "closed"];
        private static readonly string[] Priorities = ["low", "normal", "high"];

        private static DatabaseData ReadDatabase(string profileId) {
            // Read-only, no pooling: TS keeps writing while NINA images; we only ever read.
            using var connection = new SQLiteConnection($"Data Source={DatabasePath};Read Only=True;Pooling=False;");
            connection.Open();
            connection.BusyTimeout = 5000;

            var apiEnabled = false;
            var apiPort = 8188;
            double? delayGrading = null;
            var prefColumns = Columns(connection, "profilepreference");
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = "select * from profilepreference where profileId = @p";
                cmd.Parameters.AddWithValue("@p", profileId);
                using var r = cmd.ExecuteReader();
                if (r.Read()) {
                    if (prefColumns.Contains("enableAPI")) apiEnabled = Convert.ToInt64(r["enableAPI"]) != 0;
                    if (prefColumns.Contains("apiPort")) apiPort = Convert.ToInt32(r["apiPort"]);
                    if (prefColumns.Contains("delayGrading")) delayGrading = Convert.ToDouble(r["delayGrading"]);
                }
            }

            var projects = new List<(long Id, SchedulerProject Project)>();
            var projectColumns = Columns(connection, "project");
            double? Number(SQLiteDataReader r, string column) =>
                projectColumns.Contains(column) && r[column] is not DBNull ? Convert.ToDouble(r[column], CultureInfo.InvariantCulture) : null;
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = "select * from project where profileId = @p and state = 1 order by priority desc, name";
                cmd.Parameters.AddWithValue("@p", profileId);
                using var r = cmd.ExecuteReader();
                while (r.Read()) {
                    var state = Convert.ToInt32(r["state"]);
                    var priority = Convert.ToInt32(r["priority"]);
                    projects.Add((r.GetInt64(0), new SchedulerProject {
                        Id = r["guid"] as string ?? r.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                        Name = r["name"] as string ?? "Project",
                        State = state >= 0 && state < States.Length ? States[state] : "unknown",
                        Priority = priority >= 0 && priority < Priorities.Length ? Priorities[priority] : "normal",
                        MinimumAltitude = Number(r, "minimumaltitude"),
                        MaximumAltitude = Number(r, "maximumAltitude"),
                        UseCustomHorizon = Number(r, "usecustomhorizon") is double custom ? custom != 0 : null,
                        HorizonOffset = Number(r, "horizonoffset"),
                        MinimumTimeMinutes = Number(r, "minimumtime"),
                        MeridianWindowMinutes = Number(r, "meridianwindow"),
                    }));
                }
            }

            var planColumns = Columns(connection, "exposureplan");
            var exposures = new Dictionary<long, List<SchedulerExposurePlan>>();
            var targets = new Dictionary<long, List<SchedulerTarget>>();
            var projectOfTarget = new Dictionary<string, string>();

            if (projects.Count > 0) {
                var ids = string.Join(",", projects.Select(p => p.Id));
                using (var cmd = connection.CreateCommand()) {
                    cmd.CommandText = $"""
                        select e.targetid, t.filtername, e.exposure, t.defaultexposure, e.desired, e.acquired, e.accepted{(planColumns.Contains("enabled") ? ", e.enabled" : "")}
                        from exposureplan e join exposuretemplate t on t.Id = e.exposureTemplateId
                        where e.targetid in (select Id from target where projectid in ({ids}))
                        order by e.Id
                        """;
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) {
                        var exposure = Convert.ToDouble(r["exposure"]);
                        var plan = new SchedulerExposurePlan {
                            Filter = r["filtername"] as string ?? "?",
                            ExposureSeconds = exposure > 0 ? exposure : Convert.ToDouble(r["defaultexposure"]),
                            Desired = Convert.ToInt32(r["desired"]),
                            Acquired = Convert.ToInt32(r["acquired"]),
                            Accepted = Convert.ToInt32(r["accepted"]),
                            Enabled = !planColumns.Contains("enabled") || Convert.ToInt64(r["enabled"]) != 0,
                        };
                        var targetId = r.GetInt64(0);
                        (exposures.TryGetValue(targetId, out var list) ? list : exposures[targetId] = []).Add(plan);
                    }
                }

                using (var cmd = connection.CreateCommand()) {
                    cmd.CommandText = $"select Id, guid, name, active, ra, dec, rotation, projectid from target where projectid in ({ids}) order by name";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) {
                        var id = r.GetInt64(0);
                        var guid = r["guid"] as string ?? id.ToString(CultureInfo.InvariantCulture);
                        var projectId = Convert.ToInt64(r["projectid"]);
                        var target = new SchedulerTarget {
                            Id = guid,
                            Name = r["name"] as string ?? "Target",
                            Active = Convert.ToInt64(r["active"]) != 0,
                            RightAscension = Convert.ToDouble(r["ra"]),
                            Declination = Convert.ToDouble(r["dec"]),
                            Rotation = r["rotation"] is DBNull ? 0 : Convert.ToDouble(r["rotation"]),
                            Exposures = exposures.TryGetValue(id, out var plans) ? plans : [],
                        };
                        (targets.TryGetValue(projectId, out var list) ? list : targets[projectId] = []).Add(target);
                        var projectName = projects.FirstOrDefault(p => p.Id == projectId).Project?.Name;
                        if (projectName is not null) {
                            projectOfTarget[guid] = projectName;
                        }
                    }
                }
            }

            var result = projects
                .Select(p => p.Project with { Targets = targets.TryGetValue(p.Id, out var t) ? t : [] })
                .ToList();
            return new DatabaseData(apiEnabled, apiPort, delayGrading, result, projectOfTarget);
        }

        private static HashSet<string> Columns(SQLiteConnection connection, string table) {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"pragma table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) {
                columns.Add(r["name"] as string);
            }
            return columns;
        }

        private static string SchedulerAssemblyVersion() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName())
                .FirstOrDefault(n => n.Name is { } name && name.StartsWith("NINA.Plugin.TargetScheduler", StringComparison.Ordinal)
                    && !name.EndsWith(".Shared", StringComparison.Ordinal) && !name.EndsWith(".SyncService", StringComparison.Ordinal))
                ?.Version?.ToString();
    }
}
