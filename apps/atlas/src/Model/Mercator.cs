// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonAtlas.Model;

/// <summary>Web Mercator, as OpenStreetMap's tiles and Mapsforge's files use it: degrees to tiles and pixels.</summary>
public static class Mercator
{
    public const int TileSize = 256;
    public const double MaxLatitude = 85.05112877980659;

    public static double MapSize(double zoom) => TileSize * Math.Pow(2, zoom);

    public static double LonToX(double lon, double zoom) => (lon + 180) / 360 * MapSize(zoom);

    public static double LatToY(double lat, double zoom)
    {
        lat = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        var r = lat * Math.PI / 180;
        return (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * MapSize(zoom);
    }

    public static double XToLon(double x, double zoom) => x / MapSize(zoom) * 360 - 180;

    public static double YToLat(double y, double zoom)
    {
        var n = Math.PI * (1 - 2 * y / MapSize(zoom));
        return Math.Atan(Math.Sinh(n)) * 180 / Math.PI;
    }

    public static int LonToTileX(double lon, int zoom) => Math.Clamp((int)Math.Floor(LonToX(lon, zoom) / TileSize), 0, (1 << zoom) - 1);
    public static int LatToTileY(double lat, int zoom) => Math.Clamp((int)Math.Floor(LatToY(lat, zoom) / TileSize), 0, (1 << zoom) - 1);
    public static double TileXToLon(long x, int zoom) => XToLon(x * (double)TileSize, zoom);
    public static double TileYToLat(long y, int zoom) => YToLat(y * (double)TileSize, zoom);

    /// <summary>Metres a pixel covers at a latitude and zoom.</summary>
    public static double MetresPerPixel(double lat, double zoom) =>
        Math.Cos(lat * Math.PI / 180) * 2 * Math.PI * 6378137 / MapSize(zoom);
}
