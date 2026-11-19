using System;
using Avalonia;
using RedStarOS.Services;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace RedStarOS.Views;

/// <summary>Full-screen slide show. Right/Space/Enter/click = next, Left/Backspace/right-click = back, B = black screen, Esc = end.</summary>
public sealed class SlideShowWindow : Window
{
    private readonly Deck _deck;
    private readonly Image _img = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _counter = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), FontSize = 14,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 18, 12),
        IsHitTestVisible = false
    };
    private readonly Border _black = new() { Background = Brushes.Black, IsVisible = false };
    private int _i;
    private DispatcherTimer? _anim;
    private SKBitmap? _from, _to;
    private int _step;
    private bool _forward = true;
    private string _kind = "None";
    private int _pw, _ph;

    public SlideShowWindow(Deck deck, int start)
    {
        _deck = deck;
        _i = Math.Clamp(start, 0, deck.Slides.Count - 1);
        WaylandShell.Frameless(this, "Slide show");
        Background = Brushes.Black;
        WindowState = WindowState.FullScreen;
        WindowDecorations = WindowDecorations.None;
        Content = new Grid { Children = { _img, _counter, _black } };

        Opened += (_, _) => { Focus(); ShowSlide(false); };
        SizeChanged += (_, _) => { if (IsLoaded) ShowSlide(false); };
        KeyDown += OnKey;
        PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(this).Properties;
            if (p.IsRightButtonPressed) Step(-1);
            else if (p.IsLeftButtonPressed) Step(1);
        };
        Closed += (_, _) => { _anim?.Stop(); _from?.Dispose(); _to?.Dispose(); };
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right: case Key.Down: case Key.Space: case Key.Enter: case Key.PageDown: case Key.N: Step(1); break;
            case Key.Left: case Key.Up: case Key.Back: case Key.PageUp: case Key.P: Step(-1); break;
            case Key.Home: Jump(0); break;
            case Key.End: Jump(_deck.Slides.Count - 1); break;
            case Key.B: case Key.OemPeriod: _black.IsVisible = !_black.IsVisible; break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private (int W, int H) SlideFrameSize()
    {
        double bw = Math.Max(320, Bounds.Width), bh = Math.Max(180, Bounds.Height);
        double scale = Math.Min(bw / _deck.Width, bh / _deck.Height);
        scale = Math.Min(scale, 2560.0 / _deck.Width);
        return ((int)Math.Round(_deck.Width * scale), (int)Math.Round(_deck.Height * scale));
    }

    private SKBitmap RenderSlide(int index)
    {
        (_pw, _ph) = SlideFrameSize();
        return SlideRenderer.Render(_deck, _deck.Slides[index], _pw, _ph, false);
    }

    private void Present(SKBitmap frame)
    {
        _img.Source = PaintCanvas.ToAvalonia(frame);
    }

    private void ShowSlide(bool animate)
    {
        _anim?.Stop();
        _anim = null;
        _counter.Text = $"{_i + 1} / {_deck.Slides.Count}";
        using var frame = RenderSlide(_i);
        Present(frame);
    }

    private void Jump(int index)
    {
        _i = Math.Clamp(index, 0, _deck.Slides.Count - 1);
        _black.IsVisible = false;
        ShowSlide(false);
    }

    private void Step(int delta)
    {
        _black.IsVisible = false;
        int next = _i + delta;
        if (next >= _deck.Slides.Count) { Close(); return; }
        if (next < 0) return;

        // finish a running transition immediately before starting another
        if (_anim != null) { _anim.Stop(); _anim = null; }

        var slideForKind = delta > 0 ? _deck.Slides[next] : _deck.Slides[_i];
        _kind = slideForKind.Transition;
        var from = RenderSlide(_i);
        _i = next;
        _counter.Text = $"{_i + 1} / {_deck.Slides.Count}";
        var to = RenderSlide(_i);

        if (_kind == "None") { Present(to); from.Dispose(); to.Dispose(); return; }

        _from?.Dispose();
        _to?.Dispose();
        _from = from;
        _to = to;
        _forward = delta > 0;
        _step = 0;
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _anim.Tick += (_, _) => Tick();
        _anim.Start();
    }

    private void Tick()
    {
        const int steps = 16;
        _step++;
        double t = Math.Min(1.0, _step / (double)steps);
        t = t * t * (3 - 2 * t);                       // ease in-out
        if (_from == null || _to == null) { _anim?.Stop(); return; }

        using var frame = PaintDoc.Create(_pw, _ph, SKColors.Black);
        using (var c = new SKCanvas(frame))
        {
            if (_kind == "Fade")
            {
                c.DrawBitmap(_from, 0, 0);
                using var p = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(t * 255)) };
                c.DrawBitmap(_to, 0, 0, p);
            }
            else
            {
                float dir = _forward ? 1 : -1;
                c.DrawBitmap(_from, (float)(-dir * t * _pw), 0);
                c.DrawBitmap(_to, (float)(dir * (1 - t) * _pw), 0);
            }
        }
        Present(frame);
        if (_step >= steps)
        {
            _anim?.Stop();
            _anim = null;
        }
    }
}
