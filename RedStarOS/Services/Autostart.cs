// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RedStarOS.Services;

/// <summary>One program that starts at sign-in (a .desktop file in an autostart folder).</summary>
public sealed record AutostartEntry(string FileName, string Name, string Exec, string? TryExec, string? Icon, string? Comment,
    bool Hidden, bool Terminal, IReadOnlyList<string> OnlyShowIn, IReadOnlyList<string> NotShowIn, string Path, bool UserFile, bool HasSystemFile)
{
    /// <summary>Whether it runs on this desktop: not hidden or switched off, and meant for it.</summary>
    public bool RunsHere(IReadOnlyCollection<string> desktops) =>
        !Hidden && (OnlyShowIn.Count == 0 || OnlyShowIn.Any(d => desktops.Contains(d, StringComparer.OrdinalIgnoreCase)))
        && !NotShowIn.Any(d => desktops.Contains(d, StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Programs that start at sign-in, the freedesktop.org way (Desktop Application Autostart Specification): .desktop
/// files in ~/.config/autostart and in each $XDG_CONFIG_DIRS/autostart (/etc/xdg/autostart), the user's file of a name
/// replacing the system's. Turning one off writes the user's copy with Hidden=true, as the specification says, so the
/// system's file isn't touched. Run when Crimson Banner OS is the session; on top of another desktop, that desktop
/// has already started them.
/// </summary>
public static class Autostart
{
    /// <summary>The desktop names OnlyShowIn/NotShowIn are compared with.</summary>
    public static IReadOnlyCollection<string> Desktops()
    {
        var names = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var n in new[] { "CrimsonBanner", "RedStar" }) if (!names.Contains(n)) names.Add(n);
        return names;
    }

    public static string UserDir =>
        System.IO.Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart");

    public static IEnumerable<string> SystemDirs()
    {
        var dirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
        foreach (var d in (string.IsNullOrEmpty(dirs) ? "/etc/xdg" : dirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return System.IO.Path.Combine(d, "autostart");
    }

    /// <summary>Everything in the autostart folders, the user's file of a name winning over the system's.</summary>
    public static List<AutostartEntry> All(string? userDir = null, IEnumerable<string>? systemDirs = null)
    {
        userDir ??= UserDir;
        var system = new Dictionary<string, string>();
        foreach (var dir in (systemDirs ?? SystemDirs()).Reverse())          // earlier XDG_CONFIG_DIRS win
            foreach (var f in Files(dir)) system[System.IO.Path.GetFileName(f)] = f;
        var user = Files(userDir).ToDictionary(f => System.IO.Path.GetFileName(f), f => f);
        var list = new List<AutostartEntry>();
        foreach (var name in system.Keys.Union(user.Keys))
        {
            bool isUser = user.TryGetValue(name, out var path);
            var e = Parse(isUser ? path! : system[name], name, isUser, system.ContainsKey(name));
            // a user's file that only says Hidden=true: shows the system's entry, switched off
            if (e != null && isUser && e.Exec.Length == 0 && system.TryGetValue(name, out var sys) && Parse(sys, name, false, true) is { } s)
                e = s with { Hidden = e.Hidden, Path = path!, UserFile = true };
            if (e != null && e.Exec.Length > 0) list.Add(e);
        }
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> Files(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.desktop") : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public static AutostartEntry? Parse(string path, string fileName, bool userFile, bool hasSystem)
    {
        try
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(fileName), exec = "";
            string? tryExec = null, icon = null, comment = null;
            bool hidden = false, terminal = false, inEntry = false;
            var only = new List<string>(); var not = new List<string>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inEntry = line == "[Desktop Entry]"; continue; }
                if (!inEntry || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim(); var val = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "Name": name = val; break;
                    case "Exec": exec = val; break;
                    case "TryExec": tryExec = val; break;
                    case "Icon": icon = val; break;
                    case "Comment": comment = val; break;
                    case "Terminal": terminal = val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "Hidden": hidden |= val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "X-GNOME-Autostart-enabled": hidden |= val.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                    case "OnlyShowIn": only.AddRange(val.Split(';', StringSplitOptions.RemoveEmptyEntries)); break;
                    case "NotShowIn": not.AddRange(val.Split(';', StringSplitOptions.RemoveEmptyEntries)); break;
                }
            }
            return new AutostartEntry(fileName, name, exec, tryExec, icon, comment, hidden, terminal, only, not, path, userFile, hasSystem);
        }
        catch { return null; }
    }

    /// <summary>Whether Crimson Banner starts them: only when it is the session.</summary>
    public static bool IsSession =>
        Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1" || InputIsolation.InRedStarComp && !InputIsolation.IsNested;

    /// <summary>Starts what should run here (once, at sign-in). Returns their names.</summary>
    public static List<string> RunAll()
    {
        var started = new List<string>();
        var desktops = Desktops();
        foreach (var e in All())
        {
            if (!e.RunsHere(desktops)) continue;
            if (e.TryExec is { Length: > 0 } t && (t.Contains('/') ? !File.Exists(t) : ProcessUtil.Which(t) == null)) continue;
            var tokens = DesktopEntries.Tokenize(e.Exec).Where(x => !(x.Length == 2 && x[0] == '%')).Select(x => x.Replace("%%", "%")).ToList();
            if (tokens.Count == 0) continue;
            // never Crimson Banner itself
            var exe = System.IO.Path.GetFileName(tokens[0]);
            if (exe.StartsWith("RedStarOS", StringComparison.OrdinalIgnoreCase) || exe.StartsWith("redstar", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (e.Terminal) ProcessUtil.RunInTerminal(tokens[0], tokens.Skip(1).ToList());
                else ProcessUtil.StartDetached(tokens[0], tokens.Skip(1).ToList(), AppPaths.Home);
                started.Add(e.Name);
            }
            catch (Exception ex) { Console.Error.WriteLine($"autostart: {e.Name}: {ex.Message}"); }
        }
        return started;
    }

    /// <summary>Turns an entry on or off for this user.</summary>
    public static void SetEnabled(AutostartEntry e, bool on, string? userDir = null)
    {
        userDir ??= UserDir;
        Directory.CreateDirectory(userDir);
        var mine = System.IO.Path.Combine(userDir, e.FileName);
        if (on && e.HasSystemFile && e.UserFile && IsOnlyAnOverride(mine)) { File.Delete(mine); return; }   // back to the system's
        if (!e.UserFile) File.Copy(e.Path, mine, true);
        var lines = File.ReadAllLines(mine).ToList();
        lines.RemoveAll(l => l.StartsWith("Hidden=") || l.StartsWith("X-GNOME-Autostart-enabled="));
        int at = lines.FindIndex(l => l.Trim() == "[Desktop Entry]");
        if (at < 0) { lines.Insert(0, "[Desktop Entry]"); at = 0; }
        lines.Insert(at + 1, on ? "Hidden=false" : "Hidden=true");
        File.WriteAllText(mine, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }

    // a user's copy we made only to switch a system entry off
    private static bool IsOnlyAnOverride(string path)
    {
        try { return File.ReadLines(path).Any(l => l.Trim() == "Hidden=true"); } catch { return false; }
    }

    /// <summary>Adds an installed program to start at sign-in.</summary>
    public static void Add(LinuxApp app, string? userDir = null)
    {
        userDir ??= UserDir;
        Directory.CreateDirectory(userDir);
        var to = System.IO.Path.Combine(userDir, System.IO.Path.GetFileName(app.FilePath));
        File.Copy(app.FilePath, to, true);
    }

    /// <summary>Removes the user's own entry (one with no system file behind it).</summary>
    public static void Remove(AutostartEntry e)
    {
        if (e.UserFile && !e.HasSystemFile) File.Delete(e.Path);
    }
}
