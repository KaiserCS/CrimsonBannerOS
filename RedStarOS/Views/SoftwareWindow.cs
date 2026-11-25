// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
using RedStarOS.Services.Software;

namespace RedStarOS.Views;

/// <summary>
/// Software: Crimson Banner OS's own programs (our signed catalogue, apps/README.md), Flatpaks from Flathub and
/// AppImages from AppImageHub, with what's installed and its updates, in one place. Free software only.
/// A sidebar picks the place; Explore is the front page; every program has a page of its own.
/// </summary>
public sealed class SoftwareWindow : Window
{
    private enum Place { Explore, CrimsonBanner, Flatpak, AppImage, Installed, Search }

    private Place _place = Place.Explore;
    private readonly Dictionary<Place, Button> _nav = new();
    private readonly TextBox _search = new() { PlaceholderText = "Search all software", MinWidth = 280, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _page = new();
    private string? _category;
    private int _shown = PageSize;
    private const int PageSize = 60;
    private SoftwareItem? _detail;            // the program whose page is open, if any
    private ProgressBar? _detailBar;            // its install progress, updated without rebuilding the page

    private List<SoftwareItem> _ours = new(), _flatpaks = new(), _appimages = new(), _installed = new();
    private string? _oursNote, _appimageNote;
    private bool _loadedFlatpak, _loadedAppImage, _loadingFlatpak, _loadingAppImage, _showUnstated;
    private readonly Dictionary<string, (int Pct, CancellationTokenSource Cts)> _busy = new();
    private readonly Dictionary<string, AppImages.Asset> _appImageUpdates = new();
    private HashSet<string> _flatpakUpdates = new();
    private List<Flatpak.Installed> _flatpakInstalled = new();
    private HashSet<string> _pacmanInstalled = new();

    /// <summary>Well-known free programs shown on Explore when Flathub has them.</summary>
    private static readonly string[] Picks =
    {
        "org.libreoffice.LibreOffice", "org.mozilla.firefox", "org.gimp.GIMP", "org.inkscape.Inkscape", "org.kde.krita",
        "org.videolan.VLC", "org.blender.Blender", "org.audacityteam.Audacity", "com.obsproject.Studio", "org.kde.kdenlive",
        "org.mozilla.Thunderbird", "org.freecad.FreeCAD", "org.qgis.qgis", "org.octave.Octave", "org.kde.kstars", "org.gnu.emacs",
    };

    /// <summary>freedesktop.org's main categories, as people say them.</summary>
    private static readonly (string Key, string Label, string Glyph)[] Categories =
    {
        ("Office", "Office", "document"), ("Graphics", "Graphics", "palette"), ("AudioVideo", "Sound & video", "film"),
        ("Education", "Education", "note"), ("Science", "Science", "activity"), ("Development", "Development", "terminal"),
        ("Game", "Games", "game"), ("Network", "Internet", "globe"), ("Utility", "Utilities", "settings"), ("System", "System", "drive"),
    };

    public SoftwareWindow()
    {
        Title = "Software";
        Width = 1120; Height = 740; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── sidebar ──
        var side = new StackPanel { Spacing = 2, Margin = new Thickness(10, 14) };
        side.Children.Add(new TextBlock { Text = "Software", Classes = { "h1" }, Margin = new Thickness(10, 0, 0, 14) });
        foreach (var (p, label, glyph) in new[]
                 {
                     (Place.Explore, "Explore", "grid"), (Place.CrimsonBanner, "Crimson Banner", "bag"), (Place.Flatpak, "Flatpak", "download"),
                     (Place.AppImage, "AppImage", "folder"), (Place.Installed, "Installed", "list"),
                 })
        {
            var b = new Button { Classes = { "flat" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8) };
            b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Icons.Create(glyph, 16), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
            b.Click += (_, _) => _ = Go(p);
            _nav[p] = b;
            side.Children.Add(b);
        }
        var sideBorder = new Border { Width = 220, BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        sideBorder.Classes.Add("card");
        sideBorder.CornerRadius = new CornerRadius(0);
        sideBorder.Padding = new Thickness(0);

        // ── top bar ──
        _search.TextChanged += (_, _) => OnSearch();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) _search.Text = ""; };
        var top = new DockPanel { Margin = new Thickness(24, 14, 24, 6) };
        DockPanel.SetDock(_search, Dock.Right);
        top.Children.Add(_search);
        top.Children.Add(new Control());

        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(_page);

        var root = new DockPanel();
        DockPanel.SetDock(sideBorder, Dock.Left);
        root.Children.Add(sideBorder);
        root.Children.Add(main);
        Content = root;

        Opened += async (_, _) =>
        {
            Render();
            if (_sample) return;            // screenshots: the sample programs, not the real catalogues
            await LoadOurs(fetch: true);
            await RefreshInstalled();
            Render();
            // Flathub and AppImageHub fill in Explore as they arrive
            _ = LoadFlatpak(prepare: false);
            _ = LoadAppImages(fetch: false);
            _ = Task.Run(async () => { await Task.Delay(1500); await Dispatcher.UIThread.InvokeAsync(async () => { await RefreshInstalled(checkUpdates: true); if (_detail == null) Render(); }); });
        };
        Closed += (_, _) => { foreach (var b in _busy.Values) b.Cts.Cancel(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && _detail != null) { _detail = null; Render(); e.Handled = true; } };
    }

    // ── navigation ──

    private async Task Go(Place p, string? category = null)
    {
        _place = p;
        _detail = null;
        _category = category;
        _shown = PageSize;
        if (p != Place.Search && !string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; _place = p; }
        Render();
        if (p == Place.Flatpak && !_loadedFlatpak) await LoadFlatpak(prepare: false);
        if (p == Place.AppImage && !_loadedAppImage) await LoadAppImages(fetch: false);
        if (p == Place.Installed) { await RefreshInstalled(); Render(); }
    }

    private Place _beforeSearch = Place.Explore;

    private void OnSearch()
    {
        var q = (_search.Text ?? "").Trim();
        if (q.Length > 0 && _place != Place.Search) { _beforeSearch = _place; _place = Place.Search; }
        else if (q.Length == 0 && _place == Place.Search) _place = _beforeSearch;
        _detail = null;
        _shown = PageSize;
        Render();
    }

    private void Render()
    {
        foreach (var (k, b) in _nav)
        {
            var on = k == _place || (_place == Place.Search && k == _beforeSearch);
            b.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            b.Background = on ? Hover() : Brushes.Transparent;
        }
        var updates = _installed.Count(i => i.UpdateAvailable);
        if (_nav[Place.Installed].Content is StackPanel sp && sp.Children[1] is TextBlock t)
            t.Text = updates > 0 ? $"Installed · {updates}" : "Installed";

        _detailBar = null;
        try
        {
            _page.Content = _detail != null ? DetailPage(_detail) : _place switch
            {
                Place.Explore => ExplorePage(),
                Place.CrimsonBanner => SourcePage(Place.CrimsonBanner),
                Place.Flatpak => SourcePage(Place.Flatpak),
                Place.AppImage => SourcePage(Place.AppImage),
                Place.Installed => InstalledPage(),
                _ => SearchPage(),
            };
        }
        catch (Exception ex)
        {
            // never a blank page: say what went wrong (and it's in crash.log for Diagnostics)
            Diagnostics.Log("Software: " + (_detail != null ? "the page of " + _detail.Name : _place.ToString()), ex);
            var col = Column();
            col.Children.Add(EmptyCard("info", "This page couldn't be shown", ex.Message + " (the details are in Settings → Diagnostics)."));
            _page.Content = Scroll(col);
        }
    }

    /// <summary>The sidebar's highlight: the theme's hover colour for the current light or dark variant.</summary>
    private IBrush Hover() =>
        this.TryFindResource("ShellHoverBrush", ActualThemeVariant, out var v) && v is IBrush b ? b : new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));

    /// <summary>For tests: draws every page once (Explore, each source, Installed, a search), as the sidebar would.</summary>
    public IReadOnlyList<Control?> RenderEveryPage()
    {
        var pages = new List<Control?>();
        foreach (var p in new[] { Place.Explore, Place.CrimsonBanner, Place.Flatpak, Place.AppImage, Place.Installed })
        {
            _place = p; _detail = null; Render();
            pages.Add(_page.Content as Control);
        }
        _search.Text = "a";
        pages.Add(_page.Content as Control);
        _detail = new SoftwareItem { Source = SoftwareSource.CrimsonBanner, Id = "test", Name = "Test", Summary = "A test", Screenshots = { }, Methods = { new InstallMethod(InstallMethod.AppImage) { Url = "https://example.invalid/t.AppImage", Sha256 = new string('0', 64) } } };
        Render();
        pages.Add(_page.Content as Control);
        _detail = null; _search.Text = "";
        return pages;
    }

    /// <summary>
    /// For screenshots (tests): a few sample programs in each place, then the page asked for ("explore", "crimson",
    /// "flatpak", "installed", "search" or "detail"), as if the catalogues had been read.
    /// </summary>
    private bool _sample;

    public void ShowSample(string page)
    {
        _sample = true;
        SoftwareItem Item(SoftwareSource src, string id, string name, string summary, string cat, bool installed = false, bool update = false) => new()
        {
            Source = src, Id = id, Name = name, Summary = summary, License = "GPL-3.0-or-later", Developer = name + " developers",
            Version = "2.4.1", Categories = { cat }, Installed = installed, InstalledVersion = installed ? (update ? "2.3.0" : "2.4.1") : null,
            UpdateAvailable = update, Homepage = "https://example.invalid/" + id,
            Description = summary + ".\n\nA longer description of what it does, how it fits with the rest of the system and who makes it.",
            Methods = { new InstallMethod(src == SoftwareSource.Flatpak ? InstallMethod.Flatpak : InstallMethod.AppImage) { Url = "https://example.invalid/" + id, Sha256 = new string('0', 64) } },
        };
        _ours = new()
        {
            Item(SoftwareSource.CrimsonBanner, "planner", "Planner", "Economic computation for central planning", "Office"),
            Item(SoftwareSource.CrimsonBanner, "archive", "People's Archive", "Library catalogue and reading room", "Education", installed: true),
            Item(SoftwareSource.CrimsonBanner, "atlas", "Atlas", "Maps that work without the network", "Science"),
        };
        _oursNote = null;
        _flatpaks = new()
        {
            Item(SoftwareSource.Flatpak, "org.libreoffice.LibreOffice", "LibreOffice", "The office suite: documents, sheets and slides", "Office", installed: true, update: true),
            Item(SoftwareSource.Flatpak, "org.gimp.GIMP", "GIMP", "Edit photos and make pictures", "Graphics"),
            Item(SoftwareSource.Flatpak, "org.inkscape.Inkscape", "Inkscape", "Vector drawing", "Graphics"),
            Item(SoftwareSource.Flatpak, "org.kde.krita", "Krita", "Digital painting", "Graphics"),
            Item(SoftwareSource.Flatpak, "org.videolan.VLC", "VLC", "Plays every kind of film and music", "AudioVideo"),
            Item(SoftwareSource.Flatpak, "org.blender.Blender", "Blender", "3D modelling and animation", "Graphics"),
        };
        _appimages = new() { Item(SoftwareSource.AppImage, "kdenlive", "Kdenlive", "Video editing", "AudioVideo") };
        _loadedFlatpak = _loadedAppImage = true;
        _loadingFlatpak = _loadingAppImage = false;
        _installed = _ours.Concat(_flatpaks).Where(i => i.Installed).ToList();
        _detail = null; _category = null;
        _place = page switch { "crimson" => Place.CrimsonBanner, "flatpak" => Place.Flatpak, "installed" => Place.Installed, _ => Place.Explore };
        if (page == "search") { _search.Text = "in"; return; }
        if (page == "detail") _detail = _flatpaks[0];
        Render();
    }

    /// <summary>For tests: whether the last page drawn is the error card.</summary>
    public static bool IsErrorPage(Control? page) =>
        page is ScrollViewer { Content: StackPanel sp } && sp.Children.OfType<Border>().Any(b => b.Child is StackPanel row && row.Children.OfType<StackPanel>()
            .Any(c => c.Children.OfType<TextBlock>().Any(t => t.Text == "This page couldn't be shown")));

    // ── pages ──

    private static ScrollViewer Scroll(Control c) => new() { Content = c, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private static StackPanel Column() => new() { Spacing = 14, Margin = new Thickness(24, 8, 24, 28) };
    private static TextBlock H2(string text) => new() { Text = text, Classes = { "h2" }, Margin = new Thickness(0, 10, 0, 0) };
    private static TextBlock Caption(string text) => new() { Text = text, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };

    private Control ExplorePage()
    {
        var col = Column();
        // the banner
        var banner = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(26, 22), Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#C8102E"), 0), new GradientStop(Color.Parse("#7A0A1C"), 1) },
        } };
        banner.Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Software for Crimson Banner OS", FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                new TextBlock { Text = "Free software from our own catalogue, Flathub and AppImageHub, in one place.", Foreground = Brushes.White, Opacity = 0.9, TextWrapping = TextWrapping.Wrap },
            },
        };
        col.Children.Add(banner);

        // our own
        col.Children.Add(SectionHead("From Crimson Banner OS", "All", () => _ = Go(Place.CrimsonBanner)));
        if (_ours.Count > 0) col.Children.Add(CardGrid(_ours.Take(6), showSource: false));
        else col.Children.Add(EmptyCard("bag", "Nothing published yet", _oursNote ?? "Programs made for Crimson Banner OS that are too big or specialised for every install will appear here."));

        // updates waiting
        var updates = _installed.Where(i => i.UpdateAvailable).ToList();
        if (updates.Count > 0)
        {
            col.Children.Add(SectionHead($"Updates ({updates.Count})", "Update all", () => _ = UpdateAll()));
            col.Children.Add(CardGrid(updates.Take(6), showSource: true));
        }

        // picks from Flathub
        col.Children.Add(SectionHead("Picks from Flathub", "Browse Flathub", () => _ = Go(Place.Flatpak)));
        var picks = Picks.Select(id => _flatpaks.FirstOrDefault(f => f.Id == id)).Where(f => f != null).Cast<SoftwareItem>().Take(9).ToList();
        if (picks.Count > 0) col.Children.Add(CardGrid(picks, showSource: false));
        else if (_loadingFlatpak) col.Children.Add(Loading("Reading Flathub's catalogue…"));
        else if (!Flatpak.Available) col.Children.Add(EmptyCard("download", "Flatpak isn't installed", "Flatpak (free software from freedesktop.org) brings most desktop programs, from Flathub.", ("Install Flatpak", new Func<Task>(InstallFlatpakItself))));
        else col.Children.Add(EmptyCard("download", "Flathub isn't set up yet", "Its catalogue is fetched once and kept on this computer.", ("Set up Flathub", new Func<Task>(() => LoadFlatpak(prepare: true)))));

        // categories
        col.Children.Add(H2("Browse by category"));
        var cats = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label, glyph) in Categories)
        {
            var b = new Button { Classes = { "tile" }, Width = 190, Height = 56, Margin = new Thickness(0, 0, 10, 10) };
            b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { Icons.Create(glyph, 20), new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
            b.Click += (_, _) => _ = Go(Place.Flatpak, key);
            cats.Children.Add(b);
        }
        col.Children.Add(cats);
        return Scroll(col);
    }

    private Control SourcePage(Place p)
    {
        var col = Column();
        var (title, about) = p switch
        {
            Place.CrimsonBanner => ("Crimson Banner OS", "Programs made for Crimson Banner OS, signed with the project's key. On Arch they come from the [crimson-banner-apps] repository and update with the system."),
            Place.Flatpak => ("Flatpak", "Free programs from Flathub (run by the GNOME Foundation with KDE). New ones go into your own account: no password needed."),
            _ => ("AppImage", "Free programs from AppImageHub, downloaded from each project's own release into your Applications folder, checked and added to the Start menu."),
        };
        col.Children.Add(new TextBlock { Text = title, Classes = { "h1" } });
        col.Children.Add(Caption(about));

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        void Action(string text, Func<Task> run)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += async (_, _) => { b.IsEnabled = false; try { await run(); } finally { b.IsEnabled = true; } };
            actions.Children.Add(b);
        }
        List<SoftwareItem> items;
        string? note = null;
        bool loading = false;
        switch (p)
        {
            case Place.CrimsonBanner:
                items = _ours; note = _oursNote;
                Action("Check for new programs", () => LoadOurs(fetch: true));
                break;
            case Place.Flatpak:
                items = _flatpaks; loading = _loadingFlatpak;
                if (Flatpak.Available) Action(_flatpaks.Count == 0 ? "Set up Flathub" : "Refresh", () => LoadFlatpak(prepare: true));
                else Action("Install Flatpak", new Func<Task>(InstallFlatpakItself));
                break;
            default:
                items = _appimages.Where(i => _showUnstated || i.Licence == FreeLicences.Verdict.Free).ToList();
                note = _appimageNote; loading = _loadingAppImage;
                Action("Add an AppImage file…", AddAppImageFile);
                Action("Refresh", () => LoadAppImages(fetch: true));
                var unstated = new CheckBox { Content = "Include ones that don't state a licence", IsChecked = _showUnstated, Margin = new Thickness(8, 0, 0, 0) };
                unstated.IsCheckedChanged += (_, _) => { _showUnstated = unstated.IsChecked == true; Render(); };
                actions.Children.Add(unstated);
                break;
        }
        col.Children.Add(actions);
        if (note != null) col.Children.Add(Caption(note));

        // category chips: the ones these programs use most
        var cats = Categories.Where(c => items.Any(i => i.Categories.Contains(c.Key))).ToList();
        if (cats.Count > 1)
        {
            var chips = new WrapPanel { Orientation = Orientation.Horizontal };
            chips.Children.Add(Chip("All", _category == null, () => { _category = null; _shown = PageSize; Render(); }));
            foreach (var (key, label, _) in cats)
                chips.Children.Add(Chip(label, _category == key, () => { _category = key; _shown = PageSize; Render(); }));
            col.Children.Add(chips);
        }
        var shown = _category == null ? items : items.Where(i => i.Categories.Contains(_category)).ToList();

        if (loading && items.Count == 0) col.Children.Add(Loading(p == Place.Flatpak ? "Reading Flathub's catalogue…" : "Fetching AppImageHub's list…"));
        else if (shown.Count == 0)
            col.Children.Add(p switch
            {
                Place.CrimsonBanner => EmptyCard("bag", "Nothing published yet", "When the project publishes a program (tools/publish-app.sh), it appears here."),
                Place.Flatpak when !Flatpak.Available => EmptyCard("download", "Flatpak isn't installed", "Install it, and Flathub's programs appear here."),
                Place.Flatpak => EmptyCard("download", "Flathub isn't set up yet", "Set it up to see its programs."),
                _ => EmptyCard("folder", "No programs to show", "Refresh, or tick the box to include ones that don't state a licence."),
            });
        else AddGridWithMore(col, shown, showSource: false);
        return Scroll(col);
    }

    private Control InstalledPage()
    {
        var col = Column();
        col.Children.Add(new TextBlock { Text = "Installed", Classes = { "h1" } });
        var updates = _installed.Count(i => i.UpdateAvailable);
        col.Children.Add(Caption(updates > 0 ? $"{updates} update{(updates == 1 ? "" : "s")} waiting." : _installed.Count > 0 ? "Everything is up to date." : ""));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        if (updates > 0) { var u = new Button { Content = "Update all", Classes = { "primary" }, Margin = new Thickness(0, 0, 8, 0) }; u.Click += async (_, _) => await UpdateAll(); actions.Children.Add(u); }
        var check = new Button { Content = "Check for updates" };
        check.Click += async (_, _) => { check.IsEnabled = false; check.Content = "Checking…"; await RefreshInstalled(checkUpdates: true); Render(); };
        actions.Children.Add(check);
        col.Children.Add(actions);
        if (_installed.Count == 0) col.Children.Add(EmptyCard("list", "Nothing installed from here yet", "Programs installed from any of the three sources appear here, with their updates."));
        else AddGridWithMore(col, _installed, showSource: true);
        return Scroll(col);
    }

    private Control SearchPage()
    {
        var q = (_search.Text ?? "").Trim();
        var col = Column();
        col.Children.Add(new TextBlock { Text = $"Results for “{q}”", Classes = { "h1" } });
        bool Hit(SoftwareItem i) => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Summary.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                    || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase);
        // whole-name matches first, then name matches, then the rest
        var all = _ours.Concat(_flatpaks).Concat(_appimages.Where(i => _showUnstated || i.Licence == FreeLicences.Verdict.Free)).Where(Hit)
            .OrderByDescending(i => i.Name.Equals(q, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(i => i.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(i => i.Source).ToList();
        if (all.Count == 0)
            col.Children.Add(EmptyCard("info", "Nothing found", _loadingFlatpak || _loadingAppImage ? "Still reading the catalogues; try again in a moment." : "Try another word, or browse a category on Explore."));
        else AddGridWithMore(col, all, showSource: true);
        return Scroll(col);
    }

    private Control DetailPage(SoftwareItem item)
    {
        var col = Column();
        var back = new Button { Classes = { "flat" }, Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Icons.Create("arrow-left", 16), new TextBlock { Text = "Back" } } } };
        back.Click += (_, _) => { _detail = null; Render(); };
        col.Children.Add(back);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = IconFor(item, 96);
        head.Children.Add(icon);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4, Margin = new Thickness(18, 0) };
        title.Children.Add(new TextBlock { Text = item.Name, FontSize = 26, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (item.Developer is { Length: > 0 } dev) title.Children.Add(Caption(dev));
        if (item.Summary.Length > 0) title.Children.Add(new TextBlock { Text = item.Summary, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var buttons = Buttons(item);
        Grid.SetColumn(buttons, 2);
        head.Children.Add(buttons);
        col.Children.Add(head);

        if (item.Screenshots.Count > 0)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            foreach (var url in item.Screenshots.Take(6)) strip.Children.Add(Screenshot(url));
            col.Children.Add(new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(0, 6) });
        }

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,24,300") };
        if (item.Description.Length > 0 && item.Description != item.Summary)
            body.Children.Add(new SelectableTextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        var facts = Facts(item);
        Grid.SetColumn(facts, 2);
        body.Children.Add(facts);
        col.Children.Add(body);
        return Scroll(col);
    }

    private Control Buttons(SoftwareItem item)
    {
        var panel = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, MinWidth = 180 };
        if (_busy.TryGetValue(BusyKey(item), out var busy))
        {
            _detailBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = busy.Pct, Height = 8, IsIndeterminate = busy.Pct <= 0 };
            panel.Children.Add(new TextBlock { Text = "Working…", Classes = { "caption" } });
            panel.Children.Add(_detailBar);
            var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => busy.Cts.Cancel();
            panel.Children.Add(cancel);
            return panel;
        }
        Button Btn(string text, Func<Task> act, bool primary)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(18, 8) };
            if (primary) b.Classes.Add("primary");
            b.Click += async (_, _) => await act();
            panel.Children.Add(b);
            return b;
        }
        if (!item.Installed)
        {
            var method = Method(item);
            if (method != null) Btn("Install", () => Do(item, "Installing", ct => Install(item, method, ct)), true);
            else panel.Children.Add(new TextBlock { Text = "This computer can't install it yet: it needs " + string.Join(" or ", item.Methods.Select(m => m.Kind).Distinct()) + ".", TextWrapping = TextWrapping.Wrap, MaxWidth = 220, Classes = { "caption" } });
            if (method != null) panel.Children.Add(new TextBlock { Text = method.Kind switch { InstallMethod.Pacman => "From [crimson-banner-apps] (pacman)", InstallMethod.Flatpak => "As a Flatpak, for your account", _ => "As an AppImage, in ~/Applications" }, Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center });
        }
        else
        {
            if (item.UpdateAvailable) Btn("Update", () => Do(item, "Updating", ct => Update(item, ct)), true);
            if (CanOpen(item)) Btn("Open", () => { Open(item); return Task.CompletedTask; }, !item.UpdateAvailable);
            Btn("Remove", () => Do(item, "Removing", ct => Remove(item, ct)), false);
        }
        return panel;
    }

    private Control Facts(SoftwareItem item)
    {
        var card = new Border { Classes = { "card" } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        void Fact(string k, string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            var r = g.RowDefinitions.Count;
            g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var kt = new TextBlock { Text = k, Classes = { "caption" }, Margin = new Thickness(0, 3, 14, 3) };
            var vt = new SelectableTextBlock { Text = v, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3) };
            Grid.SetRow(kt, r); Grid.SetRow(vt, r); Grid.SetColumn(vt, 1);
            g.Children.Add(kt); g.Children.Add(vt);
        }
        Fact("From", SourceName(item.Source) + (item.Source == SoftwareSource.Flatpak ? " (" + (item.Methods.FirstOrDefault()?.Remote ?? Flatpak.Flathub) + ")" : ""));
        Fact("Licence", item.License is { Length: > 0 } l ? l + (item.Licence == FreeLicences.Verdict.Free ? " · free software" : "") : "Not stated: check the project's page before relying on it");
        Fact("Version", item.Installed && item.InstalledVersion is { Length: > 0 } iv && iv != item.Version
            ? iv + (item.Version is { Length: > 0 } nv ? $" (latest {nv})" : "") : item.Version ?? item.InstalledVersion);
        Fact("Installed in", item.Installed ? item.InstalledWhere : null);
        Fact("Website", item.Homepage);
        Fact("Categories", item.Categories.Count > 0 ? string.Join(", ", item.Categories.Take(5)) : null);
        card.Child = g;
        return card;
    }

    // ── building blocks ──

    private Control SectionHead(string title, string action, Action go)
    {
        var d = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var b = new Button { Classes = { "flat" }, Content = action + " ›" };
        b.Click += (_, _) => go();
        DockPanel.SetDock(b, Dock.Right);
        d.Children.Add(b);
        d.Children.Add(new TextBlock { Text = title, Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center });
        return d;
    }

    private Control CardGrid(IEnumerable<SoftwareItem> items, bool showSource)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) w.Children.Add(Card(i, showSource));
        return w;
    }

    private void AddGridWithMore(StackPanel col, List<SoftwareItem> items, bool showSource)
    {
        col.Children.Add(Caption($"{items.Count} program{(items.Count == 1 ? "" : "s")}"));
        col.Children.Add(CardGrid(items.Take(_shown), showSource));
        if (items.Count > _shown)
        {
            var more = new Button { Content = $"Show more ({items.Count - _shown} left)", HorizontalAlignment = HorizontalAlignment.Center };
            more.Click += (_, _) => { _shown += PageSize; Render(); };
            col.Children.Add(more);
        }
    }

    private Control Card(SoftwareItem item, bool showSource)
    {
        var b = new Button { Classes = { "tile" }, Width = 300, Height = 92, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(12, 10) };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(IconFor(item, 52));
        var text = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Summary.Length > 0) text.Children.Add(new TextBlock { Text = item.Summary, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var tag = item.UpdateAvailable ? "Update available" : item.Installed ? "Installed" : showSource ? SourceName(item.Source) : null;
        if (showSource && (item.Installed || item.UpdateAvailable)) tag = SourceName(item.Source) + " · " + tag;
        if (tag != null)
        {
            var t = new TextBlock { Text = tag, FontSize = 11, FontWeight = FontWeight.SemiBold };
            if (item.UpdateAvailable) t.Foreground = new SolidColorBrush(Color.Parse("#DD6B20"));
            else if (item.Installed) t.Foreground = new SolidColorBrush(Color.Parse("#38A169"));
            else t.Opacity = 0.6;
            text.Children.Add(t);
        }
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        b.Content = g;
        b.Click += (_, _) => { _detail = item; Render(); };
        return b;
    }

    private Control Chip(string label, bool on, Action pick)
    {
        var b = new Button { Content = label, Padding = new Thickness(12, 4), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(14) };
        b.Classes.Add(on ? "primary" : "tile");
        b.Click += (_, _) => pick();
        return b;
    }

    private Control EmptyCard(string glyph, string title, string text, (string Label, Func<Task> Run)? action = null)
    {
        var card = new Border { Classes = { "card" }, Padding = new Thickness(20, 18) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(new Border { Opacity = 0.6, VerticalAlignment = VerticalAlignment.Top, Child = Icons.Create(glyph, 32) });
        var col = new StackPanel { Spacing = 6, MaxWidth = 620 };
        col.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        col.Children.Add(Caption(text));
        if (action is { } a)
        {
            var b = new Button { Content = a.Label, Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            b.Click += async (_, _) => { b.IsEnabled = false; try { await a.Run(); } finally { b.IsEnabled = true; } };
            col.Children.Add(b);
        }
        row.Children.Add(col);
        card.Child = row;
        return card;
    }

    private static Control Loading(string text) => new StackPanel
    {
        Spacing = 10, Margin = new Thickness(0, 20), HorizontalAlignment = HorizontalAlignment.Left,
        Children = { new ProgressBar { IsIndeterminate = true, Width = 260, Height = 6 }, new TextBlock { Text = text, Classes = { "caption" } } },
    };

    private static string SourceName(SoftwareSource s) => s switch
    {
        SoftwareSource.CrimsonBanner => "Crimson Banner",
        SoftwareSource.Flatpak => "Flatpak",
        _ => "AppImage",
    };

    // ── doing things ──

    private static string BusyKey(SoftwareItem i) => i.Source + ":" + i.Id;

    private static InstallMethod? Method(SoftwareItem item) =>
        item.Source == SoftwareSource.CrimsonBanner ? OurCatalog.Usable(item, ProcessUtil.Which) : item.Methods.FirstOrDefault();

    private async Task Do(SoftwareItem item, string verb, Func<CancellationToken, Task<string?>> work)
    {
        var key = BusyKey(item);
        if (_busy.ContainsKey(key)) return;
        var cts = new CancellationTokenSource();
        _busy[key] = (0, cts);
        if (_detail == item) Render();
        string? error;
        try { error = await work(cts.Token); }
        catch (OperationCanceledException) { error = "cancelled"; }
        catch (Exception ex) { error = ex.Message; }
        _busy.Remove(key);
        if (error == null) DesktopEntries.Reload();      // the Start menu shows (or drops) it the next time it opens
        if (error == null) Notifier.Show("Software", $"{verb} {item.Name}: done.", "software");
        else if (error != "cancelled") Notifier.Show("Software", $"{verb} {item.Name} didn't work: {error}", "software");
        await RefreshInstalled();
        Render();
    }

    private Action<int> ProgressFor(SoftwareItem item) => pct => Dispatcher.UIThread.Post(() =>
    {
        var key = BusyKey(item);
        if (!_busy.TryGetValue(key, out var b)) return;
        _busy[key] = (pct, b.Cts);
        if (_detail == item && _detailBar != null) { _detailBar.IsIndeterminate = pct <= 0; _detailBar.Value = pct; }
    });

    private async Task<string?> Install(SoftwareItem item, InstallMethod m, CancellationToken ct)
    {
        switch (m.Kind)
        {
            case InstallMethod.Flatpak: return await Flatpak.InstallAsync(m, ProgressFor(item), ct);
            case InstallMethod.AppImage: return await AppImages.InstallAsync(item, m, ProgressFor(item), ct);
            case InstallMethod.Pacman:
                // the first of our apps: [crimson-banner-apps] is added (the password is asked for) and the lists refreshed
                if (await OurCatalog.EnableAppsRepoAsync(ct) is { } err) return "the apps repository couldn't be added: " + err;
                return await SystemUpdates.InstallByNameAsync(new[] { m.Package! }, ProgressFor(item), ct);
            default: return "unknown kind of install";
        }
    }

    private async Task<string?> Update(SoftwareItem item, CancellationToken ct)
    {
        if (FlatpakRow(item) is { } fp) return await Flatpak.UpdateAsync(fp.Id, fp.Installation, ProgressFor(item), ct);
        if (AppImageRecord(item) is { } rec)
        {
            var m = item.Methods.FirstOrDefault(x => x.Kind == InstallMethod.AppImage) ?? new InstallMethod(InstallMethod.AppImage) { GitHub = rec.GitHub };
            return await AppImages.InstallAsync(new SoftwareItem { Source = item.Source, Id = rec.Id, Name = rec.Name, Version = item.Version }, m, ProgressFor(item), ct);
        }
        return "There's no update for it here (pacman packages update with the system: Settings → Software Updates).";
    }

    private async Task<string?> Remove(SoftwareItem item, CancellationToken ct)
    {
        if (FlatpakRow(item) is { } fp) return await Flatpak.RemoveAsync(fp.Id, fp.Installation, ct);
        if (AppImageRecord(item) is { } rec) return AppImages.Remove(rec);
        if (item.Methods.FirstOrDefault(m => m.Kind == InstallMethod.Pacman) is { Package: { } pkg } && _pacmanInstalled.Contains(pkg))
            return await SystemUpdates.RemoveByNameAsync(new[] { pkg }, ProgressFor(item), ct);
        return "It isn't installed in a way Software can remove.";
    }

    private bool CanOpen(SoftwareItem item) => FlatpakRow(item) != null || AppImageRecord(item) != null;

    private void Open(SoftwareItem item)
    {
        if (FlatpakRow(item) is { } fp) Flatpak.Run(fp.Id);
        else if (AppImageRecord(item) is { } rec) AppImages.Run(rec);
    }

    private Flatpak.Installed? FlatpakRow(SoftwareItem item)
    {
        var r = item.Methods.FirstOrDefault(m => m.Kind == InstallMethod.Flatpak)?.Ref;
        var refId = r?.Split('/') is { Length: >= 2 } parts ? parts[1] : r;
        var id = item.Source == SoftwareSource.Flatpak ? item.Id : refId;
        return id == null ? null : _flatpakInstalled.FirstOrDefault(f => f.Id == id);
    }

    private static AppImages.Record? AppImageRecord(SoftwareItem item) =>
        AppImages.InstalledRecords().FirstOrDefault(r => r.Id == item.Id);

    private async Task UpdateAll()
    {
        foreach (var item in _installed.Where(i => i.UpdateAvailable).ToList())
            await Do(item, "Updating", ct => Update(item, ct));
    }

    private async Task InstallFlatpakItself()
    {
        var err = await SystemUpdates.InstallByNameAsync(new[] { "flatpak" }, null);
        if (err != null) { Notifier.Show("Software", "Couldn't install Flatpak: " + err, "software"); return; }
        await LoadFlatpak(prepare: true);
    }

    private async Task AddAppImageFile()
    {
        var r = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Add an AppImage",
            FileTypeFilter = new[] { new FilePickerFileType("AppImages") { Patterns = new[] { "*.AppImage", "*.appimage" } } },
        });
        var path = r.FirstOrDefault()?.TryGetLocalPath();
        if (path == null) return;
        var err = await AppImages.AddFileAsync(path, CancellationToken.None);
        Notifier.Show("Software", err == null ? $"{Path.GetFileName(path)} is in your Applications folder and the Start menu." : "Couldn't add it: " + err, "software");
        await RefreshInstalled();
        Render();
    }

    // ── loading ──

    private async Task LoadOurs(bool fetch)
    {
        (_ours, _oursNote) = await OurCatalog.LoadAsync(SettingsStore.Current.SoftwareCatalogUrl, fetch);
        MarkInstalled(_ours);
        if (_detail == null) Render();
    }

    private async Task LoadFlatpak(bool prepare)
    {
        if (_loadingFlatpak) return;
        if (!Flatpak.Available) { _loadedFlatpak = true; if (_detail == null) Render(); return; }
        _loadingFlatpak = true;
        if (_detail == null) Render();
        try
        {
            if (prepare || Flatpak.Catalogue().Count == 0)
            {
                // fetched only when asked, or the first time: it's a few megabytes
                if (prepare || _place == Place.Flatpak)
                {
                    var err = await Flatpak.PrepareAsync(CancellationToken.None);
                    if (err != null) Notifier.Show("Software", err, "software");
                }
            }
            _flatpaks = await Task.Run(() => Flatpak.Catalogue());
        }
        finally { _loadingFlatpak = false; _loadedFlatpak = true; }
        MarkInstalled(_flatpaks);
        if (_detail == null) Render();
    }

    private async Task LoadAppImages(bool fetch)
    {
        if (_loadingAppImage) return;
        _loadingAppImage = true;
        if (_detail == null && _place == Place.AppImage) Render();
        try { (_appimages, _appimageNote) = await AppImages.CatalogueAsync(fetch, CancellationToken.None); }
        finally { _loadingAppImage = false; _loadedAppImage = true; }
        MarkInstalled(_appimages);
        if (_detail == null) Render();
    }

    /// <summary>What's installed from each source, and (when asked) which have updates.</summary>
    private async Task RefreshInstalled(bool checkUpdates = false)
    {
        _flatpakInstalled = await Flatpak.InstalledAsync();
        if (checkUpdates)
        {
            _flatpakUpdates = await Flatpak.UpdatesAsync();
            _appImageUpdates.Clear();
            foreach (var (k, v) in await AppImages.UpdatesAsync(CancellationToken.None)) _appImageUpdates[k] = v;
        }
        var pacmanNames = _ours.SelectMany(i => i.Methods).Where(m => m.Kind == InstallMethod.Pacman && m.Package != null).Select(m => m.Package!).Distinct().ToList();
        try { _pacmanInstalled = pacmanNames.Count > 0 && ProcessUtil.Which("pacman") != null ? await SystemUpdates.InstalledAsync(pacmanNames) : new(); }
        catch (Exception) { _pacmanInstalled = new(); }

        var installed = new List<SoftwareItem>();
        foreach (var i in _ours) { MarkOne(i); if (i.Installed) installed.Add(i); }
        var catalogue = _flatpaks;
        foreach (var f in _flatpakInstalled)
        {
            if (installed.Any(i => FlatpakRow(i)?.Id == f.Id)) continue;
            var known = catalogue.FirstOrDefault(c => c.Id == f.Id);
            installed.Add(new SoftwareItem
            {
                Source = SoftwareSource.Flatpak, Id = f.Id, Name = known?.Name ?? f.Name, Summary = known?.Summary ?? "", Description = known?.Description ?? "",
                License = known?.License, Developer = known?.Developer, Homepage = known?.Homepage, Version = known?.Version, IconPath = known?.IconPath,
                Categories = known?.Categories ?? new(), Screenshots = known?.Screenshots ?? new(),
                Methods = { new InstallMethod(InstallMethod.Flatpak) { Remote = f.Origin, Ref = f.Id } },
                Installed = true, InstalledVersion = f.Version, InstalledWhere = f.Installation == "system" ? "the system (all users)" : "your account",
                UpdateAvailable = _flatpakUpdates.Contains(f.Id),
            });
        }
        foreach (var r in AppImages.InstalledRecords())
        {
            if (installed.Any(i => i.Id == r.Id)) continue;
            var known = _appimages.FirstOrDefault(c => c.Id == r.Id);
            installed.Add(new SoftwareItem
            {
                Source = SoftwareSource.AppImage, Id = r.Id, Name = r.Name, Summary = known?.Summary ?? Path.GetFileName(r.File), Description = known?.Description ?? "",
                License = known?.License, Developer = known?.Developer, Homepage = known?.Homepage, IconPath = r.Icon, IconUrl = known?.IconUrl,
                Screenshots = known?.Screenshots ?? new(),
                Version = _appImageUpdates.TryGetValue(r.Id, out var up) ? up.Tag : r.Version,
                Methods = { new InstallMethod(InstallMethod.AppImage) { GitHub = r.GitHub } },
                Installed = true, InstalledVersion = r.Version, InstalledWhere = r.File, UpdateAvailable = _appImageUpdates.ContainsKey(r.Id),
            });
        }
        _installed = installed.OrderByDescending(i => i.UpdateAvailable).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        MarkInstalled(_flatpaks);
        MarkInstalled(_appimages);
    }

    private void MarkInstalled(IEnumerable<SoftwareItem> items) { foreach (var i in items) MarkOne(i); }

    private void MarkOne(SoftwareItem i)
    {
        var fp = FlatpakRow(i);
        var rec = fp == null ? AppImageRecord(i) : null;
        var pacman = i.Methods.FirstOrDefault(m => m.Kind == InstallMethod.Pacman)?.Package is { } pkg && _pacmanInstalled.Contains(pkg);
        i.Installed = fp != null || rec != null || pacman;
        i.InstalledVersion = fp?.Version ?? rec?.Version;
        i.InstalledWhere = fp != null ? (fp.Installation == "system" ? "the system (all users)" : "your account") : rec?.File ?? (pacman ? "the system (pacman)" : null);
        i.UpdateAvailable = (fp != null && _flatpakUpdates.Contains(fp.Id)) || (rec != null && _appImageUpdates.ContainsKey(rec.Id))
                            || (i.Source == SoftwareSource.CrimsonBanner && rec != null && i.Version != null && rec.Version != i.Version);
    }

    // ── pictures ──

    private static readonly Dictionary<string, Bitmap?> Bitmaps = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static string PictureCache => Path.Combine(Path.GetDirectoryName(OurCatalog.CachePath)!, "software-pictures");

    private static Control IconFor(SoftwareItem item, double size)
    {
        var holder = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 5), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        if (Load(item.IconPath, 128) is { } bmp) { holder.Child = new Image { Source = bmp, Stretch = Stretch.Uniform }; return holder; }
        holder.Background = new SolidColorBrush(Color.Parse(item.Source == SoftwareSource.CrimsonBanner ? "#C8102E" : AppIcons.NeutralColor));
        holder.Child = new TextBlock { Text = item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?", FontSize = size * 0.45, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (item.IconUrl is { } url && url.StartsWith("https://", StringComparison.Ordinal))
            _ = Fetch(url).ContinueWith(t =>
            {
                if (t.Result is { } path) Dispatcher.UIThread.Post(() =>
                {
                    if (Load(path, 128) is { } b) { holder.Background = null; holder.Child = new Image { Source = b, Stretch = Stretch.Uniform }; }
                });
            }, TaskScheduler.Default);
        return holder;
    }

    private static Control Screenshot(string url)
    {
        var frame = new Border { Width = 400, Height = 250, CornerRadius = new CornerRadius(10), ClipToBounds = true, Classes = { "card" }, Padding = new Thickness(0) };
        frame.Child = new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _ = Fetch(url).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            frame.Child = t.Result is { } path && Load(path, 800) is { } b
                ? new Image { Source = b, Stretch = Stretch.Uniform }
                : new TextBlock { Text = "The picture couldn't be loaded.", Classes = { "caption" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }), TaskScheduler.Default);
        return frame;
    }

    private static Bitmap? Load(string? path, int width)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return null;
        var key = path + "@" + width;
        lock (Bitmaps)
        {
            if (Bitmaps.TryGetValue(key, out var b)) return b;
            try { using var s = File.OpenRead(path); b = Bitmap.DecodeToWidth(s, width); }
            catch (Exception) { b = null; }
            Bitmaps[key] = b;
            return b;
        }
    }

    /// <summary>A picture from the web, kept in the cache (PNG and JPEG only).</summary>
    private static async Task<string?> Fetch(string url)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
        var path = Path.Combine(PictureCache, name);
        if (File.Exists(path)) return path;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P';
            bool jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
            if (!png && !jpeg) return null;
            Directory.CreateDirectory(PictureCache);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception) { return null; }
    }
}
