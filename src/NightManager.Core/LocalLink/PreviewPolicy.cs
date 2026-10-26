namespace NightManager.Core.LocalLink;

/// <summary>A rectangle of the full-size frame, in sensor pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// How big a sub may get on its way to a phone over the local link (docs/LOCAL-LINK.md): a preview of at most
/// <see cref="MaxPreviewEdge"/> px on the long edge, and native-resolution crops of at most <see cref="MaxCropEdge"/>
/// px for zooming in. The full frame never leaves the PC.
/// </summary>
public static class PreviewPolicy {
    public const int MaxPreviewEdge = 2560;
    public const int MaxCropEdge = 1024;
    public const int JpegQuality = 85;

    /// <summary>The preview's size: the frame scaled down (never up) so its long edge is at most the cap.</summary>
    public static (int Width, int Height) PreviewSize(int width, int height, int maxEdge = MaxPreviewEdge) {
        if (width <= 0 || height <= 0) return (0, 0);
        maxEdge = Math.Clamp(maxEdge, 256, MaxPreviewEdge);
        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// The crop for a zoom: centred on the asked point (fractions 0–1 of the frame, as the phone sees it), at most
    /// <see cref="MaxCropEdge"/> square, kept inside the frame. Null for a frame too small to need one.
    /// </summary>
    public static PixelRect? Crop(int width, int height, double centreX, double centreY, int edge = MaxCropEdge) {
        if (width <= 0 || height <= 0 || !double.IsFinite(centreX) || !double.IsFinite(centreY)) return null;
        edge = Math.Clamp(edge, 64, MaxCropEdge);
        var w = Math.Min(edge, width);
        var h = Math.Min(edge, height);
        var x = (int)Math.Round(Math.Clamp(centreX, 0, 1) * width - w / 2.0);
        var y = (int)Math.Round(Math.Clamp(centreY, 0, 1) * height - h / 2.0);
        return new PixelRect(Math.Clamp(x, 0, width - w), Math.Clamp(y, 0, height - h), w, h);
    }
}
