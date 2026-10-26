namespace NightManager.Core.Analysis.Optics;

/// <summary>
/// How the optics looked in one sub, from the stars across the frame. Small (a few hundred bytes): the plugin does
/// the work on the PC, which has every pixel, and the app only draws these numbers.
/// Coordinates are normalised: x and y from -1 (left / top) to +1 (right / bottom), the frame centre at 0,0.
/// </summary>
public sealed record OpticsReport {
    /// <summary>Stars measured (after rejecting saturated, blended and edge stars).</summary>
    public required int Stars { get; init; }
    /// <summary>3×3 grid, row by row from the top left.</summary>
    public required IReadOnlyList<OpticsRegion> Regions { get; init; }
    /// <summary>Median HFR of the whole frame, in pixels of the sub as saved.</summary>
    public required double Hfr { get; init; }

    /// <summary>
    /// How much bigger stars are on one side of the frame than the other, in percent of the median HFR (0 = even).
    /// From one light frame this can't tell sensor tilt from collimation or a decentred element (all make one side
    /// softer); an autofocus run can (<see cref="SensorFocusReport"/>). A tilted sensor with focus in the middle (both
    /// sides slightly soft) barely shows at all in one frame.
    /// </summary>
    public required double TiltPercent { get; init; }
    /// <summary>Direction the stars get bigger towards, degrees: 0 = right, 90 = down (image orientation).</summary>
    public required double TiltAngle { get; init; }
    /// <summary>
    /// One standard error of <see cref="TiltPercent"/> from this frame's own stars (a bootstrap). Below about twice
    /// this, the gradient is within what the frame's mix of stars makes by itself: combine several frames first.
    /// </summary>
    public double? TiltUncertaintyPercent { get; init; }

    /// <summary>
    /// Field curvature: how much bigger stars are in the corners than in the centre, in percent of the median HFR
    /// (positive = edges softer: back-focus or flattener spacing; near zero = flat field).
    /// </summary>
    public required double CurvaturePercent { get; init; }

    /// <summary>
    /// Where stars are sharpest (the vertex of the fitted bowl), normalised. Far from 0,0 on a reflector or SCT means
    /// the optical axis isn't centred on the sensor: collimation (or tilt, which also moves it).
    /// </summary>
    /// Null when the field is flat (no bowl to find the bottom of).
    public double? SweetSpotX { get; init; }
    public double? SweetSpotY { get; init; }

    /// <summary>
    /// How stars away from the centre are stretched: +1 all pointing at the centre (radial: coma, a mis-spaced
    /// corrector), -1 all around it (tangential: astigmatism / field rotation), 0 no pattern.
    /// </summary>
    public required double Radial { get; init; }
    /// <summary>Median eccentricity of the outer stars (0 round, 0.5 visibly egg-shaped).</summary>
    public required double EdgeEccentricity { get; init; }
    public required double CenterEccentricity { get; init; }

    /// <summary>
    /// Sources left out because they sat on a galaxy or nebula (their sizes are the target's structure, not the
    /// optics). A target filling the frame leaves few stars to judge by.
    /// </summary>
    public int GlowStars { get; init; }

    /// <summary>
    /// The rotator's mechanical angle for this frame (degrees), when there is one. With a rotator the camera turns
    /// against the telescope: a gradient that stays put in the image across angles is in the camera (its tilt); one
    /// that turns with the angle is in the telescope (collimation, a decentred lens, the focuser).
    /// </summary>
    public double? RotatorAngle { get; init; }
    /// <summary>Side of the pier ("East"/"West") and altitude (degrees): sag in the focuser or camera can change with both.</summary>
    public string? PierSide { get; init; }
    public double? Altitude { get; init; }

    /// <summary>Arcseconds per pixel of the sub as saved, when focal length and pixel size are known.</summary>
    public double? Scale { get; init; }
}

public sealed record OpticsRegion {
    public required int Stars { get; init; }
    public double? Hfr { get; init; }
    public double? Fwhm { get; init; }
    public double? Eccentricity { get; init; }
    /// <summary>Average elongation direction, degrees 0–180 (0 = horizontal), weighted by how elongated stars are.</summary>
    public double? Angle { get; init; }
}
