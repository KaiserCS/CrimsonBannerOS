using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// "Search everything" for the Start menu: besides apps it finds settings pages, recent documents, files in the
/// home folder, does quick sums, and offers a web search. The file list is built in the background and refreshed
/// every few minutes; hidden folders and build/cache folders are skipped.
/// </summary>
public static class SearchIndex
{
    public sealed record Hit(string Kind, string Title, string Subtitle, string Icon, Action Run);

    private static List<string> _files = new();
    private static DateTime _built;
    private static bool _building;
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", "target", "build", "dist", "__pycache__", "venv", ".venv", "snap", "go", "Trash" };

    /// <summary>Settings pages: name, keywords, how to open.</summary>
    public static readonly List<(string Name, string Keywords, string Icon, Action Open)> Settings = new();

    public static void Warm()
    {
        if (_building || (DateTime.Now - _built).TotalMinutes < 5) return;
        _building = true;
        Task.Run(() =>
        {
            var list = new List<string>(20000);
            try { Walk(AppPaths.Home, 0, list); } catch { }
            _files = list;
            _built = DateTime.Now;
            _building = false;
        });
    }

    private static void Walk(string dir, int depth, List<string> list)
    {
        if (depth > 6 || list.Count > 40000) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); } catch { return; }
        foreach (var e in entries)
        {
            var name = Path.GetFileName(e);
            if (name.StartsWith('.')) continue;
            bool isDir;
            try { isDir = Directory.Exists(e) && !new FileInfo(e).Attributes.HasFlag(FileAttributes.ReparsePoint); } catch { continue; }
            list.Add(isDir ? e + "/" : e);
            if (isDir && !SkipDirs.Contains(name)) Walk(e, depth + 1, list);
            if (list.Count > 40000) return;
        }
    }

    private static string Short(string path) => path.StartsWith(AppPaths.Home) ? "~" + path[AppPaths.Home.Length..] : path;

    private static string IconFor(string path)
    {
        if (path.EndsWith('/')) return "folder";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => "image",
            ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".mp3" or ".ogg" or ".flac" or ".wav" => "film",
            ".xlsx" or ".csv" or ".ods" => "sheet",
            ".pptx" or ".odp" => "slides",
            ".sh" or ".py" or ".js" or ".cs" or ".c" or ".rs" or ".go" => "terminal",
            _ => "document"
        };
    }

    private static readonly Regex MathRx = new(@"^[\d\s\.\,\+\-\*\/\(\)%]+$", RegexOptions.Compiled);

    public static List<Hit> Find(string q, int max = 12)
    {
        var hits = new List<Hit>();
        q = q.Trim();
        if (q.Length == 0) return hits;

        // quick maths: "12*7", "(3+4)/2"
        if (MathRx.IsMatch(q) && Regex.IsMatch(q, @"\d\s*[\+\-\*\/%]\s*[\d\(]"))
        {
            try
            {
                var v = Convert.ToDouble(new DataTable().Compute(q.Replace(',', '.'), null), CultureInfo.InvariantCulture);
                var text = v.ToString("0.##########", CultureInfo.CurrentCulture);
                hits.Add(new Hit("calc", "= " + text, "Calculator — press Enter to copy", "calculator", () => ClipboardHistory.Use(text)));
            }
            catch { }
        }

        // settings pages
        foreach (var s in Settings)
            if (s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase))
                hits.Add(new Hit("setting", s.Name, "Settings", s.Icon, s.Open));

        // recent documents first, then any file in the home folder
        var seen = new HashSet<string>();
        foreach (var path in RecentDocs.List(40))
        {
            if (!Path.GetFileName(path).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            seen.Add(path);
            var p = path;
            hits.Add(new Hit("recent", Path.GetFileName(p), "Recent · " + Short(Path.GetDirectoryName(p) ?? ""), IconFor(p), () => FileOpener.Open(p)));
            if (hits.Count >= max) return hits;
        }
        Warm();
        if (q.Length >= 2)
        {
            foreach (var f in _files
                         .Where(f => Path.GetFileName(f.TrimEnd('/')).Contains(q, StringComparison.OrdinalIgnoreCase) && !seen.Contains(f))
                         .OrderByDescending(f => Path.GetFileName(f.TrimEnd('/')).StartsWith(q, StringComparison.OrdinalIgnoreCase))
                         .ThenBy(f => f.Length)
                         .Take(8))
            {
                var p = f;
                bool dir = p.EndsWith('/');
                var full = p.TrimEnd('/');
                hits.Add(new Hit(dir ? "folder" : "file", Path.GetFileName(full), (dir ? "Folder · " : "File · ") + Short(Path.GetDirectoryName(full) ?? ""), IconFor(p),
                    () => { if (dir) WindowManager.Open(new Views.FileManagerWindow(full), "files"); else FileOpener.Open(full); }));
            }
        }

        // the web
        var url = WebTab.Normalize(q, BrowserData.SearchTemplate);
        bool looksLikeSite = !q.Contains(' ') && q.Contains('.') && !url.Contains("?q=") && !url.Contains("search");
        hits.Add(looksLikeSite
            ? new Hit("web", "Open " + q, "Red Star Online", "globe", () => BrowserLauncher.Launch(url))
            : new Hit("web", "Search the web for “" + q + "”", BrowserData.Settings.SearchEngine + " · Red Star Online", "search", () => BrowserLauncher.Launch(url)));
        return hits;
    }
}
