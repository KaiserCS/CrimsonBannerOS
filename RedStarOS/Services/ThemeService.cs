using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace RedStarOS.Services;

public static class ThemeService
{
    /// <summary>Raised after the theme or accent changed (native apps redraw with the new colours).</summary>
    public static event System.Action? Changed;

    public static readonly (string Name, string Hex)[] Accents =
    {
        ("Blue", "#0078D4"), ("Crimson", "#C8102E"), ("Green", "#107C10"), ("Orange", "#D83B01"),
        ("Purple", "#744DA9"), ("Teal", "#00828A"), ("Gray", "#5D5A58")
    };

    /// <summary>True when the shell should use their dark look right now.</summary>
    public static bool IsDark =>
        SettingsStore.Current.Theme switch
        {
            "Dark" => true,
            "System" => Application.Current?.ActualThemeVariant == ThemeVariant.Dark,
            _ => false
        };

    public static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;
        var s = SettingsStore.Current;

        app.RequestedThemeVariant = s.Theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Light
        };

        if (Color.TryParse(s.Accent, out var c))
        {
            app.Resources["SystemAccentColor"] = c;
            app.Resources["SystemAccentColorLight1"] = Mix(c, Colors.White, 0.20);
            app.Resources["SystemAccentColorLight2"] = Mix(c, Colors.White, 0.40);
            app.Resources["SystemAccentColorLight3"] = Mix(c, Colors.White, 0.60);
            app.Resources["SystemAccentColorDark1"] = Mix(c, Colors.Black, 0.20);
            app.Resources["SystemAccentColorDark2"] = Mix(c, Colors.Black, 0.40);
            app.Resources["SystemAccentColorDark3"] = Mix(c, Colors.Black, 0.60);

            // Brushes used by Styles/Shell.axaml (Start tile, primary buttons, "on" tiles, badges).
            app.Resources["ShellAccentBrush"] = new SolidColorBrush(c);
            app.Resources["ShellAccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x3A, c.R, c.G, c.B));
        }

        if (Color.TryParse(s.Accent, out var fc)) app.Resources["ShellFocusBrush"] = new SolidColorBrush(fc);
        ApplyHighContrast(app, s.HighContrast);
        ApplyNoCompositor(app);
        LookAndFeel.ApplyOtherPrograms();     // GTK and Qt programs follow light/dark (when Red Star styles them)

        Changed?.Invoke();
    }

    // ───────────── no compositor (Red Star's own display without picom/xcompmgr) ─────────────
    private static bool _noCompositorDone;

    /// <summary>True when transparent windows can't be shown: popups and the Alt+Tab panel are drawn opaque.</summary>
    public static bool OpaquePopups =>
        (InputIsolation.IsNested || Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1") && !X11Props.CompositorRunning();

    private static void ApplyNoCompositor(Application app)
    {
        if (_noCompositorDone || !OpaquePopups) return;
        _noCompositorDone = true;
        app.Resources["OverlayCornerRadius"] = new CornerRadius(0);       // Fluent's menus, flyouts, drop-downs
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://RedStarOS/App.axaml"))
            { Source = new Uri("avares://RedStarOS/Styles/NoCompositor.axaml") });
    }

    // ───────────── high contrast ─────────────
    private static Avalonia.Markup.Xaml.Styling.StyleInclude? _hcStyles;
    private static bool _variantHooked;
    private static readonly string[] HcKeys =
    {
        "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush",
        "ShellBorderBrush", "ShellCardBrush", "ShellMutedBrush", "ShellPanelBorderBrush", "ShellFocusBrush", "HcStrokeBrush", "HcStrokeInverseBrush", "HcFocusBrush",
        "SystemControlFocusVisualPrimaryBrush", "SystemControlFocusVisualSecondaryBrush",
        "ControlStrokeColorDefaultBrush", "CardStrokeColorDefaultBrush", "TextControlBorderBrush", "ButtonBorderBrush",
    };

    private static void ApplyHighContrast(Application app, bool on)
    {
        if (!_variantHooked)
        {
            _variantHooked = true;     // "System" theme can flip between light and dark while running
            app.ActualThemeVariantChanged += (_, _) => { if (SettingsStore.Current.HighContrast) ApplyHighContrast(app, true); };
        }
        _hcStyles ??= new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://RedStarOS/App.axaml"))
            { Source = new Uri("avares://RedStarOS/Styles/HighContrast.axaml") };
        bool present = app.Styles.Contains(_hcStyles);
        if (!on)
        {
            if (present) app.Styles.Remove(_hcStyles);
            foreach (var k in HcKeys) if (k != "ShellFocusBrush") app.Resources.Remove(k);
            return;
        }
        bool dark = app.ActualThemeVariant == ThemeVariant.Dark;
        var fg = dark ? Colors.White : Colors.Black;
        var bg = dark ? Colors.Black : Colors.White;
        var focus = dark ? Color.Parse("#FFD700") : Color.Parse("#0000C8");
        var fgB = new SolidColorBrush(fg);
        foreach (var k in new[] { "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush",
                                  "ShellBorderBrush", "ShellMutedBrush", "ShellPanelBorderBrush", "HcStrokeBrush", "ControlStrokeColorDefaultBrush", "CardStrokeColorDefaultBrush",
                                  "TextControlBorderBrush", "ButtonBorderBrush" })
            app.Resources[k] = fgB;
        app.Resources["ShellCardBrush"] = new SolidColorBrush(dark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x18, 0, 0, 0));
        app.Resources["HcStrokeInverseBrush"] = new SolidColorBrush(bg);
        app.Resources["HcFocusBrush"] = new SolidColorBrush(focus);
        app.Resources["ShellFocusBrush"] = new SolidColorBrush(focus);
        app.Resources["SystemControlFocusVisualPrimaryBrush"] = new SolidColorBrush(focus);
        app.Resources["SystemControlFocusVisualSecondaryBrush"] = new SolidColorBrush(bg);
        if (!present) app.Styles.Add(_hcStyles);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
