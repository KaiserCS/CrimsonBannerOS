using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Alt+Tab: every window (Red Star apps and any other program), most recent first.
/// Hold Alt and press Tab (Shift+Tab) to move, release Alt to switch, Esc to cancel; clicking a tile works too.
/// </summary>
public sealed class WindowSwitcher : Window
{
    private static WindowSwitcher? _open;
    private static bool _listing, _pendingCommit, _pendingCancel;
    private static int _pendingMoves;
    private readonly List<X11Desktop.XWin> _wins;
    private readonly List<Border> _tiles = new();
    private int _index;

    private WindowSwitcher(List<X11Desktop.XWin> wins, int start)
    {
        _wins = wins;
        WindowDecorations = WindowDecorations.None;
        WaylandShell.Frameless(this, "Crimson Banner window switcher");
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        bool opaque = ThemeService.OpaquePopups;             // no compositor: transparent corners would be black
        Background = opaque ? new SolidColorBrush(Color.FromRgb(24, 24, 28)) : Brushes.Transparent;
        if (!opaque) TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var wrap = new WrapPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < wins.Count; i++)
        {
            int n = i;
            var w = wins[i];
            var task = TaskFor(w.Id);
            var foreign = WindowManager.Tasks.OfType<X11Task>().FirstOrDefault(t => t.Window == w.Id);
            Control icon = task?.AppId != null ? AppIcons.Create(task.AppId, w.Title, 52)
                : foreign?.Icon is { } img ? new Image { Source = img, Width = 52, Height = 52 }          // the program's own icon
                : AppIcons.Tile(string.IsNullOrEmpty(w.WmClass) ? "?" : w.WmClass[..1].ToUpperInvariant(), 52);
            var tile = new Border
            {
                Width = 150, Height = 120, Margin = new Thickness(6), CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Child = new StackPanel
                {
                    Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        icon,
                        new TextBlock { Text = string.IsNullOrWhiteSpace(w.Title) ? w.WmClass : w.Title, Foreground = Brushes.White, FontSize = 12,
                            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis },
                    }
                }
            };
            tile.PointerPressed += (_, _) => { _index = n; Commit(); X11Desktop.CancelSwitcher(); };
            _tiles.Add(tile);
            wrap.Children.Add(tile);
        }
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(opaque ? (byte)255 : (byte)225, 24, 24, 28)), CornerRadius = new CornerRadius(opaque ? 0 : 14), Padding = new Thickness(14),
            Child = wrap
        };
        _index = Math.Clamp(start, 0, Math.Max(0, wins.Count - 1));
        Highlight();
    }

    private static TaskItem? TaskFor(long xid) =>
        WindowManager.Tasks.OfType<NativeTask>().FirstOrDefault(t =>
            t.Window.TryGetPlatformHandle()?.Handle.ToInt64() == xid || WaylandShell.CompIdOf(t.Window) == xid);

    private void Highlight()
    {
        var accent = Application.Current?.FindResource("ShellAccentBrush") as IBrush ?? Brushes.DodgerBlue;
        for (int i = 0; i < _tiles.Count; i++) _tiles[i].BorderBrush = i == _index ? accent : Brushes.Transparent;
    }

    private void Move(int d)
    {
        if (_wins.Count == 0) return;
        _index = (_index + d + _wins.Count) % _wins.Count;
        Highlight();
    }

    private void Commit()
    {
        if (_index >= 0 && _index < _wins.Count)
        {
            var w = _wins[_index];
            var task = TaskFor(w.Id);
            if (task != null) task.Activate(); else X11Desktop.Activate(w.Id);
        }
        Close();
    }

    /// <summary>Handles the switcher shortcuts from <see cref="X11Desktop"/>.</summary>
    public static void Handle(string cmd)
    {
        switch (cmd)
        {
            case "switcher-open":
            case "switcher-open-prev":
                bool prev = cmd.EndsWith("prev");
                _listing = true; _pendingCommit = _pendingCancel = false; _pendingMoves = 0;
                X11Desktop.ListWindows(list =>
                {
                    _listing = false;
                    long self = WindowManager.Desktop?.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0;
                    list = list.Where(w => w.Id != self).ToList();
                    if (list.Count == 0) { X11Desktop.CancelSwitcher(); return; }
                    _open?.Close();
                    if (_pendingCancel) return;
                    int start = prev ? list.Count - 1 : (list.Count > 1 ? 1 : 0);
                    start = ((start + _pendingMoves) % list.Count + list.Count) % list.Count;
                    if (_pendingCommit)
                    {
                        // Alt was released before the list arrived (a quick Alt+Tab): just switch
                        var w = list[start];
                        var task = TaskFor(w.Id);
                        if (task != null) task.Activate(); else X11Desktop.Activate(w.Id);
                        return;
                    }
                    _open = new WindowSwitcher(list, start);
                    _open.Closed += (s, _) => { if (ReferenceEquals(_open, s)) _open = null; };
                    _open.Show();
                });
                break;
            case "switcher-next": if (_open != null) _open.Move(1); else if (_listing) _pendingMoves++; break;
            case "switcher-prev": if (_open != null) _open.Move(-1); else if (_listing) _pendingMoves--; break;
            case "switcher-commit": if (_open != null) _open.Commit(); else if (_listing) _pendingCommit = true; break;
            case "switcher-cancel": if (_open != null) _open.Close(); else if (_listing) _pendingCancel = true; break;
        }
    }
}
