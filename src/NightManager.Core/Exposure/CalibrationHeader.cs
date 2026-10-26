using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NightManager.Core.Exposure;

/// <summary>What a saved image is, read from its header only (FITS cards, or XISF's FITSKeyword elements).</summary>
public sealed record ImageHeader {
    public string? ImageType { get; init; }
    public double? ExposureSeconds { get; init; }
    public int? Gain { get; init; }
    public int? Offset { get; init; }
    public int Bin { get; init; } = 1;
    public string? ReadoutMode { get; init; }
    public bool Bayered { get; init; }

    /// <summary>A frame whose noise is the camera's read noise: bias, dark flat, or a dark short enough for no dark current.</summary>
    public bool IsReadNoiseFrame(double maxDarkSeconds = 10) => ImageType switch {
        "BIAS" or "DARKFLAT" or "FLATDARK" => true,
        "DARK" => ExposureSeconds is double s && s <= maxDarkSeconds,
        _ => false,
    };

    public string SettingKey => ExposureHistory.SettingKey(Gain, Offset, Bin, ReadoutMode);
}

/// <summary>
/// Reads the few header values that say which calibration frame a file is, without loading the pixels: a FITS file's
/// first header blocks, or the XML header at the start of an XISF file. Cheap enough to look at thousands of files.
/// </summary>
public static class CalibrationHeader {
    private const int MaxHeaderBytes = 128 * 1024;

    public static ImageHeader? Read(string path) {
        try {
            using var stream = File.OpenRead(path);
            var buffer = new byte[Math.Min(MaxHeaderBytes, (int)Math.Min(stream.Length, MaxHeaderBytes))];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Parse(buffer.AsSpan(0, read));
        } catch (Exception) {
            return null;
        }
    }

    public static ImageHeader? Parse(ReadOnlySpan<byte> start) {
        if (start.Length >= 8 && Encoding.ASCII.GetString(start[..8]) == "XISF0100") {
            return FromKeywords(XisfKeywords(Encoding.UTF8.GetString(start[16..])));
        }
        if (start.Length >= 80 && Encoding.ASCII.GetString(start[..6]) == "SIMPLE") {
            return FromKeywords(FitsKeywords(start));
        }
        return null;
    }

    private static Dictionary<string, string> FitsKeywords(ReadOnlySpan<byte> start) {
        var cards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; offset + 80 <= start.Length; offset += 80) {
            var card = Encoding.ASCII.GetString(start.Slice(offset, 80));
            if (card.StartsWith("END", StringComparison.Ordinal) && card[3..].Trim().Length == 0) break;
            if (card.Length > 10 && card[8] == '=') {
                cards[card[..8].Trim()] = card[10..].Split('/')[0].Trim().Trim('\'').Trim();
            }
        }
        return cards;
    }

    private static readonly Regex XisfKeyword = new("<FITSKeyword\\s+name=\"([^\"]+)\"\\s+value=\"([^\"]*)\"", RegexOptions.Compiled);

    private static Dictionary<string, string> XisfKeywords(string xml) {
        var cards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in XisfKeyword.Matches(xml)) {
            cards[m.Groups[1].Value] = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim().Trim('\'').Trim();
        }
        return cards;
    }

    private static ImageHeader? FromKeywords(Dictionary<string, string> k) {
        if (k.Count == 0) return null;
        string? Get(params string[] names) => names.Select(n => k.TryGetValue(n, out var v) && v.Length > 0 ? v : null).FirstOrDefault(v => v is not null);
        double? Number(params string[] names) => Get(names) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        var type = Get("IMAGETYP", "FRAME")?.ToUpperInvariant().Replace(" ", "").Replace("_", "").Replace("FRAME", "");
        type = type switch {
            "DARKFLAT" or "FLATDARK" => "DARKFLAT",
            "BIAS" or "OFFSET" or "ZERO" => "BIAS",
            "MASTERBIAS" or "MASTERDARK" or "MASTERFLAT" => "MASTER",
            _ => type,
        };
        var gain = Number("GAIN");
        var offset = Number("OFFSET");
        var bin = Number("XBINNING");
        return new ImageHeader {
            ImageType = type,
            ExposureSeconds = Number("EXPTIME", "EXPOSURE"),
            Gain = gain is double g ? (int)Math.Round(g) : null,
            Offset = offset is double o ? (int)Math.Round(o) : null,
            Bin = bin is double b && b >= 1 ? (int)b : 1,
            ReadoutMode = Get("READOUTM", "READMODE"),
            Bayered = Get("BAYERPAT") is { Length: > 0 },
        };
    }
}
