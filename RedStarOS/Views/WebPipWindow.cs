using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Picture-in-picture for Red Star Online. The engine puts the video into element fullscreen inside its offscreen
/// tab; this small always-on-top window shows that tab's frames. Works the same on X11 and Wayland.
/// </summary>
public sealed class WebPipWindow : Window
{
    public static WebPipWindow? Current { get; private set; }

    private readonly WebTab _tab;
    private readonly Action _backToTab;
    private readonly FrameView _view;
    private readonly Button _play;
    private bool _closingFromEngine;

    private sealed class FrameView : Control
    {
        private readonly WebTab _t;
        private bool _queued;
        public FrameView(WebTab t) { _t = t; ClipToBounds = true; }
        public void Frame() { if (_queued) return; _queued = true; Dispatcher.UIThread.Post(() => { _queued = false; InvalidateVisual(); }, DispatcherPriority.Render); }
        public override void Render(DrawingContext ctx)
        {
            ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
            var f = _t.Frame;
            if (f == null || f.Size.Width <= 0 || f.Size.Height <= 0) return;
            double s = Math.Min(Bounds.Width / f.Size.Width, Bounds.Height / f.Size.Height);
            double w = f.Size.Width * s, h = f.Size.Height * s;
            ctx.DrawImage(f, new Rect(f.Size), new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h));
        }
    }

    public WebPipWindow(WebTab tab, int videoW, int videoH, bool paused, Action backToTab)
    {
        _tab = tab; _backToTab = backToTab;
        Title = "Picture in picture — " + (string.IsNullOrEmpty(tab.Title) ? "Red Star Online" : tab.Title);
        Topmost = true;
        CanResize = true;
        ShowInTaskbar = false;
        double aspect = videoW > 0 && videoH > 0 ? (double)videoW / videoH : 16.0 / 9;
        Width = 420; Height = Math.Round(420 / aspect);
        MinWidth = 200; MinHeight = 120;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _view = new FrameView(tab);
        Button Btn(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4), Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }
        _play = Btn(paused ? "▶" : "⏸", "Play / pause (Space)", () => _tab.PipPlay());
        var back = Btn("⤢", "Back to tab", () => { _backToTab(); Close(); });
        var close = Btn("✕", "Close picture-in-picture", Close);
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Children = { _play, back, close }, Opacity = 0
        };
        var root = new Grid { Children = { _view, bar } };
        root.PointerEntered += (_, _) => bar.Opacity = 1;
        root.PointerExited += (_, _) => bar.Opacity = 0;
        root.DoubleTapped += (_, _) => { _backToTab(); Close(); };
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { _tab.PipPlay(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };

        _tab.FrameReady += _view.Frame;
        _tab.PipPlayState += OnPlayState;
        Opened += (_, _) =>
        {
            var wa = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea;
            if (wa is { } a)
            {
                var sc = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
                Position = new PixelPoint(a.Right - (int)(Width * sc) - 24, a.Bottom - (int)(Height * sc) - 24);
            }
            _view.Frame();
        };
        Closed += (_, _) =>
        {
            _tab.FrameReady -= _view.Frame;
            _tab.PipPlayState -= OnPlayState;
            if (ReferenceEquals(Current, this)) Current = null;
            if (!_closingFromEngine && _tab.InPip) _tab.PipExit();
        };
    }

    public WebTab Tab => _tab;

    private void OnPlayState(bool paused) => Dispatcher.UIThread.Post(() => _play.Content = paused ? "▶" : "⏸");

    /// <summary>Shows the window for a tab, replacing any other picture-in-picture window.</summary>
    public static void Open(WebTab tab, int w, int h, bool paused, Action backToTab)
    {
        if (Current != null && !ReferenceEquals(Current._tab, tab)) Current.Close();
        if (Current != null) { Current.Activate(); return; }
        Current = new WebPipWindow(tab, w, h, paused, backToTab);
        Current.Show();
    }

    /// <summary>The page left picture-in-picture by itself (or the tab closed).</summary>
    public static void EndFor(WebTab tab)
    {
        if (Current == null || !ReferenceEquals(Current._tab, tab)) return;
        Current._closingFromEngine = true;
        Current.Close();
    }
}
