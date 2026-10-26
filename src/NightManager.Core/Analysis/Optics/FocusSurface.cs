namespace NightManager.Core.Analysis.Optics;

/// <summary>
/// Where each part of the sensor comes to focus, from one autofocus run: star sizes per cell at every focuser
/// position give each cell its own V-curve; the bottom of each curve is that cell's best focus. A plane through
/// those positions is the sensor's tilt, a bowl is field curvature, both in focuser steps (and microns when the
/// focuser's step size is known). The same idea as Hocus Focus's aberration inspector, but passive: it reads the
/// autofocus runs that happen anyway, whichever engine runs them.
/// </summary>
public sealed class FocusSurface {
    public const int Grid = 5;
    private readonly object gate = new();
    private readonly List<(double Position, double?[] Cells)> samples = [];
    /// <summary>Width over height of the autofocus frames: tilt is fitted in the sensor's true proportions.</summary>
    private double aspect = 1;

    public void Reset() {
        lock (gate) samples.Clear();
    }

    public void Add(double position, double?[] cells, double aspect = 1) {
        if (cells.Length != Grid * Grid) return;
        lock (gate) {
            samples.Add((position, cells));
            if (double.IsFinite(aspect) && aspect > 0) this.aspect = aspect;
        }
    }

    public int Count {
        get { lock (gate) return samples.Count; }
    }

    /// <summary>
    /// The sensor's focus surface, or null when the run didn't give enough (fewer than 5 positions, a cropped
    /// autofocus region, too few stars). <paramref name="stepMicrons"/>: how far one focuser step moves the focus.
    /// </summary>
    public SensorFocusReport? Compute(int? autofocusStepSize, double? focalRatio, double? stepMicrons) {
        List<(double Position, double?[] Cells)> data;
        double xs, ys;
        lock (gate) {
            data = samples.ToList();
            // Half-width and half-height with the long side running -1…+1.
            (xs, ys) = aspect >= 1 ? (1.0, 1 / aspect) : (aspect, 1.0);
        }
        var positions = data.Select(d => d.Position).Distinct().Count();
        if (positions < 5) return null;

        // Best focus per cell: star size against focuser position is a hyperbola (HFR² = a·(p − c)² + b²), so HFR² is
        // exactly a parabola in p and its vertex is the best focus, without the pull towards the middle of the run
        // that fitting a parabola to HFR itself gives. Star sizes are good to a few percent whatever their size, so
        // HFR² is weighted by 1/HFR⁴ (else the far, blurry ends decide where the bottom is).
        var best = new double?[Grid * Grid];
        var spread = new double[Grid * Grid];
        var sharpest = new double?[Grid * Grid];
        double lo = data.Min(d => d.Position), hi = data.Max(d => d.Position);
        var mid = (lo + hi) / 2;
        var span = Math.Max(1, (hi - lo) / 2);
        // Each frame's typical star: a cell far off it (a few hot pixels, a satellite, a cell with no real stars) is dropped.
        var typical = data.Select(d => Median(d.Cells.OfType<double>().ToList())).ToList();
        for (var c = 0; c < Grid * Grid; c++) {
            var points = data.Select((d, i) => (d, i)).Where(p => p.d.Cells[c] is double v && typical[p.i] > 0 && v / typical[p.i] is > 0.5 and < 2)
                .Select(p => (X: (p.d.Position - mid) / span, Hfr: p.d.Cells[c]!.Value)).ToList();
            if (CellFocus(points) is not var (vertex, error, bottom)) continue;
            best[c] = mid + vertex * span;
            spread[c] = error * span;
            sharpest[c] = bottom;
        }
        var cells = Enumerable.Range(0, Grid * Grid).Where(c => best[c] is double).ToList();
        if (cells.Count < 9 || best[Grid * Grid / 2] is not double center) return null;

        double X(int c) => (2.0 * (c % Grid + 0.5) / Grid - 1) * xs;
        double Y(int c) => (2.0 * (c / Grid + 0.5) / Grid - 1) * ys;
        // Tilt and curvature together, each cell trusted as far as its own V-curve was clean; then once more without
        // any cell far off the fitted surface.
        var weights = cells.ToDictionary(c => c, c => 1 / Math.Max(1, spread[c] * spread[c]));
        double[] bowl = [];
        for (var pass = 0; pass < 2; pass++) {
            bowl = OpticsAnalyzer.LeastSquares(cells.Select(c => (new[] { 1, X(c), Y(c), X(c) * X(c) + Y(c) * Y(c) }, best[c]!.Value, weights[c])).ToList());
            var fitted = bowl;
            var misfit = cells.Select(c => Math.Abs(best[c]!.Value - (fitted[0] + fitted[1] * X(c) + fitted[2] * Y(c) + fitted[3] * (X(c) * X(c) + Y(c) * Y(c))))).ToList();
            var scale = 1.4826 * Median(misfit) + 1;
            for (var i = 0; i < cells.Count; i++) {
                if (misfit[i] > 4 * scale) weights[cells[i]] = 0;
            }
        }
        // The plane's spread over the whole sensor: its highest corner against its lowest.
        var tilt = 2 * (Math.Abs(bowl[1]) * xs + Math.Abs(bowl[2]) * ys);
        var curvature = bowl[3] * (xs * xs + ys * ys); // centre to corner
        // How far the tilt could be off: the scatter of the cells about the fitted surface, carried through the fit.
        var used = cells.Where(c => weights[c] > 0).ToList();
        var residuals = used.Select(c => best[c]!.Value - (bowl[0] + bowl[1] * X(c) + bowl[2] * Y(c) + bowl[3] * (X(c) * X(c) + Y(c) * Y(c)))).ToList();
        var rms = used.Count > 4 ? Math.Sqrt(residuals.Sum(v => v * v) / (used.Count - 4)) : double.NaN;
        var sxx = used.Sum(c => X(c) * X(c)) - used.Sum(X) * used.Sum(X) / Math.Max(1, used.Count);
        var syy = used.Sum(c => Y(c) * Y(c)) - used.Sum(Y) * used.Sum(Y) / Math.Max(1, used.Count);
        var tiltError = double.IsFinite(rms) && sxx > 0 && syy > 0
            ? 2 * Math.Sqrt(Math.Pow(rms * xs, 2) / sxx + Math.Pow(rms * ys, 2) / syy)
            : double.NaN;

        // Softness focus can't fix: where the bottoms of the V-curves are higher. Tilt and curvature only move the bottom
        // sideways; a side whose stars never get as small is collimation, a decentred or tilted lens, or the corrector.
        var sharpCells = Enumerable.Range(0, Grid * Grid).Where(c => sharpest[c] is double && weights.GetValueOrDefault(c) > 0).ToList();
        var typicalSharpest = Median(sharpCells.Select(c => sharpest[c]!.Value).ToList());
        double? softPercent = null, softAngle = null;
        if (sharpCells.Count >= 9 && typicalSharpest > 0) {
            var soft = OpticsAnalyzer.LeastSquares(sharpCells.Select(c => (new[] { 1, X(c), Y(c), X(c) * X(c) + Y(c) * Y(c) }, sharpest[c]!.Value / typicalSharpest, 1.0)).ToList());
            softPercent = Math.Round(2 * (Math.Abs(soft[1]) * xs + Math.Abs(soft[2]) * ys) * 100, 1);
            softAngle = Math.Round((Math.Atan2(soft[2], soft[1]) * 180 / Math.PI + 360) % 360, 0);
        }

        double? Corner(int gx, int gy) => best[gy * Grid + gx] is double v ? Math.Round(v - center, 1) : null;
        double? Microns(double steps) => stepMicrons is > 0 ? Math.Round(steps * stepMicrons.Value, 1) : null;
        double? cfz = focalRatio is > 0 ? Math.Round(4.88 * 0.55 * focalRatio.Value * focalRatio.Value, 1) : null;

        return new SensorFocusReport {
            Positions = positions,
            TiltSteps = Math.Round(tilt, 1),
            TiltUncertaintySteps = double.IsFinite(tiltError) ? Math.Round(tiltError, 1) : null,
            TiltAngle = Math.Round((Math.Atan2(bowl[2], bowl[1]) * 180 / Math.PI + 360) % 360, 0),
            CurvatureSteps = Math.Round(curvature, 1),
            UnfocusableSoftnessPercent = softPercent,
            UnfocusableSoftnessAngle = softAngle,
            SharpestCells = sharpest.Select(v => v is double b && typicalSharpest > 0 ? Math.Round(b / typicalSharpest, 2) : (double?)null).ToList(),
            TopLeft = Corner(0, 0), TopRight = Corner(Grid - 1, 0), BottomLeft = Corner(0, Grid - 1), BottomRight = Corner(Grid - 1, Grid - 1),
            Cells = best.Select(b => b is double v ? Math.Round(v - center, 1) : (double?)null).ToList(),
            AutofocusStep = autofocusStepSize,
            FocalRatio = focalRatio is > 0 ? Math.Round(focalRatio.Value, 2) : null,
            StepMicrons = stepMicrons is > 0 ? stepMicrons : null,
            TiltMicrons = Microns(tilt),
            CurvatureMicrons = Microns(curvature),
            CriticalFocusMicrons = cfz,
        };
    }

    /// <summary>
    /// One cell's best focus (in the run's -1…+1 units) and its standard error, from (position, HFR) points: a weighted
    /// parabola through HFR², refitted once without points far off it. Null when the cell never came through focus.
    /// </summary>
    internal static (double Vertex, double Error, double Sharpest)? CellFocus(List<(double X, double Hfr)> points) {
        if (points.Count < 5) return null;
        var weights = points.Select(p => 1 / Math.Pow(p.Hfr, 4)).ToArray();
        double[] fit = [];
        for (var pass = 0; pass < 2; pass++) {
            fit = OpticsAnalyzer.LeastSquares(points.Select((p, i) => (new[] { 1, p.X, p.X * p.X }, p.Hfr * p.Hfr, weights[i])).ToList());
            if (fit[2] <= 1e-9) return null; // not a V: this cell never came through focus
            var f = fit;
            var relative = points.Select(p => Math.Abs(Math.Sqrt(Math.Max(1e-9, f[0] + f[1] * p.X + f[2] * p.X * p.X)) - p.Hfr) / p.Hfr).ToList();
            var scale = 1.4826 * Median(relative) + 0.01;
            for (var i = 0; i < points.Count; i++) {
                if (relative[i] > 4 * scale) weights[i] = 0;
            }
            if (weights.Count(v => v > 0) < 5) return null;
        }
        var vertex = -fit[1] / (2 * fit[2]);
        if (vertex < -1.2 || vertex > 1.2) return null; // best focus outside the run: not trustworthy
        // Standard error of the vertex from the fit's covariance (the scatter of the points about it).
        var a = new double[3, 3];
        double chi = 0;
        var n = 0;
        for (var i = 0; i < points.Count; i++) {
            if (weights[i] <= 0) continue;
            double[] row = [1, points[i].X, points[i].X * points[i].X];
            for (var j = 0; j < 3; j++) for (var k = 0; k < 3; k++) a[j, k] += weights[i] * row[j] * row[k];
            var r = points[i].Hfr * points[i].Hfr - (fit[0] + fit[1] * row[1] + fit[2] * row[2]);
            chi += weights[i] * r * r;
            n++;
        }
        if (Invert3(a) is not { } cov) return null;
        var variance = chi / Math.Max(1, n - 3);
        double j1 = -1 / (2 * fit[2]), j2 = fit[1] / (2 * fit[2] * fit[2]);
        var error = Math.Sqrt(Math.Max(0, variance * (j1 * j1 * cov[1, 1] + 2 * j1 * j2 * cov[1, 2] + j2 * j2 * cov[2, 2])));
        // The bottom of the V: the smallest star this part of the sensor can give, whatever the focus.
        var sharpest = Math.Sqrt(Math.Max(1e-6, fit[0] - fit[1] * fit[1] / (4 * fit[2])));
        return (vertex, Math.Max(error, 1e-3), sharpest);
    }

    private static double[,]? Invert3(double[,] m) {
        var det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        if (Math.Abs(det) < 1e-300) return null;
        var inv = new double[3, 3];
        for (var i = 0; i < 3; i++) {
            for (var j = 0; j < 3; j++) {
                int r0 = (j + 1) % 3, r1 = (j + 2) % 3, c0 = (i + 1) % 3, c1 = (i + 2) % 3;
                inv[i, j] = (m[r0, c0] * m[r1, c1] - m[r0, c1] * m[r1, c0]) / det;
            }
        }
        return inv;
    }

    private static double Median(List<double> values) {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}

/// <summary>
/// Where the sensor comes to focus, from an autofocus run. Positions are focuser steps relative to the centre's best
/// focus (positive = that part needs the focuser further out).
/// </summary>
public sealed record SensorFocusReport {
    /// <summary>Focuser positions the run measured.</summary>
    public required int Positions { get; init; }
    /// <summary>Focus difference across the sensor from tilt: the furthest-out corner against the furthest-in one, in steps.</summary>
    public required double TiltSteps { get; init; }
    /// <summary>
    /// How far <see cref="TiltSteps"/> could be off (one standard error), from how cleanly the cells fit a flat, tilted,
    /// curved surface. One run is often noisy: a tilt smaller than about twice this isn't really measured yet.
    /// </summary>
    public double? TiltUncertaintySteps { get; init; }
    /// <summary>Towards where focus is further out, degrees: 0 = right, 90 = down (image orientation).</summary>
    public required double TiltAngle { get; init; }
    /// <summary>
    /// How much bigger the smallest stars are on one side than the other, in percent: softness no focus position
    /// fixes (collimation, a decentred or tilted lens, corrector spacing), unlike tilt and curvature.
    /// </summary>
    public double? UnfocusableSoftnessPercent { get; init; }
    /// <summary>Direction that softness grows towards, degrees: 0 = right, 90 = down.</summary>
    public double? UnfocusableSoftnessAngle { get; init; }
    /// <summary>5×5 grid of each cell's smallest star (its V-curve's bottom) against the typical cell, row by row from the top left.</summary>
    public IReadOnlyList<double?> SharpestCells { get; init; } = [];
    /// <summary>Corners against the centre after removing tilt, in steps (positive: the corners focus further out).</summary>
    public required double CurvatureSteps { get; init; }
    public double? TopLeft { get; init; }
    public double? TopRight { get; init; }
    public double? BottomLeft { get; init; }
    public double? BottomRight { get; init; }
    /// <summary>5×5 grid of best-focus offsets from the centre, row by row from the top left (null: not found).</summary>
    public IReadOnlyList<double?> Cells { get; init; } = [];
    /// <summary>NINA's autofocus step size: tilt bigger than about one step is visible in the corners.</summary>
    public int? AutofocusStep { get; init; }
    public double? FocalRatio { get; init; }
    public double? StepMicrons { get; init; }
    public double? TiltMicrons { get; init; }
    public double? CurvatureMicrons { get; init; }
    /// <summary>Critical focus zone (4.88 λ N², λ = 0.55 µm): focus errors smaller than this don't show.</summary>
    public double? CriticalFocusMicrons { get; init; }
}
