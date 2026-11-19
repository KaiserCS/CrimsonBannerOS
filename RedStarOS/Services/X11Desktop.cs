using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Desktop features that need the X server (the shell runs on X11 under a small window manager such as openbox):
/// global shortcuts (Super, Alt+Tab...), the list of every window (also non-Red Star apps), activating / moving /
/// and snapping them — all through the standard EWMH hints, so it works with any EWMH window manager.
/// Everything runs on one background thread with its own X connection. On Wayland (no X server) it stays off and
/// the window manager's own shortcuts apply.
/// Inside redstar-comp the compositor adds what X11 can't see (<see cref="CompIpc"/>): native Wayland windows join
/// the list, and the shortcuts come from the compositor, so they work whichever window has the keyboard.
/// </summary>
public static class X11Desktop
{
    // ── Xlib ──
    private const string Lib = "libX11.so.6";
    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] private static extern IntPtr XDefaultRootWindow(IntPtr d);
    [DllImport(Lib)] private static extern int XDefaultScreen(IntPtr d);
    [DllImport(Lib)] private static extern IntPtr XInternAtom(IntPtr d, string name, bool onlyIfExists);
    [DllImport(Lib)] private static extern int XGetWindowProperty(IntPtr d, IntPtr w, IntPtr prop, IntPtr offset, IntPtr length, bool delete,
        IntPtr reqType, out IntPtr actualType, out int actualFormat, out IntPtr nitems, out IntPtr bytesAfter, out IntPtr data);
    [DllImport(Lib)] private static extern int XFree(IntPtr data);
    [DllImport(Lib)] private static extern int XSendEvent(IntPtr d, IntPtr w, bool propagate, IntPtr mask, ref XClientMessage ev);
    [DllImport(Lib)] private static extern int XGrabKey(IntPtr d, int keycode, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] private static extern int XGrabKeyboard(IntPtr d, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode, IntPtr time);
    [DllImport(Lib)] private static extern int XUngrabKeyboard(IntPtr d, IntPtr time);
    [DllImport(Lib)] private static extern byte XKeysymToKeycode(IntPtr d, IntPtr keysym);
    [DllImport(Lib)] private static extern IntPtr XStringToKeysym(string s);
    [DllImport(Lib)] private static extern int XPending(IntPtr d);
    [DllImport(Lib)] private static extern int XNextEvent(IntPtr d, IntPtr ev);
    [DllImport(Lib)] private static extern int XFlush(IntPtr d);
    [DllImport(Lib)] private static extern int XSync(IntPtr d, bool discard);
    [DllImport(Lib)] private static extern int XConnectionNumber(IntPtr d);
    [DllImport(Lib)] private static extern int XIconifyWindow(IntPtr d, IntPtr w, int screen);
    [DllImport(Lib)] private static extern IntPtr XSetErrorHandler(XErrorHandler handler);
    [DllImport(Lib)] private static extern int XSelectInput(IntPtr d, IntPtr w, IntPtr mask);
    [DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);

    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);
    [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short REvents; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessage
    {
        public int type; public IntPtr serial; public int sendEvent; public IntPtr display; public IntPtr window;
        public IntPtr messageType; public int format;
        public IntPtr l0, l1, l2, l3, l4;
        public IntPtr pad0, pad1, pad2, pad3, pad4, pad5, pad6, pad7, pad8, pad9, pad10, pad11;   // XEvent is 24 longs
    }

    private const int KeyPress = 2, KeyRelease = 3, ClientMessage = 33, PropertyNotify = 28, DestroyNotify = 17;
    private static readonly IntPtr PropertyChangeMask = (IntPtr)(1L << 22), StructureNotifyMask = (IntPtr)(1L << 17);
    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
    private const int GrabModeAsync = 1;
    private static readonly IntPtr SubstructureMask = (IntPtr)(0x00080000 | 0x00100000);

    // ── public surface ──
    public sealed record XWin(long Id, string Title, string WmClass, int Desktop, bool Minimized);

    /// <summary>A window of another program, as the taskbar needs it.</summary>
    public sealed record ForeignWindow(long Id, string Title, string Instance, string Class, int Pid, bool Minimized, int IconVersion);

    /// <summary>Raised (UI thread) when other programs' windows, their titles/state or the active window change.</summary>
    public static event Action<IReadOnlyList<ForeignWindow>, long>? WindowsChanged;

    /// <summary>The newest window icon read for a window: width, height, BGRA pixels (straight alpha).</summary>
    public static (int W, int H, byte[] Pixels)? IconFor(long window) => _icons.TryGetValue(window, out var i) ? i : null;

    /// <summary>True once connected to an X server with an EWMH window manager.</summary>
    public static bool Available { get; private set; }

    /// <summary>Raised (on the UI thread) for global shortcuts: "start", "switcher-next", "switcher-prev",
    /// "switcher-commit", "switcher-cancel", "clipboard", "desktop", "files", "terminal", "lock", "snap-left",
    /// "snap-right", "maximize", "minimize", "search", "screenshot", "screenshot-window" (inside redstar-comp followed by
    /// a tab and the window's geometry), "power-menu" (Ctrl+Alt+Delete), "volume-up", "volume-down", "volume-mute".</summary>
    public static event Action<string>? Shortcut;

    private static IntPtr _d, _root;
    private static Thread? _thread;
    private static readonly ConcurrentQueue<Action> _jobs = new();
    private static XErrorHandler? _errorHandler;     // kept alive: Xlib holds a pointer to it
    private static bool _switching;                  // Alt+Tab switcher open (keyboard grabbed)
    private static bool _superDown, _superUsed;
    private static int _kcSuperL, _kcSuperR, _kcAltL, _kcAltR, _kcTab, _kcEsc, _kcPrint, _kcDelete, _kcKpDelete,
        _kcVolUp, _kcVolDown, _kcMute;
    private static readonly Dictionary<int, string> _keyNames = new();
    private static readonly ConcurrentDictionary<long, (int W, int H, byte[] Pixels)> _icons = new();
    private static readonly Dictionary<long, int> _iconVersion = new();
    private static readonly HashSet<long> _watched = new();
    private static bool _windowsDirty = true;
    private static long _lastWindowsScan;

    public static void Start()
    {
        if (_thread != null) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !OperatingSystem.IsLinux()) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "x11-desktop" };
        _thread.Start();
    }

    private static void Post(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);
    private static void Raise(string s) => Post(() => Shortcut?.Invoke(s));

    // ═════════════════════════════════ background thread ═════════════════════════════════
    private static void Run()
    {
        try
        {
            _d = XOpenDisplay(IntPtr.Zero);
            if (_d == IntPtr.Zero) return;
            _errorHandler = (_, _) => 0;          // e.g. BadAccess when the window manager already owns a shortcut
            XSetErrorHandler(_errorHandler);
            _root = XDefaultRootWindow(_d);
            Available = ReadCardinals(_root, "_NET_SUPPORTED") != null;
            if (InputIsolation.InRedStarComp && CompIpc.Start())
            {
                CompIpc.Key += Raise;
                CompIpc.Changed += () => _jobs.Enqueue(() => _windowsDirty = true);
                CompIpc.Send("shortcuts on");        // Super, Alt+Tab, Ctrl+Alt+T: from the compositor, not X key grabs
                Available = true;
            }
            else GrabKeys();
            XSelectInput(_d, _root, PropertyChangeMask);     // client list / active window changes → taskbar
            int fd = XConnectionNumber(_d);
            var pfd = new PollFd[1];
            IntPtr ev = Marshal.AllocHGlobal(256);
            while (true)
            {
                while (_jobs.TryDequeue(out var job)) { try { job(); } catch { } }
                XFlush(_d);
                while (XPending(_d) > 0)
                {
                    XNextEvent(_d, ev);
                    int type = Marshal.ReadInt32(ev, 0);
                    if (type == KeyPress || type == KeyRelease)
                        OnKey(type == KeyPress, (uint)Marshal.ReadInt32(ev, 80), Marshal.ReadInt32(ev, 84));
                    else if (type == PropertyNotify)
                        OnProperty(Marshal.ReadIntPtr(ev, 32).ToInt64(), Marshal.ReadIntPtr(ev, 40));
                    else if (type == DestroyNotify)
                    {
                        long gone = Marshal.ReadIntPtr(ev, 40).ToInt64();
                        _watched.Remove(gone); _icons.TryRemove(gone, out _); _iconVersion.Remove(gone);
                        _windowsDirty = true;
                    }
                }
                pfd[0].Fd = fd; pfd[0].Events = 1; pfd[0].REvents = 0;
                poll(pfd, 1, 100);
                // property changes come in bursts (a title typed letter by letter): scan at most ~6 times a second
                if (_windowsDirty && Environment.TickCount64 - _lastWindowsScan > 150 && WindowsChanged != null) ScanWindows();
            }
        }
        catch (DllNotFoundException) { Available = false; }
        catch (Exception ex) { Available = false; Console.Error.WriteLine("x11-desktop: " + ex.Message); }
    }

    private static int Kc(string keysym) => XKeysymToKeycode(_d, XStringToKeysym(keysym));

    private static void Grab(int keycode, uint mods)
    {
        if (keycode == 0) return;
        foreach (var extra in new uint[] { 0, LockMask, Mod2Mask, LockMask | Mod2Mask })   // Caps Lock / Num Lock variations
            XGrabKey(_d, keycode, mods | extra, _root, false, GrabModeAsync, GrabModeAsync);
    }

    private static void GrabKeys()
    {
        _kcSuperL = Kc("Super_L"); _kcSuperR = Kc("Super_R");
        _kcAltL = Kc("Alt_L"); _kcAltR = Kc("Alt_R");
        _kcTab = Kc("Tab"); _kcEsc = Kc("Escape");
        foreach (var name in new[] { "Left", "Right", "Up", "Down", "v", "d", "e", "l", "t", "space", "s", "1", "2", "3", "4", "5", "6", "7", "8", "9" })
        {
            int kc = Kc(name);
            if (kc != 0) _keyNames[kc] = name;
        }
        // Super alone (Start menu) — holding Super then grabs the keyboard, so Super+key combos arrive here too
        Grab(_kcSuperL, 0); Grab(_kcSuperR, 0);
        Grab(_kcSuperL, ShiftMask); Grab(_kcSuperR, ShiftMask);
        // Alt+Tab / Alt+Shift+Tab
        Grab(_kcTab, Mod1Mask); Grab(_kcTab, Mod1Mask | ShiftMask);
        // Ctrl+Alt+T: terminal
        Grab(Kc("t"), ControlMask | Mod1Mask);
        // Print / Shift+Print: screenshot of the screen / the active window
        _kcPrint = Kc("Print");
        Grab(_kcPrint, 0); Grab(_kcPrint, ShiftMask);
        // Ctrl+Alt+Delete: the power menu
        _kcDelete = Kc("Delete"); _kcKpDelete = Kc("KP_Delete");
        Grab(_kcDelete, ControlMask | Mod1Mask); Grab(_kcKpDelete, ControlMask | Mod1Mask);
        // the keyboard's volume keys (also with Shift/Ctrl held)
        _kcVolUp = Kc("XF86AudioRaiseVolume"); _kcVolDown = Kc("XF86AudioLowerVolume"); _kcMute = Kc("XF86AudioMute");
        foreach (var kc in new[] { _kcVolUp, _kcVolDown, _kcMute })
            foreach (var m in new uint[] { 0, ShiftMask, ControlMask }) Grab(kc, m);
        XSync(_d, false);
    }

    private static void OnKey(bool press, uint state, int keycode)
    {
        bool ctrl = (state & ControlMask) != 0, alt = (state & Mod1Mask) != 0, shift = (state & ShiftMask) != 0;
        _keyNames.TryGetValue(keycode, out var name);

        // ── Alt+Tab switcher ──
        if (_switching)
        {
            if (!press && (keycode == _kcAltL || keycode == _kcAltR)) { EndSwitch(); Raise("switcher-commit"); return; }
            if (press && keycode == _kcEsc) { EndSwitch(); Raise("switcher-cancel"); return; }
            if (press && (keycode == _kcTab || name == "Right" || name == "Down")) Raise(shift && keycode == _kcTab ? "switcher-prev" : "switcher-next");
            else if (press && (name == "Left" || name == "Up")) Raise("switcher-prev");
            return;
        }
        if (press && keycode == _kcTab && alt)
        {
            _switching = XGrabKeyboard(_d, _root, false, GrabModeAsync, GrabModeAsync, IntPtr.Zero) == 0;
            Raise(shift ? "switcher-open-prev" : "switcher-open");
            if (!_switching) Raise("switcher-commit");      // couldn't hold the keyboard: switch right away
            return;
        }

        // ── Super ──
        if (keycode == _kcSuperL || keycode == _kcSuperR)
        {
            if (press) { _superDown = true; _superUsed = false; }
            else
            {
                if (_superDown && !_superUsed) Raise("start");
                _superDown = false;
            }
            return;
        }
        if (press && ((state & Mod4Mask) != 0 || _superDown))
        {
            _superUsed = true;
            switch (name)
            {
                case "Left": Raise("snap-left"); return;
                case "Right": Raise("snap-right"); return;
                case "Up": Raise("maximize"); return;
                case "Down": Raise("minimize"); return;
                case "v": Raise("clipboard"); return;
                case "d": Raise("desktop"); return;
                case "e": Raise("files"); return;
                case "l": Raise("lock"); return;
                case "t": Raise("terminal"); return;
                case "s": case "space": Raise("search"); return;
                default: return;
            }
        }

        // ── Ctrl+Alt ──
        if (press && ctrl && alt && name == "t") Raise("terminal");
        if (press && keycode == _kcPrint && _kcPrint != 0) Raise(shift ? "screenshot-window" : "screenshot");
        if (press && ctrl && alt && keycode != 0 && (keycode == _kcDelete || keycode == _kcKpDelete)) Raise("power-menu");
        if (press && keycode != 0)
        {
            if (keycode == _kcVolUp) Raise("volume-up");
            else if (keycode == _kcVolDown) Raise("volume-down");
            else if (keycode == _kcMute) Raise("volume-mute");
        }
    }

    private static void EndSwitch()
    {
        _switching = false;
        XUngrabKeyboard(_d, IntPtr.Zero);
        XFlush(_d);
    }

    /// <summary>The switcher was closed from the UI (mouse click): release the keyboard.</summary>
    public static void CancelSwitcher()
    {
        if (CompIpc.Connected) CompIpc.Send("switcher-end");
        _jobs.Enqueue(() => { if (_switching) EndSwitch(); });
    }

    // ── properties ──
    private static IntPtr Atom(string name) => XInternAtom(_d, name, false);

    // ═════════════════════════════════ other programs' windows (taskbar) ═════════════════════════════════
    private static IntPtr _aClientList, _aActive, _aName, _aWmName, _aState, _aIcon;

    private static void OnProperty(long window, IntPtr atom)
    {
        if (_aClientList == IntPtr.Zero)
        {
            _aClientList = Atom("_NET_CLIENT_LIST"); _aActive = Atom("_NET_ACTIVE_WINDOW"); _aName = Atom("_NET_WM_NAME");
            _aWmName = Atom("WM_NAME"); _aState = Atom("_NET_WM_STATE"); _aIcon = Atom("_NET_WM_ICON");
        }
        if (window == _root.ToInt64())
        {
            if (atom == _aClientList || atom == _aActive) _windowsDirty = true;
            return;
        }
        if (atom == _aName || atom == _aWmName || atom == _aState) _windowsDirty = true;
        else if (atom == _aIcon) { _iconVersion.Remove(window); _windowsDirty = true; }   // re-read the icon
    }

    private static readonly int OwnPid = Environment.ProcessId;

    private static void ScanWindows()
    {
        _windowsDirty = false;
        _lastWindowsScan = Environment.TickCount64;
        var ids = ReadCardinals(_root, "_NET_CLIENT_LIST") ?? Array.Empty<long>();
        long active = ReadCardinals(_root, "_NET_ACTIVE_WINDOW")?[0] ?? 0;
        if (CompIpc.Connected && CompIpc.IsNative(CompIpc.Active)) active = CompIpc.Active;
        var skipTypes = new HashSet<long>();
        foreach (var t in new[] { "_NET_WM_WINDOW_TYPE_DOCK", "_NET_WM_WINDOW_TYPE_DESKTOP", "_NET_WM_WINDOW_TYPE_SPLASH", "_NET_WM_WINDOW_TYPE_TOOLBAR",
                     "_NET_WM_WINDOW_TYPE_MENU", "_NET_WM_WINDOW_TYPE_NOTIFICATION", "_NET_WM_WINDOW_TYPE_TOOLTIP", "_NET_WM_WINDOW_TYPE_POPUP_MENU",
                     "_NET_WM_WINDOW_TYPE_DROPDOWN_MENU", "_NET_WM_WINDOW_TYPE_UTILITY", "_NET_WM_WINDOW_TYPE_DIALOG" })
            skipTypes.Add(Atom(t).ToInt64());
        long skipTaskbar = Atom("_NET_WM_STATE_SKIP_TASKBAR").ToInt64(), hidden = Atom("_NET_WM_STATE_HIDDEN").ToInt64();
        var list = new List<ForeignWindow>();
        var seen = new HashSet<long>();
        foreach (var id in ids)
        {
            var w = (IntPtr)id;
            seen.Add(id);
            if (_watched.Add(id)) XSelectInput(_d, w, PropertyChangeMask | StructureNotifyMask);   // titles, state, icon, closing
            int pid = (int)(ReadCardinals(w, "_NET_WM_PID")?[0] ?? 0);
            if (pid == OwnPid) continue;                                            // Red Star's own windows have their own entries
            var types = ReadCardinals(w, "_NET_WM_WINDOW_TYPE");
            if (types != null && Array.Exists(types, t => skipTypes.Contains(t))) continue;
            if (ReadCardinals(w, "WM_TRANSIENT_FOR") is { Length: > 0 } tf && tf[0] != 0) continue;   // dialogs belong to their window
            var states = ReadCardinals(w, "_NET_WM_STATE") ?? Array.Empty<long>();
            if (Array.IndexOf(states, skipTaskbar) >= 0) continue;
            var title = ReadString(w, "_NET_WM_NAME") ?? ReadString(w, "WM_NAME") ?? "";
            var cls = (ReadString(w, "WM_CLASS") ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (!_iconVersion.ContainsKey(id)) { ReadIcon(w, id); _iconVersion[id] = Environment.TickCount; }
            list.Add(new ForeignWindow(id, title, cls.Length > 0 ? cls[0] : "", cls.Length > 1 ? cls[1] : (cls.Length > 0 ? cls[0] : ""),
                pid, Array.IndexOf(states, hidden) >= 0, _iconVersion[id]));
        }
        if (CompIpc.Connected)                       // native Wayland windows, from redstar-comp
            foreach (var n in CompIpc.Windows.Values)
                if (n.Pid != OwnPid)
                    list.Add(new ForeignWindow(n.Id, n.Title, n.AppId, n.AppId, n.Pid, n.Minimized, 0));
        foreach (var gone in _watched.Where(x => !seen.Contains(x)).ToList())
        {
            _watched.Remove(gone); _icons.TryRemove(gone, out _); _iconVersion.Remove(gone);
        }
        var handler = WindowsChanged;
        if (handler != null) Post(() => handler(list, active));
    }

    /// <summary>_NET_WM_ICON: several sizes of ARGB cardinals; keeps the one closest to 48 px (≥ 32 preferred).</summary>
    private static void ReadIcon(IntPtr w, long id)
    {
        if (XGetWindowProperty(_d, w, Atom("_NET_WM_ICON"), IntPtr.Zero, (IntPtr)(1 << 20), false, IntPtr.Zero,
                out _, out int format, out IntPtr n, out _, out IntPtr data) != 0 || data == IntPtr.Zero) return;
        try
        {
            if (format != 32) return;
            long count = n.ToInt64(), i = 0;
            long bestAt = -1; int bw = 0, bh = 0, bestScore = int.MaxValue;
            while (i + 2 <= count)
            {
                int iw = (int)Marshal.ReadIntPtr(data, (int)(i * IntPtr.Size)).ToInt64();
                int ih = (int)Marshal.ReadIntPtr(data, (int)((i + 1) * IntPtr.Size)).ToInt64();
                if (iw <= 0 || ih <= 0 || iw > 1024 || ih > 1024 || i + 2 + (long)iw * ih > count) break;
                int score = iw >= 32 ? Math.Abs(iw - 48) : 1000 + (48 - iw);
                if (score < bestScore) { bestScore = score; bestAt = i + 2; bw = iw; bh = ih; }
                i += 2 + (long)iw * ih;
            }
            if (bestAt < 0) return;
            var px = new byte[bw * bh * 4];
            for (int k = 0; k < bw * bh; k++)
            {
                uint argb = (uint)Marshal.ReadIntPtr(data, (int)((bestAt + k) * IntPtr.Size)).ToInt64();
                px[k * 4] = (byte)argb; px[k * 4 + 1] = (byte)(argb >> 8); px[k * 4 + 2] = (byte)(argb >> 16); px[k * 4 + 3] = (byte)(argb >> 24);
            }
            _icons[id] = (bw, bh, px);
        }
        finally { XFree(data); }
    }

    /// <summary>Asks a window to close (like its own close button).</summary>
    public static void CloseWindow(long window)
    {
        if (CompIpc.IsNative(window)) { CompIpc.Close(window); return; }
        _jobs.Enqueue(() => SendRoot((IntPtr)window, "_NET_CLOSE_WINDOW", 0, 2));
    }

    /// <summary>Re-reads every window now (e.g. once the taskbar starts listening).</summary>
    public static void RefreshWindows() => _jobs.Enqueue(() => { _windowsDirty = true; ScanWindows(); });

    private static long[]? ReadCardinals(IntPtr w, string prop)
    {
        if (XGetWindowProperty(_d, w, Atom(prop), IntPtr.Zero, (IntPtr)4096, false, IntPtr.Zero,
                out _, out int format, out IntPtr n, out _, out IntPtr data) != 0 || data == IntPtr.Zero) return null;
        try
        {
            if (format != 32) return null;
            var r = new long[(int)n];
            for (int i = 0; i < r.Length; i++) r[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size).ToInt64();   // format 32 = C longs
            return r;
        }
        finally { XFree(data); }
    }

    private static string? ReadString(IntPtr w, string prop)
    {
        if (XGetWindowProperty(_d, w, Atom(prop), IntPtr.Zero, (IntPtr)1024, false, IntPtr.Zero,
                out _, out int format, out IntPtr n, out _, out IntPtr data) != 0 || data == IntPtr.Zero) return null;
        try
        {
            if (format != 8) return null;
            var bytes = new byte[(int)n];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { XFree(data); }
    }

    private static void SendRoot(IntPtr window, string type, long a = 0, long b = 0, long c = 0, long d = 0, long e = 0)
    {
        var ev = new XClientMessage
        {
            type = ClientMessage, sendEvent = 1, display = _d, window = window, messageType = Atom(type), format = 32,
            l0 = (IntPtr)a, l1 = (IntPtr)b, l2 = (IntPtr)c, l3 = (IntPtr)d, l4 = (IntPtr)e
        };
        XSendEvent(_d, _root, false, SubstructureMask, ref ev);
        XFlush(_d);
    }

    // ═════════════════════════════════ actions (thread-safe: queued to the X thread) ═════════════════════════════════
    /// <summary>Every normal window, most recently used first. Calls back on the UI thread.</summary>
    public static void ListWindows(Action<List<XWin>> done) => _jobs.Enqueue(() =>
    {
        var list = new List<XWin>();
        var ids = ReadCardinals(_root, "_NET_CLIENT_LIST_STACKING") ?? ReadCardinals(_root, "_NET_CLIENT_LIST") ?? Array.Empty<long>();
        var skipTypes = new HashSet<long>();
        foreach (var t in new[] { "_NET_WM_WINDOW_TYPE_DOCK", "_NET_WM_WINDOW_TYPE_DESKTOP", "_NET_WM_WINDOW_TYPE_SPLASH", "_NET_WM_WINDOW_TYPE_TOOLBAR",
                     "_NET_WM_WINDOW_TYPE_MENU", "_NET_WM_WINDOW_TYPE_NOTIFICATION", "_NET_WM_WINDOW_TYPE_TOOLTIP", "_NET_WM_WINDOW_TYPE_POPUP_MENU", "_NET_WM_WINDOW_TYPE_DROPDOWN_MENU" })
            skipTypes.Add(Atom(t).ToInt64());
        long skipTaskbar = Atom("_NET_WM_STATE_SKIP_TASKBAR").ToInt64(), hidden = Atom("_NET_WM_STATE_HIDDEN").ToInt64();
        for (int i = ids.Length - 1; i >= 0; i--)
        {
            var w = (IntPtr)ids[i];
            var types = ReadCardinals(w, "_NET_WM_WINDOW_TYPE");
            if (types != null && Array.Exists(types, t => skipTypes.Contains(t))) continue;
            var states = ReadCardinals(w, "_NET_WM_STATE") ?? Array.Empty<long>();
            if (Array.IndexOf(states, skipTaskbar) >= 0) continue;
            int desk = (int)(ReadCardinals(w, "_NET_WM_DESKTOP")?[0] ?? -1);
            var title = ReadString(w, "_NET_WM_NAME") ?? ReadString(w, "WM_NAME") ?? "";
            var cls = (ReadString(w, "WM_CLASS") ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            list.Add(new XWin(ids[i], title, cls.Length > 1 ? cls[1] : cls.Length > 0 ? cls[0] : "", desk, Array.IndexOf(states, hidden) >= 0));
        }
        if (CompIpc.Connected)
        {
            foreach (var n in CompIpc.Windows.Values) list.Add(new XWin(n.Id, n.Title, n.AppId, 0, n.Minimized));
            list = OrderByStack(list, CompIpc.Stack);
        }
        Post(() => done(list));
    });

    /// <summary>Most recently used first, as redstar-comp reports it; windows it doesn't list keep their order, last.</summary>
    public static List<XWin> OrderByStack(List<XWin> list, IReadOnlyList<long> stack)
    {
        if (stack.Count == 0) return list;
        var rank = new Dictionary<long, int>();
        for (int i = 0; i < stack.Count; i++) rank.TryAdd(stack[i], i);
        return list.Select((w, i) => (w, i)).OrderBy(t => rank.TryGetValue(t.w.Id, out var r) ? r : stack.Count + t.i)
            .Select(t => t.w).ToList();
    }

    public static void Activate(long window)
    {
        if (CompIpc.IsNative(window)) { CompIpc.Activate(window); return; }
        _jobs.Enqueue(() => SendRoot((IntPtr)window, "_NET_ACTIVE_WINDOW", 2));
    }

    public static void ActiveWindow(Action<long> done) => _jobs.Enqueue(() =>
    {
        long w = ReadCardinals(_root, "_NET_ACTIVE_WINDOW")?[0] ?? 0;
        if (CompIpc.Connected && CompIpc.IsNative(CompIpc.Active)) w = CompIpc.Active;
        Post(() => done(w));
    });

    /// <summary>Snaps any window (also non-Red Star apps): "left", "right", "maximize", "restore", "minimize".</summary>
    public static void SnapWindow(long window, string how)
    {
        if (CompIpc.IsNative(window)) { CompIpc.Snap(window, how); return; }
        SnapX11(window, how);
    }

    private static void SnapX11(long window, string how) => _jobs.Enqueue(() =>
    {
        if (window == 0) return;
        var w = (IntPtr)window;
        long add = 1, remove = 0;
        long maxV = Atom("_NET_WM_STATE_MAXIMIZED_VERT").ToInt64(), maxH = Atom("_NET_WM_STATE_MAXIMIZED_HORZ").ToInt64();
        switch (how)
        {
            case "minimize": XIconifyWindow(_d, w, XDefaultScreen(_d)); XFlush(_d); return;
            case "maximize": SendRoot(w, "_NET_WM_STATE", add, maxV, maxH, 2); return;
            case "restore": SendRoot(w, "_NET_WM_STATE", remove, maxV, maxH, 2); return;
        }
        var wa = ReadCardinals(_root, "_NET_WORKAREA");
        if (wa == null || wa.Length < 4) return;
        const int i = 0;                                      // the work area (one desktop)
        long x = wa[i], y = wa[i + 1], width = wa[i + 2], height = wa[i + 3];
        SendRoot(w, "_NET_WM_STATE", remove, maxV, maxH, 2);
        long half = width / 2;
        long nx = how == "left" ? x : x + half;
        // x/y place the frame (NorthWest gravity); width/height are the window's own size, so take the
        // title bar and borders (_NET_FRAME_EXTENTS: left, right, top, bottom) off the half-screen.
        var ext = ReadCardinals(w, "_NET_FRAME_EXTENTS");
        long el = 0, er = 0, et = 0, eb = 0;
        if (ext is { Length: >= 4 }) { el = ext[0]; er = ext[1]; et = ext[2]; eb = ext[3]; }
        // gravity NorthWest, x/y/width/height given, from a pager
        SendRoot(w, "_NET_MOVERESIZE_WINDOW", 1 | (0xF << 8) | (2 << 12), nx, y, Math.Max(100, half - el - er), Math.Max(100, height - et - eb));
    });
}
