using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RedStarOS.Services;

/// <summary>A terminal colour scheme: 16 ANSI colours plus foreground / background (0xRRGGBB).</summary>
public sealed record TermScheme(string Name, uint[]? Ansi, uint Fg, uint Bg, uint? Cursor = null)
{
    /// <summary>The scheme that follows the shell's light / dark theme and accent colour.</summary>
    public bool FollowsTheme => Ansi == null;
}

public sealed class TerminalProfile
{
    public string Name { get; set; } = "Default";
    public string FontFamily { get; set; } = "";              // empty = built-in monospace list
    public double FontSize { get; set; } = 14;
    public string Scheme { get; set; } = "Crimson Banner";
    public string CursorShape { get; set; } = "Block";         // Block | Underline | Bar
    public bool CursorBlink { get; set; } = true;
    public string Command { get; set; } = "";                  // empty = the user's shell
    public string StartIn { get; set; } = "Current";           // Current | Home | Custom
    public string CustomDir { get; set; } = "";
    public int Scrollback { get; set; } = 10000;
    public bool VisualBell { get; set; } = true;
    public bool BoldIsBright { get; set; } = true;

    public TerminalProfile Clone(string name)
    {
        var p = (TerminalProfile)MemberwiseClone();
        p.Name = name;
        return p;
    }
}

public sealed class TerminalSettings
{
    public List<TerminalProfile> Profiles { get; set; } = new();
    public string Default { get; set; } = "Default";
    public double Zoom { get; set; }                           // added to every profile's font size
}

/// <summary>Terminal profiles and colour schemes (~/.config/redstaros/terminal.json).</summary>
public static class TerminalProfiles
{
    public const string DefaultFonts = "JetBrains Mono, Cascadia Mono, DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, Ubuntu Mono, monospace";

    private static readonly uint[] RsDark =
    {
        0x1C1C1C, 0xF4707C, 0x7BD88F, 0xF2C811, 0x5AA9F5, 0xC792EA, 0x56D4DD, 0xD8D8D8,
        0x6B6B6B, 0xFF99A4, 0xA5E8B5, 0xFCE66D, 0x8CC4FF, 0xDDB3F5, 0x8FE8EE, 0xFFFFFF
    };
    private static readonly uint[] RsLight =
    {
        0x1A1A1A, 0xC50F1F, 0x107C10, 0x946B00, 0x0050B3, 0x881798, 0x0E7C86, 0x8A8A8A,
        0x5F5F5F, 0xE81123, 0x13A10E, 0xB88700, 0x0078D4, 0xB146C2, 0x2AA7B3, 0xC8C8C8
    };
    private static readonly uint[] Solarized =
    {
        0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
        0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3
    };

    public static readonly TermScheme[] Schemes =
    {
        new("Crimson Banner", null, 0, 0),
        new("Crimson Dark", RsDark, 0xE6E6E6, 0x1C1C1C),
        new("Crimson Light", RsLight, 0x1A1A1A, 0xFFFFFF),
        new("Solarized Dark", Solarized, 0x839496, 0x002B36, 0x93A1A1),
        new("Solarized Light", Solarized, 0x657B83, 0xFDF6E3, 0x586E75),
        new("Tango", new uint[] { 0x2E3436, 0xCC0000, 0x4E9A06, 0xC4A000, 0x3465A4, 0x75507B, 0x06989A, 0xD3D7CF, 0x555753, 0xEF2929, 0x8AE234, 0xFCE94F, 0x729FCF, 0xAD7FA8, 0x34E2E2, 0xEEEEEC }, 0xD3D7CF, 0x2E3436),
        new("Dracula", new uint[] { 0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2, 0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF }, 0xF8F8F2, 0x282A36, 0xF8F8F2),
        new("Gruvbox Dark", new uint[] { 0x282828, 0xCC241D, 0x98971A, 0xD79921, 0x458588, 0xB16286, 0x689D6A, 0xA89984, 0x928374, 0xFB4934, 0xB8BB26, 0xFABD2F, 0x83A598, 0xD3869B, 0x8EC07C, 0xEBDBB2 }, 0xEBDBB2, 0x282828, 0xEBDBB2),
        new("Nord", new uint[] { 0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0, 0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4 }, 0xD8DEE9, 0x2E3440, 0xD8DEE9),
        new("Green on Black", RsDark, 0x33FF66, 0x000000, 0x33FF66),
    };

    private static TerminalSettings? _s;
    public static event Action? Changed;
    private static string FilePath => AppPaths.ConfigFile("terminal.json");

    public static TerminalSettings Settings
    {
        get
        {
            if (_s != null) return _s;
            try
            {
                if (File.Exists(FilePath))
                {
                    var text = File.ReadAllText(FilePath);
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("Profiles", out _)) _s = JsonSerializer.Deserialize<TerminalSettings>(text);
                    else if (doc.RootElement.TryGetProperty("fontSize", out var fs) && fs.TryGetDouble(out var size))
                        _s = new TerminalSettings { Profiles = { new TerminalProfile { FontSize = Math.Clamp(size, 7, 40) } } };   // older versions saved only the size
                }
            }
            catch { }
            _s ??= new TerminalSettings();
            if (_s.Profiles.Count == 0) _s.Profiles.Add(new TerminalProfile());
            if (Find(_s.Default) == null) _s.Default = _s.Profiles[0].Name;
            return _s;
        }
    }

    public static TerminalProfile? Find(string? name) => _s?.Profiles.FirstOrDefault(p => p.Name == name);
    public static TerminalProfile Get(string? name) => Settings.Profiles.FirstOrDefault(p => p.Name == name) ?? DefaultProfile;
    public static TerminalProfile DefaultProfile => Settings.Profiles.FirstOrDefault(p => p.Name == Settings.Default) ?? Settings.Profiles[0];
    public static TermScheme Scheme(string name)
    {
        // the schemes' names before the rename to Crimson Banner OS (20 Nov 2026), as profiles saved them
        name = name switch { "Red Star" => "Crimson Banner", "Red Star Dark" => "Crimson Dark", "Red Star Light" => "Crimson Light", _ => name };
        return Schemes.FirstOrDefault(s => s.Name == name) ?? Schemes[0];
    }

    public static string UniqueName(string baseName)
    {
        var name = baseName; int i = 2;
        while (Settings.Profiles.Any(p => p.Name == name)) name = $"{baseName} {i++}";
        return name;
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        Changed?.Invoke();
    }
}
