using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Brings bookmarks and history over from browsers already on this computer: Firefox (bookmark backups /
/// places.sqlite) and the Chromium family — Chrome, Chromium, Brave, Edge, Vivaldi, Opera (Bookmarks JSON / History).
/// History needs the sqlite3 program; bookmarks work without it. Nothing already in Red Star Online is duplicated.
/// </summary>
public static class BrowserImport
{
    public sealed record Source(string Name, string Kind, string ProfileDir);
    public sealed record Outcome(int Bookmarks, int History, string? Note);

    private static string H(string rel) => Path.Combine(AppPaths.Home, rel);

    /// <summary>Browser profiles found on this computer.</summary>
    public static List<Source> Detect()
    {
        var list = new List<Source>();
        // Firefox (regular, Snap, Flatpak) — every profile that has history
        foreach (var (label, root) in new[]
                 {
                     ("Firefox", H(".mozilla/firefox")), ("Firefox (Snap)", H("snap/firefox/common/.mozilla/firefox")),
                     ("Firefox (Flatpak)", H(".var/app/org.mozilla.firefox/.mozilla/firefox")), ("LibreWolf", H(".librewolf")),
                 })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "places.sqlite")) || Directory.Exists(Path.Combine(d, "bookmarkbackups"))); }
            catch { continue; }
            var found = dirs.OrderByDescending(d => { try { return new FileInfo(Path.Combine(d, "places.sqlite")).Length; } catch { return 0; } }).ToList();
            for (int i = 0; i < found.Count; i++)
                list.Add(new Source(found.Count > 1 ? $"{label} — {ProfileName(found[i])}" : label, "firefox", found[i]));
        }
        // Chromium family (regular + Flatpak)
        foreach (var (label, rel) in new[]
                 {
                     ("Google Chrome", ".config/google-chrome"), ("Chromium", ".config/chromium"), ("Chromium (Snap)", "snap/chromium/common/chromium"),
                     ("Brave", ".config/BraveSoftware/Brave-Browser"), ("Microsoft Edge", ".config/microsoft-edge"),
                     ("Vivaldi", ".config/vivaldi"), ("Opera", ".config/opera"),
                     ("Google Chrome (Flatpak)", ".var/app/com.google.Chrome/config/google-chrome"),
                     ("Chromium (Flatpak)", ".var/app/org.chromium.Chromium/config/chromium"),
                     ("Brave (Flatpak)", ".var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser"),
                 })
        {
            var root = H(rel);
            if (!Directory.Exists(root)) continue;
            var profiles = new List<string>();
            if (File.Exists(Path.Combine(root, "Bookmarks")) || File.Exists(Path.Combine(root, "History"))) profiles.Add(root); // Opera keeps it at the top
            try
            {
                profiles.AddRange(Directory.GetDirectories(root)
                    .Where(d => (Path.GetFileName(d) == "Default" || Path.GetFileName(d).StartsWith("Profile "))
                                && (File.Exists(Path.Combine(d, "Bookmarks")) || File.Exists(Path.Combine(d, "History")))));
            }
            catch { }
            foreach (var p in profiles)
                list.Add(new Source(profiles.Count > 1 ? $"{label} — {Path.GetFileName(p)}" : label, "chromium", p));
        }
        return list;
    }

    private static string ProfileName(string dir)
    {
        var n = Path.GetFileName(dir);
        int dot = n.IndexOf('.');
        return dot >= 0 && dot < n.Length - 1 ? n[(dot + 1)..] : n;
    }

    public static Task<Outcome> ImportAsync(Source src, bool bookmarks, bool history) => Task.Run(() =>
    {
        var marks = new List<(string Url, string Title, long Added)>();
        var visits = new List<(string Url, string Title, long Time, int Count)>();
        string? note = null;
        bool haveSqlite = ProcessUtil.Which("sqlite3") != null;
        try
        {
            if (src.Kind == "firefox")
            {
                var places = Path.Combine(src.ProfileDir, "places.sqlite");
                if (bookmarks)
                {
                    if (haveSqlite && File.Exists(places))
                        foreach (var r in Sql(places, "SELECT p.url, COALESCE(b.title,p.title,''), b.dateAdded/1000000 FROM moz_bookmarks b JOIN moz_places p ON p.id=b.fk WHERE b.type=1 AND p.url LIKE 'http%' ORDER BY b.dateAdded"))
                            if (r.Length >= 3) marks.Add((r[0], r[1], L(r[2])));
                    if (marks.Count == 0) marks.AddRange(FirefoxBackupBookmarks(src.ProfileDir));
                }
                if (history)
                {
                    if (!haveSqlite) note = "History needs the “sqlite3” program, which isn't installed — only bookmarks were imported.";
                    else if (File.Exists(places))
                        foreach (var r in Sql(places, "SELECT url, COALESCE(title,''), last_visit_date/1000000, visit_count FROM moz_places WHERE hidden=0 AND visit_count>0 AND url LIKE 'http%' ORDER BY last_visit_date DESC LIMIT 3000"))
                            if (r.Length >= 4) visits.Add((r[0], r[1], L(r[2]), (int)Math.Max(1, L(r[3]))));
                }
            }
            else
            {
                if (bookmarks)
                {
                    var f = Path.Combine(src.ProfileDir, "Bookmarks");
                    if (File.Exists(f))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        if (doc.RootElement.TryGetProperty("roots", out var roots))
                            foreach (var root in roots.EnumerateObject())
                                if (root.Value.ValueKind == JsonValueKind.Object) ChromeWalk(root.Value, marks);
                    }
                }
                if (history)
                {
                    var f = Path.Combine(src.ProfileDir, "History");
                    if (!haveSqlite) note = "History needs the “sqlite3” program, which isn't installed — only bookmarks were imported.";
                    else if (File.Exists(f))
                        // Chromium time: microseconds since 1601-01-01
                        foreach (var r in Sql(f, "SELECT url, COALESCE(title,''), last_visit_time/1000000-11644473600, visit_count FROM urls WHERE hidden=0 AND url LIKE 'http%' ORDER BY last_visit_time DESC LIMIT 3000"))
                            if (r.Length >= 4) visits.Add((r[0], r[1], L(r[2]), (int)Math.Max(1, L(r[3]))));
                }
            }
        }
        catch (Exception ex) { note = "Part of the import failed: " + ex.Message; }

        int nb = 0, nh = 0;
        Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
        {
            var store = BrowserData.Store;
            var haveB = new HashSet<string>(store.Bookmarks.Select(b => b.Url));
            foreach (var (url, title, added) in marks)
            {
                if (!haveB.Add(url)) continue;
                store.Bookmarks.Add(new Bookmark { Url = url, Title = string.IsNullOrWhiteSpace(title) ? url : title, Added = added > 0 ? added : DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                nb++;
            }
            var haveH = new HashSet<string>(store.History.Select(h => h.Url));
            foreach (var (url, title, time, count) in visits)
            {
                if (!haveH.Add(url)) continue;
                store.History.Add(new HistoryEntry { Url = url, Title = title, Time = time, Visits = count });
                nh++;
            }
            if (nh > 0)
            {
                store.History.Sort((a, b) => b.Time.CompareTo(a.Time));
                if (store.History.Count > 5000) store.History.RemoveRange(5000, store.History.Count - 5000);
            }
            if (nb + nh > 0) BrowserData.Save();
        });
        return new Outcome(nb, nh, note);
    });

    private static long L(string s) => long.TryParse(s.Trim(), out var v) ? v : 0;

    private static void ChromeWalk(JsonElement node, List<(string, string, long)> into)
    {
        var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "url" && node.TryGetProperty("url", out var u) && u.GetString() is { } url && url.StartsWith("http"))
        {
            long added = 0;
            if (node.TryGetProperty("date_added", out var d) && long.TryParse(d.GetString(), out var micros)) added = micros / 1000000 - 11644473600;
            into.Add((url, node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", added));
        }
        if (node.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var k in kids.EnumerateArray()) ChromeWalk(k, into);
    }

    /// <summary>Runs a query on a copy of the database (the browser may have it locked). Rows are tab-separated.</summary>
    private static List<string[]> Sql(string db, string query)
    {
        var rows = new List<string[]>();
        var tmp = Path.Combine(Path.GetTempPath(), "redstar-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var copy = Path.Combine(tmp, "db.sqlite");
            File.Copy(db, copy);
            foreach (var side in new[] { "-wal", "-shm" })
                if (File.Exists(db + side)) File.Copy(db + side, copy + side);
            var (code, output) = ProcessUtil.RunAsync(ProcessUtil.Which("sqlite3")!, new[] { "-readonly", "-separator", "\u001f", "-newline", "\u001e", copy, query }, 30000).GetAwaiter().GetResult();
            if (code != 0) return rows;
            foreach (var line in output.Split('\u001e'))
            {
                var l = line.Trim('\n', '\r');
                if (l.Length > 0) rows.Add(l.Split('\u001f'));
            }
        }
        catch { }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        return rows;
    }

    // ── Firefox bookmark backups: "mozLz40\0" + uint32 size + LZ4 block ──
    private static IEnumerable<(string, string, long)> FirefoxBackupBookmarks(string profile)
    {
        var dir = Path.Combine(profile, "bookmarkbackups");
        var result = new List<(string, string, long)>();
        if (!Directory.Exists(dir)) return result;
        var latest = Directory.GetFiles(dir, "*.jsonlz4").Concat(Directory.GetFiles(dir, "*.json"))
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        if (latest == null) return result;
        try
        {
            var bytes = File.ReadAllBytes(latest);
            string json = latest.EndsWith(".jsonlz4") ? Encoding.UTF8.GetString(MozLz4(bytes)) : Encoding.UTF8.GetString(bytes);
            using var doc = JsonDocument.Parse(json);
            FirefoxWalk(doc.RootElement, result);
        }
        catch { }
        return result;
    }

    private static void FirefoxWalk(JsonElement node, List<(string, string, long)> into)
    {
        if (node.TryGetProperty("uri", out var u) && u.GetString() is { } url && url.StartsWith("http"))
        {
            long added = node.TryGetProperty("dateAdded", out var d) && d.TryGetInt64(out var micros) ? micros / 1000000 : 0;
            into.Add((url, node.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "", added));
        }
        if (node.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var k in kids.EnumerateArray()) FirefoxWalk(k, into);
    }

    /// <summary>Decodes Mozilla's mozLz4 container (an LZ4 block with a small header).</summary>
    public static byte[] MozLz4(byte[] src)
    {
        if (src.Length < 12 || Encoding.ASCII.GetString(src, 0, 8) != "mozLz40\0") throw new InvalidDataException("not a mozLz4 file");
        int size = BitConverter.ToInt32(src, 8);
        var dst = new byte[size];
        int ip = 12, op = 0;
        while (ip < src.Length)
        {
            int token = src[ip++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = src[ip++]; lit += b; } while (b == 255); }
            Buffer.BlockCopy(src, ip, dst, op, lit);
            ip += lit; op += lit;
            if (ip >= src.Length || op >= size) break;           // the last sequence has only literals
            int offset = src[ip] | (src[ip + 1] << 8);
            ip += 2;
            int match = token & 15;
            if (match == 15) { int b; do { b = src[ip++]; match += b; } while (b == 255); }
            match += 4;
            int from = op - offset;
            if (from < 0) throw new InvalidDataException("bad LZ4 offset");
            for (int i = 0; i < match; i++) dst[op++] = dst[from + i];   // may overlap, so byte by byte
        }
        return dst;
    }
}
