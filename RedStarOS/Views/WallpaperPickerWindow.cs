using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Wallpapers: "Find online" searches the Wallhaven and Openverse indexes for full-resolution wallpapers (filtered to
/// at least your screen's size), with previews; "Set as wallpaper" downloads the original to ~/Pictures/Wallpapers.
/// "My wallpapers" shows saved ones or any folder, with the slideshow settings.
/// </summary>
public sealed class WallpaperPickerWindow : Window
{
    private static WallpaperPickerWindow? _open;

    // ── online ──
    private readonly TextBox _query = new() { PlaceholderText = "Search wallpapers — mountains, city at night, space, minimal…", FontSize = 15, Padding = new Thickness(12, 8) };
    private readonly ComboBox _source = new() { MinWidth = 150 };
    private readonly ComboBox _sort = new() { MinWidth = 130 };
    private readonly ComboBox _res = new() { MinWidth = 190 };
    private readonly ToggleButton _general = new() { Content = "General", IsChecked = true };
    private readonly ToggleButton _anime = new() { Content = "Anime" };
    private readonly ToggleButton _people = new() { Content = "People" };
    private readonly StackPanel _categories = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly WrapPanel _colors = new();
    private readonly WrapPanel _recent = new();
    private readonly WrapPanel _grid = new();
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _status = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6) };
    private readonly Button _more = new() { Content = "Load more", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 6), IsVisible = false, Margin = new Thickness(0, 8) };
    private string? _color;
    private WallpaperSearch.Options? _last;
    private WallpaperSearch.Page? _lastPage;
    private bool _loading;
    private CancellationTokenSource? _cts;

    // ── preview ──
    private readonly Image _previewImg = new() { Stretch = Stretch.Uniform, MaxHeight = 200 };
    private readonly TextBlock _previewTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _previewInfo = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly Button _set = new() { Content = "Set as wallpaper", Classes = { "primary" }, Padding = new Thickness(14, 7), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _save = new() { Content = "Save to My wallpapers", Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Button _page = new() { Content = "Open source page", Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false, Height = 4 };
    private readonly TextBlock _previewStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly Border _preview = new() { Width = 300, Padding = new Thickness(14), IsVisible = false };
    private WallpaperSearch.Result? _selected;
    private Border? _selectedCard;

    // ── my wallpapers ──
    private readonly WrapPanel _localGrid = new();
    private readonly TextBlock _folderLabel = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8 };
    private readonly CheckBox _slideshow = new() { Content = "Change wallpaper automatically every" };
    private readonly NumericUpDown _minutes = new() { Minimum = 1, Maximum = 1440, Increment = 1, Width = 110, FormatString = "0" };
    private int _localGen;

    private readonly TabControl _tabs = new();
    private (int W, int H) _screen = (1920, 1080);

    private static readonly (string Label, string Sort)[] Sorts = { ("Relevance", "relevance"), ("Top", "top"), ("Latest", "latest"), ("Random", "random"), ("Most viewed", "views") };
    // Wallhaven's colour palette (search accepts exactly these)
    private static readonly string[] Palette = { "660000", "cc0000", "ea4c88", "993399", "333399", "0066cc", "66cccc", "77cc33", "336600", "cccc33", "ffcc33", "ff6600", "996633", "000000", "999999", "ffffff" };

    /// <summary>Opens the picker (or brings it forward), optionally on the online search.</summary>
    public static void Open(bool online = false)
    {
        var w = _open ?? WindowManager.Open(new WallpaperPickerWindow(), "settings");
        w.Activate();
        if (online) { w._tabs.SelectedIndex = 0; w._query.Focus(); }
    }

    public WallpaperPickerWindow()
    {
        Title = "Wallpapers";
        Width = 1040; Height = 700; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open = this;
        Closed += (_, _) => { _cts?.Cancel(); if (ReferenceEquals(_open, this)) _open = null; };

        _tabs.Items.Add(new TabItem { Header = "Find online", Content = BuildOnline() });
        _tabs.Items.Add(new TabItem { Header = "My wallpapers", Content = BuildLocal() });
        Content = new Border { Padding = new Thickness(14, 8, 14, 14), Child = _tabs };

        Opened += (_, _) =>
        {
            if (Screens.Primary is { } sc) _screen = (sc.Bounds.Width, sc.Bounds.Height);
            FillResolutions();
            ShowRecent();
            _ = Search(newSearch: true);                  // something to look at straight away: this year's top wallpapers
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    // ═════════════════════════════════ Find online ═════════════════════════════════
    private Control BuildOnline()
    {
        foreach (var s in WallpaperSearch.Sources) _source.Items.Add(WallpaperSearch.SourceName(s));
        _source.SelectedIndex = 0;
        foreach (var s in Sorts) _sort.Items.Add(s.Label);
        _sort.SelectedIndex = 0;
        Avalonia.Automation.AutomationProperties.SetName(_query, "Search wallpapers");
        Avalonia.Automation.AutomationProperties.SetName(_source, "Wallpaper index");
        Avalonia.Automation.AutomationProperties.SetName(_sort, "Sort");
        Avalonia.Automation.AutomationProperties.SetName(_res, "Minimum resolution");

        var go = new Button { Content = "Search", Classes = { "primary" }, Padding = new Thickness(18, 8), IsDefault = true };
        go.Click += (_, _) => _ = Search(true);
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = Search(true); e.Handled = true; } };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        top.Children.Add(_query);
        Grid.SetColumn(go, 2); top.Children.Add(go);

        foreach (var t in new[] { _general, _anime, _people })
        {
            t.Padding = new Thickness(10, 4);
            t.IsCheckedChanged += (_, _) => { if (_last != null) _ = Search(true); };
            _categories.Children.Add(t);
        }
        _source.SelectionChanged += (_, _) => { _categories.IsVisible = _colors.IsVisible = SourceId == "wallhaven"; if (_last != null) _ = Search(true); };
        _sort.SelectionChanged += (_, _) => { if (_last != null) _ = Search(true); };
        _res.SelectionChanged += (_, _) => { if (_last != null) _ = Search(true); };

        // colour filter
        var any = ColorChip(null);
        _colors.Children.Add(any);
        foreach (var hex in Palette) _colors.Children.Add(ColorChip(hex));

        var filters = new WrapPanel();
        foreach (var f in new[] { Labeled("Index", _source), Labeled("Sort", _sort), Labeled("At least", _res), _categories })
        {
            f.Margin = new Thickness(0, 0, 14, 6);
            filters.Children.Add(f);
        }

        _more.Click += (_, _) => _ = Search(false);
        _scroll.Content = new StackPanel { Children = { _grid, _more } };
        _scroll.ScrollChanged += (_, _) =>
        {
            if (!_loading && _more.IsVisible && _scroll.Offset.Y + _scroll.Viewport.Height > _scroll.Extent.Height - 300) _ = Search(false);
        };

        // preview pane
        _set.Click += async (_, _) => await Download(apply: true);
        _save.Click += async (_, _) => await Download(apply: false);
        _page.Click += (_, _) => { if (_selected != null) BrowserLauncher.Launch(_selected.PageUrl); };
        _preview.Background = Application.Current?.FindResource("ShellCardBrush") as IBrush;
        _preview.CornerRadius = new CornerRadius(10);
        _preview.Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = _previewImg },
                _previewTitle, _previewInfo, _set, _progress, _save, _page, _previewStatus,
            }
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        body.Children.Add(_scroll);
        Grid.SetColumn(_preview, 2); body.Children.Add(_preview);

        var root = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var head = new StackPanel { Spacing = 8, Children = { top, filters, _colors, _recent, _status } };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(body);
        return root;
    }

    private static Control Labeled(string label, Control c) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 }, c }
    };

    private Control ColorChip(string? hex)
    {
        var b = new ToggleButton
        {
            Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 5, 0), CornerRadius = new CornerRadius(13),
            IsChecked = hex == null,
            Background = hex == null ? Brushes.Transparent : new SolidColorBrush(Color.Parse("#" + hex)),
            Content = hex == null ? new TextBlock { Text = "Any", FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
        };
        ToolTip.SetTip(b, hex == null ? "Any colour" : "Mostly #" + hex);
        b.Click += (_, _) =>
        {
            foreach (var o in _colors.Children.OfType<ToggleButton>()) o.IsChecked = ReferenceEquals(o, b);
            _color = hex;
            _ = Search(true);
        };
        return b;
    }

    private string SourceId => WallpaperSearch.Sources[Math.Max(0, _source.SelectedIndex)];

    private readonly List<(string Label, int W, int H)> _resolutions = new();
    private void FillResolutions()
    {
        _resolutions.Clear();
        _resolutions.Add(($"Fits my screen ({_screen.W} × {_screen.H})", _screen.W, _screen.H));
        _resolutions.Add(("Full HD (1920 × 1080)", 1920, 1080));
        _resolutions.Add(("QHD (2560 × 1440)", 2560, 1440));
        _resolutions.Add(("4K (3840 × 2160)", 3840, 2160));
        _resolutions.Add(("Any size", 0, 0));
        _res.ItemsSource = _resolutions.Select(r => r.Label).ToList();
        _res.SelectedIndex = 0;
    }

    private void ShowRecent()
    {
        _recent.Children.Clear();
        var list = WallpaperSearch.RecentSearches();
        if (list.Count == 0) { _recent.IsVisible = false; return; }
        _recent.IsVisible = true;
        _recent.Children.Add(new TextBlock { Text = "Recent:", Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        foreach (var q in list.Take(8))
        {
            var b = new Button { Content = q, Classes = { "flat" }, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 0), FontSize = 12.5 };
            b.Click += (_, _) => { _query.Text = q; _ = Search(true); };
            _recent.Children.Add(b);
        }
    }

    private async Task Search(bool newSearch)
    {
        if (_loading && !newSearch) return;
        if (!newSearch && (_lastPage == null || _last == null || _lastPage.PageNumber >= _lastPage.LastPage)) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _loading = true;

        var res = _res.SelectedIndex >= 0 && _res.SelectedIndex < _resolutions.Count ? _resolutions[_res.SelectedIndex] : ("", 1920, 1080);
        var opts = newSearch
            ? new WallpaperSearch.Options(_query.Text ?? "", SourceId, Sorts[Math.Max(0, _sort.SelectedIndex)].Sort, res.Item2, res.Item3,
                _general.IsChecked == true, _anime.IsChecked == true, _people.IsChecked == true, _color)
            : _last! with { PageNumber = _lastPage!.PageNumber + 1, Seed = _lastPage.Seed };
        if (newSearch)
        {
            _grid.Children.Clear();
            _scroll.Offset = default;
            if (!string.IsNullOrWhiteSpace(opts.Query)) { WallpaperSearch.Remember(opts.Query); ShowRecent(); }
        }
        _status.Text = newSearch ? (string.IsNullOrWhiteSpace(opts.Query) ? "Loading popular wallpapers…" : $"Searching {WallpaperSearch.SourceName(opts.Source)} for “{opts.Query.Trim()}”…") : "Loading more…";
        _more.IsVisible = false;

        WallpaperSearch.Page page;
        try { page = await WallpaperSearch.SearchAsync(opts, cts.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (ReferenceEquals(_cts, cts)) _loading = false; }
        if (cts.IsCancellationRequested) return;

        _last = opts;
        _lastPage = page;
        if (page.Error != null) { _status.Text = page.Error; return; }
        foreach (var r in page.Items) _grid.Children.Add(Card(r, cts.Token));
        int shown = _grid.Children.Count;
        _status.Text = shown == 0
            ? "Nothing found. Try other words, a lower resolution, or the other index."
            : $"{(page.Total > 0 ? page.Total.ToString("N0") : shown.ToString())} wallpapers"
              + (string.IsNullOrWhiteSpace(opts.Query) ? (opts.Source == "wallhaven" ? " — this year's most popular" : "") : $" for “{opts.Query.Trim()}”")
              + (opts.MinWidth > 0 ? $" · at least {opts.MinWidth} × {opts.MinHeight}" : "")
              + " · " + WallpaperSearch.SourceName(opts.Source);
        _more.IsVisible = page.PageNumber < page.LastPage && page.Items.Count > 0;
    }

    private Control Card(WallpaperSearch.Result r, CancellationToken ct)
    {
        var img = new Image { Stretch = Stretch.UniformToFill, Width = 232, Height = 131 };
        RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6),
            Child = new TextBlock { Text = string.Join(" · ", new[] { r.Quality, r.Resolution }.Where(x => x.Length > 0)), Foreground = Brushes.White, FontSize = 11 },
            IsVisible = r.Width > 0
        };
        var saved = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 16, 124, 16)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6),
            Child = new TextBlock { Text = "Saved", Foreground = Brushes.White, FontSize = 11 }, IsVisible = WallpaperSearch.IsSaved(r)
        };
        var card = new Border
        {
            Width = 236, Height = 135, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8), ClipToBounds = true,
            BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
            Child = new Grid { Children = { img, badge, saved } }, Focusable = true, Tag = saved,
        };
        ToolTip.SetTip(card, r.Title + (r.Resolution.Length > 0 ? " — " + r.Resolution : ""));
        Avalonia.Automation.AutomationProperties.SetName(card, r.Title + " " + r.Resolution);
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            Select(r, card);
            if (e.ClickCount == 2) _ = Download(apply: true);
        };
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Select(r, card); if (e.Key == Key.Enter) _ = Download(apply: true); e.Handled = true; } };
        _ = LoadThumb(img, r.ThumbUrl, 480, ct);
        return card;
    }

    private static async Task LoadThumb(Image img, string url, int width, CancellationToken ct)
    {
        try
        {
            var file = await WallpaperSearch.ThumbAsync(url, ct);
            if (file == null) return;
            var bmp = await Task.Run(() => { using var fs = File.OpenRead(file); return Bitmap.DecodeToWidth(fs, width, BitmapInterpolationMode.HighQuality); }, ct);
            img.Source = bmp;
        }
        catch { }
    }

    private void Select(WallpaperSearch.Result r, Border card)
    {
        if (_selectedCard != null) _selectedCard.BorderBrush = Brushes.Transparent;
        _selectedCard = card;
        card.BorderBrush = Application.Current?.FindResource("ShellAccentBrush") as IBrush ?? Brushes.DodgerBlue;
        _selected = r;
        _preview.IsVisible = true;
        _previewImg.Source = (card.Child as Grid)?.Children.OfType<Image>().FirstOrDefault()?.Source;
        _previewTitle.Text = r.Title;
        var lines = new List<string>();
        if (r.Resolution.Length > 0) lines.Add($"{r.Resolution}  ({r.Quality})" + (r.Width >= _screen.W && r.Height >= _screen.H ? " — sharp on your screen" : r.Width > 0 ? " — smaller than your screen" : ""));
        lines.Add(WallpaperSearch.SourceName(r.Source));
        if (r.Creator != null) lines.Add("By " + r.Creator);
        if (r.License != null) lines.Add(r.License + " — credit is saved in Wallpapers/credits.txt");
        _previewInfo.Text = string.Join("\n", lines);
        _previewStatus.Text = WallpaperSearch.IsSaved(r) ? "Saved in " + ShortPath(WallpaperSearch.LocalPath(r)) : "";
        _save.IsEnabled = !WallpaperSearch.IsSaved(r);
        _progress.IsVisible = false;
    }

    private async Task Download(bool apply)
    {
        var r = _selected;
        if (r == null) return;
        var card = _selectedCard;
        _set.IsEnabled = _save.IsEnabled = false;
        _progress.Value = 0; _progress.IsVisible = true;
        _previewStatus.Text = "Downloading the full-size picture…";
        try
        {
            var path = await WallpaperSearch.DownloadAsync(r, new Progress<double>(v => _progress.Value = v));
            if (apply) WallpaperService.Set(path);
            _previewStatus.Text = (apply ? "Wallpaper set. " : "Saved. ") + "File: " + ShortPath(path);
            if (card?.Tag is Border savedBadge) savedBadge.IsVisible = true;
            _localDirty = true;
        }
        catch (Exception ex) { _previewStatus.Text = "Couldn't download it: " + ex.Message; }
        finally
        {
            _progress.IsVisible = false;
            _set.IsEnabled = true;
            _save.IsEnabled = !WallpaperSearch.IsSaved(r);
        }
    }

    private static string ShortPath(string p) => p.StartsWith(AppPaths.Home) ? "~" + p[AppPaths.Home.Length..] : p;

    // ═════════════════════════════════ My wallpapers ═════════════════════════════════
    private bool _localDirty = true;

    private Control BuildLocal()
    {
        var s = SettingsStore.Current;
        _slideshow.IsChecked = s.SlideshowEnabled;
        _minutes.Value = s.SlideshowMinutes;
        _slideshow.IsCheckedChanged += (_, _) => SaveSlideshow();
        _minutes.ValueChanged += (_, _) => SaveSlideshow();

        Button B(string text, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 5), Margin = new Thickness(0, 0, 6, 6) };
            b.Click += (_, _) => act();
            return b;
        }
        var bar = new WrapPanel();
        bar.Children.Add(B("Crimson Banner collection (4K)", () => LoadFolder(WallpaperService.CollectionFolder, remember: true)));
        bar.Children.Add(B("Saved wallpapers", () => LoadFolder(WallpaperSearch.SaveFolder, remember: true)));
        bar.Children.Add(B("Pictures", () => LoadFolder(AppPaths.UserFolder("Pictures"), remember: true)));
        bar.Children.Add(B("Choose folder…", ChooseFolder));
        bar.Children.Add(B("Use the default wallpaper", () => WallpaperService.Set(null)));
        bar.Children.Add(B("Show in Files", () => WindowManager.Open(new FileManagerWindow(_folderLabel.Text ?? WallpaperSearch.SaveFolder), "files")));

        var slide = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { _slideshow, _minutes, new TextBlock { Text = "minutes (pictures from this folder)", VerticalAlignment = VerticalAlignment.Center } }
        };
        var head = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 8), Children = { bar, _folderLabel, slide } };
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { Content = _localGrid, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        _tabs.SelectionChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == 1 && _localDirty) { _localDirty = false; LoadFolder(WallpaperService.DefaultFolder, remember: false); }
        };
        return root;
    }

    private void SaveSlideshow()
    {
        var s = SettingsStore.Current;
        s.SlideshowEnabled = _slideshow.IsChecked == true;
        s.SlideshowMinutes = (int)(_minutes.Value ?? 10);
        SettingsStore.Save();
    }

    private async void ChooseFolder()
    {
        var folders = await FileDialogs.OpenFolder(this, new FolderPickerOpenOptions { Title = "Choose a folder of wallpapers", AllowMultiple = false });
        if (folders.Count == 0) return;
        var local = folders[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(local)) LoadFolder(local, remember: true);
    }

    private async void LoadFolder(string folder, bool remember)
    {
        if (remember) { SettingsStore.Current.WallpaperFolder = folder; SettingsStore.Save(); }
        _folderLabel.Text = folder;
        _localGrid.Children.Clear();
        int gen = ++_localGen;
        var files = WallpaperService.ImagesIn(folder);
        if (files.Count == 0)
        {
            _localGrid.Children.Add(new TextBlock
            {
                Text = folder == WallpaperSearch.SaveFolder ? "No saved wallpapers yet — find some on the “Find online” tab." : "No pictures in this folder.",
                Opacity = 0.7, Margin = new Thickness(4)
            });
            return;
        }
        var current = SettingsStore.Current.WallpaperPath;
        foreach (var file in files)
        {
            var bmp = await Task.Run(() =>
            {
                try { using var fs = File.OpenRead(file); return Bitmap.DecodeToWidth(fs, 480, BitmapInterpolationMode.HighQuality); }
                catch { return null; }
            });
            if (gen != _localGen) { bmp?.Dispose(); return; }
            if (bmp == null) continue;
            var f = file;
            var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill, Width = 232, Height = 131 };
            var card = new Border
            {
                Width = 236, Height = 135, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(8), ClipToBounds = true,
                BorderThickness = new Thickness(2), Cursor = new Cursor(StandardCursorType.Hand), Focusable = true,
                BorderBrush = f == current ? Application.Current?.FindResource("ShellAccentBrush") as IBrush ?? Brushes.DodgerBlue : Brushes.Transparent,
                Child = img,
            };
            ToolTip.SetTip(card, Path.GetFileName(f));
            Avalonia.Automation.AutomationProperties.SetName(card, Path.GetFileName(f));
            void Use()
            {
                foreach (var c in _localGrid.Children.OfType<Border>()) c.BorderBrush = Brushes.Transparent;
                card.BorderBrush = Application.Current?.FindResource("ShellAccentBrush") as IBrush ?? Brushes.DodgerBlue;
                WallpaperService.Set(f);
            }
            card.PointerPressed += (_, e) => { if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) Use(); };
            card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Use(); e.Handled = true; } };
            _localGrid.Children.Add(card);
        }
    }
}
