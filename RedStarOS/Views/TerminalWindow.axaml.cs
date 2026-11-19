using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Red Star Terminal: tabs, each holding one or more split panes (<see cref="TerminalPane"/>).</summary>
public partial class TerminalWindow : Window
{
    public static TerminalWindow? Instance { get; private set; }

    /// <summary>Split layout of a tab: a leaf holds a pane, an inner node two children side by side or stacked.</summary>
    private sealed class Node
    {
        public Node? Parent;
        public TerminalPane? Pane;
        public bool SideBySide;
        public Node? A, B;
        public double Ratio = 0.5;
        public Grid? Grid;
    }

    private sealed class Tab
    {
        public Node Root = null!;
        public TerminalPane Active = null!;
        public TerminalPane? Zoomed;                       // one pane maximised inside the tab
        public readonly Border Host = new();
        public IEnumerable<TerminalPane> Panes => Leaves(Root);
        public bool Bell => Panes.Any(p => p.Bell);
        public string Title => Active.Title;
    }

    private static IEnumerable<TerminalPane> Leaves(Node n)
    {
        if (n.Pane != null) { yield return n.Pane; yield break; }
        if (n.A != null) foreach (var p in Leaves(n.A)) yield return p;
        if (n.B != null) foreach (var p in Leaves(n.B)) yield return p;
    }

    private static Node? FindNode(Node n, TerminalPane pane)
    {
        if (n.Pane == pane) return n;
        return (n.A != null ? FindNode(n.A, pane) : null) ?? (n.B != null ? FindNode(n.B, pane) : null);
    }

    private readonly List<Tab> _tabs = new();
    private Tab? _cur;
    private bool _forceClose, _fullscreen;
    private WindowState _prevState;
    private readonly DispatcherTimer _titleTimer;
    private static double Zoom => TerminalProfiles.Settings.Zoom;

    public TerminalWindow()
    {
        InitializeComponent();
        Instance = this;

        AddHandler(KeyDownEvent, OnWindowKey, RoutingStrategies.Tunnel);
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DoFind(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
        FindBox.TextChanged += (_, _) => { ActiveView?.ClearSelection(); if (!string.IsNullOrEmpty(FindBox.Text)) DoFind(true); else FindInfo.Text = ""; };

        ThemeService.Changed += OnTheme;
        TerminalProfiles.Changed += OnProfilesChanged;
        // tabs without a title from the program show the folder the shell is in
        _titleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _titleTimer.Tick += (_, _) => RefreshTitles();
        _titleTimer.Start();

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _titleTimer.Stop();
            ThemeService.Changed -= OnTheme;
            TerminalProfiles.Changed -= OnProfilesChanged;
            foreach (var t in _tabs) foreach (var p in t.Panes) p.View.Close();
            _tabs.Clear();
            if (ReferenceEquals(Instance, this)) Instance = null;
        };
    }

    private TerminalView? ActiveView => _cur?.Active.View;
    private IEnumerable<TerminalPane> AllPanes => _tabs.SelectMany(t => t.Panes);

    private void OnTheme() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var p in AllPanes) { p.View.ApplyTheme(ThemeService.IsDark, SettingsStore.Current.Accent); p.ApplyBackground(); }
        UpdateFrames();
    });

    private void OnProfilesChanged() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var p in AllPanes)
        {
            // edited profiles apply to their open terminals right away; terminals of a deleted profile keep their look
            var prof = TerminalProfiles.Settings.Profiles.FirstOrDefault(x => ReferenceEquals(x, p.View.Profile));
            p.View.ApplyProfile(prof ?? p.View.Profile, Zoom);
            p.ApplyBackground();
        }
    });

    public void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }

    // ═════════════════════════════════ panes ═════════════════════════════════
    private static string StartDir(TerminalProfile p, string? current) => p.StartIn switch
    {
        "Home" => AppPaths.Home,
        "Custom" when Directory.Exists(p.CustomDir) => p.CustomDir,
        _ => current ?? AppPaths.Home
    };

    private static IList<string>? ProfileCommand(TerminalProfile p) =>
        string.IsNullOrWhiteSpace(p.Command) ? null : new List<string> { "/bin/sh", "-c", "exec " + p.Command };

    private TerminalPane CreatePane(Tab tab, TerminalProfile profile, string? cwd, IList<string>? command)
    {
        var pane = new TerminalPane(profile, Zoom) { KeepOpenOnExit = command is { Count: > 0 } && command[0] != "/bin/sh" };
        if (pane.KeepOpenOnExit) pane.Title = Path.GetFileName(command![0]);
        var view = pane.View;
        view.TitleChanged += () =>
        {
            if (!string.IsNullOrWhiteSpace(view.Emu.Title)) pane.Title = view.Emu.Title.Trim();
            RebuildTabs(); UpdateWindowTitle();
        };
        view.BellRung += () => { if (!ReferenceEquals(tab, _cur) || !IsActive || !ReferenceEquals(tab.Active, pane)) { pane.Bell = true; RebuildTabs(); } };
        view.CommandRequested += cmd => { SetActive(tab, pane, focus: false); Exec(cmd); };
        view.FocusEntered += () => { if (!ReferenceEquals(tab.Active, pane)) SetActive(tab, pane, focus: false); };
        view.Exited += code =>
        {
            if (pane.KeepOpenOnExit)
            {
                view.Emu.Feed($"\r\n\u001b[2m[Process finished with exit code {code} — press Ctrl+Shift+W to close]\u001b[0m\r\n");
                view.InvalidateVisual();
                pane.Title += " (finished)";
                RebuildTabs();
            }
            else ClosePane(tab, pane, ask: false);      // the shell ended (exit, Ctrl+D)
        };
        // start once the pane has its size, so the shell begins with the right number of columns
        Dispatcher.UIThread.Post(() => view.Start(cwd, command), DispatcherPriority.Background);
        return pane;
    }

    /// <summary>Opens a tab with a profile's shell (or <paramref name="command"/>) in <paramref name="cwd"/>.</summary>
    public void NewTab(string? cwd = null, IList<string>? command = null, TerminalProfile? profile = null)
    {
        profile ??= TerminalProfiles.DefaultProfile;
        cwd ??= StartDir(profile, ActiveView?.CurrentDirectory());
        command ??= ProfileCommand(profile);
        var tab = new Tab();
        var pane = CreatePane(tab, profile, cwd, command);
        tab.Root = new Node { Pane = pane };
        tab.Active = pane;
        _tabs.Add(tab);
        Stage.Children.Add(tab.Host);
        Attach(tab);
        SwitchTo(tab);
    }

    /// <summary>Splits the active pane: the new pane opens beside it (right) or below it, in the same folder.</summary>
    private void Split(bool sideBySide)
    {
        if (_cur == null) return;
        var tab = _cur;
        var old = tab.Active;
        var prof = old.View.Profile;
        Detach(tab);
        tab.Zoomed = null;
        var node = FindNode(tab.Root, old)!;
        var pane = CreatePane(tab, prof, StartDir(prof, old.View.CurrentDirectory()), ProfileCommand(prof));
        node.Pane = null;
        node.SideBySide = sideBySide;
        node.Ratio = 0.5;
        node.A = new Node { Pane = old, Parent = node };
        node.B = new Node { Pane = pane, Parent = node };
        Attach(tab);
        SetActive(tab, pane, focus: true);
    }

    private async void ClosePane(Tab tab, TerminalPane pane, bool ask)
    {
        if (!_tabs.Contains(tab)) return;
        var node = FindNode(tab.Root, pane);
        if (node == null) return;
        if (ask && pane.View.Pty?.HasRunningJob() == true)
        {
            var name = pane.View.Pty.ForegroundName() ?? "A program";
            var r = await Dialogs.Ask(this, "Close?", $"{name} is still running here. Closing will stop it.", "Close", "Cancel");
            if (r != 0) return;
            node = FindNode(tab.Root, pane);
            if (node == null) return;
        }
        if (node.Parent == null) { CloseTabNow(tab); return; }

        Detach(tab);
        pane.View.Close();
        var parent = node.Parent;
        var sib = ReferenceEquals(parent.A, node) ? parent.B! : parent.A!;
        parent.Pane = sib.Pane; parent.SideBySide = sib.SideBySide; parent.Ratio = sib.Ratio;
        parent.A = sib.A; parent.B = sib.B;
        if (parent.A != null) parent.A.Parent = parent;
        if (parent.B != null) parent.B.Parent = parent;
        if (ReferenceEquals(tab.Zoomed, pane)) tab.Zoomed = null;
        Attach(tab);
        var next = Leaves(parent).First();
        SetActive(tab, next, focus: ReferenceEquals(tab, _cur));
    }

    /// <summary>Takes the panes out of the tab's grids (remembering where the splitters were).</summary>
    private static void Detach(Tab tab)
    {
        void Walk(Node n)
        {
            if (n.Grid != null)
            {
                var defs = n.SideBySide ? n.Grid.ColumnDefinitions.Select(d => d.Width).ToList() : n.Grid.RowDefinitions.Select(d => d.Height).ToList();
                if (defs.Count == 3 && defs[0].IsStar && defs[2].IsStar && defs[0].Value + defs[2].Value > 0)
                    n.Ratio = defs[0].Value / (defs[0].Value + defs[2].Value);
                n.Grid.Children.Clear();
                n.Grid = null;
            }
            if (n.A != null) Walk(n.A);
            if (n.B != null) Walk(n.B);
        }
        Walk(tab.Root);
        tab.Host.Child = null;
    }

    private void Attach(Tab tab)
    {
        Control Build(Node n)
        {
            if (n.Pane != null) return n.Pane;
            var g = new Grid();
            var first = new GridLength(n.Ratio, GridUnitType.Star);
            var second = new GridLength(1 - n.Ratio, GridUnitType.Star);
            var splitter = new GridSplitter
            {
                Background = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
                ResizeDirection = n.SideBySide ? GridResizeDirection.Columns : GridResizeDirection.Rows
            };
            var a = Build(n.A!);
            var b = Build(n.B!);
            if (n.SideBySide)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition(first));
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4)));
                g.ColumnDefinitions.Add(new ColumnDefinition(second));
                Grid.SetRow(a, 0); Grid.SetRow(splitter, 0); Grid.SetRow(b, 0);
                Grid.SetColumn(a, 0); Grid.SetColumn(splitter, 1); Grid.SetColumn(b, 2);
            }
            else
            {
                g.RowDefinitions.Add(new RowDefinition(first));
                g.RowDefinitions.Add(new RowDefinition(new GridLength(4)));
                g.RowDefinitions.Add(new RowDefinition(second));
                Grid.SetColumn(a, 0); Grid.SetColumn(splitter, 0); Grid.SetColumn(b, 0);
                Grid.SetRow(a, 0); Grid.SetRow(splitter, 1); Grid.SetRow(b, 2);
            }
            g.Children.Add(a); g.Children.Add(splitter); g.Children.Add(b);
            n.Grid = g;
            return g;
        }
        if (tab.Zoomed != null)
        {
            Grid.SetRow(tab.Zoomed, 0); Grid.SetColumn(tab.Zoomed, 0);
            tab.Host.Child = tab.Zoomed;
        }
        else tab.Host.Child = Build(tab.Root);
        UpdateFrames();
    }

    private void ToggleZoomPane()
    {
        if (_cur == null) return;
        var tab = _cur;
        if (tab.Root.Pane != null) return;       // nothing to maximise
        Detach(tab);
        tab.Zoomed = tab.Zoomed == null ? tab.Active : null;
        Attach(tab);
        Dispatcher.UIThread.Post(() => tab.Active.View.Focus());
    }

    private void SetActive(Tab tab, TerminalPane pane, bool focus)
    {
        tab.Active = pane;
        pane.Bell = false;
        UpdateFrames();
        if (ReferenceEquals(tab, _cur)) { RebuildTabs(); UpdateWindowTitle(); }
        if (focus) Dispatcher.UIThread.Post(() => pane.View.Focus());
    }

    private void UpdateFrames()
    {
        foreach (var t in _tabs)
        {
            bool split = t.Root.Pane == null && t.Zoomed == null;
            foreach (var p in t.Panes) p.SetActive(ReferenceEquals(p, t.Active), split);
        }
    }

    /// <summary>Moves to the pane next to the active one (Alt+Shift+arrows).</summary>
    private void FocusDirection(int dx, int dy)
    {
        if (_cur == null || _cur.Zoomed != null) return;
        var tab = _cur;
        Rect R(TerminalPane p) => p.TranslatePoint(new Point(0, 0), tab.Host) is { } o ? new Rect(o, p.Bounds.Size) : default;
        var cur = R(tab.Active);
        TerminalPane? best = null; double bestD = double.MaxValue;
        foreach (var p in tab.Panes)
        {
            if (ReferenceEquals(p, tab.Active)) continue;
            var r = R(p);
            bool ok = dx > 0 ? r.Left >= cur.Right - 2 : dx < 0 ? r.Right <= cur.Left + 2 : dy > 0 ? r.Top >= cur.Bottom - 2 : r.Bottom <= cur.Top + 2;
            if (!ok) continue;
            var d = Math.Abs(r.Center.X - cur.Center.X) * (dx != 0 ? 1 : 3) + Math.Abs(r.Center.Y - cur.Center.Y) * (dy != 0 ? 1 : 3);
            if (d < bestD) { bestD = d; best = p; }
        }
        if (best != null) SetActive(tab, best, focus: true);
    }

    // ═════════════════════════════════ tabs ═════════════════════════════════
    private void SwitchTo(Tab tab)
    {
        _cur = tab;
        foreach (var t in _tabs) t.Host.IsVisible = ReferenceEquals(t, tab);
        RebuildTabs();
        UpdateWindowTitle();
        Dispatcher.UIThread.Post(() => tab.Active.View.Focus());
    }

    private async void CloseTab(Tab tab)
    {
        var busy = tab.Panes.Where(p => p.View.Pty?.HasRunningJob() == true).ToList();
        if (busy.Count > 0)
        {
            var names = string.Join(", ", busy.Select(p => p.View.Pty?.ForegroundName() ?? "a program").Distinct());
            var r = await Dialogs.Ask(this, "Close tab?", $"{names} is still running in this tab. Closing the tab will stop it.", "Close", "Cancel");
            if (r != 0) return;
        }
        CloseTabNow(tab);
    }

    private void CloseTabNow(Tab tab)
    {
        int i = _tabs.IndexOf(tab);
        if (i < 0) return;
        _tabs.RemoveAt(i);
        Stage.Children.Remove(tab.Host);
        foreach (var p in tab.Panes) p.View.Close();
        if (_tabs.Count == 0) { _forceClose = true; Close(); return; }
        if (ReferenceEquals(tab, _cur)) SwitchTo(_tabs[Math.Min(i, _tabs.Count - 1)]);
        else RebuildTabs();
    }

    private void RebuildTabs()
    {
        ShellTabs.Build(TabsPanel, _tabs.Select(t =>
        {
            var tab = t;
            int panes = tab.Panes.Count();
            return new TabSpec
            {
                Title = tab.Title,
                IconName = panes > 1 ? "split" : "terminal",
                Active = ReferenceEquals(tab, _cur),
                Attention = tab.Bell,
                Badge = panes > 1 ? panes.ToString() : null,
                Tooltip = string.Join("\n", tab.Panes.Select(p => p.Title)),
                Select = () => SwitchTo(tab),
                Close = () => CloseTab(tab),
            };
        }));
    }

    private void RefreshTitles()
    {
        bool changed = false;
        foreach (var pane in AllPanes)
        {
            if (pane.KeepOpenOnExit || !string.IsNullOrWhiteSpace(pane.View.Emu.Title)) continue;
            var dir = pane.View.CurrentDirectory();
            string title = dir == null ? "Terminal" : dir == AppPaths.Home ? "~" : Path.GetFileName(dir.TrimEnd('/')) is { Length: > 0 } n ? n : dir;
            var job = pane.View.Pty?.HasRunningJob() == true ? pane.View.Pty.ForegroundName() : null;
            if (job != null) title = job + " — " + title;
            if (title != pane.Title) { pane.Title = title; changed = true; }
        }
        if (changed) { RebuildTabs(); UpdateWindowTitle(); }
    }

    private void UpdateWindowTitle() => Title = (_cur?.Title ?? "Terminal") + " — Red Star Terminal";

    private void CycleTab(int d)
    {
        if (_tabs.Count < 2 || _cur == null) return;
        int i = _tabs.IndexOf(_cur);
        SwitchTo(_tabs[(i + d + _tabs.Count) % _tabs.Count]);
    }

    // ═════════════════════════════════ commands ═════════════════════════════════
    private void OnCmd(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag) Exec(tag);
    }

    private void OnProfileMenu(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var p in TerminalProfiles.Settings.Profiles)
        {
            var prof = p;
            var mi = new MenuItem { Header = "New tab: " + prof.Name + (prof.Name == TerminalProfiles.Settings.Default ? "  (default)" : "") };
            mi.Click += (_, _) => NewTab(profile: prof);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Profiles and colours…" };
        edit.Click += (_, _) => Exec("profiles");
        menu.Items.Add(edit);
        menu.ShowAt(ProfileBtn);
    }

    private void Exec(string tag)
    {
        var v = ActiveView;
        switch (tag)
        {
            case "newtab": NewTab(); break;
            case "closetab": if (_cur != null) CloseTab(_cur); break;
            case "closepane": if (_cur != null) ClosePane(_cur, _cur.Active, ask: true); break;
            case "splitright": Split(true); return;
            case "splitdown": Split(false); return;
            case "zoompane": ToggleZoomPane(); return;
            case "copy": v?.CopySelection(); break;
            case "paste": v?.PasteClipboard(); break;
            case "selectall": v?.SelectAll(); break;
            case "find": ShowFind(); return;
            case "findnext": DoFind(false); return;
            case "findprev": DoFind(true); return;
            case "findclose": CloseFind(); break;
            case "zoomin": SetZoom(Zoom + 1); break;
            case "zoomout": SetZoom(Zoom - 1); break;
            case "zoomreset": SetZoom(0); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "clear": if (v != null) { v.Emu.ClearScrollback(); v.ScrollToBottom(); v.InvalidateVisual(); _cur?.Active.SyncScrollbar(); } break;
            case "reset": if (v != null) { v.Emu.FullReset(); v.Pty?.Write("\u000c"); v.InvalidateVisual(); } break;
            case "files":
            {
                var dir = v?.CurrentDirectory() ?? AppPaths.Home;
                WindowManager.Open(new FileManagerWindow(dir));
                break;
            }
            case "profiles": new TerminalProfilesWindow().Show(this); return;
            case "help": _ = Dialogs.ShowMessage(this, "Keyboard shortcuts", ShortcutHelp); break;
        }
        v?.Focus();
    }

    private const string ShortcutHelp =
        "Ctrl+Shift+T  New tab (▾ next to ＋: pick a profile)\nCtrl+PageUp / PageDown  Previous / next tab\nAlt+1 … 9  Go to tab\n" +
        "Ctrl+Shift+E  Split right\nCtrl+Shift+O  Split down\nAlt+Shift+arrows  Move between panes\nCtrl+Shift+Z  Maximise / restore pane\nCtrl+Shift+W  Close pane (closes the tab when it is the last one)\n" +
        "Ctrl+Shift+C / Ctrl+Insert  Copy\nCtrl+Shift+V / Shift+Insert  Paste\nCtrl+Shift+A  Select all\nCtrl+Shift+F  Find\n" +
        "Ctrl+,  Profiles and colours\nF1  This list\nCtrl+Shift+K  Clear scrollback\nShift+PageUp / PageDown  Scroll\nShift+Home / End  Top / bottom\nCtrl+= / Ctrl+- / Ctrl+0  Zoom\nF11  Full screen\n\n" +
        "Mouse: double-click selects a word, triple-click a line; Ctrl+click opens a link; middle-click pastes the selection; " +
        "hold Shift to select text in programs that use the mouse; drag the line between panes to resize them.";

    private void SetZoom(double z)
    {
        TerminalProfiles.Settings.Zoom = Math.Clamp(z, -10, 20);
        TerminalProfiles.Save();             // OnProfilesChanged re-applies the size to every pane
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen) { _prevState = WindowState; _fullscreen = true; WindowState = WindowState.FullScreen; TabBar.IsVisible = false; }
        else { _fullscreen = false; WindowState = _prevState == WindowState.Minimized ? WindowState.Normal : _prevState; TabBar.IsVisible = true; }
    }

    private void ShowFind()
    {
        FindBar.IsVisible = true;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        FindBar.IsVisible = false;
        FindInfo.Text = "";
        ActiveView?.Focus();
    }

    private void DoFind(bool backwards)
    {
        if (ActiveView == null) return;
        bool ok = ActiveView.Find(FindBox.Text ?? "", backwards, CaseChk.IsChecked == true);
        FindInfo.Text = string.IsNullOrEmpty(FindBox.Text) ? "" : ok ? "" : "No matches";
        _cur?.Active.SyncScrollbar();
    }

    private void OnWindowKey(object? sender, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        bool ctrl = m.HasFlag(KeyModifiers.Control), shift = m.HasFlag(KeyModifiers.Shift), alt = m.HasFlag(KeyModifiers.Alt);
        bool handled = true;
        if (e.Key == Key.F11) ToggleFullscreen();
        else if (e.Key == Key.F1 && !ctrl && !shift && !alt) Exec("help");
        else if (ctrl && !shift && !alt && e.Key == Key.OemComma) Exec("profiles");
        else if (ctrl && !shift && !alt && e.Key == Key.PageDown) CycleTab(1);
        else if (ctrl && !shift && !alt && e.Key == Key.PageUp) CycleTab(-1);
        else if (ctrl && !alt && (e.Key == Key.OemPlus || e.Key == Key.Add)) SetZoom(Zoom + 1);
        else if (ctrl && !alt && !shift && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) SetZoom(Zoom - 1);
        else if (ctrl && !alt && !shift && (e.Key == Key.D0 || e.Key == Key.NumPad0)) SetZoom(0);
        else if (alt && shift && !ctrl && e.Key == Key.Left) FocusDirection(-1, 0);
        else if (alt && shift && !ctrl && e.Key == Key.Right) FocusDirection(1, 0);
        else if (alt && shift && !ctrl && e.Key == Key.Up) FocusDirection(0, -1);
        else if (alt && shift && !ctrl && e.Key == Key.Down) FocusDirection(0, 1);
        else if (alt && !ctrl && !shift && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            int n = e.Key - Key.D1;
            if (n < _tabs.Count) SwitchTo(_tabs[n]); else handled = false;
        }
        else if (FindBar.IsVisible && e.Key == Key.Escape && FindBox.IsFocused) CloseFind();
        else handled = false;
        if (handled) e.Handled = true;
    }

    // ═════════════════════════════════ closing ═════════════════════════════════
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose) return;
        var busy = AllPanes.Where(p => p.View.Pty?.HasRunningJob() == true).ToList();
        if (busy.Count == 0) return;
        e.Cancel = true;
        var names = string.Join(", ", busy.Select(p => p.View.Pty?.ForegroundName() ?? "a program").Distinct());
        var r = await Dialogs.Ask(this, "Close Terminal?",
            busy.Count == 1 ? $"{names} is still running. Closing the window will stop it." : $"Programs are still running in {busy.Count} terminals ({names}). Closing the window will stop them.",
            "Close", "Cancel");
        if (r == 0) { _forceClose = true; Close(); }
    }
}
