using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace RedStarOS.Views;

public enum SlideHandle { None, NW, N, NE, E, SE, S, SW, W, Rotate }

/// <summary>The editing surface: the current slide fitted into the available space, with selection handles and smart guides.</summary>
public sealed class SlideCanvas : Control
{
    private const double HandleSize = 9;
    private const double PageMargin = 28;
    private WriteableBitmap? _img;
    private double _scale = 1;
    private Point _origin;

    private static readonly IPen SelPen = new Pen(new SolidColorBrush(Color.Parse("#2B8CFF")), 1.5);
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#2B8CFF")), 1.5);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.Parse("#FF3B8E")), 1, new DashStyle(new double[] { 4, 3 }, 0));

    public Deck? Deck { get; set; }
    public Slide? Slide { get; set; }
    public SlideItem? Selected { get; set; }
    public SlideItem? HideText { get; set; }
    public List<double> GuidesX { get; } = new();
    public List<double> GuidesY { get; } = new();
    public double Scale => _scale;
    public Point Origin => _origin;

    public SlideCanvas()
    {
        Focusable = true;
        SizeChanged += (_, _) => Redraw();
    }

    private void Layout()
    {
        if (Deck == null) return;
        double aw = Math.Max(40, Bounds.Width - PageMargin * 2), ah = Math.Max(40, Bounds.Height - PageMargin * 2);
        _scale = Math.Min(aw / Deck.Width, ah / Deck.Height);
        _origin = new Point((Bounds.Width - Deck.Width * _scale) / 2, (Bounds.Height - Deck.Height * _scale) / 2);
    }

    public void Redraw()
    {
        if (Deck == null || Slide == null) { InvalidateVisual(); return; }
        Layout();
        int pw = Math.Max(16, (int)Math.Round(Deck.Width * _scale)), ph = Math.Max(16, (int)Math.Round(Deck.Height * _scale));
        using var sk = SlideRenderer.Render(Deck, Slide, pw, ph, true, HideText);
        _img = PaintCanvas.ToAvalonia(sk);
        InvalidateVisual();
    }

    public SKPoint ToSlide(Point p) => new((float)((p.X - _origin.X) / _scale), (float)((p.Y - _origin.Y) / _scale));

    // ------------------------------------------------------------ hit testing

    private static (double Cx, double Cy) Centre(SlideItem it) => (it.X + it.W / 2, it.Y + it.H / 2);

    private static Point ToLocal(SlideItem it, double x, double y)
    {
        var (cx, cy) = Centre(it);
        double a = -it.Rot * Math.PI / 180, dx = x - cx, dy = y - cy;
        return new Point(dx * Math.Cos(a) - dy * Math.Sin(a), dx * Math.Sin(a) + dy * Math.Cos(a));
    }

    public SlideItem? HitItem(SKPoint p)
    {
        if (Slide == null) return null;
        for (int i = Slide.Items.Count - 1; i >= 0; i--)
        {
            var it = Slide.Items[i];
            var l = ToLocal(it, p.X, p.Y);
            double tol = it.Type == "shape" && it.Shape == "Line" ? 10 : 0;
            if (Math.Abs(l.X) <= it.W / 2 + tol && Math.Abs(l.Y) <= it.H / 2 + tol) return it;
        }
        return null;
    }

    private IEnumerable<(SlideHandle, double, double)> HandlePoints(SlideItem it)
    {
        double w2 = it.W * _scale / 2, h2 = it.H * _scale / 2;
        yield return (SlideHandle.NW, -w2, -h2);
        yield return (SlideHandle.N, 0, -h2);
        yield return (SlideHandle.NE, w2, -h2);
        yield return (SlideHandle.E, w2, h2 * 0);
        yield return (SlideHandle.SE, w2, h2);
        yield return (SlideHandle.S, 0, h2);
        yield return (SlideHandle.SW, -w2, h2);
        yield return (SlideHandle.W, -w2, 0);
        yield return (SlideHandle.Rotate, 0, -h2 - 28);
    }

    public SlideHandle HitHandle(Point screen)
    {
        if (Selected == null) return SlideHandle.None;
        var (cx, cy) = Centre(Selected);
        double sx = _origin.X + cx * _scale, sy = _origin.Y + cy * _scale;
        double a = -Selected.Rot * Math.PI / 180, dx = screen.X - sx, dy = screen.Y - sy;
        double lx = dx * Math.Cos(a) - dy * Math.Sin(a), ly = dx * Math.Sin(a) + dy * Math.Cos(a);
        foreach (var (h, hx, hy) in HandlePoints(Selected))
            if (Math.Abs(lx - hx) <= HandleSize && Math.Abs(ly - hy) <= HandleSize) return h;
        return SlideHandle.None;
    }

    // ------------------------------------------------------------ painting

    public override void Render(DrawingContext ctx)
    {
        ctx.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Deck == null || _img == null) return;
        var dest = new Rect(_origin.X, _origin.Y, Deck.Width * _scale, Deck.Height * _scale);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, new Rect(dest.X + 3, dest.Y + 4, dest.Width, dest.Height), 3, 3);
        ctx.DrawImage(_img, new Rect(0, 0, _img.PixelSize.Width, _img.PixelSize.Height), dest);

        foreach (var gx in GuidesX) ctx.DrawLine(GuidePen, new Point(_origin.X + gx * _scale, dest.Top), new Point(_origin.X + gx * _scale, dest.Bottom));
        foreach (var gy in GuidesY) ctx.DrawLine(GuidePen, new Point(dest.Left, _origin.Y + gy * _scale), new Point(dest.Right, _origin.Y + gy * _scale));

        if (Selected == null) return;
        var (cx, cy) = Centre(Selected);
        var m = Matrix.CreateRotation(Selected.Rot * Math.PI / 180) * Matrix.CreateTranslation(_origin.X + cx * _scale, _origin.Y + cy * _scale);
        using (ctx.PushTransform(m))
        {
            double w2 = Selected.W * _scale / 2, h2 = Selected.H * _scale / 2;
            ctx.DrawRectangle(null, SelPen, new Rect(-w2, -h2, w2 * 2, h2 * 2));
            ctx.DrawLine(SelPen, new Point(0, -h2), new Point(0, -h2 - 28));
            foreach (var (h, hx, hy) in HandlePoints(Selected))
            {
                if (h == SlideHandle.Rotate) ctx.DrawEllipse(Brushes.White, HandlePen, new Point(hx, hy), 6, 6);
                else ctx.DrawRectangle(Brushes.White, HandlePen, new Rect(hx - HandleSize / 2, hy - HandleSize / 2, HandleSize, HandleSize));
            }
        }
    }
}
