// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CrimsonAtlas.Model;

namespace CrimsonAtlas.Views;

/// <summary>
/// The map: tiles drawn from the open map file on worker threads (<see cref="Renderer"/>), kept while they're near,
/// with place names over them, placed so none overlap. Drag to move, wheel or + − to zoom, arrows to pan.
/// </summary>
public sealed class MapView : Control
{
    public const int MinZoom = 2, MaxZoom = 19;

    private MapFile? _map;
    private Palette _theme = Palette.Night;
    private int _zoom = 12;
    private double _cx, _cy;                           // the middle of the view, in pixels at _zoom
    private readonly Dictionary<(int, int, int), Tile> _tiles = new();
    private readonly HashSet<(int, int, int)> _pending = new();
    private readonly SemaphoreSlim _workers = new(Math.Max(1, Environment.ProcessorCount - 1));
    private int _generation;
    private Point? _drag;
    private (double Lat, double Lon)? _pin;
    private string? _pinName;
    private readonly Dictionary<(string, int, string), (FormattedText, IBrush)> _texts = new();

    /// <summary>Draws tiles as they're needed, on the UI thread (tests and screenshots).</summary>
    public bool Synchronous { get; set; }

    public event Action? ViewChanged;

    private sealed record Tile(Bitmap Bitmap, IReadOnlyList<MapLabel> Labels, int Generation) { public long Used; }

    public MapView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public MapFile? Map => _map;
    public int Zoom => _zoom;
    public Palette Palette => _theme;
    public (double Lat, double Lon) Center => (Mercator.YToLat(_cy, _zoom), Mercator.XToLon(_cx, _zoom));

    public void Open(MapFile? map, (double Lat, double Lon)? at = null, int? zoom = null)
    {
        _map = map;
        Clear();
        _pin = null; _pinName = null;
        if (map == null) { InvalidateVisual(); return; }
        // a map opens whole (its middle, at the zoom that fits it); a place asked for, where it is
        var c = at ?? ((map.MinLat + map.MaxLat) / 2, (map.MinLon + map.MaxLon) / 2);
        GoTo(c.Item1, c.Item2, zoom ?? FitZoom(map));
    }

    /// <summary>The zoom at which the whole map fits the view.</summary>
    private int FitZoom(MapFile map)
    {
        double w = Bounds.Width > 0 ? Bounds.Width : 1000, h = Bounds.Height > 0 ? Bounds.Height : 700;
        for (int z = MaxZoom; z > MinZoom; z--)
        {
            double dx = Mercator.LonToX(map.MaxLon, z) - Mercator.LonToX(map.MinLon, z);
            double dy = Mercator.LatToY(map.MinLat, z) - Mercator.LatToY(map.MaxLat, z);
            if (dx <= w && dy <= h) return z;
        }
        return MinZoom;
    }

    public void GoTo(double lat, double lon, int zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _cx = Mercator.LonToX(lon, _zoom);
        _cy = Mercator.LatToY(lat, _zoom);
        Changed();
    }

    public void Pin(double lat, double lon, string name, int zoom)
    {
        _pin = (lat, lon); _pinName = name;
        GoTo(lat, lon, zoom);
    }

    public void SetTheme(Palette t)
    {
        if (t == _theme) return;
        _theme = t;
        _texts.Clear();
        Clear();
        InvalidateVisual();
    }

    public void ZoomBy(int steps, Point? about = null)
    {
        int z = Math.Clamp(_zoom + steps, MinZoom, MaxZoom);
        if (z == _zoom) return;
        var p = about ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        double wx = _cx + p.X - Bounds.Width / 2, wy = _cy + p.Y - Bounds.Height / 2;  // the world point under p
        double f = Math.Pow(2, z - _zoom);
        _cx = wx * f - (p.X - Bounds.Width / 2);
        _cy = wy * f - (p.Y - Bounds.Height / 2);
        _zoom = z;
        Changed();
    }

    public void PanBy(double dx, double dy) { _cx += dx; _cy += dy; Changed(); }

    private void Changed()
    {
        double size = Mercator.MapSize(_zoom);
        _cx = ((_cx % size) + size) % size;
        _cy = Math.Clamp(_cy, 0, size);
        if (_map != null)
        {
            // keep some of the map in view
            double x0 = Mercator.LonToX(_map.MinLon, _zoom), x1 = Mercator.LonToX(_map.MaxLon, _zoom);
            double y0 = Mercator.LatToY(_map.MaxLat, _zoom), y1 = Mercator.LatToY(_map.MinLat, _zoom);
            _cx = Math.Clamp(_cx, x0 - Bounds.Width / 2 + 40, Math.Max(x0, x1 + Bounds.Width / 2 - 40));
            _cy = Math.Clamp(_cy, y0 - Bounds.Height / 2 + 40, Math.Max(y0, y1 + Bounds.Height / 2 - 40));
        }
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void Clear()
    {
        _generation++;
        foreach (var t in _tiles.Values) t.Bitmap.Dispose();
        _tiles.Clear();
        _pending.Clear();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Clear();
    }

    // ───────── input ─────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (e.ClickCount == 2) { ZoomBy(1, e.GetPosition(this)); e.Handled = true; return; }
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } d) return;
        var p = e.GetPosition(this);
        PanBy(d.X - p.X, d.Y - p.Y);
        _drag = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    private double _wheel;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _wheel += e.Delta.Y;
        int steps = (int)Math.Truncate(_wheel);
        if (steps == 0 && Math.Abs(e.Delta.Y) > 0) { steps = Math.Sign(e.Delta.Y); _wheel = 0; } else _wheel -= steps;
        ZoomBy(Math.Clamp(steps, -2, 2), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = Math.Min(Bounds.Width, Bounds.Height) / 4;
        switch (e.Key)
        {
            case Key.Left: PanBy(-step, 0); break;
            case Key.Right: PanBy(step, 0); break;
            case Key.Up: PanBy(0, -step); break;
            case Key.Down: PanBy(0, step); break;
            case Key.Add or Key.OemPlus: ZoomBy(1); break;
            case Key.Subtract or Key.OemMinus: ZoomBy(-1); break;
            default: return;
        }
        e.Handled = true;
    }

    // ───────── tiles ─────────

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private Tile? Get(int z, int x, int y, bool request)
    {
        if (_tiles.TryGetValue((z, x, y), out var t)) { t.Used = Environment.TickCount64; return t; }
        if (!request || _map == null) return null;
        if (Synchronous)
        {
            t = Make(Renderer.Render(_map, z, x, y, _theme, (float)Scaling), _generation);
            _tiles[(z, x, y)] = t;
            return t;
        }
        if (!_pending.Add((z, x, y))) return null;
        var map = _map; var theme = _theme; var gen = _generation; float scale = (float)Scaling;
        _ = Task.Run(async () =>
        {
            await _workers.WaitAsync();
            try
            {
                bool still = await Dispatcher.UIThread.InvokeAsync(() => gen == _generation && Near(z, x, y));
                TileImage? img = still ? Renderer.Render(map, z, x, y, theme, scale) : null;
                Dispatcher.UIThread.Post(() =>
                {
                    _pending.Remove((z, x, y));
                    if (img == null || gen != _generation) return;
                    _tiles[(z, x, y)] = Make(img, gen);
                    Trim();
                    InvalidateVisual();
                });
            }
            catch (Exception)                  // a damaged tile or a closed file: left blank
            {
                Dispatcher.UIThread.Post(() => _pending.Remove((z, x, y)));
            }
            finally { _workers.Release(); }
        });
        return null;
    }

    /// <summary>Whether a tile is still wanted: at the zoom shown and in or next to the view.</summary>
    private bool Near(int z, int x, int y)
    {
        if (z != _zoom) return false;
        var (x0, y0, x1, y1) = Visible();
        return x >= x0 - 1 && x <= x1 + 1 && y >= y0 - 1 && y <= y1 + 1;
    }

    private static Tile Make(TileImage img, int gen)
    {
        var h = GCHandle.Alloc(img.Pixels, GCHandleType.Pinned);
        try
        {
            var bmp = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, h.AddrOfPinnedObject(), new PixelSize(img.Size, img.Size),
                new Vector(96, 96), img.Size * 4);    // sized in pixels; drawn into a tile's 256 points
            return new Tile(bmp, img.Labels, gen) { Used = Environment.TickCount64 };
        }
        finally { h.Free(); }
    }

    private void Trim()
    {
        if (_tiles.Count <= 320) return;
        foreach (var k in _tiles.OrderBy(kv => kv.Value.Used).Take(_tiles.Count - 240).Select(kv => kv.Key).ToList())
        {
            _tiles[k].Bitmap.Dispose();
            _tiles.Remove(k);
        }
    }

    private (int X0, int Y0, int X1, int Y1) Visible()
    {
        double left = _cx - Bounds.Width / 2, top = _cy - Bounds.Height / 2;
        int n = 1 << _zoom;
        return ((int)Math.Floor(left / Mercator.TileSize), Math.Max(0, (int)Math.Floor(top / Mercator.TileSize)),
                (int)Math.Floor((left + Bounds.Width) / Mercator.TileSize), Math.Min(n - 1, (int)Math.Floor((top + Bounds.Height) / Mercator.TileSize)));
    }

    // ───────── drawing ─────────

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(new SolidColorBrush(Color.FromUInt32((uint)_theme.Outside)), bounds);
        if (_map == null || Bounds.Width < 2) return;
        // whole pixels, so neighbouring tiles meet without a seam
        double left = Math.Round(_cx - Bounds.Width / 2), top = Math.Round(_cy - Bounds.Height / 2);
        var (x0, y0, x1, y1) = Visible();
        int n = 1 << _zoom;
        var labels = new List<MapLabel>();
        // the middle first, so it's drawn soonest
        var order = new List<(int x, int y)>();
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) order.Add((x, y));
        double mx = (x0 + x1) / 2.0, my = (y0 + y1) / 2.0;
        foreach (var (x, y) in order.OrderBy(t => Math.Abs(t.x - mx) + Math.Abs(t.y - my)))
        {
            int wx = ((x % n) + n) % n;
            var dest = new Rect(x * Mercator.TileSize - left, y * Mercator.TileSize - top, Mercator.TileSize, Mercator.TileSize);
            var t = Get(_zoom, wx, y, request: true);
            if (t != null)
            {
                ctx.DrawImage(t.Bitmap, new Rect(t.Bitmap.Size), dest);
                labels.AddRange(t.Labels);
                continue;
            }
            // not drawn yet: a lower zoom's tile, enlarged
            for (int up = 1; up <= 4 && _zoom - up >= 0; up++)
            {
                var pt = Get(_zoom - up, wx >> up, y >> up, request: false);
                if (pt == null) continue;
                int span = 1 << up;
                double sw = pt.Bitmap.Size.Width / span, sh = pt.Bitmap.Size.Height / span;
                var src = new Rect((wx % span) * sw, (y % span) * sh, sw, sh);
                ctx.DrawImage(pt.Bitmap, src, dest);
                break;
            }
        }
        DrawLabels(ctx, labels, left, top);
        DrawPin(ctx, left, top);
        DrawScale(ctx);
        DrawAttribution(ctx);
    }

    private (FormattedText Text, IBrush Brush) Text(string s, int rank, string kind)
    {
        if (_texts.TryGetValue((s, rank, kind), out var cached)) return cached;
        double size = rank switch { 0 => 19, 1 => 17, 2 => 15, 3 => 13.5, 4 => 12.5, _ => 11.5 };
        var weight = rank <= 2 ? FontWeight.Bold : rank == 3 ? FontWeight.SemiBold : FontWeight.Normal;
        var style = kind is "water" or "island" ? FontStyle.Italic : FontStyle.Normal;
        var color = kind == "water" ? Color.FromUInt32(_theme.Name == "day" ? 0xFF2E6A8Cu : 0xFF7FB0D0u) : Color.FromUInt32(_theme.LabelArgb);
        var brush = new SolidColorBrush(color);
        var ft = new FormattedText(rank == 0 ? s.ToUpperInvariant() : s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, style, weight), size, brush);
        if (_texts.Count > 4000) _texts.Clear();
        return _texts[(s, rank, kind)] = (ft, brush);
    }

    private void DrawLabels(DrawingContext ctx, List<MapLabel> labels, double left, double top)
    {
        var placed = new List<Rect>();
        var halo = new Pen(new SolidColorBrush(Color.FromUInt32(_theme.HaloArgb)), 3.2, lineJoin: PenLineJoin.Round);
        var dot = new SolidColorBrush(Color.FromUInt32(_theme.LabelArgb));
        var seen = new HashSet<string>();
        foreach (var l in labels.OrderBy(l => l.Rank).ThenBy(l => l.Text.Length))
        {
            // the same place, met in several tiles
            if (!seen.Add(l.Text + "|" + Math.Round(l.WorldX / 64) + "|" + Math.Round(l.WorldY / 64))) continue;
            double px = l.WorldX - left, py = l.WorldY - top;
            if (px < -200 || py < -50 || px > Bounds.Width + 200 || py > Bounds.Height + 50) continue;
            var (ft, brush) = Text(l.Text, l.Rank, l.Kind);
            bool marked = l.Kind is "city" or "town" or "village" or "peak";
            var at = marked ? new Point(px - ft.Width / 2, py - ft.Height - 4) : new Point(px - ft.Width / 2, py - ft.Height / 2);
            var box = new Rect(at, new Size(ft.Width, ft.Height)).Inflate(new Thickness(5, 2));
            if (placed.Any(p => p.Intersects(box))) continue;
            placed.Add(box);
            if (marked)
            {
                if (l.Kind == "peak")
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open()) { c.BeginFigure(new Point(px, py - 4), true); c.LineTo(new Point(px + 4.5, py + 3.5)); c.LineTo(new Point(px - 4.5, py + 3.5)); c.EndFigure(true); }
                    ctx.DrawGeometry(dot, halo, g);
                }
                else ctx.DrawEllipse(dot, new Pen(halo.Brush, 1.5), new Point(px, py), l.Rank <= 1 ? 4 : 3, l.Rank <= 1 ? 4 : 3);
            }
            if (ft.BuildGeometry(at) is { } geo)
            {
                ctx.DrawGeometry(null, halo, geo);
                ctx.DrawGeometry(brush, null, geo);
            }
            else ctx.DrawText(ft, at);
        }
    }

    private void DrawPin(DrawingContext ctx, double left, double top)
    {
        if (_pin is not { } p) return;
        double px = Mercator.LonToX(p.Lon, _zoom) - left, py = Mercator.LatToY(p.Lat, _zoom) - top;
        var red = new SolidColorBrush(Color.Parse("#C8102E"));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(px, py), true);
            c.CubicBezierTo(new Point(px - 3, py - 8), new Point(px - 10, py - 12), new Point(px - 10, py - 20));
            c.ArcTo(new Point(px + 10, py - 20), new Size(10, 10), 0, false, SweepDirection.Clockwise);
            c.CubicBezierTo(new Point(px + 10, py - 12), new Point(px + 3, py - 8), new Point(px, py));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(red, new Pen(Brushes.White, 1.6), g);
        ctx.DrawEllipse(Brushes.White, null, new Point(px, py - 20), 3.6, 3.6);
    }

    /// <summary>A scale bar of a round length (1, 2 or 5 times a power of ten), at the middle's latitude.</summary>
    private void DrawScale(DrawingContext ctx)
    {
        double mpp = Mercator.MetresPerPixel(Center.Lat, _zoom);
        double target = 110 * mpp, nice = 1;
        foreach (var m in new[] { 1.0, 2, 5 })
            for (double p = 1; p < 1e8; p *= 10)
                if (m * p <= target && m * p > nice) nice = m * p;
        double px = nice / mpp;
        var text = nice >= 1000 ? $"{nice / 1000:0.##} km" : $"{nice:0} m";
        var fg = new SolidColorBrush(Color.FromUInt32(_theme.LabelArgb));
        var bg = new SolidColorBrush(Color.FromUInt32(_theme.HaloArgb));
        double x = 14, y = Bounds.Height - 20;
        ctx.FillRectangle(bg, new Rect(x - 6, y - 20, px + 60, 30), 4);
        var pen = new Pen(fg, 2, lineCap: PenLineCap.Square);
        ctx.DrawLine(pen, new Point(x, y), new Point(x + px, y));
        ctx.DrawLine(pen, new Point(x, y - 5), new Point(x, y));
        ctx.DrawLine(pen, new Point(x + px, y - 5), new Point(x + px, y));
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 11, fg);
        ctx.DrawText(ft, new Point(x + px + 6, y - ft.Height + 2));
    }

    private void DrawAttribution(DrawingContext ctx)
    {
        var fg = new SolidColorBrush(Color.FromUInt32(_theme.LabelArgb));
        var ft = new FormattedText("Map data © OpenStreetMap contributors (ODbL) · maps by the Mapsforge project",
            System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 10.5, fg);
        var at = new Point(Bounds.Width - ft.Width - 10, Bounds.Height - ft.Height - 6);
        ctx.FillRectangle(new SolidColorBrush(Color.FromUInt32(_theme.HaloArgb)), new Rect(at.X - 6, at.Y - 2, ft.Width + 12, ft.Height + 4), 3);
        ctx.DrawText(ft, at);
    }
}
