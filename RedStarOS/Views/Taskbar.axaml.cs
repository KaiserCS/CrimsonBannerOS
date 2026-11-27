using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Implemented by flyout panels so they can close their own flyout.</summary>
public interface IShellPanel
{
    event Action? CloseRequested;
}

/// <summary>
/// The taskbar: Start button, pinned + running apps, the app tray
/// (notification centre, app icons such as media controls) and the system tray (quick settings, clock).
/// </summary>
public partial class Taskbar : UserControl
{
    private const int MaxInlineTrayIcons = 4;
    // window titles share the room the row has: up to LabelMax each, a step at a time narrower as windows open, and
    // icons only (titles in the previews and tooltips) once a title would be shorter than LabelMin
    private const double LabelMax = 130, LabelMin = 48, LabelStep = 10;
    private const double IconSlot = 26 + 2 * 9 + 4;   // icon, the button's margins, the row's spacing

    /// <summary>How wide each window's title may be when <paramref name="labelled"/> of the <paramref name="buttons"/>
    /// buttons carry one in a row <paramref name="room"/> wide; 0 = icons only. Public for the tests.</summary>
    public static double LabelWidth(double room, int buttons, int labelled)
    {
        if (labelled <= 0 || room <= 0) return 0;
        double each = (room - buttons * IconSlot) / labelled - 8 - 2;      // the gap before the title, the wider margin
        each = Math.Floor(each / LabelStep) * LabelStep;
        return each < LabelMin ? 0 : Math.Min(LabelMax, each);
    }

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly TrayEntry _bell = new() { Key = "shell:notifications", Glyph = "bell", Tooltip = "Notifications" };

    private Flyout _startFlyout = null!;
    private StartMenu _startMenu = null!;
    private string _lastClock = "";

    static Taskbar()
    {
        // Tooltips of taskbar buttons open above the taskbar, PanelGap clear of it, instead of under the pointer
        // where they covered the neighbouring buttons and took their clicks.
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((c, _) =>
        {
            if (c.FindAncestorOfType<Taskbar>() is not { } bar) return;
            if (c.TranslatePoint(new Point(0, 0), bar) is not { } p) return;
            ToolTip.SetPlacement(c, PlacementMode.Top);
            ToolTip.SetVerticalOffset(c, -(PanelGap + p.Y));
            ToolTip.SetHorizontalOffset(c, 0);
        });
    }

    public Taskbar()
    {
        InitializeComponent();
        BuildStartMenu();

        SystemTrayButton.Click += (_, _) => ShowPanel(SystemTrayButton, new QuickSettingsPanel());
        // scrolling over the network / volume / battery button turns the volume up and down, 5% a notch
        SystemTrayButton.PointerWheelChanged += (_, e) =>
        {
            if (e.Delta.Y == 0) return;
            e.Handled = true;
            StepVolume(e.Delta.Y > 0 ? VolumeStep : -VolumeStep);
        };
        ClockButton.Click += (_, _) => ShowPanel(ClockButton, new CalendarPanel());
        TaskViewButton.Content = Icons.Create("task-view", 20);
        TaskViewButton.Click += (_, _) => TaskViewWindow.Toggle();
        Avalonia.Automation.AutomationProperties.SetName(TaskViewButton, "Task View");

        TaskPreview.Host = PreviewPopup;

        // Show desktop, and Peek: half a second's rest on it makes the windows see-through until the pointer leaves
        ShowDesktopButton.Click += (_, _) => { _peek.Stop(); ShowDesktop.Peek(false); ShowDesktop.Toggle(); };
        _peek.Tick += (_, _) => { _peek.Stop(); ShowDesktop.Peek(true); };
        ShowDesktopButton.PointerEntered += (_, _) => { _peek.Stop(); _peek.Start(); };
        ShowDesktopButton.PointerExited += (_, _) => { _peek.Stop(); ShowDesktop.Peek(false); };

        // the taskbar's own menu, on its empty space
        ContextRequested += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is { } b && b != ShowDesktopButton) return;   // a button's own menu
            BuildTaskbarMenu().Open(this);
            e.Handled = true;
        };

        // The notification centre: its own button beside the clock (clipboard history is in Quick Settings and Super+V).
        _bell.PanelFactory = () => new NotificationPanel();
        UpdateBell();

        // A click on a taskbar button moves focus to the desktop on press, which changes the task list. Rebuilding
        // the buttons between press and release would throw away the pressed button and its click, so while a
        // button is held the rebuild waits for the release.
        AddHandler(PointerPressedEvent, (_, _) => { _pointerDown = true; _pressGuard.Stop(); _pressGuard.Start(); },
            Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => ReleaseSoon(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => ReleaseSoon(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _pressGuard.Tick += (_, _) => { _pressGuard.Stop(); FinishPress(); };      // safety net: never wait forever

        WindowManager.Changed += RebuildTasks;
        VirtualDesktops.Changed += RebuildTasks;
        TrayService.Changed += RebuildAppTray;
        Notifier.Changed += UpdateBell;

        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        _status.Tick += (_, _) => RefreshStatus();
        _status.Start();

        _numbersAfter.Tick += (_, _) => { _numbersAfter.Stop(); ShowNumbers(true); };

        // the titles' width follows the room the row has
        TaskScroll.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 1 && SettingsStore.Current.TaskbarLabels && WindowManager.Tasks.Any())
                RebuildTasks();
        };

        UpdateClock();
        RebuildTasks();
        RebuildAppTray();
        RefreshStatus();
    }

    private readonly DispatcherTimer _peek = new() { Interval = TimeSpan.FromMilliseconds(500) };

    /// <summary>The taskbar's menu, as on Windows: Task Manager, Show desktop, and its settings.</summary>
    private static ContextMenu BuildTaskbarMenu()
    {
        MenuItem Item(string header, string icon, Action act)
        {
            var mi = new MenuItem { Header = header, Icon = Icons.Glyph(icon, 16) };
            mi.Click += (_, _) => act();
            return mi;
        }
        return new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Item("Task Manager", "activity", () => AppRegistry.Launch("taskmgr")),
                new Separator(),
                Item(ShowDesktop.Showing ? "Show open windows" : "Show desktop", "monitor", ShowDesktop.Toggle),
                new Separator(),
                Item("Taskbar settings", "settings", () => SettingsWindow.OpenPage("Desktop & Taskbar")),
            },
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _peek.Stop();
        _clock.Stop();
        _status.Stop();
        WindowManager.Changed -= RebuildTasks;
        VirtualDesktops.Changed -= RebuildTasks;
        TrayService.Changed -= RebuildAppTray;
        Notifier.Changed -= UpdateBell;
        base.OnDetachedFromVisualTree(e);
    }

    // ------------------------------------------------------------ Start menu

    private void BuildStartMenu()
    {
        _startMenu = new StartMenu();
        _startFlyout = new Flyout { Content = _startMenu, Placement = PlacementMode.TopEdgeAlignedLeft };
        _startFlyout.FlyoutPresenterClasses.Add("shell");
        StartButton.Flyout = _startFlyout;

        _startMenu.CloseRequested += () => _startFlyout.Hide();
        _startMenu.PowerRequested += ShellActions.Perform;
        _startFlyout.Opened += (_, _) => { _startMenu.Refresh(); StartButton.Classes.Set("open", true); };
        _startFlyout.Closed += (_, _) => StartButton.Classes.Set("open", false);
        // the same gap above the taskbar as from the screen's left edge, wherever the Start button sits inside it
        StartButton.LayoutUpdated += (_, _) => PlaceAbove(_startFlyout, StartButton, alignLeft: true);
        _startFlyout.Opening += (_, _) => { FitStart(); PlaceAbove(_startFlyout, StartButton, alignLeft: true); };
    }

    /// <summary>The Start menu at most 750 × 600, smaller on a small screen (the gaps and the taskbar left clear).</summary>
    private void FitStart()
    {
        var screen = TopLevel.GetTopLevel(this)?.Screens?.ScreenFromVisual(this);
        if (screen == null) return;
        double scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        _startMenu.FitTo(screen.Bounds.Width / scale - 2 * PanelGap, screen.Bounds.Height / scale - Bounds.Height - 2 * PanelGap);
    }

    /// <summary>Gap between the taskbar and the menus and panels that open from it, and from the screen's edge.</summary>
    public const double PanelGap = 8;

    /// <summary>
    /// Places a flyout opened from a taskbar button an even <see cref="PanelGap"/> above the taskbar itself (not the
    /// button, which sits inside it), and the same gap from the screen's left edge (Start menu) or right edge (panels).
    /// </summary>
    private void PlaceAbove(PopupFlyoutBase flyout, Control anchor, bool alignLeft)
    {
        if (anchor.TranslatePoint(new Point(0, 0), this) is not { } p) return;
        flyout.VerticalOffset = -(PanelGap + p.Y);
        flyout.HorizontalOffset = alignLeft
            ? PanelGap - p.X                                                    // left edges: gap from the screen's left
            : Bounds.Width - PanelGap - (p.X + anchor.Bounds.Width);            // right edges: gap from the screen's right
    }

    /// <summary>Win key / Ctrl+Esc.</summary>
    public void ToggleStart()
    {
        if (_startFlyout.IsOpen) _startFlyout.Hide();
        else _startFlyout.ShowAt(StartButton);
    }

    /// <summary>Super+V.</summary>
    public void ShowClipboard() => ShowPanel(SystemTrayButton, new ClipboardPanel());

    // ------------------------------------------------------------ volume (scroll wheel, volume keys)

    public const int VolumeStep = 5;
    private Flyout? _volumePopup;
    private ProgressBar? _volumeBar;
    private ContentControl? _volumeIcon;
    private TextBlock? _volumeText;
    private readonly DispatcherTimer _volumeHide = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private System.Threading.Tasks.Task _volumeQueue = System.Threading.Tasks.Task.CompletedTask;

    /// <summary>Volume up (positive) or down; 0 toggles mute. Shows the new level above the tray for a moment.</summary>
    public void StepVolume(int delta)
    {
        // one change at a time, in order: fast scrolling or a held key must not race pactl
        _volumeQueue = _volumeQueue.ContinueWith(async _ =>
        {
            var r = delta == 0 ? await SystemInfo.ToggleMuted() : await SystemInfo.ChangeVolume(delta);
            if (r is { } v) Dispatcher.UIThread.Post(() => ShowVolume(v.Volume, v.Muted));
        }).Unwrap();
    }

    private static string VolumeGlyphName(int volume, bool muted) =>
        muted || volume == 0 ? "volume-mute" : volume < 40 ? "volume-low" : volume < 75 ? "volume-mid" : "volume";

    private void ShowVolume(int volume, bool muted)
    {
        VolumeGlyph.IsVisible = true;
        VolumeGlyph.Content = Icons.Create(VolumeGlyphName(volume, muted), 17);
        if (_volumePopup == null)
        {
            _volumeIcon = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
            _volumeBar = new ProgressBar { Minimum = 0, Maximum = 100, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            _volumeText = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            // transient: shown without taking the keyboard from the window you're typing in
            _volumePopup = new Flyout
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 2), Children = { _volumeIcon, _volumeBar, _volumeText } },
                Placement = PlacementMode.TopEdgeAlignedRight,
                ShowMode = FlyoutShowMode.Transient,
            };
            _volumePopup.FlyoutPresenterClasses.Add("shell");
            _volumeHide.Tick += (_, _) => { _volumeHide.Stop(); _volumePopup?.Hide(); };
        }
        _volumeIcon!.Content = Icons.Create(VolumeGlyphName(volume, muted), 20);
        _volumeBar!.Value = muted ? 0 : Math.Min(100, volume);
        _volumeText!.Text = muted ? "Muted" : volume + "%";
        if (!_volumePopup.IsOpen)
        {
            PlaceAbove(_volumePopup, SystemTrayButton, alignLeft: false);
            _volumePopup.ShowAt(SystemTrayButton);
        }
        _volumeHide.Stop(); _volumeHide.Start();
        ToolTip.SetTip(SystemTrayButton, muted ? "Volume: muted" : $"Volume: {volume}%");
    }

    // ------------------------------------------------------------ panels

    private void ShowPanel(Control anchor, Control content)
    {
        var flyout = new Flyout { Content = content, Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.FlyoutPresenterClasses.Add("shell");
        if (content is IShellPanel panel) panel.CloseRequested += () => flyout.Hide();
        PlaceAbove(flyout, anchor, alignLeft: false);
        flyout.ShowAt(anchor);
    }

    // ------------------------------------------------------------ tasks (pinned + running)

    private bool _pointerDown, _tasksPending, _trayPending;
    private string _traySignature = "";
    private readonly DispatcherTimer _pressGuard = new() { Interval = TimeSpan.FromSeconds(3) };

    // after the release, let the Click run first, then catch up on rebuilds
    private void ReleaseSoon() => Dispatcher.UIThread.Post(FinishPress, DispatcherPriority.Background);

    private void FinishPress()
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _pressGuard.Stop();
        if (_tasksPending) { _tasksPending = false; RebuildTasks(); }
        if (_trayPending) { _trayPending = false; RebuildAppTray(); }
    }

    private void RebuildTasks()
    {
        if (_pointerDown) { _tasksPending = true; return; }

        bool child = SettingsStore.Current.AccountType == "Child";
        TaskViewButton.IsVisible = SettingsStore.Current.TaskViewButton;
        // the desktop in use's windows (Settings → Desktop & Taskbar can list every desktop's)
        bool all = SettingsStore.Current.TaskbarAllDesktops;
        var tasks = WindowManager.Tasks.Where(t => all || VirtualDesktops.OnCurrent(t)).ToList();
        // one button per app: a pinned app's windows join its button, and an app's windows share one, in the
        // order they opened; windows that don't say which app they are keep a button each
        var used = new HashSet<TaskItem>();
        var entries = new List<(string? PinId, List<TaskItem> Tasks)>();

        foreach (var id in SettingsStore.Current.PinnedApps.ToList())
        {
            var app = AppRegistry.Find(id);
            if (app == null)
            {
                // an installed Linux program pinned from the Start menu or from its taskbar button
                if (child || DesktopEntries.FromId(id) == null) continue;
            }
            else if (child && app.RestrictedForChild) continue;
            var running = tasks.Where(t => t.AppId == id && !used.Contains(t)).ToList();
            used.UnionWith(running);
            entries.Add((id, running));
        }
        foreach (var t in tasks.Where(t => !used.Contains(t)))
        {
            if (used.Contains(t)) continue;
            var group = t.AppId == null ? new List<TaskItem> { t } : tasks.Where(o => o.AppId == t.AppId && !used.Contains(o)).ToList();
            used.UnionWith(group);
            entries.Add((null, group));
        }

        double labels = SettingsStore.Current.TaskbarLabels
            ? LabelWidth(TaskScroll.Bounds.Width, entries.Count, entries.Count(e => e.Tasks.Count > 0)) : 0;

        // Buttons are kept and updated in place (keyed by pinned app or window): replacing a button under the
        // pointer would leave its tooltip open but empty, and lose a click in progress.
        var keep = new HashSet<string>();
        var ordered = new List<Control>();
        foreach (var (pin, group) in entries)
        {
            var key = pin != null ? "pin:" + pin
                : group[0].AppId is { } gid ? "app:" + gid
                : "task:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(group[0]);
            keep.Add(key);
            if (!_taskButtons.TryGetValue(key, out var btn)) _taskButtons[key] = btn = NewTaskButton();
            FillTaskButton(btn, pin, group, labels);
            ordered.Add(btn);
        }
        foreach (var gone in _taskButtons.Keys.Where(k => !keep.Contains(k)).ToList()) _taskButtons.Remove(gone);
        if (!TaskList.Children.SequenceEqual(ordered))
        {
            TaskList.Children.Clear();
            TaskList.Children.AddRange(ordered);
        }
    }

    private readonly Dictionary<string, Button> _taskButtons = new();

    /// <summary>What a task button currently stands for (read by its click handler).</summary>
    private sealed class TaskSlot
    {
        public string? PinId;
        public List<TaskItem> Tasks = new();
        public TaskItem? Task => Tasks.FirstOrDefault(t => t.IsActive) ?? Tasks.FirstOrDefault();
        public string Look = "";
    }

    /// <summary>
    /// A click on a button of several windows: the next one after the window in front, so clicking again walks
    /// through them; when none of them is in front, the first. Public for the tests.
    /// </summary>
    public static int NextInGroup(IReadOnlyList<bool> active)
    {
        int front = -1;
        for (int i = 0; i < active.Count; i++) if (active[i]) { front = i; break; }
        return front < 0 ? 0 : (front + 1) % active.Count;
    }

    private Button NewTaskButton()
    {
        var btn = new Button
        {
            Height = 44,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Tag = new TaskSlot()
        };
        btn.Classes.Add("task");
        TaskPreview.AttachGroup(btn, () => (btn.Tag as TaskSlot)?.Tasks ?? new List<TaskItem>());
        btn.Click += (_, _) => Press(btn);
        return btn;
    }

    /// <summary>What a click on a taskbar button does (and Super+1…9): open the app, bring its window forward or
    /// put it away, or with several windows walk through them.</summary>
    private static void Press(Button btn)
    {
        if (btn.Tag is not TaskSlot slot) return;
        if (slot.Tasks.Count > 1) slot.Tasks[NextInGroup(slot.Tasks.Select(t => t.IsActive).ToList())].Activate();
        else if (slot.Task != null) slot.Task.ToggleFromTaskbar();
        else if (slot.PinId is { } pinId)
        {
            if (AppRegistry.Find(pinId) != null) AppRegistry.Launch(pinId);
            else if (DesktopEntries.FromId(pinId) is { } la) DesktopEntries.Launch(la);
        }
    }

    /// <summary>Super+1…9: the n-th button on the taskbar, counted from the left.</summary>
    public void OpenNth(int n)
    {
        SuperHeld(false);
        var buttons = TaskList.Children.OfType<Button>().ToList();
        if (n >= 1 && n <= buttons.Count) Press(buttons[n - 1]);
    }

    private readonly DispatcherTimer _numbersAfter = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private const string NumberTag = "super-number";

    /// <summary>Super held down a moment: each of the first nine buttons shows the number that opens it; let go, they go.</summary>
    public void SuperHeld(bool down)
    {
        _numbersAfter.Stop();
        if (down) { _numbersAfter.Start(); return; }
        ShowNumbers(false);
    }

    private void ShowNumbers(bool on)
    {
        int n = 0;
        foreach (var btn in TaskList.Children.OfType<Button>())
        {
            if (btn.Content is not Grid g) continue;
            foreach (var old in g.Children.OfType<Control>().Where(c => c.Tag as string == NumberTag).ToList()) g.Children.Remove(old);
            if (!on || ++n > 9) continue;
            g.Children.Add(new Border
            {
                Tag = NumberTag, IsHitTestVisible = false,
                MinWidth = 16, Height = 16, CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0),
                Background = new SolidColorBrush(Color.Parse("#F2F4F4F6")),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -2),
                Child = new TextBlock
                {
                    Text = n.ToString(), FontSize = 10.5, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#16161A")),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    private void FillTaskButton(Button btn, string? pinId, List<TaskItem> tasks, double labels)
    {
        var slot = (TaskSlot)btn.Tag!;
        var task = tasks.FirstOrDefault(t => t.IsActive) ?? tasks.FirstOrDefault();
        int windows = tasks.Count;
        var appId = task?.AppId ?? pinId;
        var app = appId != null ? AppRegistry.Find(appId) : null;
        var linux = app == null && appId != null ? DesktopEntries.FromId(appId) : null;
        var title = task?.Title ?? app?.Name ?? linux?.Name ?? "App";
        var active = task?.IsActive == true;
        var showLabel = labels > 0 && task != null;
        var starting = false;

        bool sameTask = ReferenceEquals(slot.Task, task) && slot.PinId == pinId && slot.Tasks.SequenceEqual(tasks);
        slot.PinId = pinId; slot.Tasks = tasks;
        // a running window has its preview instead of a tooltip
        object? tip = task != null && SettingsStore.Current.TaskbarPreviews ? null : title;
        if (!Equals(ToolTip.GetTip(btn), tip)) ToolTip.SetTip(btn, tip);      // an open tooltip just updates its text
        btn.Classes.Set("active", active);

        var look = $"{appId}|{title}|{active}|{windows}|{(showLabel ? labels : 0)}|{starting}|{task != null}|{(task as X11Task)?.IconStamp}|{task?.IsMinimized}";
        if (look == slot.Look && sameTask) return;
        slot.Look = look;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(showLabel ? 10 : 9, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(TaskIcon(task, app, linux, appId, title, 26));
        if (showLabel)
        {
            row.Children.Add(new TextBlock
            {
                Text = starting ? title + "…" : title,
                MaxWidth = labels,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        // Running indicator: a dot for a running app, a longer bar in the accent for the focused one.
        var indicator = new Border
        {
            Width = active ? 20 : 6,
            Height = 3,
            CornerRadius = new CornerRadius(2),
            Background = active ? null : new SolidColorBrush(Color.Parse("#99FFFFFF")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
            IsVisible = task != null
        };

        if (active) indicator.Bind(Border.BackgroundProperty, indicator.GetResourceObservable("ShellAccentTextBrush"));
        var content = new Grid();
        content.Children.Add(row);
        content.Children.Add(indicator);
        // several windows of one app: how many, small, at the icon's corner
        if (windows > 1)
            content.Children.Add(new Border
            {
                MinWidth = 14, Height = 14, CornerRadius = new CornerRadius(7), Padding = new Thickness(3, 0),
                Background = new SolidColorBrush(Color.Parse("#E62A2A30")), BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(26, 3, 0, 0), IsHitTestVisible = false,
                Child = new TextBlock { Text = windows.ToString(), FontSize = 9, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        btn.Content = content;
        btn.ContextMenu = BuildTaskMenu(pinId, task, appId, tasks);
    }

    /// <summary>
    /// The button's icon: another program's own icon (from its window, or its icon theme when pinned), otherwise
    /// the Red Star tile.
    /// </summary>
    private static Control TaskIcon(TaskItem? task, AppInfo? app, LinuxApp? linux, string? appId, string title, double size)
    {
        Avalonia.Media.IImage? img = task?.Icon;
        if (img == null && task == null && linux != null && DesktopEntries.IconPath(linux.Icon) is { } path)
            try { img = new Avalonia.Media.Imaging.Bitmap(path); } catch { }
        if (img != null)
        {
            var image = new Image { Source = img, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            return image;
        }
        if (app == null && (linux != null || task is X11Task))
            return AppIcons.Tile(title.Length > 0 ? title[..1].ToUpperInvariant() : "?", size);
        return AppIcons.Create(appId, title, size);
    }

    private static ContextMenu BuildTaskMenu(string? pinId, TaskItem? task, string? appId, List<TaskItem> tasks)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            return mi;
        }

        if (task != null)
        {
            menu.Items.Add(Item("Restore", task.Restore));
            menu.Items.Add(Item("Minimize", task.Minimize));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Snap left", () => task.Snap(SnapMode.Left)));
            menu.Items.Add(Item("Snap right", () => task.Snap(SnapMode.Right)));
            menu.Items.Add(Item("Fill the screen", () => task.Snap(SnapMode.Full)));
            // virtual desktops: built when the menu opens (the desktops may have changed since)
            var deskSep = new Separator { Tag = "desks" };
            menu.Items.Add(deskSep);
            menu.Opening += (_, _) =>
            {
                foreach (var old in menu.Items.OfType<Control>().Where(c => c.Tag as string == "desk-item").ToList()) menu.Items.Remove(old);
                int at = menu.Items.IndexOf(deskSep) + 1;
                foreach (var item in TaskViewWindow.MoveItems(task, () => { }))
                {
                    item.Tag = "desk-item";
                    menu.Items.Insert(at++, item);
                }
            };
        }
        else if (pinId != null)
        {
            menu.Items.Add(Item("Open", () =>
            {
                if (AppRegistry.Find(pinId) != null) AppRegistry.Launch(pinId);
                else if (DesktopEntries.FromId(pinId) is { } la) DesktopEntries.Launch(la);
            }));
        }
        // the app's recent files, as Windows' jump lists: read when the menu opens
        if (appId != null)
            menu.Opening += (_, _) =>
            {
                foreach (var old in menu.Items.OfType<Control>().Where(c => c.Tag as string == "recent").ToList()) menu.Items.Remove(old);
                var recent = JumpList.Recent(appId);
                if (recent.Count == 0) return;
                int at = 0;
                menu.Items.Insert(at++, new MenuItem { Header = "Recent", IsEnabled = false, FontSize = 11.5, Tag = "recent" });
                foreach (var path in recent)
                {
                    var mi = new MenuItem { Header = Path.GetFileName(path), Icon = Icons.Glyph(FileOps.IconName(path, false), 16), Tag = "recent" };
                    ToolTip.SetTip(mi, path);
                    mi.Click += (_, _) => FileOpener.Open(path);
                    menu.Items.Insert(at++, mi);
                }
                menu.Items.Insert(at, new Separator { Tag = "recent" });
            };
        if (task is X11Task { App: { } la2 })
        {
            menu.Items.Insert(0, Item("New window", () => DesktopEntries.Launch(la2)));
            menu.Items.Insert(1, new Separator());
        }

        if (appId != null && WindowManager.CanPin(appId))
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(Item(WindowManager.IsPinned(appId) ? "Unpin from taskbar" : "Pin to taskbar",
                () => WindowManager.TogglePin(appId)));
        }

        if (task != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(tasks.Count > 1 ? "Close this window" : "Close", task.Close));
            if (tasks.Count > 1)
                menu.Items.Add(Item($"Close all {tasks.Count} windows", () => { foreach (var t in tasks.ToList()) t.Close(); }));
        }
        return menu;
    }

    // ------------------------------------------------------------ app tray

    private void UpdateBell()
    {
        _bell.Badge = Notifier.Unread;
        _bell.Glyph = Notifier.DoNotDisturb ? "bell-off" : "bell";
        _bell.Tooltip = Notifier.DoNotDisturb
            ? "Notifications (Do not disturb)"
            : Notifier.Unread > 0 ? $"Notifications ({Notifier.Unread} new)" : "Notifications";
        BellHost.Children.Clear();
        BellHost.Children.Add(MakeTrayButton(_bell, onTaskbar: true));
    }

    private void RebuildAppTray()
    {
        if (_pointerDown) { _trayPending = true; return; }
        var entries = TrayService.Entries.ToList();
        // apps re-announce their tray icons often; rebuilding unchanged icons would empty an open tooltip
        var sig = string.Join(";", entries.Select(e =>
            $"{e.Key}|{e.Tooltip}|{e.Glyph}|{(e.Icon == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e.Icon))}|{e.Badge}|{e.Menu.Count}|{e.PanelFactory != null}|{e.MenuProvider != null}|{e.MenuOnClick}"));
        if (sig == _traySignature && AppTray.Children.Count > 0) return;
        _traySignature = sig;
        AppTray.Children.Clear();
        var inline = entries.Take(MaxInlineTrayIcons).ToList();
        var overflow = entries.Skip(MaxInlineTrayIcons).ToList();

        if (overflow.Count > 0) AppTray.Children.Add(MakeOverflowButton(overflow));
        foreach (var e in inline) AppTray.Children.Add(MakeTrayButton(e, onTaskbar: true));
    }

    private Control MakeOverflowButton(List<TrayEntry> hidden)
    {
        // a drawn chevron, not the "⌃" character: a font puts that high in its line, so it never looked centred
        var arrow = Icons.Create("chevron-up", 16);
        arrow.HorizontalAlignment = HorizontalAlignment.Center;
        arrow.VerticalAlignment = VerticalAlignment.Center;
        var btn = new Button
        {
            Content = arrow, Width = 30, Height = 36, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        btn.Classes.Add("task");
        ToolTip.SetTip(btn, "Show hidden icons");
        btn.Click += (_, _) =>
        {
            var wrap = new WrapPanel { Margin = new Thickness(8), MaxWidth = 220 };
            foreach (var e in hidden) wrap.Children.Add(MakeTrayButton(e, onTaskbar: false));
            ShowPanel(btn, wrap);
        };
        return btn;
    }

    private Button MakeTrayButton(TrayEntry entry, bool onTaskbar)
    {
        Control face = entry.Icon != null
            ? new Image { Source = entry.Icon, Width = 18, Height = 18, Stretch = Stretch.Uniform }
            : Icons.Glyph(entry.Glyph ?? "●", 17);

        var holder = new Grid { Width = 22, Height = 22 };
        holder.Children.Add(face);
        if (entry.Badge > 0)
        {
            // how many: a small red count at the icon's corner (9+ past nine)
            holder.Children.Add(new Border
            {
                MinWidth = 14,
                Height = 14,
                Padding = new Thickness(3, 0),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.Parse("#E81123")),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -7, 0),
                Child = new TextBlock
                {
                    Text = entry.Badge > 9 ? "9+" : entry.Badge.ToString(), FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var btn = new Button { Content = holder, Width = 36, Height = 36, Padding = new Thickness(0) };
        btn.Classes.Add(onTaskbar ? "task" : "flat");
        ToolTip.SetTip(btn, entry.Tooltip);

        if (entry.PanelFactory == null && entry.MenuProvider == null && entry.Menu.Count > 0) btn.ContextFlyout = BuildTrayMenu(entry, entry.Menu);
        if (entry.MenuProvider != null || entry.ContextClick != null)
            btn.ContextRequested += (_, e) =>
            {
                e.Handled = true;
                if (entry.MenuProvider != null) ShowProvidedMenu(btn, entry);
                else entry.ContextClick?.Invoke();
            };
        if (entry.MiddleClick != null)
            btn.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Middle) { e.Handled = true; entry.MiddleClick?.Invoke(); } };
        if (entry.Scroll != null)
            btn.PointerWheelChanged += (_, e) => { if (e.Delta.Y != 0) { e.Handled = true; entry.Scroll?.Invoke(e.Delta.Y > 0 ? 1 : -1); } };
        btn.Click += (_, _) =>
        {
            if (entry.PanelFactory != null)
            {
                if (entry.Key == _bell.Key) Notifier.MarkAllRead();
                ShowPanel(btn, entry.PanelFactory());
            }
            else if (entry.MenuOnClick && entry.MenuProvider != null) ShowProvidedMenu(btn, entry);
            else if (entry.MenuProvider == null && entry.Menu.Count > 0) BuildTrayMenu(entry, entry.Menu).ShowAt(btn);
            else entry.Click?.Invoke();
        };
        return btn;
    }

    /// <summary>Asks the program for its menu as it is now, then shows it above the icon.</summary>
    private async void ShowProvidedMenu(Button btn, TrayEntry entry)
    {
        var items = entry.MenuProvider == null ? null : await entry.MenuProvider();
        if (items == null || items.Count == 0) { entry.ContextClick?.Invoke(); return; }
        var flyout = BuildTrayMenu(entry, items);
        PlaceAbove(flyout, btn, alignLeft: false);
        flyout.ShowAt(btn);
    }

    private static MenuFlyout BuildTrayMenu(TrayEntry entry, List<TrayMenuEntry> entries)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedRight };
        foreach (var m in entries) flyout.Items.Add(BuildTrayMenuItem(entry, m));
        return flyout;
    }

    private static Control BuildTrayMenuItem(TrayEntry entry, TrayMenuEntry m)
    {
        if (m.Separator) return new Separator();
        var id = m.Id;
        var item = new MenuItem { Header = m.Label, IsEnabled = m.Enabled };
        if (m.Checked is { } on)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = on;
        }
        if (m.Children is { Count: > 0 } kids)
            foreach (var k in kids) item.Items.Add(BuildTrayMenuItem(entry, k));
        else item.Click += (_, _) => entry.MenuSelected?.Invoke(id);
        return item;
    }

    // ------------------------------------------------------------ system tray + clock

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var text = now.ToString(SettingsStore.Current.Clock24h ? "HH:mm" : "h:mm tt", CultureInfo.CurrentCulture);
        if (text == _lastClock) return;
        _lastClock = text;
        ClockLabel.Text = text;
        DateLabel.Text = now.ToString("ddd d MMM", CultureInfo.CurrentCulture);

        var next = CalendarStore.Next(now);
        ToolTip.SetTip(ClockButton, next == null
            ? now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture)
            : now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture) + "\nNext: " +
              (next.When.Date == DateTime.Today ? "" : next.When.ToString("ddd d MMM ", CultureInfo.CurrentCulture)) + next.ToString().Trim());
    }

    private async void RefreshStatus()
    {
        var kind = SystemInfo.ConnectionKind();
        NetGlyph.Content = Icons.Create(kind switch { "wifi" => "wifi", "wired" => "wired", _ => "offline" }, 17);

        var battery = SystemInfo.BatteryInfo();
        BatteryRow.IsVisible = battery != null;
        BatteryText.Text = battery == null ? "" : battery.Percent + "%";
        if (battery != null) BatteryGlyph.Content = Icons.Create(battery.Charging ? "bolt" : "battery", 16);

        var volume = await SystemInfo.GetVolume();
        var muted = await SystemInfo.GetMuted();
        VolumeGlyph.IsVisible = volume != null;
        VolumeGlyph.Content = Icons.Create(VolumeGlyphName(volume ?? 0, muted == true), 17);

        var parts = new List<string> { kind == "offline" ? "Offline" : "Network: " + SystemInfo.NetworkSummary() };
        if (volume != null) parts.Add(muted == true ? "Volume: muted" : $"Volume: {volume}%");
        if (battery != null) parts.Add($"Battery: {battery.Percent}%" + (battery.Charging ? " (charging)" : ""));
        ToolTip.SetTip(SystemTrayButton, string.Join("\n", parts));
    }
}
