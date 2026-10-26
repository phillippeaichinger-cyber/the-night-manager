namespace NightManager.Core.Analysis;

/// <summary>Live guiding statistics in arcseconds over a rolling window.</summary>
public sealed record GuidingStats(double RmsRa, double RmsDec, double RmsTotal, double PeakRa, double PeakDec, int Steps, DateTimeOffset At);

/// <summary>
/// Rolling RMS from the guider's individual corrections (NINA's guide-step event, raw RA/Dec distances in pixels).
/// NINA 3.2 only pushes the guider's own RMS to plugins on connect/state changes, so it reads 0 while guiding;
/// computing it from the steps works the same on every NINA version.
/// </summary>
public sealed class GuidingMonitor {
    private readonly object gate = new();
    private readonly Queue<(DateTimeOffset At, double Ra, double Dec)> steps = new();
    private readonly TimeSpan window;
    private readonly int maxSteps;

    /// <param name="window">How far back the RMS looks (PHD2's graph typically shows a few minutes).</param>
    public GuidingMonitor(TimeSpan? window = null, int maxSteps = 200) {
        this.window = window ?? TimeSpan.FromMinutes(2);
        this.maxSteps = maxSteps;
    }

    /// <summary>Arcseconds per guide-camera pixel (from the guider's info). Below or at 0, stats are not reported.</summary>
    public double PixelScale { get; set; }

    public void AddStep(double raPixels, double decPixels, DateTimeOffset at) {
        if (!double.IsFinite(raPixels) || !double.IsFinite(decPixels)) {
            return;
        }
        lock (gate) {
            steps.Enqueue((at, raPixels, decPixels));
            Trim(at);
        }
    }

    /// <summary>Guiding stopped or the guider disconnected: forget the old corrections.</summary>
    public void Reset() {
        lock (gate) {
            steps.Clear();
        }
    }

    /// <summary>Current stats, or null when there are no recent steps (not guiding) or no pixel scale.</summary>
    public GuidingStats? Current(DateTimeOffset now) {
        lock (gate) {
            Trim(now);
            if (steps.Count < 2 || PixelScale <= 0 || !double.IsFinite(PixelScale)) {
                return null;
            }
            double sumRa = 0, sumDec = 0, peakRa = 0, peakDec = 0;
            foreach (var (_, ra, dec) in steps) {
                sumRa += ra * ra;
                sumDec += dec * dec;
                peakRa = Math.Max(peakRa, Math.Abs(ra));
                peakDec = Math.Max(peakDec, Math.Abs(dec));
            }
            var rmsRa = Math.Sqrt(sumRa / steps.Count) * PixelScale;
            var rmsDec = Math.Sqrt(sumDec / steps.Count) * PixelScale;
            return new GuidingStats(
                Math.Round(rmsRa, 3),
                Math.Round(rmsDec, 3),
                Math.Round(Math.Sqrt(rmsRa * rmsRa + rmsDec * rmsDec), 3),
                Math.Round(peakRa * PixelScale, 3),
                Math.Round(peakDec * PixelScale, 3),
                steps.Count,
                steps.Last().At);
        }
    }

    private void Trim(DateTimeOffset now) {
        while (steps.Count > 0 && (steps.Count > maxSteps || now - steps.Peek().At > window)) {
            steps.Dequeue();
        }
    }
}
