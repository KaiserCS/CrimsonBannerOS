using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// First-run welcome: look (theme, accent), comfort (size, contrast, motion, sounds), bringing bookmarks and history
/// from another browser, and the keyboard shortcuts worth knowing. Shown once; Settings → Appearance can open it again.
/// Every choice applies straight away and can be changed later in Settings.
/// </summary>
public sealed class WelcomeWindow : Window
{
    private readonly ContentControl _page = new();
    private readonly Button _back = new() { Content = "Back", Padding = new Thickness(16, 7) };
    private readonly Button _next = new() { Content = "Next", Padding = new Thickness(18, 7), Classes = { "primary" }, IsDefault = true };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(string Title, Func<Control> Build)> _steps;
    private int _step;
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#C8102E"));

    private static WelcomeWindow? _open;

    /// <summary>Opens the welcome on its shortcuts page.</summary>
    public static void OpenShortcuts()
    {
        Open();
        _open?.Go(_open._steps.Count - 1);
    }

    /// <summary>Opens the welcome (or brings the open one forward).</summary>
    public static void Open()
    {
        if (_open != null) { _open.Activate(); return; }
        WindowManager.Open(new WelcomeWindow(), "welcome");
    }

    public WelcomeWindow()
    {
        Title = "Welcome to Crimson Banner OS";
        Width = 760; Height = 540; MinWidth = 600; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _steps = new()
        {
            ("Welcome", new Func<Control>(Hello)), ("Look", new Func<Control>(Look)), ("Comfort", new Func<Control>(Comfort)),
            ("Your browser", new Func<Control>(Import)), ("Shortcuts", new Func<Control>(Tour)),
        };

        var skip = new Button { Content = "Skip", Padding = new Thickness(14, 7), Background = Brushes.Transparent };
        skip.Click += (_, _) => Finish();
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => { if (_step == _steps.Count - 1) Finish(); else Go(_step + 1); };
        Avalonia.Automation.AutomationProperties.SetName(skip, "Skip the welcome");

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(24, 12, 24, 18) };
        footer.Children.Add(skip);
        Grid.SetColumn(_dots, 1); _dots.HorizontalAlignment = HorizontalAlignment.Center; footer.Children.Add(_dots);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _back, _next } };
        Grid.SetColumn(nav, 3); footer.Children.Add(nav);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _page, Padding = new Thickness(36, 28, 36, 0) });
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish();
            else if (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt && _step > 0) Go(_step - 1);
        };
        _open = this;
        Closed += (_, _) => { MarkDone(); _open = null; };
        Go(0);
    }

    private void Go(int i)
    {
        _step = Math.Clamp(i, 0, _steps.Count - 1);
        _page.Content = _steps[_step].Build();
        _back.IsVisible = _step > 0;
        _next.Content = _step == _steps.Count - 1 ? "Start using Crimson Banner OS" : "Next";
        _dots.Children.Clear();
        for (int d = 0; d < _steps.Count; d++)
        {
            var dot = new Border { Width = d == _step ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Background = d == _step ? Red : new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)) };
            ToolTip.SetTip(dot, _steps[d].Title);
            _dots.Children.Add(dot);
        }
        _next.Focus();
    }

    private static void MarkDone()
    {
        if (SettingsStore.Current.WelcomeDone) return;
        SettingsStore.Current.WelcomeDone = true;
        SettingsStore.Save();
    }

    private void Finish() { MarkDone(); Close(); }

    // ───────────── helpers ─────────────
    private static StackPanel Page(string title, string text)
    {
        var p = new StackPanel { Spacing = 14 };
        p.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeight.SemiBold });
        p.Children.Add(new TextBlock { Text = text, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        return p;
    }

    private static TextBlock Label(string t) => new() { Text = t, Classes = { "eyebrow" }, Margin = new Thickness(0, 8, 0, 0) };

    private static void Save() { SettingsStore.Save(); WindowManager.RaiseChanged(); }

    // ───────────── steps ─────────────
    private Control Hello()
    {
        var p = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        p.Children.Add(new IconView { Kind = "star", Size = 84, Brush = Red, HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = "Welcome to Crimson Banner OS", FontSize = 30, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock
        {
            Text = "A few quick choices and you're ready. Everything here can be changed later in Settings.",
            Opacity = 0.75, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, FontSize = 14
        });
        var facts = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (icon, text) in new[] { ("globe", "Crimson Online"), ("document", "Writer, Sheets, Slides"), ("terminal", "Terminal"), ("film", "Video") })
            facts.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 4),
                Children = { new IconView { Kind = icon, Size = 18 }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } }
            });
        p.Children.Add(facts);
        return p;
    }

    private Control Look()
    {
        var s = SettingsStore.Current;
        var p = Page("Choose your look", "Light, dark, or follow the time of day your system reports. The accent colour is used for buttons, selections and highlights.");
        p.Children.Add(Label("THEME"));
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var themeButtons = new List<ToggleButton>();
        foreach (var (name, bg, fg) in new[] { ("Light", "#F3F3F3", "#202020"), ("Dark", "#202020", "#F3F3F3"), ("System", "#8A8A8A", "#FFFFFF") })
        {
            var n = name;
            var preview = new Border
            {
                Width = 150, Height = 86, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.Parse(bg)),
                Child = new StackPanel
                {
                    Margin = new Thickness(10), Spacing = 6,
                    Children =
                    {
                        new Border { Height = 10, Width = 80, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.Parse(fg)), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Left },
                        new Border { Height = 8, Width = 110, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.Parse(fg)), Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Left },
                        new Border { Height = 14, Width = 46, CornerRadius = new CornerRadius(4), Background = Application.Current?.FindResource("ShellAccentBrush") as IBrush ?? Red, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) },
                    }
                }
            };
            var b = new ToggleButton { Padding = new Thickness(6), IsChecked = s.Theme == n, Content = new StackPanel { Spacing = 6, Children = { preview, new TextBlock { Text = n, HorizontalAlignment = HorizontalAlignment.Center } } } };
            Avalonia.Automation.AutomationProperties.SetName(b, n + " theme");
            b.Click += (_, _) =>
            {
                foreach (var o in themeButtons) o.IsChecked = ReferenceEquals(o, b);
                s.Theme = n; Save(); ThemeService.Apply();
            };
            themeButtons.Add(b);
            themes.Children.Add(b);
        }
        p.Children.Add(themes);

        p.Children.Add(Label("ACCENT COLOUR"));
        var swatches = new WrapPanel();
        var swatchButtons = new List<ToggleButton>();
        foreach (var (name, hex) in ThemeService.Accents)
        {
            var h = hex;
            var b = new ToggleButton
            {
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(22), Padding = new Thickness(4),
                IsChecked = s.Accent.Equals(hex, StringComparison.OrdinalIgnoreCase),
                Content = new Ellipse { Width = 30, Height = 30, Fill = new SolidColorBrush(Color.Parse(hex)) }
            };
            ToolTip.SetTip(b, name);
            Avalonia.Automation.AutomationProperties.SetName(b, name + " accent");
            b.Click += (_, _) =>
            {
                foreach (var o in swatchButtons) o.IsChecked = ReferenceEquals(o, b);
                s.Accent = h; Save(); ThemeService.Apply();
            };
            swatchButtons.Add(b);
            swatches.Children.Add(b);
        }
        p.Children.Add(swatches);
        var wall = new Button { Content = "Find a wallpaper…", Padding = new Thickness(12, 6) };
        wall.Click += (_, _) => WallpaperPickerWindow.Open(online: true);
        p.Children.Add(wall);
        return p;
    }

    private Control Comfort()
    {
        var s = SettingsStore.Current;
        var p = Page("Make it comfortable", "Size, contrast and motion. These are also in Settings → Accessibility.");

        p.Children.Add(Label("SIZE OF TEXT AND CONTROLS"));
        var sizes = new[] { 1.0, 1.25, 1.5, 1.75, 2.0 };
        var size = new ComboBox
        {
            ItemsSource = sizes.Select(v => $"{v * 100:0}%" + (v == 1.0 ? " (normal)" : "")).ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(sizes, v => Math.Abs(v - s.UiScale) < 0.01)), MinWidth = 200
        };
        Avalonia.Automation.AutomationProperties.SetName(size, "Size of text and controls");
        var sizeNote = new TextBlock { Opacity = 0.7, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        size.SelectionChanged += (_, _) =>
        {
            if (size.SelectedIndex < 0) return;
            s.UiScale = sizes[size.SelectedIndex]; Save();
            sizeNote.Text = "The new size is used the next time you sign in.";
        };
        p.Children.Add(size);
        p.Children.Add(sizeNote);

        p.Children.Add(Label("SEEING AND HEARING"));
        CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = value };
            c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); Save(); };
            return c;
        }
        p.Children.Add(Check("High contrast (stronger text, borders and focus outlines)", s.HighContrast, v => { s.HighContrast = v; ThemeService.Apply(); }));
        p.Children.Add(Check("Reduce motion (no sliding or fading animations)", s.ReduceMotion, v => { s.ReduceMotion = v; ThemeService.Apply(); }));
        p.Children.Add(Check("Play system sounds (start-up, notifications)", SystemSounds.Enabled, v => { SystemSounds.Enabled = v; if (v) SystemSounds.Play("notify"); }));
        return p;
    }

    private Control Import()
    {
        var p = Page("Bring your bookmarks", "Crimson Online can copy bookmarks and history from browsers already on this computer. Nothing is removed from the other browser.");
        var sources = BrowserImport.Detect();
        if (sources.Count == 0)
        {
            p.Children.Add(new TextBlock { Text = "No other browsers were found. You can import later from Crimson Online → Settings.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            return p;
        }
        p.Children.Add(Label("FOUND ON THIS COMPUTER"));
        var picks = new List<(CheckBox Box, BrowserImport.Source Src)>();
        foreach (var src in sources)
        {
            var c = new CheckBox { Content = src.Name, IsChecked = picks.Count == 0 };
            ToolTip.SetTip(c, src.ProfileDir);
            picks.Add((c, src));
            p.Children.Add(c);
        }
        p.Children.Add(Label("WHAT TO BRING"));
        var marks = new CheckBox { Content = "Bookmarks", IsChecked = true };
        var hist = new CheckBox { Content = "History" + (ProcessUtil.Which("sqlite3") == null ? " (needs the sqlite3 program)" : ""), IsChecked = ProcessUtil.Which("sqlite3") != null };
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Children = { marks, hist } });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        var go = new Button { Content = "Import", Classes = { "primary" }, Padding = new Thickness(16, 7), HorizontalAlignment = HorizontalAlignment.Left };
        go.Click += async (_, _) =>
        {
            var chosen = picks.Where(x => x.Box.IsChecked == true).Select(x => x.Src).ToList();
            if (chosen.Count == 0) { status.Text = "Pick at least one browser."; return; }
            go.IsEnabled = false;
            status.Text = "Importing…";
            int b = 0, h = 0;
            var notes = new HashSet<string>();
            foreach (var src in chosen)
            {
                var r = await BrowserImport.ImportAsync(src, marks.IsChecked == true, hist.IsChecked == true);
                b += r.Bookmarks; h += r.History;
                if (r.Note != null) notes.Add(r.Note);
            }
            go.IsEnabled = true;
            status.Text = $"Imported {b} bookmark{(b == 1 ? "" : "s")} and {h} history entr{(h == 1 ? "y" : "ies")}." + (notes.Count > 0 ? " " + string.Join(" ", notes) : "");
        };
        p.Children.Add(go);
        p.Children.Add(status);
        return p;
    }

    private Control Tour()
    {
        var p = Page("Shortcuts worth knowing", "Press Super (the Windows key) any time to search apps, files, settings and the web.");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), Margin = new Thickness(0, 6, 0, 0) };
        var rows = new[]
        {
            ("Super", "Start menu and search everything"),
            ("Alt + Tab", "Switch between windows"),
            ("Super + ← / →", "Snap a window to half the screen"),
            ("Super + ↑ / ↓", "Maximise / minimise"),
            ("Super + V", "Clipboard history"),
            ("Super + E", "Files"),
            ("Ctrl + Alt + T", "Terminal"),
            ("Super + D", "Show the desktop"),
            ("Super + L", "Lock / sign out"),
            ("F1 / Ctrl + ,", "Help and settings inside an app"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = new Border
            {
                Padding = new Thickness(8, 3), Margin = new Thickness(0, 3, 12, 3), CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Left,
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                Child = new TextBlock { Text = rows[i].Item1, FontFamily = new FontFamily("DejaVu Sans Mono,monospace"), FontSize = 12.5 }
            };
            var what = new TextBlock { Text = rows[i].Item2, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(key, i); Grid.SetRow(what, i); Grid.SetColumn(what, 1);
            grid.Children.Add(key); grid.Children.Add(what);
        }
        p.Children.Add(grid);
        return p;
    }
}
