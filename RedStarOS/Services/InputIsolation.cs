using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Keeps the keyboard and mouse inside Red Star OS when it runs on top of another desktop (GNOME, KDE, Xfce…).
///
/// A normal window can't stop the host from acting on Super, Alt+Tab, hot corners or its panels — on Wayland the
/// compositor always sees them first, and on X11 the host window manager owns those key grabs. So Red Star starts
/// its own display server, full screen, and runs inside it, just like a virtual machine:
///   • Wayland host → rootful Xwayland with -host-grab (the compositor's shortcuts are inhibited and the pointer is
///     confined; needs Xwayland 23.1+, and GNOME asks once whether to allow it).
///   • X11 host (or older Xwayland) → Xephyr, whose keyboard/pointer grab is switched on automatically.
/// Inside, a window manager and the Red Star shell run as in the full session; every key goes to Red Star.
/// Ctrl+Shift releases (and re-captures) the keyboard and mouse; signing out of Red Star returns to the host.
///
/// Off when Red Star *is* the session (redstaros-session sets REDSTAR_SESSION=1), with --no-isolate,
/// REDSTAR_NO_ISOLATE=1, or Settings → Desktop &amp; Taskbar.
/// </summary>
public static class InputIsolation
{
    /// <summary>Set inside the nested display: "xwayland" or "xephyr".</summary>
    public static string? Mode => Environment.GetEnvironmentVariable("REDSTAR_NESTED_MODE");
    public static bool IsNested => Environment.GetEnvironmentVariable("REDSTAR_NESTED") == "1";
    /// <summary>Set when isolation was wanted but no suitable display server was found (package hint for the user).</summary>
    public static string? Missing => Environment.GetEnvironmentVariable("REDSTAR_ISOLATION_MISSING");

    private static string? Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : null;

    /// <summary>
    /// Called first thing in Main. Returns the exit code when Red Star ran (and finished) inside its own display;
    /// null when it should simply start here.
    /// </summary>
    public static int? TryRunIsolated(string[] args)
    {
        if (args.Contains("--no-isolate") || IsNested || InRedStarComp || Env("REDSTAR_NO_ISOLATE") == "1") return null;
        bool session = Env("REDSTAR_SESSION") == "1";
        // Phase 2 of the Wayland move: Red Star inside its own Wayland compositor (experimental, opt-in)
        if (WaylandWanted(session) && RedStarCompPath() is { } compExe && RunInRedStarComp(args, compExe, session) is { } code) return code;
        if (session) return null;
        if (!SettingEnabled()) return null;
        bool wayland = Env("WAYLAND_DISPLAY") != null;
        bool x11 = Env("DISPLAY") != null;
        if (!wayland && !x11) return null;

        // ── pick the display server ──
        string? exe = null, mode = null;
        int n = FreeDisplay();
        var serverArgs = new List<string>();
        var (sw, sh) = HostScreenSize();
        if (wayland && Which("Xwayland") is { } xw && HelpOf(xw) is { } help && help.Contains("-host-grab"))
        {
            exe = xw; mode = "xwayland";
            serverArgs.AddRange(new[] { ":" + n, "-fullscreen", "-host-grab", "-noreset", "-nolisten", "tcp" });
            // Rootful Xwayland's screen is 640×480 unless told otherwise; -fullscreen alone just stretches that
            // to the monitor. Give it the monitor's real size so Red Star draws at full resolution.
            if (sw > 0 && sh > 0) serverArgs.AddRange(new[] { "-geometry", $"{sw}x{sh}" });
            if (help.Contains("-hidpi")) serverArgs.Add("-hidpi");          // follow the desktop's scale (Xwayland 24+)
        }
        else if (Which("Xephyr") is { } xe && x11)
        {
            exe = xe; mode = "xephyr";
            serverArgs.AddRange(new[] { ":" + n, "-fullscreen", "-resizeable", "-title", "Crimson Banner OS", "-noreset", "-nolisten", "tcp" });
            if (sw > 0 && sh > 0) serverArgs.AddRange(new[] { "-screen", $"{sw}x{sh}" });    // one monitor, at its full size
        }
        if (exe == null)
        {
            Environment.SetEnvironmentVariable("REDSTAR_ISOLATION_MISSING", wayland ? "xwayland-or-xephyr" : "xephyr");
            Log("no nested display server found; running directly on the host");
            return null;
        }

        // ── start it and wait for its socket ──
        Process server;
        try { server = Process.Start(new ProcessStartInfo(exe, serverArgs) { UseShellExecute = false })!; }
        catch (Exception ex) { Log("could not start " + exe + ": " + ex.Message); return null; }
        var socket = $"/tmp/.X11-unix/X{n}";
        var deadline = DateTime.Now.AddSeconds(12);
        while (!File.Exists(socket) && !server.HasExited && DateTime.Now < deadline) Thread.Sleep(100);
        if (server.HasExited || !File.Exists(socket))
        {
            Log($"{mode} did not start (exit {(server.HasExited ? server.ExitCode : -1)}); running directly on the host");
            try { if (!server.HasExited) server.Kill(true); } catch { }
            return null;
        }
        Thread.Sleep(300);

        // Xephyr grabs only after Ctrl+Shift: press it for the user once its window has focus.
        if (mode == "xephyr") new Thread(() => { Thread.Sleep(900); XephyrGrab(); }) { IsBackground = true }.Start();

        // ── environment inside ──
        var gpuFlag = Graphics.GpuFlagFor(":" + n);              // Xwayland: DRI3 → GPU; Xephyr: none
        Log($"graphics inside :{n}: {(gpuFlag == "1" ? "GPU available (DRI3)" : "no GPU access — software drawing")}");
        var env = new Dictionary<string, string?>
        {
            ["REDSTAR_GPU"] = gpuFlag,
            ["DISPLAY"] = ":" + n,
            ["WAYLAND_DISPLAY"] = null,                 // everything inside talks X11 to the nested server
            ["XDG_SESSION_TYPE"] = "x11",
            ["GDK_BACKEND"] = "x11",
            ["QT_QPA_PLATFORM"] = "xcb",
            ["SDL_VIDEODRIVER"] = "x11",
            ["ELECTRON_OZONE_PLATFORM_HINT"] = "x11",
            ["XDG_CURRENT_DESKTOP"] = "RedStar",
            ["REDSTAR_NESTED"] = "1",
            ["REDSTAR_NESTED_MODE"] = mode == "xephyr" && wayland ? "xephyr-on-wayland" : mode,
            ["REDSTAR_HOST_DISPLAY"] = Env("DISPLAY"),
            ["REDSTAR_HOST_WAYLAND_DISPLAY"] = Env("WAYLAND_DISPLAY"),
        };

        var children = new List<Process>();
        void Cleanup()
        {
            foreach (var c in children) try { if (!c.HasExited) c.Kill(true); } catch { }
            try { if (!server.HasExited) server.Kill(true); } catch { }
        }
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Cleanup());
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => Cleanup());
        using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => Cleanup());

        try
        {
            if (StartWindowManager(env) is { } wm) { children.Add(wm); Thread.Sleep(250); }
            if (StartCompositor(env) is { } comp) { children.Add(comp); Thread.Sleep(300); }

            // ── Red Star itself, inside ──
            var self = Environment.ProcessPath ?? "RedStarOS";
            var psi = new ProcessStartInfo(self) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(self) == "dotnet")
                psi.ArgumentList.Add(typeof(InputIsolation).Assembly.Location);   // running as "dotnet RedStarOS.dll"
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env)
                if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
            var shell = Process.Start(psi)!;
            children.Add(shell);
            Log($"running inside {mode} on :{n} at {(sw > 0 ? $"{sw}x{sh}" : "the server's default size")} ({string.Join(' ', serverArgs)})");
            shell.WaitForExit();
            return shell.ExitCode;
        }
        catch (Exception ex)
        {
            Log("isolated start failed: " + ex.Message);
            Cleanup();
            return null;
        }
        finally { Cleanup(); }
    }

    // ─────────── redstar-comp (compositor/redstar-comp.c, wlroots 0.20) ───────────

    /// <summary>Running inside redstar-comp (it sets this for everything it starts).</summary>
    public static bool InRedStarComp => Env("REDSTAR_COMPOSITOR_RUNNING") == "redstar-comp";

    /// <summary>
    /// Use redstar-comp? REDSTAR_WAYLAND=1 / 0 decides; otherwise Settings → Desktop &amp; Taskbar → "Wayland compositor"
    /// (only on top of another desktop: the Red Star session picks it at the login screen, "Crimson Banner OS (Wayland)").
    /// </summary>
    public static bool WaylandWanted(bool session)
    {
        var e = Env("REDSTAR_WAYLAND");
        if (e == "1") return true;
        if (e == "0" || session) return false;
        return SettingFlag("WaylandCompositor");
    }

    /// <summary>The redstar-comp program next to RedStarOS (or on PATH), when it was built.</summary>
    public static string? RedStarCompPath()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "" })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var p = Path.Combine(dir, "redstar-comp");
            if (!File.Exists(p)) continue;
            try
            {
                var mode = File.GetUnixFileMode(p);
                if ((mode & UnixFileMode.UserExecute) == 0)
                    File.SetUnixFileMode(p, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch { }
            return p;
        }
        return Which("redstar-comp");
    }

    /// <summary>
    /// Runs Red Star inside redstar-comp: on the hardware (session from a text console or the login screen) or as a
    /// window on the current desktop. redstar-comp replaces redstar-wm and picom — title bars, shadows and the
    /// work area come from it — and Red Star still talks X11, through the Xwayland it runs. Returns null when it
    /// could not start (Red Star then starts the usual way).
    /// </summary>
    private static int? RunInRedStarComp(string[] args, string comp, bool session)
    {
        var self = Environment.ProcessPath ?? "RedStarOS";
        var psi = new ProcessStartInfo(comp) { UseShellExecute = false, RedirectStandardError = true };
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(self);
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") psi.ArgumentList.Add(typeof(InputIsolation).Assembly.Location);
        foreach (var a in args) psi.ArgumentList.Add(a);
        var env = new Dictionary<string, string?>
        {
            ["XDG_CURRENT_DESKTOP"] = "RedStar",
            ["REDSTAR_COMPOSITOR_RUNNING"] = "redstar-comp",
            ["REDSTAR_HOST_DISPLAY"] = Env("DISPLAY"),
            ["REDSTAR_HOST_WAYLAND_DISPLAY"] = Env("WAYLAND_DISPLAY"),
        };
        foreach (var (k, v) in AppDisplayEnv(SettingFlag("WaylandApps", fallback: true))) env[k] = v;
        env["REDSTAR_SHADOWS"] = SettingFlag("WindowShadows", fallback: true) ? "1" : "0";
        env["REDSTAR_REDUCE_MOTION"] = SettingFlag("ReduceMotion") ? "1" : "0";         // no window fades
        if (!session) { env["REDSTAR_NESTED"] = "1"; env["REDSTAR_NESTED_MODE"] = "redstar-comp"; }
        if (Env("REDSTAR_UI_SCALE") == null && Env("AVALONIA_GLOBAL_SCALE_FACTOR") is { } sc) env["REDSTAR_UI_SCALE"] = sc;
        foreach (var (k, v) in env) if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { Log("could not start redstar-comp: " + ex.Message); return null; }
        var log = AppPaths.ConfigFile("session.log");
        bool started = false;
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.Contains("redstar-comp on WAYLAND_DISPLAY")) started = true;
            try { lock (CompLogLock) File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {e.Data}\n"); } catch { }
        };
        p.BeginErrorReadLine();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => { try { if (!p.HasExited) p.Kill(); } catch { } });
        using var hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => { try { if (!p.HasExited) p.Kill(); } catch { } });
        Log($"running inside redstar-comp ({(session ? "session" : "nested")})");
        p.WaitForExit();
        if (!started)
        {
            Log($"redstar-comp did not start (exit {p.ExitCode}); see session.log — starting the usual way");
            return null;
        }
        return p.ExitCode;
    }

    private static readonly object CompLogLock = new();

    private static bool SettingEnabled()
    {
        try
        {
            var cfg = Env("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var file = Path.Combine(cfg, "redstaros", "settings.json");
            if (!File.Exists(file)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return !(doc.RootElement.TryGetProperty("IsolateInput", out var v) && v.ValueKind == JsonValueKind.False);
        }
        catch { return true; }
    }

    /// <summary>A true/false setting straight from settings.json (this runs before the settings are loaded).</summary>
    /// <summary>
    /// How programs inside redstar-comp pick their display. Native Wayland (the default): GTK, Qt, SDL and Electron
    /// choose for themselves, which they do as Wayland. That is what screen sharing needs: OBS and Discord only offer
    /// the PipeWire portal (whole screen, every window) as Wayland programs; through Xwayland they see X11 windows
    /// only. The taskbar lists them either way (redstar-comp reports native windows). Off: everything talks X11, as
    /// Red Star did before.
    /// </summary>
    public static Dictionary<string, string?> AppDisplayEnv(bool waylandApps) => waylandApps
        ? new()
        {
            ["XDG_SESSION_TYPE"] = "wayland",
            ["GDK_BACKEND"] = null, ["QT_QPA_PLATFORM"] = null, ["SDL_VIDEODRIVER"] = null,
            ["ELECTRON_OZONE_PLATFORM_HINT"] = "auto",       // older Electron apps (Discord) stay on X11 without it
        }
        : new()
        {
            ["GDK_BACKEND"] = "x11", ["QT_QPA_PLATFORM"] = "xcb", ["SDL_VIDEODRIVER"] = "x11",
            ["ELECTRON_OZONE_PLATFORM_HINT"] = "x11",
        };

    internal static bool SettingFlag(string name, bool fallback = false)
    {
        try
        {
            var cfg = Env("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            var file = Path.Combine(cfg, "redstaros", "settings.json");
            if (!File.Exists(file)) return fallback;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty(name, out var v) || v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return fallback;
            return v.ValueKind == JsonValueKind.True;
        }
        catch { return fallback; }
    }

    private static int FreeDisplay()
    {
        for (int i = 20; i < 200; i++)
            if (!File.Exists($"/tmp/.X11-unix/X{i}") && !File.Exists($"/tmp/.X{i}-lock")) return i;
        return 99;
    }

    private static string? Which(string name)
    {
        foreach (var dir in (Env("PATH") ?? "/usr/bin:/bin").Split(':').Append("/usr/bin"))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? HelpOf(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-help") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true })!;
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(4000)) try { p.Kill(); } catch { }
            return err.Result + outp.Result;
        }
        catch { return null; }
    }

    /// <summary>
    /// The size of the monitor Red Star should fill: the primary monitor as the host's X server reports it (XRandR
    /// monitors; on Wayland that is Xwayland's view of the outputs), else the first connected screen's native
    /// mode from the kernel (/sys/class/drm).
    /// </summary>
    private static (int W, int H) HostScreenSize()
    {
        try
        {
            if (Env("DISPLAY") != null)
            {
                var d = XOpenDisplay(null);
                if (d != IntPtr.Zero)
                {
                    try
                    {
                        int w = 0, h = 0;
                        try
                        {
                            var p = XRRGetMonitors(d, XDefaultRootWindow(d), 1, out int count);
                            if (p != IntPtr.Zero)
                            {
                                for (int i = 0; i < count; i++)
                                {
                                    var m = p + i * 56;                       // sizeof(XRRMonitorInfo) on 64-bit
                                    bool primary = Marshal.ReadInt32(m, 8) != 0;
                                    int mw = Marshal.ReadInt32(m, 28), mh = Marshal.ReadInt32(m, 32);
                                    if (primary || w == 0) { w = mw; h = mh; }
                                    if (primary) break;
                                }
                                XRRFreeMonitors(p);
                            }
                        }
                        catch (DllNotFoundException) { }
                        catch (EntryPointNotFoundException) { }
                        if (w <= 0 || h <= 0)
                        {
                            int scr = XDefaultScreen(d);
                            w = XDisplayWidth(d, scr); h = XDisplayHeight(d, scr);
                        }
                        if (w >= 640 && h >= 480) return (w, h);
                    }
                    finally { XCloseDisplay(d); }
                }
            }
        }
        catch { }
        try
        {
            foreach (var dir in Directory.GetDirectories("/sys/class/drm").OrderBy(x => x))
            {
                var status = Path.Combine(dir, "status");
                var modes = Path.Combine(dir, "modes");
                if (!File.Exists(status) || File.ReadAllText(status).Trim() != "connected" || !File.Exists(modes)) continue;
                var first = File.ReadLines(modes).FirstOrDefault()?.Trim();
                var parts = first?.Split('x');
                if (parts is { Length: 2 } && int.TryParse(parts[0], out var w) && int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), out var h))
                    return (w, h);
            }
        }
        catch { }
        return (0, 0);
    }

    /// <summary>
    /// A compositor makes transparent windows (rounded popups, shadows, the Alt+Tab panel) look right. Optional:
    /// picom or xcompmgr when installed; without one Red Star draws those parts opaque. REDSTAR_COMPOSITOR=off skips it.
    /// </summary>
    public static Process? StartCompositor(Dictionary<string, string?> env)
    {
        if (Env("REDSTAR_COMPOSITOR") == "off") return null;
        var gpuVal = env.TryGetValue("REDSTAR_GPU", out var gv) ? gv : Environment.GetEnvironmentVariable("REDSTAR_GPU");
        bool gpu = gpuVal == "1";
        // picom: the user's own picom.conf when there is one (their transparency, fades and other effects), else
        // Red Star's (fades, translucency allowed, soft drop shadows). Window shadows are on unless turned off in
        // Settings → Desktop & Taskbar. With redstar-wm only framed windows that aren't maximised or full screen get
        // one (the WM marks their frames with _REDSTAR_SHADOW); tooltips, menus, popups, the desktop, the taskbar
        // and see-through undecorated windows never do — a tooltip that isn't drawn yet would otherwise show as a
        // bare shadow. Settings → Accessibility → Reduce motion turns fades off.
        bool reduceMotion = SettingFlag("ReduceMotion");
        bool shadows = SettingFlag("WindowShadows", fallback: true);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cfgHome = Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        string? conf = new[]
        {
            Path.Combine(cfgHome, "picom", "picom.conf"), Path.Combine(cfgHome, "picom.conf"),
            Path.Combine(cfgHome, "compton.conf"), Path.Combine(home, ".compton.conf"), "/etc/xdg/picom.conf",
        }.FirstOrDefault(File.Exists);
        if (conf == null)
            try
            {
                var cfgDir = Path.Combine(cfgHome, "redstaros");
                Directory.CreateDirectory(cfgDir);
                conf = Path.Combine(cfgDir, "picom.conf");
                File.WriteAllText(conf, "# written by Crimson Banner OS (used when you have no picom.conf of your own)\n"
                    + "fading = true;\nfade-in-step = 0.06;\nfade-out-step = 0.06;\nfade-delta = 8;\n"
                    + "shadow = " + (shadows ? "true" : "false") + ";\nshadow-radius = 18;\nshadow-opacity = 0.32;\nshadow-offset-x = -18;\nshadow-offset-y = -12;\n"
                    + "detect-client-opacity = true;\nunredir-if-possible = false;\n"
                    + "vsync = " + (gpu ? "true" : "false") + ";\n");
            }
            catch { conf = null; }
        Log("compositor settings: " + (conf ?? "picom defaults") + (reduceMotion ? " (reduce motion: no fades)" : "")
            + (shadows ? ", window shadows" : ", no shadows"));
        string[] Picom(string backend, bool precise)
        {
            var a = new List<string>();
            if (conf != null) { a.Add("--config"); a.Add(conf); }
            a.Add("--backend"); a.Add(backend);
            if (shadows)
            {
                a.Add("-c");                                            // shadows on, even if the user's picom.conf has them off
                if (precise && OwnWindowManager)
                { a.Add("--shadow-exclude"); a.Add("!_REDSTAR_SHADOW@:c"); }   // only the frames redstar-wm marked
                else
                {
                    // another window manager: go by window type, and leave windows that draw their own shadow alone
                    a.Add("--shadow-exclude"); a.Add("_GTK_FRAME_EXTENTS@:c");
                    a.Add("--shadow-exclude"); a.Add("_NET_WM_STATE:32a *= '_NET_WM_STATE_FULLSCREEN'");
                    foreach (var t in new[] { "desktop", "dock" }) { a.Add("--shadow-exclude"); a.Add($"window_type = '{t}'"); }
                }
            }
            else { a.Add("--shadow-exclude"); a.Add("x >= -100000"); }      // turned off: a rule every window matches
            foreach (var t in new[] { "tooltip", "menu", "popup_menu", "dropdown_menu", "notification", "dnd", "combo", "splash" })
            { a.Add("--shadow-exclude"); a.Add($"window_type = '{t}'"); }
            if (reduceMotion) a.Add("--no-fading-openclose");
            return a.ToArray();
        }
        var options = new List<(string Name, string[] Args)>();
        if (gpu) options.Add(("picom", Picom("glx", true)));           // composited on the GPU
        options.Add(("picom", Picom("xrender", true)));                // CPU, works everywhere
        options.Add(("picom", Picom("xrender", false)));               // an older picom that doesn't take the precise rule
        options.Add(("xcompmgr", Array.Empty<string>()));              // no shadows: it can't leave out tooltips and menus
        foreach (var (name, args) in options)
        {
            if (Which(name) is not { } exe) continue;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            foreach (var (k, v) in env) if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
            try
            {
                var p = Process.Start(psi);
                if (p == null) continue;
                p.ErrorDataReceived += (_, _) => { }; p.BeginErrorReadLine();
                p.OutputDataReceived += (_, _) => { }; p.BeginOutputReadLine();
                Thread.Sleep(200);
                if (p.HasExited) continue;                 // e.g. an old picom that doesn't know these options
                Log($"compositor: {name} {string.Join(' ', args)}");
                return p;
            }
            catch { }
        }
        Log("no compositor (picom / xcompmgr) installed: popups are drawn opaque");
        return null;
    }

    /// <summary>
    /// Starts the window manager on the display in <paramref name="env"/>: Red Star's own (redstar-wm) unless
    /// REDSTAR_WM names another; then openbox (minus its Alt+Tab, which Red Star draws itself) or the first one found.
    /// </summary>
    /// <summary>redstar-wm is running (it marks which windows get a shadow).</summary>
    public static bool OwnWindowManager
    {
        get
        {
            if (WindowManagerHost.BuiltinStarted) return true;
            try { return Process.GetProcessesByName("redstar-wm").Length > 0; } catch { return false; }
        }
    }

    public static Process? StartWindowManager(Dictionary<string, string?> env)
    {
        Process? Run(string exe, params string[] a)
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var x in a) psi.ArgumentList.Add(x);
            foreach (var (k, v) in env) if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
            try { return Process.Start(psi); } catch { return null; }
        }
        var choice = Env("REDSTAR_WM");
        if ((choice == null || choice == "builtin" || choice == "redstar-wm") && WindowManagerHost.StartBuiltin(env) is { } own)
        {
            Log("window manager: redstar-wm");
            return own;
        }
        if (choice != null && choice != "builtin" && choice != "openbox" && Which(choice) is { } named) { Log("window manager: " + choice); return Run(named); }
        if (Which("openbox") is { } ob)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var rc = new[] { Path.Combine(home, ".config/openbox/rc.xml"), "/etc/xdg/openbox/rc.xml", "/etc/openbox/rc.xml" }.FirstOrDefault(File.Exists);
            if (rc != null)
            {
                try
                {
                    var xml = File.ReadAllText(rc);
                    foreach (var key in new[] { "A-Tab", "A-S-Tab" })
                    {
                        int s = xml.IndexOf($"<keybind key=\"{key}\">", StringComparison.Ordinal);
                        int e = s < 0 ? -1 : xml.IndexOf("</keybind>", s, StringComparison.Ordinal);
                        if (s >= 0 && e > s) xml = xml.Remove(s, e - s + "</keybind>".Length);
                    }
                    var cfg = Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
                    var outFile = Path.Combine(cfg, "redstaros", "openbox-rc.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
                    File.WriteAllText(outFile, xml);
                    return Run(ob, "--config-file", outFile);
                }
                catch { }
            }
            return Run(ob);
        }
        foreach (var wm in new[] { "icewm", "xfwm4", "marco", "metacity", "fluxbox" })
            if (Which(wm) is { } p) return Run(p);
        return null;
    }

    // ── Xephyr: its grab is toggled by Ctrl+Shift; send that once through XTest on the host display ──
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr d);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr d);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(IntPtr d);
    [DllImport("libX11.so.6")] private static extern int XDisplayWidth(IntPtr d, int screen);
    [DllImport("libX11.so.6")] private static extern int XDisplayHeight(IntPtr d, int screen);
    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetMonitors(IntPtr d, IntPtr window, int getActive, out int count);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(IntPtr monitors);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr d);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr d, IntPtr keysym);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(IntPtr d, uint keycode, bool press, UIntPtr delay);

    private static void XephyrGrab()
    {
        try
        {
            var d = XOpenDisplay(null);                // the host display (DISPLAY of this, the outer, process)
            if (d == IntPtr.Zero) return;
            uint ctrl = XKeysymToKeycode(d, (IntPtr)0xffe3), shift = XKeysymToKeycode(d, (IntPtr)0xffe1);
            if (ctrl != 0 && shift != 0)
            {
                XTestFakeKeyEvent(d, ctrl, true, UIntPtr.Zero);
                XTestFakeKeyEvent(d, shift, true, UIntPtr.Zero);
                XTestFakeKeyEvent(d, shift, false, UIntPtr.Zero);
                XTestFakeKeyEvent(d, ctrl, false, UIntPtr.Zero);
                XFlush(d);
            }
            XCloseDisplay(d);
        }
        catch (Exception ex) { Log("automatic Xephyr grab unavailable: " + ex.Message + " (press Ctrl+Shift)"); }
    }

    private static void Log(string msg)
    {
        try
        {
            var cfg = Env("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            Directory.CreateDirectory(Path.Combine(cfg, "redstaros"));
            File.AppendAllText(Path.Combine(cfg, "redstaros", "session.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\n");
        }
        catch { }
        Console.Error.WriteLine("[Crimson Banner OS] " + msg);
    }
}
