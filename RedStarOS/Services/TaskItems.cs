using System;
using Avalonia.Controls;

namespace RedStarOS.Services;

/// <summary>
/// One taskbar entry. Native Avalonia windows (<see cref="NativeTask"/>), windows of Electron apps
/// (<see cref="ExternalTask"/>, reported through the shell bridge) and windows of any other program
/// (<see cref="X11Task"/>, seen through the window manager) look and behave the same to the taskbar.
/// </summary>
public abstract class TaskItem
{
    /// <summary>Id from <see cref="AppRegistry"/> (icon, pinning); null for dialogs that belong to no app.</summary>
    public string? AppId { get; protected set; }

    public abstract string Title { get; }
    public abstract bool IsActive { get; }
    public abstract bool IsMinimized { get; }

    public abstract void ToggleFromTaskbar();
    public abstract void Activate();
    public abstract void Minimize();
    public abstract void Restore();
    public abstract void Snap(SnapMode mode);
    public abstract void Close();

    /// <summary>The program's own icon (other programs' windows), or null for the Red Star tile.</summary>
    public virtual Avalonia.Media.IImage? Icon => null;
}

/// <summary>A window of the shell process itself.</summary>
public sealed class NativeTask : TaskItem
{
    public Window Window { get; }

    public NativeTask(Window window, string? appId)
    {
        Window = window;
        AppId = appId;
    }

    public override string Title => WaylandShell.VisibleTitle(Window) is { Length: > 0 } t && !string.IsNullOrWhiteSpace(t) ? t : "Window";

    /// <summary>
    /// Natively on Wayland a window can't un-minimise itself, and isn't told when the compositor minimised it (its
    /// title-bar button): its entry in redstar-comp's window list (this process, same title) is the truth, and
    /// redstar-comp does the minimising and restoring.
    /// </summary>
    private CompIpc.NativeWindow? Comp(bool preferMinimized = false)
    {
        if (!WaylandShell.Active || !CompIpc.Connected) return null;
        // by the token it was named with (see WaylandShell.Name); by title only if it never got one
        if (WaylandShell.CompIdOf(Window) is { } id) return CompIpc.Windows.TryGetValue(id, out var byId) ? byId : null;
        var title = Window.Title ?? "";
        var mine = CompIpc.Windows.Values.Where(w => w.Pid == Environment.ProcessId && w.Title == title).ToList();
        return mine.Count == 0 ? null : preferMinimized ? mine.FirstOrDefault(w => w.Minimized) ?? mine[0] : mine[0];
    }

    public override bool IsActive => Comp() is { } c
        ? CompIpc.Active == c.Id && !c.Minimized
        : WindowManager.Active == Window && Window.WindowState != WindowState.Minimized;
    public override bool IsMinimized => Comp() is { } c ? c.Minimized : Window.WindowState == WindowState.Minimized;

    public override void ToggleFromTaskbar()
    {
        if (Comp(preferMinimized: true) is { } c)
        {
            if (c.Minimized || CompIpc.Active != c.Id) CompIpc.Activate(c.Id);   // restores a minimised one too
            else CompIpc.Snap(c.Id, "minimize");
            return;
        }
        // Clicking the taskbar deactivates the window just before the click arrives, so a window
        // that lost focus a moment ago still counts as the active one.
        var wasActive = WindowManager.Active == Window || WindowManager.WasActiveJustNow(Window);
        if (Window.WindowState == WindowState.Minimized)
        {
            Window.WindowState = WindowState.Normal;
            Window.Activate();
        }
        else if (wasActive) Window.WindowState = WindowState.Minimized;
        else Window.Activate();
    }

    public override void Activate()
    {
        if (Comp(preferMinimized: true) is { } c) { CompIpc.Activate(c.Id); return; }
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Activate();
    }

    public override void Minimize()
    {
        if (Comp() is { } c) { CompIpc.Snap(c.Id, "minimize"); return; }
        Window.WindowState = WindowState.Minimized;
    }

    public override void Restore()
    {
        if (Comp(preferMinimized: true) is { } c) { CompIpc.Snap(c.Id, "restore"); CompIpc.Activate(c.Id); return; }
        WindowManager.Snap(Window, SnapMode.Restore); Window.Activate();
    }

    public override void Snap(SnapMode mode)
    {
        if (Comp() is { } c)
        {
            CompIpc.Snap(c.Id, mode switch { SnapMode.Left => "left", SnapMode.Right => "right", SnapMode.Full => "maximize", _ => "restore" });
            CompIpc.Activate(c.Id);
            return;
        }
        WindowManager.Snap(Window, mode); Window.Activate();
    }

    public override void Close() => Window.Close();
}

/// <summary>
/// A window that belongs to an Electron app. State arrives from the app over the shell bridge,
/// commands go back the same way. Before the app has connected it is a "starting" placeholder,
/// so a taskbar button appears the moment the app is launched.
/// </summary>
public sealed class ExternalTask : TaskItem
{
    private readonly string _name;
    private string _title;

    internal BridgeConnection? Connection { get; set; }
    internal bool Focused { get; set; }
    internal bool Minimized { get; set; }
    internal long LastFocusAt { get; set; }
    internal long LastBlurAt { get; set; }

    public ExternalTask(string appId, string name)
    {
        AppId = appId;
        _name = name;
        _title = name;
    }

    public bool Starting => Connection == null;

    public override string Title => _title;
    public override bool IsActive => Focused && !Minimized;
    public override bool IsMinimized => Minimized;

    internal void SetTitle(string? title) => _title = string.IsNullOrWhiteSpace(title) ? _name : title!;

    private void Cmd(string cmd) => Connection?.SendCommand(cmd);

    public override void ToggleFromTaskbar()
    {
        if (Connection == null) return;                                     // still starting
        var justBlurred = Environment.TickCount64 - LastBlurAt < 500 && LastBlurAt >= LastFocusAt;
        if (Minimized) Cmd("restore");
        else if (Focused || justBlurred) Cmd("minimize");
        else Cmd("focus");
    }

    public override void Activate() => Cmd(Minimized ? "restore" : "focus");
    public override void Minimize() => Cmd("minimize");
    public override void Restore() => Cmd("restore");

    public override void Snap(SnapMode mode)
    {
        if (Connection == null) return;
        if (mode == SnapMode.Restore) { Cmd("restore"); return; }
        if (WindowManager.Desktop != null &&
            WindowManager.TryComputeSnap(WindowManager.Desktop, mode, out var pos, out var w, out var h, out _))
            Connection.SendBounds(pos.X, pos.Y, w, h);
    }

    public override void Close()
    {
        if (Connection != null) Cmd("close");
        else ShellBridge.CancelLaunch(this);                                // placeholder: just drop it
    }
}

/// <summary>
/// A window of another program (Firefox, GIMP, a terminal app…). Its title, state and icon come from the window
/// manager (EWMH); clicks are sent back the same way, so it behaves like a Red Star window on the taskbar.
/// </summary>
public sealed class X11Task : TaskItem
{
    public long Window { get; }
    public string Instance { get; private set; } = "";
    public string Class { get; private set; } = "";
    public int Pid { get; private set; }
    public LinuxApp? App { get; private set; }

    private string _title = "";
    private bool _minimized, _active;
    private int _iconVersion = -1;
    private Avalonia.Media.IImage? _icon;
    internal long LastActiveAt { get; set; }
    public int IconStamp => _iconVersion;

    public X11Task(X11Desktop.ForeignWindow w) { Window = w.Id; Update(w, false); }

    /// <summary>Applies new state; true when something the taskbar shows changed.</summary>
    internal bool Update(X11Desktop.ForeignWindow w, bool active)
    {
        bool changed = w.Title != _title || w.Minimized != _minimized || active != _active || w.IconVersion != _iconVersion;
        if (_active && !active) LastActiveAt = Environment.TickCount64;
        _title = w.Title; _minimized = w.Minimized; _active = active; Pid = w.Pid;
        if (w.Instance != Instance || w.Class != Class)
        {
            Instance = w.Instance; Class = w.Class;
            App = DesktopEntries.ForWindow(Instance, Class);
            AppId = App?.Id;
            changed = true;
        }
        if (w.IconVersion != _iconVersion) { _iconVersion = w.IconVersion; _icon = null; }
        return changed;
    }

    public override string Title => string.IsNullOrWhiteSpace(_title) ? (App?.Name ?? (Class.Length > 0 ? Class : "Window")) : _title;
    public override bool IsActive => _active && !_minimized;
    public override bool IsMinimized => _minimized;

    public override Avalonia.Media.IImage? Icon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                if (X11Desktop.IconFor(Window) is { } ic)
                {
                    var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(ic.W, ic.H), new Avalonia.Vector(96, 96),
                        Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
                    using (var fb = bmp.Lock())
                        for (int y = 0; y < ic.H; y++)
                            System.Runtime.InteropServices.Marshal.Copy(ic.Pixels, y * ic.W * 4, fb.Address + y * fb.RowBytes, ic.W * 4);
                    return _icon = bmp;
                }
                if (DesktopEntries.IconPath(App?.Icon) is { } path) return _icon = new Avalonia.Media.Imaging.Bitmap(path);
            }
            catch { }
            return null;
        }
    }

    public override void ToggleFromTaskbar()
    {
        // pressing the taskbar takes focus away from the window just before the click arrives
        bool wasActive = _active || Environment.TickCount64 - LastActiveAt < 500;
        if (_minimized) X11Desktop.Activate(Window);
        else if (wasActive) X11Desktop.SnapWindow(Window, "minimize");
        else X11Desktop.Activate(Window);
    }

    public override void Activate() => X11Desktop.Activate(Window);
    public override void Minimize() => X11Desktop.SnapWindow(Window, "minimize");
    public override void Restore() { X11Desktop.SnapWindow(Window, "restore"); X11Desktop.Activate(Window); }

    public override void Snap(SnapMode mode)
    {
        X11Desktop.SnapWindow(Window, mode switch { SnapMode.Left => "left", SnapMode.Right => "right", SnapMode.Full => "maximize", _ => "restore" });
        X11Desktop.Activate(Window);
    }

    public override void Close() => X11Desktop.CloseWindow(Window);
}
