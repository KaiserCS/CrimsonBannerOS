// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CrimsonAtlas.Model;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Atlas (apps/atlas): Mapsforge files written and read back, tiles drawn, places found, and the download
/// server's lists and resumed downloads through a stand-in server (no network in tests).</summary>
public class AtlasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atlas-test-" + Guid.NewGuid().ToString("N"));
    public AtlasTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Sample() => SampleMap.Write(_dir);

    [Theory]
    [InlineData(0L)]
    [InlineData(63L)]
    [InlineData(64L)]
    [InlineData(-64L)]
    [InlineData(127L)]
    [InlineData(-1L)]
    [InlineData(1_234_567L)]
    [InlineData(-98_765_432L)]
    [InlineData(4_000_000_000L)]
    public void Variable_length_numbers_come_back_as_written(long v)
    {
        var o = new MapWriter.Out();
        o.VbeS(v);
        if (v >= 0) o.VbeU(v);
        var r = new MapFile.Reader(o.ToArray());
        Assert.Equal(v, r.VbeS());
        if (v >= 0) Assert.Equal(v, r.VbeU());
        Assert.Equal(r.Length, r.Pos);
    }

    [Fact]
    public void Mapsforge_encodes_the_way_its_specification_says()
    {
        // 300 = 0b10_0101100: 0xAC 0x02; −5 in the signed form: 0x45
        var o = new MapWriter.Out();
        o.VbeU(300); o.VbeS(-5); o.VbeS(100);
        Assert.Equal(new byte[] { 0xAC, 0x02, 0x45, 0xE4, 0x00 }, o.ToArray());
    }

    [Fact]
    public void The_example_island_reads_back_header_and_all()
    {
        using var m = new MapFile(Sample());
        Assert.Equal(5, m.Version);
        Assert.Equal((SampleMap.MinLat, SampleMap.MinLon, SampleMap.MaxLat, SampleMap.MaxLon), (m.MinLat, m.MinLon, m.MaxLat, m.MaxLon));
        Assert.Equal(SampleMap.Port, m.Start!.Value);
        Assert.Equal("Mercator", m.Projection);
        Assert.Contains("MapWriter", m.CreatedBy);
        Assert.Equal(new FileInfo(m.Path).Length, m.FileSize);
        var iv = Assert.Single(m.Intervals);
        Assert.Equal(((byte)12, (byte)0, (byte)21), (iv.Base, iv.Min, iv.Max));
        Assert.True(MapFile.IsMapFile(m.Path));
        Assert.False(MapFile.IsMapFile(typeof(AtlasTests).Assembly.Location));

        // the port's tile at zoom 12
        int x = Mercator.LonToTileX(SampleMap.Port.Lon, 12), y = Mercator.LatToTileY(SampleMap.Port.Lat, 12);
        var t = m.Read(12, x, y);
        var city = Assert.Single(t.Pois, p => p.Name == "Port Solidarity");
        Assert.Equal("city", city.Tags["place"]);
        Assert.Equal(SampleMap.Port.Lat, city.Lat, 1e-6);
        Assert.Equal(SampleMap.Port.Lon, city.Lon, 1e-6);
        var road = Assert.Single(t.Ways, w => w.Name == "Harvest Road");
        Assert.Equal("primary", road.Tags["highway"]);
        Assert.False(road.Closed);
        Assert.Contains(t.Ways, w => w.Tags.GetValueOrDefault("natural") == "sea" && w.Closed);
        Assert.Contains(t.Ways, w => w.Tags.GetValueOrDefault("building") == "yes");
        Assert.False(t.Water);

        // the peak, with its height, is in another tile
        var peak = Enumerable.Range(m.Intervals[0].Left, m.Intervals[0].Width)
            .SelectMany(tx => Enumerable.Range(m.Intervals[0].Top, m.Intervals[0].Height).Select(ty => m.Read(12, tx, ty)))
            .SelectMany(d => d.Pois).First(p => p.Name == "Mount Unity");
        Assert.Equal(812, peak.Elevation);
    }

    [Fact]
    public void Layers_double_deltas_and_water_tiles_are_read_as_written()
    {
        var file = Path.Combine(_dir, "dd.map");
        var line = Feature.Line("Bridge", (10.0, 20.0), (10.001, 20.002), (10.0035, 20.0031), (10.0021, 20.0099)).Tag("highway", "primary").Tag("bridge", "yes");
        line.Layer = 1;
        var under = Feature.Line(null, (10.002, 20.0), (10.002, 20.01)).Tag("railway", "rail");
        under.Layer = -1;
        int wx = Mercator.LonToTileX(20.03, 12), wy = Mercator.LatToTileY(10.0, 12);
        MapWriter.Write(file, new[] { line, under }, 9.99, 19.99, 10.01, 20.04, doubleDelta: true, waterTiles: new HashSet<(int, int)> { (wx, wy) });
        using var m = new MapFile(file);
        var t = m.Read(12, Mercator.LonToTileX(20.0, 12), Mercator.LatToTileY(10.0, 12));
        var w = Assert.Single(t.Ways, w => w.Name == "Bridge");
        Assert.Equal(1, w.Layer);
        Assert.Equal(-1, Assert.Single(t.Ways, w => w.Tags.ContainsKey("railway")).Layer);
        var ring = Assert.Single(w.Rings);
        Assert.Equal(4, ring.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(line.Rings[0][i].Lat, ring[i].Lat, 2e-6);
            Assert.Equal(line.Rings[0][i].Lon, ring[i].Lon, 2e-6);
        }
        Assert.True(m.Read(12, wx, wy).Water);
    }

    [Fact]
    public void A_damaged_or_foreign_file_is_refused_in_plain_words()
    {
        var bad = Path.Combine(_dir, "bad.map");
        File.WriteAllText(bad, "<!DOCTYPE html><html>Busy</html>");
        var e = Assert.Throws<InvalidDataException>(() => new MapFile(bad));
        Assert.Contains("isn't a Mapsforge map", e.Message);
        // a map cut short (a download stopped) reads as far as it goes, and then says so
        var cut = Path.Combine(_dir, "cut.map");
        var whole = File.ReadAllBytes(Sample());
        File.WriteAllBytes(cut, whole[..(whole.Length / 2)]);
        using var m = new MapFile(cut);
        var iv = m.Intervals[0];
        var ex = Record.Exception(() => { for (int ty = iv.Top; ty <= iv.Bottom; ty++) for (int tx = iv.Left; tx <= iv.Right; tx++) m.Read(12, tx, ty); });
        Assert.True(ex is InvalidDataException or EndOfStreamException, ex?.ToString());
    }

    private static (byte B, byte G, byte R) Pixel(TileImage img, double lat, double lon)
    {
        int px = (int)(Mercator.LonToX(lon, img.Zoom) - img.X * 256.0), py = (int)(Mercator.LatToY(lat, img.Zoom) - img.Y * 256.0);
        Assert.InRange(px, 0, 255); Assert.InRange(py, 0, 255);
        int i = (py * img.Size + px) * 4;
        return (img.Pixels[i], img.Pixels[i + 1], img.Pixels[i + 2]);
    }

    private static (byte, byte, byte) Bgr(SkiaSharp.SKColor c) => (c.Blue, c.Green, c.Red);

    [Fact]
    public void Tiles_are_drawn_with_sea_land_and_names()
    {
        using var m = new MapFile(Sample());
        // zoom 10: the whole island in one or two tiles
        (double, double) sea = (-30.55, -20.44), land = (-30.40, -20.20), outside = (-30.60, -20.50);
        var a = Renderer.Render(m, 10, Mercator.LonToTileX(sea.Item2, 10), Mercator.LatToTileY(sea.Item1, 10), Palette.Day);
        Assert.Equal(256, a.Size);
        Assert.Equal(Bgr(Palette.Day.Sea), Pixel(a, sea.Item1, sea.Item2));
        var b = Renderer.Render(m, 10, Mercator.LonToTileX(land.Item2, 10), Mercator.LatToTileY(land.Item1, 10), Palette.Day);
        Assert.Equal(Bgr(Palette.Day.Land), Pixel(b, land.Item1, land.Item2));
        var c = Renderer.Render(m, 10, Mercator.LonToTileX(outside.Item2, 10), Mercator.LatToTileY(outside.Item1, 10), Palette.Night);
        Assert.Equal(Bgr(Palette.Night.Outside), Pixel(c, outside.Item1, outside.Item2));

        // the port up close: its name, roads in their colours, and twice the pixels on a high-density screen
        int x = Mercator.LonToTileX(SampleMap.Port.Lon, 13), y = Mercator.LatToTileY(SampleMap.Port.Lat, 13);
        var port = Renderer.Render(m, 13, x, y, Palette.Day, scale: 2);
        Assert.Equal(512, port.Size);
        Assert.Equal(512 * 512 * 4, port.Pixels.Length);
        var name = Assert.Single(port.Labels, l => l.Text == "Port Solidarity");
        Assert.Equal((1, "city"), (name.Rank, name.Kind));
        var colours = new HashSet<(byte, byte, byte)>();
        for (int i = 0; i < port.Pixels.Length; i += 4) colours.Add((port.Pixels[i], port.Pixels[i + 1], port.Pixels[i + 2]));
        Assert.Contains(Bgr(Palette.Day.Primary), colours);
        Assert.Contains(Bgr(Palette.Day.Residential), colours);

        // villages only from zoom 11, the peak with its height from 12
        Assert.DoesNotContain(Renderer.Render(m, 10, Mercator.LonToTileX(-20.16, 10), Mercator.LatToTileY(-30.338, 10), Palette.Day).Labels, l => l.Text == "Ironvale");
        var hill = Renderer.Render(m, 12, Mercator.LonToTileX(-20.215, 12), Mercator.LatToTileY(-30.36, 12), Palette.Day);
        Assert.Contains(hill.Labels, l => l.Text == "Mount Unity (812 m)" && l.Kind == "peak");
    }

    [Fact]
    public void Places_are_found_indexed_and_searched_without_accents_or_case()
    {
        using var m = new MapFile(Sample());
        var list = Places.For(m);
        Assert.True(File.Exists(Places.IndexFile(m.Path)));
        Assert.Equal("Port Solidarity", list[0].Name);                    // the city first
        Assert.Contains(list, p => p.Name == "Mount Unity" && p.Kind == "peak");
        Assert.Equal(7, list.Count);
        Assert.Equal(list, Places.For(m));                                 // read back from the index

        Assert.Equal("Port Solidarity", Places.Search(list, "port")[0].Name);
        Assert.Equal("Harvest", Places.Search(list, "HARV")[0].Name);
        Assert.Equal("Long Field", Places.Search(list, "field")[0].Name);  // the start of a word
        Assert.Empty(Places.Search(list, "atlantis"));
        Assert.Empty(Places.Search(list, "  "));
        Assert.Equal("lodz", Places.Fold("Łódź"));
        Assert.Equal("zurich", Places.Fold("Zürich"));
        Assert.Equal("sao tome", Places.Fold("São Tomé"));
        var accented = new List<Place> { new("Kraków", "city", 50, 19, "pl"), new("Krakowiec", "village", 50, 23, "pl") };
        Assert.Equal("Kraków", Places.Search(accented, "krakow")[0].Name);
    }

    private const string Apache = """
        <html><head><title>Index of /pub/Mirrors/download.mapsforge.org/maps/v5/europe</title></head><body>
        <h1>Index of /pub/Mirrors/download.mapsforge.org/maps/v5/europe</h1><table>
        <tr><th><a href="?C=N;O=D">Name</a></th><th><a href="?C=M;O=A">Last modified</a></th><th><a href="?C=S;O=A">Size</a></th></tr>
        <tr><td><a href="/pub/Mirrors/download.mapsforge.org/maps/v5/">Parent Directory</a></td><td>&nbsp;</td><td align="right">  - </td></tr>
        <tr><td><a href="albania.map">albania.map</a></td><td align="right">2026-11-02 04:17  </td><td align="right"> 23M</td></tr>
        <tr><td><a href="france/">france/</a></td><td align="right">2026-11-02 06:40  </td><td align="right">  - </td></tr>
        <tr><td><a href="germany.map">germany.map</a></td><td align="right">2026-11-02 08:12  </td><td align="right">3.3G</td></tr>
        <tr><td><a href="ireland-and-northern-ireland.map">ireland-and-northern-ireland.map</a></td><td align="right">2026-11-02 05:01  </td><td align="right">240M</td></tr>
        <tr><td><a href="readme.txt">readme.txt</a></td><td align="right">2020-01-01 00:00  </td><td align="right">1.2K</td></tr>
        </table></body></html>
        """;

    private const string Nginx = """
        <html><head><title>Index of /maps/v5/</title></head><body><h1>Index of /maps/v5/</h1><hr><pre><a href="../">../</a>
        <a href="africa/">africa/</a>                                            02-Nov-2026 04:00                   -
        <a href="world/">world/</a>                                             02-Nov-2026 04:00                   -
        <a href="saint-helena.map">saint-helena.map</a>                         02-Nov-2026 04:00              354304
        </pre><hr></body></html>
        """;

    [Fact]
    public void The_servers_folder_lists_are_read_with_sizes_and_dates()
    {
        var eu = Mirror.ParseListing(Apache, "europe/");
        Assert.Equal(new[] { "Albania", "France", "Germany", "Ireland and Northern Ireland" }, eu.Select(r => r.Name));
        var de = eu.Single(r => r.Name == "Germany");
        Assert.Equal(("europe/germany.map", false, "2026-11-02"), (de.Path, de.IsFolder, de.Date));
        Assert.Equal((long)(3.3 * 1024 * 1024 * 1024), de.Size);
        Assert.Equal("3.3 GB", de.SizeText);
        Assert.Equal(23L * 1024 * 1024, eu[0].Size);
        var fr = eu.Single(r => r.Name == "France");
        Assert.Equal(("europe/france/", true, (long?)null), (fr.Path, fr.IsFolder, fr.Size));

        var top = Mirror.ParseListing(Nginx, "");
        Assert.Equal(new[] { "Africa", "Saint Helena", "World" }, top.Select(r => r.Name));
        Assert.Equal(354304L, top.Single(r => r.Name == "Saint Helena").Size);
        Assert.Equal("346 KB", top.Single(r => r.Name == "Saint Helena").SizeText);

        Assert.Equal("US", Mirror.Pretty("us"));
        Assert.Equal("North America", Mirror.Pretty("north-america"));
        Assert.Equal("Bosnia Herzegovina", Mirror.Pretty("bosnia-herzegovina"));
    }

    private sealed class Stand : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Asked = new();
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public Stand(Func<HttpRequestMessage, HttpResponseMessage> answer) { _answer = answer; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Asked.Add(r); return Task.FromResult(_answer(r)); }
    }

    [Fact]
    public async Task The_list_comes_from_the_mirror_and_from_the_project_when_the_mirror_is_down()
    {
        var stand = new Stand(r => r.RequestUri!.Host.Contains("esslingen")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Apache, Encoding.UTF8, "text/html") });
        using var mirror = new Mirror(stand);
        var list = await mirror.List("europe/", CancellationToken.None);
        Assert.Equal(4, list.Count);
        Assert.Equal(new[] { "ftp-stud.hs-esslingen.de", "download.mapsforge.org" }, stand.Asked.Select(a => a.RequestUri!.Host));
        Assert.EndsWith("/maps/v5/europe/", stand.Asked[1].RequestUri!.AbsolutePath);
        Assert.Contains("CrimsonAtlas/1.0", stand.Asked[0].Headers.UserAgent.ToString());

        using var down = new Mirror(new Stand(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => down.List("", CancellationToken.None));
        Assert.Contains("(429)", e.Message);
    }

    [Fact]
    public async Task A_download_carries_on_where_it_stopped_and_must_be_a_map()
    {
        var whole = File.ReadAllBytes(Sample());
        var folder = Path.Combine(_dir, "maps");
        Directory.CreateDirectory(folder);
        // a third came last time
        File.WriteAllBytes(Path.Combine(folder, "Saint Helena.map.part"), whole[..(whole.Length / 3)]);
        var stand = new Stand(r =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith(".map") is false)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html>Busy</html>") };
            long from = r.Headers.Range?.Ranges.First().From ?? 0;
            var res = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(whole[(int)from..]) };
            if (from > 0) res.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, whole.Length - 1, whole.Length);
            return res;
        });
        using var mirror = new Mirror(stand);
        var store = new MapStore(folder);
        var region = new Region("Saint Helena", "africa/saint-helena.map", false, whole.Length, null);
        Assert.Equal(whole.Length / 3, store.Partial(region));
        Assert.False(store.Has(region));
        var got = await mirror.Download(region.Path, folder, null, CancellationToken.None);
        Assert.Equal(Path.Combine(folder, "Saint Helena.map"), got);
        Assert.Equal(whole, File.ReadAllBytes(got));
        Assert.Equal(whole.Length / 3, stand.Asked[0].Headers.Range!.Ranges.First().From);
        Assert.True(store.Has(region));
        Assert.Null(store.Partial(region));
        Assert.Equal(new[] { "Saint Helena.map", SampleMap.FileName }, store.Maps().Select(Path.GetFileName));

        // a web page in the map's place is refused and not kept
        using var busy = new Mirror(new Stand(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html>Busy</html>") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => busy.Download("europe/albania.map", folder, null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(folder, "Albania.map")));
        Assert.False(File.Exists(Path.Combine(folder, "Albania.map.part")));

        store.Delete(got);
        Assert.False(File.Exists(got));
    }

    [Fact]
    public void Mercator_goes_there_and_back()
    {
        foreach (var (lat, lon) in new[] { (0.0, 0.0), (51.5, -0.12), (-33.9, 151.2), (64.1, -21.9) })
            for (int z = 0; z <= 18; z += 6)
            {
                Assert.Equal(lat, Mercator.YToLat(Mercator.LatToY(lat, z), z), 9);
                Assert.Equal(lon, Mercator.XToLon(Mercator.LonToX(lon, z), z), 9);
            }
        Assert.Equal((0, 0), (Mercator.LonToTileX(-180, 0), Mercator.LatToTileY(85, 0)));
        Assert.Equal(2047, Mercator.LonToTileX(0, 12) - 1);
        Assert.Equal(156543.03, Mercator.MetresPerPixel(0, 0), 1);
    }
}
