using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>One tab in a <see cref="ShellTabs"/> strip.</summary>
public sealed class TabSpec
{
    public string Title = "";
    public Control? Icon;              // a favicon or anything else; see IconName for the vector icons
    public string? IconName;
    public bool Active;
    public string? Tooltip;
    public string? Prefix;             // e.g. "●" for unsaved changes
    public bool Attention;             // e.g. a bell in a background terminal tab
    public string? Badge;              // small text after the title (a count)
    public string? TrailingIcon;       // e.g. "volume" for a tab playing sound
    public Action? TrailingClick;
    public Action Select = () => { };
    public Action Close = () => { };
    public string CloseTip = "Close tab";
}

/// <summary>
/// The tab strip used by every tabbed Red Star app (Online, Terminal, Writer), so tabs look and behave the same
/// everywhere: click to switch, middle-click or × to close, accent underline on the active tab.
/// </summary>
public static class ShellTabs
{
    public static void Build(Panel host, IEnumerable<TabSpec> tabs)
    {
        host.Children.Clear();
        foreach (var t in tabs) host.Children.Add(Make(t));
    }

    private static Control Make(TabSpec t)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (t.Attention) row.Children.Add(Icons.Create("bell", 13));
        var icon = t.Icon ?? (t.IconName != null ? Icons.Create(t.IconName, 14) : null);
        if (icon != null) { icon.Opacity = t.Active ? 1 : 0.75; row.Children.Add(icon); }
        row.Children.Add(new TextBlock
        {
            Text = (t.Prefix ?? "") + t.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = t.Active ? FontWeight.SemiBold : FontWeight.Normal
        });
        if (t.Badge != null) row.Children.Add(new TextBlock { Text = t.Badge, FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        if (t.TrailingIcon != null)
        {
            var tb = new Button { Content = Icons.Create(t.TrailingIcon, 13), Padding = new Thickness(3, 0), MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            tb.Click += (_, e) => { e.Handled = true; t.TrailingClick?.Invoke(); };
            row.Children.Add(tb);
        }
        var close = new Button { Content = Icons.Create("close", 11), Classes = { "flat" }, Padding = new Thickness(6, 2), MinWidth = 0 };
        ToolTip.SetTip(close, t.CloseTip);
        AutomationProperties.SetName(close, t.CloseTip + ": " + t.Title);
        close.Click += (_, e) => { e.Handled = true; t.Close(); };
        row.Children.Add(close);

        var accent = Application.Current?.FindResource("ShellAccentBrush") as IBrush ?? new SolidColorBrush(Color.Parse("#C62828"));
        var b = new Border
        {
            Child = row, Padding = new Thickness(10, 4, 4, 4), CornerRadius = new CornerRadius(6, 6, 0, 0),
            Background = t.Active ? new SolidColorBrush(Color.FromArgb(40, 120, 120, 120)) : Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, t.Active ? 2 : 0), BorderBrush = accent,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(b, t.Tooltip ?? t.Title);
        AutomationProperties.SetName(b, t.Title);
        b.PointerEntered += (_, _) => { if (!t.Active) b.Background = new SolidColorBrush(Color.FromArgb(22, 120, 120, 120)); };
        b.PointerExited += (_, _) => { if (!t.Active) b.Background = Brushes.Transparent; };
        b.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(b).Properties;
            if (p.IsMiddleButtonPressed) { t.Close(); return; }
            if (p.IsLeftButtonPressed && !t.Active) t.Select();
        };
        return b;
    }
}
