// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace RedStarOS.Services.Greeter;

/// <summary>Someone who can sign in.</summary>
public sealed record LoginUser(string Name, string RealName, int Uid, string Home)
{
    public string Shown => RealName.Length > 0 ? RealName : Name;
    /// <summary>Their picture: AccountsService's copy (readable by the login screen), else none.</summary>
    public string? Avatar(string accountsIcons = "/var/lib/AccountsService/icons") =>
        File.Exists(Path.Combine(accountsIcons, Name)) ? Path.Combine(accountsIcons, Name) : null;
}

/// <summary>A desktop that can be started (a .desktop file in wayland-sessions or xsessions).</summary>
public sealed record LoginSession(string Id, string Name, bool Wayland, IReadOnlyList<string> Exec, IReadOnlyList<string> DesktopNames)
{
    public string Shown => Name + (Wayland ? "" : " (X11)");
}

/// <summary>
/// What the login screen shows and starts: the people who can sign in (/etc/passwd), the desktops installed
/// (/usr/share/wayland-sessions and xsessions), the command and environment greetd starts for a choice, and what it
/// remembers between starts (the last person, and each person's last desktop).
/// </summary>
public static class GreeterData
{
    // ── people ──

    private static readonly string[] NoLoginShells = { "nologin", "false", "sync", "shutdown", "halt", "git-shell" };

    /// <summary>/etc/passwd's ordinary accounts (UID_MIN to UID_MAX from login.defs), ordered by name.</summary>
    public static List<LoginUser> Users(string passwd, int uidMin = 1000, int uidMax = 60000) =>
        passwd.Split('\n')
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 7 && int.TryParse(f[2], out _))
            .Select(f => (f, uid: int.Parse(f[2])))
            .Where(x => x.uid >= uidMin && x.uid <= uidMax)
            .Where(x => !NoLoginShells.Contains(Path.GetFileName(x.f[6].Trim())) && x.f[6].Trim().Length > 0)
            .Where(x => x.f[0] != "greeter" && x.f[0] != "nobody")
            .Select(x => new LoginUser(x.f[0], x.f[4].Split(',')[0].Trim(), x.uid, x.f[5]))
            .GroupBy(u => u.Name).Select(g => g.First())
            .OrderBy(u => u.Shown, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static (int Min, int Max) UidRange(string? loginDefs)
    {
        int min = 1000, max = 60000;
        foreach (var raw in (loginDefs ?? "").Split('\n'))
        {
            var p = raw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2 || p[0].StartsWith('#')) continue;
            if (p[0] == "UID_MIN" && int.TryParse(p[1], out var a)) min = a;
            if (p[0] == "UID_MAX" && int.TryParse(p[1], out var b)) max = b;
        }
        return (min, max);
    }

    public static List<LoginUser> SystemUsers()
    {
        string? defs = null;
        try { defs = File.ReadAllText("/etc/login.defs"); } catch { }
        var (min, max) = UidRange(defs);
        try { return Users(File.ReadAllText("/etc/passwd"), min, max); } catch { return new(); }
    }

    // ── desktops ──

    public static readonly string[] WaylandDirs = { "/usr/share/wayland-sessions", "/usr/local/share/wayland-sessions" };
    public static readonly string[] X11Dirs = { "/usr/share/xsessions", "/usr/local/share/xsessions" };

    public static List<LoginSession> Sessions(IEnumerable<string> waylandDirs, IEnumerable<string> x11Dirs, Func<string, string?> which)
    {
        var list = new List<LoginSession>();
        var seen = new HashSet<string>();
        void Read(IEnumerable<string> dirs, bool wayland)
        {
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.desktop").OrderBy(x => x, StringComparer.Ordinal))
                {
                    var id = Path.GetFileNameWithoutExtension(f);
                    if (!seen.Add((wayland ? "w:" : "x:") + id)) continue;
                    if (Parse(id, File.ReadAllText(f), wayland, which) is { } s) list.Add(s);
                }
            }
        }
        Read(waylandDirs, true);
        // X11 desktops need startx (xorg-xinit) to bring up the X server under greetd
        if (which("startx") != null) Read(x11Dirs, false);
        return list;
    }

    public static LoginSession? Parse(string id, string desktopFile, bool wayland, Func<string, string?> which)
    {
        var d = new Dictionary<string, string>();
        bool inEntry = false;
        foreach (var raw in desktopFile.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith('[')) { inEntry = l == "[Desktop Entry]"; continue; }
            if (!inEntry || l.StartsWith('#')) continue;
            var eq = l.IndexOf('=');
            if (eq <= 0 || l[..eq].Contains('[')) continue;
            d.TryAdd(l[..eq].Trim(), l[(eq + 1)..].Trim());
        }
        if (d.GetValueOrDefault("Hidden") == "true" || d.GetValueOrDefault("NoDisplay") == "true") return null;
        if (!d.TryGetValue("Name", out var name) || !d.TryGetValue("Exec", out var exec)) return null;
        var argv = SplitExec(exec);
        if (argv.Count == 0) return null;
        if (d.TryGetValue("TryExec", out var tryExec) && tryExec.Length > 0 && !Exists(tryExec, which)) return null;
        if (!Exists(argv[0], which)) return null;
        var names = (d.GetValueOrDefault("DesktopNames") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new LoginSession(id, name, wayland, argv, names);
    }

    private static bool Exists(string program, Func<string, string?> which) =>
        program.StartsWith('/') ? File.Exists(program) : which(program) != null;

    /// <summary>A .desktop Exec line as arguments: double quotes group, backslash escapes, field codes (%f, %U…) go.</summary>
    public static List<string> SplitExec(string exec)
    {
        var args = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < exec.Length) { cur.Append(exec[++i]); continue; }
                if (c == '"') { quoted = false; continue; }
                cur.Append(c);
            }
            else if (c == '"') { quoted = true; any = true; }
            else if (c == ' ' || c == '\t') { if (cur.Length > 0 || any) { args.Add(cur.ToString()); cur.Clear(); any = false; } }
            else if (c == '%' && i + 1 < exec.Length) { if (exec[++i] == '%') cur.Append('%'); }
            else cur.Append(c);
        }
        if (cur.Length > 0 || any) args.Add(cur.ToString());
        return args;
    }

    /// <summary>What greetd starts for a desktop: Wayland ones as they are; X11 ones through startx, which brings up
    /// the X server on this console first. Plus the variables desktops look at to know what they are.</summary>
    public static (List<string> Cmd, List<string> Env) Command(LoginSession s)
    {
        var env = new List<string>
        {
            "XDG_SESSION_TYPE=" + (s.Wayland ? "wayland" : "x11"),
            "XDG_SESSION_DESKTOP=" + s.Id,
            "DESKTOP_SESSION=" + s.Id,
        };
        if (s.DesktopNames.Count > 0) env.Add("XDG_CURRENT_DESKTOP=" + string.Join(':', s.DesktopNames));
        var cmd = s.Wayland ? s.Exec.ToList() : new List<string> { "startx", "/usr/bin/env" }.Concat(s.Exec).ToList();
        return (cmd, env);
    }

    /// <summary>The desktop to offer first: this person's last one, else Crimson Banner OS, else the first.</summary>
    public static LoginSession? Preferred(IReadOnlyList<LoginSession> sessions, string? last) =>
        sessions.FirstOrDefault(s => s.Id == last)
        ?? sessions.FirstOrDefault(s => s.Id == "redstaros")
        ?? sessions.FirstOrDefault(s => s.Id.StartsWith("redstaros", StringComparison.Ordinal))
        ?? sessions.FirstOrDefault();

    // ── what it remembers ──

    public sealed class State
    {
        public string? LastUser { get; set; }
        public Dictionary<string, string> Sessions { get; set; } = new();
    }

    /// <summary>/var/cache/crimson-greeter/state.json (the login screen's own folder; packaging/greeter), or
    /// $CRIMSON_GREETER_STATE.</summary>
    public static string StatePath => Environment.GetEnvironmentVariable("CRIMSON_GREETER_STATE") is { Length: > 0 } p ? p : "/var/cache/crimson-greeter/state.json";

    public static State Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Save(string path, State s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, path, true);
        }
        catch { /* not remembering is fine */ }
    }
}
