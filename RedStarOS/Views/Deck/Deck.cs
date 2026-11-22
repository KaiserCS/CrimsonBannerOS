// SPDX-License-Identifier: GPL-3.0-or-later
// The deck look shared by CD Player and Video: colours, transport glyphs and button builders. Styles/Deck.axaml has
// the styles. After CDPlayer by Kizarov3 (MIT), see NOTICE in this folder.
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace RedStarOS.Views.Deck;

public static class DeckColors
{
    public static readonly Color Bg = Color.FromRgb(17, 17, 19);
    public static readonly Color Accent = Color.FromRgb(196, 20, 28);
    public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
    public static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(240, 32, 40));
    public static readonly IBrush Text = new SolidColorBrush(Color.FromRgb(232, 233, 236));
    public static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(138, 142, 148));
    public static readonly IBrush Hint = new SolidColorBrush(Color.FromRgb(120, 122, 126));
    public static readonly IBrush Rule = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255));
    public static readonly IBrush Surface = new SolidColorBrush(Color.FromArgb(0x0C, 255, 255, 255));

    public static readonly IBrush Backdrop = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(28, 28, 31), 0), new GradientStop(Color.FromRgb(9, 9, 10), 1) }
    };
}

/// <summary>The deck's transport glyphs, on a 24×24 grid. Filled shapes, plus strokes for the arrows.</summary>
public static class DeckGlyphs
{
    private static readonly Dictionary<string, (string? Fill, string? Stroke)> Shapes = new()
    {
        ["play"] = ("M 8.5,5.5 L 8.5,18.5 L 19,12 Z", null),
        ["pause"] = ("M 7,6 L 10.5,6 L 10.5,18 L 7,18 Z M 13.5,6 L 17,6 L 17,18 L 13.5,18 Z", null),
        ["stop"] = ("M 7,7 L 17,7 L 17,17 L 7,17 Z", null),
        ["prev"] = ("M 5.5,6.5 L 8,6.5 L 8,17.5 L 5.5,17.5 Z M 18.5,6.5 L 18.5,17.5 L 9,12 Z", null),
        ["next"] = ("M 16,6.5 L 18.5,6.5 L 18.5,17.5 L 16,17.5 Z M 5.5,6.5 L 5.5,17.5 L 15,12 Z", null),
        ["eject"] = ("M 12,5 L 19.5,13 L 4.5,13 Z M 4.5,15.5 L 19.5,15.5 L 19.5,18.5 L 4.5,18.5 Z", null),
        ["shuffle"] = (null, "M 4,7.5 L 8,7.5 L 16,16.5 L 20,16.5 M 4,16.5 L 8,16.5 L 16,7.5 L 20,7.5 M 17.5,5 L 20,7.5 L 17.5,10 M 17.5,14 L 20,16.5 L 17.5,19"),
        ["repeat"] = (null, "M 5,11.5 L 5,8 L 18.5,8 M 16,5.5 L 18.5,8 L 16,10.5 M 19,12.5 L 19,16 L 5.5,16 M 8,13.5 L 5.5,16 L 8,18.5"),
        ["volume"] = ("M 3.5,9.5 L 7,9.5 L 11.5,5.5 L 11.5,18.5 L 7,14.5 L 3.5,14.5 Z", "M 14.5,9 C 15.8,10.5 15.8,13.5 14.5,15 M 17,6.5 C 19.8,9.5 19.8,14.5 17,17.5"),
        ["mute"] = ("M 3.5,9.5 L 7,9.5 L 11.5,5.5 L 11.5,18.5 L 7,14.5 L 3.5,14.5 Z", "M 15,9.5 L 20,14.5 M 20,9.5 L 15,14.5"),
        ["fullscreen"] = (null, "M 4,9 L 4,4 L 9,4 M 15,4 L 20,4 L 20,9 M 20,15 L 20,20 L 15,20 M 9,20 L 4,20 L 4,15"),
        ["list"] = (null, "M 5,7 L 19,7 M 5,12 L 19,12 M 5,17 L 14,17"),
        ["back5"] = (null, "M 7,8 A 7,7 0 1 1 5.5,13 M 7,3.5 L 7,8 L 11.5,8"),
        ["fwd5"] = (null, "M 17,8 A 7,7 0 1 0 18.5,13 M 17,3.5 L 17,8 L 12.5,8"),
    };

    /// <summary>A glyph that follows the foreground of the button it sits in.</summary>
    public static Control Create(string name, double size = 20)
    {
        var (fill, stroke) = Shapes.TryGetValue(name, out var s) ? s : Shapes["play"];
        var canvas = new Canvas { Width = 24, Height = 24 };
        if (fill != null)
        {
            var p = new ShapePath { Data = Geometry.Parse(fill) };
            p.Bind(Shape.FillProperty, p.GetObservable(TextElement.ForegroundProperty));
            canvas.Children.Add(p);
        }
        if (stroke != null)
        {
            var p = new ShapePath
            {
                Data = Geometry.Parse(stroke), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round
            };
            p.Bind(Shape.StrokeProperty, p.GetObservable(TextElement.ForegroundProperty));
            canvas.Children.Add(p);
        }
        return new Viewbox
        {
            Width = size, Height = size, Child = canvas,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static bool Has(string name) => Shapes.ContainsKey(name);
}

/// <summary>Builders for the deck's controls (the classes are styled in Styles/Deck.axaml).</summary>
public static class DeckUi
{
    public static Button Pill(string text, Action click, string? tip = null, bool small = false)
    {
        var b = new Button { Content = text.ToUpperInvariant() };
        b.Classes.Add("deck-pill");
        if (small) b.Classes.Add("small");
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Key(string glyph, Action click, string tip, bool primary = false)
    {
        var b = new Button { Content = DeckGlyphs.Create(glyph, primary ? 26 : 20) };
        b.Classes.Add("deck-key");
        if (primary) b.Classes.Add("primary");
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    public static TextBlock Text(string text, string cls, double? size = null)
    {
        var t = new TextBlock { Text = text };
        t.Classes.Add(cls);
        if (size is { } s) t.FontSize = s;
        return t;
    }

    /// <summary>A slider in the deck's colours: a red bar on a dim track. (Set on the slider itself, so other
    /// sliders keep the shell's look.)</summary>
    public static void Accent(Slider s)
    {
        var track = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
        foreach (var k in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed" })
            s.Resources[k] = DeckColors.AccentBrush;
        foreach (var k in new[] { "SliderTrackFill", "SliderTrackFillPointerOver", "SliderTrackFillPressed" })
            s.Resources[k] = track;
        foreach (var k in new[] { "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
            s.Resources[k] = DeckColors.Text;
    }

    public static void SetOn(StyledElement e, bool on) => e.Classes.Set("on", on);

    /// <summary>m:ss, or h:mm:ss past an hour.</summary>
    public static string Clock(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
