using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace RedStarOS.Services;

/// <summary>
/// Red Star's own window manager (wm/redstar-wm.c → the "redstar-wm" program next to RedStarOS): title bars,
/// moving, resizing, maximise, full screen and minimise. It runs inside Red Star's own display
/// (keyboard/mouse isolation) and in the Red Star session, so windows always have title bars even when no other
/// window manager is installed. REDSTAR_WM=openbox (or another name) picks a different one.
/// </summary>
public static class WindowManagerHost
{
    /// <summary>The built-in window manager program, when it was shipped / built.</summary>
    public static string? BuiltinPath()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "" })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var p = Path.Combine(dir, "redstar-wm");
            if (!File.Exists(p)) continue;
            try
            {
                // MSBuild copies may lose the executable bit
                var mode = File.GetUnixFileMode(p);
                if ((mode & UnixFileMode.UserExecute) == 0)
                    File.SetUnixFileMode(p, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }
            catch { }
            return p;
        }
        // installed from the Arch package (/usr/bin/redstar-wm)
        return ProcessUtil.Which("redstar-wm");
    }

    private static ProcessStartInfo Psi(string exe, IDictionary<string, string?>? env, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
            foreach (var (k, v) in env)
                if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        return psi;
    }

    /// <summary>True when no window manager runs on the display (the environment's DISPLAY).</summary>
    public static bool NoneRunning(IDictionary<string, string?>? env = null)
    {
        var wm = BuiltinPath();
        if (wm == null) return false;
        try
        {
            using var p = Process.Start(Psi(wm, env, "--check"))!;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>This process started redstar-wm (on its own display or for the session).</summary>
    public static bool BuiltinStarted { get; private set; }

    /// <summary>Starts redstar-wm on the display given by <paramref name="env"/> (or this process's).</summary>
    public static Process? StartBuiltin(IDictionary<string, string?>? env = null)
    {
        var wm = BuiltinPath();
        if (wm == null) return null;
        try
        {
            var psi = Psi(wm, env);
            psi.RedirectStandardError = true;           // its messages (and REDSTAR_WM_DEBUG=1 click/focus log) → session.log
            var p = Process.Start(psi);
            if (p == null) return null;
            var log = AppPaths.ConfigFile("session.log");
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                try { lock (LogLock) File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss.fff} {e.Data}\n"); } catch { }
            };
            p.BeginErrorReadLine();
            BuiltinStarted = true;
            return p;
        }
        catch { return null; }
    }

    private static readonly object LogLock = new();

    /// <summary>
    /// In the Red Star session (REDSTAR_SESSION=1): when nothing manages windows yet, start redstar-wm for the
    /// lifetime of the shell.
    /// </summary>
    public static void EnsureForSession()
    {
        if (Environment.GetEnvironmentVariable("REDSTAR_SESSION") != "1") return;
        if (InputIsolation.InRedStarComp) return;           // redstar-comp manages windows and composites itself
        if (Environment.GetEnvironmentVariable("REDSTAR_GPU") == null)
            Environment.SetEnvironmentVariable("REDSTAR_GPU", Graphics.GpuFlagFor(null));   // apps started from here inherit it
        var started = new List<Process>();
        if (NoneRunning() && StartBuiltin() is { } wm) { started.Add(wm); System.Threading.Thread.Sleep(200); }
        // transparent popups need a compositor; start one when it's installed and none is running
        if (!X11Props.CompositorRunning(fresh: true) && InputIsolation.StartCompositor(new Dictionary<string, string?>()) is { } comp)
            started.Add(comp);
        if (started.Count > 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { foreach (var p in started) try { if (!p.HasExited) p.Kill(); } catch { } };
    }
}

/// <summary>Small X11 property writes the shell needs before its windows are shown (own connection, no Avalonia).</summary>
public static class X11Props
{
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr d);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr d, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XChangeProperty(IntPtr d, IntPtr w, IntPtr prop, IntPtr type, int format, int mode, long[] data, int n);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr d);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetSelectionOwner(IntPtr d, IntPtr selection);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(IntPtr d);

    private const int PropModeReplace = 0;
    private static readonly IntPtr XA_ATOM = (IntPtr)4, XA_CARDINAL = (IntPtr)6;

    private static void With(Action<IntPtr> act)
    {
        // drawn natively on Wayland, Red Star's windows aren't X11 windows: an X11 call on them would be fatal
        if (WaylandShell.Active) return;
        try
        {
            var d = XOpenDisplay(IntPtr.Zero);
            if (d == IntPtr.Zero) return;
            try { act(d); XFlush(d); } finally { XCloseDisplay(d); }
        }
        catch { }
    }

    private static bool? _composited;

    /// <summary>Whether a compositor runs on this display (it owns _NET_WM_CM_S&lt;screen&gt;): transparent windows work.</summary>
    public static bool CompositorRunning(bool fresh = false)
    {
        if (!fresh && _composited is { } known) return known;
        bool on = true;     // when in doubt, assume the normal (composited) desktop
        With(d => on = XGetSelectionOwner(d, XInternAtom(d, "_NET_WM_CM_S" + XDefaultScreen(d), false)) != IntPtr.Zero);
        if (!fresh) _composited = on;      // a fresh look (before starting one) must not stick
        return on;
    }

    /// <summary>Marks the Red Star desktop as the desktop: below every window, full screen.</summary>
    public static void MarkDesktop(IntPtr window) => With(d =>
    {
        var desktop = XInternAtom(d, "_NET_WM_WINDOW_TYPE_DESKTOP", false);
        XChangeProperty(d, window, XInternAtom(d, "_NET_WM_WINDOW_TYPE", false), XA_ATOM, 32, PropModeReplace, new[] { (long)desktop }, 1);
        XChangeProperty(d, window, XInternAtom(d, "_NET_WM_DESKTOP", false), XA_CARDINAL, 32, PropModeReplace, new[] { 0xFFFFFFFFL }, 1);
    });

    /// <summary>Reserves the taskbar's height at the bottom so maximised and snapped windows stop above it.</summary>
    public static void SetBottomStrut(IntPtr window, int heightPx, int screenWidthPx) => With(d =>
    {
        var partial = new long[] { 0, 0, 0, heightPx, 0, 0, 0, 0, 0, 0, 0, Math.Max(0, screenWidthPx - 1) };
        XChangeProperty(d, window, XInternAtom(d, "_NET_WM_STRUT_PARTIAL", false), XA_CARDINAL, 32, PropModeReplace, partial, 12);
        XChangeProperty(d, window, XInternAtom(d, "_NET_WM_STRUT", false), XA_CARDINAL, 32, PropModeReplace, new long[] { 0, 0, 0, heightPx }, 4);
    });
}
