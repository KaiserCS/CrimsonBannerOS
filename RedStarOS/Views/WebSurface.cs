using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;
using Avalonia.Input.Platform;

namespace RedStarOS.Views;

/// <summary>Draws a <see cref="WebTab"/>'s frames and forwards mouse, wheel and keyboard input to the engine.</summary>
public sealed class WebSurface : Control
{
    private WebTab? _tab;
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private bool _rendering;

    public WebSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        // frames are drawn pixel for pixel: no filtering needed (cheaper per frame with the software renderer)
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.LowQuality);
        Cursor = new Cursor(StandardCursorType.Arrow);
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); PushSize(); };
        SizeChanged += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
    }

    public WebTab? Tab
    {
        get => _tab;
        set
        {
            if (ReferenceEquals(_tab, value)) return;
            if (_tab != null) { _tab.FrameReady -= OnFrame; _tab.CursorChanged -= OnCursor; }
            _tab = value;
            if (_tab != null) { _tab.FrameReady += OnFrame; _tab.CursorChanged += OnCursor; OnCursor(); PushSize(); }
            InvalidateVisual();
        }
    }

    private void PushSize() => _tab?.Resize((int)Bounds.Width, (int)Bounds.Height);
    private void OnFrame() { if (!_rendering) { _rendering = true; Dispatcher.UIThread.Post(() => { _rendering = false; InvalidateVisual(); }, DispatcherPriority.Render); } }

    private void OnCursor()
    {
        var n = _tab?.CursorName ?? "default";
        var t = n switch
        {
            "pointer" or "grab" or "grabbing" => StandardCursorType.Hand,
            "text" or "vertical-text" => StandardCursorType.Ibeam,
            "crosshair" or "cell" => StandardCursorType.Cross,
            "move" or "all-scroll" => StandardCursorType.SizeAll,
            "wait" => StandardCursorType.Wait,
            "progress" => StandardCursorType.AppStarting,
            "help" => StandardCursorType.Help,
            "not-allowed" or "no-drop" => StandardCursorType.No,
            "col-resize" or "ew-resize" or "e-resize" or "w-resize" => StandardCursorType.SizeWestEast,
            "row-resize" or "ns-resize" or "n-resize" or "s-resize" => StandardCursorType.SizeNorthSouth,
            "nesw-resize" or "ne-resize" or "sw-resize" => StandardCursorType.TopRightCorner,
            "nwse-resize" or "nw-resize" or "se-resize" => StandardCursorType.TopLeftCorner,
            "none" => StandardCursorType.None,
            _ => StandardCursorType.Arrow
        };
        Cursor = new Cursor(t);
    }

    public override void Render(DrawingContext ctx)
    {
        var f = _tab?.Frame;
        if (f == null) { ctx.FillRectangle(Brushes.White, new Rect(Bounds.Size)); return; }
        // drawn 1:1 from the top-left; only the strips the frame doesn't cover (during a resize) are filled white
        var fs = f.Size;
        if (fs.Width < Bounds.Width) ctx.FillRectangle(Brushes.White, new Rect(fs.Width, 0, Bounds.Width - fs.Width, Bounds.Height));
        if (fs.Height < Bounds.Height) ctx.FillRectangle(Brushes.White, new Rect(0, fs.Height, Math.Min(fs.Width, Bounds.Width), Bounds.Height - fs.Height));
        ctx.DrawImage(f, new Rect(fs), new Rect(0, 0, fs.Width, fs.Height));
    }

    // ───────────────────────── mouse ─────────────────────────
    private static string[] Mods(KeyModifiers k, PointerPointProperties? p = null)
    {
        var l = new List<string>(4);
        if (k.HasFlag(KeyModifiers.Shift)) l.Add("shift");
        if (k.HasFlag(KeyModifiers.Control)) l.Add("ctrl");
        if (k.HasFlag(KeyModifiers.Alt)) l.Add("alt");
        if (k.HasFlag(KeyModifiers.Meta)) l.Add("meta");
        if (p is { } pp)
        {
            if (pp.IsLeftButtonPressed) l.Add("left");
            if (pp.IsMiddleButtonPressed) l.Add("middle");
            if (pp.IsRightButtonPressed) l.Add("right");
        }
        return l.ToArray();
    }

    private static string ButtonOf(PointerPointProperties p) =>
        p.PointerUpdateKind switch
        {
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => "right",
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => "middle",
            _ => "left"
        };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_tab == null || !_tab.Alive) return;
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.PointerUpdateKind == PointerUpdateKind.XButton1Pressed) { _tab.Back(); e.Handled = true; return; }
        if (pt.Properties.PointerUpdateKind == PointerUpdateKind.XButton2Pressed) { _tab.Forward(); e.Handled = true; return; }
        _tab.Focus();
        _tab.Mouse("mouseDown", pt.Position.X, pt.Position.Y, ButtonOf(pt.Properties), Math.Max(1, e.ClickCount), Mods(e.KeyModifiers, pt.Properties));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_tab == null || !_tab.Alive) return;
        var pt = e.GetCurrentPoint(this);
        _tab.Mouse("mouseMove", pt.Position.X, pt.Position.Y, "left", 0, Mods(e.KeyModifiers, pt.Properties));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_tab == null || !_tab.Alive) return;
        var pt = e.GetCurrentPoint(this);
        _tab.Mouse("mouseUp", pt.Position.X, pt.Position.Y, ButtonOf(pt.Properties), 1, Mods(e.KeyModifiers));
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_tab is { Alive: true }) _tab.Mouse("mouseLeave", 0, 0, "left", 0, Array.Empty<string>());
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_tab == null || !_tab.Alive) return;
        var p = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _tab.SetZoom(_tab.Zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
            return;
        }
        _tab.Wheel(p.X, p.Y, e.Delta.X * 120, e.Delta.Y * 120, Mods(e.KeyModifiers));
        e.Handled = true;
    }

    // ───────────────────────── keyboard ─────────────────────────
    private static string? KeyName(Key k)
    {
        if (k >= Key.A && k <= Key.Z) return k.ToString();
        if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((int)(k - Key.NumPad0)).ToString();
        if (k >= Key.F1 && k <= Key.F12) return k.ToString();
        return k switch
        {
            Key.Enter => "Enter", Key.Tab => "Tab", Key.Back => "Backspace", Key.Delete => "Delete", Key.Escape => "Escape",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Insert => "Insert",
            Key.Space => "Space",
            Key.OemMinus => "-", Key.OemPlus => "=", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemBackslash or Key.OemPipe => "\\", Key.OemTilde => "`",
            _ => null
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_tab == null || !_tab.Alive) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            string? cmd = e.Key switch
            {
                Key.C => "copy", Key.X => "cut", Key.V => "paste", Key.A => "selectAll",
                Key.Z => shift ? "redo" : "undo", Key.Y => "redo", _ => null
            };
            if (cmd == "paste") { _ = PasteAsync(); e.Handled = true; return; }
            if (cmd != null) { _tab.Edit(cmd); e.Handled = true; return; }
        }
        var name = KeyName(e.Key);
        if (name == null) return;
        _tab.Key("keyDown", name, Mods(e.KeyModifiers));
        if (e.Key == Key.Enter) _tab.Char("\r");
        e.Handled = true;
    }

    public async System.Threading.Tasks.Task PasteAsync()
    {
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null || _tab == null) return;
        var text = await cb.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text)) _tab.Insert(text);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_tab == null || !_tab.Alive) return;
        var name = KeyName(e.Key);
        if (name == null) return;
        _tab.Key("keyUp", name, Mods(e.KeyModifiers));
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_tab == null || !_tab.Alive || string.IsNullOrEmpty(e.Text)) return;
        foreach (var ch in e.Text) if (!char.IsControl(ch)) _tab.Char(ch.ToString());
        e.Handled = true;
    }
}
