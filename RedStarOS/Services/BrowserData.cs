using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;

namespace RedStarOS.Services;

public sealed class Bookmark { public string Title { get; set; } = ""; public string Url { get; set; } = ""; public long Added { get; set; } }
public sealed class HistoryEntry { public string Url { get; set; } = ""; public string Title { get; set; } = ""; public long Time { get; set; } public int Visits { get; set; } = 1; }

public sealed class BrowserSettings
{
    public string SearchEngine { get; set; } = "DuckDuckGo";
    public bool ShowBookmarksBar { get; set; } = true;
    public bool BlockTrackers { get; set; } = true;
    public bool RestoreSession { get; set; } = true;
    public bool SaveHistory { get; set; } = true;
    public string BlockedSites { get; set; } = "";                       // one host per line (parental / personal blocklist)
    public List<string> TrackerAllowSites { get; set; } = new();         // sites where the blocker is switched off
    public Dictionary<string, double> SiteZoom { get; set; } = new();
    public Dictionary<string, bool> SitePermissions { get; set; } = new();   // "host|permission" -> allow
}

public sealed class BrowserStore
{
    public List<Bookmark> Bookmarks { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public BrowserSettings Settings { get; set; } = new();
    public List<string> Session { get; set; } = new();
}

/// <summary>Bookmarks, history, settings and the last session for Red Star Online, saved as one JSON file.</summary>
public static class BrowserData
{
    public static readonly (string Name, string Template)[] Engines =
    {
        ("DuckDuckGo", "https://duckduckgo.com/?q={0}"),
        ("Google", "https://www.google.com/search?q={0}"),
        ("Bing", "https://www.bing.com/search?q={0}"),
        ("Brave", "https://search.brave.com/search?q={0}"),
        ("Startpage", "https://www.startpage.com/do/search?q={0}"),
        ("Wikipedia", "https://en.wikipedia.org/w/index.php?search={0}"),
    };

    private static BrowserStore _s = new();
    private static bool _loaded, _dirty;
    private static DispatcherTimer? _timer;
    private static string FilePath => AppPaths.ConfigFile("browser-data.json");

    public static event Action? Changed;

    public static BrowserStore Store { get { Load(); ReloadIfChanged(); return _s; } }
    public static BrowserSettings Settings => Store.Settings;

    public static string SearchTemplate =>
        Engines.FirstOrDefault(e => e.Name == Settings.SearchEngine).Template ?? Engines[0].Template;

    /// <summary>What the address bar makes of what was typed: a web address as it is, a host name with https://,
    /// anything else a search.</summary>
    public static string Normalize(string input, string? searchTemplate = null)
    {
        searchTemplate ??= SearchTemplate;
        input = input.Trim();
        if (input.Length == 0) return "about:blank";
        if (Regex.IsMatch(input, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://") || Regex.IsMatch(input, @"^(about|data|view-source|file|mailto|javascript):", RegexOptions.IgnoreCase))
            return input;
        if (!input.Contains(' '))
        {
            var host = input.Split('/', '?', '#')[0];
            bool local = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?$");
            if (local) return "http://" + input;
            if (host.Contains('.') && !host.EndsWith(".") && Regex.IsMatch(host, @"^[\w\-\.]+(:\d+)?$")) return "https://" + input;
        }
        return string.Format(searchTemplate, Uri.EscapeDataString(input));
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        Read();
    }

    private static string _stamp = "";
    private static string Stamp()
    {
        try { var f = new FileInfo(FilePath); return f.Exists ? f.LastWriteTimeUtc.Ticks + ":" + f.Length : ""; }
        catch { return ""; }
    }

    private static void Read()
    {
        try { if (File.Exists(FilePath)) _s = JsonSerializer.Deserialize<BrowserStore>(File.ReadAllText(FilePath)) ?? new(); }
        catch { _s = new(); }
        _stamp = Stamp();
    }

    /// <summary>Red Star Online (the GTK browser, its own process) writes this file too: pick up its changes, unless
    /// ours aren't saved yet (it re-reads before writing, so it keeps them).</summary>
    private static long _lastCheck;
    private static void ReloadIfChanged()
    {
        if (_dirty || Environment.TickCount64 - _lastCheck < 1000) return;     // Store is read in loops: once a second is plenty
        _lastCheck = Environment.TickCount64;
        if (Stamp() != _stamp) Read();
    }

    public static void Save()
    {
        _dirty = true;
        Changed?.Invoke();
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick -= Flush; _timer.Tick += Flush;
        _timer.Stop(); _timer.Start();
    }

    private static void Flush(object? s, EventArgs e)
    {
        _timer?.Stop();
        FlushNow();
    }

    public static void FlushNow()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_s));
            File.Move(tmp, FilePath, true);
            _stamp = Stamp();
        }
        catch { }
    }

    public static string Host(string url)
    {
        try { return new Uri(url).Host.ToLowerInvariant(); } catch { return ""; }
    }

    // ── bookmarks ──
    public static Bookmark? FindBookmark(string url) => Store.Bookmarks.FirstOrDefault(b => b.Url == url);

    public static void ToggleBookmark(string url, string title)
    {
        var b = FindBookmark(url);
        if (b != null) Store.Bookmarks.Remove(b);
        else Store.Bookmarks.Add(new Bookmark { Url = url, Title = string.IsNullOrWhiteSpace(title) ? url : title, Added = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        Save();
    }

    public static void RemoveBookmark(Bookmark b) { Store.Bookmarks.Remove(b); Save(); }

    // ── history ──
    public static void RecordVisit(string url, string title)
    {
        if (!Settings.SaveHistory || string.IsNullOrEmpty(url)) return;
        if (!(url.StartsWith("http://") || url.StartsWith("https://"))) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var h = Store.History.FirstOrDefault(x => x.Url == url);
        if (h != null) { h.Time = now; h.Visits++; if (!string.IsNullOrEmpty(title)) h.Title = title; Store.History.Remove(h); Store.History.Insert(0, h); }
        else Store.History.Insert(0, new HistoryEntry { Url = url, Title = title, Time = now });
        if (Store.History.Count > 5000) Store.History.RemoveRange(5000, Store.History.Count - 5000);
        Save();
    }

    public static void UpdateTitle(string url, string title)
    {
        var h = Store.History.FirstOrDefault(x => x.Url == url);
        if (h != null && !string.IsNullOrEmpty(title) && h.Title != title) { h.Title = title; Save(); }
    }

    public static void ClearHistory() { Store.History.Clear(); Save(); }
    public static void RemoveHistory(HistoryEntry h) { Store.History.Remove(h); Save(); }

    public static List<HistoryEntry> MostVisited(int n) =>
        Store.History.GroupBy(h => Host(h.Url)).Where(g => g.Key.Length > 0)
            .Select(g => g.OrderByDescending(h => h.Visits).First())
            .OrderByDescending(h => h.Visits).Take(n).ToList();

    // ── suggestions for the address bar ──
    public sealed record Suggestion(string Text, string Url, string Kind);

    public static List<Suggestion> Suggest(string input, int max = 6)
    {
        var q = input.Trim();
        var res = new List<Suggestion>();
        if (q.Length == 0) return res;
        var low = q.ToLowerInvariant();
        foreach (var b in Store.Bookmarks)
            if (b.Url.ToLowerInvariant().Contains(low) || b.Title.ToLowerInvariant().Contains(low))
                res.Add(new Suggestion("★ " + (b.Title.Length > 0 ? b.Title : b.Url), b.Url, "bookmark"));
        foreach (var h in Store.History.OrderByDescending(h => h.Visits))
        {
            if (res.Count >= max) break;
            if ((h.Url.ToLowerInvariant().Contains(low) || h.Title.ToLowerInvariant().Contains(low)) && res.All(r => r.Url != h.Url))
                res.Add(new Suggestion("🕘 " + (h.Title.Length > 0 ? h.Title : h.Url), h.Url, "history"));
        }
        return res.Take(max).ToList();
    }

    public static bool IsBlockedSite(string url)
    {
        var host = Host(url);
        if (host.Length == 0) return false;
        foreach (var line in Settings.BlockedSites.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").Trim('/');
            if (p.Length == 0) continue;
            if (host == p || host.EndsWith("." + p)) return true;
        }
        return false;
    }
}
