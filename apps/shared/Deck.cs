// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CrimsonBanner.Apps;

/// <summary>Our apps' building blocks in the shell's shared styles: panels, small capitals, pills, badges, the deck's
/// red digits and plain tables. <c>using static CrimsonBanner.Apps.Deck;</c></summary>
public static class Deck
{
    public static TextBlock Kicker(string text, bool accent = false)
    {
        var t = new TextBlock { Text = text, Classes = { "kicker" }, TextWrapping = TextWrapping.Wrap };
        if (accent) t.Classes.Add("accent");
        return t;
    }

    public static TextBlock Muted(string text, double maxWidth = 900) =>
        new() { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Left };

    public static Button Pill(string text, bool accent = false)
    {
        var b = new Button { Content = text.ToUpperInvariant(), Classes = { "pill" } };
        if (accent) b.Classes.Add("accent");
        return b;
    }

    public static Border Panel(Control child) => new() { Classes = { "panel" }, Child = child };

    public static Border Badge(string text, bool accent = false)
    {
        var b = new Border { Classes = { "badge" }, Child = new TextBlock { Text = text }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        if (accent) b.Classes.Add("accent");
        return b;
    }

    /// <summary>Figures in the deck's red digits.</summary>
    public static Border Lcd(params (string Label, string Value)[] figures)
    {
        var row = new WrapPanel();
        foreach (var (label, value) in figures)
            row.Children.Add(new StackPanel
            {
                Spacing = 1, Margin = new Thickness(0, 0, 28, 0),
                Children =
                {
                    new TextBlock { Text = label, Classes = { "lcd-label" } },
                    new TextBlock { Text = value, Classes = { "lcd-digits" } },
                },
            });
        return new Border { Classes = { "lcd" }, Child = row, Padding = new Thickness(20, 12), HorizontalAlignment = HorizontalAlignment.Left };
    }

    /// <summary>A page's heading: its kicker, its title and a line about it.</summary>
    public static StackPanel PageColumn(string kicker, string title, string about)
    {
        var col = new StackPanel { Spacing = 16, Margin = new Thickness(22, 18, 28, 32), MaxWidth = 1400, HorizontalAlignment = HorizontalAlignment.Stretch };
        var head = new StackPanel { Spacing = 4 };
        head.Children.Add(Kicker(kicker, accent: true));
        head.Children.Add(new TextBlock { Text = title, Classes = { "display" } });
        head.Children.Add(Muted(about));
        col.Children.Add(head);
        return col;
    }
}
