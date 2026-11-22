// SPDX-License-Identifier: GPL-3.0-or-later
// CD Player: a music player built like a hi-fi CD deck. The disc spins in its case, a deck display shows the track
// and time, the shelf holds the albums in ~/Music, and audio CDs play from the drive. Sound through LibVLC.
//
// After CDPlayer by Kizarov3 (MIT licence, github.com/Kizarov3/CDPlayer): its look, layout and keys, rebuilt as a
// native app of the shell (no Electron, no streaming services). Views/Deck/NOTICE has CDPlayer's licence.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using RedStarOS.Services;
using RedStarOS.Views.Deck;

namespace RedStarOS.Views;

public sealed class CdPlayerState
{
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public bool Shuffle { get; set; }
    public string Repeat { get; set; } = "off";     // off, all, one
    public string? LastAlbum { get; set; }
    public bool CdView { get; set; }
}

public sealed class CdPlayerWindow : Window
{
    public static CdPlayerWindow? Instance { get; private set; }
    public const string AudioCdDevice = "/dev/sr0";
    private static string StatePath => AppPaths.ConfigFile("cdplayer.json");

    private readonly LibVLC? _lib;
    private readonly MediaPlayer? _player;
    private CdPlayerState _st = new();
    private List<MusicAlbum> _shelf = new();
    private MusicAlbum? _album;
    private string _source = "YOUR MUSIC LIBRARY";
    private List<int> _order = new();     // the play order (shuffled or not) as indices into the album's tracks
    private int _pos = -1;                // where in _order we are
    private int _gen;                     // bumped for each track, so a previous track's late events are dropped
    private long _time, _length;
    private bool _playing, _paused, _trayOpen, _updating, _closed, _shelfLoaded;
    private readonly Dictionary<string, long> _lengths = new();
    private readonly Dictionary<string, Bitmap?> _covers = new();
    private Media? _media;

    // the panel
    private readonly TextBlock _status = DeckUi.Text("● READY TO PLAY", "deck-status");
    private readonly DiscView _disc = new() { Width = 270, Height = 270 };
    private readonly Border _case = new();
    private readonly TextBlock _caseTitle = DeckUi.Text("", "deck-sub"), _caseArtist = DeckUi.Text("", "deck-hint");
    private readonly TextBlock _title = DeckUi.Text("No disc", "deck-title"), _artist = DeckUi.Text("Open the shelf to pick an album", "deck-sub");
    private readonly TextBlock _sourceText = DeckUi.Text("", "deck-label");
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1000, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0) };
    private readonly TextBlock _elapsed = DeckUi.Text("00:00", "deck-hint"), _total = DeckUi.Text("00:00", "deck-hint");
    private readonly TextBlock _lcdTrack = DeckUi.Text("--/--", "deck-digits"), _lcdTime = DeckUi.Text("--:--", "deck-digits");
    private readonly TextBlock _fPlay = DeckUi.Text("PLAY", "deck-flag"), _fPause = DeckUi.Text("PAUSE", "deck-flag"),
        _fShuffle = DeckUi.Text("SHUFFLE", "deck-flag"), _fRepeat = DeckUi.Text("REPEAT", "deck-flag"), _fOne = DeckUi.Text("1", "deck-flag");
    private readonly List<Button> _playKeys = new();
    private Button _shuffleKey = null!, _repeatKey = null!, _muteKey = null!, _cdPill = null!;
    private readonly Slider _vol = new() { Minimum = 0, Maximum = 100, Width = 170, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _volText = DeckUi.Text("80%", "deck-hint");
    private readonly TextBlock _albumHead = new() { FontSize = 18, FontWeight = FontWeight.Bold, Foreground = DeckColors.Text, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _albumSub = DeckUi.Text("CHANGER EMPTY", "deck-label");
    private readonly ListBox _list = new();
    private readonly List<(TextBlock Num, TextBlock Title, TextBlock Len)> _rows = new();
    private Control _faceplate = null!, _booklet = null!, _discColumn = null!, _cdViewKeys = null!;
    // the shelf
    private readonly Border _shelfPanel = new() { IsVisible = false };
    private readonly TextBox _filter = new() { Watermark = "Filter albums, artists, tracks", Width = 280 };
    private readonly WrapPanel _shelfWrap = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _shelfCount = DeckUi.Text("", "deck-label");

    public CdPlayerWindow() : this(VideoEngine.Get()) { }

    public CdPlayerWindow(LibVLC? lib)
    {
        _lib = lib;
        Title = "CD Player";
        Width = 1180; Height = 720; MinWidth = 920; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RequestedThemeVariant = ThemeVariant.Dark;
        Background = DeckColors.Backdrop;
        FontFamily = new FontFamily("Lucida Grande, Segoe UI, DejaVu Sans, Inter, sans-serif");
        Instance = this;
        LoadState();

        Content = Build();
        ApplyState();

        if (_lib != null)
        {
            _player = new MediaPlayer(_lib);
            _player.Playing += (_, _) => Ui(() => { _playing = true; _paused = false; RefreshPlay(); ShowMeta(); });
            _player.Paused += (_, _) => Ui(() => { _playing = false; _paused = true; RefreshPlay(); });
            _player.Stopped += (_, _) => Ui(() => { _playing = false; _paused = false; RefreshPlay(); });
            _player.EndReached += (_, _) => Ui(OnEnded);
            _player.LengthChanged += (_, e) => { var l = e.Length; Ui(() => OnLength(l)); };
            _player.TimeChanged += (_, e) => { var t = e.Time; Ui(() => OnTime(t)); };
            _player.EncounteredError += (_, _) => Ui(() => SetStatus("● CAN'T PLAY THIS TRACK"));
            ApplyVolume();
        }
        else
        {
            SetStatus("● NEEDS LIBVLC");
            _artist.Text = "CD Player plays through libVLC: install vlc with your package manager.";
        }

        _seek.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _updating || _player == null || _length <= 0) return;
            _player.Position = (float)(_seek.Value / 1000.0);
        };
        _vol.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || _updating) return;
            _st.Volume = (int)_vol.Value;
            if (_st.Volume > 0) _st.Muted = false;
            ApplyVolume(); SaveState();
        };
        _filter.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) FillShelf(); };
        _list.DoubleTapped += (_, _) => { if (_list.SelectedIndex >= 0) PlayTrack(_list.SelectedIndex); };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && _list.SelectedIndex >= 0) { PlayTrack(_list.SelectedIndex); e.Handled = true; } };

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Opened += (_, _) => _ = LoadShelf(restore: true);
        Closed += OnClosed;
    }

    // ───────────────────────── layout ─────────────────────────
    private Control Build()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20, 16, 20, 10) };

        // header: the status pill, the name, the pills
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var statusPill = new Border { Child = _status };
        statusPill.Classes.Add("deck-status");
        var name = DeckUi.Text("CD PLAYER", "deck-label");
        name.Margin = new Thickness(14, 0, 0, 0); name.VerticalAlignment = VerticalAlignment.Center;
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { statusPill, name } };
        DockPanel.SetDock(left, Dock.Left);
        header.Children.Add(left);
        _cdPill = DeckUi.Pill("CD view", ToggleCdView, "The disc on its own (C)");
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        pills.Children.Add(DeckUi.Pill("Shelf", () => ShowShelf(!_shelfPanel.IsVisible), "Your albums (S)"));
        pills.Children.Add(_cdPill);
        if (HasCdDrive()) pills.Children.Add(DeckUi.Pill("Audio CD", () => _ = LoadAudioCd(), "Play the CD in the drive"));
        pills.Children.Add(DeckUi.Pill("Open", () => _ = OpenFiles(), "Open songs (Ctrl+O)"));
        header.Children.Add(pills);
        root.Children.Add(header);

        // the deck: disc, faceplate, booklet
        var deck = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*,320") };
        _discColumn = BuildDiscColumn();
        _faceplate = BuildFaceplate();
        _booklet = BuildBooklet();
        Grid.SetColumn(_faceplate, 1); Grid.SetColumn(_booklet, 2);
        deck.Children.Add(_discColumn); deck.Children.Add(_faceplate); deck.Children.Add(_booklet);

        var stage = new Grid();
        Grid.SetRow(stage, 1);
        stage.Children.Add(deck);
        stage.Children.Add(BuildShelf());
        root.Children.Add(stage);

        var keys = DeckUi.Text("SPACE PLAY · J/L PREV/NEXT · ←/→ 5 SEC · X SHUFFLE · R REPEAT · D NEXT DISC · S SHELF · E TRAY · C CD VIEW",
            "deck-hint");
        keys.HorizontalAlignment = HorizontalAlignment.Center; keys.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(keys, 2);
        root.Children.Add(keys);
        return root;
    }

    private Control BuildDiscColumn()
    {
        // the jewel case: clear plastic round the disc, with a hinge down the left
        _case.Child = _disc;
        _case.Padding = new Thickness(14);
        _case.CornerRadius = new CornerRadius(6);
        _case.BorderThickness = new Thickness(6, 1, 1, 1);
        _case.BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 255, 255, 255));
        _case.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x06, 255, 255, 255), 0.5),
                              new GradientStop(Color.FromArgb(0x12, 255, 255, 255), 1) }
        };
        _case.HorizontalAlignment = HorizontalAlignment.Center;
        _disc.Transitions = new Avalonia.Animation.Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(320) } };

        _caseTitle.HorizontalAlignment = HorizontalAlignment.Center; _caseTitle.Margin = new Thickness(0, 14, 0, 2);
        _caseArtist.HorizontalAlignment = HorizontalAlignment.Center;

        // the keys shown under the disc in CD view, where the faceplate is hidden
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        keys.Children.Add(DeckUi.Key("prev", Prev, "Previous track (J)"));
        var play = DeckUi.Key("play", TogglePlay, "Play / pause (Space)", primary: true);
        _playKeys.Add(play);
        keys.Children.Add(play);
        keys.Children.Add(DeckUi.Key("next", Next, "Next track (L)"));
        _cdViewKeys = keys;
        keys.IsVisible = false;

        return new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0),
            Children = { _case, _caseTitle, _caseArtist, keys }
        };
    }

    private Control BuildFaceplate()
    {
        var p = new StackPanel { Spacing = 4 };
        p.Children.Add(DeckUi.Text("NOW PLAYING", "deck-label"));
        _title.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(_title);
        p.Children.Add(_artist);
        _sourceText.Foreground = DeckColors.AccentBrush;
        _sourceText.Margin = new Thickness(0, 6, 0, 14);
        p.Children.Add(_sourceText);

        var seek = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        DeckUi.Accent(_seek);
        _elapsed.VerticalAlignment = VerticalAlignment.Center; _total.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_seek, 1); Grid.SetColumn(_total, 2);
        seek.Children.Add(_elapsed); seek.Children.Add(_seek); seek.Children.Add(_total);
        p.Children.Add(seek);

        // the deck display
        var lcd = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var trackBox = new StackPanel { Children = { DeckUi.Text("TRACK", "deck-lcd-label"), _lcdTrack } };
        var timeBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { DeckUi.Text("TIME", "deck-lcd-label"), _lcdTime } };
        ((TextBlock)timeBox.Children[0]).HorizontalAlignment = HorizontalAlignment.Right;
        var flags = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6), Children = { _fPlay, _fPause, _fShuffle, _fRepeat, _fOne }
        };
        Grid.SetColumn(flags, 1); Grid.SetColumn(timeBox, 2);
        lcd.Children.Add(trackBox); lcd.Children.Add(flags); lcd.Children.Add(timeBox);
        var lcdBox = new Border { Child = lcd, Margin = new Thickness(0, 12, 0, 16) };
        lcdBox.Classes.Add("deck-lcd");
        p.Children.Add(lcdBox);

        // transport
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        _shuffleKey = DeckUi.Key("shuffle", ToggleShuffle, "Shuffle (X)");
        _repeatKey = DeckUi.Key("repeat", CycleRepeat, "Repeat: off, all, one (R)");
        var play = DeckUi.Key("play", TogglePlay, "Play / pause (Space)", primary: true);
        _playKeys.Add(play);
        transport.Children.Add(_shuffleKey);
        transport.Children.Add(DeckUi.Key("back5", () => SkipBy(-5000), "Back 5 seconds (←)"));
        transport.Children.Add(DeckUi.Key("prev", Prev, "Previous track (J)"));
        transport.Children.Add(play);
        transport.Children.Add(DeckUi.Key("next", Next, "Next track (L)"));
        transport.Children.Add(DeckUi.Key("fwd5", () => SkipBy(5000), "Forward 5 seconds (→)"));
        transport.Children.Add(DeckUi.Key("stop", Stop, "Stop"));
        transport.Children.Add(_repeatKey);
        p.Children.Add(transport);

        // volume, the tray, loading a track
        var row = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        _muteKey = DeckUi.Key("volume", ToggleMute, "Mute (U)");
        _muteKey.Width = 40;
        var volume = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _muteKey, _vol, _volText } };
        DeckUi.Accent(_vol);
        _volText.VerticalAlignment = VerticalAlignment.Center; _volText.Width = 40;
        DockPanel.SetDock(volume, Dock.Left);
        row.Children.Add(volume);
        var load = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { DeckUi.Key("eject", ToggleTray, "Open or close the tray (E)"), DeckUi.Pill("Load a track +", () => _ = OpenFiles(), "Open songs (Ctrl+O)") }
        };
        ((Button)load.Children[0]).Width = 40;
        row.Children.Add(load);
        p.Children.Add(row);

        var card = new Border { Child = p, Padding = new Thickness(26, 22), VerticalAlignment = VerticalAlignment.Center };
        card.Classes.Add("deck-card");
        return card;
    }

    private Control BuildBooklet()
    {
        var head = new StackPanel { Spacing = 2, Margin = new Thickness(4, 0, 4, 10), Children = { _albumSub, _albumHead } };
        DockPanel.SetDock(head, Dock.Top);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[]
                 {
                     DeckUi.Pill("Shelf", () => ShowShelf(true), small: true), DeckUi.Pill("Disc skip", NextDisc, "The next album on the shelf (D)", small: true),
                     DeckUi.Pill("Eject", ToggleTray, "Open or close the tray (E)", small: true), DeckUi.Pill("Clear", Clear, small: true)
                 })
        {
            b.Margin = new Thickness(0, 0, 6, 6);
            actions.Children.Add(b);
        }
        DockPanel.SetDock(actions, Dock.Bottom);
        _list.Classes.Add("deck-list");
        var dock = new DockPanel { Children = { head, actions, _list } };
        var card = new Border { Child = dock, Margin = new Thickness(18, 0, 0, 0) };
        card.Classes.Add("deck-card");
        return card;
    }

    private Control BuildShelf()
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { DeckUi.Text("CD SHELF", "deck-label"), _shelfCount } };
        title.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        _filter.Classes.Add("deck-field");
        head.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _filter, DeckUi.Pill("Rescan", () => _ = LoadShelf(false), "Look in your Music folder again"), DeckUi.Pill("Close", () => ShowShelf(false), "Esc") }
        });
        DockPanel.SetDock(head, Dock.Top);
        var scroll = new ScrollViewer { Content = _shelfWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _shelfPanel.Child = new DockPanel { Children = { head, scroll } };
        _shelfPanel.Padding = new Thickness(22, 18);
        _shelfPanel.CornerRadius = new CornerRadius(10);
        _shelfPanel.Background = new SolidColorBrush(Color.FromArgb(0xF4, 17, 17, 19));
        _shelfPanel.BorderBrush = DeckColors.Rule;
        _shelfPanel.BorderThickness = new Thickness(1);
        _shelfPanel.ZIndex = 10;
        return _shelfPanel;
    }

    // ───────────────────────── the shelf ─────────────────────────
    private async Task LoadShelf(bool restore)
    {
        _shelfCount.Text = "LOOKING IN " + MusicLibrary.Folder().ToUpperInvariant() + " …";
        var albums = await Task.Run(() => MusicLibrary.Scan());
        if (_closed) return;
        _shelf = albums;
        _shelfLoaded = true;
        FillShelf();
        if (restore && _album == null)
        {
            var last = _shelf.FirstOrDefault(a => a.Key == _st.LastAlbum);
            if (last != null) LoadAlbum(last, "YOUR MUSIC LIBRARY", play: false);
            else if (_shelf.Count > 0) ShowShelf(true);
        }
    }

    private void FillShelf()
    {
        _shelfWrap.Children.Clear();
        var shown = MusicLibrary.Filter(_shelf, _filter.Text).ToList();
        _shelfCount.Text = !_shelfLoaded ? "" : _shelf.Count == 0 ? "" : shown.Count == _shelf.Count ? $"{_shelf.Count} ALBUMS" : $"{shown.Count} OF {_shelf.Count}";
        if (_shelfLoaded && _shelf.Count == 0)
        {
            _shelfWrap.Children.Add(new TextBlock
            {
                Text = $"No music in {MusicLibrary.Folder()} yet. Put albums there, a folder each, or drop songs on this window.",
                Foreground = DeckColors.Muted, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(4)
            });
            return;
        }
        foreach (var a in shown.Take(600)) _shelfWrap.Children.Add(ShelfCard(a));
    }

    private Control ShelfCard(MusicAlbum a)
    {
        var art = new Border
        {
            Width = 132, Height = 132, CornerRadius = new CornerRadius(4), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)),
            Child = new DiscView { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        if (a.Cover != null)
        {
            var img = new Image { Stretch = Stretch.UniformToFill };
            art.Child = img;
            _ = Task.Run(() => Cover(a.Cover, 200)).ContinueWith(t => Dispatcher.UIThread.Post(() => img.Source = t.Result));
        }
        var text = new StackPanel
        {
            Width = 132, Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                new TextBlock { Text = a.Title, Foreground = DeckColors.Text, FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = a.Artist, Foreground = DeckColors.Muted, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        };
        var b = new Button
        {
            Content = new StackPanel { Children = { art, text } }, Background = Brushes.Transparent, Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 10, 12), CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(b, $"{a.Title} — {a.Artist} · {a.Tracks.Count} tracks");
        b.Click += (_, _) => { LoadAlbum(a, "YOUR MUSIC LIBRARY", play: true); ShowShelf(false); };
        return b;
    }

    private void ShowShelf(bool show)
    {
        _shelfPanel.IsVisible = show;
        if (show) { if (!_shelfLoaded) _ = LoadShelf(false); _filter.Focus(); }
        else Focus();
    }

    private Bitmap? Cover(string? path, int width)
    {
        if (path == null) return null;
        var key = path + "@" + width;
        lock (_covers) if (_covers.TryGetValue(key, out var hit)) return hit;
        Bitmap? bmp = null;
        try
        {
            if (path.StartsWith("file://", StringComparison.Ordinal)) path = new Uri(path).LocalPath;
            using var s = File.OpenRead(path);
            bmp = Bitmap.DecodeToWidth(s, width);
        }
        catch { /* no picture then */ }
        lock (_covers) _covers[key] = bmp;
        return bmp;
    }

    // ───────────────────────── discs and tracks ─────────────────────────
    public void LoadAlbum(MusicAlbum album, string source, bool play, int first = 0)
    {
        Halt();
        _album = album;
        _source = source;
        _trayOpen = false;
        _disc.Opacity = 1;
        if (album.Tracks.Count > 0 && album.Tracks[0].IsFile) { _st.LastAlbum = album.Key; SaveState(); }
        _disc.Cover = Cover(album.Cover, 480);
        _caseTitle.Text = album.Title;
        _caseArtist.Text = album.Artist.ToUpperInvariant();
        _albumHead.Text = album.Title;
        _albumSub.Text = $"{album.Artist.ToUpperInvariant()} · {album.Tracks.Count} TRACKS";
        FillTracks();
        MakeOrder(first);
        ShowTrack();
        if (play) PlayAt(_pos);
        else if (_lib != null) SetStatus("● READY TO PLAY");
    }

    private void FillTracks()
    {
        _rows.Clear();
        var items = new List<Control>();
        foreach (var t in _album?.Tracks ?? (IReadOnlyList<MusicTrack>)Array.Empty<MusicTrack>())
        {
            var num = new TextBlock { Text = (t.Number > 0 ? t.Number : _rows.Count + 1).ToString("00"), Foreground = DeckColors.Hint, FontSize = 12, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = t.Title, Foreground = DeckColors.Text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var len = new TextBlock { Text = LengthOf(t) is > 0 and var l ? DeckUi.Clock(l) : "", Foreground = DeckColors.Hint, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Grid.SetColumn(title, 1); Grid.SetColumn(len, 2);
            g.Children.Add(num); g.Children.Add(title); g.Children.Add(len);
            ToolTip.SetTip(g, t.Artist == _album!.Artist ? t.Title : $"{t.Title} — {t.Artist}");
            _rows.Add((num, title, len));
            items.Add(g);
        }
        _list.ItemsSource = items;
    }

    private long LengthOf(MusicTrack t) => t.LengthMs > 0 ? t.LengthMs : _lengths.TryGetValue(t.Path, out var l) ? l : 0;

    private void MakeOrder(int first)
    {
        var n = _album?.Tracks.Count ?? 0;
        _order = Enumerable.Range(0, n).ToList();
        if (_st.Shuffle && n > 1)
        {
            var rest = _order.Where(i => i != first).OrderBy(_ => Random.Shared.Next()).ToList();
            _order = new List<int> { first }.Concat(rest).ToList();
        }
        _pos = n == 0 ? -1 : Math.Max(0, _order.IndexOf(first));
    }

    private MusicTrack? Current => _album != null && _pos >= 0 && _pos < _order.Count ? _album.Tracks[_order[_pos]] : null;

    private void PlayTrack(int trackIndex)
    {
        if (_album == null || trackIndex < 0 || trackIndex >= _album.Tracks.Count) return;
        if (_st.Shuffle) MakeOrder(trackIndex); else _pos = trackIndex;
        PlayAt(_pos);
    }

    private void PlayAt(int pos)
    {
        if (_album == null || pos < 0 || pos >= _order.Count) return;
        _pos = pos;
        _gen++;
        _time = 0;
        var t = Current!;
        _length = LengthOf(t);
        ShowTrack();
        if (_player == null || _lib == null) return;
        _trayOpen = false;
        _disc.Opacity = 1;
        var old = _media;
        try
        {
            _media = t.IsFile ? new Media(_lib, new Uri(t.Path)) : new Media(_lib, t.Path, FromType.FromLocation);
            var gen = _gen;
            _media.MetaChanged += (_, _) => Dispatcher.UIThread.Post(() => { if (gen == _gen && !_closed) ShowMeta(); });
            _player.Media = _media;
            _player.Play();
            SetStatus("● LOADING");
        }
        catch (Exception e) { SetStatus("● CAN'T PLAY THIS TRACK"); Console.Error.WriteLine("cdplayer: " + e.Message); }
        old?.Dispose();
    }

    /// <summary>Stops without moving: the next play starts the same track.</summary>
    private void Halt()
    {
        _gen++;
        _playing = _paused = false;
        try { _player?.Stop(); } catch { /* already stopped */ }
        _time = 0;
        RefreshPlay();
    }

    private void TogglePlay()
    {
        if (_player == null) return;
        if (_album == null || _album.Tracks.Count == 0) { ShowShelf(true); return; }
        if (_trayOpen) ToggleTray();
        if (_playing) _player.Pause();
        else if (_paused) _player.Play();
        else PlayAt(Math.Max(0, _pos));
    }

    private void Stop() { Halt(); ShowTrack(); SetStatus("● STOPPED"); }

    private void Prev()
    {
        if (_album == null) return;
        if (_time > 3000 && _player != null) { _player.Time = 0; return; }
        if (_pos > 0) PlayAt(_pos - 1);
        else if (_st.Repeat == "all") PlayAt(_order.Count - 1);
        else PlayAt(0);
    }

    private void Next()
    {
        if (_album == null) return;
        if (_pos + 1 < _order.Count) PlayAt(_pos + 1);
        else if (_st.Repeat == "all") { if (_st.Shuffle) MakeOrder(_order[0]); PlayAt(0); }
    }

    private void OnEnded()
    {
        _playing = false;
        if (_st.Repeat == "one") { PlayAt(_pos); return; }
        if (_pos + 1 < _order.Count || _st.Repeat == "all") { Next(); return; }
        // the end of the disc: back to the first track, stopped, like a real player
        _pos = 0;
        _time = 0;
        _paused = false;
        ShowTrack();
        RefreshPlay();
        SetStatus("● END OF DISC");
    }

    private void SkipBy(long ms)
    {
        if (_player == null || _length <= 0 || !(_playing || _paused)) return;
        _player.Time = Math.Clamp(_time + ms, 0, Math.Max(0, _length - 500));
    }

    private void NextDisc()
    {
        if (_shelf.Count == 0) { ShowShelf(true); return; }
        var i = _album == null ? -1 : _shelf.FindIndex(a => a.Key == _album.Key);
        LoadAlbum(_shelf[(i + 1) % _shelf.Count], "YOUR MUSIC LIBRARY", play: true);
    }

    private void ToggleTray()
    {
        _trayOpen = !_trayOpen;
        if (_trayOpen) { Halt(); _disc.Opacity = 0.12; SetStatus("● TRAY OPEN"); }
        else { _disc.Opacity = 1; SetStatus(_album == null ? "● NO DISC" : "● READY TO PLAY"); }
    }

    private void Clear()
    {
        Halt();
        _album = null;
        _order.Clear(); _pos = -1;
        _disc.Cover = null;
        _caseTitle.Text = ""; _caseArtist.Text = "";
        _albumHead.Text = ""; _albumSub.Text = "CHANGER EMPTY";
        _st.LastAlbum = null; SaveState();
        FillTracks();
        ShowTrack();
        SetStatus("● NO DISC");
    }

    // ───────────────────────── audio CDs ─────────────────────────
    public static bool HasCdDrive() { try { return File.Exists(AudioCdDevice); } catch { return false; } }

    private async Task LoadAudioCd()
    {
        if (_lib == null) return;
        SetStatus("● READING DISC");
        try
        {
            using var disc = new Media(_lib, "cdda://" + AudioCdDevice, FromType.FromLocation);
            await disc.Parse(MediaParseOptions.ParseLocal | MediaParseOptions.ParseNetwork, 15000);
            var tracks = new List<MusicTrack>();
            var n = 0;
            foreach (var sub in disc.SubItems)
            {
                n++;
                var title = sub.Meta(MetadataType.Title);
                if (string.IsNullOrWhiteSpace(title) || title.StartsWith("Track", StringComparison.OrdinalIgnoreCase)) title = $"Track {n}";
                tracks.Add(new MusicTrack(sub.Mrl, title, sub.Meta(MetadataType.Artist) ?? "Audio CD", "Audio CD", n, sub.Duration));
            }
            if (_closed) return;
            if (tracks.Count == 0) { SetStatus("● NO AUDIO CD"); return; }
            var name = disc.Meta(MetadataType.Album);
            var artist = disc.Meta(MetadataType.Artist);
            LoadAlbum(new MusicAlbum("cdda", string.IsNullOrWhiteSpace(name) ? "Audio CD" : name!,
                string.IsNullOrWhiteSpace(artist) ? "Audio CD" : artist!, null, tracks), "AUDIO CD", play: true);
        }
        catch (Exception e) { SetStatus("● NO AUDIO CD"); Console.Error.WriteLine("cdplayer: " + e.Message); }
    }

    // ───────────────────────── files ─────────────────────────
    /// <summary>Plays files (or folders of them) as one disc.</summary>
    public void AddFiles(IEnumerable<string> paths, bool play)
    {
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                try { files.AddRange(Directory.EnumerateFiles(p, "*", opts).Where(MusicLibrary.IsAudio).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)); }
                catch { /* unreadable */ }
            }
            else if (File.Exists(p) && MusicLibrary.IsAudio(p)) files.Add(p);
        }
        if (files.Count == 0) return;
        var album = MusicLibrary.Loose(files);
        var first = album.Tracks.ToList().FindIndex(t => t.Path == files[0]);
        LoadAlbum(album, album.Title == "Loaded tracks" ? "LOADED TRACKS" : "FROM YOUR FILES", play, Math.Max(0, first));
    }

    private async Task OpenFiles()
    {
        var r = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Open songs",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Music") { Patterns = MusicLibrary.Extensions.Select(e => "*" + e).ToList() },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        var paths = r.Select(x => x.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (paths.Count > 0) AddFiles(paths, true);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
        if (paths is { Count: > 0 }) AddFiles(paths, true);
    }

    // ───────────────────────── what the panel shows ─────────────────────────
    private void SetStatus(string s) => _status.Text = s;

    private void ShowTrack()
    {
        var t = Current;
        var n = _album?.Tracks.Count ?? 0;
        if (t == null)
        {
            _title.Text = _album == null ? "No disc" : _album.Title;
            _artist.Text = _lib == null ? _artist.Text : _album == null ? "Open the shelf to pick an album" : _album.Artist;
            _sourceText.Text = _album == null ? "" : _source;
            _lcdTrack.Text = "--/--"; _lcdTime.Text = "--:--";
        }
        else
        {
            _title.Text = t.Title;
            _artist.Text = t.Artist == MusicLibrary.UnknownArtist ? _album!.Title : $"{t.Artist} — {_album!.Title}";
            _sourceText.Text = _source;
            _lcdTrack.Text = $"{_order[_pos] + 1:00}/{n:00}";
            _lcdTime.Text = DeckUi.Clock(_time);
        }
        _updating = true;
        _seek.Value = _length > 0 ? _time * 1000.0 / _length : 0;
        _updating = false;
        _elapsed.Text = DeckUi.Clock(_time);
        _total.Text = _length > 0 ? DeckUi.Clock(_length) : "--:--";
        for (var i = 0; i < _rows.Count; i++)
        {
            var on = t != null && _album!.Tracks[i] == t;
            _rows[i].Title.Foreground = on ? DeckColors.Ink : DeckColors.Text;
            _rows[i].Num.Foreground = on ? DeckColors.Ink : DeckColors.Hint;
        }
        UpdateTitle();
    }

    /// <summary>The track's own tags, once libVLC has read them, over what its file name said.</summary>
    private void ShowMeta()
    {
        if (_media == null || _album == null || Current is not { } t) return;
        var title = _media.Meta(MetadataType.Title);
        var artist = _media.Meta(MetadataType.Artist);
        var i = _order[_pos];
        if (!string.IsNullOrWhiteSpace(title) && t.IsFile && title != Path.GetFileName(t.Path))
        {
            var better = t with { Title = title!, Artist = string.IsNullOrWhiteSpace(artist) ? t.Artist : artist! };
            var tracks = _album.Tracks.ToList();
            tracks[i] = better;
            _album = _album with { Tracks = tracks };
            if (i < _rows.Count) _rows[i].Title.Text = better.Title;
        }
        // a cover kept inside the file
        if (_disc.Cover == null && _media.Meta(MetadataType.ArtworkURL) is { Length: > 0 } art && art.StartsWith("file://", StringComparison.Ordinal))
            _disc.Cover = Cover(art, 480);
        ShowTrack();
    }

    private void OnLength(long l)
    {
        _length = l;
        if (Current is { } t && l > 0)
        {
            _lengths[t.Path] = l;
            var i = _order[_pos];
            if (i < _rows.Count) _rows[i].Len.Text = DeckUi.Clock(l);
        }
        _total.Text = l > 0 ? DeckUi.Clock(l) : "--:--";
    }

    private void OnTime(long t)
    {
        _time = t;
        _elapsed.Text = DeckUi.Clock(t);
        _lcdTime.Text = DeckUi.Clock(t);
        if (_length > 0)
        {
            _updating = true;
            _seek.Value = t * 1000.0 / _length;
            _updating = false;
        }
    }

    private void RefreshPlay()
    {
        foreach (var k in _playKeys) k.Content = DeckGlyphs.Create(_playing ? "pause" : "play", 26);
        DeckUi.SetOn(_fPlay, _playing);
        DeckUi.SetOn(_fPause, _paused);
        _disc.Spinning = _playing;
        if (_playing) SetStatus("● PLAYING");
        else if (_paused) SetStatus("● PAUSED");
        UpdateTray();
        UpdateTitle();
    }

    private void UpdateTitle() => Title = Current is { } t && (_playing || _paused) ? $"{t.Title} — CD Player" : "CD Player";

    // ───────────────────────── modes ─────────────────────────
    private void ToggleShuffle()
    {
        _st.Shuffle = !_st.Shuffle;
        var cur = Current != null ? _order[_pos] : 0;
        MakeOrder(cur);
        ApplyState(); SaveState();
    }

    private void CycleRepeat()
    {
        _st.Repeat = _st.Repeat switch { "off" => "all", "all" => "one", _ => "off" };
        ApplyState(); SaveState();
    }

    private void ToggleMute() { _st.Muted = !_st.Muted; ApplyVolume(); SaveState(); }

    private void SetVolume(int v)
    {
        _st.Volume = Math.Clamp(v, 0, 100);
        if (_st.Volume > 0) _st.Muted = false;
        ApplyVolume(); SaveState();
    }

    private void ApplyVolume()
    {
        _updating = true; _vol.Value = _st.Volume; _updating = false;
        _volText.Text = _st.Muted ? "MUTE" : _st.Volume + "%";
        _muteKey.Content = DeckGlyphs.Create(_st.Muted || _st.Volume == 0 ? "mute" : "volume", 20);
        if (_player != null) { _player.Volume = _st.Muted ? 0 : _st.Volume; _player.Mute = _st.Muted; }
    }

    private void ToggleCdView() { _st.CdView = !_st.CdView; ApplyState(); SaveState(); }

    private void ApplyState()
    {
        DeckUi.SetOn(_shuffleKey, _st.Shuffle);
        DeckUi.SetOn(_fShuffle, _st.Shuffle);
        DeckUi.SetOn(_repeatKey, _st.Repeat != "off");
        DeckUi.SetOn(_fRepeat, _st.Repeat != "off");
        DeckUi.SetOn(_fOne, _st.Repeat == "one");
        ToolTip.SetTip(_repeatKey, $"Repeat: {_st.Repeat} (R)");
        ApplyVolume();

        var cd = _st.CdView;
        _faceplate.IsVisible = _booklet.IsVisible = !cd;
        Grid.SetColumnSpan(_discColumn, cd ? 3 : 1);
        _disc.Width = _disc.Height = cd ? 440 : 270;
        _cdViewKeys.IsVisible = cd;
        DeckUi.SetOn(_cdPill, cd);
    }

    private void Fullscreen() =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    // ───────────────────────── keys ─────────────────────────
    private void OnKey(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Escape)
        {
            if (_shelfPanel.IsVisible) ShowShelf(false);
            else if (_st.CdView) ToggleCdView();
            else if (WindowState == WindowState.FullScreen) Fullscreen();
            else return;
            e.Handled = true;
            return;
        }
        if (e.Source is TextBox) return;          // typing in the shelf's filter
        if (ctrl && e.Key == Key.O) { _ = OpenFiles(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.K) { ShowShelf(true); e.Handled = true; return; }
        if (ctrl || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        switch (e.Key)
        {
            case Key.Space: case Key.K: TogglePlay(); break;
            case Key.J: Prev(); break;
            case Key.L: Next(); break;
            case Key.Left: SkipBy(-5000); break;
            case Key.Right: SkipBy(5000); break;
            case Key.Up when e.Source is not ListBoxItem and not ListBox: SetVolume(_st.Volume + 5); break;
            case Key.Down when e.Source is not ListBoxItem and not ListBox: SetVolume(_st.Volume - 5); break;
            case Key.X: ToggleShuffle(); break;
            case Key.R: CycleRepeat(); break;
            case Key.U: ToggleMute(); break;
            case Key.D: NextDisc(); break;
            case Key.S: ShowShelf(!_shelfPanel.IsVisible); break;
            case Key.E: ToggleTray(); break;
            case Key.C: ToggleCdView(); break;
            case Key.F: Fullscreen(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ───────────────────────── the tray icon ─────────────────────────
    private void UpdateTray()
    {
        if (_closed) return;
        if (Current is not { } t || !(_playing || _paused)) { TrayService.Remove("cdplayer"); return; }
        TrayService.Upsert(new TrayEntry
        {
            Key = "cdplayer",
            Glyph = _playing ? "▶" : "⏸",
            Tooltip = (_playing ? "Playing: " : "Paused: ") + t.Title,
            Menu = new List<TrayMenuEntry>
            {
                new() { Id = "play", Label = _playing ? "Pause" : "Play" },
                new() { Id = "prev", Label = "Previous" },
                new() { Id = "next", Label = "Next" },
                new() { Separator = true },
                new() { Id = "show", Label = "Show CD Player" },
                new() { Id = "close", Label = "Close" }
            },
            Click = () => Dispatcher.UIThread.Post(Raise),
            MenuSelected = id => Dispatcher.UIThread.Post(() =>
            {
                switch (id)
                {
                    case "play": TogglePlay(); break;
                    case "prev": Prev(); break;
                    case "next": Next(); break;
                    case "show": Raise(); break;
                    case "close": Close(); break;
                }
            })
        });
    }

    public void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
    }

    // ───────────────────────── state ─────────────────────────
    private void Ui(Action a)
    {
        var g = _gen;
        Dispatcher.UIThread.Post(() => { if (g == _gen && !_closed) a(); });
    }

    private void LoadState()
    {
        try { if (File.Exists(StatePath)) _st = JsonSerializer.Deserialize<CdPlayerState>(File.ReadAllText(StatePath)) ?? new(); }
        catch { _st = new(); }
        if (_st.Repeat is not ("off" or "all" or "one")) _st.Repeat = "off";
    }

    private void SaveState()
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(_st, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* not worth bothering anyone */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        if (Instance == this) Instance = null;
        TrayService.Remove("cdplayer");
        SaveState();
        var p = _player; var m = _media;
        // libVLC can take a moment to stop; not on the UI thread
        if (p != null) Task.Run(() => { try { p.Stop(); p.Dispose(); m?.Dispose(); } catch { /* closing anyway */ } });
    }
}
