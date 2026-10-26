using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using NightManager.Core.Contracts;

namespace NightManager.Core.Live;

/// <summary>One guide step for the live graph: when, and the RA/Dec error in arcseconds (pixels when the scale is unknown).</summary>
/// <summary>One guider correction: error in arcseconds and the pulse sent (ms, signed: west/north positive, as NINA reports).</summary>
public readonly record struct LiveGuideStep(DateTimeOffset At, double Ra, double Dec, double RaPulseMs = 0, double DecPulseMs = 0);

/// <summary>
/// What the live lane carries while someone watches: small sections (camera, mount, guiding…) with exact values,
/// sent only when a section changed, plus the guide steps since the last send. Short keys keep it to a few hundred
/// bytes a second, which is what the live lane is billed on (data downloaded by the watching phone).
/// </summary>
public sealed class LiveTelemetry {
    private readonly ConcurrentQueue<LiveGuideStep> steps = new();
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);
    private DateTimeOffset lastSent = DateTimeOffset.MinValue;

    /// <summary>Guide steps are buffered between sends (at most 120, about four minutes of guiding).</summary>
    public void AddGuideStep(LiveGuideStep step) {
        steps.Enqueue(step);
        while (steps.Count > 120 && steps.TryDequeue(out _)) {
        }
    }

    /// <summary>Forget what was sent: the next update carries everything (someone new started watching).</summary>
    public void Reset() {
        sent.Clear();
        lastSent = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// The changed sections since the last call (plus new guide steps), or null when nothing changed and the
    /// keep-alive isn't due yet. "t" tells the app how fresh the lane is.
    /// </summary>
    public JsonObject? NextUpdate(ObservatorySnapshot snapshot, DateTimeOffset now, TimeSpan keepAlive) {
        var update = new JsonObject();
        foreach (var (key, section) in Sections(snapshot)) {
            var json = section?.ToJsonString() ?? "null";
            if (!sent.TryGetValue(key, out var previous) || previous != json) {
                update[key] = section;
                sent[key] = json;
            }
        }

        var newSteps = new JsonArray();
        while (steps.TryDequeue(out var step)) {
            // [time, RA″, Dec″, RA pulse ms, Dec pulse ms]: older apps read the first three only.
            newSteps.Add(new JsonArray(step.At.ToUnixTimeMilliseconds(), Math.Round(step.Ra, 2), Math.Round(step.Dec, 2),
                (int)Math.Round(step.RaPulseMs), (int)Math.Round(step.DecPulseMs)));
        }
        if (newSteps.Count > 0) {
            update["gs"] = newSteps;
        }

        if (update.Count == 0 && now - lastSent < keepAlive) {
            return null;
        }
        update["t"] = now.ToUnixTimeMilliseconds();
        lastSent = now;
        return update;
    }

    /// <summary>The sections, each a small object (null when the device isn't there).</summary>
    public static IEnumerable<(string Key, JsonNode? Section)> Sections(ObservatorySnapshot s) {
        yield return ("st", s.Activity is { } a ? Obj(
            ("s", a.State), ("h", a.Headline), ("d", a.Detail), ("u", a.Until?.ToUnixTimeMilliseconds()), ("p", a.Progress),
            ("tg", s.Sequence?.TargetName), ("run", s.Sequence?.Running)) : null);

        yield return ("cam", s.Camera is { Connected: true } c ? Obj(
            ("st", c.State), ("tmp", R(c.Temperature, 1)), ("set", c.TargetTemperature), ("pow", R(c.CoolerPower, 0)), ("cool", c.CoolerOn),
            ("exS", c.ExposureStartedAt?.ToUnixTimeMilliseconds()), ("exE", c.ExposureEndsAt?.ToUnixTimeMilliseconds()),
            ("f", s.FilterWheel?.CurrentFilter)) : null);

        yield return ("mnt", s.Mount is { Connected: true } m ? Obj(
            ("ra", R(m.RightAscension, 4)), ("dec", R(m.Declination, 3)), ("alt", R(m.Altitude, 1)), ("az", R(m.Azimuth, 1)),
            ("pier", m.SideOfPier), ("trk", m.Tracking), ("slew", m.Slewing), ("park", m.AtPark), ("mer", R(m.HoursToMeridian, 3))) : null);

        yield return ("gd", s.Guider is { Connected: true } g ? Obj(
            ("st", g.State), ("rms", R(g.RmsTotal, 2)), ("ra", R(g.RmsRa, 2)), ("dec", R(g.RmsDec, 2)), ("sc", R(g.PixelScale, 3))) : null);

        yield return ("foc", s.Focuser is { Connected: true } f ? Obj(
            ("pos", f.Position), ("tmp", R(f.Temperature, 1)), ("mov", f.Moving)) : null);

        yield return ("rot", s.Rotator is { Connected: true } r ? Obj(("pos", R(r.Position, 1)), ("mov", r.Moving)) : null);

        yield return ("dome", s.Dome is { Connected: true } d ? Obj(("az", R(d.Azimuth, 1)), ("sh", d.Shutter), ("slew", d.Slewing)) : null);

        yield return ("wx", s.Weather is { Connected: true } w ? Obj(
            ("cl", R(w.CloudCover, 0)), ("hum", R(w.Humidity, 0)), ("wind", R(w.WindSpeed, 1)), ("gust", R(w.WindGust, 1)),
            ("rain", R(w.RainRate, 1)), ("tmp", R(w.Temperature, 1)), ("dew", R(w.DewPoint, 1)), ("sqm", R(w.SkyQuality, 2))) : null);

        yield return ("sf", s.Safety is { Connected: true } safety ? Obj(("safe", safety.IsSafe)) : null);

        yield return ("ses", s.Session is { } ses ? Obj(("n", ses.Frames), ("ok", ses.Accepted), ("int", R(ses.IntegrationSeconds, 0))) : null);
    }

    private static double? R(double? value, int digits) => value is { } v && double.IsFinite(v) ? Math.Round(v, digits) : null;

    private static JsonObject Obj(params (string Key, object? Value)[] fields) {
        var obj = new JsonObject();
        foreach (var (key, value) in fields) {
            JsonNode? node = value switch {
                null => null,
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                int whole => JsonValue.Create(whole),
                long big => JsonValue.Create(big),
                double number => JsonValue.Create(number),
                _ => JsonValue.Create(value.ToString()),
            };
            if (node is not null) {
                obj[key] = node;
            }
        }
        return obj;
    }
}
