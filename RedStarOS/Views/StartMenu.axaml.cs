using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// The Start menu: search, a category rail with two sections — Red Star apps and the Linux programs installed on
/// this computer, each with its own "All", "Recent" and categories — the app list, and the user/power footer.
/// Search covers both and groups the results.
/// </summary>
public partial class StartMenu : UserControl
{
    public sealed class StartItem
    {
        public string Key { get; init; } = "";          // stable id used for the "Recent" list
        public string? AppId { get; init; }             // Red Star app id or "linux:<.desktop file>"; enables Pin to taskbar
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string Glyph { get; init; } = "";
        public IBrush Badge { get; init; } = Brushes.Gray;
        public string Category { get; init; } = "";     // category key
        public Action Run { get; init; } = () => { };
        public IImage? Icon { get; init; }              // a Linux program's own icon (else the Red Star tile)
        public bool IsHeader { get; init; }             // group heading in search results
        public bool IsApp => !IsHeader;
        public bool HasIcon => Icon != null && !IsHeader;
        public bool ShowBadge => Icon == null && !IsHeader;
        public bool IsLinux => Category.StartsWith(LinuxPrefix);
    }

    public sealed class CategoryRow
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";
        public string Count { get; init; } = "";
    }

    private const string KeyAll = "__all";
    private const string KeyRecent = "__recent";
    private const string LinuxPrefix = "linux:";
    private const int MaxRecent = 8;

    private static readonly Dictionary<string, string> RedStarCategoryLabels = new()
    {
        ["RSOffice"] = "Office", ["RSMedia"] = "Media", ["RSGames"] = "Games",
        ["RSOnline"] = "Online", ["RSUtilities"] = "Utilities", ["System"] = "System"
    };
    private static readonly string[] RedStarCategoryOrder = { "RSOffice", "RSMedia", "RSGames", "RSOnline", "RSUtilities", "System" };

    public event Action? CloseRequested;
    public event Action<string>? PowerRequested;       // "settings" | "logoff" | "shutdown" | "restart"

    private List<StartItem> _items = new();
    private string _categoryKey = KeyAll;
    private bool _sync;
    private bool _linuxSection;                          // which tab: Red Star apps (false) or Linux programs (true)
    private static readonly Dictionary<string, IImage?> IconCache = new();

    public StartMenu()
    {
        InitializeComponent();

        SearchBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Refilter(); };
        SearchBox.KeyDown += OnSearchKeyDown;

        RsCats.SelectionChanged += (_, _) => OnCategoryPicked(RsCats, LinuxCats);
        LinuxCats.SelectionChanged += (_, _) => OnCategoryPicked(LinuxCats, RsCats);
        LinuxTabIcon.Content = LinuxLogo(22);
        RsTab.Click += (_, _) => ShowSection(false);
        LinuxTab.Click += (_, _) => ShowSection(true);
        _linuxSection = SettingsStore.Current.StartSection == "linux";

        // Clicking launches; arrow keys only move the highlight, Enter launches.
        AppList.Tapped += (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is StartItem item) Launch(item);
        };
        AppList.ContextRequested += OnAppContextRequested;
        AppList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && AppList.SelectedItem is StartItem item) { Launch(item); e.Handled = true; }
            else if (e.Key == Key.Up && (AppList.SelectedIndex <= 0 ||
                     (AppList.ItemsSource is List<StartItem> l && l.Take(AppList.SelectedIndex).All(i => i.IsHeader)))) { SearchBox.Focus(); e.Handled = true; }
        };
    }

    // ------------------------------------------------------------ building the content

    /// <summary>Rebuild everything. Call each time the menu opens (apps may have been installed).</summary>
    public void Refresh()
    {
        bool child = SettingsStore.Current.AccountType == "Child";
        var items = new List<StartItem>();

        foreach (var app in AppRegistry.Apps.Where(a => !(child && a.RestrictedForChild)))
        {
            var captured = app;
            items.Add(new StartItem
            {
                Key = "rs:" + captured.Id,
                Title = captured.Name,
                Subtitle = RedStarCategoryLabels.GetValueOrDefault(captured.Category, captured.Category),
                AppId = captured.Id,
                Glyph = AppIcons.GlyphFor(captured.Id, captured.Name),
                Badge = AppIcons.Brush(AppIcons.ColorForCategory(captured.Category)),
                Category = captured.Category,
                Run = captured.Start
            });
        }

        if (!child)
        {
            foreach (var linuxApp in DesktopEntries.All())
            {
                var app = linuxApp;
                var cat = LinuxCategory(app.Categories);
                items.Add(new StartItem
                {
                    Key = "linux:" + app.Name,
                    AppId = app.Id,                        // installed Linux apps can be pinned too
                    Title = app.Name,
                    Subtitle = cat,
                    Glyph = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                    Badge = AppIcons.Brush(AppIcons.ColorForName(app.Name)),
                    Category = LinuxPrefix + cat,
                    Icon = LinuxIcon(app),
                    Run = () => DesktopEntries.Launch(app)
                });
            }
        }
        _items = items;

        BuildCategories();

        var avatar = ShellUi.LoadAvatar();
        if (avatar != null) AvatarImage.Source = avatar;
        UserLabel.Text = Environment.UserName;

        _categoryKey = KeyAll;
        if (!_items.Any(i => i.IsLinux)) _linuxSection = false;
        UpdateTabs();
        SearchIndex.Warm();
        SearchBox.Text = "";
        Refilter();
        Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }

    private void BuildCategories()
    {
        var rsItems = _items.Where(i => !i.IsLinux).ToList();
        var rs = new List<CategoryRow> { Row(KeyAll, "All Red Star apps", rsItems.Count) };
        var rsRecent = RecentItems(false);
        if (rsRecent.Count > 0) rs.Add(Row(KeyRecent, "Recent", rsRecent.Count));
        foreach (var key in RedStarCategoryOrder)
        {
            int n = rsItems.Count(i => i.Category == key);
            if (n > 0) rs.Add(Row(key, RedStarCategoryLabels[key], n));
        }

        var lxItems = _items.Where(i => i.IsLinux).ToList();
        var linux = new List<CategoryRow>();
        if (lxItems.Count > 0)
        {
            linux.Add(Row(KeyAll, "All Linux apps", lxItems.Count));
            var lxRecent = RecentItems(true);
            if (lxRecent.Count > 0) linux.Add(Row(KeyRecent, "Recent", lxRecent.Count));
            linux.AddRange(lxItems
                .GroupBy(i => i.Category)
                .OrderBy(g => g.Key == LinuxPrefix + "Other" ? 1 : 0)              // "Other" last
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => Row(g.Key, g.Key[LinuxPrefix.Length..], g.Count())));
        }

        RsCats.ItemsSource = rs;
        LinuxCats.ItemsSource = linux;
        SectionTabs.IsVisible = lxItems.Count > 0;        // no Linux programs (or a child account): no tabs needed
    }

    private void ShowSection(bool linux)
    {
        _linuxSection = linux;
        SettingsStore.Current.StartSection = linux ? "linux" : "redstar";
        SettingsStore.Save();
        _categoryKey = KeyAll;
        UpdateTabs();
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";       // leaves search and refilters
        else Refilter();
    }

    /// <summary>The distribution's own logo for the Linux tab, or a terminal glyph when it ships none.</summary>
    private static Control LinuxLogo(double size)
    {
        try
        {
            if (DesktopEntries.DistroLogoPath() is { } path)
            {
                using var fs = File.OpenRead(path);
                var img = new Avalonia.Controls.Image { Source = Bitmap.DecodeToWidth(fs, (int)(size * 3), BitmapInterpolationMode.HighQuality), Width = size, Height = size };
                RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
                return img;
            }
        }
        catch { }
        return Icons.Create("terminal", size - 2);
    }

    private void UpdateTabs()
    {
        // the Red Star tab is the red star itself: red on the plain tab, white on the red selected one
        RsTabIcon.Content = Icons.Create("star-filled", 20, _linuxSection ? AppIcons.Brush("#C8102E") : Brushes.White);
        RsTab.IsChecked = !_linuxSection;
        LinuxTab.IsChecked = _linuxSection;
        RsCats.IsVisible = !_linuxSection;
        LinuxCats.IsVisible = _linuxSection;
    }

    /// <summary>A Linux program's icon from the icon theme (PNG), cached for the session.</summary>
    private static IImage? LinuxIcon(LinuxApp app)
    {
        if (string.IsNullOrEmpty(app.Icon)) return null;
        if (IconCache.TryGetValue(app.Icon, out var cached)) return cached;
        IImage? img = null;
        try
        {
            if (DesktopEntries.IconPath(app.Icon, 48) is { } path)
            {
                using var fs = File.OpenRead(path);
                img = Bitmap.DecodeToWidth(fs, 72, BitmapInterpolationMode.HighQuality);
            }
        }
        catch { }
        return IconCache[app.Icon] = img;
    }

    private static CategoryRow Row(string key, string label, int count) =>
        new() { Key = key, Label = label, Count = count.ToString() };

    /// <summary>Maps a .desktop file's Categories= list to one friendly label.</summary>
    private static string LinuxCategory(string desktopCategories)
    {
        var cats = desktopCategories.Split(';', StringSplitOptions.RemoveEmptyEntries);
        (string Key, string Name)[] map =
        {
            ("Network", "Internet"), ("WebBrowser", "Internet"), ("AudioVideo", "Multimedia"), ("Audio", "Multimedia"),
            ("Video", "Multimedia"), ("Graphics", "Graphics"), ("Office", "Office"), ("Development", "Development"),
            ("Game", "Games"), ("Education", "Education"), ("Science", "Education"), ("Settings", "Settings"),
            ("System", "System Tools"), ("Utility", "Utilities")
        };
        foreach (var (key, name) in map)
            if (cats.Contains(key)) return name;
        return "Other";
    }

    // ------------------------------------------------------------ filtering

    private List<StartItem> RecentItems(bool linux)
    {
        var keys = SettingsStore.Current.RecentApps;
        return keys.Select(k => _items.FirstOrDefault(i => i.Key == k)).Where(i => i != null && i.IsLinux == linux).Cast<StartItem>().ToList();
    }

    private void Refilter()
    {
        var q = (SearchBox.Text ?? "").Trim();
        List<StartItem> list;
        string heading;

        if (q.Length > 0)
        {
            var matches = _items
                .Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            i.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => i.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // search everything: sums, settings, documents, files, the web
            var hits = SearchIndex.Find(q);
            var calc = hits.Where(h => h.Kind == "calc").ToList();
            var rest = hits.Where(h => h.Kind != "calc").ToList();
            StartItem ToItem(SearchIndex.Hit h) => new()
            {
                Key = "search:" + h.Kind + ":" + h.Title, Title = h.Title, Subtitle = h.Subtitle, Glyph = h.Icon,
                Badge = AppIcons.Brush(h.Kind switch { "web" => "#6B46C1", "setting" => "#4A5568", "calc" => "#DD6B20", _ => "#2B6CB0" }),
                Run = h.Run
            };
            static StartItem Header(string text) => new() { Key = "header:" + text, Title = text, IsHeader = true };
            list = calc.Select(ToItem).ToList();
            // the section you're in comes first
            var groups = new[] { (Linux: false, Label: "RED STAR APPS"), (Linux: true, Label: "LINUX APPS") };
            if (_linuxSection) Array.Reverse(groups);
            foreach (var (linux, label) in groups)
            {
                var g = matches.Where(i => i.IsLinux == linux).ToList();
                if (g.Count == 0) continue;
                list.Add(Header(label));
                list.AddRange(g);
            }
            if (rest.Count > 0)
            {
                list.Add(Header("FILES, SETTINGS AND THE WEB"));
                list.AddRange(rest.Select(ToItem));
            }
            heading = "SEARCH RESULTS";
            ClearCategorySelection();
        }
        else if (_categoryKey == KeyRecent)
        {
            list = RecentItems(_linuxSection);
            heading = "RECENT " + (_linuxSection ? "LINUX APPS" : "RED STAR APPS");
            HighlightCategory();
        }
        else
        {
            var section = _items.Where(i => i.IsLinux == _linuxSection);
            var src = _categoryKey == KeyAll ? section : section.Where(i => i.Category == _categoryKey);
            list = src.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
            heading = (_categoryKey == KeyAll ? (_linuxSection ? "All Linux apps" : "All Red Star apps") : CategoryLabel(_categoryKey)).ToUpperInvariant();
            HighlightCategory();
        }

        AppList.ItemsSource = list;
        int count = list.Count(i => !i.IsHeader);
        EmptyLabel.IsVisible = count == 0;
        HeaderLabel.Text = $"{heading}  ·  {count}";
    }

    private string CategoryLabel(string key) =>
        key.StartsWith(LinuxPrefix) ? key[LinuxPrefix.Length..] : RedStarCategoryLabels.GetValueOrDefault(key, key);

    private void ClearCategorySelection()
    {
        _sync = true;
        RsCats.SelectedItem = null;
        LinuxCats.SelectedItem = null;
        _sync = false;
    }

    private void HighlightCategory()
    {
        _sync = true;
        var mine = _linuxSection ? LinuxCats : RsCats;
        var other = _linuxSection ? RsCats : LinuxCats;
        mine.SelectedItem = (mine.ItemsSource as List<CategoryRow>)?.FirstOrDefault(r => r.Key == _categoryKey);
        other.SelectedItem = null;
        _sync = false;
    }

    private void OnCategoryPicked(ListBox source, ListBox other)
    {
        if (_sync || source.SelectedItem is not CategoryRow row) return;
        _categoryKey = row.Key;
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";      // leaves search mode and refilters
        else Refilter();
    }

    // ------------------------------------------------------------ launching

    private void Launch(StartItem item)
    {
        if (item.IsHeader) return;
        if (item.Key.StartsWith("search:")) { CloseRequested?.Invoke(); item.Run(); return; }   // not an app: not a "recent app"
        var recent = SettingsStore.Current.RecentApps;
        recent.Remove(item.Key);
        recent.Insert(0, item.Key);
        if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        SettingsStore.Save();

        CloseRequested?.Invoke();
        item.Run();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (AppList.ItemsSource is List<StartItem> shown && shown.FirstOrDefault(i => !i.IsHeader) is { } first) Launch(first);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && AppList.ItemCount > 0)
        {
            AppList.Focus();
            AppList.SelectedIndex = AppList.ItemsSource is List<StartItem> l ? Math.Max(0, l.FindIndex(i => !i.IsHeader)) : 0;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------ right-click: pin to taskbar

    private void OnAppContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is not StartItem { AppId: { } appId } || e.Source is not Control target) return;

        var item = new MenuItem { Header = WindowManager.IsPinned(appId) ? "Unpin from taskbar" : "Pin to taskbar" };
        item.Click += (_, _) => WindowManager.TogglePin(appId);
        var menu = new ContextMenu();
        menu.Items.Add(item);
        menu.Open(target);
        e.Handled = true;
    }

    // ------------------------------------------------------------ footer

    private void OnSettings(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); PowerRequested?.Invoke("settings"); }
    private void OnLogOff(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); PowerRequested?.Invoke("logoff"); }
    private void OnShutdown(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); PowerRequested?.Invoke("shutdown"); }
    private void OnRestart(object? s, RoutedEventArgs e) { CloseRequested?.Invoke(); PowerRequested?.Invoke("restart"); }
}
