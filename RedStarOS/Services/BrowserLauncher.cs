// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Opens web pages: in the browser chosen in Settings → Default apps, otherwise in Red Star Online (browser/, GTK 4 +
/// WebKitGTK). When Red Star Online isn't installed or won't start, the user is told why and how to fix it.
/// </summary>
public static class BrowserLauncher
{
    static BrowserLauncher()
    {
        RedStarOnline.CrashedAtStart += (_, why) => Dispatcher.UIThread.Post(() =>
            Notifier.Show("Crimson Online closed", $"It stopped right after opening ({why}). Details are in ~/.config/redstaros/online.log.", "online"));
    }

    public static void Launch(string? url = null)
    {
        // another browser chosen in Settings → Default apps
        if (!string.IsNullOrEmpty(url) && !url.StartsWith("file:") && DefaultApps.TryOpenWithLinuxApp("web", url)) return;
        if (RedStarOnline.Launch(url)) return;
        Missing(url);
    }

    /// <summary>A window whose pages live in a session that is wiped when it closes.</summary>
    public static void LaunchPrivate()
    {
        if (!RedStarOnline.Launch(null, privateWindow: true)) Missing(null);
    }

    /// <summary>Red Star Online isn't there: say how to get it, and hand a page to the system's browser meanwhile.</summary>
    private static void Missing(string? url)
    {
        var why = RedStarOnline.ProgramPath() == null
            ? "Crimson Online isn't installed. Install webkitgtk-6.0, gtk4, json-glib and libsoup3, then rebuild Crimson Banner (or run browser/build.sh and install-session.sh)."
            : "Crimson Online didn't start; see ~/.config/redstaros/online.log.";
        if (!string.IsNullOrEmpty(url) && ProcessUtil.Which("xdg-open") is { } open)
        {
            try { System.Diagnostics.Process.Start(open, new[] { url })?.Dispose(); why += " The page was opened in the system's default browser."; }
            catch { }
        }
        Dispatcher.UIThread.Post(() => Notifier.Show("Crimson Online", why, "online"));
    }
}
