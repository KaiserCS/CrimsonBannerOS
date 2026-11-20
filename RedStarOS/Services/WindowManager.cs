using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace RedStarOS.Services;

public enum SnapMode { Left, Right, Full, Restore }

/// <summary>
/// Tracks every open app window for the taskbar,
/// remembers window geometry and the pinned taskbar apps.
/// </summary>
public static class WindowManager
{
    public const int TaskbarHeight = 56;

    private static readonly List<TaskItem> _tasks = new();

    /// <summary>All taskbar entries, in the order they were opened.</summary>
    public static IReadOnlyList<TaskItem> Tasks => _tasks;
    public static Window? Desktop { get; set; }
    public static Window? Active { get; private set; }

    /// <summary>The set of windows, their titles or their state changed (taskbar rebuilds).</summary>
    public static event Action? Changed;
    public static void RaiseChanged() => Changed?.Invoke();

    private static Window? _lastDeactivated;
    private static long _lastDeactivatedAt;

    public static bool WasActiveJustNow(Window w) =>
        _lastDeactivated == w && Environment.TickCount64 - _lastDeactivatedAt < 500;

    public static T Open<T>(T window, string? appId = null) where T : Window
    {
        Register(window, appId ?? AppRegistry.IdForWindow(window));
        if (Desktop != null) window.Show(Desktop);
        else window.Show();
        return window;
    }

    // ───────────── other programs' windows ─────────────
    private static bool _foreignHooked;

    /// <summary>Starts listing other programs' windows on the taskbar (called once the X11 helper runs).</summary>
    public static void TrackForeignWindows()
    {
        if (_foreignHooked) return;
        _foreignHooked = true;
        X11Desktop.WindowsChanged += OnForeignWindows;
        X11Desktop.RefreshWindows();
        // natively on Wayland the minimised / active state of Red Star's own windows comes from redstar-comp
        if (WaylandShell.Active)
            CompIpc.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    // Red Star's own windows, in case a toolkit leaves out _NET_WM_PID
    private static readonly string OwnName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "RedStarOS");
    private static bool IsOwnClass(X11Desktop.ForeignWindow w) =>
        w.Class.Equals("RedStarOS", StringComparison.OrdinalIgnoreCase) || w.Instance.Equals("RedStarOS", StringComparison.OrdinalIgnoreCase)
        || (OwnName != "dotnet" && (w.Class.Equals(OwnName, StringComparison.OrdinalIgnoreCase) || w.Instance.Equals(OwnName, StringComparison.OrdinalIgnoreCase)));

    private static void OnForeignWindows(IReadOnlyList<X11Desktop.ForeignWindow> windows, long active)
    {
        // Red Star's own windows (same process) are already left out
        var nativeIds = new HashSet<long>(_tasks.OfType<NativeTask>()
            .Select(t => t.Window.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0));
        var wanted = windows.Where(w => !nativeIds.Contains(w.Id) && !IsOwnClass(w)).ToList();
        bool changed = false;
        var byId = _tasks.OfType<X11Task>().ToDictionary(t => t.Window);
        foreach (var w in wanted)
        {
            if (byId.Remove(w.Id, out var t)) changed |= t.Update(w, w.Id == active);
            else { var nt = new X11Task(w); nt.Update(w, w.Id == active); _tasks.Add(nt); changed = true; }
        }
        foreach (var gone in byId.Values) { _tasks.Remove(gone); changed = true; }
        if (changed) Changed?.Invoke();
    }

    private static void Register(Window w, string? appId)
    {
        WaylandShell.Name(w);                     // natively on Wayland: a stable identity for redstar-comp
        var key = w.GetType().Name;

        if (w.CanResize && SettingsStore.Current.Windows.TryGetValue(key, out var saved) && saved.W > 120 && saved.H > 80)
        {
            w.Width = saved.W;
            w.Height = saved.H;
        }

        w.Opened += (_, _) =>
        {
            if (SettingsStore.Current.Windows.TryGetValue(key, out var b) && OnAnyScreen(w, b))
                w.Position = new PixelPoint(b.X, b.Y);
        };
        w.Activated += (_, _) => { Active = w; Changed?.Invoke(); };
        w.Deactivated += (_, _) =>
        {
            _lastDeactivated = w;
            _lastDeactivatedAt = Environment.TickCount64;
            if (Active == w) Active = null;
            Changed?.Invoke();
        };
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.TitleProperty || e.Property == Window.WindowStateProperty) Changed?.Invoke();
        };
        w.Closing += (_, _) => SaveBounds(w, key);
        w.Closed += (_, _) =>
        {
            _tasks.RemoveAll(t => t is NativeTask n && n.Window == w);
            if (Active == w) Active = null;
            Changed?.Invoke();
        };
        w.KeyDown += (_, e) => HandleKeys(w, e);

        _tasks.Add(new NativeTask(w, appId));
        Changed?.Invoke();
    }

    private static bool OnAnyScreen(Window w, WindowBounds b)
    {
        try
        {
            var probe = new PixelPoint(b.X + 30, b.Y + 30);
            return w.Screens.All.Any(s => s.Bounds.Contains(probe));
        }
        catch { return false; }
    }

    private static void SaveBounds(Window w, string key)
    {
        try
        {
            if (w.WindowState != WindowState.Normal) return;
            SettingsStore.Current.Windows[key] = new WindowBounds
            {
                X = w.Position.X,
                Y = w.Position.Y,
                W = w.ClientSize.Width,
                H = w.ClientSize.Height
            };
            SettingsStore.Save();
        }
        catch { /* not critical */ }
    }

    // ---- taskbar pinning ----

    public static bool IsPinned(string appId) => SettingsStore.Current.PinnedApps.Contains(appId);

    /// <summary>Red Star apps and installed Linux apps ("linux:…") can be pinned.</summary>
    public static bool CanPin(string? appId) => appId != null && (AppRegistry.Find(appId) != null || DesktopEntries.FromId(appId) != null);

    public static void TogglePin(string appId)
    {
        var pins = SettingsStore.Current.PinnedApps;
        if (!pins.Remove(appId)) pins.Add(appId);
        SettingsStore.Save();
        Changed?.Invoke();
    }

    // ---- snapping ----

    /// <summary>
    /// Screen rectangle (in pixels) for Snap Left / Right / Fill: the work area minus the taskbar.
    /// </summary>
    public static bool TryComputeSnap(Window reference, SnapMode mode, out PixelPoint position,
        out int width, out int height, out double scale)
    {
        position = default; width = 0; height = 0; scale = 1;
        if (mode == SnapMode.Restore) return false;
        var screen = reference.Screens.ScreenFromWindow(reference) ?? reference.Screens.Primary;
        if (screen == null) return false;

        var wa = Desktop?.WindowState == WindowState.FullScreen ? screen.Bounds : screen.WorkingArea;
        scale = screen.Scaling;
        height = wa.Height - (int)(TaskbarHeight * scale);
        width = mode == SnapMode.Full ? wa.Width : wa.Width / 2;
        var x = mode == SnapMode.Right ? wa.X + wa.Width / 2 : wa.X;
        position = new PixelPoint(x, wa.Y);
        return true;
    }

    public static void Snap(Window w, SnapMode mode)
    {
        if (mode == SnapMode.Restore)
        {
            w.WindowState = WindowState.Normal;
            return;
        }
        if (!w.CanResize) return;
        if (!TryComputeSnap(w, mode, out var pos, out var width, out var height, out var scale)) return;

        w.WindowState = WindowState.Normal;
        w.Position = pos;
        w.Width = width / scale;
        w.Height = height / scale;
    }

    // Alt+arrow keys snap any native app window.
    private static void HandleKeys(Window w, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        switch (e.Key)
        {
            case Key.Left: Snap(w, SnapMode.Left); e.Handled = true; break;
            case Key.Right: Snap(w, SnapMode.Right); e.Handled = true; break;
            case Key.Up: Snap(w, SnapMode.Full); e.Handled = true; break;
            case Key.Down: Snap(w, SnapMode.Restore); e.Handled = true; break;
        }
    }
}
