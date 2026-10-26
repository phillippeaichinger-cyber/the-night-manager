using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NightManager.Core.Contracts;
using NightManager.Core.Json;

namespace NightManager.Core.Live;

/// <summary>
/// A hash of what matters in a snapshot. Values that tick all the time (sensor temperature to the hundredth, the
/// mount's altitude while tracking, upload rates) are rounded or left out, so the record is only rewritten when
/// something a person would notice changed. While someone watches, the live lane carries the exact values.
/// </summary>
public static class SnapshotSignature {
    /// <summary>Property name → rounding step (0 = leave out).</summary>
    private static readonly Dictionary<string, double> Steps = new(StringComparer.OrdinalIgnoreCase) {
        ["temperature"] = 0.5, ["coolerPower"] = 10, ["dewPoint"] = 1, ["humidity"] = 5, ["pressure"] = 5,
        ["windSpeed"] = 2, ["windGust"] = 2, ["windDirection"] = 0, ["cloudCover"] = 10, ["skyBrightness"] = 0,
        ["skyQuality"] = 0.5, ["skyTemperature"] = 2, ["rainRate"] = 0.5,
        ["altitude"] = 0, ["azimuth"] = 10, ["siderealTime"] = 0, ["hoursToMeridian"] = 0.25,
        ["rightAscension"] = 0.01, ["declination"] = 0.1,
        ["rmsRa"] = 0.25, ["rmsDec"] = 0.25, ["rmsTotal"] = 0.25, ["peakRa"] = 0, ["peakDec"] = 0,
        ["mechanicalPosition"] = 1, ["uploadBytesPerSecond"] = 512 * 1024, ["downloadBytesPerSecond"] = 512 * 1024,
        ["toUploadBytes"] = 100e6, ["toDownloadBytes"] = 100e6, ["freeBytes"] = 1e9, ["sessionBytes"] = 1e9,
        ["progress"] = 0.05,
    };

    public static string Compute(ObservatorySnapshot snapshot) {
        var node = JsonNode.Parse(NmJson.Serialize(snapshot));
        Quantize(node);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? ""));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }

    private static void Quantize(JsonNode? node) {
        switch (node) {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList()) {
                    if (value is JsonValue v && Steps.TryGetValue(key, out var step) && v.TryGetValue<double>(out var number)) {
                        if (step == 0) {
                            obj.Remove(key);
                        } else {
                            obj[key] = Math.Round(number / step) * step;
                        }
                    } else {
                        Quantize(value);
                    }
                }
                break;
            case JsonArray array:
                foreach (var item in array) {
                    Quantize(item);
                }
                break;
        }
    }
}
