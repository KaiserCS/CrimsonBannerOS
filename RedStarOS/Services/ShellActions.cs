using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>Shell-level actions shared by the Start menu, the tray panels and the desktop menu.</summary>
public static class ShellActions
{
    public static void OpenSettings() => AppRegistry.Launch("settings");

    /// <summary>True when Red Star OS is the login session (picked on the login screen), not an app on another desktop.</summary>
    public static bool IsSession => Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1";

    /// <summary>Super+L: the lock screen.</summary>
    public static void Lock()
    {
        if (WindowManager.Desktop != null) new LoginWindow().Show(WindowManager.Desktop);
    }

    /// <summary>As the login session: ends it, back to the login screen. On another desktop: the lock screen.</summary>
    public static void LogOff()
    {
        if (IsSession) WindowManager.Desktop?.Close();
        else Lock();
    }

    /// <summary>As the login session: turns the computer off. On another desktop: closes Red Star OS.</summary>
    public static void Shutdown()
    {
        if (IsSession && RunPower("shutdown")) return;
        WindowManager.Desktop?.Close();
    }

    /// <summary>As the login session: restarts the computer. On another desktop: restarts Red Star OS.</summary>
    public static void Restart()
    {
        if (IsSession && RunPower("restart")) return;
        if (SystemUtil.RelaunchSelf())
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>The command that turns the computer off or restarts it, through systemd-logind (no password needed
    /// for the person at the computer), or null for anything else.</summary>
    public static string[]? PowerCommand(string action, Func<string, string?> which)
    {
        var verb = action switch { "shutdown" => "poweroff", "restart" => "reboot", _ => null };
        if (verb == null) return null;
        if (which("systemctl") is { } sc) return new[] { sc, verb };
        if (which("loginctl") is { } lc) return new[] { lc, verb };
        return null;
    }

    private static bool RunPower(string action)
    {
        if (PowerCommand(action, ProcessUtil.Which) is not { } cmd) return false;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var (code, text) = await ProcessUtil.RunAsync(cmd[0], cmd.Skip(1), 10000);
            if (code != 0)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Notifier.Show(action == "shutdown" ? "Shut down" : "Restart",
                    "The computer refused: " + (string.IsNullOrWhiteSpace(text) ? "exit code " + code : text.Trim()), "settings"));
        });
        return true;
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
            case "lock": Lock(); break;
            case "logoff": LogOff(); break;
            case "shutdown": Shutdown(); break;
            case "restart": Restart(); break;
        }
    }
}
