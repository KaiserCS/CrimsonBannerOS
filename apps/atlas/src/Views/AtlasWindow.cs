// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CrimsonAtlas.Model;
using static CrimsonBanner.Apps.Deck;

namespace CrimsonAtlas.Views;

/// <summary>
/// Atlas: maps of the world that live on this computer. They come from the Mapsforge project (made from
/// OpenStreetMap), one country or region at a time; once downloaded, the map, its search and everything on it work
/// without the network.
/// </summary>
public sealed class AtlasWindow : Window
{
    private readonly MapStore _store;
    private readonly bool _offline;
    private readonly AtlasSettings _settings;
    private readonly Mirror _mirror = new();
    private readonly MapView _view = new();
    private readonly Grid _body = new();
    private readonly StackPanel _nav = new() { Spacing = 2 };
    private readonly TextBlock _mapLine = new() { Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private string _page = "map";
    private Control? _mapPage;
    private List<Place> _places = new();
    private CancellationTokenSource? _indexing;
    private readonly TextBox _search = new() { PlaceholderText = "Search places on this map", Width = 320 };
    private readonly Border _results = new() { IsVisible = false, Classes = { "panel" }, Padding = new Thickness(6), Width = 360, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ComboBox _mapPick = new() { MinWidth = 200 };
    private readonly Button _themeButton = Pill("Day");
    private readonly TextBlock _where = new() { Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

    // the Maps page
    private string _folder = "";
    private List<Region>? _regions;
    private string _message = "";
    private bool _messageBad;
    private bool _listing;
    private Region? _downloading;
    private (long Got, long? Total) _progress;
    private CancellationTokenSource? _download;

    public AtlasWindow() : this(new MapStore(), offline: false) { }

    public AtlasWindow(MapStore store, bool offline)
    {
        Title = "Atlas";
        Width = 1220; Height = 820; MinWidth = 900; MinHeight = 580;
        _store = store;
        _offline = offline;
        _settings = offline ? new AtlasSettings() : AtlasSettings.Load();
        _view.Synchronous = offline;
        _view.SetTheme(Palette.Named(_settings.Theme ?? (Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? "day" : "night")));

        var side = new DockPanel { Width = 206, Margin = new Thickness(14, 16, 6, 14) };
        var brand = new StackPanel { Spacing = 2, Margin = new Thickness(10, 2, 0, 18) };
        brand.Children.Add(Kicker("CRIMSON BANNER OS", accent: true));
        brand.Children.Add(new TextBlock { Text = "Atlas", FontSize = 21, FontWeight = FontWeight.Bold });
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        var foot = new StackPanel { Spacing = 6, Margin = new Thickness(10, 10, 4, 0) };
        foot.Children.Add(Kicker("THE WORLD, OFFLINE"));
        foot.Children.Add(_mapLine);
        DockPanel.SetDock(foot, Dock.Bottom);
        side.Children.Add(foot);
        side.Children.Add(_nav);
        foreach (var (id, label) in new[] { ("map", "Map"), ("maps", "Maps"), ("about", "About the maps") })
        {
            var b = new Button { Classes = { "nav" }, Content = new TextBlock { Text = label, FontSize = 14 }, Tag = id };
            b.Click += (_, _) => ShowPage(id);
            _nav.Children.Add(b);
        }
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        root.Children.Add(side);
        Grid.SetColumn(_body, 1);
        root.Children.Add(_body);
        Content = root;

        _view.ViewChanged += () => _where.Text = Where();
        _search.KeyUp += (_, e) => { if (e.Key == Key.Enter) Choose(Places.Search(_places, _search.Text ?? "", 1).FirstOrDefault()); else ShowResults(); };
        _search.LostFocus += (_, _) => DispatcherTimer.RunOnce(() => _results.IsVisible = false, TimeSpan.FromMilliseconds(250));
        _themeButton.Click += (_, _) =>
        {
            _view.SetTheme(_view.Palette == Palette.Day ? Palette.Night : Palette.Day);
            _settings.Theme = _view.Palette.Name;
            _themeButton.Content = (_view.Palette == Palette.Day ? "Night" : "Day").ToUpperInvariant();
        };
        _themeButton.Content = (_view.Palette == Palette.Day ? "Night" : "Day").ToUpperInvariant();
        _mapPick.SelectionChanged += (_, _) =>
        {
            if (_mapPick.SelectedItem is ComboBoxItem { Tag: string f } && f != _view.Map?.Path) OpenMap(f);
        };
        Closed += (_, _) =>
        {
            _indexing?.Cancel(); _download?.Cancel();
            if (!_offline && _view.Map != null)
            {
                var (lat, lon) = _view.Center;
                (_settings.Map, _settings.Lat, _settings.Lon, _settings.Zoom) = (_view.Map.Path, lat, lon, _view.Zoom);
                _settings.Save();
            }
            _view.Map?.Dispose();
            _mirror.Dispose();
        };

        var maps = _store.Maps();
        var first = _settings.Map is { } m && maps.Contains(m) ? m : maps.FirstOrDefault();
        if (first != null)
        {
            bool restore = first == _settings.Map && _settings.Zoom is > 0;
            Opened += (_, _) =>
            {
                if (_view.Map?.Path == first) return;
                OpenMap(first, restore ? ((double, double)?)(_settings.Lat, _settings.Lon) : null, restore ? _settings.Zoom : null);
            };
            if (offline) OpenMap(first);
        }
        ShowPage("map");
    }

    public void ShowPage(string page)
    {
        _page = page;
        foreach (var b in _nav.Children.OfType<Button>())
            if ((string?)b.Tag == page) b.Classes.Add("on"); else b.Classes.Remove("on");
        _mapLine.Text = "Maps are kept in " + _store.Folder;
        _body.Children.Clear();
        if (page == "map") { _body.Children.Add(_mapPage ??= MapPage()); return; }
        var scroll = new ScrollViewer
        {
            Content = page == "maps" ? MapsPage() : AboutPage(),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        _body.Children.Add(scroll);
    }

    private void Refresh() { if (_page != "map") ShowPage(_page); }

    // ───────── the map ─────────

    private Control MapPage()
    {
        var bar = new DockPanel { Margin = new Thickness(10, 14, 16, 10), LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(_search);
        left.Children.Add(_mapPick);
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var minus = Pill("−"); minus.Click += (_, _) => _view.ZoomBy(-1);
        var plus = Pill("+"); plus.Click += (_, _) => _view.ZoomBy(1);
        var whole = Pill("Whole map"); whole.Click += (_, _) => { if (_view.Map is { } mf) _view.Open(mf); };
        right.Children.Add(_where);
        right.Children.Add(whole);
        right.Children.Add(minus);
        right.Children.Add(plus);
        right.Children.Add(_themeButton);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);

        var frame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Margin = new Thickness(10, 0, 16, 16), Child = _view };
        var stack = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        stack.Children.Add(bar);
        Grid.SetRow(frame, 1);
        stack.Children.Add(frame);
        _results.Margin = new Thickness(10, 54, 0, 0);
        stack.Children.Add(_results);
        Grid.SetRowSpan(_results, 2);
        return stack;
    }

    private void FillMapPick()
    {
        _mapPick.Items.Clear();
        foreach (var f in _store.Maps())
        {
            var item = new ComboBoxItem { Content = Path.GetFileNameWithoutExtension(f), Tag = f };
            _mapPick.Items.Add(item);
            if (f == _view.Map?.Path) _mapPick.SelectedItem = item;
        }
    }

    public void OpenMap(string file, (double, double)? at = null, int? zoom = null)
    {
        MapFile map;
        try { map = new MapFile(file); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            Say($"{Path.GetFileName(file)} couldn't be opened: {e.Message}", true);
            ShowPage("maps");
            return;
        }
        var old = _view.Map;
        _view.Open(map, at, zoom);
        old?.Dispose();
        FillMapPick();
        _where.Text = Where();
        _places = new();
        _indexing?.Cancel();
        var cts = _indexing = new CancellationTokenSource();
        _search.PlaceholderText = "Finding the places on this map…";
        if (_offline) { _places = Places.Build(map); _search.PlaceholderText = $"Search {_places.Count} places on this map"; return; }
        Task.Run(() =>
        {
            try
            {
                var list = Places.For(map, cts.Token);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _places = list;
                    _search.PlaceholderText = $"Search {list.Count:N0} places on this map";
                    if (!string.IsNullOrWhiteSpace(_search.Text)) ShowResults();
                });
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException or ObjectDisposedException) { }
        });
    }

    private string Where()
    {
        var (lat, lon) = _view.Center;
        return $"{Math.Abs(lat):0.000}° {(lat >= 0 ? "N" : "S")}  {Math.Abs(lon):0.000}° {(lon >= 0 ? "E" : "W")}  ·  zoom {_view.Zoom}";
    }

    private void ShowResults()
    {
        var found = Places.Search(_places, _search.Text ?? "", 8);
        var col = new StackPanel { Spacing = 2 };
        if (found.Count == 0 && !string.IsNullOrWhiteSpace(_search.Text))
            col.Children.Add(Muted(_places.Count == 0 ? "The places on this map are still being found." : "No place on this map has that name."));
        foreach (var p in found)
        {
            var row = new DockPanel();
            var kind = new TextBlock { Text = p.KindText, Classes = { "muted" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(kind, Dock.Right);
            row.Children.Add(kind);
            row.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Button { Classes = { "nav" }, Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) => Choose(p);
            col.Children.Add(b);
        }
        _results.Child = col;
        _results.IsVisible = col.Children.Count > 0;
    }

    private void Choose(Place? p)
    {
        if (p == null) return;
        _results.IsVisible = false;
        _search.Text = p.Name;
        _view.Pin(p.Lat, p.Lon, p.Name, p.Zoom);
        _view.Focus();
    }

    // ───────── maps: on this computer, and to download ─────────

    private void Say(string text, bool bad = false) { _message = text; _messageBad = bad; }

    private Control MapsPage()
    {
        var col = PageColumn("MAPS · ON THIS COMPUTER", "Maps",
            "Each map is a country or region, made from OpenStreetMap by the Mapsforge project. Download one and it works without the network: roads, towns, rivers, and search.");
        if (_message.Length > 0)
        {
            var t = new TextBlock { Text = _message, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
            if (_messageBad) t.Foreground = Brushes.OrangeRed;
            _message = "";
            col.Children.Add(Panel(t));
        }
        var maps = _store.Maps();
        long total = maps.Sum(f => new FileInfo(f).Length);
        col.Children.Add(Lcd(("MAPS", maps.Count.ToString("00")), ("ON DISK", Mirror.SizeText(total).ToUpperInvariant()), ("SOURCE", "MAPSFORGE")));

        var list = new StackPanel { Spacing = 8 };
        foreach (var f in maps) list.Children.Add(MapRow(f));
        col.Children.Add(list);

        col.Children.Add(Kicker("DOWNLOAD A MAP", accent: true));
        if (_downloading is { } d) col.Children.Add(DownloadPanel(d));
        var crumbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var top = Pill("The world");
        top.Click += (_, _) => Browse("");
        crumbs.Children.Add(top);
        var acc = "";
        foreach (var part in _folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc += part + "/";
            var here = acc;
            crumbs.Children.Add(new TextBlock { Text = "›", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
            var b = Pill(Mirror.Pretty(part), accent: here == _folder);
            b.Click += (_, _) => Browse(here);
            crumbs.Children.Add(b);
        }
        col.Children.Add(crumbs);
        if (_regions == null)
        {
            if (_listing) col.Children.Add(Muted("Asking the Mapsforge server what it has…"));
            else
            {
                var load = Pill("See what's there", accent: true);
                load.Click += (_, _) => Browse(_folder);
                col.Children.Add(new StackPanel
                {
                    Spacing = 10,
                    Children = { Muted("Atlas asks the server only when you ask it to: the list of continents, then a continent's countries."), load },
                });
            }
        }
        else
        {
            var grid = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var r in _regions) grid.Children.Add(RegionCard(r));
            col.Children.Add(grid);
        }
        return col;
    }

    private Control MapRow(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        bool sample = Path.GetFileName(file) == SampleMap.FileName;
        string about;
        try
        {
            using var m = new MapFile(file);
            about = $"{Mirror.SizeText(new FileInfo(file).Length)} · made {m.Date:d MMMM yyyy} · {Math.Abs(m.MinLat):0.#}°{(m.MinLat < 0 ? "S" : "N")} to {Math.Abs(m.MaxLat):0.#}°{(m.MaxLat < 0 ? "S" : "N")}, {Math.Abs(m.MinLon):0.#}°{(m.MinLon < 0 ? "W" : "E")} to {Math.Abs(m.MaxLon):0.#}°{(m.MaxLon < 0 ? "W" : "E")}";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException) { about = "This file couldn't be read: " + e.Message; }
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var open = Pill("Open", accent: _view.Map?.Path != file);
        open.Click += (_, _) => { OpenMap(file); ShowPage("map"); };
        buttons.Children.Add(open);
        if (!sample)
        {
            var del = Pill("Remove");
            del.Click += (_, _) =>
            {
                if (_view.Map?.Path == file) { var shut = _view.Map; _view.Open(null); shut.Dispose(); }
                try { _store.Delete(file); Say(name + " was removed."); }
                catch (IOException e) { Say(name + " couldn't be removed: " + e.Message, true); }
                if (_view.Map == null && _store.Maps().FirstOrDefault() is { } next) OpenMap(next);
                Refresh();
            };
            buttons.Children.Add(del);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var text = new StackPanel { Spacing = 3 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.SemiBold });
        if (sample) title.Children.Add(Badge("EXAMPLE"));
        if (_view.Map?.Path == file) title.Children.Add(Badge("OPEN", accent: true));
        text.Children.Add(title);
        text.Children.Add(Muted(sample ? "An invented island, so there's a map before any is downloaded. " + about : about));
        row.Children.Add(text);
        return Panel(row);
    }

    private Control RegionCard(Region r)
    {
        var col = new StackPanel { Spacing = 6 };
        col.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        if (r.IsFolder)
        {
            col.Children.Add(Muted("Regions"));
            var b = Pill("Open");
            b.Click += (_, _) => Browse(r.Path);
            col.Children.Add(b);
        }
        else
        {
            var part = _store.Partial(r);
            col.Children.Add(Muted(string.Join(" · ", new[] { r.SizeText, r.Date }.Where(s => !string.IsNullOrEmpty(s)))));
            if (_store.Has(r)) col.Children.Add(Badge("ON THIS COMPUTER", accent: true));
            else if (_downloading == r) col.Children.Add(Badge("DOWNLOADING", accent: true));
            else
            {
                var b = Pill(part is long p ? $"Carry on ({Mirror.SizeText(p)} here)" : "Download", accent: true);
                b.IsEnabled = _downloading == null;
                b.Click += (_, _) => Download(r);
                col.Children.Add(b);
            }
        }
        return new Border { Classes = { "panel" }, Child = col, Width = 214, Margin = new Thickness(0, 0, 10, 10) };
    }

    private Control DownloadPanel(Region r)
    {
        var col = new StackPanel { Spacing = 8 };
        var head = new DockPanel();
        var stop = Pill("Stop");
        stop.Click += (_, _) => _download?.Cancel();
        DockPanel.SetDock(stop, Dock.Right);
        head.Children.Add(stop);
        head.Children.Add(new TextBlock { Text = "Downloading " + r.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        col.Children.Add(head);
        long? total = _progress.Total ?? r.Size;
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = total > 0 ? (double)_progress.Got / total.Value : 0, Height = 8, MinWidth = 300, IsIndeterminate = !(total > 0) };
        col.Children.Add(bar);
        col.Children.Add(Muted($"{Mirror.SizeText(_progress.Got)}{(total > 0 ? " of " + Mirror.SizeText(total.Value) : "")}. Stopped or cut off, it carries on from here next time."));
        return Panel(col);
    }

    private async void Browse(string folder)
    {
        _folder = folder;
        _regions = null;
        if (_offline) { Refresh(); return; }
        _listing = true;
        Refresh();
        try { _regions = await _mirror.List(folder, CancellationToken.None); }
        catch (HttpRequestException e) { Say(e.Message, true); }
        catch (TaskCanceledException) { Say("The Mapsforge server took too long to answer. Try again in a while.", true); }
        finally { _listing = false; }
        Refresh();
    }

    private async void Download(Region r)
    {
        if (_downloading != null) return;
        _downloading = r;
        _progress = (_store.Partial(r) ?? 0, r.Size);
        var cts = _download = new CancellationTokenSource();
        Refresh();
        var last = DateTime.MinValue;
        var progress = new Progress<(long, long?)>(p =>
        {
            _progress = p;
            if ((DateTime.UtcNow - last).TotalMilliseconds < 500) return;
            last = DateTime.UtcNow;
            Refresh();
        });
        try
        {
            var file = await _mirror.Download(r.Path, _store.Folder, progress, cts.Token);
            Say($"{r.Name} is on this computer. Open it from the list, or pick it on the map.");
            _downloading = null;
            FillMapPick();
            if (_view.Map == null || Path.GetFileName(_view.Map.Path) == SampleMap.FileName) OpenMap(file);
        }
        catch (OperationCanceledException) { Say($"The download of {r.Name} was stopped. What came is kept: it carries on from there."); }
        catch (HttpRequestException e) { Say(e.Message, true); }
        catch (InvalidDataException e) { Say(e.Message, true); }
        catch (IOException e) { Say($"{r.Name} couldn't be saved: {e.Message}", true); }
        finally { _downloading = null; _download = null; }
        Refresh();
    }

    // ───────── about ─────────

    private Control AboutPage()
    {
        var col = PageColumn("ABOUT THE MAPS", "Where the maps come from",
            "Atlas draws its maps itself, on this computer, from map files anyone may download and share.");
        void Section(string head, string text)
        {
            var s = new StackPanel { Spacing = 6 };
            s.Children.Add(new TextBlock { Text = head, FontSize = 16, FontWeight = FontWeight.SemiBold });
            s.Children.Add(Muted(text));
            col.Children.Add(Panel(s));
        }
        Section("OpenStreetMap", "The map of the world made by its own people: millions of volunteers who survey their streets, villages and rivers. Its data is © OpenStreetMap contributors, free under the Open Database Licence (ODbL): it may be used, shared and changed, with credit, and what's made from it stays as free.");
        Section("The Mapsforge project", "A community project that turns OpenStreetMap into compact files, one per country or region, remade every few weeks, and offers them free. Atlas downloads them from the university mirror the project points programs to (Hochschule Esslingen), or from its own server if the mirror can't be reached.");
        Section("On this computer", $"Maps are kept in {_store.Folder}. Atlas goes online only when you ask for the list of maps or download one; it sends nothing about where you look. A download that stops carries on from where it was.");
        Section("Drawing", "Land, sea and water, woods and fields, buildings, roads by importance, railways and borders, with the names of places placed so they don't overlap. Day for paper colours, Night for the dark. Drag to move, the wheel or + − to zoom, the arrow keys to pan, a double click to look closer.");
        return col;
    }

    /// <summary>A window on the example island, for screenshots: the map, the Maps page with a server's list, or About.</summary>
    public static AtlasWindow Sample(string page, string folder, string theme = "night")
    {
        var store = new MapStore(folder);
        var w = new AtlasWindow(store, offline: true);
        w._view.SetTheme(Palette.Named(theme));
        if (page.StartsWith("map-"))
        {
            // map-search: a place found; map-street: the port, up close
            if (page == "map-search") { w._search.Text = "Port"; w.ShowResults(); w._view.Pin(SampleMap.Port.Lat, SampleMap.Port.Lon, "Port Solidarity", 13); }
            else if (page == "map-street") w._view.GoTo(-30.4505, -20.257, 16);
            page = "map";
        }
        if (page == "maps")
        {
            w._folder = "europe/";
            w._regions = new List<Region>
            {
                new("Albania", "europe/albania.map", false, 24_117_248, "2026-11-02"),
                new("Austria", "europe/austria.map", false, 524_288_000, "2026-11-02"),
                new("France", "europe/france/", true, null, null),
                new("Germany", "europe/germany/", true, null, null),
                new("Ireland and Northern Ireland", "europe/ireland-and-northern-ireland.map", false, 251_658_240, "2026-11-02"),
                new("Portugal", "europe/portugal.map", false, 188_743_680, "2026-11-02"),
                new("Spain", "europe/spain.map", false, 838_860_800, "2026-11-02"),
                new("Ukraine", "europe/ukraine.map", false, 692_060_160, "2026-11-02"),
            };
            w._downloading = w._regions[1];
            w._progress = (183_500_800, 524_288_000);
        }
        w.ShowPage(page);
        return w;
    }
}

/// <summary>Atlas's own settings (~/.config/crimson-atlas/settings.json): the map last open, where, and the colours.</summary>
public sealed class AtlasSettings
{
    public string? Map { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int? Zoom { get; set; }
    public string? Theme { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "crimson-atlas");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AtlasSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AtlasSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
