// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Task View, as on Windows 10 (Super+Tab, or the button next to Start): the open windows of the desktop in use, and
/// along the bottom every desktop and "New desktop". Click a window to go to it, a desktop to switch to it; point at a
/// desktop to see its windows; drag a window onto a desktop (or "New desktop") to move it there. Right-click for more:
/// rename or close a desktop, move a window, show a window on every desktop. Esc (or Super+Tab again) closes.
/// </summary>
public sealed class TaskViewWindow : Window
{
    public const string WindowTitle = "Crimson Banner Task View";
    public const double CardWidth = 300, ThumbHeight = 180;
    private static TaskViewWindow? _open;

    private readonly WrapPanel _windows = new() { HorizontalAlignment = HorizontalAlignment.Center, Orientation = Orientation.Horizontal };
    private readonly TextBlock _heading = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _empty = new()
    {
        FontSize = 14, Foreground = new SolidColorBrush(Color.Parse("#969AA0")), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 60, 0, 0),
    };
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly List<(Control Card, int Desk)> _deskCards = new();
    private int _shown;                    // the desktop whose windows are shown (the one pointed at, else the current one)
    private int _renaming = -1;
    private readonly bool _live;           // shown on a screen (not just drawn by the tests)

    /// <summary>Super+Tab and the taskbar button: opens it, or closes it when it's open.</summary>
    public static void Toggle()
    {
        if (_open != null) { _open.Close(); return; }
        _open = new TaskViewWindow(live: true);
        _open.Closed += (s, _) => { if (ReferenceEquals(_open, s)) _open = null; };
        _open.Show();
    }

    public static bool IsOpen => _open != null;

    public TaskViewWindow() : this(live: false) { }

    private TaskViewWindow(bool live)
    {
        _live = live;
        Title = WindowTitle;
        WindowDecorations = WindowDecorations.None;
        WaylandShell.Frameless(this, WindowTitle);
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        if (live) WindowState = WindowState.FullScreen;
        bool opaque = ThemeService.OpaquePopups;
        Background = opaque ? new SolidColorBrush(Color.FromRgb(20, 20, 23)) : Brushes.Transparent;
        if (!opaque) TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        _shown = VirtualDesktops.Current;

        var windowsArea = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 18, Margin = new Thickness(40, 48, 40, 24), Children = { _heading, _windows, _empty } },
        };
        var newDesk = NewDesktopCard();
        var stripRow = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stripRow.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { _strip, newDesk } },
        });
        var stripBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 16, 24, 20),
            Child = stripRow,
        };
        stripBar.PointerExited += (_, _) => ShowDesktopWindows(VirtualDesktops.Current);
        DockPanel.SetDock(stripBar, Dock.Bottom);
        var root = new DockPanel { Children = { stripBar, windowsArea } };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(opaque ? (byte)255 : (byte)0xE6, 20, 20, 23)),
            Child = root,
        };

        Rebuild();
        VirtualDesktops.Changed += OnDesktopsChanged;
        WindowManager.Changed += OnDesktopsChanged;
        Closed += (_, _) =>
        {
            VirtualDesktops.Changed -= OnDesktopsChanged;
            WindowManager.Changed -= OnDesktopsChanged;
        };
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnDragMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnDragReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        if (live)
        {
            Deactivated += (_, _) => { if (_renaming < 0 && _drag == null) Close(); };
            Opened += (_, _) =>
            {
                // on every desktop, so switching with it open doesn't hide it
                if (TryGetPlatformHandle()?.Handle.ToInt64() is long xid && xid != 0) VirtualDesktops.Move(WaylandShell.CompIdOf(this) ?? xid, -1);
                Dispatcher.UIThread.Post(() => (_windows.Children.OfType<Button>().FirstOrDefault() as Control ?? this).Focus(), DispatcherPriority.Background);
            };
        }
    }

    private bool _pendingRebuild;
    private void OnDesktopsChanged()
    {
        if (_drag != null || _renaming >= 0) { _pendingRebuild = true; return; }
        if (_shown >= VirtualDesktops.Count) _shown = VirtualDesktops.Current;
        Rebuild();
    }

    private void Rebuild()
    {
        _pendingRebuild = false;
        BuildStrip();
        ShowDesktopWindows(_shown);
    }

    // ─────────── the windows ───────────
    private void ShowDesktopWindows(int desk)
    {
        _shown = desk;
        _windows.Children.Clear();
        var tasks = VirtualDesktops.TasksOn(desk);
        _heading.Text = VirtualDesktops.Count > 1 ? VirtualDesktops.Name(desk) : "Open windows";
        foreach (var t in tasks) _windows.Children.Add(WindowCard(t));
        _empty.IsVisible = tasks.Count == 0;
        _empty.Text = VirtualDesktops.Count > 1
            ? $"Nothing open on {VirtualDesktops.Name(desk)}. Open an app from Start, or drag a window here from another desktop."
            : "Nothing open. Open an app from Start.";
        foreach (var (card, d) in _deskCards) card.Classes.Set("shown", d == desk);
    }

    private Control WindowCard(TaskItem task)
    {
        var icon = task is NativeTask && task.AppId != null ? AppIcons.Create(task.AppId, task.Title, 20)
            : task.Icon is { } img ? new Image { Source = img, Width = 20, Height = 20 } : AppIcons.Tile(task.Title.Length > 0 ? task.Title[..1].ToUpperInvariant() : "?", 20);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 8, 0);
        var close = new Button { Classes = { "cardclose" }, Width = 28, Height = 28, Padding = new Thickness(0), Content = Icons.Glyph("close", 13),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(close, "Close window");
        close.Click += (_, e) => { e.Handled = true; task.Close(); };
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(close);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = task.Title, FontSize = 13, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var picture = new Border
        {
            Width = CardWidth, Height = ThumbHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Child = Picture(task),
        };
        var card = new Button
        {
            Classes = { "viewcard" }, Margin = new Thickness(10), Padding = new Thickness(10, 8, 10, 10),
            Content = new StackPanel { Children = { head, picture } },
            Tag = task,
        };
        if (task.IsMinimized) picture.Opacity = 0.55;
        AutomationProperties(card, task.Title);
        card.Click += (_, _) => { if (_dragMoved) return; Close(); task.Activate(); };
        card.ContextMenu = WindowMenu(task);
        card.AddHandler(PointerPressedEvent, (_, e) => StartDrag(card, task, e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return card;
    }

    private static void AutomationProperties(Control c, string name) => Avalonia.Automation.AutomationProperties.SetName(c, name);

    private static Control Picture(TaskItem task)
    {
        if (task is NativeTask { Window: { } w } && w.Bounds.Width > 1 && TaskPreview.Thumbnail(w, CardWidth * 2, ThumbHeight * 2) is { } bmp)
            return new Image { Source = bmp, Stretch = Stretch.Uniform };
        var icon = task is NativeTask && task.AppId != null ? AppIcons.Create(task.AppId, task.Title, 64)
            : task.Icon is { } img ? new Image { Source = img, Width = 64, Height = 64 } : AppIcons.Tile(task.Title.Length > 0 ? task.Title[..1].ToUpperInvariant() : "?", 64);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    private ContextMenu WindowMenu(TaskItem task)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var item in MoveItems(task, () => { })) menu.Items.Add(item);
            menu.Items.Add(new Separator());
            var close = new MenuItem { Header = "Close window", Icon = Icons.Glyph("close", 16) };
            close.Click += (_, _) => task.Close();
            menu.Items.Add(close);
        };
        return menu;
    }

    /// <summary>"Move to" (each other desktop, and a new one) and "Show on every desktop": shared with the taskbar's menu.</summary>
    public static List<Control> MoveItems(TaskItem task, Action after)
    {
        var items = new List<Control>();
        int at = VirtualDesktops.DeskOf(task);
        var move = new MenuItem { Header = "Move to", Icon = Icons.Glyph("task-view", 16) };
        for (int i = 0; i < VirtualDesktops.Count; i++)
        {
            if (i == at) continue;
            int d = i;
            var mi = new MenuItem { Header = VirtualDesktops.Name(d) };
            mi.Click += (_, _) => { VirtualDesktops.Move(task, d); after(); };
            move.Items.Add(mi);
        }
        if (VirtualDesktops.Count < VirtualDesktops.Max)
        {
            if (move.Items.Count > 0) move.Items.Add(new Separator());
            var nd = new MenuItem { Header = "New desktop", Icon = Icons.Glyph("plus", 16) };
            nd.Click += (_, _) => { int d = VirtualDesktops.Add(); if (d >= 0) VirtualDesktops.Move(task, d); after(); };
            move.Items.Add(nd);
        }
        items.Add(move);
        var all = new MenuItem { Header = at < 0 ? "Show on this desktop only" : "Show on every desktop", Icon = Icons.Glyph("copy", 16) };
        all.Click += (_, _) => { VirtualDesktops.Move(task, at < 0 ? VirtualDesktops.Current : -1); after(); };
        items.Add(all);
        return items;
    }

    // ─────────── the desktops ───────────
    private void BuildStrip()
    {
        _strip.Children.Clear();
        _deskCards.Clear();
        for (int i = 0; i < VirtualDesktops.Count; i++)
        {
            var card = DesktopCard(i);
            _deskCards.Add((card, i));
            _strip.Children.Add(card);
        }
    }

    private Control DesktopCard(int d)
    {
        bool current = d == VirtualDesktops.Current;
        // a little picture of the desktop: its wallpaper, with its windows' icons
        var mini = new Grid { Width = 176, Height = 99 };
        if ((WindowManager.Desktop as DesktopWindow)?.Wallpaper is { } wall)
            mini.Children.Add(new Image { Source = wall, Stretch = Stretch.UniformToFill, Opacity = 0.9 });
        else mini.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2F)) });
        var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var tasks = VirtualDesktops.TasksOn(d).Where(t => VirtualDesktops.DeskOf(t) >= 0).ToList();
        foreach (var t in tasks.Take(5))
            icons.Children.Add(t is NativeTask && t.AppId != null ? AppIcons.Create(t.AppId, t.Title, 26)
                : t.Icon is { } img ? new Image { Source = img, Width = 26, Height = 26 } : AppIcons.Tile(t.Title.Length > 0 ? t.Title[..1].ToUpperInvariant() : "?", 26));
        if (tasks.Count > 5) icons.Children.Add(new TextBlock { Text = $"+{tasks.Count - 5}", Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        mini.Children.Add(icons);
        var picture = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = mini };

        Control label;
        if (_renaming == d)
        {
            var box = new TextBox { Text = VirtualDesktops.Name(d), FontSize = 12.5, MinWidth = 140, Padding = new Thickness(6, 3), HorizontalAlignment = HorizontalAlignment.Center };
            box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); });
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; FinishRename(d, box.Text ?? ""); }
                else if (e.Key == Key.Escape) { e.Handled = true; FinishRename(-1, ""); }
            };
            box.LostFocus += (_, _) => { if (_renaming == d) FinishRename(d, box.Text ?? ""); };
            label = box;
        }
        else label = new TextBlock { Text = VirtualDesktops.Name(d), FontSize = 12.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };

        var body = new Button
        {
            Classes = { "deskcard" }, Padding = new Thickness(6, 6, 6, 8),
            Content = new StackPanel { Spacing = 6, Children = { picture, label } },
        };
        body.Classes.Set("current", current);
        body.Classes.Set("shown", d == _shown);
        AutomationProperties(body, VirtualDesktops.Name(d) + (current ? " (in use)" : ""));
        ToolTip.SetTip(body, current ? $"{VirtualDesktops.Name(d)} (in use)" : $"Switch to {VirtualDesktops.Name(d)}");
        body.Click += (_, _) => { if (_renaming == d) return; Close(); VirtualDesktops.Switch(d); };
        body.PointerEntered += (_, _) => { if (_drag == null && _shown != d) ShowDesktopWindows(d); };
        body.DoubleTapped += (_, _) => StartRename(d);
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Rename", Icon = Icons.Glyph("edit", 16) };
        rename.Click += (_, _) => StartRename(d);
        menu.Items.Add(rename);
        if (VirtualDesktops.Count > 1)
        {
            var close = new MenuItem { Header = "Close desktop", Icon = Icons.Glyph("close", 16) };
            ToolTip.SetTip(close, "Its windows move to the desktop on its left");
            close.Click += (_, _) => VirtualDesktops.Close(d);
            menu.Items.Add(close);
        }
        body.ContextMenu = menu;

        var wrap = new Panel { Children = { body } };
        if (VirtualDesktops.Count > 1)
        {
            var x = new Button
            {
                Classes = { "cardclose", "ondesk" }, Width = 24, Height = 24, Padding = new Thickness(0), Content = Icons.Glyph("close", 12),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(x, "Close desktop (its windows move to the one on its left)");
            x.Click += (_, e) => { e.Handled = true; VirtualDesktops.Close(d); };
            wrap.Children.Add(x);
        }
        wrap.Tag = d;
        return wrap;
    }

    private Control NewDesktopCard()
    {
        var card = new Button
        {
            Classes = { "deskcard", "newdesk" }, Padding = new Thickness(6, 6, 6, 8), VerticalAlignment = VerticalAlignment.Top,
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new Border { Width = 176, Height = 99, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                        Child = Icons.Create("plus", 28, Brushes.White) },
                    new TextBlock { Text = "New desktop", FontSize = 12.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(card, "New desktop (Super+Ctrl+D)");
        card.Click += (_, _) => VirtualDesktops.Add();
        card.Tag = -1;
        _newCard = card;
        return card;
    }

    private Control? _newCard;

    private void StartRename(int d) { _renaming = d; BuildStrip(); }

    private void FinishRename(int d, string name)
    {
        _renaming = -1;
        if (d >= 0) VirtualDesktops.Rename(d, name);
        if (_pendingRebuild) Rebuild(); else BuildStrip();
    }

    // ─────────── dragging a window onto a desktop ───────────
    private sealed class Drag { public required Control Card; public required TaskItem Task; public Point Start; public Control? Over; }
    private Drag? _drag;
    private bool _dragMoved;

    private void StartDrag(Control card, TaskItem task, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // the card's own close button isn't a drag
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is { } b && !ReferenceEquals(b, card)) return;
        _dragMoved = false;
        _drag = new Drag { Card = card, Task = task, Start = e.GetPosition(this) };
    }

    // watched from the window (tunnelling, handled or not): the card keeps the pointer, so its click still works
    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } d) return;
        var p = e.GetPosition(this);
        if (!_dragMoved && Math.Abs(p.X - d.Start.X) + Math.Abs(p.Y - d.Start.Y) < 8) return;
        _dragMoved = true;
        d.Card.Opacity = 0.5;
        var over = DropTargetAt(p);
        if (!ReferenceEquals(over, d.Over))
        {
            if (d.Over != null) Highlight(d.Over, false);
            d.Over = over;
            if (over != null) Highlight(over, true);
        }
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is not { } d) return;
        _drag = null;
        d.Card.Opacity = 1;
        if (d.Over != null) Highlight(d.Over, false);
        if (_dragMoved && d.Over?.Tag is int desk)
        {
            if (desk < 0) desk = VirtualDesktops.Add();
            if (desk >= 0) VirtualDesktops.Move(d.Task, desk);
            Rebuild();
        }
        else if (_pendingRebuild) Rebuild();      // (a plain click is the card's own Click)
        Dispatcher.UIThread.Post(() => _dragMoved = false, DispatcherPriority.Background);
    }

    private Control? DropTargetAt(Point p)
    {
        foreach (var c in _deskCards.Select(x => x.Card).Append(_newCard))
        {
            if (c == null || c.TranslatePoint(new Point(0, 0), this) is not { } at) continue;
            if (new Rect(at, c.Bounds.Size).Contains(p)) return c;
        }
        return null;
    }

    private static void Highlight(Control c, bool on)
    {
        var b = c as Button ?? (c as Panel)?.Children.OfType<Button>().FirstOrDefault(x => x.Classes.Contains("deskcard"));
        b?.Classes.Set("drop", on);
    }

    // ─────────── keys ───────────
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_renaming >= 0) return;
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.Key is Key.Left or Key.Right && e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is Control f)
        {
            // arrows step through the windows, then the desktops
            var order = _windows.Children.OfType<Control>().Concat(_deskCards.Select(x => (x.Card as Panel)?.Children.OfType<Button>().FirstOrDefault() as Control ?? x.Card))
                .Append(_newCard).Where(c => c != null).Select(c => c!).ToList();
            int i = order.FindIndex(c => ReferenceEquals(c, f) || c.IsVisualAncestorOf(f));
            int next = i < 0 ? 0 : Math.Clamp(i + (e.Key == Key.Right ? 1 : -1), 0, order.Count - 1);
            if (order.Count > 0) { order[next].Focus(NavigationMethod.Directional); e.Handled = true; }
        }
    }
}
