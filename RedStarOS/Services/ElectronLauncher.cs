using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RedStarOS.Services;

/// <summary>
/// Starts the Electron-based apps (Red Star Online, Red Star Video). Both use the single Electron
/// binary that is installed under the browser folder, so it is only shipped once.
/// </summary>
public static class ElectronLauncher
{
    /// <summary>Finds an app folder: usr/lib/&lt;packaged&gt; inside the AppImage, or ../&lt;dev&gt; when running from source.</summary>
    public static string? FindAppDir(string packagedFolder, string devFolder)
    {
        var packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "lib", packagedFolder));
        if (File.Exists(Path.Combine(packaged, "main.js"))) return packaged;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, devFolder);
            if (File.Exists(Path.Combine(candidate, "main.js"))) return candidate;
        }
        return null;
    }

    public static string? FindElectron()
    {
        var packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "lib", "redstar-browser",
            "node_modules", "electron", "dist", "electron"));
        if (File.Exists(packaged)) return packaged;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "browser", "node_modules", "electron", "dist", "electron");
            if (File.Exists(candidate)) return candidate;
        }
        return ProcessUtil.Which("electron");      // a system-wide Electron also works
    }

    /// <summary>
    /// Starts an Electron app. The app joins the taskbar and app tray through the shell bridge:
    /// it gets the socket path and the current theme on its command line / environment, and a
    /// "starting" taskbar button appears immediately.
    /// </summary>
    public static void Launch(string appId, string packagedFolder, string devFolder, string friendlyName, IEnumerable<string> appArgs)
    {
        var extra = appArgs.ToList();

        // Already running and nothing to hand over: just bring it to the front.
        if (extra.Count == 0 && ShellBridge.FocusIfRunning(appId)) return;

        var appDir = FindAppDir(packagedFolder, devFolder);
        if (appDir == null)
        {
            Notifier.Show(friendlyName, $"The {friendlyName} files were not found. Rebuild the AppImage (needs npm), or keep the '{devFolder}' folder next to the program.", appId);
            return;
        }

        var electron = FindElectron();
        if (electron == null)
        {
            Notifier.Show(friendlyName, "Electron is not installed. Run 'npm run setup' inside the browser folder.", appId);
            return;
        }

        var args = new List<string>
        {
            "--no-sandbox", "--ozone-platform-hint=auto", appDir,
            "--redstar-theme=" + (ThemeService.IsDark ? "dark" : "light"),
            "--redstar-accent=" + SettingsStore.Current.Accent
        };
        if (Environment.GetEnvironmentVariable("REDSTAR_UI_SCALE") is { Length: > 0 } scale)
            args.Add("--force-device-scale-factor=" + scale);            // Settings → Accessibility → size
        if (SettingsStore.Current.ReduceMotion) args.Add("--force-prefers-reduced-motion");
        args.AddRange(extra);

        Dictionary<string, string>? env = null;
        if (ShellBridge.SocketPath != null)
            env = new Dictionary<string, string> { ["REDSTAR_SHELL_SOCKET"] = ShellBridge.SocketPath };

        ShellBridge.ExpectLaunch(appId, friendlyName);
        if (!ProcessUtil.StartDetached(electron, args, appDir, env))
            ShellBridge.CancelLaunch(appId);
    }
}
