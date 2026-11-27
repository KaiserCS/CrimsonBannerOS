using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RedStarOS.Services;

public sealed record LinuxApp(string Name, string Exec, bool Terminal, string Categories, string FilePath,
                             string? Icon = null, string? WmClass = null, string MimeTypes = "", bool Ours = false)
{
    /// <summary>Taskbar / pin id: "linux:" + the .desktop file.</summary>
    public string Id => "linux:" + FilePath;
}

/// <summary>Finds and launches the machine's installed applications (freedesktop .desktop files).</summary>
public static class DesktopEntries
{
    private static List<LinuxApp>? _cache;

    private static IEnumerable<string> Dirs()
    {
        yield return "/usr/share/applications";
        yield return "/usr/local/share/applications";
        yield return Path.Combine(AppPaths.DataHome, "applications");
        yield return "/var/lib/flatpak/exports/share/applications";
        yield return Path.Combine(AppPaths.DataHome, "flatpak", "exports", "share", "applications");
        yield return "/var/lib/snapd/desktop/applications";
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (!string.IsNullOrEmpty(xdg))
            foreach (var d in xdg.Split(':', StringSplitOptions.RemoveEmptyEntries))
                yield return Path.Combine(d, "applications");
    }

    private static string? _signature;

    /// <summary>The program folders' state when the list was last read (<see cref="All"/>).</summary>
    public static string? CurrentSignature => _signature;

    /// <summary>When each folder of .desktop files last changed (installing or removing a program changes it).</summary>
    private static string Signature() => string.Join("|", Dirs().Distinct().Select(d =>
    {
        try { return Directory.Exists(d) ? Directory.GetLastWriteTimeUtc(d).Ticks.ToString() : "-"; }
        catch { return "?"; }
    }));

    /// <summary>The installed programs. Read again when one of their folders has changed since the last time, so a
    /// program installed from Software (or anywhere else) shows up the next time the Start menu opens.</summary>
    public static IReadOnlyList<LinuxApp> All()
    {
        var sig = Signature();
        if (_cache != null && sig == _signature) return _cache;
        _signature = sig;
        _byClass.Clear();
        var map = new Dictionary<string, LinuxApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Dirs().Distinct())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.desktop", SearchOption.TopDirectoryOnly))
                {
                    var app = Parse(file);
                    if (app != null && !map.ContainsKey(app.Name)) map[app.Name] = app;
                }
            }
            catch { /* unreadable directory */ }
        }
        _cache = map.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return _cache;
    }

    private static LinuxApp? Parse(string file)
    {
        try
        {
            string? name = null, exec = null, type = null, cats = "", icon = null, wmClass = null, mimes = "";
            bool terminal = false, hidden = false, inEntry = false, ours = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[') { inEntry = line == "[Desktop Entry]"; continue; }
                if (!inEntry) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "Name": name = val; break;
                    case "Exec": exec = val; break;
                    case "Type": type = val; break;
                    case "Categories": cats = val; break;
                    case "Icon": icon = val; break;
                    case "StartupWMClass": wmClass = val; break;
                    case "MimeType": mimes = val; break;
                    case "Terminal": terminal = val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "X-CrimsonBanner-App": ours = val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "NoDisplay":
                    case "Hidden":
                        if (val.Equals("true", StringComparison.OrdinalIgnoreCase)) hidden = true;
                        break;
                }
            }
            if (hidden || name == null || exec == null || (type != null && type != "Application")) return null;
            // our own apps (apps/, from Software's Crimson Banner tab): crimson-*.desktop, or marked as ours
            ours |= Path.GetFileName(file).StartsWith("crimson-", StringComparison.OrdinalIgnoreCase);
            return new LinuxApp(name, exec, terminal, cats, file, icon, wmClass, mimes ?? "", ours);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Forgets what was read (e.g. after programs were installed); the next call scans again.</summary>
    public static void Reload() { _cache = null; _byClass.Clear(); _iconCache.Clear(); }

    /// <summary>The app behind a "linux:…" id.</summary>
    public static LinuxApp? FromId(string id) =>
        id.StartsWith("linux:") ? All().FirstOrDefault(a => a.FilePath == id[6..]) ?? ParseFile(id[6..]) : null;

    public static LinuxApp? ParseFile(string path) => File.Exists(path) ? Parse(path) : null;

    /// <summary>The Start menu's own category (Office, Media, Games, Online, Utilities, System) for one of our apps,
    /// from its .desktop Categories.</summary>
    public static string ShellCategory(string categories)
    {
        var c = categories.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (c.Contains("Game")) return "RSGames";
        if (c.Overlaps(new[] { "Office", "Education", "Science", "Economy", "Literature", "Viewer", "Finance" })) return "RSOffice";
        if (c.Overlaps(new[] { "AudioVideo", "Audio", "Video", "Graphics", "Photography" })) return "RSMedia";
        if (c.Overlaps(new[] { "Network", "WebBrowser", "Email", "Chat" })) return "RSOnline";
        if (c.Overlaps(new[] { "System", "Settings" })) return "System";
        return "RSUtilities";
    }

    /// <summary>
    /// The installed app a window belongs to, from its WM_CLASS: StartupWMClass, then the .desktop file name
    /// (org.gnome.Nautilus.desktop ↔ "org.gnome.Nautilus", firefox.desktop ↔ "firefox"), then the program name.
    /// </summary>
    public static LinuxApp? ForWindow(string instance, string cls)
    {
        if (string.IsNullOrEmpty(instance) && string.IsNullOrEmpty(cls)) return null;
        var key = (instance + "|" + cls).ToLowerInvariant();
        if (_byClass.TryGetValue(key, out var hit)) return hit;
        bool Eq(string? a, string b) => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
        var apps = All();
        var found = apps.FirstOrDefault(a => Eq(a.WmClass, cls) || Eq(a.WmClass, instance))
                    ?? apps.FirstOrDefault(a => Eq(Path.GetFileNameWithoutExtension(a.FilePath), cls) || Eq(Path.GetFileNameWithoutExtension(a.FilePath), instance))
                    ?? apps.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a.FilePath).Split('.').Last() is { } last && (Eq(last, cls) || Eq(last, instance)))
                    ?? apps.FirstOrDefault(a => Tokenize(a.Exec).FirstOrDefault() is { } exe && (Eq(Path.GetFileName(exe), instance) || Eq(Path.GetFileName(exe), cls)))
                    ?? apps.FirstOrDefault(a => Eq(a.Name, cls));
        _byClass[key] = found;
        return found;
    }
    private static readonly Dictionary<string, LinuxApp?> _byClass = new();

    private static readonly string[] CommonThemes = { "hicolor", "Papirus", "Adwaita", "breeze", "Yaru", "elementary", "gnome", "oxygen" };

    private static string[] IconBases()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(AppPaths.DataHome, "icons"), Path.Combine(home, ".icons"), "/usr/share/icons", "/usr/local/share/icons",
            Path.Combine(AppPaths.DataHome, "flatpak/exports/share/icons"), "/var/lib/flatpak/exports/share/icons",
        };
    }

    /// <summary>
    /// A PNG for an icon name, closest to <paramref name="size"/>: from the icon theme in use first (Crimson Banner's
    /// own by default, with its drawn and adaptive icons), then hicolor and the common themes, pixmaps and Flatpak
    /// exports; an SVG-only icon as the PNG <see cref="AdaptiveIcons"/> has drawn of it; or the path itself when
    /// Icon= is an absolute PNG.
    /// </summary>
    public static string? IconPath(string? icon, int size = 48, string contexts = "apps")
    {
        if (string.IsNullOrEmpty(icon)) return null;
        if (Path.IsPathRooted(icon)) return icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) ? icon : null;
        var cacheKey = icon + "|" + contexts + "|" + size;
        if (_iconCache.TryGetValue(cacheKey, out var cached)) return cached;
        var themes = CommonThemes.ToList();
        if (LookAndFeel.EffectiveIconTheme(SettingsStore.Current) is { } chosen) { themes.Remove(chosen); themes.Insert(0, chosen); }
        var found = FindPng(icon, size, contexts, themes)
                    ?? (File.Exists($"/usr/share/pixmaps/{icon}.png") ? $"/usr/share/pixmaps/{icon}.png" : null)
                    ?? (File.Exists(AdaptiveIcons.SvgPicture(icon)) ? AdaptiveIcons.SvgPicture(icon) : null);
        _iconCache[cacheKey] = found;
        return found;
    }

    private static string? FindPng(string icon, int size, string contexts, IEnumerable<string> themes)
    {
        int[] sizes = { size, 64, 48, 128, 32, 96, 256, 24, 512 };
        var bases = IconBases();
        foreach (var t in themes)
            foreach (var sz in sizes)
                foreach (var b in bases)
                    foreach (var ctx in contexts.Split(','))
                    {
                        var p = Path.Combine(b, t, $"{sz}x{sz}", ctx, icon + ".png");
                        if (File.Exists(p)) return p;
                    }
        return null;
    }

    /// <summary>
    /// A program's own app icon, leaving out Crimson Banner's theme (what <see cref="AdaptiveIcons"/> sets on the tile):
    /// a large PNG, else an SVG, else a smaller PNG; null when it has none in the usual places.
    /// </summary>
    public static string? OriginalIconPath(string icon)
    {
        var themes = CommonThemes.ToList();
        var bases = IconBases();
        foreach (var t in themes)
            foreach (var sz in new[] { 256, 128, 512 })
                foreach (var b in bases)
                {
                    var p = Path.Combine(b, t, $"{sz}x{sz}", "apps", icon + ".png");
                    if (File.Exists(p)) return p;
                }
        foreach (var t in themes)
            foreach (var b in bases)
                foreach (var dir in new[] { "scalable/apps", "256x256/apps", "128x128/apps", "64x64/apps", "48x48/apps" })
                {
                    var p = Path.Combine(b, t, dir, icon + ".svg");
                    if (File.Exists(p)) return p;
                }
        foreach (var p in new[] { $"/usr/share/pixmaps/{icon}.svg", $"/usr/share/pixmaps/{icon}.png" })
            if (File.Exists(p)) return p;
        return FindPng(icon, 96, "apps", themes);
    }

    /// <summary>Forgets the icons found so far (a theme was chosen, or new icons were drawn).</summary>
    public static void ForgetIcons() => _iconCache.Clear();
    private static readonly Dictionary<string, string?> _iconCache = new();

    /// <summary>
    /// The installed Linux distribution's own logo (os-release LOGO=, then "distributor-logo"), as a PNG path from
    /// the icon themes or /usr/share/pixmaps; null when the distribution ships none as PNG.
    /// </summary>
    public static string? DistroLogoPath()
    {
        var names = new List<string>();
        foreach (var f in new[] { "/etc/os-release", "/usr/lib/os-release" })
            try
            {
                if (!File.Exists(f)) continue;
                var line = File.ReadLines(f).FirstOrDefault(l => l.StartsWith("LOGO="));
                if (line != null) names.Add(line[5..].Trim('"', '\''));
                break;
            }
            catch { }
        names.Add("distributor-logo");
        foreach (var n in names.Where(n => n.Length > 0))
        {
            if (IconPath(n, 64, "apps,places,status,devices") is { } p) return p;
            foreach (var pix in new[] { $"/usr/share/pixmaps/{n}.png", $"/usr/share/pixmaps/{n}-icon.png" })
                if (File.Exists(pix)) return pix;
        }
        return null;
    }

    // ───────────── file types (shared-mime-info globs) and "Open with" ─────────────
    private static Dictionary<string, string>? _globs;

    /// <summary>The MIME type of a file from its name (the system's shared MIME database: /usr/share/mime/globs2).</summary>
    public static string MimeFor(string path)
    {
        if (Directory.Exists(path)) return "inode/directory";
        if (_globs == null)
        {
            _globs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { Path.Combine(AppPaths.DataHome, "mime"), "/usr/local/share/mime", "/usr/share/mime" })
            {
                var f = Path.Combine(dir, "globs2");
                if (!File.Exists(f)) continue;
                try
                {
                    // "weight:type:glob" — keep the heaviest type for every simple "*.ext"
                    var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in File.ReadLines(f))
                    {
                        if (line.StartsWith('#')) continue;
                        var parts = line.Split(':');
                        if (parts.Length < 3 || !parts[2].StartsWith("*.") || parts[2].IndexOfAny(new[] { '*', '[', '?' }, 2) >= 0) continue;
                        var ext = parts[2][1..];
                        int w = int.TryParse(parts[0], out var ww) ? ww : 50;
                        if (!weights.TryGetValue(ext, out var old) || w > old) { weights[ext] = w; _globs[ext] = parts[1]; }
                    }
                }
                catch { }
            }
        }
        var name = Path.GetFileName(path).ToLowerInvariant();
        // longest matching extension first (".tar.gz" before ".gz")
        for (int i = name.IndexOf('.'); i >= 0 && i < name.Length - 1; i = name.IndexOf('.', i + 1))
            if (_globs.TryGetValue(name[i..], out var m)) return m;
        return "application/octet-stream";
    }

    /// <summary>Installed apps that say they open this kind of file (their MimeType=), the best matches first.</summary>
    public static List<LinuxApp> AppsFor(string path)
    {
        var mime = MimeFor(path);
        var group = mime.Split('/')[0] + "/";
        bool Lists(LinuxApp a, string m) => a.MimeTypes.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(x => x.Equals(m, StringComparison.OrdinalIgnoreCase));
        var exact = All().Where(a => Lists(a, mime)).ToList();
        // text files also open in anything that edits plain text
        var text = mime.StartsWith("text/") || mime is "application/json" or "application/xml" or "application/x-shellscript"
            ? All().Where(a => !exact.Contains(a) && Lists(a, "text/plain")).ToList() : new List<LinuxApp>();
        return exact.Concat(text).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Starts <paramref name="app"/> with the given files (filling %f %F %u %U, or appending them).</summary>
    public static void LaunchWith(LinuxApp app, IReadOnlyList<string> files)
    {
        var tokens = Tokenize(app.Exec);
        if (tokens.Count == 0) return;
        var args = new List<string>();
        bool placed = false;
        foreach (var t in tokens.Skip(1))
        {
            switch (t)
            {
                case "%f": case "%u": if (files.Count > 0) args.Add(files[0]); placed = true; break;
                case "%F": case "%U": args.AddRange(files); placed = true; break;
                default:
                    if (t.Length == 2 && t[0] == '%') continue;               // %i %c %k …
                    args.Add(t.Replace("%%", "%"));
                    break;
            }
        }
        if (!placed) args.AddRange(files);
        if (app.Terminal) ProcessUtil.RunInTerminal(tokens[0], args);
        else ProcessUtil.StartDetached(tokens[0], args, Path.GetDirectoryName(files.FirstOrDefault() ?? AppPaths.Home) ?? AppPaths.Home);
        // a %f-only app opens one file per start
        if (files.Count > 1 && tokens.Skip(1).Any(t => t is "%f" or "%u") && !tokens.Skip(1).Any(t => t is "%F" or "%U"))
            foreach (var more in files.Skip(1)) LaunchWith(app, new[] { more });
    }

    public static List<string> Tokenize(string exec)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false, have = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (c == '\\' && inQuote && i + 1 < exec.Length) { sb.Append(exec[++i]); have = true; continue; }
            if (c == '"') { inQuote = !inQuote; have = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (have) { tokens.Add(sb.ToString()); sb.Clear(); have = false; }
                continue;
            }
            sb.Append(c);
            have = true;
        }
        if (have) tokens.Add(sb.ToString());
        return tokens;
    }

    public static void Launch(LinuxApp app)
    {
        var tokens = Tokenize(app.Exec)
            .Where(t => !(t.Length == 2 && t[0] == '%'))   // drop field codes such as %U %f
            .Select(t => t.Replace("%%", "%"))
            .ToList();
        if (tokens.Count == 0) return;
        var file = tokens[0];
        var args = tokens.Skip(1).ToList();
        if (app.Terminal) ProcessUtil.RunInTerminal(file, args);
        else ProcessUtil.StartDetached(file, args, AppPaths.Home);
    }
}
