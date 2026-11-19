using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Numbers pulled from a block of cells: optional category labels and one or more series.</summary>
public sealed class ChartData
{
    public List<string> Categories { get; } = new();
    public List<(string Name, List<double> Values)> Series { get; } = new();

    public static ChartData? From(SheetModel m, int r1, int c1, int r2, int c2)
    {
        if (r2 > r1 + 2000) r2 = r1 + 2000;
        int rows = r2 - r1 + 1, cols = c2 - c1 + 1;
        var v = new object?[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                v[r, c] = m.Eval.Eval(r1 + r, c1 + c);

        bool IsNum(object? o) => o is double;

        // a header row is a first row without numbers above rows that have them
        bool header = false;
        if (rows > 1)
        {
            bool firstHasNum = false, restHasNum = false;
            for (int c = 0; c < cols; c++) if (IsNum(v[0, c])) firstHasNum = true;
            for (int r = 1; r < rows; r++) for (int c = 0; c < cols; c++) if (IsNum(v[r, c])) restHasNum = true;
            header = !firstHasNum && restHasNum;
        }
        int start = header ? 1 : 0;

        // a first column that contains text holds the category labels
        bool catCol = false;
        if (cols > 1)
            for (int r = start; r < rows; r++)
                if (v[r, 0] is string s && s.Length > 0) { catCol = true; break; }

        var d = new ChartData();
        int firstSeries = catCol ? 1 : 0;
        for (int r = start; r < rows; r++)
        {
            if (catCol) d.Categories.Add(Show(v[r, 0]));
            else d.Categories.Add((r - start + 1).ToString(CultureInfo.InvariantCulture));
        }
        for (int c = firstSeries; c < cols; c++)
        {
            var vals = new List<double>();
            bool any = false;
            for (int r = start; r < rows; r++)
            {
                if (v[r, c] is double x) { vals.Add(x); any = true; }
                else vals.Add(0);
            }
            if (!any) continue;
            string name = header ? Show(v[0, c]) : "Series " + (d.Series.Count + 1);
            if (name.Length == 0) name = "Series " + (d.Series.Count + 1);
            d.Series.Add((name, vals));
        }
        return d.Series.Count == 0 ? null : d;
    }

    private static string Show(object? o) => o switch
    {
        null => "",
        double x => FormulaEvaluator.Format(x),
        _ => o.ToString() ?? ""
    };
}

/// <summary>Draws column, bar, line and pie charts.</summary>
public sealed class ChartControl : Control
{
    private static readonly uint[] Palette =
    {
        0xFF4E79A7, 0xFFF28E2B, 0xFFE15759, 0xFF76B7B2, 0xFF59A14F, 0xFFEDC948, 0xFFB07AA1, 0xFFFF9DA7, 0xFF9C755F, 0xFFBAB0AC
    };

    public ChartData? Data { get; set; }
    public string Kind { get; set; } = "Column";
    public string Title { get; set; } = "";

    private static IBrush B(uint c) => new SolidColorBrush(Color.FromUInt32(c));

    private static FormattedText T(string s, double size, IBrush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, brush);

    private static string Fit(string s, int n) => s.Length <= n ? s : s.Substring(0, Math.Max(1, n - 1)) + "…";

    private static double NiceStep(double range, int target)
    {
        if (range <= 0) return 1;
        double raw = range / target;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        double f = n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10;
        return f * mag;
    }

    public override void Render(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height;
        var white = B(0xFFFFFFFF);
        var ink = B(0xFF26262B);
        var soft = B(0xFF777B86);
        ctx.DrawRectangle(white, null, new Rect(0, 0, W, H));
        var d = Data;
        if (d == null || d.Series.Count == 0 || W < 120 || H < 120) return;

        double top = 16;
        if (Title.Length > 0)
        {
            var t = T(Title, 17, ink, true);
            ctx.DrawText(t, new Point((W - t.Width) / 2, 12));
            top = 12 + t.Height + 12;
        }

        bool legend = Kind == "Pie" ? true : d.Series.Count > 1;
        double legendH = 0;
        var legendItems = new List<(string, uint)>();
        if (Kind == "Pie") for (int i = 0; i < d.Categories.Count; i++) legendItems.Add((d.Categories[i], Palette[i % Palette.Length]));
        else for (int i = 0; i < d.Series.Count; i++) legendItems.Add((d.Series[i].Name, Palette[i % Palette.Length]));

        // legend at the bottom, wrapped into rows
        var rowsL = new List<List<(string, uint, double)>>();
        if (legend)
        {
            var cur = new List<(string, uint, double)>();
            double x = 0;
            foreach (var (name, col) in legendItems.Take(30))
            {
                var ft = T(Fit(name, 24), 12, ink);
                double w = ft.Width + 28;
                if (x + w > W - 40 && cur.Count > 0) { rowsL.Add(cur); cur = new List<(string, uint, double)>(); x = 0; }
                cur.Add((Fit(name, 24), col, w));
                x += w;
            }
            if (cur.Count > 0) rowsL.Add(cur);
            legendH = rowsL.Count * 20 + 8;
        }
        if (legend)
        {
            double y = H - legendH + 4;
            foreach (var row in rowsL)
            {
                double total = row.Sum(r => r.Item3);
                double x = (W - total) / 2;
                foreach (var (name, col, w) in row)
                {
                    ctx.DrawRectangle(B(col), null, new Rect(x, y + 3, 12, 12));
                    ctx.DrawText(T(name, 12, ink), new Point(x + 17, y));
                    x += w;
                }
                y += 20;
            }
        }

        double bottom = H - legendH - 10;

        if (Kind == "Pie") { DrawPie(ctx, d, new Rect(20, top, W - 40, Math.Max(40, bottom - top)), ink); return; }

        // ---- axes
        double minV = 0, maxV = 0;
        foreach (var s in d.Series) foreach (var x in s.Values) { minV = Math.Min(minV, x); maxV = Math.Max(maxV, x); }
        if (maxV == minV) maxV = minV + 1;
        double step = NiceStep(maxV - minV, 6);
        double lo = Math.Floor(minV / step) * step, hi = Math.Ceiling(maxV / step) * step;

        bool horizontal = Kind == "Bar";
        int labelW = 0;
        var tickText = new List<(double, string)>();
        for (double v = lo; v <= hi + step * 0.001; v += step)
        {
            var s = FormulaEvaluator.Format(Math.Round(v, 8));
            tickText.Add((v, s));
            labelW = Math.Max(labelW, (int)T(s, 11, soft).Width);
        }

        int n = d.Categories.Count;
        double left = 20 + (horizontal ? Math.Min(140, d.Categories.Max(c => (int)T(Fit(c, 18), 11, soft).Width) + 10) : labelW + 10);
        double right = W - 24;
        double plotTop = top + 6, plotBottom = bottom - (horizontal ? 22 : 26);
        if (plotBottom - plotTop < 40 || right - left < 40) return;

        var gridPen = new Pen(B(0xFFE3E5EA), 1);
        var axisPen = new Pen(B(0xFF9A9EA9), 1);

        if (!horizontal)
        {
            double Y(double v) => plotBottom - (v - lo) / (hi - lo) * (plotBottom - plotTop);
            foreach (var (v, s) in tickText)
            {
                double y = Y(v);
                ctx.DrawLine(gridPen, new Point(left, y), new Point(right, y));
                var ft = T(s, 11, soft);
                ctx.DrawText(ft, new Point(left - 6 - ft.Width, y - ft.Height / 2));
            }
            ctx.DrawLine(axisPen, new Point(left, Y(Math.Max(lo, 0))), new Point(right, Y(Math.Max(lo, 0))));
            double slot = (right - left) / n;
            int every = Math.Max(1, (int)Math.Ceiling(n / Math.Max(1.0, (right - left) / 60)));

            for (int i = 0; i < n; i++)
            {
                double cx = left + slot * (i + 0.5);
                if (i % every == 0)
                {
                    var ft = T(Fit(d.Categories[i], Math.Max(4, (int)(slot * every / 7))), 11, soft);
                    ctx.DrawText(ft, new Point(cx - ft.Width / 2, plotBottom + 6));
                }
                if (Kind == "Column")
                {
                    double group = slot * 0.74, bw = group / d.Series.Count;
                    for (int s = 0; s < d.Series.Count; s++)
                    {
                        double v = d.Series[s].Values[i];
                        double y0 = Y(Math.Max(lo, 0)), y1 = Y(v);
                        double x0 = cx - group / 2 + bw * s;
                        ctx.DrawRectangle(B(Palette[s % Palette.Length]), null, new Rect(x0 + 1, Math.Min(y0, y1), Math.Max(1, bw - 2), Math.Abs(y1 - y0)));
                    }
                }
            }
            if (Kind == "Line")
            {
                for (int s = 0; s < d.Series.Count; s++)
                {
                    var brush = B(Palette[s % Palette.Length]);
                    var pen = new Pen(brush, 2.5);
                    Point? prev = null;
                    for (int i = 0; i < n; i++)
                    {
                        var p = new Point(left + slot * (i + 0.5), Y(d.Series[s].Values[i]));
                        if (prev.HasValue) ctx.DrawLine(pen, prev.Value, p);
                        prev = p;
                    }
                    for (int i = 0; i < n; i++)
                        ctx.DrawEllipse(brush, null, new Point(left + slot * (i + 0.5), Y(d.Series[s].Values[i])), 3.5, 3.5);
                }
            }
        }
        else
        {
            double X(double v) => left + (v - lo) / (hi - lo) * (right - left);
            foreach (var (v, s) in tickText)
            {
                double x = X(v);
                ctx.DrawLine(gridPen, new Point(x, plotTop), new Point(x, plotBottom));
                var ft = T(s, 11, soft);
                ctx.DrawText(ft, new Point(x - ft.Width / 2, plotBottom + 5));
            }
            ctx.DrawLine(axisPen, new Point(X(Math.Max(lo, 0)), plotTop), new Point(X(Math.Max(lo, 0)), plotBottom));
            double slot = (plotBottom - plotTop) / n;
            int every = Math.Max(1, (int)Math.Ceiling(n / Math.Max(1.0, (plotBottom - plotTop) / 20)));
            for (int i = 0; i < n; i++)
            {
                double cy = plotTop + slot * (i + 0.5);
                if (i % every == 0)
                {
                    var ft = T(Fit(d.Categories[i], 18), 11, soft);
                    ctx.DrawText(ft, new Point(left - 6 - ft.Width, cy - ft.Height / 2));
                }
                double group = slot * 0.74, bh = group / d.Series.Count;
                for (int s = 0; s < d.Series.Count; s++)
                {
                    double v = d.Series[s].Values[i];
                    double x0 = X(Math.Max(lo, 0)), x1 = X(v);
                    double y0 = cy - group / 2 + bh * s;
                    ctx.DrawRectangle(B(Palette[s % Palette.Length]), null, new Rect(Math.Min(x0, x1), y0 + 1, Math.Max(1, Math.Abs(x1 - x0)), Math.Max(1, bh - 2)));
                }
            }
        }
    }

    private void DrawPie(DrawingContext ctx, ChartData d, Rect area, IBrush ink)
    {
        var vals = d.Series[0].Values.Select(v => Math.Max(0, v)).ToList();
        double total = vals.Sum();
        if (total <= 0) return;
        double r = Math.Min(area.Width, area.Height) / 2 - 8;
        if (r < 20) return;
        var c = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
        double a0 = -Math.PI / 2;
        var white = new Pen(B(0xFFFFFFFF), 2);
        for (int i = 0; i < vals.Count; i++)
        {
            double frac = vals[i] / total;
            if (frac <= 0) continue;
            double a1 = a0 + frac * Math.PI * 2;
            var brush = B(Palette[i % Palette.Length]);
            if (frac >= 0.9999) ctx.DrawEllipse(brush, white, c, r, r);
            else
            {
                var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
                var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    gc.BeginFigure(c, true);
                    gc.LineTo(p0);
                    gc.ArcTo(p1, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise);
                    gc.EndFigure(true);
                }
                ctx.DrawGeometry(brush, white, g);
            }
            if (frac >= 0.04)
            {
                double mid = (a0 + a1) / 2;
                var ft = T((frac * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%", 12, B(0xFFFFFFFF), true);
                ctx.DrawText(ft, new Point(c.X + r * 0.62 * Math.Cos(mid) - ft.Width / 2, c.Y + r * 0.62 * Math.Sin(mid) - ft.Height / 2));
            }
            a0 = a1;
        }
    }
}

/// <summary>A chart built from the selected cells, with PNG export.</summary>
public sealed class SheetChartWindow : Window
{
    private readonly ChartControl _chart = new();
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "Column", "Bar", "Line", "Pie" }, SelectedIndex = 0, MinWidth = 110 };
    private readonly TextBox _title = new() { PlaceholderText = "Chart title", MinWidth = 220 };

    public SheetChartWindow(ChartData data, string title)
    {
        Title = "Chart";
        Width = 760;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _chart.Data = data;
        _chart.Title = title;
        _title.Text = title;

        var save = new Button { Content = "Save as PNG…" };
        save.Click += async (_, _) => await SavePng();
        _kind.SelectionChanged += (_, _) => { _chart.Kind = _kind.SelectedItem as string ?? "Column"; _chart.InvalidateVisual(); };
        _title.TextChanged += (_, _) => { _chart.Title = _title.Text ?? ""; _chart.InvalidateVisual(); };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10),
            Children = { new TextBlock { Text = "Type", VerticalAlignment = VerticalAlignment.Center }, _kind, _title, save }
        };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(new Border { Margin = new Thickness(10, 0, 10, 10), Child = _chart, ClipToBounds = true });
        Content = dock;
    }

    private async System.Threading.Tasks.Task SavePng()
    {
        var file = await FileDialogs.Save(this, new FilePickerSaveOptions
        {
            Title = "Save chart",
            SuggestedFileName = "Chart.png",
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } }
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";
        try
        {
            var size = new PixelSize(Math.Max(1, (int)_chart.Bounds.Width * 2), Math.Max(1, (int)_chart.Bounds.Height * 2));
            using var rtb = new RenderTargetBitmap(size, new Vector(192, 192));
            rtb.Render(_chart);
            using var fs = File.Create(path);
            rtb.Save(fs, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Save chart", "Could not save the image: " + ex.Message);
        }
    }
}
