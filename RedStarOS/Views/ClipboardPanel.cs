using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Clipboard history (Super+V): click an entry to copy it again; pin entries you reuse.</summary>
public sealed class ClipboardPanel : UserControl, IShellPanel
{
    public event Action? CloseRequested;
    private readonly StackPanel _list = new() { Spacing = 4 };

    public ClipboardPanel()
    {
        Width = 360;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = new Button { Content = "Clear", Classes = { "flat" } };
        clear.Click += (_, _) => ClipboardHistory.Clear();
        var pause = new CheckBox { Content = "Pause", IsChecked = ClipboardHistory.Paused, Margin = new Thickness(0, 0, 8, 0) };
        pause.IsCheckedChanged += (_, _) => ClipboardHistory.Paused = pause.IsChecked == true;
        DockPanel.SetDock(clear, Dock.Right); DockPanel.SetDock(pause, Dock.Right);
        header.Children.Add(clear); header.Children.Add(pause);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { Icons.Create("clipboard", 18), new TextBlock { Text = "Clipboard", FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center } }
        });
        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                header,
                new ScrollViewer { Content = _list, MaxHeight = 420 },
                new TextBlock { Text = "Kept in memory only. Super+V opens this list.", FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0) }
            }
        };
        Fill();
        ClipboardHistory.Changed += Fill;
        DetachedFromVisualTree += (_, _) => ClipboardHistory.Changed -= Fill;
    }

    private void Fill()
    {
        _list.Children.Clear();
        var items = ClipboardHistory.Items.OrderByDescending(ClipboardHistory.IsPinned).ToList();
        if (items.Count == 0) { _list.Children.Add(new TextBlock { Text = "Nothing copied yet.", Opacity = 0.6 }); return; }
        foreach (var it in items)
        {
            var text = it;
            var preview = text.Replace("\r", "").Trim();
            if (preview.Length > 220) preview = preview[..220] + "…";
            var pin = new Button { Content = Icons.Create(ClipboardHistory.IsPinned(text) ? "star-filled" : "star", 14), Classes = { "flat" }, Padding = new Thickness(4) };
            ToolTip.SetTip(pin, ClipboardHistory.IsPinned(text) ? "Unpin" : "Pin (kept when clearing)");
            pin.Click += (_, e) => { e.Handled = true; ClipboardHistory.TogglePin(text); };
            var del = new Button { Content = Icons.Create("close", 12), Classes = { "flat" }, Padding = new Thickness(4) };
            ToolTip.SetTip(del, "Remove");
            del.Click += (_, e) => { e.Handled = true; ClipboardHistory.Remove(text); };
            var row = new DockPanel();
            DockPanel.SetDock(del, Dock.Right); DockPanel.SetDock(pin, Dock.Right);
            row.Children.Add(del); row.Children.Add(pin);
            row.Children.Add(new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var card = new Border
            {
                Child = row, Padding = new Thickness(10, 6), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(card, "Copy again");
            card.PointerPressed += (_, _) => { ClipboardHistory.Use(text); CloseRequested?.Invoke(); };
            _list.Children.Add(card);
        }
    }
}
