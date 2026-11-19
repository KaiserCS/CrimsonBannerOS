using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// The Open / Save / Choose-folder dialog used by every Red Star app (see <see cref="FileDialogs"/>): Files' sidebar,
/// path bar and list in a dialog.
/// </summary>
public sealed class FileChooserWindow : Window
{
    public enum Mode { Open, Save, Folder }

    public sealed record Filter(string Name, string[] Patterns)
    {
        private readonly Regex[] _rx = Patterns.Where(p => p != "*" && p != "*.*")
            .Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
        public bool All => _rx.Length == 0;
        public bool Matches(string name) => All || _rx.Any(r => r.IsMatch(name));
        /// <summary>".docx" for "*.docx" (the extension a saved file gets), or null.</summary>
        public string? Extension => Patterns.Select(p => p.StartsWith("*.") && p.IndexOf('*', 2) < 0 && !p.Contains('?') ? p[1..] : null).FirstOrDefault(e => e != null);
        public override string ToString() => All ? Name : $"{Name} ({string.Join(", ", Patterns.Take(4))}{(Patterns.Length > 4 ? ", …" : "")})";
    }

    private sealed class Row
    {
        public string Name = "", FullPath = "", IconKey = "", Kind = "", SizeText = "", DateText = "";
        public bool IsDir;
        public long Size;
        public DateTime Modified;
        public double Dim => IsDir || Matches ? 1 : 0.4;
        public bool Matches = true;
    }

    private readonly Mode _mode;
    private readonly List<Filter> _filters;
    private string _current;
    private readonly Stack<string> _back = new();
    private readonly StackPanel _sidebar = new() { Margin = new Thickness(8, 10, 8, 10) };
    private readonly List<(Button Btn, string Path)> _nav = new();
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _pathBox = new() { IsVisible = false, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox _search = new() { PlaceholderText = "Search", Width = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListBox _list = new() { Background = Brushes.Transparent, Margin = new Thickness(10, 0, 10, 6) };
    private readonly TextBlock _empty = new() { Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, IsHitTestVisible = false };
    private readonly TextBox _name = new() { PlaceholderText = "File name" };
    private readonly ComboBox _filter = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _ok = new() { Classes = { "primary" }, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _up = new() { Classes = { "tool" } };
    private readonly Button _backBtn = new() { Classes = { "tool" } };
    private readonly TextBlock _hint = new() { Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private List<Row> _rows = new();
    private string _sort = "name";
    private bool _asc = true;

    public bool AllowMultiple { get; init; }
    public string SuggestedName { get; init; } = "";
    public string? DefaultExtension { get; init; }
    public bool ConfirmOverwrite { get; init; } = true;

    /// <summary>The chosen paths (empty when cancelled).</summary>
    public List<string> Result { get; } = new();

    public FileChooserWindow(Mode mode, string title, string start, List<Filter> filters)
    {
        _mode = mode;
        _filters = filters;
        _current = start;
        Title = title;
        Width = 900; Height = 580; MinWidth = 640; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Styles.Add(ButtonStyle("tool", new Thickness(8), 8, minSize: 34));
        Styles.Add(ButtonStyle("crumb", new Thickness(8, 4), 6));
        Styles.Add(ButtonStyle("nav", new Thickness(10, 7), 8, stretch: true));
        Styles.Add(new Style(x => x.OfType<Button>().Class("nav").Class("current")) { Setters = { new Setter(Button.BackgroundProperty, Application.Current?.FindResource("ShellAccentSoftBrush") ?? Brushes.LightGray) } });
        Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 2)), new Setter(ListBoxItem.MarginProperty, new Thickness(0, 1)), new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(8)) } });

        Content = Build();
        BuildSidebar();
        Navigate(start, record: false);

        KeyDown += OnKeys;
        Opened += (_, _) =>
        {
            // AllowMultiple, SuggestedName and DefaultExtension are set after the constructor
            _name.Text = SaveName(SuggestedNameOrDefault());
            ShowRows();
            if (_mode == Mode.Save)
            {
                _name.Focus();
                // select the name without the extension, like Files' rename
                var dot = _name.Text?.LastIndexOf('.') ?? -1;
                _name.SelectionStart = 0;
                _name.SelectionEnd = dot > 0 ? dot : _name.Text?.Length ?? 0;
            }
            else _list.Focus();
        };
    }

    private static Style ButtonStyle(string cls, Thickness pad, double radius, double minSize = 0, bool stretch = false)
    {
        var s = new Style(x => x.OfType<Button>().Class(cls))
        {
            Setters =
            {
                new Setter(Button.BackgroundProperty, Brushes.Transparent),
                new Setter(Button.BorderThicknessProperty, new Thickness(0)),
                new Setter(Button.PaddingProperty, pad),
                new Setter(Button.CornerRadiusProperty, new CornerRadius(radius)),
            }
        };
        if (minSize > 0) { s.Setters.Add(new Setter(Button.MinWidthProperty, minSize)); s.Setters.Add(new Setter(Button.MinHeightProperty, minSize)); }
        if (stretch)
        {
            s.Setters.Add(new Setter(Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            s.Setters.Add(new Setter(Button.MarginProperty, new Thickness(0, 1)));
        }
        return s;
    }

    private static IBrush? Res(string key) => Application.Current?.FindResource(key) as IBrush;

    // ═════════════════════════════════ layout ═════════════════════════════════
    private Control Build()
    {
        // toolbar: back · up · path bar · new folder · search
        _backBtn.Content = Icons.Create("arrow-left", 18); ToolTip.SetTip(_backBtn, "Back (Alt+←)");
        _up.Content = Icons.Create("arrow-up", 18); ToolTip.SetTip(_up, "Up one folder (Alt+↑)");
        _backBtn.Click += (_, _) => { if (_back.Count > 0) Navigate(_back.Pop(), record: false); };
        _up.Click += (_, _) => GoUp();
        var newFolder = new Button { Classes = { "tool" }, Content = Icons.Create("folder-plus", 18), IsVisible = _mode != Mode.Open };
        ToolTip.SetTip(newFolder, "New folder (Ctrl+Shift+N)");
        newFolder.Click += async (_, _) => await NewFolder();
        var hidden = new Button { Classes = { "tool" }, Content = Icons.Create("eye", 18) };
        ToolTip.SetTip(hidden, "Show hidden files (Ctrl+H)");
        hidden.Click += (_, _) => ToggleHidden();

        var crumbScroll = new ScrollViewer { Content = _crumbs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var pathFrame = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(4, 2), MinHeight = 34, BorderThickness = new Thickness(1),
            Background = Res("ShellCardBrush"), BorderBrush = Res("ShellBorderBrush"), Margin = new Thickness(6, 0),
            Child = new Panel { Children = { crumbScroll, _pathBox } }
        };
        ToolTip.SetTip(pathFrame, "Click an empty spot (or Ctrl+L) to type a location");
        pathFrame.PointerPressed += (_, e) => { if ((e.Source as Visual)?.FindAncestorOfType<Button>(true) == null && !_pathBox.IsVisible) EditPath(); };
        _pathBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; TypedPath(_pathBox.Text ?? ""); }
            else if (e.Key == Key.Escape) { e.Handled = true; EndEditPath(); }
        };
        _pathBox.LostFocus += (_, _) => EndEditPath();
        _search.TextChanged += (_, _) => ShowRows();

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"), Margin = new Thickness(10, 8) };
        toolbar.Children.Add(_backBtn);
        Grid.SetColumn(_up, 1); toolbar.Children.Add(_up);
        Grid.SetColumn(pathFrame, 2); toolbar.Children.Add(pathFrame);
        Grid.SetColumn(newFolder, 3); toolbar.Children.Add(newFolder);
        Grid.SetColumn(hidden, 4); toolbar.Children.Add(hidden);
        _search.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(_search, 5); toolbar.Children.Add(_search);
        var toolbarBorder = new Border { Child = toolbar, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Res("ShellBorderBrush") };

        // list: header + rows
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*,150,90,150"), Margin = new Thickness(18, 8, 18, 2) };
        void Head(int col, string text, string key)
        {
            var b = new Button { Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 4), FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => { if (_sort == key) _asc = !_asc; else { _sort = key; _asc = key == "name"; } ShowRows(); };
            Grid.SetColumn(b, col);
            header.Children.Add(b);
        }
        Head(1, "Name", "name"); Head(2, "Kind", "kind"); Head(3, "Size", "size"); Head(4, "Modified", "date");

        _list.ItemTemplate = new FuncDataTemplate<Row>((r, _) =>
        {
            if (r == null) return new TextBlock();
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("36,*,150,90,150"), MinHeight = 32, Opacity = r.Dim };
            g.Children.Add(new ContentControl { Content = IconConverter.Default.Convert(r.IconKey, typeof(object), "20", CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center });
            var cells = new (int Col, string Text, bool Caption, HorizontalAlignment Align)[]
            {
                (1, r.Name, false, HorizontalAlignment.Left), (2, r.Kind, true, HorizontalAlignment.Left),
                (3, r.SizeText, true, HorizontalAlignment.Right), (4, r.DateText, true, HorizontalAlignment.Left)
            };
            foreach (var (col, text, caption, align) in cells)
            {
                var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = align, Margin = col == 2 ? new Thickness(8, 0) : col == 3 ? new Thickness(0, 0, 16, 0) : default };
                if (caption) t.Classes.Add("caption");
                Grid.SetColumn(t, col);
                g.Children.Add(t);
            }
            return g;
        });
        _list.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is Row r) Activate(r);
        };
        _list.SelectionChanged += (_, _) => OnSelection();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is Row r && _list.SelectedItems?.Count == 1) { e.Handled = true; Activate(r); }
            else if (e.Key == Key.Enter && (_list.SelectedItems?.Count ?? 0) > 1) { e.Handled = true; _ = Accept(); }
            else if (e.Key == Key.Back) { e.Handled = true; GoUp(); }
        };

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        content.Children.Add(header);
        var listPanel = new Panel { Children = { _list, _empty } };
        Grid.SetRow(listPanel, 1);
        content.Children.Add(listPanel);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("210,1,*") };
        body.Children.Add(new ScrollViewer { Content = _sidebar, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var line = new Border { Background = Res("ShellBorderBrush") };
        Grid.SetColumn(line, 1); body.Children.Add(line);
        Grid.SetColumn(content, 2); body.Children.Add(content);

        // bottom: name (save) · filter · buttons
        foreach (var f in _filters) _filter.Items.Add(f.ToString());
        _filter.SelectedIndex = 0;
        _filter.IsVisible = _mode != Mode.Folder && !(_filters.Count == 1 && _filters[0].All);
        _filter.SelectionChanged += (_, _) => { if (_mode == Mode.Save) SwapExtension(); ShowRows(); };
        _name.IsVisible = _mode == Mode.Save;
        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = Accept(); } };
        _name.TextChanged += (_, _) => UpdateOk();
        _ok.Content = _mode switch { Mode.Save => "Save", Mode.Folder => "Select folder", _ => "Open" };
        _ok.Click += async (_, _) => await Accept();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();

        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(14, 10) };
        if (_mode == Mode.Save)
        {
            bottom.Children.Add(_name);
        }
        else bottom.Children.Add(_hint);
        _filter.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_filter, 1); bottom.Children.Add(_filter);
        _ok.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_ok, 2); bottom.Children.Add(_ok);
        Grid.SetColumn(cancel, 3); bottom.Children.Add(cancel);
        var bottomBorder = new Border { Child = bottom, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Res("ShellBorderBrush") };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(toolbarBorder);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(bottomBorder, 2); root.Children.Add(bottomBorder);
        return root;
    }

    private string SuggestedNameOrDefault() =>
        string.IsNullOrWhiteSpace(SuggestedName) ? "Untitled" : SuggestedName;

    // ═════════════════════════════════ sidebar (the same places as Files) ═════════════════════════════════
    private void BuildSidebar()
    {
        _sidebar.Children.Clear();
        _nav.Clear();
        Section("PLACES");
        Nav("home", "Home", AppPaths.Home);
        if (_mode != Mode.Folder)
        {
            Nav("clock", "Recent", Places.Recent);
        }
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
            foreach (var p in pins) Nav("pin", Path.GetFileName(p.TrimEnd('/')) is { Length: > 0 } n ? n : p, p);
        }
        var drives = FileManagerWindow.Drives();
        if (drives.Count > 0)
        {
            Section("DEVICES");
            foreach (var (mount, label, _, _) in drives) Nav("drive", label, mount);
        }
        var servers = RemoteMounts.Servers();
        if (servers.Count > 0)
        {
            Section("NETWORK");
            foreach (var m in servers) Nav("server", m.Label, m.Path);
        }
        if (Directory.Exists(RemoteMounts.CloudDir))
        {
            var clouds = Directory.GetDirectories(RemoteMounts.CloudDir).Where(IsMounted).ToList();
            if (clouds.Count > 0)
            {
                Section("CLOUD");
                foreach (var c in clouds) Nav("cloud", Path.GetFileName(c), c);
            }
        }
        Highlight();
    }

    private static bool IsMounted(string dir)
    {
        try { return File.ReadLines("/proc/mounts").Any(l => l.Split(' ') is { Length: > 2 } f && f[1].Replace("\\040", " ") == dir); }
        catch { return false; }
    }

    private void Section(string title) =>
        _sidebar.Children.Add(new TextBlock { Text = title, Classes = { "eyebrow" }, Margin = new Thickness(10, _sidebar.Children.Count == 0 ? 2 : 14, 0, 4) });

    private void Nav(string icon, string name, string path)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { Icons.Create(icon, 17), new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 } }
        };
        var b = new Button { Content = line, Classes = { "nav" } };
        ToolTip.SetTip(b, Places.IsVirtual(path) ? name : path);
        b.Click += (_, _) => Navigate(path, record: true);
        _sidebar.Children.Add(b);
        _nav.Add((b, path));
    }

    private void Highlight()
    {
        foreach (var (b, p) in _nav) b.Classes.Set("current", p.TrimEnd('/') == _current.TrimEnd('/'));
    }

    // ═════════════════════════════════ navigation ═════════════════════════════════
    private void Navigate(string path, bool record)
    {
        if (!Places.IsVirtual(path))
        {
            try { path = Path.GetFullPath(path); } catch { return; }
            if (!Directory.Exists(path)) return;
        }
        if (record && path != _current) _back.Push(_current);
        _current = path;
        _search.Text = "";
        _search.PlaceholderText = "Search in " + Label(path);
        BuildCrumbs();
        Highlight();
        Reload();
        _backBtn.IsEnabled = _back.Count > 0;
        _up.IsEnabled = !Places.IsVirtual(path) && Directory.GetParent(path) != null;
    }

    private void GoUp()
    {
        if (Places.IsVirtual(_current)) return;
        if (Directory.GetParent(_current) is { } parent) Navigate(parent.FullName, record: true);
    }

    private static string Label(string path)
    {
        if (Places.IsVirtual(path)) return Places.Title(path);
        var t = path.TrimEnd('/');
        if (t.Length == 0) return "File System";
        if (t == AppPaths.Home.TrimEnd('/')) return "Home";
        return Path.GetFileName(t);
    }

    private void BuildCrumbs()
    {
        _crumbs.Children.Clear();
        var parts = new List<(string Label, string Path, string? Icon)>();
        var path = _current.TrimEnd('/');
        var home = AppPaths.Home.TrimEnd('/');
        if (Places.IsVirtual(_current)) parts.Add((Places.Title(_current), _current, _current == Places.Recent ? "clock" : "star"));
        else if (path == home || path.StartsWith(home + "/"))
        {
            parts.Add(("Home", home, "home"));
            var acc = home;
            foreach (var seg in path[home.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)) { acc += "/" + seg; parts.Add((seg, acc, null)); }
        }
        else
        {
            parts.Add(("File System", "/", "drive"));
            var acc = "";
            foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { acc += "/" + seg; parts.Add((seg, acc, null)); }
        }
        for (int i = 0; i < parts.Count; i++)
        {
            var (label, p, icon) = parts[i];
            if (i > 0) _crumbs.Children.Add(new TextBlock { Text = "›", Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0) });
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (icon != null) content.Children.Add(Icons.Create(icon, 15));
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = i == parts.Count - 1 ? FontWeight.SemiBold : FontWeight.Normal });
            var b = new Button { Classes = { "crumb" }, Content = content };
            var target = p;
            b.Click += (_, _) => Navigate(target, record: true);
            _crumbs.Children.Add(b);
        }
    }

    private void EditPath()
    {
        if (Places.IsVirtual(_current)) _pathBox.Text = "";
        else _pathBox.Text = _current;
        _pathBox.IsVisible = true;
        _crumbs.IsVisible = false;
        _pathBox.Focus();
        _pathBox.SelectAll();
    }

    private void EndEditPath()
    {
        _pathBox.IsVisible = false;
        _crumbs.IsVisible = true;
    }

    /// <summary>A typed location: a folder opens; a file is chosen (open) or becomes the name (save).</summary>
    private void TypedPath(string text)
    {
        text = text.Trim();
        if (text.StartsWith("~")) text = AppPaths.Home + text[1..];
        if (text.StartsWith("file://")) try { text = new Uri(text).LocalPath; } catch { }
        if (!text.StartsWith('/')) text = Path.Combine(Places.IsVirtual(_current) ? AppPaths.Home : _current, text);
        EndEditPath();
        if (Directory.Exists(text)) { Navigate(text, record: true); _list.Focus(); return; }
        var dir = Path.GetDirectoryName(text);
        if (dir != null && Directory.Exists(dir))
        {
            Navigate(dir, record: true);
            if (_mode == Mode.Save) { _name.Text = Path.GetFileName(text); _ = Accept(); }
            else if (File.Exists(text)) { Result.Add(text); FileDialogs.Remember(dir); Close(); }
        }
    }

    private void ToggleHidden()
    {
        SettingsStore.Current.FilesShowHidden = !SettingsStore.Current.FilesShowHidden;
        SettingsStore.Save();
        Reload();
    }

    // ═════════════════════════════════ listing ═════════════════════════════════
    private Filter CurrentFilter => _filters.Count > 0 ? _filters[Math.Clamp(_filter.SelectedIndex, 0, _filters.Count - 1)] : new Filter("All files", new[] { "*" });

    private static Row MakeRow(string full, bool dir, long size, DateTime modified, string? kind = null)
    {
        var icon = FileOps.IconName(full, dir);
        return new Row
        {
            Name = Path.GetFileName(full.TrimEnd('/')), FullPath = full, IsDir = dir, Size = size, Modified = modified,
            IconKey = icon + "|" + FileManagerWindow.ColorFor(icon),
            Kind = kind ?? FileOps.KindOf(full, dir),
            SizeText = dir ? "" : FileOps.FormatSize(size),
            DateText = modified == DateTime.MinValue ? "" : modified.Date == DateTime.Today ? "Today " + modified.ToString("HH:mm") : modified.ToString("d MMM yyyy"),
        };
    }

    private void Reload()
    {
        var rows = new List<Row>();
        bool hidden = SettingsStore.Current.FilesShowHidden;
        try
        {
            if (_current == Places.Recent)
            {
                foreach (var (p, when) in Places.RecentFiles())
                {
                    long size = 0; try { size = new FileInfo(p).Length; } catch { }
                    rows.Add(MakeRow(p, false, size, when, "in " + ShortDir(Path.GetDirectoryName(p))));
                }
            }
            else if (_current == Places.Starred)
            {
                foreach (var p in Places.StarredItems())
                {
                    bool dir = Directory.Exists(p);
                    if (_mode == Mode.Folder && !dir) continue;
                    rows.Add(MakeRow(p, dir, dir ? 0 : new FileInfo(p).Length, dir ? Directory.GetLastWriteTime(p) : File.GetLastWriteTime(p)));
                }
            }
            else
            {
                var info = new DirectoryInfo(_current);
                foreach (var d in info.EnumerateDirectories())
                {
                    if (!hidden && d.Name.StartsWith('.')) continue;
                    rows.Add(MakeRow(d.FullName, true, 0, d.LastWriteTime));
                }
                if (_mode != Mode.Folder)
                    foreach (var f in info.EnumerateFiles())
                    {
                        if (!hidden && f.Name.StartsWith('.')) continue;
                        rows.Add(MakeRow(f.FullName, false, f.Length, f.LastWriteTime));
                    }
            }
            _empty.Text = "";
        }
        catch (UnauthorizedAccessException) { _empty.Text = "You don't have permission to look in this folder."; }
        catch (Exception ex) { _empty.Text = ex.Message; }
        _rows = rows;
        ShowRows();
    }

    private static string ShortDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        var home = AppPaths.Home.TrimEnd('/');
        return dir == home ? "Home" : dir.StartsWith(home + "/") ? "~" + dir[home.Length..] : dir;
    }

    private void ShowRows()
    {
        var f = CurrentFilter;
        var q = (_search.Text ?? "").Trim();
        IEnumerable<Row> src = _rows;
        foreach (var r in _rows) r.Matches = r.IsDir || f.Matches(r.Name);
        // files of other kinds are left out (folders always show), like other file dialogs
        src = src.Where(r => r.IsDir || r.Matches);
        if (q.Length > 0) src = src.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        Func<Row, object> key = _sort switch
        {
            "size" => r => r.Size,
            "date" => r => r.Modified,
            "kind" => r => r.Kind,
            _ => r => r.Name.TrimStart('.').ToLowerInvariant()
        };
        var ordered = _current == Places.Recent && _sort == "name" && q.Length == 0
            ? src.ToList()                                                         // Recent: newest first
            : (_asc ? src.OrderByDescending(r => r.IsDir).ThenBy(key) : src.OrderByDescending(r => r.IsDir).ThenByDescending(key)).ToList();
        _list.SelectionMode = AllowMultiple && _mode == Mode.Open ? SelectionMode.Multiple : SelectionMode.Single;
        _list.ItemsSource = ordered;
        _empty.IsVisible = ordered.Count == 0;
        if (ordered.Count == 0 && _empty.Text is null or "")
            _empty.Text = q.Length > 0 ? $"Nothing called “{q}” here." : _current == Places.Recent ? "No recent files." : _current == Places.Starred ? "Nothing starred." : _mode == Mode.Folder ? "No folders here." : "This folder is empty.";
        int files = ordered.Count(r => !r.IsDir), dirs = ordered.Count - files;
        _hint.Text = _mode == Mode.Folder ? $"Choosing: {Label(_current)}" : $"{dirs} folder{(dirs == 1 ? "" : "s")}, {files} file{(files == 1 ? "" : "s")}" + (f.All ? "" : $" · showing {f.Name.ToLowerInvariant()}");
        UpdateOk();
    }

    // ═════════════════════════════════ choosing ═════════════════════════════════
    private List<Row> SelectedRows() => _list.SelectedItems?.OfType<Row>().ToList() ?? new List<Row>();

    private void OnSelection()
    {
        var sel = SelectedRows();
        if (_mode == Mode.Save && sel.Count == 1 && !sel[0].IsDir) _name.Text = sel[0].Name;
        if (_mode == Mode.Folder) _hint.Text = "Choosing: " + (sel.Count == 1 ? sel[0].Name : Label(_current));
        UpdateOk();
    }

    private void UpdateOk()
    {
        var sel = SelectedRows();
        _ok.IsEnabled = _mode switch
        {
            Mode.Save => !string.IsNullOrWhiteSpace(_name.Text) && !Places.IsVirtual(_current),
            Mode.Folder => sel.Count == 1 || !Places.IsVirtual(_current),
            _ => sel.Count > 0,
        };
        if (_mode == Mode.Open) _ok.Content = sel.Count == 1 && sel[0].IsDir ? "Open folder" : "Open";
    }

    private void Activate(Row r)
    {
        if (r.IsDir) { Navigate(r.FullPath, record: true); return; }
        if (_mode == Mode.Open) { Choose(new List<string> { r.FullPath }); return; }
        if (_mode == Mode.Save) { _name.Text = r.Name; _ = Accept(); }
    }

    private void Choose(List<string> paths)
    {
        Result.Clear();
        Result.AddRange(paths);
        if (paths.Count > 0) FileDialogs.Remember(Path.GetDirectoryName(paths[0].TrimEnd('/')) ?? _current);
        Close();
    }

    private async Task Accept()
    {
        var sel = SelectedRows();
        switch (_mode)
        {
            case Mode.Open:
                if (sel.Count == 1 && sel[0].IsDir) { Navigate(sel[0].FullPath, record: true); return; }
                var files = sel.Where(r => !r.IsDir).Select(r => r.FullPath).ToList();
                if (files.Count > 0) Choose(files);
                return;
            case Mode.Folder:
                Choose(new List<string> { sel.Count == 1 && sel[0].IsDir ? sel[0].FullPath : _current });
                return;
            case Mode.Save:
                var name = (_name.Text ?? "").Trim();
                if (name.Length == 0 || Places.IsVirtual(_current)) return;
                if (name.Contains('/'))
                {
                    // a path typed into the name box
                    var full = name.StartsWith('/') ? name : Path.Combine(_current, name);
                    if (Directory.Exists(full)) { Navigate(full, record: true); _name.Text = SaveName(SuggestedNameOrDefault()); return; }
                    var dir = Path.GetDirectoryName(full);
                    if (dir == null || !Directory.Exists(dir)) { await Dialogs.ShowMessage(this, Title ?? "Save", $"The folder “{dir}” doesn't exist."); return; }
                    Navigate(dir, record: true);
                    name = Path.GetFileName(full);
                }
                name = SaveName(name);
                var target = Path.Combine(_current, name);
                if (Directory.Exists(target)) { Navigate(target, record: true); _name.Text = SaveName(SuggestedNameOrDefault()); return; }
                if (File.Exists(target) && ConfirmOverwrite)
                {
                    var answer = await Dialogs.Ask(this, "Replace file?", $"“{name}” already exists in {Label(_current)}. Replace it?", "Replace", "Cancel");
                    if (answer != 0) return;
                }
                Choose(new List<string> { target });
                return;
        }
    }

    /// <summary>Adds the chosen type's extension when the name has none that fits (e.g. "Report" → "Report.docx").</summary>
    private string SaveName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return name;
        var f = CurrentFilter;
        if (!f.All && f.Matches(name)) return name;
        var ext = f.Extension ?? (string.IsNullOrEmpty(DefaultExtension) ? null : "." + DefaultExtension.TrimStart('.'));
        if (ext == null) return name;
        if (f.All && Path.HasExtension(name)) return name;
        return name + ext;
    }

    /// <summary>Switching the type in a Save dialog changes the name's extension to match.</summary>
    private void SwapExtension()
    {
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) return;
        var known = _filters.Select(x => x.Extension).Where(e => e != null).Cast<string>().ToList();
        var current = known.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (current != null) name = name[..^current.Length];
        _name.Text = SaveName(name);
    }

    private async Task NewFolder()
    {
        if (Places.IsVirtual(_current)) return;
        var name = await Dialogs.Prompt(this, "New folder", "Name of the new folder", "New folder");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var path = FileOps.UniquePath(_current, name.Trim());
            Directory.CreateDirectory(path);
            Navigate(path, record: true);
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "New folder", ex.Message); }
    }

    private void OnKeys(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (e.Key == Key.Escape && !_pathBox.IsVisible) { Close(); e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { EditPath(); e.Handled = true; }
        else if (ctrl && e.Key == Key.H) { ToggleHidden(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { _search.Focus(); e.Handled = true; }
        else if (ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.Key == Key.N) { _ = NewFolder(); e.Handled = true; }
        else if (alt && e.Key == Key.Up) { GoUp(); e.Handled = true; }
        else if (alt && e.Key == Key.Left) { if (_back.Count > 0) Navigate(_back.Pop(), record: false); e.Handled = true; }
        else if (e.Key == Key.Enter && !_pathBox.IsFocused && !_name.IsFocused && !_list.IsKeyboardFocusWithin) { _ = Accept(); e.Handled = true; }
    }
}
