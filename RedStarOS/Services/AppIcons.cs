using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RedStarOS.Services;

/// <summary>
/// One place that decides what an app looks like. The taskbar, Start menu, switcher, notification centre, Settings
/// and toasts all use it, so an app has the same icon everywhere: its picture (Assets/apps/{id}.png, drawn from
/// branding/src/apps), else its line glyph or initial on the graphite plate the pictures are drawn on.
/// </summary>
public static class AppIcons
{
    // icon names from Services/Icons (vector line icons)
    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["studio"] = "clipboard", ["writer"] = "document", ["sheets"] = "sheet", ["slides"] = "slides", ["video"] = "film", ["cdplayer"] = "disc", ["pong"] = "game",
        ["online"] = "globe", ["messenger"] = "chat", ["files"] = "folder", ["calculator"] = "calculator", ["notes"] = "note",
        ["imageviewer"] = "image", ["camera"] = "camera", ["calendar"] = "calendar", ["settings"] = "settings", ["terminal"] = "terminal",
        ["paint"] = "palette", ["clock"] = "clock", ["taskmgr"] = "activity",
        ["blocks"] = "blocks", ["mahjongg"] = "tiles", ["gems"] = "gem", ["screenshot"] = "screenshot", ["recorder"] = "record", ["voicememos"] = "mic", ["disks"] = "drive", ["software"] = "bag"
    };

    /// <summary>The graphite of the app pictures' tiles, top to bottom, for plates drawn in code.</summary>
    public static readonly IBrush Plate = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#2A2A2F"), 0), new GradientStop(Color.Parse("#0C0C0E"), 1) },
    }.ToImmutable();

    /// <summary>The plate's fine light rim.</summary>
    public static readonly IBrush Rim = new ImmutableSolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    private static readonly Dictionary<string, Bitmap?> Pictures = new();

    /// <summary>An app's picture, or null for an app without one.</summary>
    public static Bitmap? Picture(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        lock (Pictures)
        {
            if (Pictures.TryGetValue(appId, out var b)) return b;
            var uri = new System.Uri($"avares://RedStarOS/Assets/apps/{appId}.png");
            try { b = AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null; }
            catch (System.Exception e) when (e is System.IO.IOException or System.InvalidOperationException) { b = null; }
            return Pictures[appId] = b;
        }
    }

    public static string GlyphFor(string? appId, string fallbackName)
    {
        if (appId != null && Glyphs.TryGetValue(appId, out var g)) return g;
        return string.IsNullOrEmpty(fallbackName) ? "•" : fallbackName[..1].ToUpperInvariant();
    }

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>An app's icon at a size: its picture, else its glyph on the plate.</summary>
    public static Control Create(string? appId, string name, double size = 28)
    {
        if (Picture(appId) is { } bmp)
        {
            var image = new Image { Source = bmp, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            return image;
        }
        return Tile(GlyphFor(appId, name), size);
    }

    /// <summary>A glyph or an initial, white on the graphite plate (as the pictures are drawn: a 240/256 tile).</summary>
    public static Control Tile(string glyph, double size = 28)
    {
        double inner = size * 240 / 256;
        return new Border
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            Child = new Border
            {
                Width = inner, Height = inner,
                CornerRadius = new CornerRadius(inner * 0.225),
                Background = Plate, BorderBrush = Rim, BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = Icons.Glyph(glyph, size * 0.5, Brushes.White),
            },
        };
    }
}
