// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Taskbar previews, as on Windows: rest the pointer on a running app's button and its window appears above it, a
/// picture of it for Crimson Banner's own windows (other programs' show their icon), with its title and a close
/// button. Click the picture to switch to it. Moving between buttons keeps the preview up and changes it.
/// </summary>
public static class TaskPreview
{
    private static Flyout? _popup;
    private static bool _showing;
    private static Button? _for;
    private static Button? _waiting;
    private static readonly DispatcherTimer Show = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private static readonly DispatcherTimer Hide = new() { Interval = TimeSpan.FromMilliseconds(280) };
    public const double ThumbWidth = 220, ThumbHeight = 132;

    static TaskPreview()
    {
        Show.Tick += (_, _) => { Show.Stop(); if (_waiting != null) Open(_waiting); };
        Hide.Tick += (_, _) => { Hide.Stop(); Close(); };
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, Func<TaskItem?>> Tasks = new();
    private static TaskItem? TaskOf(Button b) => Tasks.TryGetValue(b, out var f) ? f() : null;

    /// <summary>Gives a taskbar button its preview (of whatever window it stands for at the time).</summary>
    public static void Attach(Button btn, Func<TaskItem?> task)
    {
        Tasks.AddOrUpdate(btn, task);
        btn.PointerEntered += (_, _) =>
        {
            Hide.Stop();
            if (!SettingsStore.Current.TaskbarPreviews || TaskOf(btn) == null) return;
            if (_showing) { if (_for != btn) Open(btn); return; }      // already showing one: move at once
            _waiting = btn;
            Show.Stop(); Show.Start();
        };
        btn.PointerExited += (_, _) => { Show.Stop(); _waiting = null; if (_showing) { Hide.Stop(); Hide.Start(); } };
        btn.AddHandler(InputElement.PointerPressedEvent, (_, _) => { Show.Stop(); Close(); }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static void Close()
    {
        _popup?.Hide();
        _popup = null;
        _showing = false;
        _for = null;
    }

    private static void Open(Button btn)
    {
        var task = TaskOf(btn);
        if (task == null) { Close(); return; }
        Close();
        _for = btn;
        var card = Card(task);
        _popup = new Flyout { Content = card, Placement = PlacementMode.Top, ShowMode = FlyoutShowMode.Transient };
        _popup.FlyoutPresenterClasses.Add("preview");
        card.PointerEntered += (_, _) => Hide.Stop();
        card.PointerExited += (_, _) => { Hide.Stop(); Hide.Start(); };
        _popup.Closed += (s, _) => { if (ReferenceEquals(s, _popup)) { _showing = false; _for = null; } };
        _popup.ShowAt(btn);
        _showing = true;
    }

    public static Control Card(TaskItem task)
    {
        var close = new Button
        {
            Classes = { "previewclose" }, Width = 26, Height = 26, Padding = new Thickness(0),
            Content = Icons.Glyph("close", 13), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, "Close window");
        close.Click += (_, e) => { e.Handled = true; Close(); task.Close(); };
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var icon = task is NativeTask && task.AppId != null ? AppIcons.Create(task.AppId, task.Title, 16)
            : task.Icon is { } img ? new Image { Source = img, Width = 16, Height = 16 } : AppIcons.Tile("apps", 16);
        icon.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = task.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = ThumbWidth - 50 });

        var picture = new Border
        {
            Width = ThumbWidth, Height = ThumbHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Child = Picture(task),
        };
        var body = new Button
        {
            Classes = { "previewcard" }, Padding = new Thickness(10, 8, 10, 10),
            Content = new StackPanel { Spacing = 8, Children = { head, picture } },
        };
        body.Click += (_, _) => { Close(); task.Activate(); };
        return new Border
        {
            Classes = { "preview" }, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            Child = body,
        };
    }

    /// <summary>A picture of one of Crimson Banner's windows, drawn small; other programs' windows show their icon.</summary>
    private static Control Picture(TaskItem task)
    {
        if (task is NativeTask { Window: { } w } && w.Bounds.Width > 1 && w.Bounds.Height > 1 && Thumbnail(w) is { } bmp)
            return new Image { Source = bmp, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = task is NativeTask && task.AppId != null ? AppIcons.Create(task.AppId, task.Title, 56)
            : task.Icon is { } img ? new Image { Source = img, Width = 56, Height = 56 } : AppIcons.Tile("apps", 56);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    /// <summary>
    /// The window drawn at the preview's size: drawn whole at its own size first (a bitmap's resolution doesn't scale
    /// what's drawn into it, so a window drawn straight into a small one lands in its corner), then shrunk smoothly.
    /// </summary>
    public static RenderTargetBitmap? Thumbnail(Visual window, double maxW = ThumbWidth * 2, double maxH = ThumbHeight * 2)
    {
        try
        {
            int w = Math.Max(1, (int)Math.Ceiling(window.Bounds.Width)), h = Math.Max(1, (int)Math.Ceiling(window.Bounds.Height));
            var full = new RenderTargetBitmap(new PixelSize(w, h));
            full.Render(window);
            double k = Math.Min(1, Math.Min(maxW / w, maxH / h));
            if (k >= 1) return full;
            var size = new PixelSize(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)));
            var small = new RenderTargetBitmap(size);
            using (var ctx = small.CreateDrawingContext())
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(full, new Rect(0, 0, w, h), new Rect(0, 0, size.Width, size.Height));
            full.Dispose();
            return small;
        }
        catch (Exception) { return null; }
    }
}
