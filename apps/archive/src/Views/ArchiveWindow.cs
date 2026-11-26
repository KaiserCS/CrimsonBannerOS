// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PeoplesArchive.Model;
using static CrimsonBanner.Apps.Deck;

namespace PeoplesArchive.Views;

/// <summary>
/// People's Archive: a shelf of free books on this computer, and three public-domain sources to find more in (Project
/// Gutenberg, Wikisource, the Marxists Internet Archive), with any OPDS catalogue a person adds. The network is used
/// only when they search or download; reading happens here, offline (<see cref="ReaderWindow"/>).
/// </summary>
public sealed class ArchiveWindow : Window
{
    private readonly Library _lib;
    private List<Source> _sources;
    private readonly Net _net = new();
    private readonly ContentControl _content = new();
    private readonly StackPanel _nav = new() { Spacing = 2 };
    private readonly TextBlock _folderLine = new() { Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private string _page = "shelf";
    private string _shelfQuery = "";
    private int _source;
    private string _query = "";
    private Results? _results;
    private readonly List<Listing> _shown = new();
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _message = "";
    private bool _messageBad;
    private readonly Dictionary<string, double> _downloading = new();
    private readonly bool _offline;
    private ArchiveSettings _settings;

    public ArchiveWindow() : this(new Library(Library.DefaultFolder), offline: false) { }

    public ArchiveWindow(Library lib, bool offline)
    {
        Title = "People's Archive";
        Width = 1180; Height = 800; MinWidth = 860; MinHeight = 560;
        _lib = lib;
        _offline = offline;
        _settings = offline ? new ArchiveSettings() : ArchiveSettings.Load();
        try { if (!offline) _lib.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say("The shelf couldn't be read: " + e.Message, true); }
        _sources = offline ? new List<Source> { Sources.Gutenberg(), new WikisourceSource(_settings.WikisourceLanguage), new MiaSource(Path.GetTempPath()) }
                           : Sources.All(_settings.WikisourceLanguage);

        var side = new DockPanel { Width = 216, Margin = new Thickness(14, 16, 6, 14) };
        var brand = new StackPanel { Spacing = 2, Margin = new Thickness(10, 2, 0, 18) };
        brand.Children.Add(Kicker("CRIMSON BANNER OS", accent: true));
        brand.Children.Add(new TextBlock { Text = "People's Archive", FontSize = 21, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);
        var foot = new StackPanel { Spacing = 6, Margin = new Thickness(10, 10, 4, 0) };
        foot.Children.Add(Kicker("FREE BOOKS · FREE TO SHARE"));
        foot.Children.Add(_folderLine);
        DockPanel.SetDock(foot, Dock.Bottom);
        side.Children.Add(foot);
        side.Children.Add(_nav);
        foreach (var (id, label) in new[] { ("shelf", "Shelf"), ("find", "Find books"), ("sources", "Sources") })
        {
            var b = new Button { Classes = { "nav" }, Content = new TextBlock { Text = label, FontSize = 14 }, Tag = id };
            b.Click += (_, _) => ShowPage(id);
            _nav.Children.Add(b);
        }
        var scroll = new ScrollViewer { Content = _content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        root.Children.Add(side);
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closed += (_, _) => { _cts?.Cancel(); _net.Dispose(); };
        ShowPage(_lib.Books.Count == 0 ? "find" : "shelf");
    }

    public void ShowPage(string page)
    {
        _page = page;
        foreach (var b in _nav.Children.OfType<Button>())
            if ((string?)b.Tag == page) b.Classes.Add("on"); else b.Classes.Remove("on");
        _folderLine.Text = "Books are kept in " + _lib.Folder;
        _content.Content = page switch { "find" => FindPage(), "sources" => SourcesPage(), _ => ShelfPage() };
    }

    private void Refresh() => ShowPage(_page);

    private void Say(string text, bool bad = false) { _message = text; _messageBad = bad; }

    private void AddMessage(StackPanel col)
    {
        if (_message.Length == 0) return;
        var t = new TextBlock { Text = _message, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        if (_messageBad) t.Foreground = Brushes.OrangeRed;
        _message = "";
        col.Children.Add(Panel(t));
    }

    // ───────── the shelf ─────────

    private Control ShelfPage()
    {
        var col = PageColumn("THE SHELF · ON THIS COMPUTER", "Your books",
            "Books you've downloaded, and any EPUB or text file you add. They're read here, without the network.");
        AddMessage(col);
        int reading = _lib.Books.Count(b => b.Progress > 0 && b.Progress < 0.995);
        col.Children.Add(Lcd(("BOOKS", _lib.Books.Count.ToString("000")), ("READING", reading.ToString("00")), ("SOURCES", _sources.Count.ToString("00"))));

        var bar = new DockPanel();
        var search = new TextBox { Text = _shelfQuery, PlaceholderText = "Search your shelf: title, author or source", Width = 380 };
        search.KeyUp += (_, _) =>
        {
            if (search.Text == _shelfQuery) return;
            _shelfQuery = search.Text ?? "";
            if (_content.Content is StackPanel sp && sp.Children.LastOrDefault() is Control grid && grid.Tag as string == "books")
                sp.Children[^1] = Books();
        };
        var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var path = new TextBox { PlaceholderText = "/path/to/a/book.epub", Width = 280 };
        var addB = Pill("Add a file");
        addB.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(path.Text)) { AddFile(path.Text.Trim()); Refresh(); } };
        add.Children.Add(path); add.Children.Add(addB);
        DockPanel.SetDock(add, Dock.Right);
        bar.Children.Add(add);
        bar.Children.Add(search);
        col.Children.Add(bar);
        if (_lib.Books.Count == 0)
        {
            var empty = new StackPanel { Spacing = 10 };
            empty.Children.Add(Kicker("NOTHING HERE YET", accent: true));
            empty.Children.Add(Muted("Find a book in Project Gutenberg, Wikisource or the Marxists Internet Archive, or drop an EPUB on this window."));
            var go = Pill("Find books", accent: true); go.Click += (_, _) => ShowPage("find");
            empty.Children.Add(go);
            col.Children.Add(Panel(empty));
        }
        col.Children.Add(Books());
        return col;
    }

    private Control Books()
    {
        var wrap = new WrapPanel { Tag = "books" };
        foreach (var b in _lib.Search(_shelfQuery))
        {
            var card = new StackPanel { Spacing = 6, Width = 190 };
            var cover = Cover(b, 190, 262);
            var open = new Button { Content = cover, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(open, "Read " + b.Title);
            open.Click += (_, _) => Read(b);
            card.Children.Add(open);
            card.Children.Add(new TextBlock { Text = b.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis });
            if (b.Author.Length > 0) card.Children.Add(new TextBlock { Text = b.Author, Classes = { "muted" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            if (b.Progress > 0)
                card.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = b.Progress, Height = 4, MinHeight = 4, MinWidth = 0 });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Badge(Short(b.Source)));
            var remove = new Button { Content = "Remove", Classes = { "flat" }, FontSize = 11 };
            remove.Click += (_, _) =>
            {
                if ((string?)remove.Tag != "sure") { remove.Tag = "sure"; remove.Content = "Remove for good?"; return; }
                try { _lib.Remove(b); Say($"Removed {b.Title}."); }
                catch (IOException e) { Say("It couldn't be removed: " + e.Message, true); }
                Refresh();
            };
            row.Children.Add(remove);
            card.Children.Add(row);
            wrap.Children.Add(new Border { Child = card, Margin = new Thickness(0, 0, 22, 26) });
        }
        return wrap;
    }

    private static string Short(string source) => source switch
    {
        "Project Gutenberg" => "GUTENBERG",
        "Marxists Internet Archive" => "MIA",
        "Wikisource" => "WIKISOURCE",
        "This computer" => "ADDED",
        _ => source.ToUpperInvariant().Length > 16 ? source.ToUpperInvariant()[..16] : source.ToUpperInvariant(),
    };

    /// <summary>A book's cover: its own picture, or one drawn from its title in the deck's colours.</summary>
    private Control Cover(ShelfBook b, double w, double h)
    {
        try
        {
            using var book = Book.Open(_lib.PathOf(b));
            if (book.CoverPath != null && book.ReadBytes(book.CoverPath) is { } bytes)
            {
                using var ms = new MemoryStream(bytes);
                return new Border { Width = w, Height = h, CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = new Image { Source = new Bitmap(ms), Stretch = Stretch.UniformToFill } };
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or ArgumentException or InvalidOperationException) { }
        var tone = (uint)b.Title.Aggregate(17, (a, c) => a * 31 + c) % 3;
        var top = tone switch { 0 => Color.Parse("#2A2A2F"), 1 => Color.Parse("#3A1016"), _ => Color.Parse("#232A2E") };
        var g = new Grid { Width = w, Height = h };
        g.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(top, 0), new GradientStop(Color.Parse("#0C0C0E"), 1) },
            },
        });
        var words = new StackPanel { Margin = new Thickness(16, 22, 16, 16), Spacing = 8 };
        words.Children.Add(new Border { Height = 4, Width = 36, Background = new SolidColorBrush(Color.Parse("#C8102E")), HorizontalAlignment = HorizontalAlignment.Left });
        words.Children.Add(new TextBlock { Text = b.Title, Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Noto Serif, DejaVu Serif, serif") });
        if (b.Author.Length > 0) words.Children.Add(new TextBlock { Text = b.Author, Foreground = new SolidColorBrush(Color.Parse("#C4C4CC")), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        g.Children.Add(words);
        return g;
    }

    private void Read(ShelfBook b)
    {
        try { new ReaderWindow(_lib, b, save: !_offline).Show(); }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            Say($"{b.Title} couldn't be opened: {e.Message}", true);
            Refresh();
        }
    }

    /// <summary>A book file given to the app (from the file manager): onto the shelf, and open.</summary>
    public void OpenFile(string file)
    {
        var before = _lib.Books.ToList();
        AddFile(file);
        var added = _lib.Books.Except(before).FirstOrDefault() ?? _lib.Books.FirstOrDefault(b => b.File == Path.GetFileName(file));
        ShowPage("shelf");
        if (added != null) Read(added);
    }

    private void AddFile(string file)
    {
        try
        {
            if (!File.Exists(file)) { Say($"There's no file {file}.", true); return; }
            var b = _lib.Add(file, "This computer", "", "");
            Say($"Added {b.Title} to the shelf.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            Say($"{Path.GetFileName(file)} couldn't be added: it isn't an EPUB or text file People's Archive can read ({e.Message}).", true);
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (files == null || files.Count == 0) return;
        foreach (var f in files.Where(Library.IsBook)) AddFile(f);
        ShowPage("shelf");
    }

    // ───────── finding books ─────────

    private Control FindPage()
    {
        var src = _sources[Math.Clamp(_source, 0, _sources.Count - 1)];
        var col = PageColumn("FIND BOOKS · PUBLIC-DOMAIN SOURCES", "Find books",
            "Search a source, and download what you'd like to read. People's Archive goes online only when you search or download.");
        AddMessage(col);
        var pills = new WrapPanel();
        for (int i = 0; i < _sources.Count; i++)
        {
            int k = i;
            var p = Pill(_sources[i].Name, accent: i == _source);
            p.Margin = new Thickness(0, 0, 8, 8);
            p.Click += (_, _) => { if (_source == k) return; _source = k; _results = null; _shown.Clear(); _cts?.Cancel(); Refresh(); };
            pills.Children.Add(p);
        }
        col.Children.Add(pills);

        var about = new StackPanel { Spacing = 6 };
        about.Children.Add(Kicker(src.Name.ToUpperInvariant(), accent: true));
        about.Children.Add(Muted(src.About));
        about.Children.Add(new TextBlock { Text = "Terms: " + src.Licence + "   ·   Run by " + src.RunBy, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var box = new TextBox { Text = _query, PlaceholderText = src is WikisourceSource ? "A title, an author or a phrase" : "A title or an author", Width = 420 };
        var go = Pill(_busy ? "Searching…" : "Search", accent: true);
        go.IsEnabled = !_busy;
        void Go() { _query = box.Text?.Trim() ?? ""; _ = Search(src, null); }
        go.Click += (_, _) => Go();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Go(); };
        bar.Children.Add(box); bar.Children.Add(go);
        if (src is not WikisourceSource)
        {
            var browse = Pill("Browse");
            browse.IsEnabled = !_busy;
            browse.Click += (_, _) => { _query = ""; box.Text = ""; _ = Search(src, null); };
            bar.Children.Add(browse);
        }
        about.Children.Add(bar);
        col.Children.Add(Panel(about));

        if (_results?.Note is { } note) col.Children.Add(Muted(note));
        if (_shown.Count > 0)
        {
            var list = new StackPanel { Spacing = 0 };
            list.Children.Add(Kicker($"{_shown.Count} BOOK{(_shown.Count == 1 ? "" : "S")}{(_query.Length > 0 ? " FOR “" + _query.ToUpperInvariant() + "”" : "")}"));
            foreach (var l in _shown) list.Children.Add(Row(src, l));
            if (_results?.Next != null)
            {
                var more = Pill(_busy ? "Loading…" : "More");
                more.IsEnabled = !_busy;
                more.Margin = new Thickness(0, 12, 0, 0);
                more.Click += (_, _) => _ = Search(src, _results.Next);
                list.Children.Add(more);
            }
            col.Children.Add(Panel(list));
        }
        else if (_results != null && _results.Note == null && !_busy)
            col.Children.Add(Muted("Nothing found. Try fewer or other words, or another source."));
        return col;
    }

    private Control Row(Source src, Listing l)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
        var have = _lib.FromUrl(l.Page) ?? (l.Download != null ? _lib.FromUrl(l.Download) : null);
        if (have != null)
        {
            var read = Pill("Read", accent: true);
            read.Click += (_, _) => Read(have);
            actions.Children.Add(read);
        }
        else if (_downloading.TryGetValue(l.Page, out var p))
            actions.Children.Add(new StackPanel
            {
                Width = 120, Spacing = 3, VerticalAlignment = VerticalAlignment.Center,
                Children = { new ProgressBar { Minimum = 0, Maximum = 1, Value = p, IsIndeterminate = p <= 0, Height = 4, MinHeight = 4, MinWidth = 0 },
                             new TextBlock { Text = "Downloading…", FontSize = 11, Classes = { "muted" } } },
            });
        else
        {
            var dl = Pill("Download", accent: true);
            dl.Click += (_, _) => _ = Download(src, l);
            actions.Children.Add(dl);
        }
        if (l.Page.StartsWith("http", StringComparison.Ordinal))
        {
            var page = Pill("Its page");
            page.Click += (_, _) => OpenUrl(l.Page);
            actions.Children.Add(page);
        }
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = l.Title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (l.Author.Length > 0) text.Children.Add(new TextBlock { Text = l.Author, TextWrapping = TextWrapping.Wrap });
        if (l.Summary.Length > 0) text.Children.Add(new TextBlock { Text = l.Summary, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        return new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = (IBrush?)this.FindResource("ShellRuleBrush") ?? Brushes.Gray };
    }

    private async Task Search(Source src, string? next)
    {
        if (_offline) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _busy = true;
        if (next == null) _shown.Clear();
        Refresh();
        try
        {
            var r = await Task.Run(() => src.Search(_net, _query, next, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _results = r;
            _shown.AddRange(r.Books.Where(b => !_shown.Any(s => s.Page == b.Page)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or System.Xml.XmlException or System.Text.Json.JsonException or InvalidDataException or UriFormatException)
        {
            _results = null;
            Say($"{src.Name} couldn't be reached or read: {e.Message}", true);
        }
        finally { if (_cts == cts) _busy = false; }
        if (_page == "find") Refresh();
    }

    private async Task Download(Source src, Listing l)
    {
        if (_offline || _downloading.ContainsKey(l.Page)) return;
        _downloading[l.Page] = 0;
        Refresh();
        var tmp = Path.Combine(Path.GetTempPath(), "peoples-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            int lastStep = -1;
            var progress = new Progress<double>(p =>
            {
                _downloading[l.Page] = p;
                var step = (int)(p * 20);             // the page is drawn again every 5%
                if (step == lastStep) return;
                lastStep = step;
                if (_page == "find") Refresh();
            });
            var url = await src.DownloadFor(_net, l, CancellationToken.None)
                      ?? throw new InvalidDataException("the source doesn't offer this book as an EPUB or text file.");
            var user = (src as OpdsSource)?.User;
            await Task.Run(() => _net.Download(url, tmp, progress, CancellationToken.None, user));
            var b = _lib.Add(tmp, src.Name, l.Page, l.Licence, l.Title, l.Author);
            Say($"{b.Title} is on your shelf.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or System.Xml.XmlException or TaskCanceledException or UriFormatException)
        {
            Say($"{l.Title} couldn't be downloaded: {e.Message}", true);
        }
        finally
        {
            _downloading.Remove(l.Page);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
        Refresh();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ───────── sources ─────────

    private Control SourcesPage()
    {
        var col = PageColumn("SOURCES · WHERE BOOKS COME FROM", "Sources",
            "Every source here offers books free to read and share. People's Archive asks one only when you search or download, says who's asking, and fetches one book at a time.");
        AddMessage(col);
        foreach (var s in _sources)
        {
            var p = new StackPanel { Spacing = 6 };
            var head = new DockPanel();
            if (s is OpdsSource { Custom: true } custom)
            {
                var rm = new Button { Content = "Remove", Classes = { "flat" } };
                rm.Click += (_, _) =>
                {
                    var list = Sources.LoadCatalogues();
                    list.RemoveAll(c => c.Url == custom.Start);
                    Sources.SaveCatalogues(list);
                    _sources = Sources.All(_settings.WikisourceLanguage);
                    _source = 0;
                    Refresh();
                };
                DockPanel.SetDock(rm, Dock.Right);
                head.Children.Add(rm);
            }
            head.Children.Add(Kicker(s.Name.ToUpperInvariant(), accent: true));
            p.Children.Add(head);
            p.Children.Add(Muted(s.About));
            p.Children.Add(new TextBlock { Text = "Terms: " + s.Licence, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            p.Children.Add(new TextBlock { Text = "Run by: " + s.RunBy, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            if (s is WikisourceSource ws)
            {
                var langs = new[] { "en", "fr", "de", "es", "it", "pt", "ru", "pl", "zh", "ar", "la" };
                var names = new[] { "English", "Français", "Deutsch", "Español", "Italiano", "Português", "Русский", "Polski", "中文", "العربية", "Latina" };
                var lang = new ComboBox { ItemsSource = names, SelectedIndex = Math.Max(0, Array.IndexOf(langs, ws.Language)), MinWidth = 180 };
                lang.SelectionChanged += (_, _) =>
                {
                    if (lang.SelectedIndex < 0 || langs[lang.SelectedIndex] == _settings.WikisourceLanguage) return;
                    _settings.WikisourceLanguage = langs[lang.SelectedIndex];
                    if (!_offline) _settings.Save();
                    _sources = _offline ? _sources : Sources.All(_settings.WikisourceLanguage);
                    Dispatcher.UIThread.Post(Refresh);
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(new TextBlock { Text = "Wikisource in", VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(lang);
                p.Children.Add(row);
            }
            col.Children.Add(Panel(p));
        }

        var add = new StackPanel { Spacing = 8 };
        add.Children.Add(Kicker("ADD AN OPDS CATALOGUE", accent: true));
        add.Children.Add(Muted("Many libraries and publishers offer a catalogue for reading apps (OPDS). Give its address; if it asks you to sign in (Standard Ebooks' does, for its patrons), your user name or email too."));
        var name = new TextBox { PlaceholderText = "Its name", Width = 220 };
        var url = new TextBox { PlaceholderText = "https://…/opds", Width = 360 };
        var user = new TextBox { PlaceholderText = "User name or email (if it asks)", Width = 240 };
        var go = Pill("Add", accent: true);
        go.Click += (_, _) =>
        {
            var u = url.Text?.Trim() ?? "";
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) { Say("Give the catalogue's full web address.", true); Refresh(); return; }
            var list = Sources.LoadCatalogues();
            list.RemoveAll(c => c.Url == u);
            list.Add(new SavedCatalogue { Name = string.IsNullOrWhiteSpace(name.Text) ? uri.Host : name.Text.Trim(), Url = u, User = user.Text?.Trim() ?? "" });
            Sources.SaveCatalogues(list);
            _sources = Sources.All(_settings.WikisourceLanguage);
            Say("Added. It's in Find books.");
            Refresh();
        };
        var row2 = new WrapPanel();
        foreach (var c in new Control[] { name, url, user, go }) { c.Margin = new Thickness(0, 0, 8, 8); row2.Children.Add(c); }
        add.Children.Add(row2);
        col.Children.Add(Panel(add));
        return col;
    }

    /// <summary>For the tests: a page with a sample shelf and sample results, nothing fetched or saved.</summary>
    public static ArchiveWindow Sample(string page, string folder)
    {
        var lib = Samples.Shelf(folder);
        var w = new ArchiveWindow(lib, offline: true);
        if (page == "find")
        {
            w._source = 0;
            w._query = "manifesto";
            w._results = new Results();
            w._shown.AddRange(new[]
            {
                new Listing { Title = "The Communist Manifesto", Author = "Karl Marx, Friedrich Engels", Summary = "The 1888 English translation by Samuel Moore, edited by Engels.", Page = "https://www.gutenberg.org/ebooks/61", Source = "Project Gutenberg" },
                new Listing { Title = "Pride and Prejudice", Author = "Jane Austen", Page = "https://www.gutenberg.org/ebooks/1342", Source = "Project Gutenberg" },
                new Listing { Title = "The Iron Heel", Author = "Jack London", Summary = "A novel of the struggle between the oligarchy and the working class.", Page = "https://www.gutenberg.org/ebooks/1164", Source = "Project Gutenberg" },
            });
        }
        w.ShowPage(page);
        return w;
    }
}

/// <summary>People's Archive's own settings (~/.config/peoples-archive/settings.json).</summary>
public sealed class ArchiveSettings
{
    public string WikisourceLanguage { get; set; } = "en";
    public double FontSize { get; set; } = 19;
    public string Tone { get; set; } = "paper";

    private static string FilePath => Path.Combine(Sources.ConfigFolder, "settings.json");

    public static ArchiveSettings Load()
    {
        try { return File.Exists(FilePath) ? System.Text.Json.JsonSerializer.Deserialize<ArchiveSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Sources.ConfigFolder); File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this)); }
        catch (IOException) { }
    }
}
