using System;
using System.Collections.Generic;
using SkiaSharp;

namespace RedStarOS.Views;

public enum ShapeKind
{
    Line, Curve, Oval, Rectangle, RoundedRectangle, Polygon, Triangle, RightTriangle, Diamond, Pentagon, Hexagon,
    ArrowRight, ArrowLeft, ArrowUp, ArrowDown, Star4, Star5, Star6, Heart, Lightning
}

public enum FillMode { Outline, OutlineAndFill, Solid }

/// <summary>Geometry for the Shapes gallery plus the common stroke/fill painter.</summary>
public static class PaintShapes
{
    public static readonly (ShapeKind Kind, string Name)[] All =
    {
        (ShapeKind.Line, "Line"), (ShapeKind.Curve, "Curve"), (ShapeKind.Oval, "Oval"), (ShapeKind.Rectangle, "Rectangle"),
        (ShapeKind.RoundedRectangle, "Rounded rectangle"), (ShapeKind.Polygon, "Polygon"), (ShapeKind.Triangle, "Triangle"),
        (ShapeKind.RightTriangle, "Right triangle"), (ShapeKind.Diamond, "Diamond"), (ShapeKind.Pentagon, "Pentagon"),
        (ShapeKind.Hexagon, "Hexagon"), (ShapeKind.ArrowRight, "Right arrow"), (ShapeKind.ArrowLeft, "Left arrow"),
        (ShapeKind.ArrowUp, "Up arrow"), (ShapeKind.ArrowDown, "Down arrow"), (ShapeKind.Star4, "4-point star"),
        (ShapeKind.Star5, "5-point star"), (ShapeKind.Star6, "6-point star"), (ShapeKind.Heart, "Heart"),
        (ShapeKind.Lightning, "Lightning")
    };

    /// <summary>Shapes defined by a dragged bounding box (everything except line, curve and polygon).</summary>
    public static SKPath Box(ShapeKind kind, SKRect r)
    {
        var p = new SKPath();
        float cx = r.MidX, cy = r.MidY;
        switch (kind)
        {
            case ShapeKind.Oval: p.AddOval(r); break;
            case ShapeKind.Rectangle: p.AddRect(r); break;
            case ShapeKind.RoundedRectangle:
                float rad = Math.Min(r.Width, r.Height) * 0.22f;
                p.AddRoundRect(r, rad, rad);
                break;
            case ShapeKind.Triangle: Poly(p, new[] { new SKPoint(cx, r.Top), new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom) }); break;
            case ShapeKind.RightTriangle: Poly(p, new[] { new SKPoint(r.Left, r.Top), new SKPoint(r.Left, r.Bottom), new SKPoint(r.Right, r.Bottom) }); break;
            case ShapeKind.Diamond: Poly(p, new[] { new SKPoint(cx, r.Top), new SKPoint(r.Right, cy), new SKPoint(cx, r.Bottom), new SKPoint(r.Left, cy) }); break;
            case ShapeKind.Pentagon: Poly(p, Regular(r, 5, -90, 1f, 1f)); break;
            case ShapeKind.Hexagon: Poly(p, Regular(r, 6, 0, 1f, 1f)); break;
            case ShapeKind.Star4: Poly(p, Star(r, 4, 0.4f)); break;
            case ShapeKind.Star5: Poly(p, Star(r, 5, 0.382f)); break;
            case ShapeKind.Star6: Poly(p, Star(r, 6, 0.55f)); break;
            case ShapeKind.ArrowRight: Poly(p, Arrow(r, 0)); break;
            case ShapeKind.ArrowLeft: Poly(p, Arrow(r, 1)); break;
            case ShapeKind.ArrowUp: Poly(p, Arrow(r, 2)); break;
            case ShapeKind.ArrowDown: Poly(p, Arrow(r, 3)); break;
            case ShapeKind.Heart: Heart(p, r); break;
            case ShapeKind.Lightning:
                Poly(p, Unit(r, new (float, float)[] { (0.55f, 0f), (0.2f, 0.55f), (0.47f, 0.55f), (0.35f, 1f), (0.8f, 0.4f), (0.53f, 0.4f), (0.8f, 0f) }));
                break;
        }
        return p;
    }

    public static SKPath Polyline(IReadOnlyList<SKPoint> pts, bool close)
    {
        var p = new SKPath();
        if (pts.Count == 0) return p;
        p.MoveTo(pts[0]);
        for (int i = 1; i < pts.Count; i++) p.LineTo(pts[i]);
        if (close && pts.Count > 2) p.Close();
        return p;
    }

    public static SKPath Line(SKPoint a, SKPoint b)
    {
        var p = new SKPath();
        p.MoveTo(a);
        p.LineTo(b);
        return p;
    }

    public static SKPath Curve(SKPoint a, SKPoint b, SKPoint c1, SKPoint c2)
    {
        var p = new SKPath();
        p.MoveTo(a);
        p.CubicTo(c1, c2, b);
        return p;
    }

    /// <summary>Strokes and/or fills a path the way Paint's outline/fill options do.</summary>
    public static void Paint(SKCanvas c, SKPath path, SKColor fg, SKColor bg, FillMode mode, float size, bool smooth, bool closedShape)
    {
        if (closedShape && mode != FillMode.Outline)
        {
            using var fp = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = smooth, Color = mode == FillMode.Solid ? fg : bg };
            c.DrawPath(path, fp);
        }
        if (!closedShape || mode != FillMode.Solid)
        {
            using var sp = new SKPaint
            {
                Style = SKPaintStyle.Stroke, IsAntialias = smooth, Color = fg, StrokeWidth = Math.Max(1f, size),
                StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Miter, StrokeMiter = 4f
            };
            c.DrawPath(path, sp);
        }
    }

    // ------------------------------------------------------------ geometry helpers

    private static void Poly(SKPath p, SKPoint[] pts)
    {
        p.MoveTo(pts[0]);
        for (int i = 1; i < pts.Length; i++) p.LineTo(pts[i]);
        p.Close();
    }

    private static SKPoint[] Unit(SKRect r, (float x, float y)[] u)
    {
        var o = new SKPoint[u.Length];
        for (int i = 0; i < u.Length; i++) o[i] = new SKPoint(r.Left + u[i].x * r.Width, r.Top + u[i].y * r.Height);
        return o;
    }

    private static SKPoint[] Regular(SKRect r, int n, float startDeg, float sx, float sy)
    {
        var pts = new SKPoint[n];
        float rx = r.Width / 2 * sx, ry = r.Height / 2 * sy;
        for (int i = 0; i < n; i++)
        {
            double a = (startDeg + 360.0 * i / n) * Math.PI / 180;
            pts[i] = new SKPoint(r.MidX + rx * (float)Math.Cos(a), r.MidY + ry * (float)Math.Sin(a));
        }
        return pts;
    }

    private static SKPoint[] Star(SKRect r, int points, float inner)
    {
        var pts = new SKPoint[points * 2];
        float rx = r.Width / 2, ry = r.Height / 2;
        for (int i = 0; i < points * 2; i++)
        {
            double a = (-90 + 180.0 * i / points) * Math.PI / 180;
            float k = i % 2 == 0 ? 1f : inner;
            pts[i] = new SKPoint(r.MidX + rx * k * (float)Math.Cos(a), r.MidY + ry * k * (float)Math.Sin(a));
        }
        return pts;
    }

    // dir: 0 right, 1 left, 2 up, 3 down
    private static SKPoint[] Arrow(SKRect r, int dir)
    {
        var u = new (float, float)[] { (0f, .25f), (.6f, .25f), (.6f, 0f), (1f, .5f), (.6f, 1f), (.6f, .75f), (0f, .75f) };
        var o = new SKPoint[u.Length];
        for (int i = 0; i < u.Length; i++)
        {
            var (a, b) = u[i];
            (float x, float y) = dir switch { 0 => (a, b), 1 => (1 - a, b), 2 => (b, 1 - a), _ => (b, a) };
            o[i] = new SKPoint(r.Left + x * r.Width, r.Top + y * r.Height);
        }
        return o;
    }

    private static void Heart(SKPath p, SKRect r)
    {
        SKPoint P(float x, float y) => new(r.Left + x * r.Width, r.Top + y * r.Height);
        p.MoveTo(P(.5f, .92f));
        p.CubicTo(P(0f, .55f), P(.05f, .05f), P(.3f, .08f));
        p.CubicTo(P(.42f, .1f), P(.5f, .2f), P(.5f, .28f));
        p.CubicTo(P(.5f, .2f), P(.58f, .1f), P(.7f, .08f));
        p.CubicTo(P(.95f, .05f), P(1f, .55f), P(.5f, .92f));
        p.Close();
    }
}
