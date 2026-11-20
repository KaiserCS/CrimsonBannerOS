// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>
/// What happens when nobody uses the computer (System Settings → Power): the screen turns off, the screen locks, and
/// later the computer sleeps. Inside redstar-comp the compositor counts the time without input ("idle-timeout") and
/// turns the screens off and on itself; under X11, Crimson Banner asks the X server how long it has been idle
/// (XScreenSaver extension) and turns the screen off with DPMS. Programs that play video keep the screen on: Wayland
/// programs through idle-inhibit, the rest through org.freedesktop.ScreenSaver.Inhibit on the session bus, which
/// Crimson Banner answers (browsers and video players use it).
/// </summary>
public static class IdleManager
{
    private static bool _started, _idle;
    private static DispatcherTimer? _sleepTimer, _x11Poll;
    private static readonly Dictionary<uint, (string App, string Reason, string Sender)> _inhibitors = new();
    private static readonly HashSet<string> _ownInhibits = new();
    private static uint _cookie;

    /// <summary>Programs keeping the screen on right now (for Settings).</summary>
    public static IReadOnlyList<string> Inhibitors
    {
        get { lock (_inhibitors) return _inhibitors.Values.Select(i => i.Reason.Length > 0 ? $"{i.App}: {i.Reason}" : i.App).Concat(_ownInhibits).ToList(); }
    }

    public static bool Inhibited { get { lock (_inhibitors) return _inhibitors.Count > 0 || _ownInhibits.Count > 0; } }

    /// <summary>Seconds without input before the first step (screen off, or sleep when the screen stays on); 0: never.</summary>
    public static int FirstStepSeconds(int screenOffMin, int sleepMin)
    {
        var steps = new[] { screenOffMin, sleepMin }.Where(m => m > 0).ToList();
        return steps.Count == 0 ? 0 : steps.Min() * 60;
    }

    /// <summary>Starts it (once, when Crimson Banner is the session; on top of another desktop that desktop does this).</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;
        CompIpc.IdleChanged += idle => Dispatcher.UIThread.Post(() => { if (idle) GoneIdle(); else CameBack(); });
        _ = StartScreenSaverServiceAsync();
        Apply();
    }

    /// <summary>After a change in Settings.</summary>
    public static void Apply()
    {
        if (!_started) return;
        var s = SettingsStore.Current;
        int first = FirstStepSeconds(s.ScreenOffMinutes, s.SleepMinutes);
        if (InputIsolation.InRedStarComp)
        {
            CompIpc.Send($"idle-timeout\t{first}");
            CompIpc.Send($"inhibit\t{(Inhibited ? 1 : 0)}");
        }
        else if (Environment.GetEnvironmentVariable("DISPLAY") != null)
        {
            // X11: the X server's own blanking off (we decide), and a poll of its idle time
            if (ProcessUtil.Which("xset") is { } xset) _ = ProcessUtil.RunAsync(xset, new[] { "s", "off", "-dpms" }, 5000);
            _x11Poll ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => PollX11());
            if (first > 0) _x11Poll.Start(); else _x11Poll.Stop();
        }
    }

    private static void GoneIdle()
    {
        if (_idle) return;
        _idle = true;
        var s = SettingsStore.Current;
        bool screenFirst = s.ScreenOffMinutes > 0 && (s.SleepMinutes == 0 || s.ScreenOffMinutes <= s.SleepMinutes);
        if (screenFirst)
        {
            if (s.LockOnScreenOff) ShellActions.Lock();
            ScreenOff();
            if (s.SleepMinutes > s.ScreenOffMinutes)
            {
                _sleepTimer?.Stop();
                _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(s.SleepMinutes - s.ScreenOffMinutes) };
                _sleepTimer.Tick += (_, _) => { _sleepTimer?.Stop(); if (_idle && !Inhibited) ShellActions.Suspend(); };
                _sleepTimer.Start();
            }
        }
        else if (s.SleepMinutes > 0) ShellActions.Suspend();
    }

    private static void CameBack()
    {
        _idle = false;
        _sleepTimer?.Stop();
    }

    private static void ScreenOff()
    {
        if (InputIsolation.InRedStarComp) CompIpc.Send("dpms\toff");      // back on at the first input, by redstar-comp
        else if (ProcessUtil.Which("xset") is { } xset) _ = ProcessUtil.RunAsync(xset, new[] { "dpms", "force", "off" }, 5000);
    }

    // ── X11: how long since the last input ──

    [StructLayout(LayoutKind.Sequential)]
    private struct XScreenSaverInfo { public IntPtr Window; public int State, Kind; public ulong TilOrSince, Idle, EventMask; }
    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr d);
    [DllImport("libXss.so.1")] private static extern IntPtr XScreenSaverAllocInfo();
    [DllImport("libXss.so.1")] private static extern int XScreenSaverQueryInfo(IntPtr d, IntPtr drawable, IntPtr info);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr p);
    private static IntPtr _dpy, _info;
    private static bool _noXss;

    /// <summary>Milliseconds since the last input on the X display, or -1 when the X server can't say.</summary>
    public static long X11IdleMs()
    {
        if (_noXss) return -1;
        try
        {
            if (_dpy == IntPtr.Zero) _dpy = XOpenDisplay(IntPtr.Zero);
            if (_dpy == IntPtr.Zero) return -1;
            if (_info == IntPtr.Zero) _info = XScreenSaverAllocInfo();
            if (XScreenSaverQueryInfo(_dpy, XDefaultRootWindow(_dpy), _info) == 0) return -1;
            return (long)Marshal.PtrToStructure<XScreenSaverInfo>(_info).Idle;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _noXss = true;
            Console.Error.WriteLine("idle: libXss isn't installed, so the screen doesn't turn off by itself under X11");
            return -1;
        }
    }

    private static void PollX11()
    {
        var s = SettingsStore.Current;
        int first = FirstStepSeconds(s.ScreenOffMinutes, s.SleepMinutes);
        long idle = X11IdleMs();
        if (idle < 0 || first == 0) return;
        if (idle >= first * 1000L && !Inhibited) GoneIdle();
        else if (idle < 5000 && _idle) CameBack();
    }

    // ── keeping the screen on ──

    /// <summary>Crimson Banner's own reasons (its video player) to keep the screen on.</summary>
    public static void Inhibit(string reason, bool on)
    {
        lock (_inhibitors) { if (on) _ownInhibits.Add(reason); else _ownInhibits.Remove(reason); }
        InhibitChanged();
    }

    private static void InhibitChanged()
    {
        if (InputIsolation.InRedStarComp) CompIpc.Send($"inhibit\t{(Inhibited ? 1 : 0)}");
    }

    private static async Task StartScreenSaverServiceAsync()
    {
        if (DBusConnection.SessionAddress() == null) return;
        try
        {
            var bus = await DBusConnection.ConnectAsync();
            bus.MethodCall = m => OnScreenSaverCall(bus, m);
            bus.Signal += m =>
            {
                // a program that kept the screen on went away without saying so
                if (m.Member == "NameOwnerChanged" && m.Body.Length >= 3 && m.Body[2] is "" && m.Body[0] is string gone)
                {
                    bool changed;
                    lock (_inhibitors)
                    {
                        var drop = _inhibitors.Where(i => i.Value.Sender == gone).Select(i => i.Key).ToList();
                        foreach (var k in drop) _inhibitors.Remove(k);
                        changed = drop.Count > 0;
                    }
                    if (changed) InhibitChanged();
                }
            };
            await bus.AddMatchAsync("type='signal',sender='org.freedesktop.DBus',interface='org.freedesktop.DBus',member='NameOwnerChanged'");
            uint got = await bus.RequestNameAsync("org.freedesktop.ScreenSaver");
            Console.Error.WriteLine(got is 1 or 4 ? "idle: programs can keep the screen on (org.freedesktop.ScreenSaver)"
                                                  : "idle: another desktop answers org.freedesktop.ScreenSaver");
        }
        catch (Exception ex) { Console.Error.WriteLine("idle: org.freedesktop.ScreenSaver unavailable: " + ex.Message); }
    }

    private static bool OnScreenSaverCall(DBusConnection bus, Message m)
    {
        if (m.Path is not ("/org/freedesktop/ScreenSaver" or "/ScreenSaver")) return false;
        switch (m.Member)
        {
            case "Inhibit":
            {
                uint id;
                lock (_inhibitors)
                {
                    id = ++_cookie;
                    _inhibitors[id] = (m.Body.ElementAtOrDefault(0) as string ?? "a program", m.Body.ElementAtOrDefault(1) as string ?? "", m.Sender ?? "");
                }
                bus.Reply(m, "u", id);
                InhibitChanged();
                return true;
            }
            case "UnInhibit":
            {
                lock (_inhibitors) if (m.Body.ElementAtOrDefault(0) is uint id) _inhibitors.Remove(id);
                bus.Reply(m);
                InhibitChanged();
                return true;
            }
            case "SimulateUserActivity":
                bus.Reply(m);
                return true;
            case "GetActive":
                Dispatcher.UIThread.Post(() => bus.Reply(m, "b", ShellActions.IsLocked));
                return true;
            case "Lock":
                Dispatcher.UIThread.Post(ShellActions.Lock);
                bus.Reply(m);
                return true;
            case "Introspect":
                bus.Reply(m, "s", "<node><interface name=\"org.freedesktop.ScreenSaver\"><method name=\"Inhibit\"><arg type=\"s\" direction=\"in\"/>" +
                    "<arg type=\"s\" direction=\"in\"/><arg type=\"u\" direction=\"out\"/></method><method name=\"UnInhibit\"><arg type=\"u\" direction=\"in\"/></method>" +
                    "<method name=\"SimulateUserActivity\"/><method name=\"GetActive\"><arg type=\"b\" direction=\"out\"/></method><method name=\"Lock\"/></interface></node>");
                return true;
        }
        return false;
    }
}
