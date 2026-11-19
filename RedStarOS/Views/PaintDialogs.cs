using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SkiaSharp;

namespace RedStarOS.Views;

public sealed record ResizeResult(int Width, int Height, double SkewX, double SkewY);

/// <summary>The small modal dialogs of Paint: Resize and Skew, Canvas size, Edit colours.</summary>
public static class PaintDialogs
{
    private static Window MakeWindow(string title, double width, StackPanel body) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = body
    };

    private static StackPanel Row(string label, Control c, double labelWidth = 120)
    {
        c.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center }, c }
        };
    }

    private static TextBox Box(string text, double width = 90) => new() { Text = text, Width = width };

    private static StackPanel Buttons(out Button ok, out Button cancel)
    {
        ok = new Button { Content = "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel }
        };
    }

    private static bool TryInt(string? s, out int v) =>
        int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    private static bool TryDouble(string? s, out double v) =>
        double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Resize (percentage or pixels, optionally keeping the aspect ratio) and skew.</summary>
    public static async Task<ResizeResult?> ResizeSkew(Window owner, int curW, int curH, bool whatIsSelection)
    {
        ResizeResult? result = null;
        var percent = new RadioButton { Content = "Percentage", IsChecked = true, GroupName = "unit" };
        var pixels = new RadioButton { Content = "Pixels", GroupName = "unit" };
        var hBox = Box("100");
        var vBox = Box("100");
        var keep = new CheckBox { Content = "Maintain aspect ratio", IsChecked = true };
        var skewX = Box("0");
        var skewY = Box("0");
        var error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        bool busy = false;

        void Sync(bool fromH)
        {
            if (busy || keep.IsChecked != true) return;
            busy = true;
            try
            {
                if (fromH && TryDouble(hBox.Text, out var h))
                    vBox.Text = percent.IsChecked == true ? Fmt(h) : Fmt(Math.Round(h * curH / curW));
                else if (!fromH && TryDouble(vBox.Text, out var v))
                    hBox.Text = percent.IsChecked == true ? Fmt(v) : Fmt(Math.Round(v * curW / curH));
            }
            finally { busy = false; }
        }
        hBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Sync(true); };
        vBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Sync(false); };
        percent.IsCheckedChanged += (_, _) =>
        {
            busy = true;
            if (percent.IsChecked == true) { hBox.Text = "100"; vBox.Text = "100"; }
            else { hBox.Text = curW.ToString(); vBox.Text = curH.ToString(); }
            busy = false;
        };

        var buttons = Buttons(out var ok, out var cancel);
        var win = MakeWindow(whatIsSelection ? "Resize and Skew selection" : "Resize and Skew", 380, new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Resize", FontWeight = FontWeight.SemiBold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { percent, pixels } },
                Row("Horizontal", hBox), Row("Vertical", vBox), keep,
                new TextBlock { Text = "Skew (degrees)", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                Row("Horizontal", skewX), Row("Vertical", skewY),
                error, buttons
            }
        });

        void Accept()
        {
            if (!TryDouble(hBox.Text, out var h) || !TryDouble(vBox.Text, out var v) ||
                !TryDouble(skewX.Text, out var sx) || !TryDouble(skewY.Text, out var sy) || h <= 0 || v <= 0)
            { error.Text = "Enter positive numbers for the size and plain numbers for the skew."; error.IsVisible = true; return; }
            if (Math.Abs(sx) > 89 || Math.Abs(sy) > 89)
            { error.Text = "Skew must be between -89 and 89 degrees."; error.IsVisible = true; return; }
            int w = percent.IsChecked == true ? (int)Math.Round(curW * h / 100) : (int)Math.Round(h);
            int hh = percent.IsChecked == true ? (int)Math.Round(curH * v / 100) : (int)Math.Round(v);
            if (w < 1 || hh < 1 || w > 8000 || hh > 8000)
            { error.Text = "The result must be between 1 and 8000 pixels on each side."; error.IsVisible = true; return; }
            result = new ResizeResult(w, hh, sx, sy);
            win.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => win.Close();
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); else if (e.Key == Key.Enter) Accept(); };
        await win.ShowDialog(owner);
        return result;
    }

    private static string Fmt(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Canvas width and height in pixels.</summary>
    public static async Task<(int W, int H)?> CanvasSize(Window owner, int curW, int curH)
    {
        (int, int)? result = null;
        var wBox = Box(curW.ToString());
        var hBox = Box(curH.ToString());
        var keep = new CheckBox { Content = "Maintain aspect ratio" };
        var error = new TextBlock { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        bool busy = false;
        wBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || busy || keep.IsChecked != true || !TryInt(wBox.Text, out var w)) return;
            busy = true; hBox.Text = Math.Max(1, (int)Math.Round(w * (double)curH / curW)).ToString(); busy = false;
        };
        hBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || busy || keep.IsChecked != true || !TryInt(hBox.Text, out var h)) return;
            busy = true; wBox.Text = Math.Max(1, (int)Math.Round(h * (double)curW / curH)).ToString(); busy = false;
        };

        var buttons = Buttons(out var ok, out var cancel);
        var win = MakeWindow("Canvas size", 330, new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children = { Row("Width (px)", wBox), Row("Height (px)", hBox), keep, error, buttons }
        });
        void Accept()
        {
            if (!TryInt(wBox.Text, out var w) || !TryInt(hBox.Text, out var h) || w < 1 || h < 1 || w > 8000 || h > 8000)
            { error.Text = "Width and height must be whole numbers between 1 and 8000."; error.IsVisible = true; return; }
            result = (w, h);
            win.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => win.Close();
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); else if (e.Key == Key.Enter) Accept(); };
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>Red/green/blue sliders with a hex field and live preview.</summary>
    public static async Task<SKColor?> EditColor(Window owner, SKColor initial)
    {
        SKColor? result = null;
        Slider Make(double v) => new() { Minimum = 0, Maximum = 255, Value = v, Width = 190, TickFrequency = 1, IsSnapToTickEnabled = true };
        var r = Make(initial.Red);
        var g = Make(initial.Green);
        var b = Make(initial.Blue);
        var rv = new TextBlock { Width = 32, VerticalAlignment = VerticalAlignment.Center };
        var gv = new TextBlock { Width = 32, VerticalAlignment = VerticalAlignment.Center };
        var bv = new TextBlock { Width = 32, VerticalAlignment = VerticalAlignment.Center };
        var hex = new TextBox { Width = 100 };
        var preview = new Border { Height = 44, CornerRadius = new CornerRadius(6), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        bool busy = false;

        void FromSliders()
        {
            if (busy) return;
            busy = true;
            int rr = (int)r.Value, gg = (int)g.Value, bb = (int)b.Value;
            rv.Text = rr.ToString(); gv.Text = gg.ToString(); bv.Text = bb.ToString();
            hex.Text = $"#{rr:X2}{gg:X2}{bb:X2}";
            preview.Background = new SolidColorBrush(Color.FromRgb((byte)rr, (byte)gg, (byte)bb));
            busy = false;
        }
        foreach (var s in new[] { r, g, b })
            s.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) FromSliders(); };
        hex.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || busy) return;
            if (Color.TryParse((hex.Text ?? "").Trim(), out var c))
            {
                busy = true;
                r.Value = c.R; g.Value = c.G; b.Value = c.B;
                rv.Text = c.R.ToString(); gv.Text = c.G.ToString(); bv.Text = c.B.ToString();
                preview.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                busy = false;
            }
        };
        FromSliders();

        StackPanel Line(string name, Slider s, TextBlock v) => new()
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new TextBlock { Text = name, Width = 44, VerticalAlignment = VerticalAlignment.Center }, s, v }
        };

        var buttons = Buttons(out var ok, out var cancel);
        var win = MakeWindow("Edit colour", 380, new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children = { preview, Line("Red", r, rv), Line("Green", g, gv), Line("Blue", b, bv), Row("Hex", hex, 44), buttons }
        });
        void Accept()
        {
            result = new SKColor((byte)(int)r.Value, (byte)(int)g.Value, (byte)(int)b.Value);
            win.Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => win.Close();
        win.KeyDown += (_, e) => { if (e.Key == Key.Escape) win.Close(); };
        await win.ShowDialog(owner);
        return result;
    }
}
