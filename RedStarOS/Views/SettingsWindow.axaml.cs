using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// System Settings: one window with a sidebar. Appearance, Desktop &amp; Taskbar and Games are pages here;
/// Network, Account, Wallpapers and Software Updates open their own windows (the sidebar entry shows a launcher).
/// Everything applies immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    public sealed record NavItem(string Name, string Glyph, IBrush Color, Func<Control> Build);

    private static readonly string[] Themes = { "Light", "Dark", "System" };

    public static SettingsWindow? Instance { get; private set; }

    /// <summary>Opens Settings at a page (used by Start-menu search and other apps).</summary>
    public static void OpenPage(string name)
    {
        var w = Instance ?? WindowManager.Open(new SettingsWindow(), "settings");
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        if (w.Nav.ItemsSource is IEnumerable<NavItem> items)
        {
            var it = items.FirstOrDefault(i => i.Name == name);
            if (it != null) w.Nav.SelectedItem = it;
        }
    }

    /// <summary>Makes the settings pages findable from the Start menu.</summary>
    public static void RegisterSearch()
    {
        if (SearchIndex.Settings.Count > 0) return;
        foreach (var (name, keywords, icon) in SearchPages)
        {
            var n = name;
            SearchIndex.Settings.Add((n, keywords, icon, () => OpenPage(n)));
        }
    }

    private static readonly (string Name, string Keywords, string Icon)[] SearchPages =
    {
        ("Appearance", "theme dark light accent colour color font", "palette"),
        ("Desktop & Taskbar", "taskbar pinned labels desktop icons virtual desktops task view", "monitor"),
        ("Wallpapers", "background picture slideshow wallpaper search download", "image"),
        ("Wi-Fi & Network", "wifi wi-fi internet ethernet wired airplane network", "wifi"),
        ("Sound", "volume speaker headphones microphone audio output input", "volume"),
        ("Bluetooth", "headphones pair device mouse keyboard", "bluetooth"),
        ("Displays", "screen monitor resolution rotate mirror projector night light warm blue light", "monitor"),
        ("Printers", "print printer cups queue", "printer"),
        ("Default apps", "open with browser default program", "apps"),
        ("Accessibility", "size scale bigger text zoom contrast motion animation cursor pointer sounds keyboard screen reader", "accessibility"),
        ("Account", "password user photo child parental", "user"),
        ("Software Updates", "update upgrade version release", "refresh"),
        ("Diagnostics", "crash log error bug report system info", "bug"),
        ("Games", "pong blocks mahjongg gems scores", "game"),
    };

    public SettingsWindow()
    {
        InitializeComponent();
        Instance = this;
        Closed += (_, _) => { if (ReferenceEquals(Instance, this)) Instance = null; };

        var items = new List<NavItem>
        {
            new("Appearance", "palette", AppIcons.Plate, BuildAppearance),
            new("Desktop & Taskbar", "panel", AppIcons.Plate, BuildTaskbar),
            new("Wallpapers", "image", AppIcons.Plate, () => PageLauncher("Wallpapers", "Search online for full-resolution wallpapers that fit your screen, or use your own pictures and slideshows.", "Open Wallpapers", () => WallpaperPickerWindow.Open())),
            new("Wi-Fi & Network", "wifi", AppIcons.Plate, SettingsPages.Network),
            new("Sound", "volume", AppIcons.Plate, SettingsPages.Sound),
            new("Bluetooth", "bluetooth", AppIcons.Plate, SettingsPages.Bluetooth),
            new("Displays", "monitor", AppIcons.Plate, SettingsPages.Displays),
            new("Printers", "printer", AppIcons.Plate, SettingsPages.Printers),
            new("Default apps", "apps", AppIcons.Plate, SettingsPages.DefaultAppsPage),
            new("Startup apps", "power", AppIcons.Plate, SettingsPages.StartupPage),
            new("Power & battery", "battery", AppIcons.Plate, SettingsPages.PowerPage),
            new("Date & time", "clock", AppIcons.Plate, SettingsPages.DateTimePage),
            new("Users", "user", AppIcons.Plate, SettingsPages.UsersPage),
            new("Region & keyboard", "keyboard", AppIcons.Plate, SettingsPages.RegionPage),
            new("Crimson Online", "globe", AppIcons.Plate, SettingsPages.OnlinePage),
            new("Accessibility", "accessibility", AppIcons.Plate, SettingsPages.Accessibility),
            new("Account", "user", AppIcons.Plate, () => PageLauncher("Account", "Password, profile photo and account type.", "Open Account", () => WindowManager.Open(new AccountWindow()))),
            new("Software Updates", "refresh", AppIcons.Plate, SettingsPages.Updates),
            new("Diagnostics", "bug", AppIcons.Plate, SettingsPages.DiagnosticsPage),
            new("Games", "game", AppIcons.Plate, BuildGames),
        };
        Nav.ItemsSource = items;
        Nav.SelectionChanged += (_, _) =>
        {
            if (Nav.SelectedItem is NavItem n) Page.Content = n.Build();
        };
        Nav.SelectedIndex = 0;
    }

    // ------------------------------------------------------------ helpers

    private static StackPanel PageOf(string title, string? subtitle = null)
    {
        var p = new StackPanel { Spacing = 12 };
        p.Children.Add(new TextBlock { Text = title, Classes = { "h1" } });
        if (subtitle != null) p.Children.Add(new TextBlock { Text = subtitle, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        return p;
    }

    private static TextBlock Eyebrow(string text) =>
        new() { Text = text, Classes = { "eyebrow" }, Margin = new Avalonia.Thickness(0, 8, 0, 0) };

    private static Control PageLauncher(string title, string text, string button, Action open)
    {
        var p = PageOf(title, text);
        var b = new Button { Content = button, Classes = { "primary" }, Padding = new Avalonia.Thickness(16, 8), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => open();
        p.Children.Add(b);
        return p;
    }

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value };
        c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); SettingsStore.Save(); WindowManager.RaiseChanged(); };
        return c;
    }

    // ------------------------------------------------------------ pages

    private static Control BuildAppearance()
    {
        var s = SettingsStore.Current;
        var p = PageOf("Appearance", "Theme, colours, fonts, the mouse pointer and how other programs look.");

        p.Children.Add(Eyebrow("THEME"));
        var theme = new ComboBox { ItemsSource = Themes, SelectedIndex = Math.Max(0, Array.IndexOf(Themes, s.Theme)), HorizontalAlignment = HorizontalAlignment.Stretch };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            SettingsStore.Current.Theme = Themes[theme.SelectedIndex];
            SettingsStore.Save();
            ThemeService.Apply();
        };
        p.Children.Add(theme);

        p.Children.Add(Eyebrow("ACCENT COLOUR"));
        var accents = ThemeService.Accents;
        var idx = Array.FindIndex(accents, a => a.Hex.Equals(s.Accent, StringComparison.OrdinalIgnoreCase));
        var accent = new ComboBox { ItemsSource = accents.Select(a => a.Name).ToList(), SelectedIndex = idx < 0 ? 0 : idx, HorizontalAlignment = HorizontalAlignment.Stretch };
        accent.SelectionChanged += (_, _) =>
        {
            if (accent.SelectedIndex < 0) return;
            SettingsStore.Current.Accent = accents[accent.SelectedIndex].Hex;
            SettingsStore.Save();
            ThemeService.Apply();
        };
        p.Children.Add(accent);

        BuildLook(p);

        p.Children.Add(Eyebrow("SCREEN"));
        var full = new CheckBox { Content = "Full-screen desktop (covers the host panel)", IsChecked = s.Fullscreen };
        full.IsCheckedChanged += (_, _) =>
        {
            SettingsStore.Current.Fullscreen = full.IsChecked == true;
            SettingsStore.Save();
            if (WindowManager.Desktop != null)
                WindowManager.Desktop.WindowState = SettingsStore.Current.Fullscreen ? WindowState.FullScreen : WindowState.Maximized;
        };
        p.Children.Add(full);

        p.Children.Add(Eyebrow("GETTING STARTED"));
        var welcome = new Button { Content = "Show the welcome again", Padding = new Avalonia.Thickness(12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        welcome.Click += (_, _) => WelcomeWindow.Open();
        p.Children.Add(welcome);
        return p;
    }

    private const string DefaultChoice = "(default)";

    /// <summary>A drop-down of names with "(default)" first; null is the default.</summary>
    private static ComboBox Choice(List<string> names, string? current, Action<string?> set)
    {
        var items = new List<string> { DefaultChoice };
        items.AddRange(names);
        if (!string.IsNullOrEmpty(current) && !items.Contains(current)) items.Add(current);
        var box = new ComboBox { ItemsSource = items, SelectedItem = string.IsNullOrEmpty(current) ? DefaultChoice : current,
            HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 360 };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is not string v) return;
            set(v == DefaultChoice ? null : v);
            SettingsStore.Save();
        };
        return box;
    }

    private static TextBlock Caption(string text) => new() { Text = text, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };

    private static Grid Labelled(string label, Control control)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(t);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>Fonts, pointer, icons and the look of GTK and Qt programs (LookAndFeel).</summary>
    private static void BuildLook(StackPanel p)
    {
        var s = SettingsStore.Current;

        p.Children.Add(Eyebrow("FONTS"));
        var font = Choice(LookAndFeel.Fonts(), s.UiFont, v => { SettingsStore.Current.UiFont = v; LookAndFeel.ApplyShellFont(); LookAndFeel.ApplyOtherPrograms(); });
        p.Children.Add(Labelled("Interface font", font));
        var sizes = new[] { 11.0, 12, 13, 14, 15, 16, 18, 20 };
        var size = new ComboBox { ItemsSource = sizes.Select(v => v == LookAndFeel.DefaultFontSize ? $"{v} px (default)" : $"{v} px").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(sizes, s.UiFontSize)), HorizontalAlignment = HorizontalAlignment.Stretch };
        size.SelectionChanged += (_, _) =>
        {
            if (size.SelectedIndex < 0) return;
            SettingsStore.Current.UiFontSize = sizes[size.SelectedIndex];
            SettingsStore.Save();
            LookAndFeel.ApplyShellFont();
            LookAndFeel.ApplyOtherPrograms();
        };
        p.Children.Add(Labelled("Text size", size));
        p.Children.Add(Caption("Crimson Banner's windows change at once. The terminal's font is in Terminal → Profiles, Writer's in Writer."));

        p.Children.Add(Eyebrow("MOUSE POINTER"));
        var cursors = LookAndFeel.CursorThemes();
        p.Children.Add(Labelled("Pointer theme", Choice(cursors, s.CursorTheme, v => { SettingsStore.Current.CursorTheme = v; LookAndFeel.ApplyPointer(); })));
        var csizes = new[] { 24, 32, 40, 48, 64, 96 };
        var csize = new ComboBox { ItemsSource = csizes.Select(v => v == 24 ? "24 (default)" : v.ToString()).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(csizes, s.CursorSize)), HorizontalAlignment = HorizontalAlignment.Stretch };
        csize.SelectionChanged += (_, _) =>
        {
            if (csize.SelectedIndex < 0) return;
            SettingsStore.Current.CursorSize = csizes[csize.SelectedIndex];
            SettingsStore.Save();
            LookAndFeel.ApplyPointer();
        };
        p.Children.Add(Labelled("Pointer size", csize));
        p.Children.Add(Caption((cursors.Count == 0 ? "No pointer themes are installed besides the default (Arch/CachyOS: e.g. pacman -S xcursor-themes). " : "")
            + (InputIsolation.InRedStarComp ? "Changes at once in redstar-comp; some X11 programs follow after they restart."
                                           : "Programs started from now on follow; everything follows at the next sign-in.")
            + (s.LargeCursor ? " Accessibility → Large mouse pointer keeps it at 48 or more." : "")));

        p.Children.Add(Eyebrow("OTHER PROGRAMS (GTK AND QT)"));
        var other = new CheckBox { Content = "Style other programs to match Crimson Banner", IsChecked = LookAndFeel.StylesOtherPrograms };
        var otherBox = new StackPanel { Spacing = 10, IsEnabled = LookAndFeel.StylesOtherPrograms };
        other.IsCheckedChanged += (_, _) =>
        {
            SettingsStore.Current.StyleOtherApps = other.IsChecked == true;
            SettingsStore.Save();
            otherBox.IsEnabled = other.IsChecked == true;
            LookAndFeel.ApplyEnvironment();
            LookAndFeel.ApplyOtherPrograms();
        };
        p.Children.Add(other);
        p.Children.Add(Caption("Writes GTK's settings (~/.config/gtk-3.0 and gtk-4.0, and GNOME's interface settings) and tells Qt programs "
            + "how to look. On by default in a Crimson Banner session; off when Crimson Banner runs on top of another desktop, since those are "
            + "that desktop's settings too. Light or dark follows the Theme above."));
        otherBox.Children.Add(Labelled("GTK theme", Choice(LookAndFeel.GtkThemes().Where(t => t != "Adwaita").ToList(), s.GtkTheme,
            v => { SettingsStore.Current.GtkTheme = v; LookAndFeel.ApplyOtherPrograms(); })));
        otherBox.Children.Add(Labelled("Icon theme", Choice(LookAndFeel.IconThemes(), s.IconTheme,
            v => { SettingsStore.Current.IconTheme = v; LookAndFeel.ApplyOtherPrograms(); })));
        var qtModes = new List<(string Key, string Label)> { ("gtk", "Follow GTK (Qt's own GTK theme support)") };
        qtModes.Add(("qt6ct", LookAndFeel.Qt6ctInstalled ? "qt6ct (its own settings program)" : "qt6ct (not installed: pacman -S qt6ct)"));
        qtModes.Add(("off", "Leave Qt programs alone"));
        var qt = new ComboBox { ItemsSource = qtModes.Select(m => m.Label).ToList(), SelectedIndex = Math.Max(0, qtModes.FindIndex(m => m.Key == s.QtStyle)),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        qt.SelectionChanged += (_, _) =>
        {
            if (qt.SelectedIndex < 0) return;
            SettingsStore.Current.QtStyle = qtModes[qt.SelectedIndex].Key;
            SettingsStore.Save();
            LookAndFeel.ApplyEnvironment();
            LookAndFeel.ApplyOtherPrograms();
        };
        otherBox.Children.Add(Labelled("Qt programs", qt));
        otherBox.Children.Add(Caption("Programs that are already open pick up the theme, icons and font the next time they start; "
            + "GTK 4 programs often at once. Crimson Banner's own apps keep their own icons."));
        p.Children.Add(otherBox);
    }

    private static string InputIsolationStatus() => InputIsolation.Mode switch
    {
        "xwayland" => "Now: isolated (Xwayland, the Wayland desktop's shortcuts are switched off while Crimson Banner has the keyboard).",
        "xephyr" => "Now: isolated (Xephyr on X11).",
        "redstar-comp" => "Now: isolated by redstar-comp — full screen, and the other desktop passes its shortcuts through (if it allows that).",
        "xephyr-on-wayland" =>"Now: partly isolated — Xephyr on Wayland can't switch off the desktop's own shortcuts. Xwayland 23.1 or newer fixes that.",
        _ when Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1" => "Now: Crimson Banner OS is the whole session, so nothing else can take the keyboard.",
        _ when InputIsolation.Missing != null => "Now: not isolated — install Xephyr (Debian/Ubuntu: xserver-xephyr · Fedora: xorg-x11-server-Xephyr · Arch: xorg-server-xephyr), or Xwayland 23.1+ on Wayland.",
        _ => "Now: running directly on the other desktop."
    };

    private static Control BuildTaskbar()
    {
        var s = SettingsStore.Current;
        var p = PageOf("Desktop & Taskbar", "Choose what the taskbar shows and which apps stay pinned to it.");

        p.Children.Add(Eyebrow("KEYBOARD AND MOUSE"));
        p.Children.Add(Check("Keep the keyboard and mouse inside Crimson Banner OS when it runs on another desktop", s.IsolateInput, v => SettingsStore.Current.IsolateInput = v));
        p.Children.Add(new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = "Crimson Banner OS opens in its own display (like a virtual machine), so Super, Alt+Tab, hot corners and panels of the "
                 + "other desktop can't take over. Press Ctrl+Shift to release the keyboard and mouse, and again to capture them. "
                 + "Takes effect the next time Crimson Banner OS starts.\n" + InputIsolationStatus()
        });

        p.Children.Add(Eyebrow("WINDOWS"));
        p.Children.Add(Check("Soft shadows under windows", s.WindowShadows, v => SettingsStore.Current.WindowShadows = v));
        p.Children.Add(Check("When I snap a window, offer the others for the space left (Snap Assist)", s.SnapAssist, v => SettingsStore.Current.SnapAssist = v));
        p.Children.Add(Check("Peek at the desktop when I point at Show desktop, right of the clock", s.PeekDesktop, v => SettingsStore.Current.PeekDesktop = v));
        p.Children.Add(Check("Preview a taskbar button's windows when I point at it", s.TaskbarPreviews, v => SettingsStore.Current.TaskbarPreviews = v));
        p.Children.Add(new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = "Drag a window by its title bar to the left or right edge of the screen to give it half, to a corner for a quarter, "
                 + "or to the top to maximise it; a frame in your accent colour shows where it will go. Super and the arrow keys snap too.",
        });
        p.Children.Add(new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = (InputIsolation.InRedStarComp ? "Drawn by redstar-comp. " : "Drawn by the picom compositor (install picom if it's missing). ")
                 + "Windows that are maximised or full screen, menus and tooltips have none. Takes effect the next time Crimson Banner OS starts."
                 + (X11Props.CompositorRunning() ? "" : "\nNow: no compositor is running, so there are no shadows.")
        });

        p.Children.Add(Eyebrow("VIRTUAL DESKTOPS"));
        p.Children.Add(Check("Show the Task View button next to Start", s.TaskViewButton, v => SettingsStore.Current.TaskViewButton = v));
        p.Children.Add(Check("On the taskbar, show windows from every desktop", s.TaskbarAllDesktops, v => SettingsStore.Current.TaskbarAllDesktops = v));
        p.Children.Add(Check("Alt+Tab offers windows from every desktop", s.AltTabAllDesktops, v => SettingsStore.Current.AltTabAllDesktops = v));
        var openTaskView = new Button { Content = "Open Task View", Padding = new Avalonia.Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        openTaskView.Click += (_, _) => TaskViewWindow.Toggle();
        p.Children.Add(openTaskView);
        p.Children.Add(new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = "Task View (Super+Tab) shows your windows and desktops: add a desktop, drag a window onto one, right-click to rename "
                 + "or close it. Super+Ctrl+D adds a desktop, Super+Ctrl+Left and Right switch, with Shift the active window comes along, "
                 + "and Super+Ctrl+F4 closes the one in use (its windows move to the desktop on its left)."
                 + (VirtualDesktops.Count > 1 ? $"\nNow: {VirtualDesktops.Count} desktops, {VirtualDesktops.Name(VirtualDesktops.Current)} in use." : ""),
        });

        p.Children.Add(Eyebrow("WAYLAND (EXPERIMENTAL)"));
        p.Children.Add(Check("Run Crimson Banner OS in its own Wayland compositor", s.WaylandCompositor, v => SettingsStore.Current.WaylandCompositor = v));
        bool haveComp = InputIsolation.RedStarCompPath() != null;
        p.Children.Add(new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = "redstar-comp (wlroots) takes the place of the window manager and picom: same title bars, snapping and shadows, "
                 + "and native Wayland apps work too. On another desktop it opens "
                 + "full screen and asks that desktop to pass Super, Alt+Tab and its other shortcuts through (KDE and GNOME may ask "
                 + "once); set REDSTAR_COMP_WINDOWED=1 for an ordinary window. Ctrl+Alt+Backspace leaves. "
                 + "For a full session pick \"Crimson Banner OS (Wayland)\" at the login screen. Takes effect the next time Crimson Banner OS starts."
                 + (InputIsolation.InRedStarComp ? "\nNow: running inside redstar-comp."
                    : haveComp ? "" : "\nNot installed: build it with compositor/build.sh (needs wlroots 0.20).")
        });
        p.Children.Add(Check("Run other programs natively on Wayland", s.WaylandApps, v => SettingsStore.Current.WaylandApps = v));
        p.Children.Add(new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = "Inside redstar-comp, GTK, Qt and Electron programs pick Wayland for themselves. Screen sharing in OBS and "
                 + "Discord needs this: only as Wayland programs do they offer the PipeWire screen capture. Turn it off if a "
                 + "program misbehaves, and everything talks X11 through Xwayland as before. Takes effect the next time Crimson Banner "
                 + "OS starts."
        });
        p.Children.Add(Check("Draw Crimson Banner's own windows natively on Wayland (experimental)", s.NativeWaylandShell, v => SettingsStore.Current.NativeWaylandShell = v));
        p.Children.Add(new TextBlock
        {
            Classes = { "caption" }, TextWrapping = TextWrapping.Wrap,
            Text = "Inside redstar-comp, Crimson Banner's desktop, taskbar and apps use Avalonia's Wayland backend instead of Xwayland: "
                 + "sharper on scaled screens and no X11 in between. Avalonia marks it experimental. If it fails to start, Crimson Banner "
                 + "starts again through Xwayland on its own and tells you. Other programs are not affected. Takes effect the next time "
                 + "Crimson Banner OS starts."
                 + (WaylandShell.Active ? "\nNow: drawing natively on Wayland."
                    : WaylandShell.FellBackReason is { } why ? "\nNow: " + why : "")
        });

        p.Children.Add(Eyebrow("TASKBAR"));
        p.Children.Add(Check("Show window titles next to taskbar icons", s.TaskbarLabels, v => SettingsStore.Current.TaskbarLabels = v));
        p.Children.Add(Check("24-hour clock", s.Clock24h, v => SettingsStore.Current.Clock24h = v));

        p.Children.Add(Eyebrow("NOTIFICATIONS"));
        var dnd = new CheckBox { Content = "Do not disturb (keep notifications in the notification centre, no pop-ups)", IsChecked = Notifier.DoNotDisturb };
        dnd.IsCheckedChanged += (_, _) => Notifier.SetDoNotDisturb(dnd.IsChecked == true);
        p.Children.Add(dnd);

        p.Children.Add(Eyebrow("PINNED APPS"));
        var child = s.AccountType == "Child";
        foreach (var app in AppRegistry.Apps.Where(a => !(child && a.RestrictedForChild)))
        {
            var id = app.Id;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Avalonia.Thickness(0, 2) };
            var icon = AppIcons.Create(id, app.Name, 26);
            row.Children.Add(icon);
            var name = new TextBlock { Text = app.Name, Margin = new Avalonia.Thickness(10, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var sw = new ToggleSwitch { IsChecked = WindowManager.IsPinned(id), OnContent = "", OffContent = "" };
            sw.IsCheckedChanged += (_, _) =>
            {
                if ((sw.IsChecked == true) != WindowManager.IsPinned(id)) WindowManager.TogglePin(id);
            };
            Grid.SetColumn(sw, 2);
            row.Children.Add(sw);
            p.Children.Add(row);
        }
        return p;
    }

    private static Control BuildGames()
    {
        var s = SettingsStore.Current;
        var p = PageOf("Games", "Scores are kept on this computer.");
        p.Children.Add(new TextBlock { Text = $"Ping Pong - best solo rally: {s.PongBest}" });
        p.Children.Add(new TextBlock { Text = $"Ping Pong - wins against the computer: {s.PongAiWins}" });
        p.Children.Add(new TextBlock { Text = $"Blocks - best score: {s.BlocksBest:N0}" });
        p.Children.Add(new TextBlock { Text = $"Gems - best score: {s.GemsBest:N0}" });
        p.Children.Add(new TextBlock { Text = $"Mahjongg - boards cleared: {s.MahjonggWins}" });
        var reset = new Button { Content = "Reset scores", Classes = { "flat" }, HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            var c = SettingsStore.Current;
            c.PongBest = c.PongAiWins = c.BlocksBest = c.GemsBest = c.MahjonggWins = 0;
            SettingsStore.Save();
            reset.Content = "Scores reset";
        };
        p.Children.Add(reset);
        return p;
    }
}
