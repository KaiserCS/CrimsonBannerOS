using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RedStarOS.Views;

/// <summary>One object on a slide: a text box, a shape (optionally with text) or a picture.</summary>
public sealed class SlideItem
{
    public string Type { get; set; } = "text";            // text | shape | image
    public string Shape { get; set; } = "Rectangle";      // a ShapeKind name
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; } = 400;
    public double H { get; set; } = 100;
    public double Rot { get; set; }
    public string Fill { get; set; } = "";                // "#RRGGBB" or "" for none
    public string Stroke { get; set; } = "";
    public double StrokeW { get; set; } = 2;
    public string Text { get; set; } = "";
    public string Placeholder { get; set; } = "";         // grey hint shown only while editing
    public string Font { get; set; } = "Sans";
    public double Size { get; set; } = 32;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Bullets { get; set; }
    public string Color { get; set; } = "#1A1A1A";
    public string Align { get; set; } = "left";           // left | center | right
    public string VAlign { get; set; } = "top";           // top | middle | bottom
    public string ImageId { get; set; } = "";

    public SlideItem Clone() => JsonSerializer.Deserialize<SlideItem>(JsonSerializer.Serialize(this))!;
}

public sealed class Slide
{
    public string Background { get; set; } = "#FFFFFF";
    public string Transition { get; set; } = "None";      // None | Fade | Push
    public string Notes { get; set; } = "";
    public List<SlideItem> Items { get; set; } = new();
}

public sealed class Deck
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public string Theme { get; set; } = "Red Star";
    public List<Slide> Slides { get; set; } = new();
    /// <summary>Embedded pictures by id (base64 PNG/JPEG). Never rolled back by undo.</summary>
    public Dictionary<string, string> Images { get; set; } = new();
}

public sealed record SlideTheme(string Name, string Bg, string Text, string Accent);

public static class SlideThemes
{
    public static readonly SlideTheme[] All =
    {
        new("Red Star", "#FFFFFF", "#1A1A1A", "#C8102E"),
        new("Dark", "#1E1E1E", "#F2F2F2", "#4FA3FF"),
        new("Ocean", "#E8F1FA", "#0B2545", "#1E6FD9"),
        new("Forest", "#F2F7F0", "#1B3A2A", "#2F855A"),
        new("Sunset", "#FFF4E8", "#3A1F0B", "#DD6B20"),
        new("Midnight", "#0B1D3A", "#FFFFFF", "#FFC90E"),
        new("Paper", "#F7F3EA", "#2B2B2B", "#6B46C1"),
    };

    public static SlideTheme Get(string name) => All.FirstOrDefault(t => t.Name == name) ?? All[0];
}

/// <summary>Snapshot used for undo/redo (pictures are not part of it).</summary>
public sealed class DeckState
{
    public string Theme { get; set; } = "Red Star";
    public List<Slide> Slides { get; set; } = new();
}

public static class SlideLayouts
{
    public static readonly string[] Names = { "Title Slide", "Title and Content", "Section Header", "Two Content", "Blank" };

    private static SlideItem Text(double x, double y, double w, double h, double size, bool bold, string color, string align,
                                  string valign, string placeholder, string font, bool bullets = false) => new()
    {
        Type = "text", X = x, Y = y, W = w, H = h, Size = size, Bold = bold, Color = color, Align = align, VAlign = valign,
        Placeholder = placeholder, Font = font, Bullets = bullets
    };

    private static SlideItem Bar(double x, double y, double w, double h, string color) => new()
    {
        Type = "shape", Shape = "Rectangle", X = x, Y = y, W = w, H = h, Fill = color, Stroke = ""
    };

    public static Slide Make(string layout, SlideTheme t, string font)
    {
        var s = new Slide { Background = t.Bg };
        switch (layout)
        {
            case "Title Slide":
                s.Items.Add(Text(140, 200, 1000, 160, 66, true, t.Text, "center", "bottom", "Click to add title", font));
                s.Items.Add(Bar(560, 378, 160, 6, t.Accent));
                s.Items.Add(Text(240, 400, 800, 100, 30, false, t.Text, "center", "top", "Click to add subtitle", font));
                break;
            case "Title and Content":
                s.Items.Add(Text(80, 40, 1120, 110, 46, true, t.Text, "left", "middle", "Click to add title", font));
                s.Items.Add(Bar(92, 156, 140, 6, t.Accent));
                s.Items.Add(Text(80, 190, 1120, 480, 30, false, t.Text, "left", "top", "Click to add text", font, true));
                break;
            case "Section Header":
                s.Items.Add(Bar(0, 0, 44, 720, t.Accent));
                s.Items.Add(Text(110, 240, 1000, 140, 60, true, t.Text, "left", "bottom", "Section title", font));
                s.Items.Add(Text(110, 392, 1000, 90, 28, false, t.Text, "left", "top", "Click to add text", font));
                break;
            case "Two Content":
                s.Items.Add(Text(80, 40, 1120, 110, 46, true, t.Text, "left", "middle", "Click to add title", font));
                s.Items.Add(Bar(92, 156, 140, 6, t.Accent));
                s.Items.Add(Text(80, 190, 540, 480, 26, false, t.Text, "left", "top", "Click to add text", font, true));
                s.Items.Add(Text(660, 190, 540, 480, 26, false, t.Text, "left", "top", "Click to add text", font, true));
                break;
        }
        return s;
    }
}
