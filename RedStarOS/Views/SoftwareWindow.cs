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
    private readonly Dictionary<Place, (Border Bar, Border Badge, TextBlock BadgeText)> _navParts = new();
    private readonly Avalonia.Controls.Shapes.Ellipse _statusDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center };
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

        // the deck look (Styles/Shell.axaml, "Panels"): a backdrop with panels lifted off it
        this.Bind(BackgroundProperty, this.GetResourceObservable("ShellBackdropBrush"));

        // ── sidebar ──
        var side = new DockPanel { Margin = new Thickness(14, 20, 14, 16) };
        var brand = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 20) };
        brand.Children.Add(Kicker("CRIMSON BANNER OS", accent: true));
        brand.Children.Add(new TextBlock { Text = "Software", Classes = { "h1" } });
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        var foot = Kicker("FREE SOFTWARE ONLY");
        foot.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(foot, Dock.Bottom);
        side.Children.Add(foot);
        var navList = new StackPanel { Spacing = 4 };
        foreach (var (p, label, glyph) in new[]
                 {
                     (Place.Explore, "Explore", "grid"), (Place.CrimsonBanner, "Crimson Banner", "bag"), (Place.Flatpak, "Flatpak", "download"),
                     (Place.AppImage, "AppImage", "folder"), (Place.Installed, "Installed", "list"),
                 })
        {
            var b = new Button { Classes = { "nav" } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
            var bar = new Border { Width = 3, Height = 18, CornerRadius = new CornerRadius(2), Margin = new Thickness(-6, 0, 9, 0), IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
            bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable("ShellAccentBrush"));
            var icon = Icons.Create(glyph, 16);
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var badgeText = new TextBlock();
            var badge = new Border { Classes = { "badge", "accent" }, Child = badgeText, IsVisible = false };
            Grid.SetColumn(icon, 1); Grid.SetColumn(text, 2); Grid.SetColumn(badge, 3);
            row.Children.Add(bar); row.Children.Add(icon); row.Children.Add(text); row.Children.Add(badge);
            b.Content = row;
            b.Click += (_, _) => _ = Go(p);
            _nav[p] = b;
            _navParts[p] = (bar, badge, badgeText);
            navList.Children.Add(b);
        }
        side.Children.Add(navList);
        var sideBorder = new Border { Width = 236, BorderThickness = new Thickness(0, 0, 1, 0), Child = side };
        sideBorder.Bind(Border.BorderBrushProperty, sideBorder.GetResourceObservable("ShellRuleBrush"));

        // ── top bar: what Software is doing, and the search ──
        _search.Classes.Add("pill-field");
        _search.Width = 340;
        _search.InnerLeftContent = new Border { Margin = new Thickness(12, 0, 0, 0), Opacity = 0.6, Child = Icons.Create("search", 14) };
        _search.TextChanged += (_, _) => OnSearch();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape) _search.Text = ""; };
        var status = new Border { Classes = { "status" }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        status.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _statusDot, _statusText } };
        var top = new DockPanel { Margin = new Thickness(28, 18, 28, 10) };
        DockPanel.SetDock(_search, Dock.Right);
        top.Children.Add(_search);
        top.Children.Add(status);

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
        var updates = _installed.Count(i => i.UpdateAvailable);
        foreach (var (k, b) in _nav)
        {
            var on = k == _place || (_place == Place.Search && k == _beforeSearch);
            b.Classes.Set("on", on);
            var (bar, badge, badgeText) = _navParts[k];
            bar.IsVisible = on;
            badge.IsVisible = k == Place.Installed && updates > 0;
            badgeText.Text = updates.ToString();
        }
        UpdateStatus(updates);

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
    private static StackPanel Column() => new() { Spacing = 16, Margin = new Thickness(28, 6, 28, 32) };

    private Control ExplorePage()
    {
        var col = Column();

        // the hero: what Software is, and how much is in it, on the deck's display
        var hero = new Border { Classes = { "panel" }, Padding = new Thickness(28, 24), ClipToBounds = true };
        var heroGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var words = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        words.Children.Add(Kicker("SOFTWARE · FREE PROGRAMS ONLY", accent: true));
        words.Children.Add(new TextBlock { Text = "Everything for Crimson Banner OS", Classes = { "display" } });
        words.Children.Add(Muted("Our own catalogue, Flathub and AppImageHub, in one place. Every program here is free software, checked before it's shown."));
        heroGrid.Children.Add(words);
        var lcd = Lcd(("OURS", _ours.Count, 2), ("FLATPAK", _flatpaks.Count, 3), ("APPIMAGE", _appimages.Count, 3));
        lcd.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(lcd, 1);
        heroGrid.Children.Add(lcd);
        hero.Child = new Panel { Children = { Glow(), heroGrid } };
        col.Children.Add(hero);

        // our own
        col.Children.Add(SectionHead("FROM CRIMSON BANNER OS", _ours.Count, "ALL", () => _ = Go(Place.CrimsonBanner)));
        if (_ours.Count > 0) col.Children.Add(CardGrid(_ours.Take(6), showSource: false));
        else col.Children.Add(EmptyCard("bag", "Nothing published yet", _oursNote ?? "Programs made for Crimson Banner OS that are too big or specialised for every install will appear here."));

        // updates waiting
        var updates = _installed.Where(i => i.UpdateAvailable).ToList();
        if (updates.Count > 0)
        {
            col.Children.Add(SectionHead("UPDATES", updates.Count, "UPDATE ALL", () => _ = UpdateAll()));
            col.Children.Add(CardGrid(updates.Take(6), showSource: true));
        }

        // picks from Flathub
        col.Children.Add(SectionHead("PICKS FROM FLATHUB", null, "BROWSE FLATHUB", () => _ = Go(Place.Flatpak)));
        var picks = Picks.Select(id => _flatpaks.FirstOrDefault(f => f.Id == id)).Where(f => f != null).Cast<SoftwareItem>().Take(9).ToList();
        if (picks.Count > 0) col.Children.Add(CardGrid(picks, showSource: false));
        else if (_loadingFlatpak) col.Children.Add(Loading("Reading Flathub's catalogue…"));
        else if (!Flatpak.Available) col.Children.Add(EmptyCard("download", "Flatpak isn't installed", "Flatpak (free software from freedesktop.org) brings most desktop programs, from Flathub.", ("Install Flatpak", new Func<Task>(InstallFlatpakItself))));
        else col.Children.Add(EmptyCard("download", "Flathub isn't set up yet", "Its catalogue is fetched once and kept on this computer.", ("Set up Flathub", new Func<Task>(() => LoadFlatpak(prepare: true)))));

        // categories
        col.Children.Add(SectionHead("BROWSE BY CATEGORY", null, null, null));
        var cats = new ResponsiveGrid { MinItemWidth = 180, Gap = 12 };
        foreach (var (key, label, glyph) in Categories)
        {
            var b = new Button { Classes = { "panel" }, Padding = new Thickness(12, 10) };
            b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Disc(glyph, 34), new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center } } };
            b.Click += (_, _) => _ = Go(Place.Flatpak, key);
            cats.Children.Add(b);
        }
        col.Children.Add(cats);
        return Scroll(col);
    }

    private Control SourcePage(Place p)
    {
        var col = Column();
        var (kicker, title, about) = p switch
        {
            Place.CrimsonBanner => ("OUR OWN · SIGNED", "Crimson Banner OS", "Programs made for Crimson Banner OS, signed with the project's key. On Arch they come from the [crimson-banner-apps] repository and update with the system."),
            Place.Flatpak => ("FLATHUB", "Flatpak", "Free programs from Flathub (run by the GNOME Foundation with KDE). New ones go into your own account: no password needed."),
            _ => ("APPIMAGEHUB", "AppImage", "Free programs from AppImageHub, downloaded from each project's own release into your Applications folder, checked and added to the Start menu."),
        };

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        void Action(string text, Func<Task> run, bool accent = false)
        {
            var b = Pill(text, accent);
            b.Margin = new Thickness(0, 0, 8, 8);
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
                if (Flatpak.Available) Action(_flatpaks.Count == 0 ? "Set up Flathub" : "Refresh", () => LoadFlatpak(prepare: true), accent: _flatpaks.Count == 0);
                else Action("Install Flatpak", new Func<Task>(InstallFlatpakItself), accent: true);
                break;
            default:
                items = _appimages.Where(i => _showUnstated || i.Licence == FreeLicences.Verdict.Free).ToList();
                note = _appimageNote; loading = _loadingAppImage;
                Action("Add an AppImage file…", AddAppImageFile);
                Action("Refresh", () => LoadAppImages(fetch: true));
                var unstated = new CheckBox { Content = "Include ones that don't state a licence", IsChecked = _showUnstated, Margin = new Thickness(4, 0, 0, 8) };
                unstated.IsCheckedChanged += (_, _) => { _showUnstated = unstated.IsChecked == true; Render(); };
                actions.Children.Add(unstated);
                break;
        }

        var head = new Border { Classes = { "panel" }, Padding = new Thickness(26, 22), ClipToBounds = true };
        var hs = new StackPanel { Spacing = 6 };
        hs.Children.Add(Kicker(kicker, accent: true));
        hs.Children.Add(new TextBlock { Text = title, Classes = { "display" } });
        hs.Children.Add(Muted(about));
        actions.Margin = new Thickness(0, 10, 0, -8);
        hs.Children.Add(actions);
        if (note != null) hs.Children.Add(Muted(note));
        head.Child = new Panel { Children = { Glow(), hs } };
        col.Children.Add(head);

        // category pills: the ones these programs use
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
        var updates = _installed.Count(i => i.UpdateAvailable);
        var head = new Border { Classes = { "panel" }, Padding = new Thickness(26, 22), ClipToBounds = true };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var hs = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        hs.Children.Add(Kicker("ON THIS COMPUTER", accent: true));
        hs.Children.Add(new TextBlock { Text = "Installed", Classes = { "display" } });
        hs.Children.Add(Muted(updates > 0 ? $"{updates} update{(updates == 1 ? "" : "s")} waiting." : _installed.Count > 0 ? "Everything is up to date." : "Programs installed from any of the three sources appear here, with their updates."));
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (updates > 0) { var u = Pill("Update all", accent: true); u.Margin = new Thickness(0, 0, 8, 0); u.Click += async (_, _) => await UpdateAll(); actions.Children.Add(u); }
        var check = Pill("Check for updates");
        check.Click += async (_, _) => { check.IsEnabled = false; check.Content = "CHECKING…"; await RefreshInstalled(checkUpdates: true); Render(); };
        actions.Children.Add(check);
        hs.Children.Add(actions);
        g.Children.Add(hs);
        var lcd = Lcd(("INSTALLED", _installed.Count, 2), ("UPDATES", updates, 2));
        lcd.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(lcd, 1);
        g.Children.Add(lcd);
        head.Child = new Panel { Children = { Glow(), g } };
        col.Children.Add(head);
        if (_installed.Count == 0) col.Children.Add(EmptyCard("list", "Nothing installed from here yet", "Programs installed from any of the three sources appear here, with their updates."));
        else AddGridWithMore(col, _installed, showSource: true);
        return Scroll(col);
    }

    private Control SearchPage()
    {
        var q = (_search.Text ?? "").Trim();
        var col = Column();
        var head = new StackPanel { Spacing = 4, Margin = new Thickness(4, 6, 0, 4) };
        head.Children.Add(Kicker("SEARCH · ALL SOURCES", accent: true));
        head.Children.Add(new TextBlock { Text = $"“{q}”", Classes = { "display" } });
        col.Children.Add(head);
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
        var back = Pill("‹  Back");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Click += (_, _) => { _detail = null; Render(); };
        col.Children.Add(back);

        // the hero: icon, name, what it is, and what can be done with it
        var hero = new Border { Classes = { "panel" }, Padding = new Thickness(26, 24), ClipToBounds = true };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(IconFor(item, 104));
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 6, Margin = new Thickness(22, 0) };
        title.Children.Add(Kicker((item.Developer is { Length: > 0 } dev ? dev : SourceName(item.Source)).ToUpperInvariant(), accent: true));
        title.Children.Add(new TextBlock { Text = item.Name, Classes = { "display" } });
        if (item.Summary.Length > 0) title.Children.Add(new TextBlock { Text = item.Summary, FontSize = 15, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        var badges = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        badges.Children.Add(Badge(SourceName(item.Source)));
        if (item.Licence == FreeLicences.Verdict.Free) badges.Children.Add(Badge("Free software"));
        if (item.Version is { Length: > 0 } v) badges.Children.Add(Badge("Version " + v));
        if (item.UpdateAvailable) badges.Children.Add(Badge("Update available", accent: true));
        else if (item.Installed) badges.Children.Add(Badge("Installed"));
        title.Children.Add(badges);
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var buttons = Buttons(item);
        Grid.SetColumn(buttons, 2);
        head.Children.Add(buttons);
        hero.Child = new Panel { Children = { Glow(), head } };
        col.Children.Add(hero);

        if (item.Screenshots.Count > 0)
        {
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            foreach (var url in item.Screenshots.Take(6)) strip.Children.Add(Screenshot(url));
            col.Children.Add(new ScrollViewer { Content = strip, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(0, 2) });
        }

        // about it, and the facts
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,320") };
        var about = new Border { Classes = { "panel" }, VerticalAlignment = VerticalAlignment.Top };
        var aboutCol = new StackPanel { Spacing = 10 };
        aboutCol.Children.Add(Kicker("ABOUT"));
        aboutCol.Children.Add(new SelectableTextBlock
        {
            Text = item.Description.Length > 0 ? item.Description : item.Summary.Length > 0 ? item.Summary : "Its project hasn't written a description.",
            TextWrapping = TextWrapping.Wrap, LineHeight = 22,
        });
        about.Child = aboutCol;
        body.Children.Add(about);
        var facts = Facts(item);
        Grid.SetColumn(facts, 2);
        body.Children.Add(facts);
        col.Children.Add(body);
        return Scroll(col);
    }

    private Control Buttons(SoftwareItem item)
    {
        var panel = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, MinWidth = 190 };
        if (_busy.TryGetValue(BusyKey(item), out var busy))
        {
            _detailBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = busy.Pct, Height = 6, IsIndeterminate = busy.Pct <= 0 };
            panel.Children.Add(Kicker("WORKING…", accent: true));
            panel.Children.Add(_detailBar);
            var cancel = Pill("Cancel", large: true);
            cancel.HorizontalAlignment = HorizontalAlignment.Stretch;
            cancel.Click += (_, _) => busy.Cts.Cancel();
            panel.Children.Add(cancel);
            return panel;
        }
        Button Btn(string text, Func<Task> act, bool primary)
        {
            var b = Pill(text, accent: primary, large: true);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
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
        var card = new Border { Classes = { "panel" }, VerticalAlignment = VerticalAlignment.Top };
        var list = new StackPanel { Spacing = 12 };
        list.Children.Add(Kicker("DETAILS"));
        void Fact(string k, string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            list.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children = { new TextBlock { Text = k.ToUpperInvariant(), Classes = { "kicker" }, FontSize = 10 }, new SelectableTextBlock { Text = v, TextWrapping = TextWrapping.Wrap } },
            });
        }
        Fact("From", SourceName(item.Source) + (item.Source == SoftwareSource.Flatpak ? " (" + (item.Methods.FirstOrDefault()?.Remote ?? Flatpak.Flathub) + ")" : ""));
        Fact("Licence", item.License is { Length: > 0 } l ? l + (item.Licence == FreeLicences.Verdict.Free ? " · free software" : "") : "Not stated: check the project's page before relying on it");
        Fact("Version", item.Installed && item.InstalledVersion is { Length: > 0 } iv && iv != item.Version
            ? iv + (item.Version is { Length: > 0 } nv ? $" (latest {nv})" : "") : item.Version ?? item.InstalledVersion);
        Fact("Installed in", item.Installed ? item.InstalledWhere : null);
        Fact("Website", item.Homepage);
        Fact("Categories", item.Categories.Count > 0 ? string.Join(", ", item.Categories.Take(5)) : null);
        card.Child = list;
        return card;
    }

    // ── building blocks (the deck look: Styles/Shell.axaml, "Panels") ──

    private static TextBlock Kicker(string text, bool accent = false)
    {
        var t = new TextBlock { Text = text, Classes = { "kicker" }, TextWrapping = TextWrapping.Wrap };
        if (accent) t.Classes.Add("accent");
        return t;
    }

    private static TextBlock Muted(string text) => new() { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

    private static Button Pill(string text, bool accent = false, bool large = false)
    {
        var b = new Button { Content = text.ToUpperInvariant(), Classes = { "pill" } };
        if (accent) b.Classes.Add("accent");
        if (large) b.Classes.Add("large");
        return b;
    }

    private static Border Badge(string text, bool accent = false)
    {
        var b = new Border { Classes = { "badge" }, Margin = new Thickness(0, 0, 6, 4), Child = new TextBlock { Text = text.ToUpperInvariant() } };
        if (accent) b.Classes.Add("accent");
        return b;
    }

    /// <summary>The deck's display: a label over red digits, for each figure.</summary>
    private static Border Lcd(params (string Label, int Value, int Digits)[] figures)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26 };
        foreach (var (label, value, digits) in figures)
            row.Children.Add(new StackPanel
            {
                Spacing = 1,
                Children =
                {
                    new TextBlock { Text = label, Classes = { "lcd-label" } },
                    new TextBlock { Text = Math.Min(value, (int)Math.Pow(10, digits) - 1).ToString(new string('0', digits)), Classes = { "lcd-digits" } },
                },
            });
        return new Border { Classes = { "lcd" }, Child = row, Padding = new Thickness(20, 12) };
    }

    /// <summary>A faint wash of the accent colour from the panel's top left, as the deck's lit panels have.</summary>
    private Control Glow()
    {
        var c = this.TryFindResource("ShellAccentBrush", out var a) && a is ISolidColorBrush sb ? sb.Color : Color.Parse("#C8102E");
        return new Border
        {
            Margin = new Thickness(-30, -26), IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.75, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0x30, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0.7) },
            },
        };
    }

    /// <summary>A glyph in a round, softly lit disc (categories, empty pages).</summary>
    private static Control Disc(string glyph, double size)
    {
        var d = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = Icons.Create(glyph, size * 0.5), VerticalAlignment = VerticalAlignment.Center };
        d.Bind(Border.BackgroundProperty, d.GetResourceObservable("ShellAccentSoftBrush"));
        return d;
    }

    private Control SectionHead(string title, int? count, string? action, Action? go)
    {
        var d = new DockPanel { Margin = new Thickness(4, 14, 0, 0) };
        if (action != null && go != null)
        {
            var b = Pill(action + "  ›");
            b.Click += (_, _) => go();
            DockPanel.SetDock(b, Dock.Right);
            d.Children.Add(b);
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Kicker(title));
        if (count is { } n) { var badge = Badge(n.ToString()); badge.Margin = new Thickness(0); left.Children.Add(badge); }
        d.Children.Add(left);
        return d;
    }

    private Control CardGrid(IEnumerable<SoftwareItem> items, bool showSource)
    {
        var g = new ResponsiveGrid { MinItemWidth = 250, Gap = 14 };
        foreach (var i in items) g.Children.Add(Card(i, showSource));
        return g;
    }

    private void AddGridWithMore(StackPanel col, List<SoftwareItem> items, bool showSource)
    {
        var count = Kicker($"{items.Count} PROGRAM{(items.Count == 1 ? "" : "S")}");
        count.Margin = new Thickness(4, 6, 0, 0);
        col.Children.Add(count);
        col.Children.Add(CardGrid(items.Take(_shown), showSource));
        if (items.Count > _shown)
        {
            var more = Pill($"Show more ({items.Count - _shown} left)");
            more.HorizontalAlignment = HorizontalAlignment.Center;
            more.Click += (_, _) => { _shown += PageSize; Render(); };
            col.Children.Add(more);
        }
    }

    private static readonly IBrush InstalledInk = new SolidColorBrush(Color.Parse("#38A169"));
    private static readonly IBrush UpdateInk = new SolidColorBrush(Color.Parse("#DD6B20"));

    private Control Card(SoftwareItem item, bool showSource)
    {
        var b = new Button { Classes = { "panel" }, MinHeight = 108 };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        g.Children.Add(IconFor(item, 56));
        var text = new StackPanel { Spacing = 3, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = item.Name, FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Summary.Length > 0) text.Children.Add(new TextBlock { Text = item.Summary, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var foot = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };   // wraps in a narrow card
        if (showSource) { var src = Badge(SourceName(item.Source)); src.Margin = new Thickness(0, 0, 8, 2); foot.Children.Add(src); }
        if (item.UpdateAvailable || item.Installed)
            foot.Children.Add(new TextBlock
            {
                Text = item.UpdateAvailable ? "● UPDATE AVAILABLE" : "● INSTALLED", FontSize = 10, FontWeight = FontWeight.Bold, LetterSpacing = 0.5,
                Foreground = item.UpdateAvailable ? UpdateInk : InstalledInk, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 2),
            });
        if (foot.Children.Count > 0) text.Children.Add(foot);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        b.Content = g;
        b.Click += (_, _) => { _detail = item; Render(); };
        return b;
    }

    private Control Chip(string label, bool on, Action pick)
    {
        var b = Pill(label);
        b.Margin = new Thickness(0, 0, 6, 6);
        b.Classes.Set("on", on);
        b.Click += (_, _) => pick();
        return b;
    }

    private Control EmptyCard(string glyph, string title, string text, (string Label, Func<Task> Run)? action = null)
    {
        var card = new Border { Classes = { "panel" }, Padding = new Thickness(22, 20) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        var disc = Disc(glyph, 48);
        disc.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(disc);
        var col = new StackPanel { Spacing = 6, MaxWidth = 640 };
        col.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        col.Children.Add(Muted(text));
        if (action is { } a)
        {
            var b = Pill(a.Label, accent: true);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.Margin = new Thickness(0, 8, 0, 0);
            b.Click += async (_, _) => { b.IsEnabled = false; try { await a.Run(); } finally { b.IsEnabled = true; } };
            col.Children.Add(b);
        }
        row.Children.Add(col);
        card.Child = row;
        return card;
    }

    private static Control Loading(string text) => new Border
    {
        Classes = { "panel" }, Padding = new Thickness(22, 18),
        Child = new StackPanel
        {
            Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left,
            Children = { new TextBlock { Text = text.ToUpperInvariant(), Classes = { "kicker", "accent" } }, new ProgressBar { IsIndeterminate = true, Width = 280, Height = 4 } },
        },
    };

    /// <summary>The top bar's status pill: what Software is doing right now.</summary>
    private void UpdateStatus(int updates)
    {
        var (text, ink) = _busy.Count > 0 ? ($"WORKING ON {_busy.Count}", UpdateInk)
            : _loadingFlatpak ? ("READING FLATHUB…", UpdateInk)
            : _loadingAppImage ? ("READING APPIMAGEHUB…", UpdateInk)
            : updates > 0 ? ($"{updates} UPDATE{(updates == 1 ? "" : "S")} WAITING", (IBrush?)null)
            : ("READY", InstalledInk);
        _statusText.Text = text;
        if (ink != null) { _statusDot.Fill = ink; _statusText.Foreground = ink; }
        else
        {
            var accent = this.TryFindResource("ShellAccentBrush", out var a) && a is IBrush ab ? ab : UpdateInk;
            _statusDot.Fill = accent; _statusText.Foreground = accent;
        }
    }

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
        var frame = new Border { Width = 420, Height = 262, CornerRadius = new CornerRadius(12), ClipToBounds = true, Classes = { "panel" }, Padding = new Thickness(0) };
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
