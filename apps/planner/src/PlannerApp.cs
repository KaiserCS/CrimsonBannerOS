// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using CrimsonPlanner.Views;

namespace CrimsonPlanner;

/// <summary>Planner: the shell's own look (Styles/Shell.axaml, shared with it), in the theme and accent it uses.</summary>
public sealed class PlannerApp : Application
{
    public static string? StartupFile { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://CrimsonPlanner/")) { Source = new Uri("avares://CrimsonPlanner/Styles/Shell.axaml") });
        Look.Apply(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new PlannerWindow(StartupFile);
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>The shell's theme (light, dark or the system's) and accent, read from its settings file.</summary>
public static class Look
{
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
        app.Resources["ShellFocusBrush"] = new SolidColorBrush(c);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
