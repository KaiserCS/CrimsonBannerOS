using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RedStarOS.Services;

/// <summary>
/// One place that decides what an app looks like (glyph + colour). The taskbar, Start menu,
/// notification centre, Settings hub and toasts all use it, so an app has the same icon everywhere.
/// </summary>
public static class AppIcons
{
    // icon names from Services/Icons (vector line icons)
    private static readonly Dictionary<string, string> Glyphs = new()
    {
        ["studio"] = "clipboard", ["writer"] = "document", ["sheets"] = "sheet", ["slides"] = "slides", ["video"] = "film", ["pong"] = "game",
        ["online"] = "globe", ["messenger"] = "chat", ["files"] = "folder", ["calculator"] = "calculator", ["notes"] = "note",
        ["imageviewer"] = "image", ["camera"] = "camera", ["calendar"] = "calendar", ["settings"] = "settings", ["terminal"] = "terminal",
        ["paint"] = "palette", ["clock"] = "clock", ["taskmgr"] = "activity",
        ["blocks"] = "blocks", ["mahjongg"] = "tiles", ["gems"] = "gem", ["screenshot"] = "screenshot", ["recorder"] = "record", ["voicememos"] = "mic", ["disks"] = "drive", ["software"] = "bag"
    };

    private static readonly Dictionary<string, string> CategoryColors = new()
    {
        ["RSOffice"] = "#2B6CB0", ["RSMedia"] = "#C8102E", ["RSGames"] = "#2F855A",
        ["RSOnline"] = "#6B46C1", ["RSUtilities"] = "#DD6B20", ["System"] = "#4A5568"
    };

    private static readonly string[] LinuxColors =
        { "#3182CE", "#38A169", "#D69E2E", "#DD6B20", "#805AD5", "#319795", "#D53F8C", "#5A67D8" };

    public const string NeutralColor = "#4A5568";

    public static string GlyphFor(string? appId, string fallbackName)
    {
        if (appId != null && Glyphs.TryGetValue(appId, out var g)) return g;
        return string.IsNullOrEmpty(fallbackName) ? "•" : fallbackName[..1].ToUpperInvariant();
    }

    public static string ColorFor(string? appId)
    {
        if (appId == "terminal") return CategoryColors["RSUtilities"];
        var cat = appId == null ? null : AppRegistry.Find(appId)?.Category;
        return cat != null && CategoryColors.TryGetValue(cat, out var c) ? c : NeutralColor;
    }

    public static string ColorForCategory(string category) =>
        CategoryColors.TryGetValue(category, out var c) ? c : NeutralColor;

    public static string ColorForName(string name) =>
        LinuxColors[name.Sum(ch => (int)ch) % LinuxColors.Length];

    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>A rounded coloured tile with the app's glyph.</summary>
    public static Control Create(string? appId, string name, double size = 28) =>
        Tile(GlyphFor(appId, name), Brush(ColorFor(appId)), size);

    public static Control Tile(string glyph, IBrush background, double size = 28) =>
        new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.28),
            Background = background,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Icons.Glyph(glyph, size * 0.58, Brushes.White)
        };
}
