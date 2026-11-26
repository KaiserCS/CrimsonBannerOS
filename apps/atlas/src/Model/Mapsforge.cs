// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace CrimsonAtlas.Model;

/// <summary>A place or point of interest in a map file.</summary>
public sealed class Poi
{
    public double Lat { get; init; }
    public double Lon { get; init; }
    public int Layer { get; init; }
    public Dictionary<string, string> Tags { get; } = new();
    public string? Name { get; set; }
    public string? HouseNumber { get; set; }
    public int? Elevation { get; set; }
}

/// <summary>A way: a line, or an area (closed, or several rings: the first outer, the rest holes).</summary>
public sealed class Way
{
    public int Layer { get; init; }
    public Dictionary<string, string> Tags { get; } = new();
    public string? Name { get; set; }
    public string? Ref { get; set; }
    public (double Lat, double Lon)? LabelAt { get; set; }
    /// <summary>Rings or lines of (lat, lon); each way data block's coordinate blocks, all together.</summary>
    public List<(double Lat, double Lon)[]> Rings { get; } = new();

    public bool Closed => Rings.Count > 0 && Rings[0].Length > 2 && Rings[0][0] == Rings[0][^1];
}

public sealed class ZoomInterval
{
    public byte Base { get; init; }
    public byte Min { get; init; }
    public byte Max { get; init; }
    public long Start { get; init; }
    public long Size { get; init; }
    // the tiles at the base zoom the file covers
    public int Left, Top, Right, Bottom;
    public int Width => Right - Left + 1;
    public int Height => Bottom - Top + 1;
}

/// <summary>The contents of one tile at a zoom: its places and ways, and whether it's all water (open sea).</summary>
public sealed class TileData
{
    public List<Poi> Pois { get; } = new();
    public List<Way> Ways { get; } = new();
    public bool Water { get; set; }
}

/// <summary>
/// A Mapsforge binary map file (version 3 to 5), as the Mapsforge project publishes for each country and region,
/// made from OpenStreetMap. Read here, as needed, tile by tile:
///   header: magic, size, version, file size, date, bounding box, tile size, projection, flags (and what they promise),
///           the POI and way tag lists, and the zoom intervals, each a sub-file;
///   sub-file: a 5-byte index entry per tile at its base zoom (the top bit: all water), then the tiles;
///   tile: a zoom table (POIs and ways per zoom), the POIs, then the ways, positions in microdegrees relative to the
///         tile's top left corner, numbers in variable-length bytes.
/// See Mapsforge's docs/Specification-Binary-Map-File.md.
/// </summary>
public sealed class MapFile : IDisposable
{
    public const string Magic = "mapsforge binary OSM";
    public string Path { get; }
    public int Version { get; private set; }
    public long FileSize { get; private set; }
    public DateTime Date { get; private set; }
    public double MinLat { get; private set; }
    public double MinLon { get; private set; }
    public double MaxLat { get; private set; }
    public double MaxLon { get; private set; }
    public int TileSize { get; private set; }
    public string Projection { get; private set; } = "";
    public bool Debug { get; private set; }
    public (double Lat, double Lon)? Start { get; private set; }
    public byte? StartZoom { get; private set; }
    public string? Languages { get; private set; }
    public string? Comment { get; private set; }
    public string? CreatedBy { get; private set; }
    public List<string> PoiTags { get; } = new();
    public List<string> WayTags { get; } = new();
    public List<ZoomInterval> Intervals { get; } = new();

    private readonly FileStream _file;
    private readonly object _lock = new();

    public MapFile(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        ReadHeader();
    }

    public static bool IsMapFile(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[Magic.Length];
            return f.Read(b, 0, b.Length) == b.Length && Encoding.ASCII.GetString(b) == Magic;
        }
        catch (IOException) { return false; }
    }

    private void ReadHeader()
    {
        var head = new byte[24];
        _file.Position = 0;
        _file.ReadExactly(head);
        if (Encoding.ASCII.GetString(head, 0, 20) != Magic) throw new InvalidDataException("This isn't a Mapsforge map file.");
        int size = BigInt(head, 20);
        if (size < 70 || size > 1_000_000) throw new InvalidDataException("This map file's header is damaged.");
        var r = new Reader(ReadAt(24, size));
        Version = r.Int();
        if (Version < 3 || Version > 5) throw new InvalidDataException($"This map file is version {Version}; Atlas reads versions 3 to 5.");
        FileSize = r.Long();
        Date = DateTimeOffset.FromUnixTimeMilliseconds(r.Long()).UtcDateTime;
        MinLat = r.Int() / 1e6; MinLon = r.Int() / 1e6; MaxLat = r.Int() / 1e6; MaxLon = r.Int() / 1e6;
        TileSize = r.Short();
        Projection = r.String();
        var flags = r.Byte();
        Debug = (flags & 0x80) != 0;
        if ((flags & 0x40) != 0) Start = (r.Int() / 1e6, r.Int() / 1e6);
        if ((flags & 0x20) != 0) StartZoom = r.Byte();
        if ((flags & 0x10) != 0) Languages = r.String();
        if ((flags & 0x08) != 0) Comment = r.String();
        if ((flags & 0x04) != 0) CreatedBy = r.String();
        int n = r.Short();
        for (int i = 0; i < n; i++) PoiTags.Add(r.String());
        n = r.Short();
        for (int i = 0; i < n; i++) WayTags.Add(r.String());
        n = r.Byte();
        if (n == 0) throw new InvalidDataException("This map file has no zoom intervals.");
        for (int i = 0; i < n; i++)
        {
            var z = new ZoomInterval { Base = r.Byte(), Min = r.Byte(), Max = r.Byte(), Start = r.Long(), Size = r.Long() };
            z.Left = Mercator.LonToTileX(MinLon, z.Base); z.Right = Mercator.LonToTileX(MaxLon, z.Base);
            z.Top = Mercator.LatToTileY(MaxLat, z.Base); z.Bottom = Mercator.LatToTileY(MinLat, z.Base);
            Intervals.Add(z);
        }
    }

    private byte[] ReadAt(long pos, int length)
    {
        var b = new byte[length];
        lock (_lock)
        {
            _file.Position = pos;
            _file.ReadExactly(b);
        }
        return b;
    }

    /// <summary>The interval that serves a zoom (outside every interval: the nearest).</summary>
    public ZoomInterval IntervalFor(int zoom) =>
        Intervals.FirstOrDefault(i => zoom >= i.Min && zoom <= i.Max)
        ?? (zoom < Intervals.Min(i => i.Min) ? Intervals.OrderBy(i => i.Min).First() : Intervals.OrderByDescending(i => i.Max).First());

    /// <summary>Everything the file has for a map tile at a zoom (from the base-zoom tiles that cover it).</summary>
    public TileData Read(int zoom, int x, int y)
    {
        var iv = IntervalFor(zoom);
        int tableZoom = Math.Clamp(zoom, iv.Min, iv.Max);
        var data = new TileData();
        int x0, x1, y0, y1;
        if (zoom >= iv.Base) { x0 = x1 = x >> (zoom - iv.Base); y0 = y1 = y >> (zoom - iv.Base); }
        else
        {
            int s = iv.Base - zoom;
            x0 = x << s; x1 = ((x + 1) << s) - 1; y0 = y << s; y1 = ((y + 1) << s) - 1;
        }
        x0 = Math.Max(x0, iv.Left); x1 = Math.Min(x1, iv.Right); y0 = Math.Max(y0, iv.Top); y1 = Math.Min(y1, iv.Bottom);
        bool allWater = x0 <= x1 && y0 <= y1;
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
                allWater &= ReadBaseTile(iv, tx, ty, tableZoom, data);
        data.Water = allWater && x0 <= x1;
        return data;
    }

    /// <summary>One tile at an interval's base zoom, with what it has up to a zoom (for an index of places).</summary>
    public TileData ReadBase(ZoomInterval iv, int tx, int ty, int zoom, bool ways = true)
    {
        var d = new TileData();
        d.Water = ReadBaseTile(iv, tx, ty, Math.Clamp(zoom, iv.Min, iv.Max), d, ways);
        return d;
    }

    /// <summary>One tile at an interval's base zoom; true when it's all water.</summary>
    private bool ReadBaseTile(ZoomInterval iv, int tx, int ty, int zoom, TileData into, bool readWays = true)
    {
        long index = (long)(ty - iv.Top) * iv.Width + (tx - iv.Left);
        long indexStart = iv.Start + (Debug ? 16 : 0);
        long total = (long)iv.Width * iv.Height;
        var entry = ReadAt(indexStart + index * 5, index + 1 < total ? 10 : 5);
        long e0 = Five(entry, 0);
        bool water = (e0 & 0x8000000000L) != 0;
        long offset = e0 & 0x7FFFFFFFFFL;
        long next = index + 1 < total ? Five(entry, 5) & 0x7FFFFFFFFFL : iv.Size;
        long length = next - offset;
        if (length <= 0) return water;
        if (length > 50_000_000) throw new InvalidDataException("A tile in this map file is too large: the file may be damaged.");
        var r = new Reader(ReadAt(iv.Start + offset, (int)length));
        if (Debug) r.Skip(32);
        int rows = iv.Max - iv.Min + 1, upTo = zoom - iv.Min;
        int pois = 0, ways = 0;
        for (int i = 0; i < rows; i++)
        {
            int p = (int)r.VbeU(), w = (int)r.VbeU();
            if (i <= upTo) { pois += p; ways += w; }
        }
        int firstWay = (int)r.VbeU();
        int waysAt = r.Pos + firstWay;
        double lat0 = Mercator.TileYToLat(ty, iv.Base), lon0 = Mercator.TileXToLon(tx, iv.Base);
        for (int i = 0; i < pois; i++) into.Pois.Add(ReadPoi(r, lat0, lon0));
        if (!readWays) return water;
        r.Pos = waysAt;
        for (int i = 0; i < ways && r.Pos < r.Length; i++)
            if (ReadWay(r, lat0, lon0) is { } w) into.Ways.Add(w);
        return water;
    }

    private Poi ReadPoi(Reader r, double lat0, double lon0)
    {
        if (Debug) r.Skip(32);
        double lat = lat0 + r.VbeS() / 1e6, lon = lon0 + r.VbeS() / 1e6;
        int special = r.Byte();                               // the layer (+5) in the high four bits, the tag count in the low
        var p = new Poi { Lat = lat, Lon = lon, Layer = (special >> 4) - 5 };
        ReadTags(r, special & 0x0F, PoiTags, p.Tags);
        int flags = r.Byte();
        if ((flags & 0x80) != 0) p.Name = Display(r.String());
        if ((flags & 0x40) != 0) p.HouseNumber = r.String();
        if ((flags & 0x20) != 0) p.Elevation = (int)r.VbeS();
        return p;
    }

    private Way? ReadWay(Reader r, double lat0, double lon0)
    {
        if (Debug) r.Skip(32);
        int size = (int)r.VbeU();
        int end = r.Pos + size;
        r.Skip(2);                                            // which of the 16 sub-tiles it touches
        int special = r.Byte();
        var w = new Way { Layer = (special >> 4) - 5 };
        ReadTags(r, special & 0x0F, WayTags, w.Tags);
        int flags = r.Byte();
        if ((flags & 0x80) != 0) w.Name = Display(r.String());
        if ((flags & 0x40) != 0) r.String();                  // house number
        if ((flags & 0x20) != 0) w.Ref = r.String();
        (double, double)? label = (flags & 0x10) != 0 ? (r.VbeS() / 1e6, r.VbeS() / 1e6) : null;
        int blocks = (flags & 0x08) != 0 ? (int)r.VbeU() : 1;
        bool doubleDelta = (flags & 0x04) != 0;
        for (int b = 0; b < blocks; b++)
        {
            int rings = (int)r.VbeU();
            for (int k = 0; k < rings; k++)
            {
                int nodes = (int)r.VbeU();
                if (nodes <= 0 || nodes > 10_000_000) { r.Pos = end; return null; }
                var pts = new (double, double)[nodes];
                double lat = lat0 + r.VbeS() / 1e6, lon = lon0 + r.VbeS() / 1e6;
                pts[0] = (lat, lon);
                double dLat = 0, dLon = 0;
                for (int i = 1; i < nodes; i++)
                {
                    double a = r.VbeS() / 1e6, o = r.VbeS() / 1e6;
                    if (doubleDelta) { dLat += a; dLon += o; lat += dLat; lon += dLon; }
                    else { lat += a; lon += o; }
                    pts[i] = (lat, lon);
                }
                w.Rings.Add(pts);
            }
        }
        if (label is { } l && w.Rings.Count > 0) w.LabelAt = (w.Rings[0][0].Lat + l.Item1, w.Rings[0][0].Lon + l.Item2);
        r.Pos = end;
        return w;
    }

    /// <summary>Tags by number; a tag such as "ele=%i" has its value in the data, after the numbers.</summary>
    private static void ReadTags(Reader r, int count, List<string> names, Dictionary<string, string> into)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = (int)r.VbeU();
        foreach (var id in ids)
        {
            if (id < 0 || id >= names.Count) continue;
            var t = names[id];
            var eq = t.IndexOf('=');
            var key = eq < 0 ? t : t[..eq];
            var value = eq < 0 ? "" : t[(eq + 1)..];
            if (value.Length == 2 && value[0] == '%')
                value = value[1] switch
                {
                    'b' => ((sbyte)r.Byte()).ToString(),
                    'h' => ((short)r.Short()).ToString(),
                    'i' => r.Int().ToString(),
                    'f' => BitConverter.Int32BitsToSingle(r.Int()).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    's' => r.String(),
                    _ => value,
                };
            into[key] = value;
        }
    }

    /// <summary>A name in several languages ("Name\rde\bName…"): the first, the local one.</summary>
    private static string Display(string name) { var i = name.IndexOf('\r'); return i < 0 ? name : name[..i]; }

    private static int BigInt(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static long Five(byte[] b, int i) => ((long)b[i] << 32) | ((long)b[i + 1] << 24) | ((long)b[i + 2] << 16) | ((long)b[i + 3] << 8) | b[i + 4];

    public void Dispose() => _file.Dispose();

    /// <summary>Big-endian fixed numbers, Mapsforge's variable-length ones, and its strings.</summary>
    public sealed class Reader
    {
        private readonly byte[] _b;
        public int Pos { get; set; }
        public int Length => _b.Length;
        public Reader(byte[] b) { _b = b; }

        private void Need(int n) { if (Pos + n > _b.Length) throw new InvalidDataException("The map file ends early: it may be damaged or still downloading."); }
        public void Skip(int n) { Need(n); Pos += n; }
        public byte Byte() { Need(1); return _b[Pos++]; }
        public int Short() { Need(2); var v = (_b[Pos] << 8) | _b[Pos + 1]; Pos += 2; return v; }
        public int Int() { Need(4); var v = BigInt(_b, Pos); Pos += 4; return v; }
        public long Long() { Need(8); long v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | _b[Pos + i]; Pos += 8; return v; }

        /// <summary>Unsigned: seven bits a byte, least significant first, the top bit set while more follow.</summary>
        public long VbeU()
        {
            long v = 0; int shift = 0;
            while (true)
            {
                var b = Byte();
                if ((b & 0x80) == 0) return v | ((long)b << shift);
                v |= (long)(b & 0x7F) << shift;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("A number in the map file is too long.");
            }
        }

        /// <summary>Signed: as unsigned, but the last byte has six bits and its 0x40 bit for a negative number.</summary>
        public long VbeS()
        {
            long v = 0; int shift = 0;
            while (true)
            {
                var b = Byte();
                if ((b & 0x80) == 0)
                {
                    v |= (long)(b & 0x3F) << shift;
                    return (b & 0x40) != 0 ? -v : v;
                }
                v |= (long)(b & 0x7F) << shift;
                shift += 7;
                if (shift > 63) throw new InvalidDataException("A number in the map file is too long.");
            }
        }

        public string String()
        {
            int n = (int)VbeU();
            Need(n);
            var s = Encoding.UTF8.GetString(_b, Pos, n);
            Pos += n;
            return s;
        }
    }
}
