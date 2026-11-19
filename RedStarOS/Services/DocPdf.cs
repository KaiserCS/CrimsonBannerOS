using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace RedStarOS.Services;

/// <summary>Vector PDF export (selectable text, links, tables, images, page numbers) drawn straight with Skia.</summary>
internal static class SkFontExt
{
    public static float Measure(this SKFont f, string s) => s.Length == 0 ? 0 : f.MeasureText(f.Typeface.GetGlyphs(s));
}

public static class DocPdf
{
    private sealed class Word { public string Text = ""; public Run Run = null!; public float W; }
    private sealed class Line { public List<Word> Words = new(); public float W, H; }

    private static readonly Dictionary<string, SKTypeface> Faces = new();

    private static SKTypeface Face(string family, bool bold, bool italic)
    {
        var key = family + "|" + bold + "|" + italic;
        if (Faces.TryGetValue(key, out var f)) return f;
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        f = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        Faces[key] = f;
        return f;
    }

    private static SKColor ColorOf(string? hex, SKColor fallback) => hex != null && SKColor.TryParse(hex, out var c) ? c : fallback;

    private static (SKFont font, double size) FontFor(Para p, Run r)
    {
        double size = r.Size > 0 ? r.Size : RichDoc.DefaultSize(p.Style);
        if (r.Sup || r.Sub) size *= 0.7;
        bool bold = r.Bold || RichDoc.DefaultBold(p.Style), italic = r.Italic || RichDoc.DefaultItalic(p.Style);
        var fam = !string.IsNullOrEmpty(r.Font) ? r.Font! : p.Style == PStyle.Code ? "DejaVu Sans Mono" : "Noto Sans";
        return (new SKFont(Face(fam, bold, italic), (float)size) { Subpixel = true }, size);
    }

    private static List<Line> Wrap(Para p, float width)
    {
        var lines = new List<Line>();
        var cur = new Line();
        float maxPt = (float)RichDoc.DefaultSize(p.Style);
        foreach (var r in p.Runs) if (r.Size > maxPt) maxPt = (float)r.Size;
        float defH = maxPt * 1.25f * (float)p.Spacing;
        void Break() { cur.H = Math.Max(cur.H, defH); lines.Add(cur); cur = new Line(); }

        foreach (var r in p.Runs)
        {
            var (font, size) = FontFor(p, r);
            var parts = System.Text.RegularExpressions.Regex.Split(r.Text, @"(?<= )");
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                var w = font.Measure(part);
                var trimmed = part.TrimEnd(' ');
                float wt = trimmed.Length == part.Length ? w : font.Measure(trimmed);
                if (cur.W + wt > width && cur.Words.Count > 0) Break();
                cur.Words.Add(new Word { Text = part, Run = r, W = w });
                cur.W += w;
                float h = (float)size * 1.25f * (float)p.Spacing;
                if (h > cur.H) cur.H = h;
            }
        }
        Break();
        return lines;
    }

    /// <summary>Draws one wrapped line; returns nothing. <paramref name="y"/> is the top of the line.</summary>
    private static void DrawLine(SKCanvas c, Para p, Line ln, float x, float y, float width, bool lastLine)
    {
        float startX = x;
        float extraPerSpace = 0;
        if (p.Align == Align.Center) startX = x + (width - TrimW(ln)) / 2;
        else if (p.Align == Align.Right) startX = x + width - TrimW(ln);
        else if (p.Align == Align.Justify && !lastLine)
        {
            int spaces = ln.Words.Count(w => w.Text.EndsWith(' '));
            if (spaces > 0) extraPerSpace = Math.Max(0, (width - TrimW(ln)) / spaces);
        }
        float cx = startX;
        foreach (var w in ln.Words)
        {
            var (font, size) = FontFor(p, w.Run);
            float baseline = y + ln.H * 0.8f;
            if (w.Run.Sup) baseline -= (float)size * 0.5f;
            if (w.Run.Sub) baseline += (float)size * 0.25f;
            if (w.Run.Highlight != null)
            {
                using var hp = new SKPaint { Color = ColorOf(w.Run.Highlight, SKColors.Yellow) };
                c.DrawRect(cx, y, w.W, ln.H, hp);
            }
            var col = w.Run.Link != null && w.Run.Color == null ? new SKColor(0x0B, 0x57, 0xD0) : ColorOf(w.Run.Color, p.Style == PStyle.Quote ? new SKColor(0x55, 0x55, 0x55) : SKColors.Black);
            using var paint = new SKPaint { Color = col, IsAntialias = true };
            c.DrawText(w.Text, cx, baseline, font, paint);
            float tw = font.Measure(w.Text.TrimEnd(' '));
            using var lp = new SKPaint { Color = col, StrokeWidth = Math.Max(0.5f, (float)size / 16), IsAntialias = true };
            if (w.Run.Underline || w.Run.Link != null) c.DrawLine(cx, baseline + 1.5f, cx + tw, baseline + 1.5f, lp);
            if (w.Run.Strike) c.DrawLine(cx, baseline - (float)size * 0.3f, cx + tw, baseline - (float)size * 0.3f, lp);
            if (w.Run.Link != null && !w.Run.Link.StartsWith("#"))
                try { c.DrawUrlAnnotation(new SKRect(cx, y, cx + tw, y + ln.H), w.Run.Link); } catch { }
            cx += w.W + (w.Text.EndsWith(' ') ? extraPerSpace : 0);
        }
    }

    private static float TrimW(Line ln)
    {
        float w = ln.W;
        if (ln.Words.Count > 0 && ln.Words[^1].Text.EndsWith(' '))
        {
            w -= ln.Words[^1].W - (ln.Words[^1].W * ln.Words[^1].Text.TrimEnd(' ').Length / Math.Max(1, ln.Words[^1].Text.Length));
        }
        return w;
    }

    public static void Save(RichDoc doc, string path, bool letter, bool pageNumbers, string title)
    {
        float pw = letter ? 612 : 595, ph = letter ? 792 : 842, margin = 72;
        float cw = pw - 2 * margin, bottom = ph - margin;

        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var pdf = SKDocument.CreatePdf(fs, new SKDocumentPdfMetadata { Title = title, Creator = "Red Star Writer" }))
        {
            int page = 0;
            SKCanvas? canvas = null;
            float y = margin;

            void NewPage()
            {
                if (canvas != null) { Footer(); pdf.EndPage(); }
                canvas = pdf.BeginPage(pw, ph);
                page++; y = margin;
            }
            void Footer()
            {
                if (!pageNumbers || canvas == null) return;
                using var f = new SKFont(Face("Noto Sans", false, false), 10);
                using var pt = new SKPaint { Color = new SKColor(0x66, 0x66, 0x66), IsAntialias = true };
                var s = page.ToString();
                canvas.DrawText(s, pw / 2 - f.Measure(s) / 2, ph - 36, f, pt);
            }
            NewPage();

            var counters = new int[12];
            foreach (var b in doc.Blocks)
            {
                switch (b.Kind)
                {
                    case BlockKind.Para:
                    {
                        var p = b.Para!;
                        float indent = p.List != ListKind.None ? 18 * (p.Level + 1) : p.Level * 18f;
                        if (p.Style == PStyle.Quote) indent += 12;
                        if (p.List == ListKind.Number) { int lv = Math.Clamp(p.Level, 0, 11); counters[lv]++; for (int k = lv + 1; k < 12; k++) counters[k] = 0; }
                        else if (p.List == ListKind.None) Array.Clear(counters);
                        y += (float)RichDoc.SpaceBefore(p.Style) * 0.8f * (y > margin ? 1 : 0);
                        var lines = Wrap(p, cw - indent);
                        for (int li = 0; li < lines.Count; li++)
                        {
                            var ln = lines[li];
                            if (y + ln.H > bottom) NewPage();
                            if (p.Style == PStyle.Code)
                            {
                                using var bg = new SKPaint { Color = new SKColor(0xF1, 0xF3, 0xF4) };
                                canvas!.DrawRect(margin + indent - 4, y, cw - indent + 8, ln.H, bg);
                            }
                            if (p.Style == PStyle.Quote)
                            {
                                using var qb = new SKPaint { Color = new SKColor(0xB0, 0xB7, 0xC3) };
                                canvas!.DrawRect(margin + indent - 10, y, 2.5f, ln.H, qb);
                            }
                            if (li == 0 && p.List != ListKind.None)
                            {
                                var mk = p.List == ListKind.Bullet ? (p.Level % 2 == 0 ? "•" : "◦") : counters[Math.Clamp(p.Level, 0, 11)] + ".";
                                var (mf, _) = FontFor(p, p.Runs.FirstOrDefault() ?? new Run());
                                using var mp = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                                canvas!.DrawText(mk, margin + indent - 4 - mf.Measure(mk), y + ln.H * 0.8f, mf, mp);
                            }
                            DrawLine(canvas!, p, ln, margin + indent, y, cw - indent, li == lines.Count - 1);
                            y += ln.H;
                        }
                        y += (float)RichDoc.SpaceAfter(p.Style) * (p.Style == PStyle.Code ? 0.2f : 0.8f);
                        break;
                    }
                    case BlockKind.Table:
                    {
                        Array.Clear(counters);
                        var rows = b.Rows!;
                        int cols = rows.Max(r => r.Count);
                        float colW = cw / cols, pad = 4;
                        foreach (var row in rows)
                        {
                            var cellLines = row.Select(c => Wrap(c, colW - 2 * pad)).ToList();
                            float rh = cellLines.Max(l => l.Sum(x => x.H)) + 2 * pad;
                            if (y + rh > bottom) NewPage();
                            using var bp = new SKPaint { Color = new SKColor(0x8A, 0x8A, 0x8A), Style = SKPaintStyle.Stroke, StrokeWidth = 0.7f };
                            for (int ci = 0; ci < row.Count; ci++)
                            {
                                canvas!.DrawRect(margin + ci * colW, y, colW, rh, bp);
                                float cy = y + pad;
                                var ls = cellLines[ci];
                                for (int li = 0; li < ls.Count; li++)
                                {
                                    DrawLine(canvas, row[ci], ls[li], margin + ci * colW + pad, cy, colW - 2 * pad, li == ls.Count - 1);
                                    cy += ls[li].H;
                                }
                            }
                            y += rh;
                        }
                        y += 10;
                        break;
                    }
                    case BlockKind.Image:
                    {
                        Array.Clear(counters);
                        if (b.Image == null) break;
                        using var bmp = SKBitmap.Decode(b.Image);
                        if (bmp == null) break;
                        float w = (float)(b.ImgW > 0 ? b.ImgW : bmp.Width) * 0.75f, h = (float)(b.ImgH > 0 ? b.ImgH : bmp.Height) * 0.75f;
                        if (w > cw) { h *= cw / w; w = cw; }
                        float maxH = bottom - margin;
                        if (h > maxH) { w *= maxH / h; h = maxH; }
                        if (y + h > bottom) NewPage();
                        float x = b.ImgAlign == Align.Center ? margin + (cw - w) / 2 : b.ImgAlign == Align.Right ? margin + cw - w : margin;
                        using var ip = new SKPaint { IsAntialias = true };
                        canvas!.DrawBitmapSmooth(bmp, new SKRect(x, y, x + w, y + h), ip);
                        y += h + 6;
                        break;
                    }
                    case BlockKind.Rule:
                    {
                        Array.Clear(counters);
                        if (y + 12 > bottom) NewPage();
                        using var rp = new SKPaint { Color = new SKColor(0x77, 0x77, 0x77), StrokeWidth = 0.8f };
                        canvas!.DrawLine(margin, y + 6, margin + cw, y + 6, rp);
                        y += 14;
                        break;
                    }
                    case BlockKind.PageBreak:
                        Array.Clear(counters);
                        NewPage();
                        break;
                }
            }
            Footer();
            pdf.EndPage();
            pdf.Close();
        }
        File.Move(tmp, path, true);
    }
}
