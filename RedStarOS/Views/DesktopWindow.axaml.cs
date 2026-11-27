using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>The desktop shell: wallpaper, icons, toasts and the taskbar (see <see cref="Taskbar"/>).</summary>
public partial class DesktopWindow : Window
{
    private static void DriveArrived(DriveVolume v) =>
        Notifier.Show("Drive connected", $"“{v.Label}” is plugged in.", "files", new NotificationAction("Open", () => Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var at = await RemovableDrives.Mount(RemovableDrives.Volumes.FirstOrDefault(x => x.ObjectPath == v.ObjectPath) ?? v);
                WindowManager.Open(new FileManagerWindow(at), "files");
            }
            catch (Exception ex) { Notifier.Show("Drive", $"Couldn't open “{v.Label}”: {ex.Message}", "files"); }
        })));

    private static bool _autostarted;

    private readonly DesktopIconsView _icons;

    private readonly DispatcherTimer _background = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime _lastSlide = DateTime.Now;

    public DesktopWindow()
    {
        InitializeComponent();
        WindowManager.Desktop = this;
        // In Red Star's own display / session the desktop is a real desktop window: always at the bottom, on every
        // screen, filling it — and the taskbar's height is reserved for maximised windows.
        if (WaylandShell.Active)
        {
            // natively on Wayland there are no X11 window types: redstar-comp is told which window is the desktop
            CompIpc.Start();
            CompIpc.Role("desktop", Title ?? "Crimson Banner OS");
            Bar.SizeChanged += (_, _) => ReserveTaskbar();
            Opened += (_, _) => ReserveTaskbar();
        }
        else if (InputIsolation.IsNested || Environment.GetEnvironmentVariable("REDSTAR_SESSION") == "1")
        {
            if (TryGetPlatformHandle()?.Handle is { } xid && xid != IntPtr.Zero) X11Props.MarkDesktop(xid);
            Bar.SizeChanged += (_, _) => ReserveTaskbar();
            Opened += (_, _) => ReserveTaskbar();
        }
        WindowState = SettingsStore.Current.Fullscreen ? WindowState.FullScreen : WindowState.Maximized;

        WallpaperService.Changed += LoadWallpaper;
        ThemeService.Changed += OnThemeChanged;
        Notifier.PostedRecord += ShowToast;
        LoadWallpaper();
        _icons = new DesktopIconsView(() => DesktopFolder);
        _icons.Activated += OpenIcon;
        _icons.DesktopMenu += BuildDesktopMenu;
        IconsHost.Children.Add(_icons);
        _icons.Reload();
        ShowSplash();

        _background.Tick += (_, _) => BackgroundTick();
        _background.Start();

        KeyUp += (_, e) =>
        {
            if (e.Key == Key.LWin || e.Key == Key.RWin ||
                (e.Key == Key.Escape && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
                Bar.ToggleStart();
        };

        Opened += (_, _) =>
        {
            if (WaylandShell.Active) DispatcherTimer.RunOnce(WaylandShell.Started, TimeSpan.FromSeconds(8));
            if (WaylandShell.FellBackReason is { } why)
                DispatcherTimer.RunOnce(() => Notifier.Show("Using Xwayland", why + " Turn native drawing off in Settings → Desktop & Taskbar if this keeps happening.", "settings"), TimeSpan.FromSeconds(2));
            BackgroundTick();
            ClipboardHistory.Start(this);
            X11Desktop.Shortcut += OnGlobalShortcut;
            X11Desktop.Snapped += SnapAssist.Offer;     // a window dropped at an edge: offer the others for the rest
            X11Desktop.Start();
            WindowManager.TrackForeignWindows();      // other programs' windows get taskbar buttons too
            VirtualDesktops.Switched += ShowDesktopSign;
            // night light, once the window manager or compositor is there to take it
            DispatcherTimer.RunOnce(NightLight.Start, TimeSpan.FromSeconds(1.5));
            PortalSetup.Run();                         // inside redstar-comp: screenshots and screen sharing for other apps
            SystemTray.Start();                        // other programs' tray icons (Discord, Steam…) on the taskbar
            OtherAppsNotifications.Start();            // other programs' notifications (org.freedesktop.Notifications)
            // drives: shown in Files, and "plugged in" notifications when Crimson Banner is the session
            RemovableDrives.Arrived += v => { if (Autostart.IsSession) DriveArrived(v); };
            _ = RemovableDrives.StartAsync();
            // keyboard layouts (redstar-comp got them at start; X11 needs setxkbmap)
            if (SettingsStore.Current.KeyboardLayouts.Count > 0 && !InputIsolation.InRedStarComp) RegionSettings.ApplyKeyboard();
            // Messenger's Matrix connection, so new messages notify even before it's opened
            RedStarOS.Services.Matrix.MatrixService.Resume();
            // the screen reader, if it was on at sign-out
            if (SettingsStore.Current.ScreenReaderOn) ScreenReader.Start();
            // screen off, lock and sleep when nobody uses the computer (System Settings → Power)
            if (Autostart.IsSession) IdleManager.Start();
            // the system's package updates, once a day (PackageKit)
            if (Autostart.IsSession && SettingsStore.Current.AutoUpdateCheck) SystemUpdates.CheckInBackground();
            // the administrator password prompt for polkit, when Crimson Banner is the session
            if (Autostart.IsSession) PolkitAgent.Start(PolkitDialog.Ask);
            // programs that start at sign-in, once the tray and notifications are there for them (only as the session)
            if (Autostart.IsSession && SettingsStore.Current.RunAutostart && !_autostarted)
            {
                _autostarted = true;
                DispatcherTimer.RunOnce(() =>
                {
                    var started = Autostart.RunAll();
                    if (started.Count > 0) Console.Error.WriteLine("autostart: " + string.Join(", ", started));
                }, TimeSpan.FromSeconds(2));
            }
            UpdateChecker.StartBackground();
            if (InputIsolation.IsNested)
                Notifier.Show("Keyboard and mouse are in Crimson Banner OS",
                    InputIsolation.Mode == "xephyr-on-wayland"
                        ? "Some shortcuts of your other desktop may still work — Xwayland 23.1+ blocks them all. Ctrl+Shift releases the keyboard and mouse."
                        : "Press Ctrl+Shift to release them (and again to come back). Shut down Crimson Banner OS to return to your desktop.", "settings");
            else if (InputIsolation.Missing != null)
                Notifier.Show("Keyboard and mouse can't be kept in Crimson Banner OS",
                    "Your other desktop may react to Super and Alt+Tab. Install Xephyr (or Xwayland 23.1+ on Wayland) — see Settings → Desktop & Taskbar.", "settings",
                    new NotificationAction("Details", () => SettingsWindow.OpenPage("Desktop & Taskbar")));
            LookAndFeel.ApplyPointer();
            SystemSounds.Play("startup");
            if (!SettingsStore.Current.WelcomeDone)
                Avalonia.Threading.DispatcherTimer.RunOnce(() => WelcomeWindow.Open(), TimeSpan.FromSeconds(1.2));
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _background.Stop();
        WallpaperService.Changed -= LoadWallpaper;
        ThemeService.Changed -= OnThemeChanged;
        Notifier.PostedRecord -= ShowToast;
        X11Desktop.Shortcut -= OnGlobalShortcut;
        X11Desktop.Snapped -= SnapAssist.Offer;
        VirtualDesktops.Switched -= ShowDesktopSign;
        NightLight.Stop();
        base.OnClosed(e);
    }

    // ------------------------------------------------------------ Background jobs

    private void BackgroundTick()
    {
        foreach (var ev in CalendarStore.TakeDueReminders(DateTime.Now))
            Notifier.Show("Reminder", ev.ToString(), "calendar");

        var s = SettingsStore.Current;
        if (s.SlideshowEnabled && (DateTime.Now - _lastSlide).TotalMinutes >= Math.Max(1, s.SlideshowMinutes))
        {
            _lastSlide = DateTime.Now;
            WallpaperService.Next();
        }
    }

    // ------------------------------------------------------------ Global shortcuts (X11Desktop)

    private void OnGlobalShortcut(string cmd)
    {
        if (cmd.StartsWith("switcher-")) { WindowSwitcher.Handle(cmd); return; }
        if (cmd.StartsWith("screenshot")) { _ = Screenshots.Take(cmd); return; }
        switch (cmd)
        {
            case "start": case "search": Bar.ToggleStart(); break;
            case "clipboard": Bar.ShowClipboard(); break;
            case "files": WindowManager.Open(new FileManagerWindow(AppPaths.Home), "files"); break;
            case "terminal": TerminalLauncher.Launch(); break;
            case "lock": ShellActions.Lock(); break;
            case "screen-reader": ScreenReader.Toggle(); break;
            case "power-menu": PowerMenu.Show(); break;
            case "volume-up": Bar.StepVolume(Taskbar.VolumeStep); break;
            case "volume-down": Bar.StepVolume(-Taskbar.VolumeStep); break;
            case "volume-mute": Bar.StepVolume(0); break;
            case "desktop": ShowDesktop.Toggle(); break;          // Super+D, like the button: again brings them back
            case "snap-left": SnapActive(SnapMode.Left, "left"); break;
            case "snap-right": SnapActive(SnapMode.Right, "right"); break;
            case "maximize": SnapActive(SnapMode.Full, "maximize"); break;
            case "minimize": SnapActive(null, "minimize"); break;
            // virtual desktops
            case "task-view": TaskViewWindow.Toggle(); break;
            case "desktop-next": VirtualDesktops.Next(); break;
            case "desktop-prev": VirtualDesktops.Previous(); break;
            case "desktop-move-next": MoveActiveToDesktop(+1); break;
            case "desktop-move-prev": MoveActiveToDesktop(-1); break;
            case "desktop-new": { int d = VirtualDesktops.Add(); if (d >= 0) VirtualDesktops.Switch(d); break; }
            case "desktop-close": VirtualDesktops.Close(VirtualDesktops.Current); break;
            default: break;
        }
    }

    /// <summary>Super+Ctrl+Shift+Left/Right: the active window goes to the next desktop, and so do you.</summary>
    private static void MoveActiveToDesktop(int step)
    {
        X11Desktop.ActiveWindow(xid =>
        {
            int to = VirtualDesktops.Current + step;
            if (xid == 0 || to < 0 || to >= VirtualDesktops.Count) return;
            if (WindowManager.Desktop?.TryGetPlatformHandle()?.Handle.ToInt64() == xid) return;   // the desktop itself stays
            VirtualDesktops.Move(xid, to);
            VirtualDesktops.Switch(to);
            X11Desktop.Activate(xid);
        });
    }

    // ------------------------------------------------------------ The switch's sign ("Desktop 2", for a moment)

    // a popup: above every window (an override-redirect window on X11; redstar-comp draws the desktop's popups on top)
    private Avalonia.Controls.Primitives.Popup? _deskSign;
    private readonly DispatcherTimer _deskSignTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    private void ShowDesktopSign(int desk)
    {
        if (TaskViewWindow.IsOpen) return;
        if (_deskSign == null)
        {
            _deskSign = new Avalonia.Controls.Primitives.Popup
            {
                PlacementTarget = this, Placement = PlacementMode.Center, IsLightDismissEnabled = false, Topmost = true,
                IsHitTestVisible = false,
            };
            if (Content is Panel root) root.Children.Add(_deskSign);     // in the tree (it draws nothing in place)
            _deskSignTimer.Tick += (_, _) => { _deskSignTimer.Stop(); if (_deskSign != null) _deskSign.IsOpen = false; };
        }
        _deskSign.Child = DesktopSign(desk);
        _deskSign.IsOpen = true;
        _deskSignTimer.Stop(); _deskSignTimer.Start();
    }

    /// <summary>The sign shown for a moment after switching: the desktop's name, and a dot for each desktop.</summary>
    public static Control DesktopSign(int desk)
    {
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < VirtualDesktops.Count && VirtualDesktops.Count <= 12; i++)
        {
            var dot = new Border { Width = i == desk ? 18 : 7, Height = 7, CornerRadius = new Avalonia.CornerRadius(4) };
            dot.Classes.Add(i == desk ? "deskdot-on" : "deskdot");
            dots.Children.Add(dot);
        }
        return new Border
        {
            Classes = { "desksign" },
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = VirtualDesktops.Name(desk), FontSize = 18, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    dots,
                },
            },
        };
    }

    /// <summary>Super+arrows: Red Star windows use the shell's own snapping, other programs the window manager's.</summary>
    private static void SnapActive(SnapMode? mode, string how)
    {
        X11Desktop.ActiveWindow(xid =>
        {
            var task = WindowManager.Tasks.OfType<NativeTask>().FirstOrDefault(t =>
                t.Window.TryGetPlatformHandle()?.Handle.ToInt64() == xid || WaylandShell.CompIdOf(t.Window) == xid);
            if (task != null)
            {
                if (mode is { } m) task.Snap(m); else task.Minimize();
            }
            else X11Desktop.SnapWindow(xid, how);
        });
    }

    // ------------------------------------------------------------ Toasts

    private void ShowToast(NotificationRecord rec)
    {
        string title = rec.Title, message = rec.Message; string? appId = rec.AppId;
        var text = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(10, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = message, FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxLines = 4 });

        var icon = appId != null && AppRegistry.Find(appId) != null
            ? AppIcons.Create(appId, title, 32)
            : AppIcons.Tile("bell", 32);
        icon.VerticalAlignment = VerticalAlignment.Top;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        Border? toastRef = null;
        if (rec.Actions is { Count: > 0 } actions)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
            foreach (var act in actions)
            {
                var a = act;
                var b = new Button { Content = a.Label, Padding = new Avalonia.Thickness(10, 3), FontSize = 12 };
                b.Click += (_, e) => { e.Handled = true; if (toastRef != null) Toasts.Children.Remove(toastRef); try { a.Run(); } catch { } };
                buttons.Children.Add(b);
            }
            text.Children.Add(buttons);
        }
        var toast = new Border { Child = row };
        toastRef = toast;
        toast.Classes.Add("toast");
        toast.PointerPressed += (_, _) => Toasts.Children.Remove(toast);       // click to dismiss
        Toasts.Children.Add(toast);

        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(rec.Actions is { Count: > 0 } ? 10 : 6) };
        t.Tick += (_, _) => { t.Stop(); Toasts.Children.Remove(toast); };
        t.Start();
    }

    private void OnSettings(object? s, RoutedEventArgs e) => ShellActions.OpenSettings();

    private int _strut = -1;
    private void ReserveTaskbar()
    {
        if (WaylandShell.Active)
        {
            // redstar-comp works in logical pixels (the screen's own scale taken out)
            double screenScale = Screens.ScreenFromVisual(this)?.Scaling ?? 1;
            int lh = (int)Math.Ceiling(Bar.Bounds.Height * RenderScaling / Math.Max(1, screenScale));
            if (lh <= 0 || lh == _strut) return;
            _strut = lh;
            CompIpc.Role("desktop", Title ?? "Crimson Banner OS", bottom: lh);
            return;
        }
        if (TryGetPlatformHandle()?.Handle is not { } xid || xid == IntPtr.Zero) return;
        int h = (int)Math.Ceiling(Bar.Bounds.Height * RenderScaling);
        if (h <= 0 || h == _strut) return;
        _strut = h;
        int sw = Screens.ScreenFromVisual(this)?.Bounds.Width ?? Screens.Primary?.Bounds.Width ?? 0;
        X11Props.SetBottomStrut(xid, h, sw);
    }

    // ------------------------------------------------------------ Start-up splash

    /// <summary>A short Red Star splash over the desktop while it finishes loading; fades out (or just goes, with Reduce motion).</summary>
    private void ShowSplash()
    {
        if (Content is not Grid root) return;
        // the deck's graphite, lit by a crimson glow, with the mark (branding/)
        var splash = new Border
        {
            Background = new LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0.5, 0, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(0.5, 1, Avalonia.RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#1D1D21"), 0), new GradientStop(Color.Parse("#070708"), 1) },
            },
            ZIndex = 10000, IsHitTestVisible = false,
            Child = new Panel
            {
                Children =
                {
                    new Border
                    {
                        Background = new RadialGradientBrush
                        {
                            GradientStops = { new GradientStop(Color.Parse("#55C4141C"), 0), new GradientStop(Color.Parse("#00C4141C"), 1) },
                        },
                    },
                    new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 10,
                        Children =
                        {
                            Brand.Mark(132),
                            new TextBlock { Text = "Crimson Banner OS", Foreground = Brushes.White, FontSize = 34, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 8, 0, 0) },
                            new TextBlock { Text = ("Version " + AppVersion.Current.Short).ToUpperInvariant(), Foreground = new SolidColorBrush(Color.Parse("#F02028")), FontSize = 11, FontWeight = FontWeight.Bold, LetterSpacing = 1, HorizontalAlignment = HorizontalAlignment.Center },
                        }
                    },
                }
            }
        };
        Grid.SetRowSpan(splash, Math.Max(1, root.RowDefinitions.Count));
        if (root.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(splash, root.ColumnDefinitions.Count);
        root.Children.Add(splash);
        bool reduce = SettingsStore.Current.ReduceMotion;
        if (!reduce)
            splash.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(500) }
            };
        DispatcherTimer.RunOnce(() =>
        {
            splash.Opacity = 0;
            DispatcherTimer.RunOnce(() => root.Children.Remove(splash), TimeSpan.FromMilliseconds(reduce ? 0 : 600));
        }, TimeSpan.FromMilliseconds(1100));
    }

    // ------------------------------------------------------------ Wallpaper

    /// <summary>Light or dark: the default wallpaper follows, when no other has been chosen.</summary>
    private void OnThemeChanged()
    {
        if (string.IsNullOrEmpty(SettingsStore.Current.WallpaperPath)) LoadWallpaper();
    }

    /// <summary>The wallpaper as drawn (Task View's little desktops).</summary>
    public IImage? Wallpaper => WallpaperImage.Source;

    private void LoadWallpaper()
    {
        try
        {
            var path = SettingsStore.Current.WallpaperPath;
            Bitmap bmp;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                bmp = DecodeForScreen(path);
            }
            else
            {
                // none chosen: Banner, or Daylight in the light theme (branding/)
                bmp = new Bitmap(AssetLoader.Open(new Uri(ThemeService.IsDark
                    ? "avares://RedStarOS/Assets/wallpaper.jpg" : "avares://RedStarOS/Assets/Wallpapers/crimson-daylight-4k.jpg")));
            }
            var old = WallpaperImage.Source as IDisposable;
            WallpaperImage.Source = bmp;
            // already scaled to the screen in high quality: draw it without re-filtering on every repaint
            // (the desktop repaints parts of itself on each hover, which matters with the software renderer)
            RenderOptions.SetBitmapInterpolationMode(WallpaperImage,
                bmp != null && IsScreenSized(bmp) ? BitmapInterpolationMode.LowQuality : BitmapInterpolationMode.HighQuality);
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Notifier.Show("Wallpaper", "Could not load wallpaper: " + ex.Message);
        }
    }

    /// <summary>
    /// Big pictures (a 4K wallpaper on a 1080p screen) are scaled down once, in high quality, to the screen's size —
    /// sharper than letting every frame shrink them on the fly, and far less memory.
    /// </summary>
    private bool IsScreenSized(Bitmap b)
    {
        var sc = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (sc == null) return false;
        double cover = Math.Max((double)sc.Bounds.Width / b.PixelSize.Width, (double)sc.Bounds.Height / b.PixelSize.Height);
        return cover > 0.98 && cover < 1.02;
    }

    private Bitmap DecodeForScreen(string path)
    {
        int screenW = Screens.ScreenFromVisual(this)?.Bounds.Width ?? Screens.Primary?.Bounds.Width ?? 0;
        using var fs = File.OpenRead(path);
        var full = new Bitmap(fs);
        if (screenW <= 0 || full.PixelSize.Width <= screenW * 1.1) return full;
        // keep enough width that UniformToFill can still crop to the screen without upscaling
        int screenH = Screens.ScreenFromVisual(this)?.Bounds.Height ?? Screens.Primary?.Bounds.Height ?? 0;
        double cover = screenH > 0 ? Math.Max((double)screenW / full.PixelSize.Width, (double)screenH / full.PixelSize.Height) : (double)screenW / full.PixelSize.Width;
        int width = (int)Math.Ceiling(full.PixelSize.Width * cover);
        if (width >= full.PixelSize.Width) return full;
        var scaled = full.CreateScaledBitmap(new Avalonia.PixelSize(width, (int)Math.Ceiling(full.PixelSize.Height * cover)), BitmapInterpolationMode.HighQuality);
        full.Dispose();
        return scaled;
    }

    private void OnMoreWallpapers(object? s, RoutedEventArgs e) => WallpaperPickerWindow.Open(online: true);
    private void OnWallpaperPicker(object? s, RoutedEventArgs e) => WallpaperPickerWindow.Open();
    private void OnNextWallpaper(object? s, RoutedEventArgs e) => WallpaperService.Next();
    private void OnDefaultWallpaper(object? s, RoutedEventArgs e) => WallpaperService.Set(null);

    private async void OnChangeWallpaper(object? s, RoutedEventArgs e)
    {
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Change Wallpaper",
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });
        if (files.Count == 0) return;
        var local = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(local)) WallpaperService.Set(local);
    }

    // ------------------------------------------------------------ Desktop icons

    private static string DesktopFolder
    {
        get
        {
            var d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return string.IsNullOrEmpty(d) ? System.IO.Path.Combine(AppPaths.Home, "Desktop") : d;
        }
    }

    private void OpenIcon(DesktopIconsView.Item item)
    {
        if (item.IsDir) WindowManager.Open(new FileManagerWindow(item.Path), "files");
        else if (item.App != null) DesktopEntries.Launch(item.App);
        else FileOpener.Open(item.Path);
    }

    private static MenuItem Item(string header, string? icon, Action act, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        if (icon != null) m.Icon = Icons.Glyph(icon, 16);
        m.Click += (_, _) => act();
        return m;
    }

    private static MenuItem Check(string header, bool on, Action act)
    {
        var m = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = on };
        m.Click += (_, _) => act();
        return m;
    }

    private static MenuItem Radio(string header, bool on, Action act)
    {
        var m = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = on, GroupName = header };
        m.Click += (_, _) => act();
        return m;
    }

    /// <summary>The desktop's menu, as on Windows: View, Sort by, Refresh, Paste, New, then the display and its look.</summary>
    private ContextMenu BuildDesktopMenu()
    {
        var l = _icons.Layout;
        var view = new MenuItem { Header = "View", Icon = Icons.Glyph("grid", 16) };
        view.ItemsSource = new Control[]
        {
            Radio("Large icons", l.Size == DesktopIconSize.Large, () => _icons.SetSize(DesktopIconSize.Large)),
            Radio("Medium icons", l.Size == DesktopIconSize.Medium, () => _icons.SetSize(DesktopIconSize.Medium)),
            Radio("Small icons", l.Size == DesktopIconSize.Small, () => _icons.SetSize(DesktopIconSize.Small)),
            new Separator(),
            Check("Auto arrange icons", l.AutoArrange, () => _icons.SetAutoArrange(!l.AutoArrange)),
            Check("Show desktop icons", l.ShowIcons, () => _icons.SetShowIcons(!l.ShowIcons)),
        };
        var sort = new MenuItem { Header = "Sort by", Icon = Icons.Glyph("sort", 16) };
        sort.ItemsSource = new Control[]
        {
            Item("Name", null, () => _icons.SetSort(DesktopSort.Name)),
            Item("Size", null, () => _icons.SetSort(DesktopSort.Size)),
            Item("Item type", null, () => _icons.SetSort(DesktopSort.Type)),
            Item("Date modified", null, () => _icons.SetSort(DesktopSort.Date)),
        };
        var @new = new MenuItem { Header = "New", Icon = Icons.Glyph("plus", 16) };
        @new.ItemsSource = new Control[]
        {
            Item("Folder", "folder", () => _icons.New("New folder", folder: true)),
            new Separator(),
            Item("Text document", "note", () => _icons.New("New text document.txt", folder: false)),
        };
        var look = new MenuItem { Header = "Personalise", Icon = Icons.Glyph("palette", 16) };
        look.ItemsSource = new Control[]
        {
            Item("Wallpapers…", "image", () => WallpaperPickerWindow.Open()),
            Item("Next wallpaper", "refresh", () => WallpaperService.Next()),
            Item("Choose a picture…", "folder", () => OnChangeWallpaper(null, new RoutedEventArgs())),
            Item("Find wallpapers online…", "globe", () => WallpaperPickerWindow.Open(online: true)),
            Item("Default wallpaper", "restore", () => WallpaperService.Set(null)),
            new Separator(),
            Item("Appearance settings", "palette", () => SettingsWindow.OpenPage("Appearance")),
        };
        return new ContextMenu
        {
            ItemsSource = new Control[]
            {
                view, sort,
                Item("Refresh", "refresh", () => _icons.Reload()),
                new Separator(),
                Item("Paste", "paste", () => _icons.Paste(), _icons.CanPaste),
                new Separator(),
                @new,
                new Separator(),
                Item("Open in Terminal", "terminal", () => TerminalLauncher.Launch(DesktopFolder)),
                Item("Display settings", "monitor", () => SettingsWindow.OpenPage("Displays")),
                look,
            },
        };
    }
}
