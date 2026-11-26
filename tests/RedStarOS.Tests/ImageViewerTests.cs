// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RedStarOS.Services;
using RedStarOS.Views;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>The Image Viewer's fitting (shrink only, or zoom to fit) and Ctrl+arrow scrolling of a zoomed picture.</summary>
public class ImageViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "viewer-test-" + Guid.NewGuid().ToString("N"));
    private readonly bool _was = SettingsStore.Current.ImageZoomToFit;
    public ImageViewerTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        SettingsStore.Current.ImageZoomToFit = _was;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Picture(string name, int w, int h)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(w, h));
        using (var ctx = bmp.CreateDrawingContext()) ctx.FillRectangle(Brushes.Teal, new Rect(0, 0, w, h));
        var path = Path.Combine(_dir, name);
        bmp.Save(path);
        return path;
    }

    private static void Press(ImageViewerWindow w, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        w.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = w });
        Dispatcher.UIThread.RunJobs();
    }

    private static ImageViewerWindow Open(string path)
    {
        var w = new ImageViewerWindow { Width = 900, Height = 700 };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        w.LoadFile(path);
        Dispatcher.UIThread.RunJobs();
        return w;
    }

    [AvaloniaFact]
    public void Fit_leaves_a_small_picture_its_size_and_zoom_to_fit_fills_the_window()
    {
        SettingsStore.Current.ImageZoomToFit = false;
        var w = Open(Picture("small.png", 200, 150));
        try
        {
            Assert.Equal(1, w.Zoom, 3);                              // fit: never enlarged
            Press(w, Key.F, KeyModifiers.Shift);                      // zoom to fit
            Assert.True(w.Zoom > 2, $"zoom {w.Zoom}");
            Assert.True(SettingsStore.Current.ImageZoomToFit);        // kept for the next picture
            Press(w, Key.F);
            Assert.Equal(1, w.Zoom, 3);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void Ctrl_arrows_scroll_a_zoomed_picture_and_plain_arrows_still_change_pictures()
    {
        SettingsStore.Current.ImageZoomToFit = false;
        Picture("a-first.png", 300, 200);
        var w = Open(Picture("b-big.png", 3000, 2000));
        try
        {
            Press(w, Key.D0);                                         // actual size: far bigger than the window
            Assert.Equal(1, w.Zoom, 3);
            var start = w.ScrollOffset;
            Press(w, Key.Right, KeyModifiers.Control);
            Assert.True(w.ScrollOffset.X > start.X, $"{start} → {w.ScrollOffset}");
            Press(w, Key.Down, KeyModifiers.Control);
            Assert.True(w.ScrollOffset.Y > start.Y, $"{start} → {w.ScrollOffset}");
            for (int i = 0; i < 40; i++) Press(w, Key.Left, KeyModifiers.Control);
            Assert.Equal(0, w.ScrollOffset.X);                        // stops at the edge
            Assert.Contains("b-big", w.Title);
            Press(w, Key.Left);                                       // without Ctrl: the previous picture
            Assert.Contains("a-first", w.Title);
        }
        finally { w.Close(); }
    }
}
