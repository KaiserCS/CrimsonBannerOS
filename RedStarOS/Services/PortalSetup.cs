using System;
using System.IO;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Screenshots and screen sharing for other apps (browsers, OBS, video calls) inside redstar-comp go through
/// xdg-desktop-portal, which hands them to xdg-desktop-portal-wlr. This tells the portal to use it for the Red Star
/// desktop (~/.config/xdg-desktop-portal/redstar-portals.conf, written only when you have none) and, in the
/// Red Star (Wayland) session, points the session's services at this compositor.
/// Running as a window on another desktop, that desktop's portal stays in charge.
/// </summary>
public static class PortalSetup
{
    public const string Config =
        "# written by Crimson Banner OS: screenshots and screen sharing through xdg-desktop-portal-wlr (redstar-comp)\n" +
        "[preferred]\n" +
        "default=gtk\n" +
        "org.freedesktop.impl.portal.Screenshot=wlr\n" +
        "org.freedesktop.impl.portal.ScreenCast=wlr\n";

    /// <summary>The portal reads &lt;desktop&gt;-portals.conf for each name in XDG_CURRENT_DESKTOP, lower-cased.</summary>
    public static string[] FileNames => new[] { "redstar-portals.conf", "redstaros-portals.conf" };

    public static bool WlrInstalled =>
        File.Exists("/usr/share/xdg-desktop-portal/portals/wlr.portal") || File.Exists("/usr/local/share/xdg-desktop-portal/portals/wlr.portal");

    /// <summary>
    /// xdg-desktop-portal-wlr's own settings (~/.config/xdg-desktop-portal-wlr/config, written only when you have
    /// none). Its usual screen pickers (slurp, wofi, bemenu) draw with layer-shell, which redstar-comp doesn't have,
    /// so asking one makes screen sharing fail. Without a picker it shares the screen (the first one on several).
    /// </summary>
    public const string WlrConfig =
        "# written by Crimson Banner OS: share the screen without a picker (slurp/wofi need layer-shell, which redstar-comp lacks)\n" +
        "[screencast]\n" +
        "chooser_type=none\n" +
        "max_fps=60\n";

    public static void Run()
    {
        if (!InputIsolation.InRedStarComp) return;
        try
        {
            var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(AppPaths.Home, ".config");
            var dir = Path.Combine(cfg, "xdg-desktop-portal");
            Directory.CreateDirectory(dir);
            foreach (var name in FileNames)
            {
                var f = Path.Combine(dir, name);
                if (!File.Exists(f)) File.WriteAllText(f, Config);
            }
            var wlrDir = Path.Combine(cfg, "xdg-desktop-portal-wlr");
            Directory.CreateDirectory(wlrDir);
            var wlr = Path.Combine(wlrDir, "config");
            if (!File.Exists(wlr)) File.WriteAllText(wlr, WlrConfig);
        }
        catch { }
        if (Environment.GetEnvironmentVariable("REDSTAR_SESSION") != "1") return;
        _ = Task.Run(async () =>
        {
            // the session's D-Bus and systemd services (the portal among them) start with this display's variables
            var vars = new[] { "WAYLAND_DISPLAY", "DISPLAY", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE" };
            if (ProcessUtil.Which("dbus-update-activation-environment") is { } d)
                await ProcessUtil.RunAsync(d, new[] { "--systemd" }.Concat(vars));
            else if (ProcessUtil.Which("systemctl") is { } sc)
                await ProcessUtil.RunAsync(sc, new[] { "--user", "import-environment" }.Concat(vars));
            if (ProcessUtil.Which("systemctl") is { } sys)
                await ProcessUtil.RunAsync(sys, new[] { "--user", "try-restart", "xdg-desktop-portal.service", "xdg-desktop-portal-wlr.service" });
        });
    }
}
