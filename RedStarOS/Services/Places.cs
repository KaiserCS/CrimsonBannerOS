using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace RedStarOS.Services;

/// <summary>
/// The virtual places in the Files sidebar: <b>Recent</b> (files opened lately, from Red Star and from other Linux
/// apps through ~/.local/share/recently-used.xbel) and <b>Starred</b> (files and folders you starred).
/// </summary>
public static class Places
{
    public const string Recent = "recent:";
    public const string Starred = "starred:";

    public static bool IsVirtual(string path) => path is Recent or Starred;

    public static string Title(string place) => place == Recent ? "Recent" : "Starred";

    // ─────────── Recent ───────────
    /// <summary>Recently used files (existing ones only), newest first.</summary>
    public static List<(string Path, DateTime When)> RecentFiles(int max = 100)
    {
        var found = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        void Add(string p, DateTime when)
        {
            if (string.IsNullOrEmpty(p)) return;
            if (!found.TryGetValue(p, out var old) || when > old) found[p] = when;
        }
        // Red Star's own list is in order, newest first: give it descending times so it sorts naturally
        var own = SettingsStore.Current.RecentFiles;
        for (int i = 0; i < own.Count; i++)
        {
            DateTime t;
            try { t = File.Exists(own[i]) ? File.GetLastAccessTime(own[i]) : DateTime.MinValue; } catch { t = DateTime.MinValue; }
            // the access time can be stale (relatime); keep the list's order as a fallback
            Add(own[i], t > DateTime.MinValue ? Max(t, DateTime.Now.AddMinutes(-i)) : DateTime.Now.AddMinutes(-i));
        }
        foreach (var (p, when) in ReadXbel(XbelPath)) Add(p, when);
        return found.Where(kv => File.Exists(kv.Key)).OrderByDescending(kv => kv.Value).Take(max).Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    public static string XbelPath => Path.Combine(AppPaths.DataHome, "recently-used.xbel");

    /// <summary>file:// bookmarks of a freedesktop recently-used.xbel file, with their "modified"/"visited" time.</summary>
    public static List<(string Path, DateTime When)> ReadXbel(string file)
    {
        var list = new List<(string, DateTime)>();
        if (!File.Exists(file)) return list;
        try
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "bookmark") continue;
                var href = reader.GetAttribute("href");
                if (href == null || !href.StartsWith("file://")) continue;
                string path;
                try { path = new Uri(href).LocalPath; } catch { continue; }
                var when = DateTime.MinValue;
                foreach (var attr in new[] { "visited", "modified", "added" })
                    if (DateTime.TryParse(reader.GetAttribute(attr), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d))
                    { when = d.ToLocalTime(); break; }
                list.Add((path, when));
            }
        }
        catch { /* a broken recent list is not worth an error */ }
        return list;
    }

    public static void ForgetRecent(string path)
    {
        SettingsStore.Current.RecentFiles.RemoveAll(p => p == path);
        SettingsStore.Save();
    }

    // ─────────── Starred ───────────
    public static bool IsStarred(string path) => SettingsStore.Current.StarredPaths.Contains(path.TrimEnd('/'));

    public static List<string> StarredItems() =>
        SettingsStore.Current.StarredPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();

    /// <summary>Stars or un-stars; returns whether it is starred now.</summary>
    public static bool ToggleStar(string path)
    {
        var p = path.TrimEnd('/');
        var list = SettingsStore.Current.StarredPaths;
        bool now = !list.Remove(p);
        if (now) list.Insert(0, p);
        SettingsStore.Save();
        return now;
    }

    /// <summary>Keeps stars on things that were moved or renamed in Files.</summary>
    public static void Moved(string from, string to)
    {
        var list = SettingsStore.Current.StarredPaths;
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == from) { list[i] = to; changed = true; }
            else if (list[i].StartsWith(from + "/")) { list[i] = to + list[i][from.Length..]; changed = true; }
        }
        if (changed) SettingsStore.Save();
    }
}
