using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RedStarOS.Services;

/// <summary>
/// Which program opens which kind of file ("Default apps" in Settings). "redstar" = the built-in Red Star app;
/// otherwise the path of a Linux .desktop file. Can also be applied system-wide through xdg-mime.
/// </summary>
public static class DefaultApps
{
    public sealed record Kind(string Id, string Label, string RedStarName, string[] Extensions, string[] Mimes);

    public static readonly Kind[] Kinds =
    {
        new("web", "Web browser", "Red Star Online", Array.Empty<string>(), new[] { "x-scheme-handler/http", "x-scheme-handler/https", "text/html" }),
        new("documents", "Documents", "Red Star Writer", new[] { ".docx", ".txt", ".md", ".html", ".htm", ".rtf", ".odt" }, new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "text/plain", "text/markdown" }),
        new("sheets", "Spreadsheets", "Red Star Sheets", new[] { ".xlsx", ".csv", ".tsv", ".ods" }, new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/csv" }),
        new("pdf", "PDF documents", "Red Star Online", new[] { ".pdf" }, new[] { "application/pdf" }),
        new("images", "Pictures", "Image Viewer", new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".tif", ".tiff" }, new[] { "image/png", "image/jpeg", "image/gif", "image/webp" }),
        new("media", "Video and music", "Red Star Video", new[] { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".mp3", ".ogg", ".flac", ".wav", ".m4a" }, new[] { "video/mp4", "video/x-matroska", "video/webm", "audio/mpeg", "audio/ogg", "audio/flac" }),
    };

    private static Dictionary<string, string>? _map;
    private static string FilePath => AppPaths.ConfigFile("default-apps.json");

    private static Dictionary<string, string> Map
    {
        get
        {
            if (_map != null) return _map;
            try { if (File.Exists(FilePath)) _map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)); } catch { }
            return _map ??= new Dictionary<string, string>();
        }
    }

    /// <summary>"redstar", "system" (the desktop's own default, xdg-open) or a .desktop file path.</summary>
    public static string Get(string kind) => Map.TryGetValue(kind, out var v) ? v : kind == "pdf" ? "system" : "redstar";

    public static void Set(string kind, string value, bool systemWide)
    {
        Map[kind] = value;
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Map)); } catch { }
        if (systemWide && value.EndsWith(".desktop") && ProcessUtil.Which("xdg-mime") is { } xm)
        {
            var k = Kinds.First(x => x.Id == kind);
            var id = Path.GetFileName(value);
            foreach (var mime in k.Mimes) _ = ProcessUtil.RunAsync(xm, new[] { "default", id, mime }, 5000);
        }
    }

    public static Kind? KindFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Kinds.FirstOrDefault(k => k.Extensions.Contains(ext));
    }

    /// <summary>Opens the file / URL with the chosen Linux app, when the user picked one. False: use Red Star.</summary>
    public static bool TryOpenWithLinuxApp(string kind, string pathOrUrl)
    {
        var choice = Get(kind);
        if (choice == "redstar") return false;
        if (choice == "system")
        {
            if (kind == "web") return false;            // links stay in Red Star Online unless a browser is picked
            SystemUtil.OpenExternal(pathOrUrl);
            return true;
        }
        var app = DesktopEntries.All().FirstOrDefault(a => a.FilePath == choice);
        if (app == null) return false;
        var tokens = DesktopEntries.Tokenize(app.Exec);
        bool placed = false;
        var args = new List<string>();
        foreach (var t in tokens.Skip(1))
        {
            if (t is "%f" or "%F" or "%u" or "%U") { args.Add(pathOrUrl); placed = true; }
            else if (!(t.Length == 2 && t[0] == '%')) args.Add(t.Replace("%%", "%"));
        }
        if (!placed) args.Add(pathOrUrl);
        if (tokens.Count == 0) return false;
        if (app.Terminal) ProcessUtil.RunInTerminal(tokens[0], args);
        else ProcessUtil.StartDetached(tokens[0], args, AppPaths.Home);
        return true;
    }
}
