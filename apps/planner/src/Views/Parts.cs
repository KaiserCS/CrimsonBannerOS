// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace CrimsonPlanner.Views;

/// <summary>Planner's building blocks, in the shell's shared styles (Styles/Shell.axaml): panels, small capitals,
/// pills, badges and the red digits of the deck's display.</summary>
public sealed partial class PlannerWindow
{
    private static TextBlock Kicker(string text, bool accent = false)
    {
        var t = new TextBlock { Text = text, Classes = { "kicker" }, TextWrapping = TextWrapping.Wrap };
        if (accent) t.Classes.Add("accent");
        return t;
    }

    private static TextBlock Muted(string text) =>
        new() { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

    private static Button Pill(string text, bool accent = false)
    {
        var b = new Button { Content = text.ToUpperInvariant(), Classes = { "pill" } };
        if (accent) b.Classes.Add("accent");
        return b;
    }

    private static Border Panel(Control child) => new() { Classes = { "panel" }, Child = child };

    private static Border Badge(string text, bool accent = false)
    {
        var b = new Border { Classes = { "badge" }, Child = new TextBlock { Text = text }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        if (accent) b.Classes.Add("accent");
        return b;
    }

    /// <summary>Figures in the deck's red digits.</summary>
    private static Border Lcd(params (string Label, string Value)[] figures)
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

    private static Control Labelled(string label, Control box)
    {
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(Kicker(label.ToUpperInvariant()));
        s.Children.Add(box);
        return s;
    }

    /// <summary>A table: a grid with a header row of small capitals (a trailing comma: an unlabelled last column).</summary>
    private static Grid Table(string headers)
    {
        var names = headers.Split(',');
        var g = new Grid { ColumnSpacing = 14, RowSpacing = 6 };
        foreach (var _ in names) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < names.Length; c++)
        {
            if (names[c].Length == 0) continue;
            var h = Kicker(names[c].ToUpperInvariant());
            h.MaxWidth = 150;
            h.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(h, c);
            g.Children.Add(h);
        }
        return g;
    }

    private static void Cells(Grid g, int row, params Control[] cells)
    {
        while (g.RowDefinitions.Count <= row) g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < cells.Length; c++)
        {
            while (g.ColumnDefinitions.Count <= c) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetRow(cells[c], row);
            Grid.SetColumn(cells[c], c);
            g.Children.Add(cells[c]);
        }
    }

    private static TextBlock Cell(string text, bool right = false, bool bold = false) => new()
    {
        Text = text, VerticalAlignment = VerticalAlignment.Center, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
    };

    private static TextBlock Mono(string text) => new()
    {
        Text = text, FontFamily = new FontFamily("monospace"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" },
    };

    /// <summary>A text field that keeps its value when it loses focus (or on Enter), and saves the plan.</summary>
    private TextBox Text(string value, Action<string> set, double width)
    {
        var tb = new TextBox { Text = value, Width = width };
        string current = value;
        void Commit()
        {
            var t = tb.Text?.Trim() ?? "";
            if (t.Length == 0 || t == current) { tb.Text = current; return; }
            set(t);
            current = t;
            Changed();
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return tb;
    }

    /// <summary>A number field (0 or more); empty only where allowed (then null: not known).</summary>
    private TextBox Num(double? value, Action<double?> set, double width = 90, bool allowEmpty = false, bool save = true)
    {
        var tb = new TextBox { Text = Raw(value), Width = width, HorizontalContentAlignment = HorizontalAlignment.Right };
        double? current = value;
        void Commit()
        {
            var t = tb.Text?.Trim() ?? "";
            double? v;
            if (t.Length == 0 && allowEmpty) v = null;
            else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && !double.IsInfinity(d)) v = d;
            else { tb.Text = Raw(current); return; }
            if (v == current) return;
            current = v;
            set(v);
            if (save) Changed();
        }
        tb.LostFocus += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        return tb;
    }

    private static string Raw(double? v) => v is double d ? d.ToString("0.######", CultureInfo.InvariantCulture) : "";
}
