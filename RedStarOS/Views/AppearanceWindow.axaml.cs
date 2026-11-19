using System.Linq;
using Avalonia.Controls;
using RedStarOS.Services;

namespace RedStarOS.Views;

public partial class AppearanceWindow : Window
{
    private static readonly string[] Themes = { "Light", "Dark", "System" };

    public AppearanceWindow()
    {
        InitializeComponent();
        var s = SettingsStore.Current;

        ThemeBox.ItemsSource = Themes;
        ThemeBox.SelectedIndex = System.Math.Max(0, System.Array.IndexOf(Themes, s.Theme));

        AccentBox.ItemsSource = ThemeService.Accents.Select(a => a.Name).ToList();
        var idx = System.Array.FindIndex(ThemeService.Accents, a => a.Hex.Equals(s.Accent, System.StringComparison.OrdinalIgnoreCase));
        AccentBox.SelectedIndex = idx < 0 ? 0 : idx;

        FullscreenCheck.IsChecked = s.Fullscreen;
        FullscreenCheck.IsCheckedChanged += (_, _) =>
        {
            SettingsStore.Current.Fullscreen = FullscreenCheck.IsChecked == true;
            SettingsStore.Save();
            if (WindowManager.Desktop != null)
                WindowManager.Desktop.WindowState = SettingsStore.Current.Fullscreen ? Avalonia.Controls.WindowState.FullScreen : Avalonia.Controls.WindowState.Maximized;
        };
        ThemeBox.SelectionChanged += (_, _) => Apply();
        AccentBox.SelectionChanged += (_, _) => Apply();
    }

    private void Apply()
    {
        var s = SettingsStore.Current;
        if (ThemeBox.SelectedIndex >= 0) s.Theme = Themes[ThemeBox.SelectedIndex];
        if (AccentBox.SelectedIndex >= 0) s.Accent = ThemeService.Accents[AccentBox.SelectedIndex].Hex;
        SettingsStore.Save();
        ThemeService.Apply();
    }
}
