// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// What Writer, Sheets and Slides share, so the three look and work as one suite: the document's tab row, the menu
/// row, one toolbar of line icons in groups (<see cref="OfficeToolbar"/>: what doesn't fit goes into More), one colour
/// button and swatch panel, and one zoom control in the status bar. Styles: Border.officetabs, .officebar,
/// .officetools, .officestatus, Button.tool and Border.tbsep in Shell.axaml.
/// </summary>
public static class OfficeChrome
{
    /// <summary>The colours every office app offers, in one order: greys, then the hues, then soft tints.</summary>
    public static readonly string[] Palette =
    {
        "#000000", "#404040", "#7F7F7F", "#BFBFBF", "#FFFFFF", "#C8102E", "#E8590C", "#F2C200",
        "#2F9E44", "#0B7285", "#1C7ED6", "#5F3DC4", "#A61E4D", "#8B4513", "#FFD8A8", "#FFF3BF",
        "#D3F9D8", "#C5F6FA", "#D0EBFF", "#E5DBFF",
    };

    /// <summary>
    /// A colour button's face: its line icon over a bar of the colour it applies (none: a hairline outline). Set the
    /// bar with the returned action.
    /// </summary>
    public static (Control Face, Action<string?> SetColour) ColourFace(string icon, string? initial)
    {
        var bar = new Border { Height = 3, Width = 16, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 1, 0, 0), BorderThickness = new Thickness(1) };
        void Set(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) { bar.Background = Brushes.Transparent; bar.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80)); }
            else { var c = Color.Parse(hex); bar.Background = new SolidColorBrush(c); bar.BorderBrush = new SolidColorBrush(c); }
        }
        Set(initial);
        var face = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center, Children = { Icons.Create(icon, 16), bar } };
        return (face, Set);
    }

    /// <summary>
    /// The swatch panel every colour button opens: the palette, a "none" choice where that means something (its label:
    /// "No colour", "Automatic", "No highlight"), and "Custom…" for any colour written as hex.
    /// </summary>
    public static Flyout Swatches(Action<string?> pick, string? noneLabel, Window? owner = null, string customTitle = "Colour")
    {
        var fly = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var grid = new UniformGrid { Columns = 5 };
        foreach (var hex in Palette)
        {
            var h = hex;
            var b = new Button
            {
                Classes = { "swatch" }, Width = 28, Height = 28, Margin = new Thickness(2), Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse(h)),
            };
            ToolTip.SetTip(b, h);
            Avalonia.Automation.AutomationProperties.SetName(b, h);
            b.Click += (_, _) => { fly.Hide(); pick(h); };
            grid.Children.Add(b);
        }
        var row = new UniformGrid { Rows = 1 };
        if (noneLabel != null)
        {
            var none = new Button { Content = noneLabel, Classes = { "flat" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            none.Click += (_, _) => { fly.Hide(); pick(null); };
            row.Children.Add(none);
        }
        if (owner != null)
        {
            var custom = new Button { Content = "Custom…", Classes = { "flat" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            custom.Click += async (_, _) =>
            {
                fly.Hide();
                var v = await Dialogs.Prompt(owner, customTitle, "Colour as hex, for example #1A73E8", "#");
                if (DocTextIO.ParseColor(v) is { } c) pick(c);
            };
            row.Children.Add(custom);
        }
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(2), Children = { grid } };
        if (row.Children.Count > 0) stack.Children.Add(row);
        fly.Content = stack;
        return fly;
    }

    /// <summary>A whole colour button: the face and the swatches, as a toolbar tool.</summary>
    public static Button ColourButton(string icon, string tip, string? initial, Action<string?> pick, string? noneLabel, Window? owner, Func<string?>? current = null)
    {
        var btn = new Button { Classes = { "tool" } };
        Fit(btn, icon, tip, initial, pick, noneLabel, owner, current);
        return btn;
    }

    /// <summary>Makes an existing button (from XAML) a colour button.</summary>
    public static void Fit(Button btn, string icon, string tip, string? initial, Action<string?> pick, string? noneLabel, Window? owner, Func<string?>? current = null)
    {
        var (face, set) = ColourFace(icon, initial);
        btn.Content = face;
        ToolTip.SetTip(btn, tip);
        Avalonia.Automation.AutomationProperties.SetName(btn, tip);
        OfficeToolbar.SetMenuHeader(btn, tip + "…");
        var fly = Swatches(h => { set(h); pick(h); }, noneLabel, owner, tip);
        if (current != null) fly.Opening += (_, _) => set(current());
        btn.Flyout = fly;
    }

    /// <summary>
    /// Sheets' and Slides' tab row: one tab for their one document, named from the window's title ("● Name - Sheets"),
    /// looking the same as Writer's tabs ("+" for a new document is the window's own button beside it).
    /// </summary>
    public static void SingleTab(Window w, Panel host, string icon, string appSuffix, Action close)
    {
        void Build()
        {
            var title = w.Title ?? "";
            if (title.EndsWith(appSuffix, StringComparison.Ordinal)) title = title[..^appSuffix.Length];
            bool dirty = title.StartsWith("● ") || title.StartsWith("• ") || title.StartsWith("*");
            title = title.TrimStart('●', '•', '*', ' ');
            ShellTabs.Build(host, new[]
            {
                new TabSpec
                {
                    Title = title.Length > 0 ? title : "Untitled", IconName = icon, Active = true, Prefix = dirty ? "● " : null,
                    Tooltip = dirty ? title + " (not saved)" : title, Close = close, CloseTip = "Close",
                },
            });
        }
        Build();
        w.PropertyChanged += (_, e) => { if (e.Property == Window.TitleProperty) Build(); };
    }
}

/// <summary>
/// The office apps' toolbar: tools in one row, grouped by Border.tbsep dividers. When the window is too narrow the
/// tools that don't fit are taken off the end and offered in a More menu (by their tooltip, or
/// <see cref="MenuHeaderProperty"/>), so nothing is ever cut off half-way.
/// </summary>
public sealed class OfficeToolbar : Panel
{
    public static readonly AttachedProperty<string?> MenuHeaderProperty =
        AvaloniaProperty.RegisterAttached<OfficeToolbar, Control, string?>("MenuHeader");
    public static void SetMenuHeader(Control c, string? v) => c.SetValue(MenuHeaderProperty, v);
    public static string? GetMenuHeader(Control c) => c.GetValue(MenuHeaderProperty);

    public double Spacing { get; set; } = 2;

    private readonly Button _more;
    private readonly List<Control> _hidden = new();

    public OfficeToolbar()
    {
        ClipToBounds = true;
        Classes.Add("officetoolbar");
        _more = new Button { Classes = { "tool" }, Content = Icons.Create("more", 18) };
        ToolTip.SetTip(_more, "More");
        Avalonia.Automation.AutomationProperties.SetName(_more, "More tools");
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) => FillMore(menu);
        _more.Flyout = menu;
        Children.Add(_more);
    }

    /// <summary>The tools that don't fit now (for the tests).</summary>
    public IReadOnlyList<Control> Overflow => _hidden;

    private IEnumerable<Control> Tools => Children.Where(c => !ReferenceEquals(c, _more));

    protected override Size MeasureOverride(Size available)
    {
        double w = 0, h = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            if (!ReferenceEquals(c, _more) && c.IsVisible) w += c.DesiredSize.Width + Spacing;
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? w : Math.Min(w, available.Width), h);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _hidden.Clear();
        var tools = Tools.Where(c => c.IsVisible).ToList();
        double total = tools.Sum(c => c.DesiredSize.Width + Spacing);
        bool overflow = total > final.Width + 0.5;
        double limit = overflow ? final.Width - _more.DesiredSize.Width - Spacing : final.Width;
        double x = 0;
        bool cut = false;
        var shown = new List<Control>();
        foreach (var c in tools)
        {
            double cw = c.DesiredSize.Width;
            if (!cut && x + cw <= limit) { shown.Add(c); x += cw + Spacing; }
            else { cut = true; _hidden.Add(c); }
        }
        // no divider left dangling at the end of the row
        while (shown.Count > 0 && IsDivider(shown[^1])) { _hidden.Insert(0, shown[^1]); shown.RemoveAt(shown.Count - 1); }
        x = 0;
        foreach (var c in shown)
        {
            double top = (final.Height - c.DesiredSize.Height) / 2;
            c.Arrange(new Rect(x, top, c.DesiredSize.Width, c.DesiredSize.Height));
            KeyboardNavigation.SetIsTabStop(c, true);
            x += c.DesiredSize.Width + Spacing;
        }
        foreach (var c in _hidden)
        {
            c.Arrange(new Rect(-10000, -10000, c.DesiredSize.Width, c.DesiredSize.Height));   // out of sight, out of reach
            KeyboardNavigation.SetIsTabStop(c, false);
        }
        foreach (var c in Tools.Where(c => !c.IsVisible)) c.Arrange(default);
        if (_hidden.Any(c => !IsDivider(c)))
            _more.Arrange(new Rect(final.Width - _more.DesiredSize.Width, (final.Height - _more.DesiredSize.Height) / 2, _more.DesiredSize.Width, _more.DesiredSize.Height));
        else _more.Arrange(new Rect(-10000, -10000, _more.DesiredSize.Width, _more.DesiredSize.Height));
        return final;
    }

    private static bool IsDivider(Control c) => c is Border b && b.Classes.Contains("tbsep");

    private static string Label(Control c)
    {
        if (GetMenuHeader(c) is { Length: > 0 } h) return h;
        if (ToolTip.GetTip(c) is string tip && tip.Length > 0) return tip;
        if (c is ContentControl { Content: string s }) return s;
        return Avalonia.Automation.AutomationProperties.GetName(c) ?? "";
    }

    /// <summary>What a hidden tool becomes in the More menu.</summary>
    private void FillMore(MenuFlyout menu)
    {
        menu.Items.Clear();
        foreach (var c in _hidden)
        {
            var tool = c is ContentControl { Content: Button inner } && c is not Button ? inner : c;
            switch (tool)
            {
                case Border when IsDivider(tool):
                    if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator());
                    break;
                case ComboBox combo:
                {
                    var mi = new MenuItem { Header = Label(combo) };
                    for (int i = 0; i < combo.ItemCount; i++)
                    {
                        int index = i;
                        var item = combo.Items[i];
                        var sub = new MenuItem
                        {
                            Header = item is ComboBoxItem cbi ? cbi.Content : item,
                            ToggleType = MenuItemToggleType.Radio, IsChecked = combo.SelectedIndex == i,
                        };
                        sub.Click += (_, _) => combo.SelectedIndex = index;
                        mi.Items.Add(sub);
                    }
                    if (mi.Items.Count > 0) menu.Items.Add(mi);
                    break;
                }
                case CheckBox check:
                {
                    var mi = new MenuItem { Header = check.Content, ToggleType = MenuItemToggleType.CheckBox, IsChecked = check.IsChecked == true };
                    mi.Click += (_, _) => check.IsChecked = check.IsChecked != true;
                    menu.Items.Add(mi);
                    break;
                }
                case ToggleButton toggle:
                {
                    var mi = new MenuItem { Header = Label(toggle), ToggleType = MenuItemToggleType.CheckBox, IsChecked = toggle.IsChecked == true };
                    mi.Click += (_, _) => { toggle.IsChecked = toggle.IsChecked != true; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                    menu.Items.Add(mi);
                    break;
                }
                case Button b when b.Flyout != null:
                {
                    var mi = new MenuItem { Header = Label(b), IsEnabled = b.IsEnabled };
                    var fly = b.Flyout;
                    mi.Click += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => fly.ShowAt(_more));
                    menu.Items.Add(mi);
                    break;
                }
                case Button b:
                {
                    var mi = new MenuItem
                    {
                        Header = Label(b), IsEnabled = b.IsEnabled,
                        ToggleType = b.Classes.Contains("on") ? MenuItemToggleType.CheckBox : MenuItemToggleType.None,
                        IsChecked = b.Classes.Contains("on"),
                    };
                    mi.Click += (_, _) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    menu.Items.Add(mi);
                    break;
                }
            }
        }
        while (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
    }
}

/// <summary>The office apps' zoom, at the right of the status bar: zoom out, a slider, zoom in, and the percentage.</summary>
public sealed class OfficeZoom : StackPanel
{
    private readonly Slider _slider;
    private readonly TextBlock _text;
    private bool _setting;

    /// <summary>The zoom changed by the person (percent).</summary>
    public event Action<double>? Changed;

    public double Minimum { get => _slider.Minimum; set => _slider.Minimum = value; }
    public double Maximum { get => _slider.Maximum; set => _slider.Maximum = value; }
    public double Step { get; set; } = 10;

    /// <summary>The zoom in percent (setting it doesn't raise <see cref="Changed"/>).</summary>
    public double Percent
    {
        get => _slider.Value;
        set { _setting = true; _slider.Value = Math.Clamp(value, Minimum, Maximum); _text.Text = $"{Math.Round(_slider.Value)}%"; _setting = false; }
    }

    public OfficeZoom()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;
        _slider = new Slider { Minimum = 50, Maximum = 250, Value = 100, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(_slider, "Zoom");
        _text = new TextBlock { Text = "100%", Width = 42, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Classes = { "caption" } };
        Button Step1(string icon, string tip, int sign)
        {
            var b = new Button { Classes = { "tool", "small" }, Content = Icons.Create(icon, 14) };
            ToolTip.SetTip(b, tip);
            Avalonia.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => { Percent = _slider.Value + sign * Step; Changed?.Invoke(_slider.Value); };
            return b;
        }
        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _text.Text = $"{Math.Round(_slider.Value)}%";
            if (!_setting) Changed?.Invoke(_slider.Value);
        };
        Children.Add(Step1("minus", "Zoom out", -1));
        Children.Add(_slider);
        Children.Add(Step1("plus", "Zoom in", 1));
        Children.Add(_text);
    }
}
