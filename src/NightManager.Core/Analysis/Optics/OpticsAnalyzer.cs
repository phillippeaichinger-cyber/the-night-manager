namespace NightManager.Core.Analysis.Optics;

/// <summary>
/// Measures stars across a raw sub and fits tilt, field curvature and collimation from them. Written for this
/// plugin (Hocus Focus's aberration inspector was a reference for the ideas: per-region star metrics, a plane for tilt
/// and a bowl for curvature), but it works on any light frame as it's saved, with no extra exposures and no focuser
/// moves. Pure .NET, no NINA types: the adapter hands it the pixels.
/// </summary>
public static class OpticsAnalyzer {
    private const int Grid = 3;
    private const int MaxStars = 2_000;
    private const int MinStars = 25;
    /// <summary>Widest measuring aperture (radius, measured pixels): holds the light of stars up to HFR ≈ 4.5.</summary>
    private const double MaxAperture = 16;

    /// <summary>
    /// Analyses one frame. <paramref name="bayered"/>: a colour sensor's raw mosaic (pixels are binned 2×2 so each
    /// measured pixel holds every colour). Big frames are binned 2×2 too (speed); HFRs are reported in pixels of the
    /// sub as saved either way. Returns null when there are too few usable stars (clouds, a flat, a dark).
    /// </summary>
    public static OpticsReport? Analyze(ushort[] pixels, int width, int height, int bitDepth = 16, bool bayered = false,
        double? pixelSizeMicrons = null, double? focalLengthMm = null, CancellationToken token = default) {
        if (Stars(pixels, width, height, bitDepth, bayered, token) is not var (stars, w, h)) {
            return null;
        }
        return Summarize(stars, w, h, pixelSizeMicrons is > 0 && focalLengthMm is > 0 ? 206.265 * pixelSizeMicrons.Value / focalLengthMm.Value : null);
    }

    /// <summary>The measured stars (positions in measured pixels, sizes in pixels of the sub) and the measured frame's size.</summary>
    internal static (List<Star> Stars, int W, int H)? Stars(ushort[] pixels, int width, int height, int bitDepth = 16, bool bayered = false, CancellationToken token = default) {
        if (pixels.Length < width * height || width < 64 || height < 64) {
            return null;
        }
        var bin = bayered || (long)width * height > 40_000_000 ? 2 : 1;
        var (img, w, h) = bin == 1 ? (ToFloat(pixels, width, height), width, height) : Bin2(pixels, width, height);
        token.ThrowIfCancellationRequested();

        var saturation = (float)(((1 << Math.Clamp(bitDepth, 8, 16)) - 1) * bin * bin * 0.85);
        var (background, noise) = BackgroundMap(img, w, h, 64);
        token.ThrowIfCancellationRequested();

        var stars = Detect(img, w, h, background, noise, saturation);
        token.ThrowIfCancellationRequested();
        if (stars.Count < MinStars) {
            return null;
        }

        // Back to pixels of the saved sub.
        foreach (var s in stars) {
            s.Hfr *= bin;
            s.Fwhm *= bin;
        }
        return (stars, w, h);
    }

    /// <summary>
    /// Median HFR in each cell of a <paramref name="grid"/>×<paramref name="grid"/> split of the frame (row by row
    /// from the top left; null where too few stars). Used on autofocus exposures, where every cell's own V-curve
    /// shows where that part of the sensor comes to focus.
    /// </summary>
    public static double?[]? HfrGrid(ushort[] pixels, int width, int height, int bitDepth = 16, bool bayered = false, int grid = 5, CancellationToken token = default) {
        if (pixels.Length < width * height || width < 64 || height < 64) {
            return null;
        }
        var bin = bayered || (long)width * height > 40_000_000 ? 2 : 1;
        var (img, w, h) = bin == 1 ? (ToFloat(pixels, width, height), width, height) : Bin2(pixels, width, height);
        token.ThrowIfCancellationRequested();
        var saturation = (float)(((1 << Math.Clamp(bitDepth, 8, 16)) - 1) * bin * bin * 0.85);
        var (background, noise) = BackgroundMap(img, w, h, 64);
        var stars = Detect(img, w, h, background, noise, saturation);
        if (stars.Count < MinStars) {
            return null;
        }
        var cells = new double?[grid * grid];
        for (var gy = 0; gy < grid; gy++) {
            for (var gx = 0; gx < grid; gx++) {
                var hfrs = stars.Where(s => (int)(s.X * grid / w) == gx && (int)(s.Y * grid / h) == gy).Select(s => s.Hfr * bin).ToList();
                cells[gy * grid + gx] = hfrs.Count >= 5 ? Round(MedianOf(hfrs), 3) : null;
            }
        }
        return cells;
    }

    // MARK: Pixels

    private static float[] ToFloat(ushort[] pixels, int width, int height) {
        var img = new float[width * height];
        for (var i = 0; i < img.Length; i++) img[i] = pixels[i];
        return img;
    }

    private static (float[] Img, int W, int H) Bin2(ushort[] pixels, int width, int height) {
        int w = width / 2, h = height / 2;
        var img = new float[w * h];
        for (var y = 0; y < h; y++) {
            var r0 = 2 * y * width;
            var r1 = r0 + width;
            for (var x = 0; x < w; x++) {
                var c = 2 * x;
                img[y * w + x] = pixels[r0 + c] + pixels[r0 + c + 1] + pixels[r1 + c] + pixels[r1 + c + 1];
            }
        }
        return (img, w, h);
    }

    /// <summary>Median background per tile (sky gradients stay out of the star fluxes) and the frame's noise (MAD).</summary>
    private static (float[] Tiles, float Noise) BackgroundMap(float[] img, int w, int h, int tile) {
        int tx = (w + tile - 1) / tile, ty = (h + tile - 1) / tile;
        var tiles = new float[tx * ty];
        var sample = new List<float>(tile * tile / 4);
        var deviations = new List<float>();
        for (var j = 0; j < ty; j++) {
            for (var i = 0; i < tx; i++) {
                sample.Clear();
                for (var y = j * tile; y < Math.Min(h, (j + 1) * tile); y += 2) {
                    for (var x = i * tile; x < Math.Min(w, (i + 1) * tile); x += 2) sample.Add(img[y * w + x]);
                }
                var median = Median(sample);
                tiles[j * tx + i] = median;
                if ((i + j) % 3 == 0) {
                    foreach (var v in sample) deviations.Add(Math.Abs(v - median));
                }
            }
        }
        return (tiles, Math.Max(1f, 1.4826f * Median(deviations)));
    }

    private static float Median(List<float> values) {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>
    /// The sky without the target: a smooth surface (a quadratic in x and y) through the tile medians, refitted without
    /// the tiles well above it. Glow above it is a galaxy or nebula, not the light-pollution gradient, which is smooth.
    /// </summary>
    private static float[] SmoothSky(float[] tiles, int tx, int ty) {
        double U(int i) => tx > 1 ? 2.0 * (i % tx) / (tx - 1) - 1 : 0;
        double V(int i) => ty > 1 ? 2.0 * (i / tx) / (ty - 1) - 1 : 0;
        var keep = Enumerable.Repeat(true, tiles.Length).ToArray();
        double[] fit = [Median(tiles.ToList()), 0, 0, 0, 0, 0];
        for (var pass = 0; pass < 5; pass++) {
            var rows = Enumerable.Range(0, tiles.Length).Where(i => keep[i])
                .Select(i => (new[] { 1, U(i), V(i), U(i) * U(i), U(i) * V(i), V(i) * V(i) }, (double)tiles[i], 1.0)).ToList();
            if (rows.Count < 12) break;
            fit = LeastSquares(rows);
            var f = fit;
            var residuals = Enumerable.Range(0, tiles.Length).Select(i => tiles[i] - (f[0] + f[1] * U(i) + f[2] * V(i) + f[3] * U(i) * U(i) + f[4] * U(i) * V(i) + f[5] * V(i) * V(i))).ToArray();
            var spread = 1.4826 * Median(residuals.Where((_, i) => keep[i]).Select(v => (float)Math.Abs(v)).ToList()) + 1e-3;
            for (var i = 0; i < tiles.Length; i++) keep[i] = residuals[i] < 2.5 * spread;
        }
        return Enumerable.Range(0, tiles.Length).Select(i => (float)(fit[0] + fit[1] * U(i) + fit[2] * V(i) + fit[3] * U(i) * U(i) + fit[4] * U(i) * V(i) + fit[5] * V(i) * V(i))).ToArray();
    }

    // MARK: Stars

    internal sealed class Star {
        public double X, Y, Flux, Hfr, Fwhm, Eccentricity, Angle;
        /// <summary>Sky under the star above the smooth sky, in noise units: a galaxy or nebula behind it.</summary>
        public double Glow;
        /// <summary>Signal-to-noise of the star's light in its aperture.</summary>
        public double Snr;
        /// <summary>HFR against stars of the same brightness across the frame (the frame's median HFR = typical).</summary>
        public double Size;
    }

    private static List<Star> Detect(float[] img, int w, int h, float[] tiles, float noise, float saturation) {
        const int tile = 64;
        var tx = (w + tile - 1) / tile;
        const int r = 7;
        var threshold = 6 * noise;
        var sky = SmoothSky(tiles, tx, (h + tile - 1) / tile);
        var found = new List<Star>();
        for (var y = r + 1; y < h - r - 1; y++) {
            for (var x = r + 1; x < w - r - 1; x++) {
                var v = img[y * w + x];
                var bg = tiles[(y / tile) * tx + x / tile];
                if (v - bg < threshold || v >= saturation) continue;
                // A local maximum in a 5×5 box.
                var peak = true;
                for (var dy = -2; dy <= 2 && peak; dy++) {
                    for (var dx = -2; dx <= 2; dx++) {
                        if ((dx != 0 || dy != 0) && img[(y + dy) * w + x + dx] > v) { peak = false; break; }
                    }
                }
                if (!peak) continue;
                if (Measure(img, w, x, y, r, bg, noise, saturation) is { } star) {
                    star.Glow = (bg - sky[(y / tile) * tx + x / tile]) / noise;
                    found.Add(star);
                }
                x += 2;
            }
        }
        // The brightest unsaturated stars measure best; a cap keeps dense fields fast.
        return found.OrderByDescending(s => s.Flux).Take(MaxStars).ToList();
    }

    private static Star? Measure(float[] img, int w, int cx, int cy, int r, float bg, float noise, float saturation) {
        // Pass 1: centroid and a first size from clearly-lit pixels only (3σ): sky noise in the box would otherwise
        // swell every star and make round stars look elongated.
        var cut = 3 * noise;
        double sum = 0, sx = 0, sy = 0;
        for (var y = cy - r; y <= cy + r; y++) {
            for (var x = cx - r; x <= cx + r; x++) {
                var v = img[y * w + x];
                if (v >= saturation) return null;
                var f = v - bg;
                if (f <= cut) continue;
                sum += f; sx += f * x; sy += f * y;
            }
        }
        // Only well-exposed stars: faint ones are mostly noise and pull every figure about.
        if (sum <= 60 * noise) return null;
        // A hot pixel or cosmic ray: nearly all its light in one pixel (a star a pixel wide still spreads over several).
        // Far from focus these are often all that's left, and they read as perfectly sharp.
        if (img[cy * w + cx] - bg > 0.6 * sum) return null;
        double mx = sx / sum, my = sy / sum;
        var hfr0 = HalfFluxRadius(img, w, mx, my, r, bg, cut, sum);
        if (hfr0 < 0.3) return null;

        // Pass 2, inside an aperture scaled to the star: shape from pixels above 1σ, size from all its light. The first
        // size comes from the small detection box, so a big star gets a second, wider look.
        var h = img.Length / w;
        var aperture = Math.Clamp(3 * hfr0 + 2, 3, MaxAperture);
        if (mx - aperture - Ring < 0 || my - aperture - Ring < 0 || mx + aperture + Ring > w - 1 || my + aperture + Ring > h - 1) return null;
        // The star's own sky, from a ring just outside it: the tile's median (64 px) is off by a little under most stars,
        // and over a whole aperture that little adds up to a faint star reading far too big (measured: the faintest
        // fifth of the stars in an O-III sub 60% bigger than the brightest). On a galaxy or nebula, more so.
        var local = LocalSky(img, w, mx, my, aperture);
        var shapeCut = noise;
        double total = 0, mxx = 0, myy = 0, mxy = 0, tx = 0, ty = 0;
        for (var y = Math.Max(0, (int)(my - aperture)); y <= Math.Min(h - 1, (int)Math.Ceiling(my + aperture)); y++) {
            for (var x = Math.Max(0, (int)(mx - aperture)); x <= Math.Min(w - 1, (int)Math.Ceiling(mx + aperture)); x++) {
                double dx = x - mx, dy = y - my;
                if (dx * dx + dy * dy > aperture * aperture) continue;
                var f = img[y * w + x] - local;
                if (f <= shapeCut) continue;
                total += f; tx += f * x; ty += f * y;
                mxx += f * dx * dx; myy += f * dy * dy; mxy += f * dx * dy;
            }
        }
        if (total <= 0) return null;
        mxx /= total; myy /= total; mxy /= total;
        mx = tx / total; my = ty / total;
        // Eigenvalues of the second-moment matrix: the star's long and short axes.
        var tr = (mxx + myy) / 2;
        var diff = Math.Sqrt(Math.Max(0, (mxx - myy) * (mxx - myy) / 4 + mxy * mxy));
        double l1 = tr + diff, l2 = tr - diff;
        if (l2 <= 0.02 || l1 > r * r) return null; // a hot pixel, or a blend / extended object
        var hfr = HalfFluxRadius(img, w, mx, my, aperture, local, 0, total);
        if (hfr > (aperture - 2) / 3 && aperture < MaxAperture) {
            aperture = Math.Min(MaxAperture, 3 * hfr + 3);
            if (mx - aperture - Ring < 0 || my - aperture - Ring < 0 || mx + aperture + Ring > w - 1 || my + aperture + Ring > h - 1) return null;
            local = LocalSky(img, w, mx, my, aperture);
            hfr = HalfFluxRadius(img, w, mx, my, aperture, local, 0, 0);
        }
        if (hfr < 0.3) return null;
        // Thresholding trims the wings, so the moments under-read a star's width: FWHM comes from the half-flux radius
        // (for a Gaussian star both are 1.1774σ × 2 and 1.1774σ, so FWHM = 2 × HFR).
        return new Star {
            X = mx, Y = my, Flux = total, Hfr = hfr, Snr = total / (noise * Math.Sqrt(Math.PI * aperture * aperture)),
            Fwhm = 2 * hfr,
            Eccentricity = Math.Sqrt(Math.Max(0, 1 - l2 / l1)),
            Angle = 0.5 * Math.Atan2(2 * mxy, mxx - myy),
        };
    }

    /// <summary>Width of the sky ring outside a star's aperture.</summary>
    private const double Ring = 6;

    /// <summary>Median of the ring from 2 to <see cref="Ring"/> pixels outside the aperture (neighbours barely move a median).</summary>
    private static float LocalSky(float[] img, int w, double mx, double my, double aperture) {
        double inner = aperture + 2, outer = aperture + Ring;
        var ring = new List<float>();
        for (var y = (int)(my - outer); y <= (int)Math.Ceiling(my + outer); y++) {
            for (var x = (int)(mx - outer); x <= (int)Math.Ceiling(mx + outer); x++) {
                double dx = x - mx, dy = y - my, rr = dx * dx + dy * dy;
                if (rr >= inner * inner && rr <= outer * outer) ring.Add(img[y * w + x]);
            }
        }
        return Median(ring);
    }

    /// <summary>
    /// Radius holding half the star's light. Each pixel's light is spread over 4×4 points inside it (not all at its
    /// centre), so small stars of a pixel or two aren't read 20% small.
    /// </summary>
    private static double HalfFluxRadius(float[] img, int w, double mx, double my, double radius, float bg, float cut, double flux) {
        const int sub = 4;
        const double binWidth = 0.05;
        var bins = new double[(int)((radius + 1) / binWidth) + 2];
        double total = 0;
        var h = img.Length / w;
        for (var y = Math.Max(0, (int)(my - radius)); y <= Math.Min(h - 1, (int)Math.Ceiling(my + radius)); y++) {
            for (var x = Math.Max(0, (int)(mx - radius)); x <= Math.Min(w - 1, (int)Math.Ceiling(mx + radius)); x++) {
                double dx = x - mx, dy = y - my;
                if (Math.Sqrt(dx * dx + dy * dy) > radius) continue;
                var f = img[y * w + x] - bg;
                if (f <= cut) continue;
                total += f;
                var part = f / (sub * sub);
                for (var j = 0; j < sub; j++) {
                    var py = dy + (j + 0.5) / sub - 0.5;
                    for (var i = 0; i < sub; i++) {
                        var px = dx + (i + 0.5) / sub - 0.5;
                        bins[Math.Min(bins.Length - 1, (int)(Math.Sqrt(px * px + py * py) / binWidth))] += part;
                    }
                }
            }
        }
        if (total <= 0) return 0;
        double acc = 0;
        for (var b = 0; b < bins.Length; b++) {
            if (acc + bins[b] >= total / 2) {
                return (b + (total / 2 - acc) / Math.Max(bins[b], 1e-12)) * binWidth;
            }
            acc += bins[b];
        }
        return bins.Length * binWidth;
    }

    // MARK: Fits

    /// <summary>Stars on more glow than this (noise units above the smooth sky) are left out of the fits.</summary>
    private const double GlowLimit = 1.5;
    /// <summary>Stars fainter than this signal-to-noise are left out of the fits.</summary>
    private const double MinSnr = 25;

    internal static OpticsReport Summarize(List<Star> all, int w, int h, double? scale) {
        double Nx(Star s) => 2 * s.X / (w - 1) - 1;
        double Ny(Star s) => 2 * s.Y / (h - 1) - 1;

        // Only stars on clear sky. A galaxy's or nebula's knots, clusters and HII regions are found as "stars" 3–5
        // times the size of real ones (measured on M31, M33 and M15), and the sky under a star on a galaxy is uneven,
        // so its size is off too. Very elongated sources are satellite trails and blends.
        // And only stars bright enough to size well: in the noise a faint star's measured size swells (the faintest
        // fifth read 30–60% bigger than the brightest on real subs), so a corner of faint stars would look soft.
        var stars = all.Where(s => s.Glow < GlowLimit && s.Eccentricity < 0.9 && s.Snr >= MinSnr).ToList();
        // Then the brighter half: the faint end of a galaxy field is full of its clusters and HII regions and of
        // background galaxies, all bigger than stars (on M31 the faintest fifth measured twice the size of the rest).
        if (stars.Count >= 4 * MinStars) {
            var cut = MedianOf(stars.Select(s => s.Flux));
            stars = stars.Where(s => s.Flux >= cut).ToList();
        }
        if (stars.Count < MinStars) stars = all;

        var regions = new List<OpticsRegion>();
        for (var gy = 0; gy < Grid; gy++) {
            for (var gx = 0; gx < Grid; gx++) {
                var inRegion = stars.Where(s => (int)(s.X * Grid / w) == gx && (int)(s.Y * Grid / h) == gy).ToList();
                regions.Add(new OpticsRegion {
                    Stars = inRegion.Count,
                    Hfr = inRegion.Count >= 3 ? Round(MedianOf(inRegion.Select(s => s.Hfr)), 2) : null,
                    Fwhm = inRegion.Count >= 3 ? Round(MedianOf(inRegion.Select(s => s.Fwhm)), 2) : null,
                    Eccentricity = inRegion.Count >= 3 ? Round(MedianOf(inRegion.Select(s => s.Eccentricity)), 3) : null,
                    Angle = inRegion.Count >= 3 ? Round(AverageAngle(inRegion), 0) : null,
                });
            }
        }

        var median = MedianOf(stars.Select(s => s.Hfr));
        NormaliseByBrightness(stars, median);
        // Fitted in the sensor's true proportions (the long side runs -1…+1), so a diagonal on a 3:2 sensor keeps its
        // angle. xs, ys: half-width and half-height in those units.
        double xs = (double)w / Math.Max(w, h), ys = (double)h / Math.Max(w, h);
        var field = FitField(stars, all, w, h, xs, ys);
        // How sure: the same fit on the stars drawn again at random (a bootstrap). A frame's own mix of stars makes a
        // pattern of its own, often as big as a real one; only a gradient well above this is worth acting on.
        var random = new Random(stars.Count);
        var draws = new List<(double B, double C)>();
        for (var i = 0; i < 24; i++) {
            var sample = Enumerable.Range(0, stars.Count).Select(_ => stars[random.Next(stars.Count)]).ToList();
            if (FitField(sample, all, w, h, xs, ys) is { } f) draws.Add((f[1], f[2]));
        }

        double tiltPercent = 0, tiltAngle = 0, tiltError = 0, curvature = 0;
        double? sweetX = null, sweetY = null;
        if (field is { } b && median > 0.1) {
            // Gradient: how much bigger stars are on one side than the other (the plane's spread over the sensor).
            tiltPercent = 2 * (Math.Abs(b[1]) * xs + Math.Abs(b[2]) * ys) / median * 100;
            tiltAngle = (Math.Atan2(b[2], b[1]) * 180 / Math.PI + 360) % 360;
            if (draws.Count > 4) {
                double mb = draws.Average(d => d.B), mc = draws.Average(d => d.C);
                var sb = Math.Sqrt(draws.Sum(d => (d.B - mb) * (d.B - mb)) / (draws.Count - 1));
                var sc = Math.Sqrt(draws.Sum(d => (d.C - mc) * (d.C - mc)) / (draws.Count - 1));
                tiltError = 2 * Math.Sqrt(sb * sb * xs * xs + sc * sc * ys * ys) / median * 100;
            }
            // Curvature: corners against the centre, from the part of the bowl that rises in every direction. Stars soft
            // at only two opposite edges (a tilted sensor focused in the middle, astigmatism) aren't curvature: of the
            // quadratic's two principal slopes, take the one nearer zero.
            var round = b[3];
            var saddle = Math.Sqrt(b[4] * b[4] + b[5] * b[5] / 4);
            var bowl = round >= 0 ? Math.Max(0, round - saddle) : Math.Min(0, round + saddle);
            var corner = xs * xs + ys * ys;
            curvature = (bowl * corner + b[6] * corner * corner) / median * 100;
            if (curvature >= 4) {
                // The bottom of the bowl: off-centre on a reflector or SCT means the optical axis isn't centred on the
                // sensor (collimation), or tilt, which also moves it.
                sweetX = Math.Clamp(-b[1] / (2 * round) / xs, -1.5, 1.5);
                sweetY = Math.Clamp(-b[2] / (2 * round) / ys, -1.5, 1.5);
            }
        }

        var outer = stars.Where(s => Math.Sqrt(Nx(s) * Nx(s) + Ny(s) * Ny(s)) > 0.6).ToList();
        var inner = stars.Where(s => Math.Sqrt(Nx(s) * Nx(s) + Ny(s) * Ny(s)) < 0.35).ToList();
        // cos 2(θ − φ): +1 when a star's long axis points at the centre, −1 when it runs around it.
        var radial = outer.Count == 0 ? 0 : outer.Average(s => s.Eccentricity * Math.Cos(2 * (s.Angle - Math.Atan2(Ny(s), Nx(s))))) /
            Math.Max(0.05, outer.Average(s => s.Eccentricity));

        return new OpticsReport {
            Stars = stars.Count,
            Regions = regions,
            Hfr = Round(median, 2),
            TiltPercent = Round(tiltPercent, 1),
            TiltAngle = Round(tiltAngle, 0),
            TiltUncertaintyPercent = Round(tiltError, 1),
            CurvaturePercent = Round(curvature, 1),
            // A sweet spot only means something when the field is curved enough to have a bottom.
            SweetSpotX = sweetX is double sx ? Round(sx, 2) : null,
            SweetSpotY = sweetY is double sy ? Round(sy, 2) : null,
            Radial = Round(Math.Clamp(radial, -1, 1), 2),
            EdgeEccentricity = Round(outer.Count > 0 ? MedianOf(outer.Select(s => s.Eccentricity)) : 0, 3),
            CenterEccentricity = Round(inner.Count > 0 ? MedianOf(inner.Select(s => s.Eccentricity)) : 0, 3),
            Scale = scale is double sc2 ? Round(sc2, 3) : null,
            GlowStars = all.Count(s => s.Glow >= GlowLimit),
        };
    }

    /// <summary>
    /// Measured size still depends a little on brightness (faint stars' wings sink into the noise; bright ones bloom),
    /// so a part of the frame with fainter stars would look softer. Each star is compared with the median of stars
    /// of about its brightness across the whole frame instead.
    /// </summary>
    private static void NormaliseByBrightness(List<Star> stars, double median) {
        const int bins = 8;
        if (stars.Count < bins * 10) {
            foreach (var s in stars) s.Size = s.Hfr;
            return;
        }
        var sorted = stars.OrderBy(s => s.Flux).ToList();
        var centres = new List<(double LogFlux, double Hfr)>();
        for (var b = 0; b < bins; b++) {
            var part = sorted.Skip(b * sorted.Count / bins).Take(sorted.Count / bins).ToList();
            centres.Add((MedianOf(part.Select(s => Math.Log10(s.Flux))), MedianOf(part.Select(s => s.Hfr))));
        }
        foreach (var s in stars) {
            var lf = Math.Log10(s.Flux);
            var i = Math.Clamp(centres.FindIndex(c => c.LogFlux >= lf), 1, bins - 1);
            var (x0, y0) = centres[i - 1];
            var (x1, y1) = centres[i];
            var expected = x1 > x0 ? y0 + (y1 - y0) * Math.Clamp((lf - x0) / (x1 - x0), -0.5, 1.5) : y0;
            s.Size = expected > 0.1 ? s.Hfr / expected * median : s.Hfr;
        }
    }

    /// <summary>
    /// Star size across the frame: a 5×5 grid of median HFRs (single stars are too noisy, and a dense corner mustn't
    /// outvote the rest) fitted with <c>HFR = a + b·x + c·y + d·r² + e·(x² − y²) + f·x·y</c>, cells far off the fit
    /// down-weighted (Tukey), plus r⁴. b, c: a gradient; d (and r⁴): round curvature; e, f soak up a field that's soft
    /// on two opposite sides (a tilted sensor focused in the middle, or astigmatism) so it doesn't leak into the
    /// others. The r⁴ term matters on a rectangular sensor: without it a round bowl, seen through a wide frame, fits
    /// as if one pair of sides were softer. A cell
    /// where many stars sat on a galaxy or nebula is left out whole: what's left there is mostly its fainter knots.
    /// </summary>
    private static double[]? FitField(List<Star> stars, List<Star> all, int w, int h, double xs, double ys) {
        const int fine = 5;
        var cells = new List<(double X, double Y, double Hfr, double Weight)>();
        for (var gy = 0; gy < fine; gy++) {
            for (var gx = 0; gx < fine; gx++) {
                bool In(Star s) => (int)(s.X * fine / w) == gx && (int)(s.Y * fine / h) == gy;
                var cell = stars.Where(In).ToList();
                if (cell.Count < 6) continue;
                var onGlow = all.Count(s => In(s) && s.Glow >= GlowLimit);
                if (onGlow > 0.25 * (onGlow + cell.Count)) continue;
                // At the cell's stars rather than its middle: the outer cells' stars sit nearer the edge than it.
                cells.Add((cell.Average(s => 2 * s.X / (w - 1) - 1) * xs, cell.Average(s => 2 * s.Y / (h - 1) - 1) * ys,
                    MedianOf(cell.Select(s => s.Size)), Math.Sqrt(cell.Count)));
            }
        }
        if (cells.Count < 12) return null;
        static double[] Row((double X, double Y, double Hfr, double Weight) c) {
            var r2 = c.X * c.X + c.Y * c.Y;
            return [1, c.X, c.Y, r2, c.X * c.X - c.Y * c.Y, c.X * c.Y, r2 * r2];
        }
        var weights = cells.Select(c => c.Weight).ToArray();
        double[] fit = [];
        for (var pass = 0; pass < 6; pass++) {
            fit = LeastSquares(cells.Select((c, i) => (Row(c), c.Hfr, weights[i])).ToList());
            var f = fit;
            var residuals = cells.Select(c => c.Hfr - Row(c).Zip(f, (x, k) => x * k).Sum()).ToArray();
            var spread = 1.4826 * MedianOf(residuals.Select(Math.Abs)) + 1e-3;
            for (var i = 0; i < cells.Count; i++) {
                var u = residuals[i] / (4.685 * spread);
                weights[i] = Math.Abs(u) < 1 ? cells[i].Weight * Math.Pow(1 - u * u, 2) : 0;
            }
        }
        return fit;
    }

    /// <summary>Elongation direction averaged on the doubled angle (0° and 180° are the same axis), 0–180°.</summary>
    private static double AverageAngle(List<Star> stars) {
        double c = 0, s = 0;
        foreach (var star in stars) {
            c += star.Eccentricity * Math.Cos(2 * star.Angle);
            s += star.Eccentricity * Math.Sin(2 * star.Angle);
        }
        return (Math.Atan2(s, c) / 2 * 180 / Math.PI + 180) % 180;
    }

    private static double MedianOf(IEnumerable<double> values) {
        var list = values.Where(double.IsFinite).OrderBy(v => v).ToList();
        return list.Count == 0 ? 0 : list[list.Count / 2];
    }

    private static double Round(double value, int digits) => double.IsFinite(value) ? Math.Round(value, digits) : 0;

    /// <summary>Weighted least squares via the normal equations (a handful of unknowns).</summary>
    internal static double[] LeastSquares(List<(double[] X, double Y, double Weight)> rows) {
        var n = rows[0].X.Length;
        var a = new double[n, n + 1];
        foreach (var (x, y, weight) in rows) {
            for (var i = 0; i < n; i++) {
                for (var j = 0; j < n; j++) a[i, j] += weight * x[i] * x[j];
                a[i, n] += weight * x[i] * y;
            }
        }
        for (var col = 0; col < n; col++) {
            var pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(a[r, col]));
            for (var k = 0; k <= n; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            if (Math.Abs(a[col, col]) < 1e-12) continue;
            for (var r = 0; r < n; r++) {
                if (r == col) continue;
                var factor = a[r, col] / a[col, col];
                for (var k = col; k <= n; k++) a[r, k] -= factor * a[col, k];
            }
        }
        var result = new double[n];
        for (var i = 0; i < n; i++) result[i] = Math.Abs(a[i, i]) < 1e-12 ? 0 : a[i, n] / a[i, i];
        return result;
    }
}
