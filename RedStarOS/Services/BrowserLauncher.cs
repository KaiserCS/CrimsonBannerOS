using System;
using Avalonia.Threading;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>
/// Red Star Online. The native window (tabs and menus in Avalonia, pages rendered by the headless web engine)
/// is primary; if Electron or the engine can't start, the classic single-window Electron browser opens instead.
/// </summary>
public static class BrowserLauncher
{
    private static bool _starting;

    public static void Launch(string? url = null)
    {
        // another browser chosen in Settings → Default apps
        if (!string.IsNullOrEmpty(url) && !url.StartsWith("file:") && DefaultApps.TryOpenWithLinuxApp("web", url)) return;
        Dispatcher.UIThread.Post(() => _ = LaunchAsync(url));
    }

    /// <summary>Opens an extra window whose pages live in an in-memory session that is wiped when it closes.</summary>
    public static void LaunchPrivate() => Dispatcher.UIThread.Post(() => _ = LaunchPrivateAsync());

    private static async System.Threading.Tasks.Task LaunchPrivateAsync()
    {
        var win = new WebBrowserWindow(true);
        WindowManager.Open(win, "online");
        if (!await win.StartAsync(null))
        {
            win.Close();
            Notifier.Show("Red Star Online", "Private browsing needs the web engine: " + (WebEngine.Error ?? "it could not start") + ".", "online");
        }
    }

    private static async System.Threading.Tasks.Task LaunchAsync(string? url)
    {
        if (WebBrowserWindow.Instance is { } open)
        {
            if (!string.IsNullOrEmpty(url)) open.OpenUrl(url);
            open.Raise();
            return;
        }
        if (_starting) return;
        _starting = true;
        try
        {
            var win = new WebBrowserWindow();
            WindowManager.Open(win, "online");
            if (!await win.StartAsync(url))
            {
                var why = WebEngine.Error;
                win.Close();
                Notifier.Show("Red Star Online", "Using the classic browser (" + (why ?? "the web engine could not start") + ").", "online");
                ElectronLauncher.Launch("online", "redstar-browser", "browser", "Red Star Online",
                    string.IsNullOrEmpty(url) ? Array.Empty<string>() : new[] { url });
            }
        }
        catch (Exception ex)
        {
            // never fail silently: log it, tell the user and fall back to the classic browser
            try { System.IO.File.WriteAllText(AppPaths.ConfigFile("browser-error.log"), DateTime.Now + "\n" + ex); } catch { }
            if (WebBrowserWindow.Instance is { } broken) { try { broken.Close(); } catch { } }
            Notifier.Show("Red Star Online", "The new browser failed to start (" + ex.GetType().Name + ": " + ex.Message + "). Using the classic browser.", "online");
            ElectronLauncher.Launch("online", "redstar-browser", "browser", "Red Star Online",
                string.IsNullOrEmpty(url) ? Array.Empty<string>() : new[] { url });
        }
        finally { _starting = false; }
    }
}
