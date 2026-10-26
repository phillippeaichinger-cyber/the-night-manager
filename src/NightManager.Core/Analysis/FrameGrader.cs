using System.Globalization;
using NightManager.Core.Contracts;

namespace NightManager.Core.Analysis;

/// <summary>Host-independent description of one saved frame, filled by the NINA adapter.</summary>
public sealed record FrameMetrics {
    public required DateTimeOffset Timestamp { get; init; }
    public required string ImageType { get; init; }
    public string? Target { get; init; }
    public string? Filter { get; init; }
    public double? ExposureSeconds { get; init; }
    public string? Binning { get; init; }
    public double? Hfr { get; init; }
    public int? Stars { get; init; }
    public double? Median { get; init; }
    public double? GuidingRmsTotal { get; init; }
}

public enum GradingSensitivity {
    Relaxed,
    Normal,
    Strict
}

public sealed record GradingThresholds {
    public double HfrMarginalRatio { get; init; } = 1.15;
    public double HfrRejectRatio { get; init; } = 1.35;
    /// <summary>HFR changes smaller than this (pixels) are treated as noise regardless of ratio.</summary>
    public double HfrMinimumDelta { get; init; } = 0.25;
    public double StarsMarginalRatio { get; init; } = 0.70;
    public double StarsRejectRatio { get; init; } = 0.45;
    public double GuidingMarginalRatio { get; init; } = 1.6;
    public double GuidingRejectRatio { get; init; } = 2.2;
    public double BackgroundMarginalRatio { get; init; } = 1.6;
    public int BaselineWindow { get; init; } = 15;
    public int WarmupFrames { get; init; } = 3;

    public static GradingThresholds For(GradingSensitivity sensitivity) => sensitivity switch {
        GradingSensitivity.Relaxed => new GradingThresholds {
            HfrMarginalRatio = 1.25, HfrRejectRatio = 1.50,
            StarsMarginalRatio = 0.60, StarsRejectRatio = 0.35,
            GuidingMarginalRatio = 2.0, GuidingRejectRatio = 3.0,
            BackgroundMarginalRatio = 2.0
        },
        GradingSensitivity.Strict => new GradingThresholds {
            HfrMarginalRatio = 1.10, HfrRejectRatio = 1.25,
            StarsMarginalRatio = 0.80, StarsRejectRatio = 0.55,
            GuidingMarginalRatio = 1.4, GuidingRejectRatio = 1.8,
            BackgroundMarginalRatio = 1.4
        },
        _ => new GradingThresholds()
    };
}

/// <summary>Comparison result kept alongside the grade, so Night Watch can reuse it.</summary>
public sealed record GradeAnalysis(FrameGrade Grade, double? HfrRatio, double? StarsRatio, bool HasBaseline);

/// <summary>
/// Grades light frames against a rolling median of recent good frames from the same
/// target / filter / exposure / binning group. Relative comparison makes it work for any
/// optics, sensor and sky without per-rig tuning. Not thread-safe; call from one thread.
/// </summary>
public sealed class FrameGrader {
    private readonly Dictionary<string, Baseline> baselines = new(StringComparer.OrdinalIgnoreCase);
    private GradingThresholds thresholds;

    public FrameGrader(GradingThresholds? thresholds = null) {
        this.thresholds = thresholds ?? new GradingThresholds();
    }

    public void SetThresholds(GradingThresholds updated) => thresholds = updated;

    /// <summary>After autofocus the optical state changed; start fresh baselines.</summary>
    public void ResetBaselines() => baselines.Clear();

    public GradeAnalysis Grade(FrameMetrics frame) {
        if (!IsLight(frame.ImageType)) {
            return new GradeAnalysis(new FrameGrade { Score = 100, Verdict = Verdicts.NotGraded }, null, null, false);
        }

        if (frame.Stars is 0 || (frame.Stars is null && frame.Hfr is null)) {
            var reason = frame.Stars is 0
                ? "No stars detected: clouds, dew, a closed roof or lost focus?"
                : "No star metrics available for this frame.";
            var emptyVerdict = frame.Stars is 0 ? Verdicts.Reject : Verdicts.NotGraded;
            return new GradeAnalysis(new FrameGrade { Score = frame.Stars is 0 ? 0 : 100, Verdict = emptyVerdict, Reasons = [reason] }, null, frame.Stars is 0 ? 0 : null, false);
        }

        var key = GroupKey(frame);

        if (!baselines.TryGetValue(key, out var baseline)) {
            baseline = new Baseline(thresholds.BaselineWindow);
            baselines[key] = baseline;
        }

        var reasons = new List<string>();
        var marginal = false;
        var reject = false;
        double penalty = 0;
        double? hfrRatio = null;
        double? starsRatio = null;
        var hasBaseline = baseline.Count >= thresholds.WarmupFrames;
        var baseHfr = baseline.MedianHfr;
        var baseStars = baseline.MedianStars;

        if (hasBaseline) {
            if (frame.Hfr is double hfr && baseHfr is double referenceHfr && referenceHfr > 0) {
                hfrRatio = hfr / referenceHfr;
                var delta = hfr - referenceHfr;

                if (delta >= thresholds.HfrMinimumDelta) {
                    if (hfrRatio >= thresholds.HfrRejectRatio) {
                        reject = true;
                        reasons.Add(Invariant($"HFR {hfr:0.00} is {Percent(hfrRatio.Value - 1)} above the baseline {referenceHfr:0.00}."));
                    } else if (hfrRatio >= thresholds.HfrMarginalRatio) {
                        marginal = true;
                        reasons.Add(Invariant($"HFR {hfr:0.00} is {Percent(hfrRatio.Value - 1)} above the baseline {referenceHfr:0.00}."));
                    }
                }

                penalty += Math.Clamp((hfrRatio.Value - 1) * 150, 0, 60);
            }

            if (frame.Stars is int stars && baseStars is double referenceStars && referenceStars > 0) {
                starsRatio = stars / referenceStars;

                if (starsRatio <= thresholds.StarsRejectRatio) {
                    reject = true;
                    reasons.Add(Invariant($"Only {stars} stars vs a baseline of {referenceStars:0} ({Percent(1 - starsRatio.Value)} fewer): likely clouds or haze."));
                } else if (starsRatio <= thresholds.StarsMarginalRatio) {
                    marginal = true;
                    reasons.Add(Invariant($"{stars} stars vs a baseline of {referenceStars:0} ({Percent(1 - starsRatio.Value)} fewer)."));
                }

                penalty += Math.Clamp((1 - starsRatio.Value) * 100, 0, 60);
            }

            if (frame.GuidingRmsTotal is double rms && baseline.MedianGuiding is double referenceRms && referenceRms > 0) {
                var guidingRatio = rms / referenceRms;

                if (guidingRatio >= thresholds.GuidingRejectRatio) {
                    reject = true;
                    reasons.Add(Invariant($"Guiding RMS {rms:0.00}\" is {guidingRatio:0.0}× the usual {referenceRms:0.00}\"."));
                } else if (guidingRatio >= thresholds.GuidingMarginalRatio) {
                    marginal = true;
                    reasons.Add(Invariant($"Guiding RMS {rms:0.00}\" is {guidingRatio:0.0}× the usual {referenceRms:0.00}\"."));
                }

                penalty += Math.Clamp((guidingRatio - 1) * 30, 0, 30);
            }

            if (frame.Median is double median && baseline.MedianBackground is double referenceBackground && referenceBackground > 0) {
                var backgroundRatio = median / referenceBackground;

                if (backgroundRatio >= thresholds.BackgroundMarginalRatio) {
                    marginal = true;
                    reasons.Add(Invariant($"Sky background is {backgroundRatio:0.0}× brighter than usual (moon, dawn or high cloud?)."));
                    penalty += 10;
                }
            }
        }

        var verdict = reject ? Verdicts.Reject : marginal ? Verdicts.Marginal : Verdicts.Accept;
        var score = (int)Math.Round(Math.Clamp(100 - penalty, 0, 100));

        // Keep score and verdict consistent so lists sort the way users expect.
        score = verdict switch {
            Verdicts.Reject => Math.Min(score, 39),
            Verdicts.Marginal => Math.Clamp(score, 40, 69),
            _ => Math.Max(score, 70)
        };

        if (verdict != Verdicts.Reject) {
            baseline.Add(frame);
        }

        var grade = new FrameGrade {
            Score = score,
            Verdict = verdict,
            Reasons = reasons,
            BaselineHfr = hasBaseline ? Round(baseHfr) : null,
            BaselineStars = hasBaseline ? Round(baseStars) : null
        };

        return new GradeAnalysis(grade, hfrRatio, starsRatio, hasBaseline);
    }

    public static bool IsLight(string? imageType) =>
        string.Equals(imageType, "LIGHT", StringComparison.OrdinalIgnoreCase);

    private static string GroupKey(FrameMetrics frame) => string.Join('|',
        frame.Target ?? string.Empty,
        frame.Filter ?? string.Empty,
        frame.ExposureSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
        frame.Binning ?? string.Empty);

    private static string Percent(double fraction) => Invariant($"{fraction * 100:0}%");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static double? Round(double? value) => value is null ? null : Math.Round(value.Value, 2);

    private sealed class Baseline(int window) {
        private readonly Queue<FrameMetrics> frames = new();

        public int Count => frames.Count;

        public double? MedianHfr => Median(frames.Select(f => f.Hfr));

        public double? MedianStars => Median(frames.Select(f => (double?)f.Stars));

        public double? MedianGuiding => Median(frames.Select(f => f.GuidingRmsTotal));

        public double? MedianBackground => Median(frames.Select(f => f.Median));

        public void Add(FrameMetrics frame) {
            frames.Enqueue(frame);

            while (frames.Count > window) {
                frames.Dequeue();
            }
        }
    }

    internal static double? Median(IEnumerable<double?> values) {
        var sorted = values.Where(v => v is not null && double.IsFinite(v.Value)).Select(v => v!.Value).Order().ToArray();

        if (sorted.Length == 0) {
            return null;
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
