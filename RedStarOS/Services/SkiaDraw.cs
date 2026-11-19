using SkiaSharp;

namespace RedStarOS.Services;

/// <summary>SkiaSharp 3 replacements for the old SKPaint.FilterQuality drawing (smooth scaling of pictures).</summary>
public static class SkiaDraw
{
    /// <summary>Smooth scaling: bilinear with mipmaps, so shrinking a big picture doesn't shimmer.</summary>
    public static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public static void DrawBitmapSmooth(this SKCanvas canvas, SKBitmap bitmap, SKRect dest, SKPaint? paint = null)
    {
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, dest, Smooth, paint);
    }

    public static void DrawBitmapSmooth(this SKCanvas canvas, SKBitmap bitmap, float x, float y, SKPaint? paint = null)
    {
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, x, y, Smooth, paint);
    }
}
