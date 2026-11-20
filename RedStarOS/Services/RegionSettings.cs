// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Keyboard layouts, the time zone and clock, and the system language (System Settings → Date &amp; time, Region &amp;
/// keyboard). Layouts are Crimson Banner's own setting, applied at once (redstar-comp's "keymap", setxkbmap under X11)
/// and from the next sign-in through XKB_DEFAULT_*; the time zone, network time and language are the system's, changed
/// through systemd's timedatectl and localectl, which ask polkit (and so the administrator password prompt).
/// </summary>
public static class RegionSettings
{
    /// <summary>A layout and its variant, as XKB names them ("de" and "nodeadkeys"; variant "" for the basic one).</summary>
    public readonly record struct Layout(string Code, string Variant)
    {
        public override string ToString() => Variant.Length > 0 ? Code + ":" + Variant : Code;
        public static Layout Parse(string s) { var i = s.IndexOf(':'); return i < 0 ? new(s, "") : new(s[..i], s[(i + 1)..]); }
    }

    /// <summary>The XKB names of the chosen layouts: layouts, variants and options, comma-separated.</summary>
    public static (string Layouts, string Variants, string Options) Xkb(Settings s)
    {
        var layouts = s.KeyboardLayouts.Select(Layout.Parse).Where(l => l.Code.Length > 0).ToList();
        var opts = new List<string>();
        if (layouts.Count > 1 && s.KeyboardSwitch.Length > 0) opts.Add(s.KeyboardSwitch);
        if (s.KeyboardCompose.Length > 0) opts.Add(s.KeyboardCompose);
        if (s.KeyboardCapsLock.Length > 0) opts.Add(s.KeyboardCapsLock);
        return (string.Join(',', layouts.Select(l => l.Code)), layouts.Any(l => l.Variant.Length > 0) ? string.Join(',', layouts.Select(l => l.Variant)) : "",
                string.Join(',', opts));
    }

    /// <summary>The layouts, at once.</summary>
    public static void ApplyKeyboard()
    {
        var (l, v, o) = Xkb(SettingsStore.Current);
        if (l.Length == 0) return;                              // the system's own layout stays
        Environment.SetEnvironmentVariable("XKB_DEFAULT_LAYOUT", l);
        Environment.SetEnvironmentVariable("XKB_DEFAULT_VARIANT", v.Length > 0 ? v : null);
        Environment.SetEnvironmentVariable("XKB_DEFAULT_OPTIONS", o.Length > 0 ? o : null);
        if (InputIsolation.InRedStarComp) CompIpc.Send($"keymap\t{l}\t{v}\t{o}");
        else if (Environment.GetEnvironmentVariable("DISPLAY") != null && ProcessUtil.Which("setxkbmap") is { } sx)
        {
            var args = new List<string> { "-layout", l, "-variant", v, "-option", "" };
            if (o.Length > 0) { args.Add("-option"); args.Add(o); }
            _ = ProcessUtil.RunAsync(sx, args, 5000);
        }
    }

    // ── what XKB offers ──

    public sealed record XkbLayout(string Code, string Name, IReadOnlyList<(string Variant, string Name)> Variants);

    private static List<XkbLayout>? _layouts;

    /// <summary>Layouts and their variants from XKB's rules list (evdev.lst), sorted by name.</summary>
    public static List<XkbLayout> Layouts(string? lst = null)
    {
        if (lst == null && _layouts != null) return _layouts;
        var path = lst ?? new[] { "/usr/share/X11/xkb/rules/evdev.lst", "/usr/share/X11/xkb/rules/base.lst" }.FirstOrDefault(File.Exists);
        var names = new Dictionary<string, string>();
        var variants = new Dictionary<string, List<(string, string)>>();
        if (path != null)
        {
            string section = "";
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('!')) { section = line[1..].Trim(); continue; }
                if (line.Length == 0) continue;
                int sp = line.IndexOfAny(new[] { ' ', '\t' });
                if (sp < 0) continue;
                var key = line[..sp]; var rest = line[sp..].Trim();
                if (section == "layout") names[key] = rest;
                else if (section == "variant")
                {
                    int colon = rest.IndexOf(':');
                    if (colon < 0) continue;
                    var layout = rest[..colon].Trim();
                    (variants.TryGetValue(layout, out var list) ? list : variants[layout] = new()).Add((key, rest[(colon + 1)..].Trim()));
                }
            }
        }
        var result = names.Select(n => new XkbLayout(n.Key, n.Value, variants.TryGetValue(n.Key, out var v) ? v : new List<(string, string)>()))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (lst == null) _layouts = result;
        return result;
    }

    /// <summary>"German (no dead keys)" for a chosen layout.</summary>
    public static string Describe(Layout l)
    {
        var x = Layouts().FirstOrDefault(k => k.Code == l.Code);
        if (x == null) return l.ToString();
        if (l.Variant.Length == 0) return x.Name;
        return x.Variants.FirstOrDefault(v => v.Variant == l.Variant).Name ?? $"{x.Name} ({l.Variant})";
    }

    // ── the system's clock and language ──

    public sealed record TimeInfo(string Timezone, bool Ntp, bool Synchronized);

    public static async Task<TimeInfo?> TimeAsync()
    {
        if (ProcessUtil.Which("timedatectl") is not { } td) return null;
        var (code, text) = await ProcessUtil.RunAsync(td, new[] { "show" }, 5000);
        if (code != 0) return null;
        var kv = text.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        return new TimeInfo(kv.GetValueOrDefault("Timezone", ""), kv.GetValueOrDefault("NTP") == "yes", kv.GetValueOrDefault("NTPSynchronized") == "yes");
    }

    public static async Task<List<string>> TimezonesAsync()
    {
        if (ProcessUtil.Which("timedatectl") is not { } td) return new();
        var (code, text) = await ProcessUtil.RunAsync(td, new[] { "list-timezones" }, 10000);
        return code == 0 ? text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : new();
    }

    /// <summary>Runs timedatectl or localectl; null when it worked, else what it said. They ask polkit themselves.</summary>
    public static async Task<string?> RunAsync(string tool, params string[] args)
    {
        if (ProcessUtil.Which(tool) is not { } exe) return tool + " isn't installed (it comes with systemd)";
        // up to two minutes: polkit may ask for the administrator password
        var (code, text) = await ProcessUtil.RunAsync(exe, args, 120000);
        return code == 0 ? null : (text.Trim().Length > 0 ? text.Trim() : $"{tool} failed");
    }

    public static async Task<(string Current, List<string> All)> LocalesAsync()
    {
        if (ProcessUtil.Which("localectl") is not { } lc) return ("", new());
        var (_, status) = await ProcessUtil.RunAsync(lc, new[] { "status" }, 5000);
        var current = status.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("System Locale:"))?.Split("LANG=", 2).ElementAtOrDefault(1)?.Trim() ?? "";
        var (code, list) = await ProcessUtil.RunAsync(lc, new[] { "list-locales" }, 10000);
        return (current, code == 0 ? list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : new());
    }
}
