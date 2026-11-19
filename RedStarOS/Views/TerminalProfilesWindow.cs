using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Edits Red Star Terminal profiles: font, colour scheme, cursor, command, start folder, scrollback, bell.
/// Changes apply to open terminals straight away.</summary>
public sealed class TerminalProfilesWindow : Window
{
    private readonly ListBox _list = new() { Width = 190 };
    private readonly ContentControl _editor = new();
    private bool _loading;

    public TerminalProfilesWindow()
    {
        Title = "Terminal profiles and colours";
        Width = 760; Height = 560; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Button Btn(string text, Action act) { var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch }; b.Click += (_, _) => act(); return b; }
        var left = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                Btn("New profile", () => Add(new TerminalProfile { Name = TerminalProfiles.UniqueName("Profile") })),
                Btn("Duplicate", () => { if (Selected is { } p) Add(p.Clone(TerminalProfiles.UniqueName(p.Name + " copy"))); }),
                Btn("Set as default", () => { if (Selected is { } p) { TerminalProfiles.Settings.Default = p.Name; TerminalProfiles.Save(); Fill(p); } }),
                Btn("Delete", Delete),
            }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        left.Children.Add(buttons);
        left.Children.Add(_list);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        root.Children.Add(new ScrollViewer { Content = _editor, Padding = new Thickness(4, 12, 16, 12) });
        Content = root;

        _list.SelectionChanged += (_, _) => { if (!_loading && Selected is { } p) ShowEditor(p); };
        Fill(TerminalProfiles.DefaultProfile);
    }

    private TerminalProfile? Selected =>
        _list.SelectedItem is string s ? TerminalProfiles.Settings.Profiles.FirstOrDefault(p => Label(p) == s) : null;

    private static string Label(TerminalProfile p) => p.Name + (p.Name == TerminalProfiles.Settings.Default ? "  ★" : "");

    private void Fill(TerminalProfile select)
    {
        _loading = true;
        var items = TerminalProfiles.Settings.Profiles.Select(Label).ToList();
        _list.ItemsSource = items;
        _list.SelectedItem = Label(select);
        _loading = false;
        ShowEditor(select);
    }

    private void Add(TerminalProfile p)
    {
        TerminalProfiles.Settings.Profiles.Add(p);
        TerminalProfiles.Save();
        Fill(p);
    }

    private async void Delete()
    {
        if (Selected is not { } p) return;
        var s = TerminalProfiles.Settings;
        if (s.Profiles.Count <= 1) { await Dialogs.ShowMessage(this, "Profiles", "The last profile can't be deleted."); return; }
        if (await Dialogs.Ask(this, "Delete profile", $"Delete the profile “{p.Name}”? Open terminals keep their look until they close.", "Delete", "Cancel") != 0) return;
        s.Profiles.Remove(p);
        if (s.Default == p.Name) s.Default = s.Profiles[0].Name;
        TerminalProfiles.Save();
        Fill(TerminalProfiles.DefaultProfile);
    }

    private static List<string> MonospaceFonts()
    {
        var keys = new[] { "mono", "code", "courier", "consol", "fixed", "terminal", "hack", "iosevka", "fira", "inconsolata", "menlo", "source code" };
        var list = new List<string> { "(built-in choice)" };
        try
        {
            list.AddRange(FontManager.Current.SystemFonts.Select(f => f.Name)
                .Where(n => keys.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
                .Distinct().OrderBy(n => n));
        }
        catch { }
        return list;
    }

    private void ShowEditor(TerminalProfile p)
    {
        void Changed() { if (!_loading) TerminalProfiles.Save(); }
        Control Row(string label, Control c, string? hint = null)
        {
            var sp = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
            sp.Children.Add(c);
            if (hint != null) sp.Children.Add(new TextBlock { Text = hint, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            return sp;
        }

        // name
        var name = new TextBox { Text = p.Name, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 260 };
        name.LostFocus += (_, _) =>
        {
            var n = (name.Text ?? "").Trim();
            if (n.Length == 0 || n == p.Name) { name.Text = p.Name; return; }
            if (TerminalProfiles.Settings.Profiles.Any(x => x != p && x.Name == n)) n = TerminalProfiles.UniqueName(n);
            if (TerminalProfiles.Settings.Default == p.Name) TerminalProfiles.Settings.Default = n;
            p.Name = n;
            TerminalProfiles.Save();
            Fill(p);
        };

        // font
        var fonts = MonospaceFonts();
        var font = new ComboBox { ItemsSource = fonts, MinWidth = 260, SelectedItem = string.IsNullOrEmpty(p.FontFamily) || !fonts.Contains(p.FontFamily) ? fonts[0] : p.FontFamily };
        font.SelectionChanged += (_, _) => { p.FontFamily = font.SelectedIndex <= 0 ? "" : font.SelectedItem as string ?? ""; Changed(); };
        var size = new NumericUpDown { Minimum = 7, Maximum = 40, Increment = 1, Value = (decimal)p.FontSize, FormatString = "0", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
        size.ValueChanged += (_, _) => { if (size.Value is { } v) { p.FontSize = (double)v; Changed(); } };

        // colours
        var preview = new StackPanel { Spacing = 0 };
        var scheme = new ComboBox { ItemsSource = TerminalProfiles.Schemes.Select(s => s.Name).ToList(), SelectedItem = TerminalProfiles.Scheme(p.Scheme).Name, MinWidth = 260 };
        void Preview()
        {
            preview.Children.Clear();
            var sc = TerminalProfiles.Scheme(p.Scheme);
            bool dark = ThemeService.IsDark;
            uint[] ansi = sc.Ansi ?? (dark
                ? new uint[] { 0x1C1C1C, 0xF4707C, 0x7BD88F, 0xF2C811, 0x5AA9F5, 0xC792EA, 0x56D4DD, 0xD8D8D8, 0x6B6B6B, 0xFF99A4, 0xA5E8B5, 0xFCE66D, 0x8CC4FF, 0xDDB3F5, 0x8FE8EE, 0xFFFFFF }
                : new uint[] { 0x1A1A1A, 0xC50F1F, 0x107C10, 0x946B00, 0x0050B3, 0x881798, 0x0E7C86, 0x8A8A8A, 0x5F5F5F, 0xE81123, 0x13A10E, 0xB88700, 0x0078D4, 0xB146C2, 0x2AA7B3, 0xC8C8C8 });
            uint fg = sc.FollowsTheme ? (dark ? 0xE6E6E6u : 0x1A1A1Au) : sc.Fg, bg = sc.FollowsTheme ? (dark ? 0x1C1C1Cu : 0xFFFFFFu) : sc.Bg;
            IBrush B(uint c) => new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            var sample = new StackPanel { Spacing = 2 };
            var mono = new FontFamily(string.IsNullOrEmpty(p.FontFamily) ? TerminalProfiles.DefaultFonts : p.FontFamily + ", " + TerminalProfiles.DefaultFonts);
            var line1 = new StackPanel { Orientation = Orientation.Horizontal };
            line1.Children.Add(new TextBlock { Text = "user@redstar", Foreground = B(ansi[2]), FontFamily = mono, FontWeight = FontWeight.Bold });
            line1.Children.Add(new TextBlock { Text = ":", Foreground = B(fg), FontFamily = mono });
            line1.Children.Add(new TextBlock { Text = "~/Documents", Foreground = B(ansi[4]), FontFamily = mono, FontWeight = FontWeight.Bold });
            line1.Children.Add(new TextBlock { Text = "$ ls --color", Foreground = B(fg), FontFamily = mono });
            sample.Children.Add(line1);
            var line2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            line2.Children.Add(new TextBlock { Text = "notes.txt", Foreground = B(fg), FontFamily = mono });
            line2.Children.Add(new TextBlock { Text = "photos", Foreground = B(ansi[12]), FontFamily = mono, FontWeight = FontWeight.Bold });
            line2.Children.Add(new TextBlock { Text = "run.sh", Foreground = B(ansi[10]), FontFamily = mono, FontWeight = FontWeight.Bold });
            line2.Children.Add(new TextBlock { Text = "error!", Foreground = B(ansi[1]), FontFamily = mono });
            sample.Children.Add(line2);
            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var c in ansi) swatches.Children.Add(new Border { Width = 18, Height = 14, CornerRadius = new CornerRadius(2), Background = B(c) });
            sample.Children.Add(swatches);
            preview.Children.Add(new Border { Background = B(bg), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(6), Child = sample, HorizontalAlignment = HorizontalAlignment.Left });
        }
        scheme.SelectionChanged += (_, _) => { if (scheme.SelectedItem is string s) { p.Scheme = s; Changed(); Preview(); } };
        Preview();

        // cursor
        var cursor = new ComboBox { ItemsSource = new[] { "Block", "Underline", "Bar" }, SelectedItem = p.CursorShape, Width = 160 };
        cursor.SelectionChanged += (_, _) => { if (cursor.SelectedItem is string s) { p.CursorShape = s; Changed(); } };
        var blink = new CheckBox { Content = "Blinking cursor", IsChecked = p.CursorBlink };
        blink.IsCheckedChanged += (_, _) => { p.CursorBlink = blink.IsChecked == true; Changed(); };

        // command and folder
        var command = new TextBox { Text = p.Command, PlaceholderText = "Your login shell (" + PtySession.DefaultShell() + ")", MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        command.LostFocus += (_, _) => { p.Command = (command.Text ?? "").Trim(); Changed(); };
        var startOptions = new[] { "The current tab's folder", "Home folder", "This folder:" };
        var start = new ComboBox { ItemsSource = startOptions, SelectedIndex = p.StartIn == "Home" ? 1 : p.StartIn == "Custom" ? 2 : 0, Width = 220 };
        var dir = new TextBox { Text = p.CustomDir, PlaceholderText = "/path/to/folder", MinWidth = 260, IsEnabled = p.StartIn == "Custom" };
        start.SelectionChanged += (_, _) => { p.StartIn = start.SelectedIndex switch { 1 => "Home", 2 => "Custom", _ => "Current" }; dir.IsEnabled = start.SelectedIndex == 2; Changed(); };
        dir.LostFocus += (_, _) => { p.CustomDir = (dir.Text ?? "").Trim(); Changed(); };

        // scrollback and bell
        var scroll = new NumericUpDown { Minimum = 100, Maximum = 200000, Increment = 1000, Value = p.Scrollback, FormatString = "0", Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        scroll.ValueChanged += (_, _) => { if (scroll.Value is { } v) { p.Scrollback = (int)v; Changed(); } };
        var bell = new CheckBox { Content = "Flash the terminal on the bell", IsChecked = p.VisualBell };
        bell.IsCheckedChanged += (_, _) => { p.VisualBell = bell.IsChecked == true; Changed(); };
        var bright = new CheckBox { Content = "Show bold text in bright colours", IsChecked = p.BoldIsBright };
        bright.IsCheckedChanged += (_, _) => { p.BoldIsBright = bright.IsChecked == true; Changed(); };

        _loading = true;
        _editor.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = p.Name == TerminalProfiles.Settings.Default ? "Default profile — used for new tabs" : "Profile", FontSize = 13, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 10) },
                Row("Name", name),
                Row("Font", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { font, size } }),
                Row("Colours", new StackPanel { Spacing = 8, Children = { scheme, preview } }, "“Red Star” follows the light / dark theme and accent colour of the desktop."),
                Row("Cursor", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cursor, blink } }, "Programs can still change the cursor shape while they run."),
                Row("Command", command, "Leave empty for your shell, or enter a program, e.g. “ssh server” or “htop”."),
                Row("Start in", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, dir } }),
                Row("Scrollback lines", scroll, "Applies to new terminals."),
                Row("Bell and text", new StackPanel { Spacing = 6, Children = { bell, bright } }),
            }
        };
        _loading = false;
    }
}
