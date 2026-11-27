// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CrimsonBanner.Apps;

/// <summary>
/// Our apps' look (apps/shared, linked into each): the shell's own styles (Styles/Shell.axaml, linked into the app as
/// Styles/Shell.axaml) in the theme and accent the shell uses, read from its settings file.
/// </summary>
public static class Look
{
    /// <summary>Fluent, the shell's styles from the app's own assembly, and the shell's theme and accent.</summary>
    public static void Use(Application app, string assembly)
    {
        app.Styles.Add(new FluentTheme());
        app.Styles.Add(new StyleInclude(new Uri($"avares://{assembly}/")) { Source = new Uri($"avares://{assembly}/Styles/Shell.axaml") });
        Apply(app);
    }

    public static void Apply(Application app)
    {
        string theme = "Dark", accent = "#C8102E";
        try
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var file = Path.Combine(cfg, "redstaros", "settings.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Theme", out var t) && t.GetString() is { Length: > 0 } ts) theme = ts;
                if (doc.RootElement.TryGetProperty("Accent", out var a) && a.GetString() is { Length: > 0 } acs) accent = acs;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        app.RequestedThemeVariant = theme switch { "Light" => ThemeVariant.Light, "System" => ThemeVariant.Default, _ => ThemeVariant.Dark };
        if (!Color.TryParse(accent, out var c)) c = Color.Parse("#C8102E");
        app.Resources["SystemAccentColor"] = c;
        app.Resources["SystemAccentColorLight1"] = Mix(c, Colors.White, 0.20);
        app.Resources["SystemAccentColorLight2"] = Mix(c, Colors.White, 0.40);
        app.Resources["SystemAccentColorLight3"] = Mix(c, Colors.White, 0.60);
        app.Resources["SystemAccentColorDark1"] = Mix(c, Colors.Black, 0.20);
        app.Resources["SystemAccentColorDark2"] = Mix(c, Colors.Black, 0.40);
        app.Resources["SystemAccentColorDark3"] = Mix(c, Colors.Black, 0.60);
        app.Resources["ShellAccentBrush"] = new SolidColorBrush(c);
        app.Resources["ShellAccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x3A, c.R, c.G, c.B));
        // the accent for small text: lightened on dark so it reads at 4.5:1 on graphite (DESIGN.md), as is on light
        app.Resources["ShellAccentTextBrush"] = new SolidColorBrush(theme == "Light" ? c : Mix(c, Colors.White, 0.45));
        app.Resources["ShellFocusBrush"] = new SolidColorBrush(c);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
