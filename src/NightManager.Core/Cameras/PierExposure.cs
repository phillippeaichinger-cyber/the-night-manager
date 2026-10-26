namespace NightManager.Core.Cameras;

/// <summary>
/// Exposure for a pier camera driven by the plugin: a daylight setting and a darkness setting (chosen from the
/// observatory's twilight), each finding its own exposure from the last picture's brightness. After dark it never
/// goes past the camera's cap (default 2 s, at most 10 s): the picture is for seeing the kit, not the sky, and a long
/// exposure would hold the camera (and the USB bus) for nothing. An IR lamp on the pier just means it settles shorter.
/// </summary>
public static class PierExposure {
    public const double DefaultMaxNightSeconds = 2;
    public const double MinNightCapSeconds = 0.1;
    public const double MaxNightCapSeconds = 10;

    /// <summary>Shortest and longest exposure in daylight, microseconds.</summary>
    public const int DayMinMicroseconds = 32;
    public const int DayMaxMicroseconds = 1_000_000;
    public const int NightMinMicroseconds = 1_000;

    /// <summary>Where a picture's median brightness should sit (8-bit), and the band that's good enough to keep.</summary>
    public const double TargetMedian = 70;
    public const double GoodLow = 25;
    public const double GoodHigh = 170;

    public static int StartMicroseconds(bool dark, double maxNightSeconds) =>
        dark ? Math.Min(1_000_000, Cap(dark, maxNightSeconds)) : 1_000;

    /// <summary>Longest exposure allowed now.</summary>
    public static int Cap(bool dark, double maxNightSeconds) =>
        dark ? (int)(Math.Clamp(maxNightSeconds, MinNightCapSeconds, MaxNightCapSeconds) * 1_000_000) : DayMaxMicroseconds;

    public static int Floor(bool dark) => dark ? NightMinMicroseconds : DayMinMicroseconds;

    /// <summary>Is this picture usable as it is?</summary>
    public static bool Good(double median, double saturated) => median is >= GoodLow and <= GoodHigh && saturated < 0.1;

    /// <summary>
    /// The next exposure from this one's result: aim the median at <see cref="TargetMedian"/>, at most ×4 or ÷4 a
    /// step (one bright light in the frame mustn't swing it), cut hard when much of the frame is burnt out.
    /// </summary>
    public static int Next(int microseconds, double median, double saturated, bool dark, double maxNightSeconds) {
        double next;
        if (saturated > 0.25) {
            next = microseconds * 0.2;
        } else if (median < 1) {
            next = microseconds * 4;
        } else {
            next = microseconds * Math.Clamp(TargetMedian / median, 0.25, 4);
        }
        return (int)Math.Clamp(next, Floor(dark), Cap(dark, maxNightSeconds));
    }
}
