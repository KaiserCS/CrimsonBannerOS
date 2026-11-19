using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>The raster behind Paint: one opaque BGRA bitmap plus an undo/redo history of whole-bitmap snapshots.</summary>
public sealed class PaintDoc
{
    private readonly List<SKBitmap> _undo = new();
    private readonly List<SKBitmap> _redo = new();
    private const int MaxHistory = 60;
    private const long MaxBytes = 400L * 1024 * 1024;

    public SKBitmap Bmp { get; private set; }
    public int Width => Bmp.Width;
    public int Height => Bmp.Height;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public PaintDoc(int w, int h) { Bmp = Create(w, h, SKColors.White); }
    public PaintDoc(SKBitmap bmp) { Bmp = bmp; }

    public static SKBitmap Create(int w, int h, SKColor fill)
    {
        var b = new SKBitmap(new SKImageInfo(Math.Max(1, w), Math.Max(1, h), SKColorType.Bgra8888, SKAlphaType.Premul));
        b.Erase(fill);
        return b;
    }

    /// <summary>Writable view of the pixels as 0xAARRGGBB words (the bitmap is Bgra8888).</summary>
    private static unsafe Span<uint> Px(SKBitmap b) =>
        new Span<uint>(b.GetPixels().ToPointer(), b.RowBytes / 4 * b.Height);

    // ------------------------------------------------------------ history

    /// <summary>Snapshot the current picture so the change about to be made can be undone.</summary>
    public void PushUndo()
    {
        _undo.Add(Bmp.Copy());
        foreach (var r in _redo) r.Dispose();
        _redo.Clear();
        long bytes = 0;
        foreach (var u in _undo) bytes += u.ByteCount;
        while (_undo.Count > 1 && (_undo.Count > MaxHistory || bytes > MaxBytes))
        {
            bytes -= _undo[0].ByteCount;
            _undo[0].Dispose();
            _undo.RemoveAt(0);
        }
    }

    /// <summary>Drops the newest snapshot without applying it (a cancelled operation).</summary>
    public void DiscardUndo()
    {
        if (_undo.Count == 0) return;
        _undo[^1].Dispose();
        _undo.RemoveAt(_undo.Count - 1);
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Bmp);
        Bmp = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Bmp);
        Bmp = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
    }

    public void ClearHistory()
    {
        foreach (var u in _undo) u.Dispose();
        foreach (var r in _redo) r.Dispose();
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Swaps in a new picture (after a resize, rotate, …). Call PushUndo first.</summary>
    public void Replace(SKBitmap nb)
    {
        var old = Bmp;
        Bmp = nb;
        old.Dispose();
    }

    /// <summary>Copies the pixels of a same-sized snapshot back (live shape previews).</summary>
    public void Restore(SKBitmap snapshot)
    {
        if (snapshot.Width != Bmp.Width || snapshot.Height != Bmp.Height) return;
        Px(snapshot).CopyTo(Px(Bmp));
    }

    // ------------------------------------------------------------ whole-image operations

    public void ResizeCanvas(int w, int h, SKColor fill)
    {
        w = Math.Clamp(w, 1, 8000);
        h = Math.Clamp(h, 1, 8000);
        if (w == Width && h == Height) return;
        var nb = Create(w, h, fill);
        using (var c = new SKCanvas(nb)) c.DrawBitmap(Bmp, 0, 0);
        Replace(nb);
    }

    public SKColor GetColor(int x, int y) =>
        x < 0 || y < 0 || x >= Width || y >= Height ? SKColors.White : Bmp.GetPixel(x, y);

    /// <summary>Exact-colour flood fill (what classic Paint does).</summary>
    public void FloodFill(int x, int y, SKColor color)
    {
        int w = Width, h = Height;
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        var px = Px(Bmp);
        int stride = Bmp.RowBytes / 4;
        uint target = px[y * stride + x];
        uint fill = (uint)new SKColor(color.Red, color.Green, color.Blue, 255);
        if (target == fill) return;

        var stack = new Stack<(int, int)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var (sx, sy) = stack.Pop();
            int row = sy * stride;
            if (px[row + sx] != target) continue;
            int l = sx, r = sx;
            while (l > 0 && px[row + l - 1] == target) l--;
            while (r < w - 1 && px[row + r + 1] == target) r++;
            for (int i = l; i <= r; i++) px[row + i] = fill;
            for (int dy = -1; dy <= 1; dy += 2)
            {
                int ny = sy + dy;
                if (ny < 0 || ny >= h) continue;
                int nrow = ny * stride;
                bool span = false;
                for (int i = l; i <= r; i++)
                {
                    if (px[nrow + i] == target) { if (!span) { stack.Push((i, ny)); span = true; } }
                    else span = false;
                }
            }
        }
    }

    // ------------------------------------------------------------ bitmap helpers (static, used for the picture and for selections)

    public static SKBitmap CopyRegion(SKBitmap src, SKRectI r)
    {
        var dst = Create(r.Width, r.Height, SKColors.Transparent);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(src, -r.Left, -r.Top);
        return dst;
    }

    /// <summary>Draws src through a matrix onto a new bitmap just big enough to hold the result.</summary>
    public static SKBitmap Transform(SKBitmap src, SKMatrix m, SKColor fill)
    {
        var r = m.MapRect(new SKRect(0, 0, src.Width, src.Height));
        int w = Math.Clamp((int)Math.Round(r.Width), 1, 8000);
        int h = Math.Clamp((int)Math.Round(r.Height), 1, 8000);
        var dst = Create(w, h, fill);
        using var c = new SKCanvas(dst);
        c.Translate(-r.Left, -r.Top);
        c.Concat(in m);
        using var p = new SKPaint { IsAntialias = true };
        c.DrawBitmapSmooth(src, 0, 0, p);
        return dst;
    }

    public static SKBitmap Scale(SKBitmap src, int w, int h, SKColor fill)
    {
        w = Math.Clamp(w, 1, 8000);
        h = Math.Clamp(h, 1, 8000);
        var dst = Create(w, h, fill);
        using var c = new SKCanvas(dst);
        using var p = new SKPaint { IsAntialias = true };
        c.DrawBitmapSmooth(src, new SKRect(0, 0, w, h), p);
        return dst;
    }

    public static void InvertColors(SKBitmap bmp)
    {
        var px = Px(bmp);
        for (int i = 0; i < px.Length; i++)
        {
            uint c = px[i];
            if ((c >> 24) == 0) continue;
            px[i] = (c & 0xFF000000u) | (~c & 0x00FFFFFFu);
        }
    }

    /// <summary>Copy of src where every pixel equal to 'key' becomes see-through (Paint's "transparent selection").</summary>
    public static SKBitmap KeyOut(SKBitmap src, SKColor key)
    {
        var copy = src.Copy();
        var px = Px(copy);
        uint k = (uint)new SKColor(key.Red, key.Green, key.Blue, 255) & 0x00FFFFFFu;
        for (int i = 0; i < px.Length; i++)
            if ((px[i] >> 24) != 0 && (px[i] & 0x00FFFFFFu) == k) px[i] = 0;
        return copy;
    }

    // ------------------------------------------------------------ text

    public static void DrawText(SKCanvas c, string text, SKRect box, string family, float px, bool bold, bool italic, bool underline,
                                SKColor fg, SKColor? back)
    {
        if (back is SKColor bg)
        {
            using var bp = new SKPaint { Color = bg, Style = SKPaintStyle.Fill };
            c.DrawRect(box, bp);
        }
        using var tf = SKTypeface.FromFamilyName(family,
            bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        using var font = new SKFont(tf, px) { Edging = SKFontEdging.Antialias };
        using var p = new SKPaint { IsAntialias = true, Color = fg };
        var fm = font.Metrics;
        float lineH = fm.Descent - fm.Ascent + fm.Leading;
        float y = box.Top - fm.Ascent;
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            foreach (var line in Wrap(font, para, box.Width))
            {
                c.DrawText(line, box.Left, y, SKTextAlign.Left, font, p);
                if (underline && line.Length > 0)
                {
                    using var up = new SKPaint { Color = fg, Style = SKPaintStyle.Fill };
                    c.DrawRect(new SKRect(box.Left, y + px * 0.12f, box.Left + font.MeasureText(line), y + px * 0.12f + Math.Max(1f, px / 14f)), up);
                }
                y += lineH;
            }
        }
    }

    private static IEnumerable<string> Wrap(SKFont font, string para, float width)
    {
        if (para.Length == 0) { yield return ""; yield break; }
        var words = para.Split(' ');
        var line = "";
        foreach (var w in words)
        {
            var candidate = line.Length == 0 ? w : line + " " + w;
            if (line.Length > 0 && font.MeasureText(candidate) > width) { yield return line; line = w; }
            else line = candidate;
        }
        yield return line;
    }

    // ------------------------------------------------------------ files

    /// <summary>Decodes an image file onto a white opaque canvas.</summary>
    public static SKBitmap? Load(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        using var src = SKBitmap.Decode(ms);
        if (src == null || src.Width < 1 || src.Height < 1) return null;
        var dst = Create(src.Width, src.Height, SKColors.White);
        using var c = new SKCanvas(dst);
        c.DrawBitmap(src, 0, 0);
        return dst;
    }

    public static byte[] Encode(SKBitmap bmp, string extension)
    {
        extension = extension.ToLowerInvariant();
        if (extension == ".bmp") return EncodeBmp(bmp);
        using var img = SKImage.FromBitmap(bmp);
        using var data = extension is ".jpg" or ".jpeg"
            ? img.Encode(SKEncodedImageFormat.Jpeg, 92)
            : img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Plain 24-bit BMP (Skia has no BMP encoder).</summary>
    private static byte[] EncodeBmp(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int rowSize = (w * 3 + 3) & ~3;
        int dataSize = rowSize * h;
        using var ms = new MemoryStream(54 + dataSize);
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(54 + dataSize); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)24);
        bw.Write(0); bw.Write(dataSize); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        var row = new byte[rowSize];
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                row[x * 3] = c.Blue; row[x * 3 + 1] = c.Green; row[x * 3 + 2] = c.Red;
            }
            bw.Write(row);
        }
        return ms.ToArray();
    }
}
