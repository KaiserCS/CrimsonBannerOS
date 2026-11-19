using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>Shell-level actions shared by the Start menu, the tray panels and the desktop menu.</summary>
public static class ShellActions
{
    public static void OpenSettings() => AppRegistry.Launch("settings");

    public static void LogOff()
    {
        if (WindowManager.Desktop != null) new LoginWindow().Show(WindowManager.Desktop);
    }

    public static void Shutdown() => WindowManager.Desktop?.Close();

    public static void Restart()
    {
        if (SystemUtil.RelaunchSelf())
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public static void ToggleTheme()
    {
        SettingsStore.Current.Theme = ThemeService.IsDark ? "Light" : "Dark";
        SettingsStore.Save();
        ThemeService.Apply();
    }

    public static void Perform(string action)
    {
        switch (action)
        {
            case "settings": OpenSettings(); break;
            case "logoff": LogOff(); break;
            case "shutdown": Shutdown(); break;
            case "restart": Restart(); break;
        }
    }
}
