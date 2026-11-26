// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CrimsonAtlas.Model;

/// <summary>A named place on a map, for search.</summary>
public sealed record Place(string Name, string Kind, double Lat, double Lon, string Map)
{
    public int Rank => Kind switch
    {
        "country" => 0, "city" => 1, "state" => 2, "town" => 3, "island" => 4, "village" => 5, "suburb" => 6, "peak" => 7, _ => 8,
    };

    public string KindText => Kind switch
    {
        "city" => "City", "town" => "Town", "village" => "Village", "hamlet" => "Hamlet", "suburb" => "Suburb",
        "island" => "Island", "peak" => "Peak", "country" => "Country", "state" => "Region", "locality" => "Locality",
        "neighbourhood" or "quarter" => "Neighbourhood", _ => "Place",
    };

    public int Zoom => Kind switch
    {
        "country" => 6, "state" => 8, "city" => 12, "town" => 13, "island" => 12, "peak" => 14, _ => 15,
    };
}

/// <summary>
/// The places in a map file, found once by reading every tile's points of interest and kept beside the map as
/// <c>.places.json</c>, so search is on your computer and instant.
/// </summary>
public static class Places
{
    public static string IndexFile(string map) => map + ".places.json";

    public static List<Place> For(MapFile map, CancellationToken cancel = default)
    {
        var file = IndexFile(map.Path);
        try
        {
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(map.Path))
                return JsonSerializer.Deserialize<List<Place>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var list = Build(map, cancel);
        try { File.WriteAllText(file, JsonSerializer.Serialize(list)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return list;
    }

    public static List<Place> Build(MapFile map, CancellationToken cancel = default)
    {
        // the middle interval (Mapsforge's maps: zooms 8 to 11, at base zoom 10), which has cities, towns and villages;
        // the detailed one would mean reading the whole file
        var iv = map.IntervalFor(11);
        var seen = new HashSet<(string, long, long)>();
        var list = new List<Place>();
        var name = System.IO.Path.GetFileNameWithoutExtension(map.Path);
        for (int ty = iv.Top; ty <= iv.Bottom; ty++)
            for (int tx = iv.Left; tx <= iv.Right; tx++)
            {
                cancel.ThrowIfCancellationRequested();
                TileData d;
                try { d = map.ReadBase(iv, tx, ty, Math.Min((int)iv.Max, 16), ways: false); }
                catch (InvalidDataException) { continue; }
                foreach (var p in d.Pois)
                {
                    if (string.IsNullOrWhiteSpace(p.Name)) continue;
                    string? kind = p.Tags.TryGetValue("place", out var pl) ? pl
                        : p.Tags.TryGetValue("natural", out var n) && n is "peak" or "volcano" ? "peak" : null;
                    if (kind == null) continue;
                    if (seen.Add((p.Name!, (long)Math.Round(p.Lat * 1000), (long)Math.Round(p.Lon * 1000))))
                        list.Add(new Place(p.Name!, kind, p.Lat, p.Lon, name));
                }
            }
        return list.OrderBy(p => p.Rank).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Folds case and accents, so "Zurich" finds "Zürich" and "lodz" finds "Łódź".</summary>
    public static string Fold(string s)
    {
        var b = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (c < 0x80) { b.Append(c); continue; }
            int i = Accented.IndexOf(c);
            if (i >= 0) { b.Append(Plain[i]); continue; }
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) continue;
            b.Append(c switch { 'ß' => "ss", 'æ' => "ae", 'œ' => "oe", 'þ' => "th", _ => c.ToString() });
        }
        return b.ToString();
    }

    // Latin letters with accents and their plain forms (a table, since the app runs without ICU's normalisation)
    private const string Accented = "àáâãäåāăąçćĉċčďđèéêëēĕėęěĝğġģĥħìíîïĩīĭįıĵķĺļľŀłñńņňòóôõöøōŏőŕŗřśŝşšșţťŧțùúûüũūŭůűųŵýÿŷźżžðġ";
    private const string Plain    = "aaaaaaaaacccccddeeeeeeeeegggghhiiiiiiiiijklllllnnnnooooooooorrrsssssttttuuuuuuuuuuwyyyzzzdg";

    /// <summary>Best matches first: whole name, then start of the name, then start of a word, then anywhere; bigger places first.</summary>
    public static List<Place> Search(IEnumerable<Place> places, string query, int max = 30)
    {
        var q = Fold(query.Trim());
        if (q.Length == 0) return new();
        return places
            .Select(p => (p, n: Fold(p.Name)))
            .Select(x => (x.p, score: x.n == q ? 0 : x.n.StartsWith(q) ? 1 : x.n.Contains(" " + q) || x.n.Contains("-" + q) ? 2 : x.n.Contains(q) ? 3 : 9))
            .Where(x => x.score < 9)
            .OrderBy(x => x.score).ThenBy(x => x.p.Rank).ThenBy(x => x.p.Name.Length)
            .Take(max).Select(x => x.p).ToList();
    }
}
