using NightManager.Core.Settings;
using NightManager.Core.Contracts;
using NightManager.Core.Sync;
using System.Globalization;

namespace NightManager.Plugin.Runtime {

    /// <summary>The next 7 nights from Target Scheduler, so the app knows what's coming even while the PC is off.</summary>
    public sealed partial class NightManagerService {
        private static readonly TimeSpan ForecastEvery = TimeSpan.FromHours(3);
        private const int ForecastNights = 7;
        private DateTimeOffset nextForecast = DateTimeOffset.UtcNow.AddMinutes(2);
        private int forecasting;

        private void MaybeSendForecast(DateTimeOffset now) {
            if (scheduler is null || !TargetSchedulerBridge.IsInstalled || sync?.DataOffKnown != true || !DataOn(DataFeatures.Scheduler) || now < nextForecast || Interlocked.Exchange(ref forecasting, 1) == 1) {
                return;
            }
            nextForecast = now + ForecastEvery;
            var profileId = RigId;
            var token = lifetime?.Token ?? CancellationToken.None;
            _ = Task.Run(async () => {
                try {
                    var nights = new List<ForecastNight>();
                    for (var day = 0; day < ForecastNights; day++) {
                        var noon = DateTime.Today.AddDays(day).AddHours(12);
                        var data = nighttime.Calculate(noon);
                        var dusk = data.TwilightRiseAndSet?.Set ?? data.NauticalTwilightRiseAndSet?.Set ?? data.SunRiseAndSet?.Set;
                        var dawn = data.TwilightRiseAndSet?.Rise ?? data.NauticalTwilightRiseAndSet?.Rise ?? data.SunRiseAndSet?.Rise;
                        if (dusk is not DateTime start) {
                            continue;
                        }
                        // Tonight's preview starts now if the night has begun; later nights at their dusk.
                        var from = day == 0 && DateTime.Now > start ? DateTime.Now : start;
                        var plan = await scheduler.PreviewAsync(profileId, from, token);
                        if (plan is null) {
                            return; // TS's API is off: nothing to forecast with
                        }
                        nights.Add(new ForecastNight {
                            Date = noon.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            Dusk = Utc(start),
                            Dawn = dawn is DateTime d && d < start ? Utc(d.AddDays(1)) : Utc(dawn),
                            MoonIllumination = data.Illumination is double illumination ? Math.Round(illumination, 3) : null,
                            Plan = plan,
                        });
                    }
                    Enqueue(new SchedulerForecastItem { Id = SyncEngine.NewId(), Timestamp = DateTimeOffset.UtcNow, Nights = nights });
                    NinaLog.Instance.Info($"Target Scheduler forecast: {nights.Count} nights, {nights.Sum(n => n.Plan.Count(p => !p.Wait))} target stretches.");
                } catch (OperationCanceledException) {
                } catch (Exception ex) {
                    NinaLog.Instance.Debug($"Target Scheduler forecast skipped: {ex.Message}");
                } finally {
                    Volatile.Write(ref forecasting, 0);
                }
            });
        }
    }
}
