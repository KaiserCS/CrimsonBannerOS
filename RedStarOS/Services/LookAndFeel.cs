// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace RedStarOS.Services;

/// <summary>
/// System Settings → Appearance → fonts, pointer, icons and the look of other programs. The shell's own font is applied
/// at once; the pointer at once inside redstar-comp (otherwise for programs started from now on, and everything at next
/// sign-in); GTK programs read ~/.config/gtk-3.0 and gtk-4.0/settings.ini and GNOME's settings; Qt programs are told to
/// follow GTK (Qt's own gtk3 platform theme) or qt6ct. Writing other programs' settings only happens when "Style other
/// programs" is on: by default in a Red Star session, not when Red Star runs on top of another desktop, whose own
/// settings those are.
/// </summary>
public static class LookAndFeel
{
    public const double DefaultFontSize = 14;
    public const int DefaultCursorSize = 24;

    // ───────────── what's installed ─────────────

    private static IEnumerable<string> DataDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dh ? dh : Path.Combine(home, ".local", "share");
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var d in (string.IsNullOrEmpty(dirs) ? "/usr/local/share:/usr/share" : dirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return d;
    }

    private static IEnumerable<string> IconRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".icons");
        foreach (var d in DataDirs()) yield return Path.Combine(d, "icons");
        yield return "/usr/share/pixmaps";
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Pointer themes: folders with a cursors/ directory.</summary>
    public static List<string> CursorThemes() =>
        IconRoots().SelectMany(SafeDirs).Where(d => Directory.Exists(Path.Combine(d, "cursors")))
            .Select(Path.GetFileName).OfType<string>().Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Crimson Banner's own icon theme (icons/CrimsonBanner, branding/icon-theme.py).</summary>
    public const string OurIconTheme = "CrimsonBanner";

    /// <summary>The installed system copy of an icon theme (not the one in your home folder), or null.</summary>
    public static string? SystemThemeDir(string name)
    {
        foreach (var root in IconRoots().Skip(2))      // ~/.icons and ~/.local/share/icons come first
        {
            var d = Path.Combine(root, name);
            if (File.Exists(Path.Combine(d, "index.theme"))) return d;
        }
        return null;
    }

    /// <summary>
    /// The icon theme in use: the one chosen in Settings, else Crimson Banner's own when it's installed (it is with
    /// the system), else none (the programs' own choice).
    /// </summary>
    public static string? EffectiveIconTheme(Settings s) =>
        !string.IsNullOrEmpty(s.IconTheme) ? s.IconTheme : (_ourThemeInstalled ??= SystemThemeDir(OurIconTheme) != null) ? OurIconTheme : null;
    private static bool? _ourThemeInstalled;

    /// <summary>Icon themes: an index.theme that lists icon directories (pointer-only themes have none), not hidden.</summary>
    public static List<string> IconThemes()
    {
        var list = new List<string>();
        foreach (var d in IconRoots().SelectMany(SafeDirs))
        {
            var index = Path.Combine(d, "index.theme");
            if (!File.Exists(index)) continue;
            try
            {
                var text = File.ReadAllText(index);
                if (!text.Contains("Directories=") || text.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(d) is { } n && n != "default") list.Add(n);
            }
            catch { }
        }
        return list.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>GTK themes: Adwaita (built into GTK) and any theme folder with a gtk-3.0 or gtk-4.0 directory.</summary>
    public static List<string> GtkThemes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Path.Combine(home, ".themes") }.Concat(DataDirs().Select(d => Path.Combine(d, "themes")));
        var list = new List<string> { "Adwaita", "HighContrast" };
        foreach (var d in roots.SelectMany(SafeDirs))
            if (Directory.Exists(Path.Combine(d, "gtk-3.0")) || Directory.Exists(Path.Combine(d, "gtk-4.0")))
                if (Path.GetFileName(d) is { } n) list.Add(n);
        return list.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> Fonts()
    {
        try { return FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return new List<string>(); }
    }

    public static bool Qt6ctInstalled => ProcessUtil.Which("qt6ct") != null;

    // ───────────── effective values ─────────────

    /// <summary>The pointer size: the chosen one, at least 48 with Accessibility → Large mouse pointer.</summary>
    public static int CursorSize()
    {
        var s = SettingsStore.Current;
        var size = s.CursorSize is >= 12 and <= 256 ? s.CursorSize : DefaultCursorSize;
        return s.LargeCursor ? Math.Max(48, size) : size;
    }

    /// <summary>Whether Red Star writes GTK's and Qt's settings: the user's choice, else only in a Red Star session.</summary>
    public static bool StylesOtherPrograms =>
        SettingsStore.Current.StyleOtherApps ?? (Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1" || InputIsolation.InRedStarComp && !InputIsolation.IsNested);

    /// <summary>For programs started from now on: pointer theme and size, and how Qt programs pick their look.</summary>
    public static void ApplyEnvironment()
    {
        var s = SettingsStore.Current;
        if (!string.IsNullOrEmpty(s.CursorTheme)) Environment.SetEnvironmentVariable("XCURSOR_THEME", s.CursorTheme);
        Environment.SetEnvironmentVariable("XCURSOR_SIZE", CursorSize().ToString(CultureInfo.InvariantCulture));
        if (!StylesOtherPrograms) return;
        switch (s.QtStyle)
        {
            case "gtk": Environment.SetEnvironmentVariable("QT_QPA_PLATFORMTHEME", "gtk3"); break;
            case "qt6ct" when Qt6ctInstalled: Environment.SetEnvironmentVariable("QT_QPA_PLATFORMTHEME", "qt6ct"); break;
        }
    }

    // ───────────── applying ─────────────

    private static Style? _fontStyle;

    /// <summary>The shell's own font and text size, at once in every window.</summary>
    public static void ApplyShellFont()
    {
        var app = Application.Current;
        if (app == null) return;
        var s = SettingsStore.Current;
        if (_fontStyle != null) app.Styles.Remove(_fontStyle);
        _fontStyle = null;
        var size = s.UiFontSize is >= 9 and <= 28 ? s.UiFontSize : DefaultFontSize;
        if (string.IsNullOrEmpty(s.UiFont)) app.Resources.Remove("ContentControlThemeFontFamily");
        else app.Resources["ContentControlThemeFontFamily"] = new FontFamily(s.UiFont);
        app.Resources["ControlContentThemeFontSize"] = size;
        if (string.IsNullOrEmpty(s.UiFont) && Math.Abs(size - DefaultFontSize) < 0.01) return;
        var style = new Style(x => x.Is<TopLevel>());
        if (!string.IsNullOrEmpty(s.UiFont)) style.Setters.Add(new Setter(TemplatedControl.FontFamilyProperty, new FontFamily(s.UiFont)));
        style.Setters.Add(new Setter(TemplatedControl.FontSizeProperty, size));
        _fontStyle = style;
        app.Styles.Add(style);
    }

    /// <summary>The pointer: redstar-comp redraws it at once; X programs and new programs follow.</summary>
    public static void ApplyPointer()
    {
        var s = SettingsStore.Current;
        var size = CursorSize();
        ApplyEnvironment();
        if (InputIsolation.InRedStarComp) CompIpc.Send($"cursor\t{s.CursorTheme ?? ""}\t{size}");
        var xrdb = ProcessUtil.Which("xrdb");
        if (xrdb != null && Environment.GetEnvironmentVariable("DISPLAY") != null)
        {
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), "redstar-xcursor.ad");
                var text = $"Xcursor.size: {size}\n" + (string.IsNullOrEmpty(s.CursorTheme) ? "" : $"Xcursor.theme: {s.CursorTheme}\n");
                File.WriteAllText(tmp, text);
                _ = ProcessUtil.RunAsync(xrdb, new[] { "-merge", tmp }, 5000);
            }
            catch { }
        }
        ApplyOtherPrograms();
    }

    /// <summary>Everything at once (System Settings and sign-in).</summary>
    public static void ApplyAll()
    {
        ApplyShellFont();
        ApplyPointer();
    }

    /// <summary>GTK's settings.ini (3 and 4), GNOME's interface settings and qt6ct's file, when Red Star styles other
    /// programs.</summary>
    public static void ApplyOtherPrograms()
    {
        if (!StylesOtherPrograms) return;
        var s = SettingsStore.Current;
        var dark = ThemeService.IsDark;
        var keys = GtkKeys(s, dark);
        var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        foreach (var v in new[] { "gtk-3.0", "gtk-4.0" })
        {
            var gtk4 = v == "gtk-4.0";
            // GTK 4 has no gtk-application-prefer-dark-theme for libadwaita apps, but plain GTK 4 still reads it
            try { MergeIni(Path.Combine(cfg, v, "settings.ini"), "Settings", keys.Where(k => !(gtk4 && k.Key == "gtk-theme-name" && k.Value == "HighContrast"))); }
            catch (Exception ex) { Console.Error.WriteLine($"appearance: {v}: {ex.Message}"); }
        }
        if (ProcessUtil.Which("gsettings") is { } gs)
        {
            void Set(string key, string value) => _ = ProcessUtil.RunAsync(gs, new[] { "set", "org.gnome.desktop.interface", key, value }, 5000);
            Set("color-scheme", dark ? "prefer-dark" : "default");
            if (!string.IsNullOrEmpty(s.GtkTheme)) Set("gtk-theme", s.GtkTheme);
            if (EffectiveIconTheme(s) is { } icons) Set("icon-theme", icons);
            if (!string.IsNullOrEmpty(s.CursorTheme)) Set("cursor-theme", s.CursorTheme);
            Set("cursor-size", CursorSize().ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(s.UiFont)) Set("font-name", GtkFont(s));
        }
        if (s.QtStyle == "qt6ct" && Qt6ctInstalled)
        {
            var qt = new List<KeyValuePair<string, string>>();
            if (EffectiveIconTheme(s) is { } icons) qt.Add(new("icon_theme", icons));
            qt.Add(new("style", "Fusion"));
            try { MergeIni(Path.Combine(cfg, "qt6ct", "qt6ct.conf"), "Appearance", qt); } catch { }
        }
    }

    /// <summary>"Family 10.5": GTK's font names are in points, Red Star's sizes in pixels at 96 dpi.</summary>
    public static string GtkFont(Settings s)
    {
        var px = s.UiFontSize is >= 9 and <= 28 ? s.UiFontSize : DefaultFontSize;
        var pt = Math.Round(px * 0.75 * 2) / 2;
        return $"{(string.IsNullOrEmpty(s.UiFont) ? "Sans" : s.UiFont)} {pt.ToString("0.#", CultureInfo.InvariantCulture)}";
    }

    public static List<KeyValuePair<string, string>> GtkKeys(Settings s, bool dark)
    {
        var k = new List<KeyValuePair<string, string>> { new("gtk-application-prefer-dark-theme", dark ? "1" : "0") };
        if (!string.IsNullOrEmpty(s.GtkTheme)) k.Add(new("gtk-theme-name", s.GtkTheme));
        if (EffectiveIconTheme(s) is { } icons) k.Add(new("gtk-icon-theme-name", icons));
        if (!string.IsNullOrEmpty(s.CursorTheme)) k.Add(new("gtk-cursor-theme-name", s.CursorTheme));
        k.Add(new("gtk-cursor-theme-size", CursorSize().ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(s.UiFont)) k.Add(new("gtk-font-name", GtkFont(s)));
        return k;
    }

    /// <summary>Sets keys in one [section] of an ini file, keeping every other line as it was.</summary>
    public static void MergeIni(string path, string section, IEnumerable<KeyValuePair<string, string>> values)
    {
        var want = values.ToDictionary(v => v.Key, v => v.Value);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            start = lines.Count - 1;
        }
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        var done = new HashSet<string>();
        for (int i = start + 1; i < end; i++)
        {
            var eq = lines[i].IndexOf('=');
            if (eq <= 0 || lines[i].TrimStart().StartsWith('#') || lines[i].TrimStart().StartsWith(';')) continue;
            var key = lines[i][..eq].Trim();
            if (want.TryGetValue(key, out var v)) { lines[i] = $"{key}={v}"; done.Add(key); }
        }
        // new keys go after the section's last setting, before any blank lines that separate it from the next section
        int at = end;
        while (at > start + 1 && lines[at - 1].Trim().Length == 0) at--;
        lines.InsertRange(at, want.Where(w => !done.Contains(w.Key)).Select(w => $"{w.Key}={w.Value}"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    }
}
