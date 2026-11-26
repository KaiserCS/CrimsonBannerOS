// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonAtlas.Model;

/// <summary>
/// An invented island in the South Atlantic, written as a Mapsforge file: the map Atlas opens before any map is
/// downloaded, and the one its tests and screenshots use, so they need no network.
/// </summary>
public static class SampleMap
{
    public const string FileName = "Example Island.map";
    public const double MinLat = -30.56, MinLon = -20.46, MaxLat = -30.24, MaxLon = -19.94;
    public static readonly (double Lat, double Lon) Port = (-30.452, -20.258);

    public static string Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FileName);
        MapWriter.Write(file, Features(), MinLat, MinLon, MaxLat, MaxLon, start: Port);
        return file;
    }

    /// <summary>The island's coast: an irregular oval around its middle, as a closed ring.</summary>
    public static (double, double)[] Coast()
    {
        const double cLat = -30.40, cLon = -20.20;
        var ring = new List<(double, double)>();
        for (int i = 0; i < 96; i++)
        {
            double a = i * 2 * Math.PI / 96;
            double r = 1 + 0.13 * Math.Sin(3 * a + 0.4) + 0.07 * Math.Cos(5 * a) + 0.04 * Math.Sin(11 * a + 1.3);
            ring.Add((cLat + 0.105 * r * Math.Sin(a), cLon + 0.175 * r * Math.Cos(a)));
        }
        ring.Add(ring[0]);
        return ring.ToArray();
    }

    private static (double, double)[] Blob(double lat, double lon, double dLat, double dLon, int seed, int n = 28)
    {
        var ring = new List<(double, double)>();
        for (int i = 0; i < n; i++)
        {
            double a = i * 2 * Math.PI / n;
            double r = 1 + 0.18 * Math.Sin(2 * a + seed) + 0.1 * Math.Cos(3 * a + seed * 0.7);
            ring.Add((lat + dLat * r * Math.Sin(a), lon + dLon * r * Math.Cos(a)));
        }
        ring.Add(ring[0]);
        return ring.ToArray();
    }

    public static List<Feature> Features()
    {
        var f = new List<Feature>
        {
            Feature.Area(null, (MaxLat, MinLon), (MaxLat, MaxLon), (MinLat, MaxLon), (MinLat, MinLon)).Tag("natural", "sea"),
            Feature.Area(null, Coast()).Tag("natural", "nosea"),
            Feature.Area(null, Blob(-30.355, -20.135, 0.035, 0.07, 1)).Tag("natural", "wood"),
            Feature.Area(null, Blob(-30.335, -20.300, 0.025, 0.05, 4)).Tag("landuse", "forest"),
            Feature.Area(null, Blob(-30.430, -20.120, 0.022, 0.045, 2)).Tag("landuse", "farmland"),
            Feature.Area(null, Blob(-30.405, -20.330, 0.018, 0.035, 5)).Tag("landuse", "farmland"),
            Feature.Area(null, Blob(-30.449, -20.255, 0.012, 0.022, 3)).Tag("landuse", "residential"),
            Feature.Area(null, Blob(-30.441, -20.214, 0.006, 0.012, 6)).Tag("landuse", "industrial"),
            Feature.Area(null, Blob(-30.445, -20.268, 0.0028, 0.004, 7, 16)).Tag("leisure", "park"),
            Feature.Area("Lake Concord", Blob(-30.385, -20.215, 0.010, 0.022, 8)).Tag("natural", "water"),
            Feature.Area(null, Blob(-30.497, -20.160, 0.004, 0.02, 9, 20)).Tag("natural", "beach"),

            Feature.Line("Concord River", (-30.394, -20.215), (-30.405, -20.222), (-30.418, -20.236), (-30.431, -20.240),
                (-30.444, -20.243), (-30.458, -20.246), (-30.470, -20.244)).Tag("waterway", "river"),
            Feature.Line(null, (-30.34, -20.20), (-30.36, -20.205), (-30.374, -20.214), (-30.383, -20.215)).Tag("waterway", "stream"),

            Feature.Line("Harvest Road", (-30.452, -20.258), (-30.444, -20.230), (-30.437, -20.190), (-30.425, -20.150),
                (-30.410, -20.115), (-30.395, -20.090)).Tag("highway", "primary"),
            Feature.Line("Coast Road", (-30.452, -20.258), (-30.440, -20.300), (-30.420, -20.340), (-30.395, -20.360),
                (-30.360, -20.350), (-30.330, -20.310)).Tag("highway", "secondary"),
            Feature.Line("Ironvale Road", (-30.437, -20.190), (-30.410, -20.185), (-30.385, -20.170), (-30.360, -20.165),
                (-30.335, -20.160)).Tag("highway", "tertiary"),
            Feature.Line(null, (-30.425, -20.150), (-30.440, -20.135), (-30.452, -20.120)).Tag("highway", "unclassified"),
            Feature.Line(null, (-30.360, -20.165), (-30.345, -20.140), (-30.333, -20.130)).Tag("highway", "track"),
            Feature.Line(null, (-30.383, -20.170), (-30.372, -20.150), (-30.362, -20.142)).Tag("highway", "path"),

            Feature.Line(null, (-30.441, -20.214), (-30.430, -20.180), (-30.418, -20.150), (-30.404, -20.118),
                (-30.398, -20.098)).Tag("railway", "rail"),
            Feature.Line(null, (-30.4535, -20.282), (-30.4535, -20.230)).Tag("highway", "service"),

            Feature.Point(-30.452, -20.258, "Port Solidarity").Tag("place", "city"),
            Feature.Point(-30.396, -20.092, "Harvest").Tag("place", "town"),
            Feature.Point(-30.338, -20.160, "Ironvale").Tag("place", "village"),
            Feature.Point(-30.335, -20.310, "Long Field").Tag("place", "hamlet"),
            Feature.Point(-30.442, -20.267, "Old Harbour").Tag("place", "suburb"),
            Feature.Point(-30.258, -20.400, "Gull Rock").Tag("place", "island"),
        };
        var peak = Feature.Point(-30.360, -20.215, "Mount Unity").Tag("natural", "peak");
        peak.Elevation = 812;
        f.Add(peak);

        // the port's streets and buildings
        for (int i = 0; i < 5; i++)
        {
            double lat = -30.4445 - i * 0.0025;
            f.Add(Feature.Line(i == 2 ? "May Day Street" : null, (lat, -20.274), (lat, -20.240)).Tag("highway", "residential"));
        }
        for (int j = 0; j < 7; j++)
        {
            double lon = -20.272 + j * 0.005;
            f.Add(Feature.Line(j == 3 ? "Dockers' Avenue" : null, (-30.4440, lon), (-30.4560, lon)).Tag("highway", "residential"));
        }
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 6; j++)
            {
                if (i == 1 && j is 0 or 1) continue;                        // the park
                double lat = -30.4452 - i * 0.0025, lon = -20.2712 + j * 0.005;
                for (int b = 0; b < 3; b++)
                {
                    double l0 = lon + 0.0004 + b * 0.0015, a0 = lat - 0.0003;
                    f.Add(Feature.Area(null, (a0, l0), (a0, l0 + 0.0011), (a0 - 0.0016, l0 + 0.0011), (a0 - 0.0016, l0)).Tag("building", "yes"));
                }
            }
        f.Add(Feature.Line(null, (MaxLat - 0.001, -20.0), (MinLat + 0.001, -20.0)).Tag("boundary", "administrative").Tag("admin_level", "2"));
        return f;
    }
}
