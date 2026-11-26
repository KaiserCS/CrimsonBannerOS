// SPDX-License-Identifier: GPL-3.0-or-later
using SkiaSharp;

namespace CrimsonAtlas.Model;

/// <summary>A map's colours: Day, after OpenStreetMap's own, muted; Night, on the deck's graphite.</summary>
public sealed class Palette
{
    public required string Name { get; init; }
    public SKColor Outside, Land, Sea, Water, Forest, Park, Grass, Farm, Residential, Industrial, Sand, Wetland, Ice,
        Building, BuildingLine, Motorway, MotorwayCase, Trunk, Primary, Secondary, Tertiary, Minor, Case, Path, Rail, Boundary, Runway;
    public uint LabelArgb, HaloArgb;

    public static readonly Palette Day = new()
    {
        Name = "day",
        Outside = new(0xDD, 0xDD, 0xDF), Land = new(0xF2, 0xEF, 0xE9), Sea = new(0xAA, 0xD3, 0xDF), Water = new(0xAA, 0xD3, 0xDF),
        Forest = new(0xAD, 0xD1, 0x9E), Park = new(0xC8, 0xFA, 0xCC), Grass = new(0xCD, 0xEB, 0xB0), Farm = new(0xEE, 0xF0, 0xD5),
        Residential = new(0xE0, 0xDF, 0xDF), Industrial = new(0xEB, 0xDB, 0xE8), Sand = new(0xF5, 0xE9, 0xC6), Wetland = new(0xD6, 0xE7, 0xD9),
        Ice = new(0xF6, 0xFB, 0xFF), Building = new(0xD9, 0xD0, 0xC9), BuildingLine = new(0xC4, 0xB6, 0xAB),
        Motorway = new(0xE8, 0x92, 0xA2), MotorwayCase = new(0xC8, 0x10, 0x2E), Trunk = new(0xF9, 0xB2, 0x9C), Primary = new(0xFC, 0xD6, 0xA4),
        Secondary = new(0xF7, 0xFA, 0xBF), Tertiary = new(0xFF, 0xFF, 0xFF), Minor = new(0xFF, 0xFF, 0xFF), Case = new(0xB8, 0xB4, 0xAE),
        Path = new(0xC8, 0x6A, 0x5A), Rail = new(0x70, 0x70, 0x74), Boundary = new(0x9E, 0x7C, 0xA8), Runway = new(0xBB, 0xBB, 0xCC),
        LabelArgb = 0xFF1D1D1F, HaloArgb = 0xE6FFFFFF,
    };

    public static readonly Palette Night = new()
    {
        Name = "night",
        Outside = new(0x0B, 0x0B, 0x0D), Land = new(0x1C, 0x1C, 0x1F), Sea = new(0x0E, 0x24, 0x33), Water = new(0x12, 0x2C, 0x3D),
        Forest = new(0x1C, 0x2B, 0x20), Park = new(0x1F, 0x30, 0x22), Grass = new(0x22, 0x2E, 0x20), Farm = new(0x24, 0x26, 0x20),
        Residential = new(0x26, 0x26, 0x2A), Industrial = new(0x2A, 0x24, 0x2A), Sand = new(0x33, 0x2E, 0x24), Wetland = new(0x1E, 0x2A, 0x2A),
        Ice = new(0x30, 0x34, 0x3A), Building = new(0x33, 0x33, 0x3A), BuildingLine = new(0x40, 0x40, 0x48),
        Motorway = new(0xC8, 0x10, 0x2E), MotorwayCase = new(0x5A, 0x08, 0x14), Trunk = new(0xA0, 0x3A, 0x30), Primary = new(0x8A, 0x6A, 0x3A),
        Secondary = new(0x6A, 0x66, 0x48), Tertiary = new(0x55, 0x55, 0x5C), Minor = new(0x44, 0x44, 0x4A), Case = new(0x10, 0x10, 0x12),
        Path = new(0x8A, 0x4A, 0x44), Rail = new(0x60, 0x60, 0x68), Boundary = new(0x8A, 0x60, 0x96), Runway = new(0x44, 0x44, 0x50),
        LabelArgb = 0xFFE6E6EA, HaloArgb = 0xD0101012,
    };

    public static Palette Named(string? name) => name == "day" ? Day : Night;
}

/// <summary>A name to put on the map: where, how important, and from which zoom.</summary>
public sealed record MapLabel(string Text, double WorldX, double WorldY, int Rank, string Kind);

/// <summary>A rendered tile: its pixels (BGRA, premultiplied) and the names on it, placed by the map across tiles.</summary>
public sealed record TileImage(int Zoom, int X, int Y, int Size, byte[] Pixels, IReadOnlyList<MapLabel> Labels);

/// <summary>
/// Draws a map tile from a Mapsforge file: land and sea, land cover, water, buildings, roads (casing, then fill, by
/// importance), railways and boundaries, sized for the zoom. Thread-safe: each call draws its own bitmap.
/// </summary>
public static class Renderer
{
    private sealed record Shape(int Layer, int Order, SKPath Path, SKColor? FillColor, SKColor? Stroke, float Width, float[]? Dash, SKColor? Outline = null);

    public static TileImage Render(MapFile map, int z, int x, int y, Palette th, float scale = 1)
    {
        int size = (int)Math.Round(Mercator.TileSize * scale);
        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(th.Outside);
        var labels = new List<MapLabel>();
        double ox = x * (double)Mercator.TileSize, oy = y * (double)Mercator.TileSize;
        SKPoint P(double lat, double lon) => new((float)((Mercator.LonToX(lon, z) - ox) * scale), (float)((Mercator.LatToY(lat, z) - oy) * scale));

        // the part of the tile the map covers
        var a = P(map.MaxLat, map.MinLon); var b = P(map.MinLat, map.MaxLon);
        var cover = new SKRect(a.X, a.Y, b.X, b.Y);
        if (cover.Right < 0 || cover.Bottom < 0 || cover.Left > size || cover.Top > size)
            return Finish(bmp, z, x, y, size, labels);
        canvas.Save();
        canvas.ClipRect(cover);
        TileData data;
        try { data = map.Read(z, x, y); }
        catch (Exception e) when (e is InvalidDataException or IOException) { canvas.Restore(); return Finish(bmp, z, x, y, size, labels); }
        using (var bg = new SKPaint { Color = data.Water ? th.Sea : th.Land }) canvas.DrawRect(cover, bg);

        var shapes = new List<Shape>();
        foreach (var w in data.Ways) Classify(w, z, scale, th, P, shapes, labels);
        foreach (var s in shapes.OrderBy(s => s.Order < 50 ? -10 : s.Layer).ThenBy(s => s.Order))
        {
            if (s.FillColor is { } fc)
            {
                using var p = new SKPaint { Color = fc, IsAntialias = true, Style = SKPaintStyle.Fill };
                canvas.DrawPath(s.Path, p);
                if (s.Outline is { } oc)
                {
                    using var o = new SKPaint { Color = oc, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(0.6f, 0.8f * scale) };
                    canvas.DrawPath(s.Path, o);
                }
            }
            else if (s.Stroke is { } sc)
            {
                using var p = new SKPaint
                {
                    Color = sc, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s.Width,
                    StrokeCap = s.Dash == null ? SKStrokeCap.Round : SKStrokeCap.Butt, StrokeJoin = SKStrokeJoin.Round,
                };
                if (s.Dash != null) p.PathEffect = SKPathEffect.CreateDash(s.Dash, 0);
                canvas.DrawPath(s.Path, p);
            }
            s.Path.Dispose();
        }
        foreach (var poi in data.Pois) LabelFor(poi, z, labels);
        canvas.Restore();
        canvas.Flush();
        return Finish(bmp, z, x, y, size, labels);
    }

    private static TileImage Finish(SKBitmap bmp, int z, int x, int y, int size, List<MapLabel> labels)
    {
        var bytes = new byte[bmp.RowBytes * bmp.Height];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), bytes, 0, bytes.Length);
        return new TileImage(z, x, y, size, bytes, labels);
    }

    /// <summary>Place names: cities from zoom 4, towns 8, villages 11, suburbs 12, hamlets 13; peaks 12, with height.</summary>
    private static void LabelFor(Poi p, int z, List<MapLabel> labels)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) return;
        int rank, from;
        string kind;
        if (p.Tags.TryGetValue("place", out var place))
        {
            (rank, from) = place switch
            {
                "country" => (0, 2), "state" => (1, 5), "city" => (1, 4), "town" => (2, 8), "island" => (3, 9),
                "village" => (3, 11), "suburb" => (4, 12), "hamlet" or "locality" or "neighbourhood" or "quarter" => (5, 13),
                _ => (6, 14),
            };
            kind = place;
        }
        else if (p.Tags.TryGetValue("natural", out var nat) && nat is "peak" or "volcano") { rank = 4; from = 12; kind = "peak"; }
        else return;
        if (z < from) return;
        var text = kind == "peak" && p.Elevation is int e ? $"{p.Name} ({e} m)" : kind == "peak" && p.Tags.TryGetValue("ele", out var ev) ? $"{p.Name} ({ev} m)" : p.Name!;
        labels.Add(new MapLabel(text, Mercator.LonToX(p.Lon, z), Mercator.LatToY(p.Lat, z), rank, kind));
    }

    private static void Classify(Way w, int z, float scale, Palette th, Func<double, double, SKPoint> P, List<Shape> shapes, List<MapLabel> labels)
    {
        var t = w.Tags;
        string? V(string k) => t.TryGetValue(k, out var v) ? v : null;
        bool area = w.Closed && V("area") != "no";
        SKColor? fill = null;
        int order = 0;
        bool building = false;
        if (area)
        {
            var natural = V("natural"); var landuse = V("landuse"); var leisure = V("leisure");
            (fill, order) = natural switch
            {
                "sea" => (th.Sea, 1),
                "nosea" or "land" => (th.Land, 2),
                "water" => (th.Water, 30),
                "wood" => (th.Forest, 10),
                "grassland" or "heath" or "scrub" => (th.Grass, 10),
                "beach" or "sand" or "bare_rock" or "scree" => (th.Sand, 10),
                "wetland" => (th.Wetland, 10),
                "glacier" => (th.Ice, 10),
                _ => ((SKColor?)null, 0),
            };
            if (fill == null)
                (fill, order) = landuse switch
                {
                    "forest" => (th.Forest, 10),
                    "grass" or "meadow" or "village_green" or "recreation_ground" => (th.Grass, 10),
                    "farmland" or "farmyard" or "orchard" or "vineyard" or "allotments" or "greenhouse_horticulture" => (th.Farm, 9),
                    "residential" or "commercial" or "retail" => (th.Residential, 8),
                    "industrial" or "railway" or "construction" or "quarry" => (th.Industrial, 8),
                    "cemetery" => (th.Grass, 11),
                    "reservoir" or "basin" => (th.Water, 30),
                    _ => ((SKColor?)null, 0),
                };
            if (fill == null && leisure is "park" or "garden" or "nature_reserve" or "pitch" or "golf_course" or "playground") (fill, order) = (th.Park, 11);
            if (fill == null && V("waterway") is "riverbank" or "dock") (fill, order) = (th.Water, 30);
            if (fill == null && V("water") != null) (fill, order) = (th.Water, 30);
            if (fill == null && V("building") != null && z >= 14) { fill = th.Building; order = 40; building = true; }
            if (fill == null && V("aeroway") is "aerodrome") (fill, order) = (th.Industrial, 8);
        }
        if (fill is { } f)
        {
            var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var ring in w.Rings) AddRing(path, ring, P, close: true);
            shapes.Add(new Shape(w.Layer, order, path, f, null, 0, null, building && z >= 15 ? th.BuildingLine : null));
            if (order == 30 && !string.IsNullOrWhiteSpace(w.Name) && z >= 12 && w.LabelAt is { } la)
                labels.Add(new MapLabel(w.Name!, Mercator.LonToX(la.Lon, z), Mercator.LatToY(la.Lat, z), 5, "water"));
            return;
        }

        // lines: roads, water, railways, boundaries
        double k = Math.Pow(2, z - 15) * scale;
        float Wd(double atZ15, double min) => (float)Math.Max(min * scale, atZ15 * k);
        bool tunnel = V("tunnel") is "yes" or "building_passage";
        var hw = V("highway");
        if (hw != null)
        {
            var kind = hw.Replace("_link", "");
            (int from, double width, SKColor color, int importance) = kind switch
            {
                "motorway" => (5, 12.0, th.Motorway, 9),
                "trunk" => (6, 11.0, th.Trunk, 8),
                "primary" => (8, 10.0, th.Primary, 7),
                "secondary" => (9, 9.0, th.Secondary, 6),
                "tertiary" => (10, 8.0, th.Tertiary, 5),
                "unclassified" or "residential" or "living_street" or "road" or "pedestrian" => (12, 6.0, th.Minor, 4),
                "service" => (14, 3.5, th.Minor, 3),
                "track" => (14, 2.0, th.Path, 2),
                "footway" or "path" or "cycleway" or "bridleway" or "steps" => (15, 1.4, th.Path, 1),
                _ => (99, 0, th.Minor, 0),
            };
            if (z < from) return;
            float width2 = Wd(width, importance >= 5 ? 0.9 : 0.6);
            bool thin = importance <= 2;
            if (z >= 12 && !thin)
                shapes.Add(new Shape(w.Layer, 50 + importance, LinePath(w, P), null, kind == "motorway" ? th.MotorwayCase : th.Case, width2 + Math.Max(1.2f, 1.6f * scale), tunnel ? new[] { 4f * scale, 3f * scale } : null));
            shapes.Add(new Shape(w.Layer, 60 + importance, LinePath(w, P), null, tunnel ? color.WithAlpha(150) : color, width2,
                thin ? new[] { 3f * scale, 2.5f * scale } : null));
            return;
        }
        var ww = V("waterway");
        if (ww is "river" or "canal" && z >= 8) { shapes.Add(new Shape(w.Layer, 35, LinePath(w, P), null, th.Water, Wd(ww == "river" ? 6 : 4, 0.8), null)); return; }
        if (ww is "stream" or "ditch" or "drain" && z >= 13) { shapes.Add(new Shape(w.Layer, 35, LinePath(w, P), null, th.Water, Wd(2, 0.6), null)); return; }
        var rw = V("railway");
        if (rw is "rail" or "light_rail" or "narrow_gauge" or "tram" or "subway" && z >= 10 && !(rw == "subway" && tunnel))
        {
            float rwW = Wd(rw == "tram" ? 2 : 3, 0.8);
            shapes.Add(new Shape(w.Layer, 70, LinePath(w, P), null, th.Rail, rwW, null));
            if (z >= 13) shapes.Add(new Shape(w.Layer, 71, LinePath(w, P), null, th.Land, Math.Max(0.6f, rwW * 0.45f), new[] { 6f * scale, 6f * scale }));
            return;
        }
        if (V("aeroway") is "runway" && z >= 10) { shapes.Add(new Shape(w.Layer, 45, LinePath(w, P), null, th.Runway, Wd(40, 1.5), null)); return; }
        if (V("boundary") == "administrative" && V("admin_level") is { } lvl)
        {
            if (lvl == "2") shapes.Add(new Shape(9, 80, LinePath(w, P), null, th.Boundary, Math.Max(1.2f, 1.6f * scale), new[] { 7f * scale, 3f * scale }));
            else if (lvl == "4" && z >= 5) shapes.Add(new Shape(9, 80, LinePath(w, P), null, th.Boundary.WithAlpha(170), Math.Max(0.8f, 1f * scale), new[] { 4f * scale, 3f * scale }));
        }
    }

    private static SKPath LinePath(Way w, Func<double, double, SKPoint> P)
    {
        var p = new SKPath();
        foreach (var ring in w.Rings) AddRing(p, ring, P, close: false);
        return p;
    }

    private static void AddRing(SKPath path, (double Lat, double Lon)[] ring, Func<double, double, SKPoint> P, bool close)
    {
        if (ring.Length == 0) return;
        path.MoveTo(P(ring[0].Lat, ring[0].Lon));
        for (int i = 1; i < ring.Length; i++) path.LineTo(P(ring[i].Lat, ring[i].Lon));
        if (close) path.Close();
    }
}
