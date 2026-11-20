// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;

namespace RedStarOS.Services;

/// <summary>
/// Red Star Online, the GTK 4 + WebKitGTK browser (browser/): its own native window, sharing bookmarks, history and
/// settings with the shell (browser-data.json). Used when it's installed, unless Settings → Default apps says to keep
/// the previous browser (Electron), which stays until the new one has everything.
/// </summary>
public static class RedStarOnline
{
    /// <summary>Its Wayland app id and X11 class.</summary>
    public const string WindowId = "org.redstaros.Online";

    /// <summary>The program: $REDSTAR_ONLINE, next to Red Star, then on PATH (/usr/local/bin after install-session.sh).</summary>
    public static string? ProgramPath()
    {
        if (Environment.GetEnvironmentVariable("REDSTAR_ONLINE") is { Length: > 0 } env && File.Exists(env)) return env;
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "" })
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "redstar-online"))) return Path.Combine(dir, "redstar-online");
        return ProcessUtil.Which("redstar-online");
    }

    public static bool Available => !SettingsStore.Current.ClassicBrowser && ProgramPath() != null;

    /// <summary>A window of Red Star Online (so the taskbar shows it as the Red Star Online app).</summary>
    public static bool IsItsWindow(string instance, string cls) =>
        string.Equals(instance, WindowId, StringComparison.OrdinalIgnoreCase) || string.Equals(cls, WindowId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The arguments for opening a page, or a private window.</summary>
    public static string[] Arguments(string? url, bool privateWindow)
    {
        if (privateWindow) return string.IsNullOrEmpty(url) ? new[] { "--private" } : new[] { "--private", url };
        return string.IsNullOrEmpty(url) ? Array.Empty<string>() : new[] { url };
    }

    /// <summary>Opens Red Star Online (a running one opens the page as a new tab). False when it isn't there.</summary>
    public static bool Launch(string? url, bool privateWindow = false)
    {
        if (!Available || ProgramPath() is not { } exe) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in Arguments(url, privateWindow)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("online: couldn't start " + exe + ": " + ex.Message);
            return false;
        }
    }
}
