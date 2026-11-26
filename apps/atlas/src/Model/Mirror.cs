// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace CrimsonAtlas.Model;

/// <summary>A country, region or folder of regions on the Mapsforge download server.</summary>
public sealed record Region(string Name, string Path, bool IsFolder, long? Size, string? Date)
{
    public string SizeText => Size is long s ? Mirror.SizeText(s) : "";
}

/// <summary>
/// The Mapsforge project's ready-made maps of the world (version 5, made from OpenStreetMap every few weeks), from
/// the university mirror it points programs to (Hochschule Esslingen), and its own server when the mirror can't be
/// reached. One download at a time, resumed where it stopped.
/// </summary>
public sealed class Mirror : IDisposable
{
    public const string Agent = "CrimsonAtlas/1.0 (Crimson Banner OS; +https://github.com/KaiserCS/CrimsonBannerOS)";
    public static readonly string[] Bases =
    {
        "https://ftp-stud.hs-esslingen.de/pub/Mirrors/download.mapsforge.org/maps/v5/",
        "https://download.mapsforge.org/maps/v5/",
    };

    private readonly HttpClient _http;

    public Mirror(HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>What's in a folder of the server ("" for the continents, "europe/" for Europe's countries).</summary>
    public async Task<List<Region>> List(string folder, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var b in Bases)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using var res = await _http.GetAsync(b + folder, cts.Token);
                Check(res);
                var list = ParseListing(await res.Content.ReadAsStringAsync(cts.Token), folder);
                if (list.Count > 0) return list;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { last = e; }
        }
        throw last as HttpRequestException ?? new HttpRequestException("The Mapsforge server couldn't be reached. Check the connection and try again.", last);
    }

    private static readonly Regex Row = new(@"<a\s+href=""(?<href>[^""?/][^""]*)""[^>]*>[^<]*</a>(?<rest>.*?)(?=<a\s|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex When = new(@"(\d{4}-\d{2}-\d{2}|\d{2}-[A-Za-z]{3}-\d{4})(?:\s+\d{2}:\d{2}(?::\d{2})?)?");
    private static readonly Regex Bytes = new(@"(?<![\w.:-])(\d+(?:\.\d+)?)([KMGT])?(?![\w.:-])", RegexOptions.IgnoreCase);

    /// <summary>Reads an Apache or nginx folder listing: its .map files and folders, with sizes and dates.</summary>
    public static List<Region> ParseListing(string html, string folder)
    {
        var list = new List<Region>();
        foreach (Match m in Row.Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase) || href.StartsWith("..") || href.Contains("://")) continue;
            href = Uri.UnescapeDataString(href);
            bool isFolder = href.EndsWith('/');
            if (!isFolder && !href.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = m.Groups["rest"].Value;
            string? date = When.Match(rest) is { Success: true } w ? w.Groups[1].Value : null;
            long? size = null;
            if (!isFolder)
            {
                var text = When.Replace(Regex.Replace(rest, "<[^>]+>", " "), " ");
                if (Bytes.Matches(text).LastOrDefault() is { } sized) size = ParseSize(sized.Groups[1].Value, sized.Groups[2].Value);
            }
            var stem = href.TrimEnd('/');
            if (stem.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            if (list.Any(r => r.Path == folder + href)) continue;
            list.Add(new Region(Pretty(stem), folder + href, isFolder, size, date));
        }
        return list.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static long ParseSize(string number, string unit)
    {
        var n = double.Parse(number, CultureInfo.InvariantCulture);
        return (long)(n * (unit.ToUpperInvariant() switch { "K" => 1024, "M" => 1024 * 1024, "G" => 1024L * 1024 * 1024, "T" => 1024L * 1024 * 1024 * 1024, _ => 1 }));
    }

    public static string SizeText(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{b / (1024.0 * 1024):0} MB",
        >= 1024 => $"{b / 1024.0:0} KB",
        _ => $"{b} bytes",
    };

    /// <summary>"north-america" → "North America", "us" → "US", "bosnia-herzegovina" → "Bosnia Herzegovina".</summary>
    public static string Pretty(string stem)
    {
        if (stem.Length <= 3 && !stem.Contains('-')) return stem.ToUpperInvariant();
        var words = stem.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w is "and" or "of" or "the" or "de" or "del" ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        var s = string.Join(' ', words);
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : stem;
    }

    /// <summary>
    /// Downloads a map into a folder: into a <c>.part</c> file first, carried on from where it stopped when it's there
    /// already (a Range request), and checked to be a Mapsforge map before it takes its name.
    /// </summary>
    public async Task<string> Download(string path, string folder, IProgress<(long Got, long? Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var name = System.IO.Path.GetFileName(path);
        var final = System.IO.Path.Combine(folder, Pretty(name[..^4]) + ".map");
        var part = final + ".part";
        Exception? last = null;
        foreach (var b in Bases)
        {
            try
            {
                await Fetch(b + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), part, progress, ct);
                if (!MapFile.IsMapFile(part))
                {
                    File.Delete(part);
                    throw new InvalidDataException("The server sent something other than a map file. Try again later.");
                }
                File.Move(part, final, overwrite: true);
                return final;
            }
            catch (HttpRequestException e) when (!ct.IsCancellationRequested) { last = e; }
            catch (IOException e) when (e is not FileNotFoundException && !ct.IsCancellationRequested && e.InnerException is System.Net.Sockets.SocketException) { last = e; }
        }
        throw last ?? new HttpRequestException("The map couldn't be downloaded.");
    }

    private async Task Fetch(string url, string part, IProgress<(long, long?)>? progress, CancellationToken ct)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0) return;   // it was all there
        Check(res);
        bool resumed = res.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) have = 0;
        long? total = res.Content.Headers.ContentLength is long len ? len + have : null;
        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buf = new byte[1 << 16];
        long got = have;
        var last = DateTime.MinValue;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            got += n;
            if ((DateTime.UtcNow - last).TotalMilliseconds > 200) { progress?.Report((got, total)); last = DateTime.UtcNow; }
        }
        progress?.Report((got, total));
        if (total is long t && got < t) throw new HttpRequestException("The download stopped before the end. Download it again to carry on.");
    }

    /// <summary>A server's refusal in plain words.</summary>
    public static void Check(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        int code = (int)res.StatusCode;
        throw new HttpRequestException(code switch
        {
            404 or 410 => $"The map isn't on the server any more ({code}). Its list may have changed: refresh it.",
            429 => "The Mapsforge server asks to wait before the next download (429). Try again in a while.",
            503 or 502 or 504 => $"The Mapsforge server is busy ({code}). Try again in a while.",
            403 => "The Mapsforge server refused the download (403).",
            _ => $"The Mapsforge server answered {code} ({res.ReasonPhrase}).",
        }, null, res.StatusCode);
    }
}

/// <summary>The maps on this computer: <c>~/.local/share/crimson-atlas/maps</c>, plus the example map.</summary>
public sealed class MapStore
{
    public string Folder { get; }

    public MapStore(string? folder = null)
    {
        Folder = folder ?? System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "crimson-atlas", "maps");
    }

    /// <summary>Every map, the example last; the example is written the first time.</summary>
    public List<string> Maps()
    {
        Directory.CreateDirectory(Folder);
        var sample = System.IO.Path.Combine(Folder, SampleMap.FileName);
        if (!File.Exists(sample)) { try { SampleMap.Write(Folder); } catch (IOException) { } }
        return Directory.EnumerateFiles(Folder, "*.map")
            .OrderBy(f => System.IO.Path.GetFileName(f) == SampleMap.FileName)
            .ThenBy(f => System.IO.Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public bool Has(Region r) => File.Exists(System.IO.Path.Combine(Folder, Mirror.Pretty(System.IO.Path.GetFileName(r.Path)[..^4]) + ".map"));

    public long? Partial(Region r)
    {
        var p = System.IO.Path.Combine(Folder, Mirror.Pretty(System.IO.Path.GetFileName(r.Path)[..^4]) + ".map.part");
        return File.Exists(p) ? new FileInfo(p).Length : null;
    }

    public void Delete(string map)
    {
        File.Delete(map);
        if (File.Exists(Places.IndexFile(map))) File.Delete(Places.IndexFile(map));
    }
}
