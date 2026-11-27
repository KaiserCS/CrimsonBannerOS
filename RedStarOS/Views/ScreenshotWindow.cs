// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// A full-screen picture of the screen to drag a rectangle on (Screenshot's and Screen Recorder's "Select an area").
/// The result is the rectangle in the picture's pixels, and as fractions of the whole (for the recorder). Esc cancels.
/// </summary>
public sealed class AreaPickerWindow : Window
{
    private readonly Bitmap _shot;
    private Point? _from;
    private Point _to;
    private readonly TaskCompletionSource<PixelRect?> _done = new();

    public AreaPickerWindow(Bitmap shot)
    {
        _shot = shot;
        Title = "Select an area";
        WindowDecorations = WindowDecorations.None;
        WindowState = WindowState.FullScreen;
        Topmost = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        Background = Brushes.Transparent;       // drawn in Render; a brush keeps the whole window clickable
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Closed += (_, _) => _done.TrySetResult(null);
    }

    public Task<PixelRect?> Result => _done.Task;

    /// <summary>The picture's place in the window (it fills it when the window is the screen's size).</summary>
    private Rect ImageRect()
    {
        double sx = Bounds.Width / _shot.PixelSize.Width, sy = Bounds.Height / _shot.PixelSize.Height, s = Math.Min(sx, sy);
        double w = _shot.PixelSize.Width * s, h = _shot.PixelSize.Height * s;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    private Rect Selection => _from is { } f ? new Rect(f, _to).Normalize() : default;

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        var img = ImageRect();
        ctx.DrawImage(_shot, new Rect(0, 0, _shot.PixelSize.Width, _shot.PixelSize.Height), img);
        var dim = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
        var sel = Selection;
        if (sel.Width < 1 || sel.Height < 1)
        {
            ctx.DrawRectangle(dim, null, img);
            var hint = new FormattedText("Drag to select an area · Esc to cancel", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 20, Brushes.White);
            var box = new Rect((Bounds.Width - hint.Width) / 2 - 16, 40, hint.Width + 32, hint.Height + 16);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)), null, box, 10, 10);
            ctx.DrawText(hint, new Point(box.X + 16, box.Y + 8));
            return;
        }
        // darken all but the selection
        ctx.DrawRectangle(dim, null, new Rect(img.X, img.Y, img.Width, sel.Y - img.Y));
        ctx.DrawRectangle(dim, null, new Rect(img.X, sel.Bottom, img.Width, img.Bottom - sel.Bottom));
        ctx.DrawRectangle(dim, null, new Rect(img.X, sel.Y, sel.X - img.X, sel.Height));
        ctx.DrawRectangle(dim, null, new Rect(sel.Right, sel.Y, img.Right - sel.Right, sel.Height));
        ctx.DrawRectangle(null, new Pen(Brushes.White, 2, new DashStyle(new double[] { 4, 3 }, 0)), sel);
        var px = ToPixels(sel);
        var size = new FormattedText($"{px.Width} × {px.Height}", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 13, Brushes.White);
        var tag = new Rect(sel.X, Math.Max(0, sel.Y - size.Height - 10), size.Width + 12, size.Height + 6);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 24)), null, tag, 4, 4);
        ctx.DrawText(size, new Point(tag.X + 6, tag.Y + 3));
    }

    private PixelRect ToPixels(Rect r)
    {
        var img = ImageRect();
        double s = _shot.PixelSize.Width / img.Width;
        int x = (int)Math.Round((r.X - img.X) * s), y = (int)Math.Round((r.Y - img.Y) * s);
        int w = (int)Math.Round(r.Width * s), h = (int)Math.Round(r.Height * s);
        x = Math.Clamp(x, 0, _shot.PixelSize.Width - 1); y = Math.Clamp(y, 0, _shot.PixelSize.Height - 1);
        w = Math.Clamp(w, 1, _shot.PixelSize.Width - x); h = Math.Clamp(h, 1, _shot.PixelSize.Height - y);
        return new PixelRect(x, y, w, h);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); _from = _to = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); if (_from != null) { _to = e.GetPosition(this); InvalidateVisual(); } }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_from == null) return;
        _to = e.GetPosition(this);
        var sel = Selection;
        if (sel.Width < 4 || sel.Height < 4) { _from = null; InvalidateVisual(); return; }   // a click: start again
        Finish(ToPixels(sel));
    }

    private void Finish(PixelRect? r) { _done.TrySetResult(r); Close(); }

    /// <summary>Cuts <paramref name="r"/> out of the picture into a PNG file.</summary>
    public static void Crop(Bitmap shot, PixelRect r, string file)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(r.Width, r.Height), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
            ctx.DrawImage(shot, new Rect(r.X, r.Y, r.Width, r.Height), new Rect(0, 0, r.Width, r.Height));
        rtb.Save(file);
    }
}

/// <summary>Screenshot: the whole screen or an area, now or after a few seconds; saved in ~/Pictures/Screenshots, then
/// copied, saved elsewhere or opened. (Print and Shift+Print take one straight away.)</summary>
public sealed class ScreenshotWindow : Window
{
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "The whole screen", "An area I select" }, SelectedIndex = 0, MinWidth = 200 };
    private readonly ComboBox _delay = new() { ItemsSource = new[] { "Now", "In 3 seconds", "In 5 seconds", "In 10 seconds" }, SelectedIndex = 0, MinWidth = 200 };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _after = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly StackPanel _empty = new()
    {
        Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), MaxWidth = 320,
        Children =
        {
            new IconView { Kind = "screenshot", Size = 26, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.55 },
            new TextBlock { Text = "No screenshot yet", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "Choose what to take and when, then press Take screenshot. It's saved in Pictures/Screenshots.", Classes = { "caption" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
        },
    };
    private string? _file;

    public ScreenshotWindow()
    {
        Title = "Screenshot";
        Width = 720; Height = 560; MinWidth = 480; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button B(string text, Action act, bool primary = false)
        {
            var b = new Button { Content = text };
            if (primary) b.Classes.Add("primary");
            b.Click += (_, _) => act();
            return b;
        }
        var take = B("Take screenshot", () => _ = Take(), true);
        _after.Children.Add(B("Copy", Copy));
        _after.Children.Add(B("Save as…", () => _ = SaveAs()));
        _after.Children.Add(B("Open", () => { if (_file != null) FileOpener.Open(_file); }));
        static Grid Row(string label, Control c)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(c, 1); c.HorizontalAlignment = HorizontalAlignment.Left;
            g.Children.Add(c);
            return g;
        }
        take.Content = Icons.Label("screenshot", "Take screenshot");
        var header = new AppHeader { AppId = "screenshot", Heading = "Screenshot" };
        header.Children.Add(B("Show in Files", () => WindowManager.Open(new FileManagerWindow(Screenshots.Folder), "files")));
        var top = new StackPanel
        {
            Spacing = 10,
            Children = { header, Row("Take", _mode), Row("When", _delay), take, _status, _after }
        };
        var root = new DockPanel { Margin = new Thickness(18, 16, 18, 18) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var hint = new TextBlock { Classes = { "caption" }, Margin = new Thickness(0, 10, 0, 0),
            Text = "Print takes the whole screen at once, Shift+Print the active window." };
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(new Border { Classes = { "panel" }, Margin = new Thickness(0, 14, 0, 0), ClipToBounds = true,
            Child = new Panel { Children = { _preview, _empty } } });
        Content = root;
    }

    private async Task Take()
    {
        int delay = _delay.SelectedIndex switch { 1 => 3, 2 => 5, 3 => 10, _ => 0 };
        bool area = _mode.SelectedIndex == 1;
        var file = Screenshots.NewPath(DateTime.Now);
        var temp = area ? Path.Combine(Path.GetTempPath(), $"redstar-shot-{Guid.NewGuid():N}.png") : file;
        Hide();
        await Task.Delay(450 + delay * 1000);      // this window is out of the picture by then
        var error = await Screenshots.Capture(temp);
        if (error == null && area)
        {
            using var shot = new Bitmap(temp);
            var picker = new AreaPickerWindow(shot);
            picker.Show();
            var r = await picker.Result;
            try { if (r is { } rect) { Directory.CreateDirectory(Screenshots.Folder); AreaPickerWindow.Crop(shot, rect, file); } }
            catch (Exception ex) { error = "Couldn't cut out the area: " + ex.Message; }
            try { File.Delete(temp); } catch { }
            if (r == null && error == null) { Show(); Activate(); _status.Text = "Cancelled."; return; }
        }
        Show();
        Activate();
        if (error != null) { _status.Text = error; return; }
        _file = file;
        SystemSounds.Play("camera");
        try { using var s = File.OpenRead(file); _preview.Source = new Bitmap(s); } catch { }
        _status.Text = "Saved as " + Path.GetFileName(file) + " in Pictures/Screenshots.";
        _after.IsVisible = true;
        _empty.IsVisible = false;
    }

    private void Copy()
    {
        if (_file == null) return;
        _status.Text = Screenshots.CopyImage(_file) ? "Copied to the clipboard."
            : "Copying pictures needs wl-clipboard (inside redstar-comp) or xclip.";
    }

    private async Task SaveAs()
    {
        if (_file == null) return;
        var to = await FileDialogs.Save(this, new FilePickerSaveOptions { Title = "Save the screenshot", SuggestedFileName = Path.GetFileName(_file), DefaultExtension = "png" });
        if (to == null) return;
        try { File.Copy(_file, to.LocalPath, true); _status.Text = "Saved as " + to.LocalPath; }
        catch (Exception ex) { _status.Text = "Couldn't save: " + ex.Message; }
    }
}
