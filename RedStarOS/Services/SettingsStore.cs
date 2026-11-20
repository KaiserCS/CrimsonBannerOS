using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RedStarOS.Services;

public sealed class WindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

/// <summary>Everything that is remembered between sessions (~/.config/redstaros/settings.json).</summary>
public sealed class Settings
{
    public string? WallpaperPath { get; set; }
    public string? WallpaperFolder { get; set; }
    public bool SlideshowEnabled { get; set; }
    public int SlideshowMinutes { get; set; } = 10;

    public string? AvatarPath { get; set; }
    public string AccountType { get; set; } = "Admin";
    public string? PasswordSalt { get; set; }
    public string? PasswordHash { get; set; }

    public bool Fullscreen { get; set; } = true;       // desktop covers the whole screen
    public string Theme { get; set; } = "Light";      // Light | Dark | System
    public string Accent { get; set; } = "#0078D4";

    public string? UpdateUrl { get; set; }            // e.g. https://api.github.com/repos/USER/REPO/releases/latest

    public string? WriterFontFamily { get; set; }
    public double WriterFontSize { get; set; } = 16;
    public string? WriterColor { get; set; }
    public List<string> RecentFiles { get; set; } = new();
    public List<string> RecentApps { get; set; } = new();     // Start menu "Recent" list
    public string StartSection { get; set; } = "redstar";      // Start menu tab: "redstar" or "linux"
    public string FilesView { get; set; } = "list";            // File Manager: "list" or "grid"
    public bool FilesShowHidden { get; set; }                   // File Manager: show dot-files
    public List<string> PinnedFolders { get; set; } = new();    // File Manager sidebar: Pinned
    public List<string> StarredPaths { get; set; } = new();     // File Manager: Starred files and folders
    public bool FilesDetails { get; set; }                      // File Manager: details panel (F9)
    public string? LastFolder { get; set; }                     // the folder last open in Files or an Open / Save dialog
    public List<string> RecentServers { get; set; } = new();    // File Manager: Connect to server history
    public int PongBest { get; set; }                          // solo mode: longest rally
    public int PongAiWins { get; set; }                        // versus-computer wins
    public bool LockWithSystemPassword { get; set; } = true;  // the lock screen checks the Linux password (PAM)
    public List<string> KeyboardLayouts { get; set; } = new();    // Region & keyboard: "us", "de:nodeadkeys"… (empty: the system's)
    public string KeyboardSwitch { get; set; } = "grp:alt_shift_toggle"; // how to switch between layouts (an XKB option)
    public string KeyboardCompose { get; set; } = "";          // e.g. "compose:ralt"
    public string KeyboardCapsLock { get; set; } = "";         // e.g. "caps:escape", "ctrl:nocaps"
    public int ScreenOffMinutes { get; set; } = 10;            // Power: screen off after so long without input (0: never)
    public bool LockOnScreenOff { get; set; } = true;          // Power: lock when the screen goes off, and before sleep
    public int SleepMinutes { get; set; }                      // Power: sleep after so long without input (0: never)
    public bool RunAutostart { get; set; } = true;             // start ~/.config/autostart and /etc/xdg/autostart programs at sign-in
    public int BlocksBest { get; set; }                        // Blocks: best score
    public int GemsBest { get; set; }                          // Gems: best score
    public int MahjonggWins { get; set; }                      // Mahjongg: boards cleared
    public bool Clock24h { get; set; } = true;
    public bool TaskbarLabels { get; set; } = true;
    public List<string> PinnedApps { get; set; } = new() { "files", "online", "video" };   // taskbar launchers

    public Dictionary<string, WindowBounds> Windows { get; set; } = new();

    public bool WelcomeDone { get; set; }                      // first-run welcome finished
    public bool AutoUpdateCheck { get; set; } = true;          // look for a new release once a day
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }
    public double UiScale { get; set; } = 1.0;                 // Accessibility: size of everything (restart)
    public bool HighContrast { get; set; }                     // Accessibility
    public bool ReduceMotion { get; set; }                     // Accessibility: no animations
    public bool LargeCursor { get; set; }                      // Accessibility
    public string? UiFont { get; set; }                         // Appearance: the shell's font (null: the theme's)
    public double UiFontSize { get; set; } = 14;                // Appearance: text size in pixels
    public string? CursorTheme { get; set; }                    // Appearance: pointer theme (null: the system's default)
    public int CursorSize { get; set; } = 24;                   // Appearance: pointer size (48 at least with LargeCursor)
    public string? IconTheme { get; set; }                      // Appearance: icon theme for GTK and Qt programs
    public string? GtkTheme { get; set; }                       // Appearance: GTK programs' theme
    public string QtStyle { get; set; } = "gtk";                // Appearance: Qt programs follow "gtk", "qt6ct" or are left "off"
    public bool? StyleOtherApps { get; set; }                   // Appearance: write GTK/Qt settings (null: only in a Red Star session)
    public bool IsolateInput { get; set; } = true;             // own display on top of another desktop (keyboard/mouse stay in Red Star)
    public bool WindowShadows { get; set; } = true;            // drop shadows under windows (picom; next start)
    public bool WaylandCompositor { get; set; }                // experimental: run inside redstar-comp (wlroots) instead of redstar-wm + picom; next start
    public bool WaylandApps { get; set; } = true;               // inside redstar-comp other programs run natively on Wayland (screen sharing); off: all X11; next start
    public bool NativeWaylandShell { get; set; }               // experimental: Red Star's own windows natively on Wayland inside redstar-comp; next start
    public string Graphics { get; set; } = "auto";              // auto | gpu | software (Settings → Diagnostics)
}

public static class AppPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigDir
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(b)) b = Path.Combine(Home, ".config");
            var d = Path.Combine(b, "redstaros");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string DataHome
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return string.IsNullOrEmpty(b) ? Path.Combine(Home, ".local", "share") : b;
        }
    }

    public static string ConfigFile(string name) => Path.Combine(ConfigDir, name);

    /// <summary>~/Pictures, ~/Videos ... falling back to ~/Name when the folder is missing.</summary>
    public static string UserFolder(string name)
    {
        var p = Path.Combine(Home, name);
        return p;
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static Settings Current { get; private set; } = new();

    private static string FilePath => AppPaths.ConfigFile("settings.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            Current = new Settings();
        }
    }

    public static void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not save settings: " + ex.Message);
        }
    }
}
