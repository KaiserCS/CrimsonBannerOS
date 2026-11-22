// SPDX-License-Identifier: GPL-3.0-or-later
// CD Player's shelf: the music in ~/Music, one album per folder, with the cover found next to the tracks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RedStarOS.Services;

/// <summary>A track: a file, or an audio CD's track (Path is then a cdda:// address).</summary>
public sealed record MusicTrack(string Path, string Title, string Artist, string Album, int Number, long LengthMs = 0)
{
    public bool IsFile => !Path.Contains("://");
}

public sealed record MusicAlbum(string Key, string Title, string Artist, string? Cover, IReadOnlyList<MusicTrack> Tracks)
{
    public long LengthMs => Tracks.Sum(t => t.LengthMs);
}

public static class MusicLibrary
{
    public const string UnknownArtist = "Unknown artist";

    /// <summary>What CD Player plays: the shell's audio types, and the lossless ones people keep albums in.</summary>
    public static readonly string[] Extensions =
        FileOps.AudioExtensions.Concat(new[] { ".aif", ".aiff", ".wv", ".ape", ".wma", ".mka", ".oga" }).Distinct().ToArray();

    private static readonly string[] CoverNames = { "cover", "folder", "front", "albumart", "album", "art" };
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

    public static bool IsAudio(string path) => Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The XDG music folder (XDG_MUSIC_DIR in ~/.config/user-dirs.dirs), else ~/Music.</summary>
    public static string Folder(string? home = null)
    {
        home ??= AppPaths.Home;
        try
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(cfg)) cfg = System.IO.Path.Combine(home, ".config");
            var dirs = System.IO.Path.Combine(cfg, "user-dirs.dirs");
            if (File.Exists(dirs) && UserDir(File.ReadAllLines(dirs), "XDG_MUSIC_DIR", home) is { } d) return d;
        }
        catch { /* the default below */ }
        return System.IO.Path.Combine(home, "Music");
    }

    public static string? UserDir(IEnumerable<string> lines, string key, string home)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith(key + "=", StringComparison.Ordinal)) continue;
            var v = line[(key.Length + 1)..].Trim().Trim('"').Replace("$HOME", home);
            return v.Length > 0 ? v : null;
        }
        return null;
    }

    private static readonly Regex Numbered = new(@"^\s*(?:(?:cd|disc)\s*\d+\s*[-._ ]\s*)?(\d{1,3})(?:\s*[-._)]\s*|\s+)(.+)$", RegexOptions.IgnoreCase);

    /// <summary>A track's number, title and artist as far as its path tells them:
    /// Artist/Album/01 - Title.flac, Album/03. Artist - Title.mp3, Title.ogg …</summary>
    public static MusicTrack FromPath(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        var number = 0;
        var m = Numbered.Match(name);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n < 200) { number = n; name = m.Groups[2].Value.Trim(); }

        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var album = System.IO.Path.GetFileName(dir);
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir) ?? "");
        string artist = UnknownArtist;

        // "Artist - Album" folders
        var dash = album.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) { artist = album[..dash].Trim(); album = album[(dash + 3)..].Trim(); }
        else if (!IsMusicRoot(album) && !IsMusicRoot(parent)) artist = parent;

        // "Artist - Title" file names win over the folder
        dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) { artist = name[..dash].Trim(); name = name[(dash + 3)..].Trim(); }

        if (album.Length == 0 || IsMusicRoot(album)) album = "Loose tracks";
        return new MusicTrack(path, name.Length > 0 ? name : System.IO.Path.GetFileName(path), artist, album, number);
    }

    private static bool IsMusicRoot(string folder) =>
        folder.Equals("Music", StringComparison.OrdinalIgnoreCase) || folder.Equals("Musik", StringComparison.OrdinalIgnoreCase) ||
        folder.Equals("Downloads", StringComparison.OrdinalIgnoreCase) || folder.Length == 0;

    /// <summary>The album's cover next to its tracks: cover.jpg, folder.png, front.jpg …, or the only picture there.</summary>
    public static string? FindCover(string dir)
    {
        try
        {
            var pics = Directory.EnumerateFiles(dir).Where(f => ImageExts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())).ToList();
            foreach (var want in CoverNames)
            {
                var hit = pics.FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p).Equals(want, StringComparison.OrdinalIgnoreCase))
                          ?? pics.FirstOrDefault(p => System.IO.Path.GetFileNameWithoutExtension(p).StartsWith(want, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return pics.Count == 1 ? pics[0] : null;
        }
        catch { return null; }
    }

    /// <summary>One album per folder, tracks in their numbered order; albums by artist, then title.</summary>
    public static List<MusicAlbum> Group(IEnumerable<string> files, Func<string, string?>? cover = null)
    {
        cover ??= FindCover;
        return files.Where(IsAudio)
            .GroupBy(f => System.IO.Path.GetDirectoryName(f) ?? "", StringComparer.Ordinal)
            .Select(g =>
            {
                var tracks = g.Select(FromPath)
                    .OrderBy(t => t.Number == 0 ? int.MaxValue : t.Number)
                    .ThenBy(t => System.IO.Path.GetFileName(t.Path), StringComparer.OrdinalIgnoreCase).ToList();
                var artists = tracks.Select(t => t.Artist).Distinct().ToList();
                var artist = artists.Count == 1 ? artists[0] : "Various artists";
                return new MusicAlbum(g.Key, tracks[0].Album, artist, cover(g.Key), tracks);
            })
            .OrderBy(a => a.Artist == UnknownArtist ? 1 : 0)
            .ThenBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Every album under the folder (~/Music by default). Unreadable folders are skipped.</summary>
    public static List<MusicAlbum> Scan(string? root = null, int max = 50000)
    {
        root ??= Folder();
        if (!Directory.Exists(root)) return new();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden };
        try { return Group(Directory.EnumerateFiles(root, "*", opts).Where(IsAudio).Take(max)); }
        catch { return new(); }
    }

    /// <summary>The shelf's filter: every word must be in the album's title, artist or one of its tracks.</summary>
    public static IEnumerable<MusicAlbum> Filter(IEnumerable<MusicAlbum> albums, string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return albums;
        return albums.Where(a => words.All(w =>
            a.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase) || a.Artist.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            a.Tracks.Any(t => t.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase))));
    }

    /// <summary>Files dropped or opened by hand, played as one disc in the order given.</summary>
    public static MusicAlbum Loose(IReadOnlyList<string> files)
    {
        var tracks = files.Select((f, i) => FromPath(f) with { Number = i + 1 }).ToList();
        var dirs = files.Select(f => System.IO.Path.GetDirectoryName(f) ?? "").Distinct().ToList();
        if (dirs.Count == 1)
        {
            var g = Group(files);
            if (g.Count == 1) return g[0];
        }
        return new MusicAlbum("loaded:" + string.Join("|", files.Take(3)), "Loaded tracks",
            tracks.Select(t => t.Artist).Distinct().Count() == 1 ? tracks[0].Artist : "Various artists", null, tracks);
    }
}
