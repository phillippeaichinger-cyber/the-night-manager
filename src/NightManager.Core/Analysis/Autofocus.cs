using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NightManager.Core.Analysis;

/// <summary>One measured autofocus step: focuser position and the star size (HFR or FWHM) found there.</summary>
public sealed record FocusPoint {
    public double Position { get; init; }
    public double Value { get; init; }
    public double? Error { get; init; }
}

/// <summary>
/// The fitted focus curve, so the app can draw it. Hyperbolic: y = a/b·√((x−c)² + b²) + d.
/// Quadratic: y = a·x² + b·x + c (d unused).
/// </summary>
public sealed record FocusFit {
    /// <summary>hyperbolic | quadratic</summary>
    public required string Type { get; init; }
    public double A { get; init; }
    public double B { get; init; }
    public double C { get; init; }
    public double D { get; init; }
    public double? RSquared { get; init; }
}

/// <summary>What an autofocus run produced, from NINA's report file (written by NINA and by Hocus Focus).</summary>
public sealed record AutofocusResult {
    public string? Engine { get; init; }
    public string? Filter { get; init; }
    public double? Temperature { get; init; }
    public string? Method { get; init; }
    public IReadOnlyList<FocusPoint> Points { get; init; } = [];
    public FocusPoint? Initial { get; init; }
    public FocusPoint? Final { get; init; }
    public FocusPoint? Previous { get; init; }
    public double? FinalHfr { get; init; }
    public FocusFit? Fit { get; init; }
    public double? DurationSeconds { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>Reads NINA autofocus report JSON (%LOCALAPPDATA%\NINA\AutoFocus\*.json).</summary>
public static class AutofocusReport {
    public static AutofocusResult Parse(string json) {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        var root = doc.RootElement;

        var points = new List<FocusPoint>();
        if (root.TryGetProperty("MeasurePoints", out var measured) && measured.ValueKind == JsonValueKind.Array) {
            foreach (var p in measured.EnumerateArray()) {
                if (Point(p) is { } point) {
                    points.Add(point);
                }
            }
        }

        var fitType = Str(root, "Fitting")?.ToUpperInvariant();
        FocusFit? fit = null;
        if (root.TryGetProperty("Fittings", out var fittings)) {
            var rsq = root.TryGetProperty("RSquares", out var rs) ? rs : default;
            if (fitType is null or "HYPERBOLIC" or "TRENDHYPERBOLIC") {
                fit = Hyperbolic(Str(fittings, "Hyperbolic"), Num(rsq, "Hyperbolic"));
            }
            if (fit is null && fitType is null or "PARABOLIC" or "TRENDPARABOLIC" or "QUADRATIC") {
                fit = Quadratic(Str(fittings, "Quadratic"), Num(rsq, "Quadratic"));
            }
            fit ??= Hyperbolic(Str(fittings, "Hyperbolic"), Num(rsq, "Hyperbolic"));
        }

        return new AutofocusResult {
            Engine = Str(root, "AutoFocuserName"),
            Filter = Str(root, "Filter"),
            Temperature = Num(root, "Temperature"),
            Method = Str(root, "Method"),
            Points = points.OrderBy(p => p.Position).ToList(),
            Initial = root.TryGetProperty("InitialFocusPoint", out var i) ? Point(i) : null,
            Final = root.TryGetProperty("CalculatedFocusPoint", out var f) ? Point(f) : null,
            Previous = root.TryGetProperty("PreviousFocusPoint", out var pr) ? Point(pr) : null,
            FinalHfr = Num(root, "FinalHFR"),
            Fit = fit,
            DurationSeconds = Str(root, "Duration") is { } d && TimeSpan.TryParse(d, CultureInfo.InvariantCulture, out var span) ? Math.Round(span.TotalSeconds) : null,
            Timestamp = Str(root, "Timestamp") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts) ? ts : null,
        };
    }

    // "y = 2.144/54.121 * √((x - 12016.152)² + 54.121²) + -0.639"
    private static readonly Regex HyperbolicPattern = new(
        @"y\s*=\s*(?<a>-?[\d.]+)\s*/\s*(?<b>-?[\d.]+)\s*\*\s*√\(\(x\s*-\s*(?<c>-?[\d.]+)\)²\s*\+\s*(?<b2>-?[\d.]+)²\)\s*\+\s*(?<d>-?[\d.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "y = 1.2E-05 * x² + -0.29 * x + 1750.1" (NINA's quadratic text)
    private static readonly Regex QuadraticPattern = new(
        @"y\s*=\s*(?<a>-?[\d.Ee+-]+)\s*\*\s*x²\s*\+\s*(?<b>-?[\d.Ee+-]+)\s*\*\s*x\s*\+\s*(?<c>-?[\d.Ee+-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static FocusFit? Hyperbolic(string? text, double? r2) {
        var m = text is null ? null : HyperbolicPattern.Match(text);
        if (m is null || !m.Success) {
            return null;
        }
        return new FocusFit { Type = "hyperbolic", A = D(m, "a"), B = D(m, "b"), C = D(m, "c"), D = D(m, "d"), RSquared = Finite(r2) };
    }

    private static FocusFit? Quadratic(string? text, double? r2) {
        var m = text is null ? null : QuadraticPattern.Match(text);
        if (m is null || !m.Success) {
            return null;
        }
        return new FocusFit { Type = "quadratic", A = D(m, "a"), B = D(m, "b"), C = D(m, "c"), RSquared = Finite(r2) };
    }

    private static double D(Match m, string group) => double.Parse(m.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static FocusPoint? Point(JsonElement e) {
        if (e.ValueKind != JsonValueKind.Object || Num(e, "Position") is not double pos || Num(e, "Value") is not double value) {
            return null;
        }
        return new FocusPoint { Position = pos, Value = Math.Round(value, 4), Error = Num(e, "Error") is double err ? Math.Round(err, 4) : null };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) {
            return null;
        }
        return v.ValueKind switch {
            JsonValueKind.Number when v.TryGetDouble(out var d) => Finite(d),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => Finite(d),
            _ => null,
        };
    }

    private static double? Finite(double? d) => d is double x && double.IsFinite(x) ? x : null;
}
