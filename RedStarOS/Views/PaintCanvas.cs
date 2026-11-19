using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace RedStarOS.Views;

public enum Grip { None, N, NE, E, SE, S, SW, W, NW }

/// <summary>Displays the picture at any zoom, with grid, selection ants, floating selection, resize grips and a canvas-size ghost.</summary>
public sealed class PaintCanvas : Control
{
    private const double GripSize = 7;
    private WriteableBitmap? _image;
    private WriteableBitmap? _float;
    private double _zoom = 1;

    private static readonly IPen Ants1 = new Pen(Brushes.White, 1);
    private static readonly IPen Ants2 = new Pen(Brushes.Black, 1, new DashStyle(new double[] { 4, 4 }, 0));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), 1);
    private static readonly IPen GripPen = new Pen(Brushes.Black, 1);

    public PaintDoc? Doc { get; set; }
    public bool ShowGrid { get; set; }

    /// <summary>Current selection rectangle in picture pixels (null = none).</summary>
    public SKRectI? SelRect { get; set; }
    /// <summary>Free-form outline relative to the selection's original top-left; null for rectangles.</summary>
    public List<SKPoint>? SelPoly { get; set; }
    public int SelOrigW { get; set; } = 1;
    public int SelOrigH { get; set; } = 1;
    /// <summary>Outline of a free-form selection still being drawn (absolute picture pixels).</summary>
    public List<SKPoint>? Marquee { get; set; }
    /// <summary>Preview of the canvas size while its grips are dragged.</summary>
    public PixelSize? Ghost { get; set; }

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.125, 16);
            RenderOptions.SetBitmapInterpolationMode(this, _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Lifted selection pixels drawn over the picture (null = nothing lifted).</summary>
    public void SetFloat(SKBitmap? content)
    {
        _float = content == null ? null : ToAvalonia(content);
        InvalidateVisual();
    }

    public unsafe void Refresh()
    {
        if (Doc == null) return;
        var b = Doc.Bmp;
        if (_image == null || _image.PixelSize.Width != b.Width || _image.PixelSize.Height != b.Height)
            _image = new WriteableBitmap(new PixelSize(b.Width, b.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Upload(b, _image);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static unsafe void Upload(SKBitmap b, WriteableBitmap target)
    {
        using var fb = target.Lock();
        byte* src = (byte*)b.GetPixels().ToPointer();
        byte* dst = (byte*)fb.Address.ToPointer();
        long row = (long)b.Width * 4;
        for (int y = 0; y < b.Height; y++)
            Buffer.MemoryCopy(src + (long)y * b.RowBytes, dst + (long)y * fb.RowBytes, row, row);
    }

    public static WriteableBitmap ToAvalonia(SKBitmap b)
    {
        var wb = new WriteableBitmap(new PixelSize(b.Width, b.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Upload(b, wb);
        return wb;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Doc == null) return new Size(10, 10);
        return new Size(Doc.Width * _zoom + 16, Doc.Height * _zoom + 16);
    }

    // ------------------------------------------------------------ grips

    public Rect ImageRect => Doc == null ? default : new Rect(0, 0, Doc.Width * _zoom, Doc.Height * _zoom);

    private Rect SelScreen()
    {
        var r = SelRect!.Value;
        return new Rect(r.Left * _zoom, r.Top * _zoom, r.Width * _zoom, r.Height * _zoom);
    }

    private IEnumerable<(Grip, Point)> GripPoints()
    {
        if (SelRect != null)
        {
            var r = SelScreen();
            yield return (Grip.NW, r.TopLeft);
            yield return (Grip.N, new Point(r.Center.X, r.Top));
            yield return (Grip.NE, r.TopRight);
            yield return (Grip.E, new Point(r.Right, r.Center.Y));
            yield return (Grip.SE, r.BottomRight);
            yield return (Grip.S, new Point(r.Center.X, r.Bottom));
            yield return (Grip.SW, r.BottomLeft);
            yield return (Grip.W, new Point(r.Left, r.Center.Y));
        }
        else if (Doc != null)
        {
            var r = ImageRect;
            yield return (Grip.E, new Point(r.Right + 4, r.Center.Y));
            yield return (Grip.S, new Point(r.Center.X, r.Bottom + 4));
            yield return (Grip.SE, new Point(r.Right + 4, r.Bottom + 4));
        }
    }

    public Grip HitGrip(Point p)
    {
        foreach (var (g, c) in GripPoints())
            if (Math.Abs(p.X - c.X) <= GripSize / 2 + 2 && Math.Abs(p.Y - c.Y) <= GripSize / 2 + 2) return g;
        return Grip.None;
    }

    // ------------------------------------------------------------ painting

    public override void Render(DrawingContext ctx)
    {
        if (Doc == null || _image == null) return;
        double z = _zoom;
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // makes the whole area (grips included) hit-testable
        var dest = new Rect(0, 0, Doc.Width * z, Doc.Height * z);
        ctx.DrawImage(_image, new Rect(0, 0, _image.PixelSize.Width, _image.PixelSize.Height), dest);

        if (_float != null && SelRect is SKRectI fr)
            ctx.DrawImage(_float, new Rect(0, 0, _float.PixelSize.Width, _float.PixelSize.Height),
                new Rect(fr.Left * z, fr.Top * z, fr.Width * z, fr.Height * z));

        if (ShowGrid && z >= 4)
        {
            for (int x = 1; x < Doc.Width; x++) ctx.DrawLine(GridPen, new Point(x * z, 0), new Point(x * z, dest.Height));
            for (int y = 1; y < Doc.Height; y++) ctx.DrawLine(GridPen, new Point(0, y * z), new Point(dest.Width, y * z));
        }

        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)), 1), new Rect(-0.5, -0.5, dest.Width + 1, dest.Height + 1));

        if (SelRect is SKRectI sr)
        {
            if (SelPoly != null && SelPoly.Count > 2)
            {
                double sx = sr.Width / (double)Math.Max(1, SelOrigW), sy = sr.Height / (double)Math.Max(1, SelOrigH);
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(new Point((sr.Left + SelPoly[0].X * sx) * z, (sr.Top + SelPoly[0].Y * sy) * z), false);
                    for (int i = 1; i < SelPoly.Count; i++)
                        gc.LineTo(new Point((sr.Left + SelPoly[i].X * sx) * z, (sr.Top + SelPoly[i].Y * sy) * z));
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(null, Ants1, g);
                ctx.DrawGeometry(null, Ants2, g);
            }
            else
            {
                var r = SelScreen();
                ctx.DrawRectangle(null, Ants1, r);
                ctx.DrawRectangle(null, Ants2, r);
            }
        }

        if (Marquee != null && Marquee.Count > 1)
        {
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(Marquee[0].X * z, Marquee[0].Y * z), false);
                for (int i = 1; i < Marquee.Count; i++) gc.LineTo(new Point(Marquee[i].X * z, Marquee[i].Y * z));
                gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, Ants1, g);
            ctx.DrawGeometry(null, Ants2, g);
        }

        if (Ghost is PixelSize gs)
        {
            var r = new Rect(0, 0, gs.Width * z, gs.Height * z);
            ctx.DrawRectangle(null, Ants1, r);
            ctx.DrawRectangle(null, Ants2, r);
        }

        foreach (var (_, c) in GripPoints())
            ctx.DrawRectangle(Brushes.White, GripPen, new Rect(c.X - GripSize / 2, c.Y - GripSize / 2, GripSize, GripSize));
    }
}
