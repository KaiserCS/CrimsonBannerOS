// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace CrimsonAtlas.Model;

/// <summary>A feature to write: a point (one coordinate) or a way (rings or a line), with its tags.</summary>
public sealed class Feature
{
    public Dictionary<string, string> Tags { get; } = new();
    public string? Name { get; set; }
    public List<(double Lat, double Lon)[]> Rings { get; } = new();
    public int Layer { get; set; }
    public int? Elevation { get; set; }
    public bool IsPoint => Rings.Count == 1 && Rings[0].Length == 1;

    public Feature Tag(string k, string v) { Tags[k] = v; return this; }
    public static Feature Point(double lat, double lon, string? name = null) { var f = new Feature { Name = name }; f.Rings.Add(new[] { (lat, lon) }); return f; }
    public static Feature Line(string? name, params (double, double)[] pts) { var f = new Feature { Name = name }; f.Rings.Add(pts); return f; }
    public static Feature Area(string? name, params (double, double)[] ring)
    {
        var f = new Feature { Name = name };
        f.Rings.Add(ring.Length > 0 && ring[0] != ring[^1] ? ring.Append(ring[0]).ToArray() : ring);
        return f;
    }
}

/// <summary>
/// Writes a small Mapsforge map file (version 5): one zoom interval, every feature in every tile it might touch,
/// single- or double-delta coordinates. For the tests and the example map; real maps come from the Mapsforge project.
/// </summary>
public static class MapWriter
{
    public static void Write(string file, IReadOnlyList<Feature> features, double minLat, double minLon, double maxLat, double maxLon,
                             byte baseZoom = 12, byte minZoom = 0, byte maxZoom = 21, (double, double)? start = null, ISet<(int, int)>? waterTiles = null, bool doubleDelta = false)
    {
        // the tag lists, in the order they're first met
        var poiTags = new List<string>(); var wayTags = new List<string>();
        int Id(List<string> list, string t) { var i = list.IndexOf(t); if (i < 0) { list.Add(t); i = list.Count - 1; } return i; }
        foreach (var f in features) foreach (var (k, v) in f.Tags) Id(f.IsPoint ? poiTags : wayTags, $"{k}={v}");

        int left = Mercator.LonToTileX(minLon, baseZoom), right = Mercator.LonToTileX(maxLon, baseZoom);
        int top = Mercator.LatToTileY(maxLat, baseZoom), bottom = Mercator.LatToTileY(minLat, baseZoom);
        int width = right - left + 1, height = bottom - top + 1;
        int rows = maxZoom - minZoom + 1;

        // each tile: everything that touches it (by bounding box), placed at the lowest zoom of the interval
        var tiles = new List<byte[]>();
        for (int ty = top; ty <= bottom; ty++)
            for (int tx = left; tx <= right; tx++)
            {
                double tLat0 = Mercator.TileYToLat(ty, baseZoom), tLon0 = Mercator.TileXToLon(tx, baseZoom);
                double tLat1 = Mercator.TileYToLat(ty + 1, baseZoom), tLon1 = Mercator.TileXToLon(tx + 1, baseZoom);
                bool Touches(Feature f)
                {
                    var all = f.Rings.SelectMany(r => r).ToList();
                    return all.Max(p => p.Lat) >= tLat1 && all.Min(p => p.Lat) <= tLat0 && all.Max(p => p.Lon) >= tLon0 && all.Min(p => p.Lon) <= tLon1;
                }
                var pois = features.Where(f => f.IsPoint && Touches(f)).ToList();
                var ways = features.Where(f => !f.IsPoint && Touches(f)).ToList();
                var poiBytes = new Out();
                foreach (var p in pois)
                {
                    poiBytes.VbeS(Micro(p.Rings[0][0].Lat - tLat0)); poiBytes.VbeS(Micro(p.Rings[0][0].Lon - tLon0));
                    poiBytes.Byte((byte)(((p.Layer + 5) << 4) | p.Tags.Count));
                    foreach (var (k, v) in p.Tags) poiBytes.VbeU(Id(poiTags, $"{k}={v}"));
                    byte flags = (byte)((p.Name != null ? 0x80 : 0) | (p.Elevation != null ? 0x20 : 0));
                    poiBytes.Byte(flags);
                    if (p.Name != null) poiBytes.String(p.Name);
                    if (p.Elevation is int e) poiBytes.VbeS(e);
                }
                var wayBytes = new Out();
                foreach (var w in ways)
                {
                    var body = new Out();
                    body.Byte(0xFF); body.Byte(0xFF);                     // every sub-tile
                    body.Byte((byte)(((w.Layer + 5) << 4) | w.Tags.Count));
                    foreach (var (k, v) in w.Tags) body.VbeU(Id(wayTags, $"{k}={v}"));
                    body.Byte((byte)((w.Name != null ? 0x80 : 0) | (doubleDelta ? 0x04 : 0)));   // one block
                    if (w.Name != null) body.String(w.Name);
                    body.VbeU(w.Rings.Count);
                    foreach (var ring in w.Rings)
                    {
                        body.VbeU(ring.Length);
                        body.VbeS(Micro(ring[0].Lat - tLat0)); body.VbeS(Micro(ring[0].Lon - tLon0));
                        long dLat0 = 0, dLon0 = 0;
                        for (int i = 1; i < ring.Length; i++)
                        {
                            long dLat = Micro(ring[i].Lat) - Micro(ring[i - 1].Lat), dLon = Micro(ring[i].Lon) - Micro(ring[i - 1].Lon);
                            body.VbeS(doubleDelta ? dLat - dLat0 : dLat);
                            body.VbeS(doubleDelta ? dLon - dLon0 : dLon);
                            (dLat0, dLon0) = (dLat, dLon);
                        }
                    }
                    wayBytes.VbeU(body.Length);
                    wayBytes.Bytes(body.ToArray());
                }
                var t = new Out();
                for (int z = 0; z < rows; z++) { t.VbeU(z == 0 ? pois.Count : 0); t.VbeU(z == 0 ? ways.Count : 0); }
                t.VbeU(poiBytes.Length);
                t.Bytes(poiBytes.ToArray());
                t.Bytes(wayBytes.ToArray());
                tiles.Add(t.ToArray());
            }

        // the sub-file: the index (5 bytes a tile, offsets from the sub-file's start), then the tiles
        var sub = new Out();
        long offset = (long)width * height * 5;
        int k2 = 0;
        for (int ty = top; ty <= bottom; ty++)
            for (int tx = left; tx <= right; tx++)
            {
                long v = offset | (waterTiles?.Contains((tx, ty)) == true ? 0x8000000000L : 0);
                for (int s = 32; s >= 0; s -= 8) sub.Byte((byte)(v >> s));
                offset += tiles[k2++].Length;
            }
        foreach (var t in tiles) sub.Bytes(t);

        // the header, then the sub-file after it
        var h = new Out();
        h.Int(5);
        long fileSizeAt = h.Length; h.Long(0);
        h.Long(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        h.Int(Micro(minLat)); h.Int(Micro(minLon)); h.Int(Micro(maxLat)); h.Int(Micro(maxLon));
        h.Short(256);
        h.String("Mercator");
        h.Byte((byte)((start != null ? 0x40 : 0) | 0x04));
        if (start is { } st) { h.Int(Micro(st.Item1)); h.Int(Micro(st.Item2)); }
        h.String("Crimson Banner OS Atlas (MapWriter)");
        h.Short(poiTags.Count); foreach (var t in poiTags) h.String(t);
        h.Short(wayTags.Count); foreach (var t in wayTags) h.String(t);
        h.Byte(1);
        h.Byte(baseZoom); h.Byte(minZoom); h.Byte(maxZoom);
        long startAt = h.Length; h.Long(0); h.Long(sub.Length);
        var header = h.ToArray();
        long subStart = 20 + 4 + header.Length;
        long fileSize = subStart + sub.Length;
        Put(header, (int)fileSizeAt, fileSize);
        Put(header, (int)startAt, subStart);
        using var output = File.Create(file);
        output.Write(Encoding.ASCII.GetBytes(MapFile.Magic));
        output.Write(new[] { (byte)(header.Length >> 24), (byte)(header.Length >> 16), (byte)(header.Length >> 8), (byte)header.Length });
        output.Write(header);
        output.Write(sub.ToArray());
    }

    private static int Micro(double degrees) => (int)Math.Round(degrees * 1e6);
    private static void Put(byte[] b, int at, long v) { for (int i = 0; i < 8; i++) b[at + i] = (byte)(v >> (56 - 8 * i)); }

    /// <summary>Mapsforge's encodings, written.</summary>
    public sealed class Out
    {
        private readonly MemoryStream _m = new();
        public int Length => (int)_m.Length;
        public byte[] ToArray() => _m.ToArray();
        public void Byte(byte b) => _m.WriteByte(b);
        public void Bytes(byte[] b) => _m.Write(b);
        public void Short(int v) { Byte((byte)(v >> 8)); Byte((byte)v); }
        public void Int(int v) { for (int s = 24; s >= 0; s -= 8) Byte((byte)(v >> s)); }
        public void Long(long v) { for (int s = 56; s >= 0; s -= 8) Byte((byte)(v >> s)); }
        public void VbeU(long v) { while (v > 0x7F) { Byte((byte)((v & 0x7F) | 0x80)); v >>= 7; } Byte((byte)v); }
        public void VbeS(long v)
        {
            bool neg = v < 0; long m = Math.Abs(v);
            while (m > 0x3F) { Byte((byte)((m & 0x7F) | 0x80)); m >>= 7; }
            Byte((byte)(m | (neg ? 0x40u : 0u)));
        }
        public void String(string s) { var b = Encoding.UTF8.GetBytes(s); VbeU(b.Length); Bytes(b); }
    }
}
