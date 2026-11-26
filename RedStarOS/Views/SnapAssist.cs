// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Snap Assist, as on Windows: after a window is dropped at the side (or a corner) of the screen, the other open
/// windows are offered for the half left over. Click one to snap it there; Esc, or a click anywhere else, declines.
/// </summary>
public sealed class SnapAssist : Window
{
    private static SnapAssist? _open;
    public const string WindowTitle = "Crimson Banner Snap Assist";
    private readonly string _fill;

    /// <summary>The half left over when a window took this zone, or null (maximised: nothing left over).</summary>
    public static string? Remaining(string zone) => zone switch
    {
        "left" or "top-left" or "bottom-left" => "right",
        "right" or "top-right" or "bottom-right" => "left",
        _ => null,
    };

    public static void Offer(long snapped, string zone)
    {
        if (!SettingsStore.Current.SnapAssist || Remaining(zone) is not { } fill) return;
        X11Desktop.ListWindows(list =>
        {
            long self = WindowManager.Desktop?.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0;
            var others = list.Where(w => w.Id != snapped && w.Id != self && w.Title != WindowTitle && !string.IsNullOrWhiteSpace(w.Title)).ToList();
            _open?.Close();
            if (others.Count == 0) return;
            _open = new SnapAssist(others, fill);
            _open.Closed += (s, _) => { if (ReferenceEquals(_open, s)) _open = null; };
            _open.Show();
        });
    }

    public SnapAssist(List<X11Desktop.XWin> wins, string fill)
    {
        _fill = fill;
        WindowDecorations = WindowDecorations.None;
        WaylandShell.Frameless(this, WindowTitle);
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        bool opaque = ThemeService.OpaquePopups;
        Background = opaque ? new SolidColorBrush(Color.FromRgb(24, 24, 28)) : Brushes.Transparent;
        if (!opaque) TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        // the half it will fill, in screen pixels (X11 places the window there; natively on Wayland it's centred)
        var screen = Screens.Primary;
        var wa = screen?.WorkingArea ?? new PixelRect(0, 0, 1600, 900);
        double scale = screen?.Scaling ?? 1;
        var half = fill == "left" ? new PixelRect(wa.X, wa.Y, wa.Width / 2, wa.Height) : new PixelRect(wa.X + wa.Width / 2, wa.Y, wa.Width - wa.Width / 2, wa.Height);

        var wrap = new WrapPanel { MaxWidth = Math.Max(260, half.Width / scale - 80), HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var w in wins.Take(12))
        {
            var task = WindowManager.Tasks.OfType<NativeTask>().FirstOrDefault(t =>
                t.Window.TryGetPlatformHandle()?.Handle.ToInt64() == w.Id || WaylandShell.CompIdOf(t.Window) == w.Id);
            var foreign = WindowManager.Tasks.OfType<X11Task>().FirstOrDefault(t => t.Window == w.Id);
            Control icon = task?.AppId != null ? AppIcons.Create(task.AppId, w.Title, 48)
                : foreign?.Icon is { } img ? new Image { Source = img, Width = 48, Height = 48 }
                : AppIcons.Tile(string.IsNullOrEmpty(w.WmClass) ? "?" : w.WmClass[..1].ToUpperInvariant(), 48);
            var card = new Button
            {
                Classes = { "snapcard" }, Width = 176, Height = 132, Margin = new Thickness(6), Padding = new Thickness(10),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        icon,
                        new TextBlock { Text = w.Title, Foreground = Brushes.White, FontSize = 12.5, TextAlignment = TextAlignment.Center,
                            TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            ToolTip.SetTip(card, $"Snap “{w.Title}” to the {fill}");
            long id = w.Id;
            card.Click += (_, _) =>
            {
                X11Desktop.SnapWindow(id, fill);
                if (task != null) task.Activate(); else X11Desktop.Activate(id);
                Close();
            };
            wrap.Children.Add(card);
        }
        var accent = ThemeService.AccentColor;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(opaque ? (byte)255 : (byte)232, 24, 24, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(opaque ? 0 : 14), Padding = new Thickness(18, 14, 18, 18),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"Choose a window for the {fill} half", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(6, 0, 0, 0) },
                    wrap,
                },
            },
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Deactivated += (_, _) => Close();
        Opened += (_, _) =>
        {
            if (WaylandShell.Active) return;
            // in the middle of the half left over
            var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
            Position = new PixelPoint(half.X + Math.Max(0, (half.Width - size.Width) / 2), half.Y + Math.Max(0, (half.Height - size.Height) / 2));
        };
    }
}
