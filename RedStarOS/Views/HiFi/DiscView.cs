// SPDX-License-Identifier: GPL-3.0-or-later
// The compact disc in CD Player's tray: the album cover printed on a silver disc, which spins while it plays.
// After CDPlayer by Kizarov3 (MIT), see NOTICE in this folder.
using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace RedStarOS.Views.HiFi;

public sealed class DiscView : Control
{
    public static readonly StyledProperty<IImage?> CoverProperty = AvaloniaProperty.Register<DiscView, IImage?>(nameof(Cover));
    public static readonly StyledProperty<bool> SpinningProperty = AvaloniaProperty.Register<DiscView, bool>(nameof(Spinning));

    /// <summary>One turn in this many seconds: slow enough to read the cover as it goes round.</summary>
    public const double SecondsPerTurn = 2.4;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = new();
    private double _angle;

    static DiscView()
    {
        AffectsRender<DiscView>(CoverProperty);
    }

    public DiscView()
    {
        _timer.Tick += (_, _) =>
        {
            _angle = (_angle + _clock.Elapsed.TotalSeconds * 360 / SecondsPerTurn) % 360;
            _clock.Restart();
            InvalidateVisual();
        };
    }

    public IImage? Cover { get => GetValue(CoverProperty); set => SetValue(CoverProperty, value); }
    public bool Spinning { get => GetValue(SpinningProperty); set => SetValue(SpinningProperty, value); }
    public double Angle => _angle;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpinningProperty) UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        if (Spinning && VisualRoot != null) { _clock.Restart(); _timer.Start(); }
        else _timer.Stop();
    }

    private static readonly IBrush Silver = new RadialGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromRgb(236, 238, 242), 0.25), new GradientStop(Color.FromRgb(196, 199, 205), 0.7),
            new GradientStop(Color.FromRgb(150, 153, 160), 1)
        }
    };

    // the rainbow a disc throws back at the light
    private static readonly IBrush Sheen = new ConicGradientBrush
    {
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x55, 255, 255, 255), 0.08),
            new GradientStop(Color.FromArgb(0x30, 120, 200, 255), 0.14), new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.25),
            new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.5), new GradientStop(Color.FromArgb(0x50, 255, 255, 255), 0.58),
            new GradientStop(Color.FromArgb(0x30, 255, 160, 200), 0.64), new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.75),
            new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1)
        }
    };

    private static readonly IPen Groove = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0, 0, 0)), 0.8);
    private static readonly IPen Rim = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 1.2);
    private static readonly IBrush Hub = new SolidColorBrush(Color.FromArgb(0xC8, 214, 218, 224));
    private static readonly IPen HubEdge = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 1);
    private static readonly IBrush Hole = new SolidColorBrush(DeckColors.Bg);
    private static readonly IBrush Shadow = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 8) return;
        var r = size / 2 - 3;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);

        ctx.DrawEllipse(Shadow, null, c + new Point(0, 3), r + 1, r + 1);
        var turn = Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(_angle * Math.PI / 180) * Matrix.CreateTranslation(c.X, c.Y);
        using (ctx.PushTransform(turn))
        {
            ctx.DrawEllipse(Silver, null, c, r, r);
            if (Cover is { } cover && cover.Size.Width > 0)
            {
                // the cover printed on the disc, cropped to a square from its middle
                var s = cover.Size;
                var side = Math.Min(s.Width, s.Height);
                var src = new Rect((s.Width - side) / 2, (s.Height - side) / 2, side, side);
                using (ctx.PushGeometryClip(new EllipseGeometry(new Rect(c.X - r * 0.97, c.Y - r * 0.97, r * 1.94, r * 1.94))))
                    ctx.DrawImage(cover, src, new Rect(c.X - r, c.Y - r, r * 2, r * 2));
            }
            else
            {
                for (var i = 0; i < 16; i++) ctx.DrawEllipse(null, Groove, c, r * (0.36 + i * 0.04), r * (0.36 + i * 0.04));
            }
            ctx.DrawEllipse(Sheen, null, c, r, r);
            // the clear plastic round the hole, and the hole
            ctx.DrawEllipse(Hub, HubEdge, c, r * 0.24, r * 0.24);
            ctx.DrawEllipse(null, HubEdge, c, r * 0.165, r * 0.165);
            ctx.DrawEllipse(Hole, HubEdge, c, r * 0.075, r * 0.075);
        }
        ctx.DrawEllipse(null, Rim, c, r, r);
    }
}
