// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using Avalonia;
using Avalonia.Controls;

namespace RedStarOS.Views;

/// <summary>
/// Cards in as many equal columns as fit (each at least <see cref="MinItemWidth"/> wide), stretched to fill the
/// width, row after row. Unlike a WrapPanel of fixed-width cards, it leaves no empty strip on the right.
/// </summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<ResponsiveGrid, double>(nameof(MinItemWidth), 280);
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<ResponsiveGrid, double>(nameof(Gap), 14);
    public static readonly StyledProperty<int> MaxColumnsProperty = AvaloniaProperty.Register<ResponsiveGrid, int>(nameof(MaxColumns), 6);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public int MaxColumns { get => GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    static ResponsiveGrid() => AffectsMeasure<ResponsiveGrid>(MinItemWidthProperty, GapProperty, MaxColumnsProperty);

    /// <summary>How many columns fit in a width (at least one).</summary>
    public static int ColumnsFor(double width, double minItem, double gap, int max) =>
        double.IsInfinity(width) || width <= 0 ? 1 : Math.Clamp((int)Math.Floor((width + gap) / (minItem + gap)), 1, Math.Max(1, max));

    private (int Cols, double ItemWidth) Layout(double width)
    {
        var w = double.IsInfinity(width) ? MinItemWidth * 3 + Gap * 2 : width;
        var cols = ColumnsFor(w, MinItemWidth, Gap, MaxColumns);
        return (cols, Math.Max(0, (w - Gap * (cols - 1)) / cols));
    }

    protected override Size MeasureOverride(Size available)
    {
        var (cols, itemW) = Layout(available.Width);
        double total = 0, rowH = 0;
        int i = 0;
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            c.Measure(new Size(itemW, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % cols == 0) { total += rowH + Gap; rowH = 0; }
        }
        if (i % cols != 0) total += rowH; else if (i > 0) total -= Gap;
        var width = double.IsInfinity(available.Width) ? cols * itemW + (cols - 1) * Gap : available.Width;
        return new Size(width, Math.Max(0, total));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (cols, itemW) = Layout(final.Width);
        double y = 0;
        int i = 0;
        var row = new System.Collections.Generic.List<Control>();
        void Flush()
        {
            double h = 0;
            foreach (var c in row) h = Math.Max(h, c.DesiredSize.Height);
            for (int k = 0; k < row.Count; k++) row[k].Arrange(new Rect(k * (itemW + Gap), y, itemW, h));
            y += h + Gap;
            row.Clear();
        }
        foreach (var c in Children)
        {
            if (!c.IsVisible) continue;
            row.Add(c);
            if (++i % cols == 0) Flush();
        }
        if (row.Count > 0) Flush();
        return final;
    }
}
