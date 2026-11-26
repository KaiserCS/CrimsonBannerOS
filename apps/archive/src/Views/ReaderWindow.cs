// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PeoplesArchive.Model;
using static CrimsonBanner.Apps.Deck;
using M = PeoplesArchive.Model;

namespace PeoplesArchive.Views;

/// <summary>
/// The reading room: one book, a chapter at a time, set in a book face on paper, sepia or night, at the size chosen.
/// Its contents are beside it; the place reached is remembered on the shelf, so the book opens there next time.
/// </summary>
public sealed class ReaderWindow : Window
{
    private readonly Library _lib;
    private readonly ShelfBook _entry;
    private readonly Book _book;
    private readonly bool _save;
    private readonly ArchiveSettings _settings;
    private int _chapter;
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly StackPanel _page = new() { Spacing = 0, MaxWidth = 720, Margin = new Thickness(40, 36, 40, 60), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _paper = new();
    private readonly ListBox _contents = new() { Width = 260 };
    private readonly TextBlock _where = new() { Classes = { "muted" }, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _chapterTitle = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _prev, _next;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _restoring;

    private static readonly FontFamily Serif = new("Noto Serif, DejaVu Serif, Liberation Serif, serif");
    private static readonly FontFamily MonoFace = new("Noto Sans Mono, DejaVu Sans Mono, Liberation Mono, monospace");

    public ReaderWindow(Library lib, ShelfBook entry, bool save = true)
    {
        _lib = lib; _entry = entry; _save = save;
        _settings = save ? ArchiveSettings.Load() : new ArchiveSettings();
        _book = Book.Open(lib.PathOf(entry));
        Title = entry.Title + " — People's Archive";
        Width = 1100; Height = 840; MinWidth = 640; MinHeight = 480;

        // the contents
        _contents.ItemsSource = _book.Contents.Select(c => new string(' ', c.Depth * 3) + c.Title).ToList();
        _contents.SelectionChanged += (_, _) =>
        {
            if (_restoring || _contents.SelectedIndex < 0) return;
            var t = _book.Contents[_contents.SelectedIndex];
            if (t.Chapter != _chapter) Go(t.Chapter, 0);
            if (t.Fragment != null) Dispatcher.UIThread.Post(() => ScrollTo(t.Fragment), DispatcherPriority.Background);
        };
        var side = new DockPanel { Margin = new Thickness(12, 12, 0, 12) };
        var head = new StackPanel { Spacing = 3, Margin = new Thickness(8, 4, 8, 12) };
        head.Children.Add(Kicker("CONTENTS", accent: true));
        head.Children.Add(new TextBlock { Text = _book.Title, FontWeight = FontWeight.Bold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (_book.Author.Length > 0) head.Children.Add(new TextBlock { Text = _book.Author, Classes = { "muted" }, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (entry.Licence.Length > 0) head.Children.Add(new TextBlock { Text = entry.Licence, Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        DockPanel.SetDock(head, Dock.Top);
        side.Children.Add(head);
        side.Children.Add(_contents);

        // the bar: where you are, the type, the paper
        _prev = Pill("‹ Previous"); _next = Pill("Next ›");
        _prev.Click += (_, _) => Go(_chapter - 1, 0);
        _next.Click += (_, _) => Go(_chapter + 1, 0);
        var smaller = Pill("A−"); var larger = Pill("A+");
        smaller.Click += (_, _) => Resize(-1);
        larger.Click += (_, _) => Resize(+1);
        var tones = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (id, label) in new[] { ("paper", "Paper"), ("sepia", "Sepia"), ("night", "Night") })
        {
            var b = Pill(label, accent: _settings.Tone == id);
            b.Click += (_, _) =>
            {
                _settings.Tone = id;
                if (_save) _settings.Save();
                foreach (var x in tones.Children.OfType<Button>()) x.Classes.Remove("accent");
                b.Classes.Add("accent");
                Draw(keepPlace: true);
            };
            tones.Children.Add(b);
        }
        var mark = Pill("Bookmark");
        mark.Click += (_, _) =>
        {
            if (!_entry.Bookmarks.Contains(_chapter)) _entry.Bookmarks.Add(_chapter);
            Remember();
            mark.Content = "MARKED";
        };
        var bar = new DockPanel { Margin = new Thickness(16, 10, 16, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        right.Children.Add(smaller); right.Children.Add(larger); right.Children.Add(tones); right.Children.Add(mark);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        left.Children.Add(_prev); left.Children.Add(_next); left.Children.Add(_chapterTitle); left.Children.Add(_where);
        bar.Children.Add(left);

        _scroll.Content = _page;
        _paper.Child = _scroll;
        _paper.CornerRadius = new CornerRadius(10);
        _paper.Margin = new Thickness(12, 0, 12, 12);
        var main = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        main.Children.Add(bar);
        main.Children.Add(_paper);
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        root.Children.Add(side);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        Content = root;

        _scroll.ScrollChanged += (_, _) => { if (!_restoring) { _saveTimer.Stop(); _saveTimer.Start(); } };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Remember(); };
        KeyDown += OnKey;
        Closing += (_, _) => Remember();
        Closed += (_, _) => _book.Dispose();

        _chapter = Math.Clamp(entry.Chapter, 0, Math.Max(0, _book.Chapters.Count - 1));
        Go(_chapter, entry.Position);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        switch (e.Key)
        {
            case Key.Right when e.KeyModifiers == KeyModifiers.None: Go(_chapter + 1, 0); e.Handled = true; break;
            case Key.Left when e.KeyModifiers == KeyModifiers.None: Go(_chapter - 1, 0); e.Handled = true; break;
            case Key.Add or Key.OemPlus when e.KeyModifiers.HasFlag(KeyModifiers.Control): Resize(+1); e.Handled = true; break;
            case Key.Subtract or Key.OemMinus when e.KeyModifiers.HasFlag(KeyModifiers.Control): Resize(-1); e.Handled = true; break;
        }
    }

    private void Resize(int step)
    {
        _settings.FontSize = Math.Clamp(_settings.FontSize + step * 1.5, 12, 34);
        if (_save) _settings.Save();
        Draw(keepPlace: true);
    }

    /// <summary>Opens a chapter at a place in it (0–1 of the way down).</summary>
    public void Go(int chapter, double position)
    {
        if (chapter < 0 || chapter >= _book.Chapters.Count) return;
        _chapter = chapter;
        Draw(keepPlace: false);
        _restoring = true;
        var idx = _book.Contents.FindIndex(c => c.Chapter == chapter);
        _contents.SelectedIndex = idx;
        Dispatcher.UIThread.Post(() =>
        {
            var extent = _scroll.Extent.Height - _scroll.Viewport.Height;
            _scroll.Offset = new Vector(0, extent > 0 ? extent * Math.Clamp(position, 0, 1) : 0);
            _restoring = false;
            Remember();
        }, DispatcherPriority.Background);
    }

    private (IBrush Back, IBrush Ink, IBrush Soft) Tone() => _settings.Tone switch
    {
        "sepia" => (new SolidColorBrush(Color.Parse("#F1E7D0")), new SolidColorBrush(Color.Parse("#3B2F22")), new SolidColorBrush(Color.Parse("#7A6A55"))),
        "night" => (new SolidColorBrush(Color.Parse("#141418")), new SolidColorBrush(Color.Parse("#D9D9DE")), new SolidColorBrush(Color.Parse("#8C8C96"))),
        _ => (new SolidColorBrush(Color.Parse("#FBF8F1")), new SolidColorBrush(Color.Parse("#1D1D1F")), new SolidColorBrush(Color.Parse("#6B6B70"))),
    };

    /// <summary>Draws the chapter (again, after a change of type or paper; then at the same place).</summary>
    private void Draw(bool keepPlace)
    {
        double place = Place();
        var (back, ink, soft) = Tone();
        _paper.Background = back;
        _page.Children.Clear();
        double size = _settings.FontSize;
        foreach (var b in _book.ChapterBlocks(_chapter)) _page.Children.Add(Render(b, size, ink, soft));
        _chapterTitle.Text = _book.Chapters[_chapter].Title;
        _where.Text = $"{_chapter + 1} of {_book.Chapters.Count}";
        _prev.IsEnabled = _chapter > 0;
        _next.IsEnabled = _chapter < _book.Chapters.Count - 1;
        if (keepPlace)
        {
            _restoring = true;
            Dispatcher.UIThread.Post(() =>
            {
                var extent = _scroll.Extent.Height - _scroll.Viewport.Height;
                _scroll.Offset = new Vector(0, extent > 0 ? extent * place : 0);
                _restoring = false;
            }, DispatcherPriority.Background);
        }
        else _scroll.Offset = default;
    }

    private Control Render(Block b, double size, IBrush ink, IBrush soft)
    {
        switch (b.Kind)
        {
            case BlockKind.Rule:
                return new Border { Height = 1, Background = soft, Opacity = 0.5, Margin = new Thickness(size * 4, size, size * 4, size), Tag = b.Anchor };
            case BlockKind.Image:
                try
                {
                    if (b.Image != null && _book.ReadBytes(b.Image) is { } bytes)
                    {
                        using var ms = new MemoryStream(bytes);
                        var bmp = new Bitmap(ms);
                        return new Image { Source = bmp, MaxWidth = Math.Min(bmp.Size.Width, 680), MaxHeight = 900, Stretch = Stretch.Uniform, Margin = new Thickness(0, size * 0.6, 0, size * 0.6), Tag = b.Anchor };
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or NotSupportedException) { }
                return new TextBlock { Text = "(a picture that couldn't be shown)", Foreground = soft, FontStyle = FontStyle.Italic, FontSize = size * 0.8, Tag = b.Anchor };
        }
        var t = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = ink, FontFamily = Serif, FontSize = size, LineHeight = size * 1.55, Tag = b.Anchor,
        };
        var inlines = new InlineCollection();
        foreach (var r in b.Runs) inlines.Add(InlineFor(r, size, soft));
        t.Inlines = inlines;
        switch (b.Kind)
        {
            case BlockKind.Heading:
                t.FontSize = size * b.Level switch { 1 => 1.8, 2 => 1.5, 3 => 1.3, _ => 1.12 };
                t.LineHeight = t.FontSize * 1.25;
                t.FontWeight = FontWeight.Bold;
                t.Margin = new Thickness(0, size * 1.4, 0, size * 0.6);
                return t;
            case BlockKind.Quote:
                t.Margin = new Thickness(size * 0.9, 0, 0, 0);
                return new Border
                {
                    BorderBrush = soft, BorderThickness = new Thickness(3, 0, 0, 0), Margin = new Thickness(size * 0.6 * b.Level, 0, 0, size * 0.8),
                    Child = t, Tag = b.Anchor,
                };
            case BlockKind.ListItem:
                t.Margin = new Thickness(size * 1.1 * b.Level, 0, 0, size * 0.35);
                return t;
            case BlockKind.Preformatted:
                t.FontFamily = MonoFace;
                t.FontSize = size * 0.8;
                t.LineHeight = size * 1.2;
                t.TextWrapping = TextWrapping.Wrap;
                t.Margin = new Thickness(size, 0, 0, size * 0.8);
                return t;
            case BlockKind.Caption:
                t.FontSize = size * 0.85;
                t.Foreground = soft;
                t.TextAlignment = TextAlignment.Center;
                t.Margin = new Thickness(0, 0, 0, size);
                return t;
            default:
                t.Margin = new Thickness(0, 0, 0, size * 0.8);
                return t;
        }
    }

    private static Inline InlineFor(M.Run r, double size, IBrush soft)
    {
        var run = new Avalonia.Controls.Documents.Run(r.Text);
        if (r.Italic) run.FontStyle = FontStyle.Italic;
        if (r.Bold) run.FontWeight = FontWeight.Bold;
        if (r.Mono) run.FontFamily = MonoFace;
        if (r.Small) run.FontSize = size * 0.72;
        if (r.Link != null && !r.Link.StartsWith('#')) run.TextDecorations = TextDecorations.Underline;
        return run;
    }

    private void ScrollTo(string anchor)
    {
        var target = _page.Children.FirstOrDefault(c => (string?)c.Tag == anchor);
        if (target != null) _scroll.Offset = new Vector(0, Math.Max(0, target.Bounds.Top - 12));
    }

    /// <summary>How far down the chapter the reader is (0–1).</summary>
    private double Place()
    {
        var extent = _scroll.Extent.Height - _scroll.Viewport.Height;
        return extent > 1 ? Math.Clamp(_scroll.Offset.Y / extent, 0, 1) : 0;
    }

    /// <summary>The place reached, on the shelf.</summary>
    private void Remember()
    {
        _entry.Chapter = _chapter;
        _entry.Position = Place();
        _entry.Chapters = _book.Chapters.Count;
        _entry.LastRead = DateTime.UtcNow;
        if (!_save) return;
        try { _lib.Save(); } catch (IOException) { }
    }
}
