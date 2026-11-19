using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Files: breadcrumb path, search, list and grid views with picture thumbnails, a sidebar with places, pinned
/// folders, drives and the Trash (with restore), Properties, and the usual clipboard / rename / trash actions.
/// The open folder is watched, so changes made elsewhere show up by themselves.
/// </summary>
public partial class FileManagerWindow : Window
{
    // ───────────────────────── items ─────────────────────────
    public sealed class FileEntry : INotifyPropertyChanged
    {
        public string Name { get; init; } = "";
        public string FullPath { get; init; } = "";
        public bool IsDir { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public string Kind { get; init; } = "";
        public string IconKey { get; init; } = "file";        // "icon|#colour" for the icon converter
        public bool Hidden => Name.StartsWith('.');
        public double NameOpacity => Hidden ? 0.6 : 1.0;
        public string SizeText => IsDir ? "" : FileOps.FormatSize(Size);
        public string DateText => FriendlyDate(Modified);
        public string TileName => Name;

        private bool _starred;
        public bool Starred { get => _starred; set { _starred = value; Changed(nameof(Starred)); } }

        private IImage? _thumb;
        public IImage? Thumb { get => _thumb; set { _thumb = value; Changed(nameof(Thumb)); Changed(nameof(HasThumb)); } }
        public bool HasThumb => _thumb != null;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private enum SortKey { Name, Kind, Size, Date }

    // each tab has its own folder and history
    private sealed class TabState
    {
        public string Current = "";
        public readonly Stack<string> Back = new();
        public readonly Stack<string> Forward = new();
    }
    private readonly List<TabState> _tabs = new() { new TabState() };
    private int _activeTab;
    private TabState Tab => _tabs[_activeTab];
    private Stack<string> _back => Tab.Back;
    private Stack<string> _forward => Tab.Forward;
    private string _current { get => Tab.Current; set => Tab.Current = value; }
    private SortKey _sort = SortKey.Name;
    private bool _ascending = true;
    private List<FileEntry> _all = new();                  // everything in the folder (before the search filter)
    private CancellationTokenSource? _searchCts, _thumbCts;
    private bool _deepResults;                              // showing results from subfolders
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _reloadSoon = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _filterSoon = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private string _typeAhead = ""; private DateTime _typeAheadAt;

    private static readonly Dictionary<string, IImage> ThumbCache = new();
    private static readonly SemaphoreSlim ThumbGate = new(3);

    private static bool IsGridView => SettingsStore.Current.FilesView == "grid";
    private bool InTrash => _current.TrimEnd('/') == FileOps.TrashFolder.TrimEnd('/');
    private bool InVirtual => Places.IsVirtual(_current);
    private bool InArchive => Archives.SourceOf(_current) != null;
    /// <summary>Nothing can be pasted or created here (Trash, Recent, Starred, inside an archive).</summary>
    private bool ReadOnlyPlace => InTrash || InVirtual || InArchive;

    public FileManagerWindow() : this(null) { }

    public FileManagerWindow(string? startPath)
    {
        InitializeComponent();

        BackButton.Content = Icons.Create("arrow-left", 18);
        ForwardButton.Content = Icons.Create("arrow-right", 18);
        UpButton.Content = Icons.Create("arrow-up", 18);
        NewButton.Content = Icons.Create("plus", 18);
        MoreButton.Content = Icons.Create("more", 18);
        NewTabButton.Content = Icons.Create("plus", 16);
        PreviewClose.Content = Icons.Create("close", 16);
        PreviewClose.Click += (_, _) => ClosePreview();
        PreviewOpen.Click += (_, _) =>
        {
            if (_previewEntry is not { } pe) return;
            ClosePreview();
            if (InTrash) _ = Restore(new List<FileEntry> { pe }); else OpenEntry(pe);
        };
        PreviewLayer.PointerPressed += (_, e) => { if (e.Source == PreviewLayer) ClosePreview(); };
        OpCancel.Click += (_, _) => _opCts?.Cancel();
        UndoButton.Click += (_, _) => DoUndo();
        FileTransfer.UndoChanged += UpdateUndo;
        // middle-click a folder: open it in a new tab
        FileList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle && (e.Source as Control)?.DataContext is FileEntry { IsDir: true } fe && !InTrash)
            { NewTab(fe.FullPath, activate: false); e.Handled = true; }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        SearchBox.InnerLeftContent = new Border { Margin = new Thickness(10, 0, 4, 0), Child = Icons.Create("search", 15), Opacity = 0.6 };
        foreach (var b in new[] { BackButton, ForwardButton, UpButton, NewButton, ViewButton, MoreButton })
            Avalonia.Automation.AutomationProperties.SetName(b, ToolTip.GetTip(b) as string ?? "");
        BuildNewMenu();
        BuildMoreMenu();
        ApplyView();
        InitExtras();

        PathFrame.PointerPressed += (_, e) => { if (e.Source == PathFrame || e.Source == Crumbs || e.Source is ScrollViewer) EditPath(); };
        PathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(PathBox.Text)) { Navigate(ExpandPath(PathBox.Text.Trim()), true); e.Handled = true; EndEditPath(); }
            else if (e.Key == Key.Escape) { EndEditPath(); e.Handled = true; }
        };
        PathBox.LostFocus += (_, _) => EndEditPath();

        SearchBox.TextChanged += (_, _) => { _filterSoon.Stop(); _filterSoon.Start(); };
        _filterSoon.Tick += (_, _) => { _filterSoon.Stop(); ApplyFilter(); };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = DeepSearch(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SearchBox.Text = ""; FileList.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down) { FileList.Focus(); if (FileList.ItemCount > 0) FileList.SelectedIndex = 0; e.Handled = true; }
        };

        FileList.SelectionChanged += (_, _) => { UpdateStatus(); UpdateDetailsSoon(); if (_previewOpen && Selected().FirstOrDefault() is { } sel) ShowPreview(sel); };
        FileList.ContextRequested += OnContextRequested;
        FileList.AddHandler(KeyDownEvent, OnListKeys, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeys, RoutingStrategies.Tunnel);

        _reloadSoon.Tick += (_, _) => { _reloadSoon.Stop(); Reload(keepSelection: true); };
        Closed += (_, _) => { _bgCts.Cancel(); _watcher?.Dispose(); _searchCts?.Cancel(); _thumbCts?.Cancel(); FileTransfer.UndoChanged -= UpdateUndo; };
        UpdateUndo();

        var start = startPath;
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start) && !Places.IsVirtual(start))
        {
            start = Path.Combine(AppPaths.Home, "Documents");
            if (!Directory.Exists(start)) start = AppPaths.Home;
        }
        Navigate(start, record: false);
    }

    // ═════════════════════════════════ sidebar ═════════════════════════════════
    private readonly List<(Button Button, string Path)> _navButtons = new();

    private void BuildSidebar()
    {
        Sidebar.Children.Clear();
        _navButtons.Clear();
        var home = AppPaths.Home;

        Section("PLACES");
        Nav("home", "Home", home);
        Nav("clock", "Recent", Places.Recent);
        Nav("star", "Starred", Places.Starred);
        foreach (var (icon, name) in new[] { ("monitor", "Desktop"), ("document", "Documents"), ("download", "Downloads"), ("image", "Pictures"), ("music", "Music"), ("film", "Videos") })
        {
            var p = AppPaths.UserFolder(name);
            if (Directory.Exists(p)) Nav(icon, name, p);
        }

        var pins = SettingsStore.Current.PinnedFolders.Where(Directory.Exists).ToList();
        if (pins.Count > 0)
        {
            Section("PINNED");
            foreach (var p in pins)
            {
                var btn = Nav("pin", Path.GetFileName(p.TrimEnd('/')) is { Length: > 0 } n ? n : p, p);
                var unpin = new MenuItem { Header = "Unpin from sidebar", Icon = Icons.Create("close", 14) };
                unpin.Click += (_, _) => TogglePin(p);
                btn.ContextMenu = new ContextMenu { Items = { unpin } };
            }
        }

        var drives = Drives();
        if (drives.Count > 0)
        {
            Section("DEVICES");
            foreach (var (mount, label, total, free) in drives)
            {
                var m = mount;
                Nav("drive", label, mount, total > 0 ? (double)(total - free) / total : -1, total > 0 ? $"{FileOps.FormatSize(free)} free of {FileOps.FormatSize(total)}" : null,
                    RemoteMounts.IsRemovableMount(mount) ? (Action)(() => _ = EjectDrive(m, label)) : null);
            }
        }

        AddNetworkSections();

        Section("OTHER");
        int trash = FileOps.TrashCount();
        Nav("trash", trash > 0 ? $"Trash ({trash})" : "Trash", FileOps.TrashFolder);
        HighlightNav();
    }

    private void Section(string title) =>
        Sidebar.Children.Add(new TextBlock { Text = title, Classes = { "eyebrow" }, Margin = new Thickness(10, Sidebar.Children.Count == 0 ? 2 : 14, 0, 4) });

    private Button Nav(string icon, string name, string path, double usage = -1, string? tip = null, Action? eject = null, Action? click = null)
    {
        var content = new StackPanel { Spacing = 4 };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        line.Children.Add(Icons.Create(icon, 17));
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
        Grid.SetColumn(label, 1);
        line.Children.Add(label);
        if (eject != null)
        {
            var ej = new Button { Content = Icons.Create("eject", 13), Padding = new Thickness(3), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
            ToolTip.SetTip(ej, "Eject / disconnect");
            ej.Click += (_, e) => { e.Handled = true; eject(); };
            Grid.SetColumn(ej, 2);
            line.Children.Add(ej);
        }
        content.Children.Add(line);
        if (usage >= 0)
            content.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = usage, Height = 3, MinHeight = 3, Margin = new Thickness(27, 0, 4, 0) });
        var btn = new Button { Content = content, Classes = { "nav" } };
        ToolTip.SetTip(btn, tip ?? path);
        Avalonia.Automation.AutomationProperties.SetName(btn, name);
        btn.Click += (_, _) =>
        {
            if (click != null) click();
            else if (Places.IsVirtual(path) || Directory.Exists(path)) Navigate(path, true);
            else if (path == FileOps.TrashFolder) { Directory.CreateDirectory(path); Navigate(path, true); }
        };
        // drop files on a place to move them there (Ctrl: copy), on Starred to star them, on the Trash to trash them
        DragDrop.SetAllowDrop(btn, true);
        btn.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DropEffect(path, e); e.Handled = true; });
        btn.AddHandler(DragDrop.DropEvent, (_, e) => { e.Handled = true; _ = DropInto(path, e); });
        Sidebar.Children.Add(btn);
        _navButtons.Add((btn, path));
        return btn;
    }

    private void HighlightNav()
    {
        foreach (var (b, p) in _navButtons) b.Classes.Set("current", p.TrimEnd('/') == _current.TrimEnd('/'));
    }

    /// <summary>Mounted drives worth showing: /, /home when separate, and anything under /media, /run/media, /mnt.</summary>
    internal static List<(string Mount, string Label, long Total, long Free)> Drives()
    {
        var list = new List<(string, string, long, long)>();
        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var f = line.Split(' ');
                if (f.Length < 3 || !f[0].StartsWith("/dev/")) continue;
                var mount = f[1].Replace("\\040", " ");
                bool show = mount == "/" || mount == "/home" || mount.StartsWith("/media/") || mount.StartsWith("/run/media/") || mount.StartsWith("/mnt/");
                if (!show || list.Any(x => x.Item1 == mount)) continue;
                long total = 0, free = 0;
                try { var di = new DriveInfo(mount); total = di.TotalSize; free = di.AvailableFreeSpace; } catch { }
                var label = mount == "/" ? "File System" : mount == "/home" ? "Home drive" : Path.GetFileName(mount);
                list.Add((mount, label, total, free));
            }
        }
        catch { }
        if (list.Count == 0) list.Add(("/", "File System", 0, 0));
        return list;
    }

    private static string? MountFor(string path)
    {
        try
        {
            return File.ReadLines("/proc/mounts").Select(l => l.Split(' ')).Where(f => f.Length > 2)
                .Select(f => f[1].Replace("\\040", " "))
                .Where(m => path == m || path.StartsWith(m.TrimEnd('/') + "/") || m == "/")
                .OrderByDescending(m => m.Length).FirstOrDefault();
        }
        catch { return null; }
    }

    private void TogglePin(string folder)
    {
        var pins = SettingsStore.Current.PinnedFolders;
        if (!pins.Remove(folder)) pins.Add(folder);
        SettingsStore.Save();
        BuildSidebar();
    }

    // ═════════════════════════════════ path bar ═════════════════════════════════
    private void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var home = AppPaths.Home.TrimEnd('/');
        string path = _current.TrimEnd('/');
        var parts = CrumbParts(path);
        for (int i = 0; i < parts.Count; i++)
        {
            var (label, p, icon) = parts[i];
            if (i > 0) Crumbs.Children.Add(new Border { Opacity = 0.45, Margin = new Thickness(0, 0), Child = Icons.Create("chevron-right", 14), VerticalAlignment = VerticalAlignment.Center });
            object content = icon != null
                ? new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icons.Create(icon, 15), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } }
                : label;
            var b = new Button { Content = content, Classes = { "crumb" }, FontWeight = i == parts.Count - 1 ? FontWeight.SemiBold : FontWeight.Normal };
            var target = p;
            b.Click += (_, _) => Navigate(target, true);
            DragDrop.SetAllowDrop(b, true);
            b.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DropEffect(target, e); e.Handled = true; });
            b.AddHandler(DragDrop.DropEvent, (_, e) => { e.Handled = true; _ = DropInto(target, e); });
            Crumbs.Children.Add(b);
        }
        Dispatcher.UIThread.Post(() => CrumbScroll.Offset = new Vector(CrumbScroll.Extent.Width, 0), DispatcherPriority.Background);
    }

    private void EditPath()
    {
        PathBox.Text = InVirtual ? "" : _current;
        CrumbScroll.IsVisible = false;
        PathBox.IsVisible = true;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void EndEditPath()
    {
        PathBox.IsVisible = false;
        CrumbScroll.IsVisible = true;
    }

    private static string ExpandPath(string p) => p == "~" ? AppPaths.Home : p.StartsWith("~/") ? Path.Combine(AppPaths.Home, p[2..]) : p;

    // ═════════════════════════════════ navigation ═════════════════════════════════
    private void Navigate(string path, bool record)
    {
        try
        {
            if (Places.IsVirtual(path)) { NavigateVirtual(path, record); return; }
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path)) return;
            RestoreSortAfterRecent();
            FileDialogs.Remember(path);                       // Open / Save dialogs in the other apps start here
            // can we read it?
            _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();

            if (record && _current.Length > 0 && _current != path) { _back.Push(_current); _forward.Clear(); }
            _current = path;
            Title = "Files — " + TabTitle(path);
            SearchBox.Text = "";
            SearchBox.PlaceholderText = "Search in " + (TabTitle(path) is { Length: > 0 } n ? n : "this folder");
            BuildCrumbs();
            BuildSidebar();
            Reload(keepSelection: false);
            Watch();
            UpdateButtons();
            UpdateFreeSpace();
            BuildTabs();
            UpdateDetailsSoon();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            _ = Dialogs.ShowMessage(this, "Files", "That folder can't be opened: " + ex.Message);
        }
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (InVirtual) return;
        try
        {
            _watcher = new FileSystemWatcher(_current) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite };
            FileSystemEventHandler h = (_, _) => Dispatcher.UIThread.Post(() => { _reloadSoon.Stop(); _reloadSoon.Start(); });
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h;
            _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(() => { _reloadSoon.Stop(); _reloadSoon.Start(); });
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    private void Reload(bool keepSelection)
    {
        if (_deepResults) return;                         // a subfolder search is on screen; Esc / typing goes back
        var keep = keepSelection ? Selected().Select(e => e.FullPath).ToHashSet() : new HashSet<string>();
        bool hidden = SettingsStore.Current.FilesShowHidden;
        var list = new List<FileEntry>();
        if (InVirtual) list = VirtualEntries();
        else try
        {
            var info = new DirectoryInfo(_current);
            foreach (var d in info.EnumerateDirectories())
                if (hidden || !d.Name.StartsWith('.')) list.Add(Entry(d.FullName, d.Name, true, 0, d.LastWriteTime));
            foreach (var f in info.EnumerateFiles())
                if (hidden || !f.Name.StartsWith('.')) list.Add(Entry(f.FullName, f.Name, false, f.Length, f.LastWriteTime));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        var starred = SettingsStore.Current.StarredPaths.Count > 0 ? SettingsStore.Current.StarredPaths.ToHashSet() : null;
        if (starred != null) foreach (var x in list) x.Starred = starred.Contains(x.FullPath);
        _all = list;
        ApplyFilter(keep);
        if (InTrash) UpdateTrashBar();
        TrashBar.IsVisible = InTrash;
        UpdateArchiveBar();
    }

    private static FileEntry Entry(string full, string name, bool dir, long size, DateTime modified, string? kind = null) => new()
    {
        Name = name, FullPath = full, IsDir = dir, Size = size, Modified = modified,
        Kind = kind ?? FileOps.KindOf(full, dir),
        IconKey = FileOps.IconName(full, dir) + "|" + ColorFor(FileOps.IconName(full, dir)),
    };

    internal static string ColorFor(string icon) => icon switch
    {
        "folder" => SettingsStore.Current.Accent is { Length: > 0 } a ? a : "#C8102E",
        "image" => "#2F855A", "music" => "#805AD5", "film" => "#DD6B20", "archive" => "#B7791F",
        "sheet" => "#38A169", "slides" => "#E53E3E", "document" => "#3182CE", "code" => "#718096", "apps" => "#C8102E",
        _ => "#A0AEC0"
    };

    private void ApplyFilter(HashSet<string>? keep = null)
    {
        var q = (SearchBox.Text ?? "").Trim();
        if (_deepResults && q.Length == 0) { _deepResults = false; Reload(false); return; }
        if (_deepResults) return;
        IEnumerable<FileEntry> src = _all;
        if (q.Length > 0) src = src.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        Show(InVirtual && _current == Places.Recent && _sort == SortKey.Date && !_ascending ? src.ToList() : Sorted(src), keep,
             q.Length > 0 ? (InVirtual ? $"Nothing called “{q}” here." : $"Nothing called “{q}” here — press Enter to search the subfolders too.") : null);
    }

    private List<FileEntry> Sorted(IEnumerable<FileEntry> src)
    {
        Func<FileEntry, object> key = _sort switch
        {
            SortKey.Size => e => e.Size,
            SortKey.Date => e => e.Modified,
            SortKey.Kind => e => e.Kind,
            _ => e => e.Name.TrimStart('.').ToLowerInvariant()
        };
        var ordered = InVirtual ? src.OrderBy(_ => 0) : src.OrderByDescending(e => e.IsDir);
        return (_ascending ? ordered.ThenBy(key) : ordered.ThenByDescending(key)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void Show(List<FileEntry> items, HashSet<string>? keep, string? emptyText)
    {
        FileList.ItemsSource = items;
        if (keep is { Count: > 0 })
            foreach (var e in items.Where(i => keep.Contains(i.FullPath))) FileList.SelectedItems?.Add(e);
        EmptyState.IsVisible = items.Count == 0;
        EmptyIcon.Content = Icons.Create(InTrash ? "trash" : emptyText != null ? "search" : _current == Places.Recent ? "clock" : _current == Places.Starred ? "star" : "folder", 56);
        EmptyText.Text = items.Count > 0 ? "" : emptyText ?? (InTrash ? "The Trash is empty"
            : _current == Places.Recent ? "Files you open show up here."
            : _current == Places.Starred ? "Nothing starred yet. Right-click a file or folder and choose Star to keep it here."
            : "This folder is empty");
        UpdateSortHeaders();
        UpdateStatus();
        LoadThumbnails(items);
    }

    /// <summary>Enter in the search box: look through the subfolders too (in the background, up to 500 hits).</summary>
    private async Task DeepSearch()
    {
        var q = (SearchBox.Text ?? "").Trim();
        if (q.Length == 0 || InVirtual) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var root = _current;
        bool hidden = SettingsStore.Current.FilesShowHidden;
        StatusLabel.Text = $"Searching {Path.GetFileName(root.TrimEnd('/'))} and its subfolders…";
        List<FileEntry> hits;
        try
        {
            hits = await Task.Run(() =>
            {
                var r = new List<FileEntry>();
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = hidden ? 0 : FileAttributes.Hidden, MaxRecursionDepth = 12 };
                foreach (var p in Directory.EnumerateFileSystemEntries(root, "*" + q + "*", opts))
                {
                    if (cts.IsCancellationRequested || r.Count >= 500) break;
                    var name = Path.GetFileName(p);
                    if (!hidden && p[root.Length..].Split('/').Any(s => s.StartsWith('.'))) continue;
                    bool dir = Directory.Exists(p);
                    var rel = Path.GetDirectoryName(p)![root.Length..].TrimStart('/');
                    try
                    {
                        var fi = new FileInfo(p);
                        r.Add(Entry(p, name, dir, dir ? 0 : fi.Length, dir ? Directory.GetLastWriteTime(p) : fi.LastWriteTime, rel.Length == 0 ? "Here" : "in " + rel));
                    }
                    catch { }
                }
                return r;
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested) return;
        _deepResults = true;
        Show(Sorted(hits), null, $"Nothing called “{q}” in this folder or its subfolders.");
        StatusLabel.Text = $"{hits.Count}{(hits.Count >= 500 ? "+" : "")} found in subfolders · Esc to go back";
    }

    private void UpdateButtons()
    {
        BackButton.IsEnabled = _back.Count > 0;
        ForwardButton.IsEnabled = _forward.Count > 0;
        UpButton.IsEnabled = !InVirtual && (ArchiveParent() != null || Directory.GetParent(_current) != null);
    }

    private void UpdateStatus()
    {
        if (_deepResults) return;
        var total = (FileList.ItemsSource as List<FileEntry>)?.Count ?? 0;
        var sel = Selected();
        if (sel.Count == 0) { StatusLabel.Text = total == 1 ? "1 item" : $"{total} items"; return; }
        var files = sel.Where(e => !e.IsDir).ToList();
        StatusLabel.Text = $"{sel.Count} of {total} selected" + (files.Count > 0 ? $" · {FileOps.FormatSize(files.Sum(e => e.Size))}" : "")
                           + (sel.Count == 1 ? $" · {sel[0].Kind}" : "");
    }

    private void UpdateFreeSpace()
    {
        FreeLabel.Text = ""; FreeBar.IsVisible = false;
        if (InVirtual) return;
        try
        {
            if (MountFor(_current) is not { } m) return;
            var di = new DriveInfo(m);
            if (di.TotalSize <= 0) return;
            FreeLabel.Text = $"{FileOps.FormatSize(di.AvailableFreeSpace)} free of {FileOps.FormatSize(di.TotalSize)}";
            FreeBar.Value = 1 - (double)di.AvailableFreeSpace / di.TotalSize;
            FreeBar.IsVisible = true;
        }
        catch { }
    }

    private void OnBack(object? s, RoutedEventArgs e)
    {
        if (_back.Count == 0) return;
        _forward.Push(_current);
        Navigate(_back.Pop(), record: false);
    }

    private void OnForward(object? s, RoutedEventArgs e)
    {
        if (_forward.Count == 0) return;
        _back.Push(_current);
        Navigate(_forward.Pop(), record: false);
    }

    private void OnUp(object? s, RoutedEventArgs e)
    {
        if (InVirtual) return;
        if (ArchiveParent() is { } outside) { Navigate(outside, record: true); return; }
        var parent = Directory.GetParent(_current);
        if (parent != null) Navigate(parent.FullName, record: true);
    }

    // ═════════════════════════════════ tabs ═════════════════════════════════
    private string TabTitle(string path) =>
        Places.IsVirtual(path) ? Places.Title(path)
        : Archives.SourceOf(path) is { } arc && ArchiveRoot(path) == path.TrimEnd('/') ? Path.GetFileName(arc)
        : path.TrimEnd('/') == FileOps.TrashFolder.TrimEnd('/') ? "Trash"
        : path == "/" ? "File System"
        : path.TrimEnd('/') == AppPaths.Home.TrimEnd('/') ? "Home"
        : Path.GetFileName(path.TrimEnd('/'));

    private void BuildTabs()
    {
        TabBar.IsVisible = _tabs.Count > 1;
        if (_tabs.Count < 2) { TabStrip.Children.Clear(); return; }
        ShellTabs.Build(TabStrip, _tabs.Select((t, i) => new TabSpec
        {
            Title = TabTitle(t.Current),
            IconName = t.Current == Places.Recent ? "clock" : t.Current == Places.Starred ? "star" : Archives.SourceOf(t.Current) != null ? "archive"
                     : t.Current.TrimEnd('/') == FileOps.TrashFolder.TrimEnd('/') ? "trash" : t.Current.TrimEnd('/') == AppPaths.Home.TrimEnd('/') ? "home" : "folder",
            Active = i == _activeTab,
            Tooltip = t.Current,
            Select = () => SwitchTab(i),
            Close = () => CloseTab(i),
        }));
    }

    private void NewTab(string path, bool activate = true)
    {
        var t = new TabState { Current = path };
        _tabs.Insert(_activeTab + 1, t);
        if (activate) SwitchTab(_activeTab + 1);
        else BuildTabs();
    }

    private void SwitchTab(int i)
    {
        if (i < 0 || i >= _tabs.Count) return;
        if (_previewOpen) ClosePreview();
        _activeTab = i;
        _deepResults = false;
        Navigate(Tab.Current, record: false);
    }

    private void CloseTab(int i)
    {
        if (_tabs.Count <= 1) { Close(); return; }
        _tabs.RemoveAt(i);
        if (_activeTab >= _tabs.Count) _activeTab = _tabs.Count - 1;
        else if (i < _activeTab) _activeTab--;
        SwitchTab(_activeTab);
    }

    private void OnNewTab(object? s, RoutedEventArgs e) => NewTab(_current);

    // ═════════════════════════════════ Space: preview ═════════════════════════════════
    private bool _previewOpen;
    private FileEntry? _previewEntry;
    private CancellationTokenSource? _previewCts;

    private void TogglePreview()
    {
        if (_previewOpen) { ClosePreview(); return; }
        if (Selected().FirstOrDefault() is { } e) ShowPreview(e);
    }

    private void ClosePreview()
    {
        _previewOpen = false;
        _previewEntry = null;
        _previewCts?.Cancel();
        PreviewLayer.IsVisible = false;
        PreviewBody.Children.Clear();
        FileList.Focus();
    }

    private void ShowPreview(FileEntry e)
    {
        _previewOpen = true;
        _previewEntry = e;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        PreviewLayer.IsVisible = true;
        PreviewIcon.Content = new IconConverter(null, 32).Convert(e.IconKey, typeof(object), "32", System.Globalization.CultureInfo.InvariantCulture);
        PreviewName.Text = e.Name;
        PreviewInfo.Text = e.IsDir ? $"{e.Kind} · {e.DateText}" : $"{e.Kind} · {e.SizeText} · {e.DateText}";
        PreviewOpen.Content = InTrash ? "Restore" : e.IsDir ? "Open folder" : "Open";
        PreviewBody.Children.Clear();

        Control Center(Control c) { c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center; return c; }
        Control Message(string icon, string text) => Center(new StackPanel
        {
            Spacing = 12,
            Children = { Center((Control)new IconConverter(null, 96).Convert(e.IconKey.Split('|')[0] == "file" ? icon + "|" + "#A0AEC0" : e.IconKey, typeof(object), "96", System.Globalization.CultureInfo.InvariantCulture)!),
                         Center(new TextBlock { Text = text, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420 }) }
        });

        var icon = e.IconKey.Split('|')[0];
        if (icon == "image" && !e.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var img = new Image { Stretch = Stretch.Uniform, Source = e.Thumb };
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
            PreviewBody.Children.Add(img);
            _ = Task.Run(() =>
            {
                try
                {
                    using var fs = File.OpenRead(e.FullPath);
                    var bmp = Bitmap.DecodeToWidth(fs, 1600, BitmapInterpolationMode.HighQuality);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        img.Source = bmp;
                        PreviewInfo.Text += $" · {bmp.PixelSize.Width} × {bmp.PixelSize.Height}";
                    });
                }
                catch { Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) { PreviewBody.Children.Clear(); PreviewBody.Children.Add(Message("image", "This picture can't be shown.")); } }); }
            }, cts.Token);
        }
        else if (!e.IsDir && (icon == "code" || FileOps.TextExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()) || LooksLikeText(e)))
        {
            var text = new SelectableTextBlock { FontFamily = new FontFamily("DejaVu Sans Mono,Liberation Mono,monospace"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            PreviewBody.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                Child = new ScrollViewer { Content = text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }
            });
            _ = Task.Run(() =>
            {
                string content;
                try
                {
                    using var fs = File.OpenRead(e.FullPath);
                    var buf = new byte[Math.Min(fs.Length, 96 * 1024)];
                    int n = fs.Read(buf, 0, buf.Length);
                    content = System.Text.Encoding.UTF8.GetString(buf, 0, n) + (fs.Length > n ? "\n\n… (the rest isn't shown — open the file to see all of it)" : "");
                }
                catch (Exception ex) { content = "Can't read this file: " + ex.Message; }
                Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) text.Text = content; });
            }, cts.Token);
        }
        else if (e.IsDir)
        {
            var info = new TextBlock { Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, Text = "Counting…" };
            var names = new TextBlock { Opacity = 0.85, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, MaxWidth = 520 };
            PreviewBody.Children.Add(Center(new StackPanel { Spacing = 12, Children = { Center((Control)new IconConverter(null, 96).Convert(e.IconKey, typeof(object), "96", System.Globalization.CultureInfo.InvariantCulture)!), info, names } }));
            _ = Task.Run(() =>
            {
                try
                {
                    var entries = Directory.EnumerateFileSystemEntries(e.FullPath).Where(x => !Path.GetFileName(x).StartsWith('.')).ToList();
                    long size = FileTransfer.SizeOf(e.FullPath);
                    var first = string.Join("  ·  ", entries.Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(14));
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cts.IsCancellationRequested) return;
                        info.Text = $"{entries.Count} item{(entries.Count == 1 ? "" : "s")} · {FileOps.FormatSize(size)}";
                        names.Text = entries.Count > 14 ? first + "  …" : first;
                    });
                }
                catch (Exception ex) { Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) info.Text = "Can't look inside: " + ex.Message; }); }
            }, cts.Token);
        }
        else PreviewBody.Children.Add(Message(icon, "There's no preview for this kind of file. Press Enter to open it."));
    }

    /// <summary>Small files without a known extension: text when the first bytes contain no zero byte.</summary>
    private static bool LooksLikeText(FileEntry e)
    {
        if (e.Size == 0 || e.Size > 2_000_000 || Path.HasExtension(e.Name)) return false;
        try
        {
            using var fs = File.OpenRead(e.FullPath);
            var buf = new byte[Math.Min(4096, (int)e.Size)];
            int n = fs.Read(buf, 0, buf.Length);
            return !buf.Take(n).Contains((byte)0);
        }
        catch { return false; }
    }

    // ═════════════════════════════════ copy / move with progress, undo ═════════════════════════════════
    private CancellationTokenSource? _opCts;
    private bool _busy;

    /// <summary>Copies or moves with progress and a Cancel button; asks once about names that already exist.</summary>
    private async Task Transfer(List<string> paths, string dest, bool move)
    {
        if (paths.Count == 0 || Places.IsVirtual(dest) || InTrash && dest.TrimEnd('/') == _current.TrimEnd('/')) return;
        if (Archives.SourceOf(dest) != null) { StatusLabel.Text = "Files can't be added to an archive here — extract it first."; return; }
        if (_busy) { StatusLabel.Text = "Another copy or move is still running — wait for it to finish."; return; }
        var clash = FileTransfer.Conflict.KeepBoth;
        var clashes = FileTransfer.Clashes(paths, dest, move);
        if (clashes.Count > 0)
        {
            var where = TabTitle(dest);
            var msg = clashes.Count == 1 ? $"“{clashes[0]}” already exists in {where}." : $"{clashes.Count} items already exist in {where} (for example “{clashes[0]}”).";
            var choice = await Dialogs.Ask(this, move ? "Move" : "Copy", msg + "\n\nReplaced items go to the Trash, so you can get them back.",
                clashes.Count == 1 ? "Replace" : "Replace all", "Keep both", clashes.Count == 1 ? "Skip" : "Skip them", "Cancel");
            if (choice is < 0 or 3) return;
            clash = choice switch { 0 => FileTransfer.Conflict.Replace, 2 => FileTransfer.Conflict.Skip, _ => FileTransfer.Conflict.KeepBoth };
        }

        _busy = true;
        var cts = _opCts = new CancellationTokenSource();
        var verb = move ? "Moving" : "Copying";
        var what = paths.Count == 1 ? $"“{Path.GetFileName(paths[0].TrimEnd('/'))}”" : $"{paths.Count} items";
        OpText.Text = $"{verb} {what} to {TabTitle(dest)}…";
        OpBar.Value = 0;
        // only show the panel when it takes a moment (renames are instant)
        var showTimer = DispatcherTimer.RunOnce(() => OpPanel.IsVisible = true, TimeSpan.FromMilliseconds(400));
        var progress = new Progress<FileTransfer.Progress>(pr =>
        {
            OpBar.Value = pr.Fraction;
            OpText.Text = pr.BytesTotal > 0
                ? $"{verb} {pr.Current} — {FileOps.FormatSize(pr.BytesDone)} of {FileOps.FormatSize(pr.BytesTotal)} ({pr.ItemsDone + 1} of {pr.ItemsTotal})"
                : $"{verb} {pr.Current} ({pr.ItemsDone + 1} of {pr.ItemsTotal})";
        });
        FileTransfer.Result result;
        try { result = await FileTransfer.Run(paths, dest, move, clash, progress, cts.Token); }
        catch (OperationCanceledException) { result = new FileTransfer.Result { Cancelled = true }; }
        finally
        {
            showTimer.Dispose();
            OpPanel.IsVisible = false;
            _busy = false;
        }
        FileTransfer.RecordTransfer(result, move, dest);
        if (move) foreach (var (f, t) in result.Done) Places.Moved(f, t);
        if (move && FileClipboard.IsCut) FileClipboard.Paths = new List<string>();
        Reload(keepSelection: false);
        // select what arrived here
        if (dest.TrimEnd('/') == _current.TrimEnd('/') && FileList.ItemsSource is List<FileEntry> now)
            foreach (var x in now.Where(x => result.Done.Any(d => d.To == x.FullPath))) FileList.SelectedItems?.Add(x);
        BuildSidebar();
        if (result.Cancelled) StatusLabel.Text = $"Stopped — {result.Done.Count} of {paths.Count} done (Ctrl+Z undoes those).";
        if (result.Errors.Count > 0) await Dialogs.ShowMessage(this, move ? "Move" : "Copy", string.Join("\n", result.Errors.Take(6)));
    }

    private void UpdateUndo()
    {
        // transfers finish on a background thread: update the button on the window's own
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { Avalonia.Threading.Dispatcher.UIThread.Post(UpdateUndo); return; }
        var label = FileTransfer.UndoLabel;
        UndoButton.IsVisible = label != null;
        UndoButton.Content = label != null ? "Undo " + LowerFirst(label) : "";
        ToolTip.SetTip(UndoButton, "Ctrl+Z");
    }

    private static string LowerFirst(string s) => s.Length > 0 ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private void DoUndo()
    {
        if (_busy) return;
        string? what;
        try { what = FileTransfer.Undo(); }
        catch (Exception ex) { StatusLabel.Text = "Couldn't undo: " + ex.Message; return; }
        if (what == null) return;
        Reload(keepSelection: false);
        BuildSidebar();
        StatusLabel.Text = "Undone: " + LowerFirst(what);
    }

    // ═════════════════════════════════ view, sorting ═════════════════════════════════
    private void OnToggleView(object? s, RoutedEventArgs e) => SetView(!IsGridView);

    private void SetView(bool grid)
    {
        SettingsStore.Current.FilesView = grid ? "grid" : "list";
        SettingsStore.Save();
        ApplyView();
        LoadThumbnails(FileList.ItemsSource as List<FileEntry> ?? new());
    }

    private void ApplyView()
    {
        bool grid = IsGridView;
        FileList.ItemTemplate = (IDataTemplate)Resources[grid ? "GridTile" : "ListRow"]!;
        FileList.ItemsPanel = grid
            ? new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal })
            : new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        FileList.Classes.Set("grid", grid);
        ListHeader.IsVisible = !grid;
        ViewButton.Content = Icons.Create(grid ? "list" : "grid", 18);
        ToolTip.SetTip(ViewButton, grid ? "Show as a list (Ctrl+1)" : "Show as a grid (Ctrl+2)");
    }

    private void SortBy(SortKey key)
    {
        if (_sort == key) _ascending = !_ascending;
        else { _sort = key; _ascending = key is SortKey.Name or SortKey.Kind; }
        if (_deepResults) Show(Sorted(FileList.ItemsSource as List<FileEntry> ?? new()), null, null);
        else ApplyFilter(Selected().Select(x => x.FullPath).ToHashSet());
    }

    private void UpdateSortHeaders()
    {
        string Arrow(SortKey k) => _sort == k ? (_ascending ? "  ↑" : "  ↓") : "";
        SortName.Content = "Name" + Arrow(SortKey.Name);
        SortKind.Content = "Kind" + Arrow(SortKey.Kind);
        SortSize.Content = "Size" + Arrow(SortKey.Size);
        SortDate.Content = "Modified" + Arrow(SortKey.Date);
    }

    private void OnSortName(object? s, RoutedEventArgs e) => SortBy(SortKey.Name);
    private void OnSortKind(object? s, RoutedEventArgs e) => SortBy(SortKey.Kind);
    private void OnSortSize(object? s, RoutedEventArgs e) => SortBy(SortKey.Size);
    private void OnSortDate(object? s, RoutedEventArgs e) => SortBy(SortKey.Date);

    private static string FriendlyDate(DateTime d)
    {
        var today = DateTime.Today;
        if (d.Date == today) return "Today, " + d.ToString("HH:mm");
        if (d.Date == today.AddDays(-1)) return "Yesterday, " + d.ToString("HH:mm");
        if (d.Year == today.Year) return d.ToString("d MMM, HH:mm");
        return d.ToString("d MMM yyyy");
    }

    // ═════════════════════════════════ thumbnails ═════════════════════════════════
    private void LoadThumbnails(List<FileEntry> items)
    {
        _thumbCts?.Cancel();
        var cts = _thumbCts = new CancellationTokenSource();
        var pics = items.Where(e => !e.IsDir && e.IconKey.StartsWith("image|") && e.Size < 40_000_000 && !e.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).Take(400).ToList();
        foreach (var e in pics)
        {
            var key = e.FullPath + "|" + e.Modified.Ticks;
            if (ThumbCache.TryGetValue(key, out var cached)) { e.Thumb = cached; continue; }
            var entry = e;
            _ = Task.Run(async () =>
            {
                await ThumbGate.WaitAsync(cts.Token);
                try
                {
                    if (cts.IsCancellationRequested) return;
                    using var fs = File.OpenRead(entry.FullPath);
                    var bmp = Bitmap.DecodeToWidth(fs, 160, BitmapInterpolationMode.HighQuality);
                    await Dispatcher.UIThread.InvokeAsync(() => { ThumbCache[key] = bmp; if (!cts.IsCancellationRequested) entry.Thumb = bmp; });
                }
                catch { }
                finally { ThumbGate.Release(); }
            }, cts.Token);
        }
    }

    // ═════════════════════════════════ opening ═════════════════════════════════
    private List<FileEntry> Selected() => FileList.SelectedItems?.Cast<FileEntry>().ToList() ?? new List<FileEntry>();

    private void OpenEntry(FileEntry entry)
    {
        if (InTrash) { _ = ShowProperties(entry); return; }          // trashed files are restored, not opened
        if (entry.IsDir) Navigate(entry.FullPath, record: true);
        else if (Archives.IsArchive(entry.FullPath)) _ = BrowseArchive(entry.FullPath);
        else
        {
            RecentDocs.Add(entry.FullPath);
            FileOpener.Open(entry.FullPath);
        }
    }

    private void OnEntryActivated(object? s, TappedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is FileEntry entry) OpenEntry(entry);
    }

    // ═════════════════════════════════ menus ═════════════════════════════════
    private static MenuItem Item(string header, string? icon, Action act, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (icon != null) mi.Icon = Icons.Create(icon, 15);
        if (gesture != null) mi.InputGesture = KeyGesture.Parse(gesture);
        mi.Click += (_, _) => act();
        return mi;
    }

    private void BuildNewMenu()
    {
        var f = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        f.Items.Add(Item("New folder", "folder-plus", () => _ = NewFolder(), "Ctrl+Shift+N"));
        f.Items.Add(Item("New text document", "file-plus", () => _ = NewTextFile()));
        NewButton.Flyout = f;
    }

    private void BuildMoreMenu()
    {
        var f = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        f.Opening += (_, _) =>
        {
            f.Items.Clear();
            var hidden = new MenuItem { Header = "Show hidden files", ToggleType = MenuItemToggleType.CheckBox, IsChecked = SettingsStore.Current.FilesShowHidden, InputGesture = KeyGesture.Parse("Ctrl+H") };
            hidden.Click += (_, _) => ToggleHidden();
            f.Items.Add(hidden);
            var sort = new MenuItem { Header = "Sort by", Icon = Icons.Create("sort", 15) };
            foreach (var (label, k) in new[] { ("Name", SortKey.Name), ("Kind", SortKey.Kind), ("Size", SortKey.Size), ("Modified", SortKey.Date) })
            {
                var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = _sort == k };
                var kk = k;
                mi.Click += (_, _) => SortBy(kk);
                sort.Items.Add(mi);
            }
            f.Items.Add(sort);
            f.Items.Add(new Separator());
            var details = new MenuItem { Header = "Details panel", ToggleType = MenuItemToggleType.CheckBox, IsChecked = SettingsStore.Current.FilesDetails, InputGesture = KeyGesture.Parse("F9") };
            details.Click += (_, _) => ToggleDetails();
            f.Items.Add(details);
            f.Items.Add(new Separator());
            f.Items.Add(Item("Connect to server…", "server", () => _ = ConnectToServer()));
            if (!ReadOnlyPlace)
            {
                bool pinned = SettingsStore.Current.PinnedFolders.Contains(_current);
                f.Items.Add(Item(pinned ? "Unpin this folder" : "Pin this folder to the sidebar", "pin", () => TogglePin(_current)));
                f.Items.Add(Item("Open Terminal here", "terminal", () => TerminalLauncher.Launch(_current)));
                f.Items.Add(Item("Properties of this folder", "info", () => _ = ShowProperties(Entry(_current, Path.GetFileName(_current.TrimEnd('/')) is { Length: > 0 } n ? n : "/", true, 0, Directory.GetLastWriteTime(_current)))));
            }
        };
        f.Items.Add(new MenuItem { Header = "…" });
        MoreButton.Flyout = f;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // right-click on an item selects it (unless it's already part of the selection); on empty space, clears it
        var hit = (e.Source as Control)?.DataContext as FileEntry;
        if (hit == null) FileList.SelectedItems?.Clear();
        else if (FileList.SelectedItems?.Contains(hit) != true) { FileList.SelectedItems?.Clear(); FileList.SelectedItems?.Add(hit); }
        var sel = Selected();
        var menu = new ContextMenu();
        var items = menu.Items;
        if (InTrash)
        {
            if (sel.Count > 0)
            {
                items.Add(Item("Restore", "restore", () => _ = Restore(sel)));
                items.Add(Item("Delete permanently", "trash", () => _ = Delete(permanently: true), "Shift+Delete"));
                items.Add(new Separator());
                items.Add(Item("Properties", "info", () => _ = ShowProperties(sel[0]), "Alt+Enter", sel.Count == 1));
            }
            else items.Add(Item("Empty Trash", "trash", () => OnEmptyTrash(null, new RoutedEventArgs()), enabled: _all.Count > 0));
        }
        else if (sel.Count == 0)
        {
            if (InVirtual)
            {
                if (_current == Places.Recent) items.Add(Item("Clear Red Star's recent list", "close", () => { SettingsStore.Current.RecentFiles.Clear(); SettingsStore.Save(); Reload(false); }));
                items.Add(Item("Select all", null, () => FileList.SelectAll(), "Ctrl+A"));
            }
            else if (InArchive)
            {
                items.Add(Item("Extract all here", "archive", () => _ = ExtractArchive(Archives.SourceOf(_current)!, null)));
                items.Add(Item("Select all", null, () => FileList.SelectAll(), "Ctrl+A"));
            }
            else
            {
                items.Add(Item("New folder", "folder-plus", () => _ = NewFolder(), "Ctrl+Shift+N"));
                items.Add(Item("New text document", "file-plus", () => _ = NewTextFile()));
                items.Add(Item("Paste", "paste", () => _ = Paste(_current), "Ctrl+V", FileClipboard.Paths.Count > 0 || _systemClipboardMayHaveFiles));
                if (FileTransfer.UndoLabel is { } ul) items.Add(Item("Undo " + LowerFirst(ul), "restore", DoUndo, "Ctrl+Z"));
                items.Add(new Separator());
                items.Add(Item("Select all", null, () => FileList.SelectAll(), "Ctrl+A"));
                items.Add(Item("Open Terminal here", "terminal", () => TerminalLauncher.Launch(_current)));
                items.Add(Item("Properties", "info", () => _ = ShowProperties(Entry(_current, Path.GetFileName(_current.TrimEnd('/')), true, 0, Directory.GetLastWriteTime(_current))), "Alt+Enter"));
            }
        }
        else
        {
            var one = sel.Count == 1 ? sel[0] : null;
            bool canChange = !InArchive;
            items.Add(Item("Open", one?.IsDir == true ? "folder" : one != null && Archives.IsArchive(one.FullPath) ? "archive" : "file", () => { foreach (var x in sel.Take(10)) OpenEntry(x); }, "Enter"));
            var files = sel.Where(x => !x.IsDir).ToList();
            if (files.Count > 0 && files.Count == sel.Count) items.Add(OpenWithMenu(files));
            if (one is { IsDir: true })
            {
                items.Add(Item("Open in new tab", null, () => NewTab(one.FullPath), "Ctrl+Enter"));
                items.Add(Item("Open in new window", null, () => WindowManager.Open(new FileManagerWindow(one.FullPath), "files")));
                items.Add(Item("Open Terminal here", "terminal", () => TerminalLauncher.Launch(one.FullPath)));
            }
            if (one is { IsDir: false })
                items.Add(Item("Share…", "share", () => ShareService.ShowMenu(FileList, one.Name, file: one.FullPath)));
            if ((_deepResults || InVirtual || InArchive) && one != null)
                items.Add(Item(InArchive ? "Show the archive in its folder" : "Show in its folder", "folder", () =>
                {
                    _deepResults = false;
                    var target = InArchive ? Archives.SourceOf(_current)! : one.FullPath;
                    Navigate(Path.GetDirectoryName(target)!, true);
                    SelectPath(target);
                }));

            // archives
            var archives = sel.Where(x => !x.IsDir && Archives.IsArchive(x.FullPath)).ToList();
            if (archives.Count > 0 && canChange)
            {
                items.Add(new Separator());
                items.Add(Item(archives.Count == 1 ? "Extract here" : $"Extract {archives.Count} archives here", "archive", () => _ = ExtractArchives(archives.Select(a => a.FullPath).ToList(), null)));
                items.Add(Item("Extract to…", null, () => _ = ExtractArchivesTo(archives.Select(a => a.FullPath).ToList())));
            }
            if (canChange && !InVirtual && !_deepResults)
            {
                var compress = new MenuItem { Header = "Compress", Icon = Icons.Create("archive", 15) };
                var baseName = one != null ? (one.IsDir ? one.Name : BatchRename.Split(one.Name).Stem) : TabTitle(_current) is { Length: > 0 } t ? t : "Archive";
                compress.Items.Add(Item($"{baseName}.zip", null, () => _ = Compress(sel, baseName + ".zip")));
                compress.Items.Add(Item($"{baseName}.tar.gz", null, () => _ = Compress(sel, baseName + ".tar.gz")));
                if (archives.Count == 0 || archives.Count < sel.Count) { items.Add(new Separator()); }
                items.Add(compress);
            }

            items.Add(new Separator());
            if (canChange) items.Add(Item("Cut", "cut", () => ClipSelection(cut: true), "Ctrl+X"));
            items.Add(Item("Copy", "copy", () => ClipSelection(cut: false), "Ctrl+C"));
            if (one is { IsDir: true } && canChange) items.Add(Item("Paste into folder", "paste", () => _ = Paste(one.FullPath), enabled: FileClipboard.Paths.Count > 0));
            if (canChange)
            {
                items.Add(Item("Duplicate", "copy", () => _ = Duplicate(sel)));
                items.Add(Item(sel.Count > 1 ? $"Rename {sel.Count} items…" : "Rename", "rename", () => _ = Rename(), "F2"));
            }
            if (!InArchive)
            {
                bool allStarred = sel.All(x => x.Starred);
                items.Add(Item(allStarred ? "Remove star" : "Star", allStarred ? "star" : "star-filled", () => SetStar(sel, !allStarred)));
            }
            if (one is { IsDir: true } && !InArchive)
                items.Add(Item(SettingsStore.Current.PinnedFolders.Contains(one.FullPath) ? "Unpin from sidebar" : "Pin to sidebar", "pin", () => TogglePin(one.FullPath)));
            items.Add(Item("Copy path", null, () => ClipboardHistory.Use(string.Join("\n", sel.Select(x => x.FullPath)))));
            if (_current == Places.Recent)
                items.Add(Item("Remove from Recent", "close", () => { foreach (var x in sel) Places.ForgetRecent(x.FullPath); Reload(false); }));
            if (canChange)
            {
                items.Add(new Separator());
                items.Add(Item("Move to Trash", "trash", () => _ = Delete(permanently: false), "Delete"));
            }
            items.Add(new Separator());
            items.Add(Item("Properties", "info", () => _ = ShowProperties(sel[0]), "Alt+Enter", one != null));
        }
        menu.Open(FileList);
        e.Handled = true;
    }

    // ═════════════════════════════════ actions ═════════════════════════════════
    private void ClipSelection(bool cut)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (InArchive) cut = false;                       // files inside an archive view are copied out, never moved
        FileClipboard.Paths = sel.Select(x => x.FullPath).ToList();
        FileClipboard.IsCut = cut;
        _ = WriteSystemClipboard(FileClipboard.Paths, cut);
        StatusLabel.Text = $"{sel.Count} item{(sel.Count == 1 ? "" : "s")} {(cut ? "cut" : "copied")} — paste with Ctrl+V";
    }

    private async Task Paste(string into)
    {
        if (IsReadOnlyFolder(into)) return;
        if (FileClipboard.Paths.Count == 0) await ReadSystemClipboard();
        if (FileClipboard.Paths.Count == 0) return;
        await Transfer(FileClipboard.Paths.ToList(), into, move: FileClipboard.IsCut);
    }

    private async Task Duplicate(List<FileEntry> sel)
    {
        foreach (var g in sel.GroupBy(s => Path.GetDirectoryName(s.FullPath)!))
            await Transfer(g.Select(s => s.FullPath).ToList(), g.Key, move: false);
    }

    private async Task Delete(bool permanently)
    {
        var sel = Selected();
        if (sel.Count == 0 || InArchive) return;
        permanently |= InTrash;
        var what = sel.Count == 1 ? $"“{sel[0].Name}”" : $"{sel.Count} items";
        var r = await Dialogs.Ask(this, permanently ? "Delete permanently" : "Move to Trash",
            permanently ? $"Permanently delete {what}? This can't be undone." : $"Move {what} to the Trash? You can restore it from there.",
            permanently ? "Delete" : "Move to Trash", "Cancel");
        if (r != 0) return;
        var errors = new List<string>();
        var trashed = new List<string>();
        foreach (var item in sel)
        {
            try
            {
                if (InTrash) FileOps.DeleteFromTrash(item.Name);
                else if (permanently) { if (item.IsDir) Directory.Delete(item.FullPath, true); else File.Delete(item.FullPath); }
                else trashed.Add(FileOps.MoveToTrash(item.FullPath));
            }
            catch (Exception ex) { errors.Add($"{item.Name}: {ex.Message}"); }
        }
        if (trashed.Count > 0)
            FileTransfer.Record((trashed.Count == 1 ? $"Move “{sel[0].Name}”" : $"Move {trashed.Count} items") + " to the Trash",
                () => { foreach (var t in trashed) try { FileOps.RestoreFromTrash(t); } catch { } });
        Reload(keepSelection: false);
        BuildSidebar();
        if (errors.Count > 0) await Dialogs.ShowMessage(this, "Delete", string.Join("\n", errors.Take(5)));
    }

    private async Task Restore(List<FileEntry> sel)
    {
        var errors = new List<string>();
        var restored = new List<string>();
        foreach (var item in sel)
            try { restored.Add(FileOps.RestoreFromTrash(item.Name)); }
            catch (Exception ex) { errors.Add($"{item.Name}: {ex.Message}"); }
        if (restored.Count > 0)
            FileTransfer.Record(restored.Count == 1 ? $"Restore “{Path.GetFileName(restored[0])}”" : $"Restore {restored.Count} items",
                () => { foreach (var r in restored) try { FileOps.MoveToTrash(r); } catch { } });
        Reload(keepSelection: false);
        BuildSidebar();
        if (errors.Count > 0) await Dialogs.ShowMessage(this, "Restore", string.Join("\n", errors.Take(5)));
        else Notifier.Show("Restored", sel.Count == 1 ? $"“{sel[0].Name}” is back where it was." : $"{sel.Count} items are back where they were.", "files");
    }

    private void OnRestore(object? s, RoutedEventArgs e) => _ = Restore(Selected());

    private async void OnEmptyTrash(object? s, RoutedEventArgs e)
    {
        int n = FileOps.TrashCount();
        if (n == 0) return;
        var r = await Dialogs.Ask(this, "Empty Trash", $"Permanently delete the {n} item{(n == 1 ? "" : "s")} in the Trash? This can't be undone.", "Empty Trash", "Cancel");
        if (r != 0) return;
        FileOps.EmptyTrash();
        Reload(false);
        BuildSidebar();
    }

    private void UpdateTrashBar()
    {
        int n = _all.Count;
        TrashText.Text = n == 0 ? "The Trash is empty." : $"{n} item{(n == 1 ? "" : "s")} in the Trash. Select items to restore them where they were.";
    }

    private async Task Rename()
    {
        var sel = Selected();
        if (InTrash || InArchive) return;
        if (sel.Count > 1) { await BatchRenameWindow.Run(this, sel.Select(x => x.FullPath).ToList()); Reload(keepSelection: false); return; }
        if (sel.Count != 1) return;
        var item = sel[0];
        var name = await Dialogs.Prompt(this, "Rename", "New name:", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        name = name.Trim();
        if (name.Contains('/')) { await Dialogs.ShowMessage(this, "Rename", "A name can't contain “/”."); return; }
        var target = Path.Combine(Path.GetDirectoryName(item.FullPath)!, name);
        try
        {
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Something with that name already exists here.");
            if (item.IsDir) Directory.Move(item.FullPath, target); else File.Move(item.FullPath, target);
            var (from, to, dir) = (item.FullPath, target, item.IsDir);
            Places.Moved(from, to);
            FileTransfer.Record($"Rename to “{name}”", () =>
            {
                if (File.Exists(from) || Directory.Exists(from)) return;
                if (dir) Directory.Move(to, from); else File.Move(to, from);
                Places.Moved(to, from);
            });
            Reload(keepSelection: false);
            if ((FileList.ItemsSource as List<FileEntry>)?.FirstOrDefault(x => x.FullPath == target) is { } renamed) FileList.SelectedItem = renamed;
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Rename", ex.Message); }
    }

    private async Task NewFolder()
    {
        if (ReadOnlyPlace) return;
        var name = await Dialogs.Prompt(this, "New folder", "Folder name:", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var p = FileOps.UniquePath(_current, name.Trim());
            Directory.CreateDirectory(p);
            FileTransfer.Record($"New folder “{Path.GetFileName(p)}”", () => { if (Directory.Exists(p)) FileOps.MoveToTrash(p); });
            Reload(false);
            if ((FileList.ItemsSource as List<FileEntry>)?.FirstOrDefault(x => x.FullPath == p) is { } made) { FileList.SelectedItem = made; FileList.ScrollIntoView(made); }
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "New folder", ex.Message); }
    }

    private async Task NewTextFile()
    {
        if (ReadOnlyPlace) return;
        var name = await Dialogs.Prompt(this, "New text document", "File name:", "New document.txt");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var p = FileOps.UniquePath(_current, name.Trim());
            await File.WriteAllTextAsync(p, "");
            FileTransfer.Record($"New file “{Path.GetFileName(p)}”", () => { if (File.Exists(p)) FileOps.MoveToTrash(p); });
            Reload(false);
            if ((FileList.ItemsSource as List<FileEntry>)?.FirstOrDefault(x => x.FullPath == p) is { } made) { FileList.SelectedItem = made; FileList.ScrollIntoView(made); }
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "New text document", ex.Message); }
    }

    private void ToggleHidden()
    {
        SettingsStore.Current.FilesShowHidden = !SettingsStore.Current.FilesShowHidden;
        SettingsStore.Save();
        Reload(keepSelection: true);
        StatusLabel.Text = SettingsStore.Current.FilesShowHidden ? "Showing hidden files (Ctrl+H to hide them)" : "Hidden files are hidden";
    }

    // ═════════════════════════════════ properties ═════════════════════════════════
    private async Task ShowProperties(FileEntry entry)
    {
        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions() };
        int r = 0;
        TextBlock Add(string key, string value)
        {
            rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = key, Opacity = 0.65, Margin = new Thickness(0, 4) };
            var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4) };
            Grid.SetRow(k, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            rows.Children.Add(k); rows.Children.Add(v);
            r++;
            return v;
        }
        Add("Kind", entry.Kind);
        var sizeBox = Add("Size", entry.IsDir ? "Counting…" : $"{FileOps.FormatSize(entry.Size)} ({entry.Size:N0} bytes)");
        if (InTrash)
        {
            var (origin, deleted) = FileOps.TrashOrigin(entry.Name);
            Add("Deleted from", origin ?? "unknown");
            if (deleted is { } d) Add("Deleted", d.ToString("f"));
        }
        else Add("Location", Path.GetDirectoryName(entry.FullPath) ?? "/");
        try
        {
            Add("Modified", File.GetLastWriteTime(entry.FullPath).ToString("f"));
            Add("Created", File.GetCreationTime(entry.FullPath).ToString("f"));
            var mode = File.GetUnixFileMode(entry.FullPath);
            string Bits(UnixFileMode rr, UnixFileMode w, UnixFileMode x) => $"{((mode & rr) != 0 ? "r" : "-")}{((mode & w) != 0 ? "w" : "-")}{((mode & x) != 0 ? "x" : "-")}";
            Add("Permissions", Bits(UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute) + " "
                               + Bits(UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute) + " "
                               + Bits(UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute) + "  (you · group · everyone)");
        }
        catch { }

        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 14),
            Children =
            {
                entry.Thumb != null ? new Image { Source = entry.Thumb, Width = 48, Height = 48 } : (Control)new IconConverter(null, 48).Convert(entry.IconKey, typeof(object), "48", System.Globalization.CultureInfo.InvariantCulture)!,
                new SelectableTextBlock { Text = entry.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 }
            }
        };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 6), IsDefault = true, IsCancel = true };
        var dlg = new Window
        {
            Title = "Properties — " + entry.Name, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Children = { head, rows, close } }
        };
        close.Click += (_, _) => dlg.Close();

        var cts = new CancellationTokenSource();
        dlg.Closed += (_, _) => cts.Cancel();
        if (entry.IsDir)
            _ = Task.Run(() =>
            {
                long bytes = 0; int files = 0, dirs = 0;
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                    foreach (var fi in new DirectoryInfo(entry.FullPath).EnumerateFileSystemInfos("*", opts))
                    {
                        if (cts.IsCancellationRequested) return;
                        if (fi is FileInfo f) { bytes += f.Length; files++; } else dirs++;
                        if ((files + dirs) % 2000 == 0) { var (b, fl, d) = (bytes, files, dirs); Dispatcher.UIThread.Post(() => sizeBox.Text = $"{FileOps.FormatSize(b)} so far — {fl:N0} files, {d:N0} folders…"); }
                    }
                }
                catch { }
                Dispatcher.UIThread.Post(() => sizeBox.Text = $"{FileOps.FormatSize(bytes)} — {files:N0} files, {dirs:N0} folders");
            }, cts.Token);
        await dlg.ShowDialog(this);
    }

    // ═════════════════════════════════ keyboard ═════════════════════════════════
    private void OnKeys(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // shortcuts that work everywhere in the window, even while typing in the search box
        if (ctrl && e.Key == Key.L) { EditPath(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (alt && e.Key == Key.Left) { OnBack(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (alt && e.Key == Key.Right) { OnForward(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (alt && e.Key == Key.Up) { OnUp(null, new RoutedEventArgs()); e.Handled = true; return; }
        if (ctrl && e.Key == Key.H) { ToggleHidden(); e.Handled = true; return; }
        if (ctrl && e.Key is Key.D1 or Key.NumPad1) { SetView(false); e.Handled = true; return; }
        if (ctrl && e.Key is Key.D2 or Key.NumPad2) { SetView(true); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.N) { _ = NewFolder(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.T) { NewTab(_current); e.Handled = true; return; }
        if (ctrl && e.Key == Key.W) { CloseTab(_activeTab); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Tab) { SwitchTab((_activeTab + (shift ? -1 : 1) + _tabs.Count) % _tabs.Count); e.Handled = true; return; }
        if (ctrl && e.Key == Key.PageDown) { SwitchTab((_activeTab + 1) % _tabs.Count); e.Handled = true; return; }
        if (ctrl && e.Key == Key.PageUp) { SwitchTab((_activeTab - 1 + _tabs.Count) % _tabs.Count); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z && e.Source is not TextBox) { DoUndo(); e.Handled = true; return; }
        if (e.Key == Key.F9) { ToggleDetails(); e.Handled = true; return; }
        if (e.Key == Key.F5) { _deepResults = false; Reload(true); BuildSidebar(); UpdateFreeSpace(); e.Handled = true; }
    }

    private void OnListKeys(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key == Key.Space && !ctrl && !alt) { TogglePreview(); e.Handled = true; return; }
        if (_previewOpen)
        {
            if (e.Key == Key.Escape) { ClosePreview(); e.Handled = true; return; }
            if (e.Key == Key.Enter && _previewEntry is { } pe) { ClosePreview(); if (InTrash) _ = Restore(new List<FileEntry> { pe }); else OpenEntry(pe); e.Handled = true; return; }
            // arrow keys move through the list and the preview follows (SelectionChanged)
        }
        if (ctrl && e.Key == Key.Enter)
        {
            foreach (var d in Selected().Where(x => x.IsDir).Take(8)) NewTab(d.FullPath, activate: false);
            e.Handled = true; return;
        }
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C: ClipSelection(false); e.Handled = true; break;
                case Key.X: ClipSelection(true); e.Handled = true; break;
                case Key.V: _ = Paste(_current); e.Handled = true; break;
                case Key.A: FileList.SelectAll(); e.Handled = true; break;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.Delete: _ = Delete(permanently: shift); e.Handled = true; break;
            case Key.F2: _ = Rename(); e.Handled = true; break;
            case Key.Back: OnUp(null, new RoutedEventArgs()); e.Handled = true; break;
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = ""; else FileList.SelectedItems?.Clear();
                e.Handled = true; break;
            case Key.Enter:
                if (alt) { if (Selected().FirstOrDefault() is { } p) _ = ShowProperties(p); }
                else foreach (var x in Selected().Take(10)) OpenEntry(x);
                e.Handled = true; break;
            default:
                // type a few letters to jump to a name
                if (e.KeyModifiers == KeyModifiers.None && e.Key >= Key.A && e.Key <= Key.Z)
                {
                    if ((DateTime.Now - _typeAheadAt).TotalMilliseconds > 900) _typeAhead = "";
                    _typeAheadAt = DateTime.Now;
                    _typeAhead += e.Key.ToString().ToLowerInvariant();
                    if ((FileList.ItemsSource as List<FileEntry>)?.FirstOrDefault(x => x.Name.TrimStart('.').StartsWith(_typeAhead, StringComparison.OrdinalIgnoreCase)) is { } hit)
                    {
                        FileList.SelectedItem = hit;
                        FileList.ScrollIntoView(hit);
                    }
                    e.Handled = true;
                }
                break;
        }
    }
}
