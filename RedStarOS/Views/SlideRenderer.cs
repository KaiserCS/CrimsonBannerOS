using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Draws a slide with Skia. The editor, thumbnails, slide show, PDF and PNG export all use this one renderer.</summary>
public static class SlideRenderer
{
    private static readonly Dictionary<string, SKBitmap> Images = new();

    public static SKColor Parse(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        return SKColor.TryParse(hex, out var c) ? c : fallback;
    }

    public static string ToHex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>Renders the slide onto a new bitmap of the given pixel size.</summary>
    public static SKBitmap Render(Deck deck, Slide slide, int pw, int ph, bool editing, SlideItem? hideText = null)
    {
        var bmp = PaintDoc.Create(Math.Max(1, pw), Math.Max(1, ph), SKColors.White);
        using var c = new SKCanvas(bmp);
        c.Scale(pw / (float)deck.Width);
        Draw(c, deck, slide, editing, hideText);
        return bmp;
    }

    public static void Draw(SKCanvas c, Deck deck, Slide slide, bool editing, SlideItem? hideText = null)
    {
        c.Clear(Parse(slide.Background, SKColors.White));
        foreach (var it in slide.Items) DrawItem(c, deck, it, editing, it == hideText);
    }

    private static SKBitmap? ImageFor(Deck deck, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (Images.TryGetValue(id, out var cached)) return cached;
        if (!deck.Images.TryGetValue(id, out var b64)) return null;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(b64));
            var bmp = SKBitmap.Decode(ms);
            if (bmp != null) Images[id] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    private static void DrawItem(SKCanvas c, Deck deck, SlideItem it, bool editing, bool hideText)
    {
        c.Save();
        c.Translate((float)(it.X + it.W / 2), (float)(it.Y + it.H / 2));
        c.RotateDegrees((float)it.Rot);
        c.Translate((float)(-it.W / 2), (float)(-it.H / 2));
        var r = new SKRect(0, 0, (float)it.W, (float)it.H);

        var fill = Parse(it.Fill, SKColors.Transparent);
        bool hasFill = !string.IsNullOrEmpty(it.Fill);
        bool hasStroke = !string.IsNullOrEmpty(it.Stroke) && it.StrokeW > 0;
        var stroke = Parse(it.Stroke, SKColors.Black);

        switch (it.Type)
        {
            case "image":
            {
                var bmp = ImageFor(deck, it.ImageId);
                if (bmp != null)
                {
                    using var p = new SKPaint { IsAntialias = true };
                    c.DrawBitmapSmooth(bmp, r, p);
                }
                else
                {
                    using var p = new SKPaint { Color = new SKColor(200, 200, 200), Style = SKPaintStyle.Fill };
                    c.DrawRect(r, p);
                }
                if (hasStroke) StrokeRect(c, r, stroke, (float)it.StrokeW);
                break;
            }
            case "shape":
            {
                bool isLine = it.Shape == "Line";
                using var path = isLine ? PaintShapes.Line(new SKPoint(0, 0), new SKPoint(r.Right, r.Bottom))
                                        : PaintShapes.Box(Enum.TryParse<ShapeKind>(it.Shape, out var k) ? k : ShapeKind.Rectangle, r);
                if (hasFill && !isLine)
                {
                    using var fp = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
                    c.DrawPath(path, fp);
                }
                if (isLine || hasStroke)
                {
                    var col = hasStroke ? stroke : (hasFill ? fill : SKColors.Black);
                    using var sp = new SKPaint
                    {
                        Color = col, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = (float)Math.Max(1, it.StrokeW),
                        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
                    };
                    c.DrawPath(path, sp);
                }
                break;
            }
            default:
                if (hasFill)
                {
                    using var fp = new SKPaint { Color = fill, Style = SKPaintStyle.Fill, IsAntialias = true };
                    c.DrawRect(r, fp);
                }
                if (hasStroke) StrokeRect(c, r, stroke, (float)it.StrokeW);
                break;
        }

        if (!hideText && it.Type != "image")
        {
            if (it.Text.Length > 0) DrawText(c, it, it.Text, Parse(it.Color, SKColors.Black), r);
            else if (editing && it.Placeholder.Length > 0)
                DrawText(c, it, it.Placeholder, new SKColor(140, 140, 140, 200), r);
        }
        c.Restore();
    }

    private static void StrokeRect(SKCanvas c, SKRect r, SKColor color, float width)
    {
        using var p = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true };
        c.DrawRect(r, p);
    }

    private static List<string> Wrap(SKFont font, string para, float width)
    {
        var lines = new List<string>();
        if (para.Length == 0) { lines.Add(""); return lines; }
        var line = "";
        foreach (var w in para.Split(' '))
        {
            var candidate = line.Length == 0 ? w : line + " " + w;
            if (line.Length > 0 && font.MeasureText(candidate) > width) { lines.Add(line); line = w; }
            else line = candidate;
        }
        lines.Add(line);
        return lines;
    }

    public static SKTypeface Typeface(SlideItem it) => SKTypeface.FromFamilyName(it.Font,
        it.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
        it.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private static void DrawText(SKCanvas c, SlideItem it, string text, SKColor color, SKRect r)
    {
        const float pad = 12f;
        using var tf = Typeface(it);
        using var font = new SKFont(tf, (float)it.Size) { Edging = SKFontEdging.Antialias };
        using var p = new SKPaint { IsAntialias = true, Color = color };
        var fm = font.Metrics;
        float glyphH = fm.Descent - fm.Ascent;
        float lineH = glyphH * 1.18f;
        float maxW = Math.Max(10, r.Width - pad * 2);
        const string bullet = "•  ";
        float indent = it.Bullets ? font.MeasureText(bullet) : 0;

        var lines = new List<(string Text, bool First)>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var wrapped = Wrap(font, para, maxW - indent);
            for (int i = 0; i < wrapped.Count; i++) lines.Add((wrapped[i], i == 0));
        }

        float total = lines.Count * lineH;
        float y0 = it.VAlign switch
        {
            "middle" => r.MidY - total / 2,
            "bottom" => r.Bottom - pad - total,
            _ => r.Top + pad
        };

        for (int i = 0; i < lines.Count; i++)
        {
            var (line, first) = lines[i];
            float baseline = y0 + i * lineH - fm.Ascent + (lineH - glyphH) / 2;
            float w = font.MeasureText(line);
            float x = it.Align switch
            {
                "center" => r.MidX - w / 2,
                "right" => r.Right - pad - w,
                _ => r.Left + pad + indent
            };
            if (it.Bullets && first && line.Length > 0) c.DrawText(bullet, r.Left + pad, baseline, SKTextAlign.Left, font, p);
            c.DrawText(line, x, baseline, SKTextAlign.Left, font, p);
            if (it.Underline && line.Length > 0)
            {
                using var up = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
                float uy = baseline + (float)it.Size * 0.12f;
                c.DrawRect(new SKRect(x, uy, x + w, uy + Math.Max(1f, (float)it.Size / 14f)), up);
            }
        }
    }
}
