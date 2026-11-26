// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace PeoplesArchive.Model;

/// <summary>A book a source offers.</summary>
public sealed class Listing
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Language { get; set; } = "";
    /// <summary>The book's own page, where a person can read about it (also how the shelf knows it has it).</summary>
    public string Page { get; set; } = "";
    /// <summary>The file to download, when known already.</summary>
    public string? Download { get; set; }
    /// <summary>An OPDS feed of the book's own, which names its files (when the download isn't known yet).</summary>
    public string? Feed { get; set; }
    public string Licence { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class Results
{
    public List<Listing> Books { get; } = new();
    public string? Next { get; set; }
    public string? Note { get; set; }
}

/// <summary>Somewhere free books come from.</summary>
public abstract class Source
{
    public abstract string Name { get; }
    public abstract string About { get; }
    /// <summary>The terms its books come under, as the source states them.</summary>
    public abstract string Licence { get; }
    /// <summary>Who runs it (PRINCIPLES.md: who controls each thing we depend on).</summary>
    public abstract string RunBy { get; }
    /// <summary>A search, or with an empty query what it offers to browse; next: a page of results after the first.</summary>
    public abstract Task<Results> Search(Net net, string query, string? next, CancellationToken ct);
    /// <summary>The file to download for a listing.</summary>
    public virtual Task<string?> DownloadFor(Net net, Listing l, CancellationToken ct) => Task.FromResult(l.Download);
}

/// <summary>
/// The network, only ever at a person's request: a search or a download they asked for. Every request says who's
/// asking (People's Archive, and where its source is), as sources ask apps to.
/// </summary>
public sealed class Net : IDisposable
{
    public const string Agent = "PeoplesArchive/1.0 (Crimson Banner OS; +https://github.com/KaiserCS/CrimsonBannerOS)";
    public const long MaxDownload = 300L * 1024 * 1024;
    private readonly HttpClient _http;

    public Net(HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        _http.Timeout = TimeSpan.FromSeconds(90);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
    }

    public async Task<string> Text(string url, CancellationToken ct, string? user = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Auth(req, user);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new HttpRequestException("This catalogue needs you to sign in (Sources: its user name or email).");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Downloads a book to a file; it has to be an EPUB (a zip) or plain text, not a web page.</summary>
    public async Task Download(string url, string file, IProgress<double>? progress, CancellationToken ct, string? user = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Auth(req, user);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength;
        if (total > MaxDownload) throw new InvalidDataException("The book is larger than People's Archive takes (300 MB).");
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buf = new byte[81920];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                got += n;
                if (got > MaxDownload) throw new InvalidDataException("The book is larger than People's Archive takes (300 MB).");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                if (total > 0) progress?.Report((double)got / total.Value);
            }
        }
        if (!Book.IsZip(file))
        {
            var head = File.ReadAllText(file).TrimStart();
            if (type.Contains("html") || head.StartsWith('<'))
                throw new InvalidDataException("The source sent a web page, not a book. It may be busy, or no longer offer this book this way: try its page instead.");
        }
    }

    private static void Auth(HttpRequestMessage req, string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user.Trim() + ":")));
    }

    public void Dispose() => _http.Dispose();
}

// ───────── OPDS catalogues (Project Gutenberg's, and any a person adds) ─────────

public sealed class OpdsFeed
{
    public List<Listing> Entries { get; } = new();
    public string? Next { get; set; }
    /// <summary>A search address with {searchTerms} in it, when the feed gives one.</summary>
    public string? SearchTemplate { get; set; }
    /// <summary>An OpenSearch description to read the search address from.</summary>
    public string? SearchDescription { get; set; }
}

public static class Opds
{
    private const string Atom = "http://www.w3.org/2005/Atom";

    /// <summary>An OPDS 1 (Atom) feed: its books, the next page, and how to search it.</summary>
    public static OpdsFeed Parse(string xml, string baseUrl, string source, string licence)
    {
        var doc = Book.Xml(xml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("a", Atom);
        var feed = new OpdsFeed();
        var root = new Uri(baseUrl);
        string Abs(string href) => new Uri(root, WebUtility.HtmlDecode(href)).ToString();
        foreach (XmlNode link in doc.SelectNodes("/a:feed/a:link", ns)!)
        {
            var rel = link.Attributes?["rel"]?.Value ?? ""; var href = link.Attributes?["href"]?.Value; var type = link.Attributes?["type"]?.Value ?? "";
            if (href == null) continue;
            if (rel == "next") feed.Next = Abs(href);
            else if (rel == "search" && href.Contains("{searchTerms}")) feed.SearchTemplate = Abs(href).Replace("%7BsearchTerms%7D", "{searchTerms}");
            else if (rel == "search" && type.Contains("opensearchdescription")) feed.SearchDescription = Abs(href);
        }
        foreach (XmlNode e in doc.SelectNodes("/a:feed/a:entry", ns)!)
        {
            var l = new Listing { Source = source, Licence = licence };
            l.Title = Book.Clean(e.SelectSingleNode("a:title", ns)?.InnerText ?? "");
            l.Author = string.Join(", ", e.SelectNodes("a:author/a:name", ns)!.Cast<XmlNode>().Select(n => Book.Clean(n.InnerText)).Where(n => n.Length > 0));
            l.Summary = Book.Clean(Regex.Replace(WebUtility.HtmlDecode(e.SelectSingleNode("a:summary", ns)?.InnerText ?? e.SelectSingleNode("a:content", ns)?.InnerText ?? ""), "<[^>]+>", " "));
            l.Language = e.SelectSingleNode("*[local-name()='language']")?.InnerText.Trim() ?? "";
            if (e.SelectSingleNode("*[local-name()='rights']")?.InnerText is { Length: > 0 } rights) l.Licence = Book.Clean(rights);
            string? epub = null, text = null, feedLink = null, page = null;
            int best = -1;
            foreach (XmlNode link in e.SelectNodes("a:link", ns)!)
            {
                var rel = link.Attributes?["rel"]?.Value ?? ""; var href = link.Attributes?["href"]?.Value; var type = link.Attributes?["type"]?.Value ?? "";
                if (href == null) continue;
                var abs = Abs(href);
                if (rel.StartsWith("http://opds-spec.org/acquisition", StringComparison.Ordinal))
                {
                    if (type.StartsWith("application/epub+zip", StringComparison.Ordinal))
                    {
                        // a publisher's choice of several: the newest EPUB, with its pictures
                        int score = (abs.Contains("epub3") ? 2 : 0) + (abs.Contains("noimages") ? 0 : abs.Contains("images") ? 1 : 0);
                        if (score > best) { best = score; epub = abs; }
                    }
                    else if (type.StartsWith("text/plain", StringComparison.Ordinal)) text ??= abs;
                }
                else if (type.Contains("opds-catalog") || (type.StartsWith("application/atom+xml", StringComparison.Ordinal) && rel != "self"))
                    feedLink ??= abs;
                else if (rel == "alternate" && type.Contains("html")) page ??= abs;
            }
            l.Download = epub ?? text;
            l.Feed = l.Download == null ? feedLink : null;
            // a catalogue's own navigation (sort orders, categories) rather than a book: a feed with a query, no author
            if (l.Download == null && l.Author.Length == 0 && feedLink != null && feedLink.Contains('?')) continue;
            l.Page = page ?? (feedLink != null && feedLink.EndsWith(".opds", StringComparison.Ordinal) ? feedLink[..^5] : feedLink)
                     ?? l.Download ?? (e.SelectSingleNode("a:id", ns)?.InnerText.Trim() ?? "");
            if (l.Title.Length > 0 && (l.Download != null || l.Feed != null)) feed.Entries.Add(l);
        }
        return feed;
    }

    /// <summary>An OpenSearch description's search address for Atom results.</summary>
    public static string? SearchTemplateFrom(string osd, string baseUrl)
    {
        var doc = Book.Xml(osd);
        foreach (XmlNode u in doc.SelectNodes("//*[local-name()='Url']")!)
        {
            var type = u.Attributes?["type"]?.Value ?? ""; var t = u.Attributes?["template"]?.Value;
            if (t != null && (type.Contains("atom") || type.Contains("opds"))) return new Uri(new Uri(baseUrl), t).ToString().Replace("%7BsearchTerms%7D", "{searchTerms}");
        }
        return null;
    }
}

public sealed class OpdsSource : Source
{
    private readonly string _name, _about, _licence, _runBy;
    public string Start { get; }
    public string? SearchTemplate { get; private set; }
    public string? User { get; }
    public bool Custom { get; }

    public OpdsSource(string name, string start, string? searchTemplate, string about, string licence, string runBy, string? user = null, bool custom = false)
    {
        _name = name; Start = start; SearchTemplate = searchTemplate; _about = about; _licence = licence; _runBy = runBy; User = user; Custom = custom;
    }

    public override string Name => _name;
    public override string About => _about;
    public override string Licence => _licence;
    public override string RunBy => _runBy;

    public override async Task<Results> Search(Net net, string query, string? next, CancellationToken ct)
    {
        string url;
        if (next != null) url = next;
        else if (query.Trim().Length == 0) url = Start;
        else
        {
            if (SearchTemplate == null)
            {
                // the catalogue says how to search it on its first page
                var start = Opds.Parse(await net.Text(Start, ct, User), Start, Name, Licence);
                SearchTemplate = start.SearchTemplate;
                if (SearchTemplate == null && start.SearchDescription != null)
                    SearchTemplate = Opds.SearchTemplateFrom(await net.Text(start.SearchDescription, ct, User), start.SearchDescription);
                if (SearchTemplate == null) return new Results { Note = $"{Name} can't be searched, only browsed: clear the search to see what it offers." };
            }
            url = SearchTemplate.Replace("{searchTerms}", Uri.EscapeDataString(query.Trim()));
        }
        var feed = Opds.Parse(await net.Text(url, ct, User), url, Name, Licence);
        var r = new Results { Next = feed.Next };
        r.Books.AddRange(feed.Entries);
        return r;
    }

    public override async Task<string?> DownloadFor(Net net, Listing l, CancellationToken ct)
    {
        if (l.Download != null) return l.Download;
        if (l.Feed == null) return null;
        // the book's own feed names its files
        var feed = Opds.Parse(await net.Text(l.Feed, ct, User), l.Feed, Name, Licence);
        return feed.Entries.Select(e => e.Download).FirstOrDefault(d => d != null);
    }

    public string? UserFor => User;
}

// ───────── Wikisource ─────────

/// <summary>
/// Wikisource, the Wikimedia Foundation's library of free texts: searched through its own API, and downloaded as
/// EPUB through Wikimedia's export tool (WS Export), which assembles a work from its pages.
/// </summary>
public sealed class WikisourceSource : Source
{
    public string Language { get; }
    public WikisourceSource(string language = "en") { Language = language; }

    public override string Name => "Wikisource";
    public override string About => "The Wikimedia Foundation's library of free texts, transcribed and proofread by volunteers. Whole works come as EPUB through Wikimedia's own export tool.";
    public override string Licence => "Public domain or CC BY-SA, as marked on Wikisource";
    public override string RunBy => "Wikimedia Foundation (non-profit)";

    public string SearchUrl(string query, int offset) =>
        $"https://{Language}.wikisource.org/w/api.php?action=query&list=search&srsearch={Uri.EscapeDataString(query)}&srnamespace=0&srlimit=30&sroffset={offset}&format=json&formatversion=2";

    public string PageUrl(string title) => $"https://{Language}.wikisource.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/")}";

    /// <summary>WS Export's address for a work as EPUB 3.</summary>
    public string ExportUrl(string title) =>
        $"https://ws-export.wmcloud.org/?format=epub-3&lang={Language}&page={Uri.EscapeDataString(title.Replace(' ', '_'))}";

    public override async Task<Results> Search(Net net, string query, string? next, CancellationToken ct)
    {
        if (query.Trim().Length == 0)
            return new Results { Note = "Search Wikisource by title, author or a phrase from the text." };
        int offset = next != null && int.TryParse(next, out var o) ? o : 0;
        return Parse(await net.Text(SearchUrl(query.Trim(), offset), ct), this);
    }

    /// <summary>The search API's answer: whole works first (pages of a work, "Work/Chapter", after).</summary>
    public static Results Parse(string json, WikisourceSource src)
    {
        var r = new Results();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var hits)) return r;
        var list = new List<Listing>();
        foreach (var h in hits.EnumerateArray())
        {
            var title = h.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (title.Length == 0) continue;
            var snippet = h.TryGetProperty("snippet", out var s) ? WebUtility.HtmlDecode(Regex.Replace(s.GetString() ?? "", "<[^>]+>", "")) : "";
            list.Add(new Listing
            {
                Title = title, Summary = Book.Clean(snippet), Language = src.Language, Page = src.PageUrl(title),
                Download = src.ExportUrl(title), Licence = src.Licence, Source = src.Name,
            });
        }
        r.Books.AddRange(list.OrderBy(l => l.Title.Contains('/') ? 1 : 0));
        if (doc.RootElement.TryGetProperty("continue", out var c) && c.TryGetProperty("sroffset", out var off)) r.Next = off.GetInt32().ToString();
        return r;
    }
}

// ───────── the Marxists Internet Archive ─────────

/// <summary>
/// The Marxists Internet Archive's ebook collection: its index page lists every EPUB it has. The index is fetched
/// when asked (and kept for a week), and searched on this computer.
/// </summary>
public sealed class MiaSource : Source
{
    public const string IndexUrl = "https://www.marxists.org/ebooks/index.htm";
    private readonly string _cache;
    private List<Listing>? _all;

    public MiaSource(string cacheFolder) { _cache = Path.Combine(cacheFolder, "marxists-ebooks.htm"); }

    public override string Name => "Marxists Internet Archive";
    public override string About => "A volunteer archive of Marxist writers and others, run since 1990. Its ebook collection is listed on one page, which People's Archive keeps for a week and searches here.";
    public override string Licence => "Free to read and share, as the Marxists Internet Archive marks each work";
    public override string RunBy => "Marxists Internet Archive (volunteers)";

    public override async Task<Results> Search(Net net, string query, string? next, CancellationToken ct)
    {
        if (_all == null)
        {
            string html;
            if (File.Exists(_cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cache) < TimeSpan.FromDays(7)) html = await File.ReadAllTextAsync(_cache, ct);
            else
            {
                html = await net.Text(IndexUrl, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(_cache)!);
                await File.WriteAllTextAsync(_cache, html, ct);
            }
            _all = Parse(html, IndexUrl, this);
        }
        var q = query.Trim();
        var hits = q.Length == 0 ? _all : _all.Where(l => l.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || l.Author.Contains(q, StringComparison.OrdinalIgnoreCase));
        int skip = next != null && int.TryParse(next, out var n) ? n : 0;
        var page = hits.Skip(skip).Take(60).ToList();
        var r = new Results { Next = hits.Count() > skip + 60 ? (skip + 60).ToString() : null };
        r.Books.AddRange(page);
        if (q.Length == 0) r.Note = $"{_all.Count} ebooks in the archive's collection. Search by title or writer.";
        return r;
    }

    /// <summary>Every EPUB the index links to: its title and writer from the file's name and folder
    /// (ebooks/WRITER/Title_-_Author.epub).</summary>
    public static List<Listing> Parse(string html, string baseUrl, MiaSource? src)
    {
        var seen = new HashSet<string>();
        var list = new List<Listing>();
        var root = new Uri(baseUrl);
        foreach (Match m in Regex.Matches(html, "href\\s*=\\s*[\"']([^\"']+\\.epub)[\"']", RegexOptions.IgnoreCase))
        {
            var url = new Uri(root, WebUtility.HtmlDecode(m.Groups[1].Value)).ToString();
            if (!seen.Add(url)) continue;
            var path = new Uri(url).AbsolutePath;
            var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(path)).Replace('_', ' ');
            name = Regex.Replace(name, @"\s+", " ").Trim();
            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var folder = segs.Length >= 2 ? segs[^2] : "";
            string title = name, author = "";
            var dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) { title = name[..dash].Trim(); author = name[(dash + 3)..].Trim(); }
            else if (folder.Length > 0 && folder != "ebooks") author = string.Join(' ', folder.Split('-', '_').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
            list.Add(new Listing
            {
                Title = title, Author = author, Page = url, Download = url, Language = "en",
                Licence = src?.Licence ?? "", Source = src?.Name ?? "Marxists Internet Archive",
            });
        }
        return list.OrderBy(l => l.Author, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>A catalogue a person added (Sources), kept in ~/.config/peoples-archive/catalogues.json.</summary>
public sealed class SavedCatalogue
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string User { get; set; } = "";
}

/// <summary>The sources People's Archive starts with, and those a person adds.</summary>
public static class Sources
{
    public static string ConfigFolder
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(b)) b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(b, "peoples-archive");
        }
    }

    public static string CacheFolder
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(b)) b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(b, "peoples-archive");
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static List<SavedCatalogue> LoadCatalogues(string? folder = null)
    {
        var f = Path.Combine(folder ?? ConfigFolder, "catalogues.json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<List<SavedCatalogue>>(File.ReadAllText(f), Json) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException) { return new(); }
    }

    public static void SaveCatalogues(List<SavedCatalogue> list, string? folder = null)
    {
        var dir = folder ?? ConfigFolder;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "catalogues.json"), JsonSerializer.Serialize(list, Json));
    }

    public static OpdsSource From(SavedCatalogue c) => new(c.Name, c.Url, null,
        "An OPDS catalogue you added.", "As the catalogue states for each book", "Whoever runs " + new Uri(c.Url).Host,
        string.IsNullOrWhiteSpace(c.User) ? null : c.User, custom: true);

    /// <summary>Every source: the three built in, then the catalogues a person added.</summary>
    public static List<Source> All(string wikisourceLanguage = "en", string? configFolder = null) =>
        new List<Source> { Gutenberg(), new WikisourceSource(wikisourceLanguage), new MiaSource(CacheFolder) }
            .Concat(LoadCatalogues(configFolder).Where(c => Uri.TryCreate(c.Url, UriKind.Absolute, out _)).Select(From)).ToList();

    public static OpdsSource Gutenberg() => new(
        "Project Gutenberg", "https://www.gutenberg.org/ebooks/search.opds/?sort_order=downloads",
        "https://www.gutenberg.org/ebooks/search.opds/?query={searchTerms}",
        "Over 75,000 free ebooks, the oldest digital library, run by volunteers since 1971. Browsed and searched through the catalogue it offers reading apps.",
        "Public domain in the USA (check your country's law), under Project Gutenberg's terms",
        "Project Gutenberg Literary Archive Foundation (non-profit)");
}
