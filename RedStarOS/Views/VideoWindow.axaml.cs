using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibVLCSharp.Shared;
using RedStarOS.Services;

namespace RedStarOS.Views;

public sealed class PlayItem : INotifyPropertyChanged
{
    private bool _active;
    public string Path { get; init; } = "";
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool Active
    {
        get => _active;
        set { _active = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label))); }
    }
    public string Label => (_active ? "▶  " : "    ") + Title;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class VideoResume { public string Path { get; set; } = ""; public double Pos { get; set; } public long At { get; set; } }

public sealed class VideoState
{
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public double Speed { get; set; } = 1.0;
    public string Repeat { get; set; } = "off";
    public bool SubsOn { get; set; } = true;
    public bool ShowList { get; set; } = true;
    public List<VideoResume> Resume { get; set; } = new();
}

/// <summary>Loads libvlc once; null when it is not installed.</summary>
public static class VideoEngine
{
    private static LibVLC? _lib;
    private static bool _tried;
    public static string? Error { get; private set; }

    public static LibVLC? Get()
    {
        if (_tried) return _lib;
        _tried = true;
        try { Core.Initialize(); _lib = new LibVLC(); }
        catch (Exception e) { Error = e.Message; _lib = null; }
        return _lib;
    }
}

public partial class VideoWindow : Window
{
    public static VideoWindow? Instance { get; private set; }

    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 3 };
    private static readonly string[] SubExts = { ".srt", ".vtt", ".ass", ".ssa", ".sub" };
    private static string StatePath => AppPaths.ConfigFile("video.json");

    private readonly LibVLC _lib;
    private readonly MediaPlayer _player;
    private readonly ObservableCollection<PlayItem> _items = new();
    private VideoState _st = new();
    private int _index = -1;
    private int _gen;                 // bumped for each media so stale events are ignored
    private long _length;             // ms
    private long _time;               // ms
    private double _resumeTo;         // seconds, applied once playing
    private bool _updating, _playing, _subsAdded, _fullscreen, _forceClose, _loading = true;
    private string? _pendingSubs;
    private long _lastSave;
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(2500) };
    private WindowState _prevState = WindowState.Normal;
    private bool _listBefore;
    private readonly object _fl = new();
    private IntPtr _buf, _front;
    private uint _fw, _fh;
    private long _bufSize;
    private bool _fresh, _posted, _closedPlayer;
    private WriteableBitmap? _bmp;
    private bool _ready;              // native video surface exists
    private int _pendingIndex = -1;

    public VideoWindow() : this(VideoEngine.Get() ?? throw new InvalidOperationException("libvlc is not available")) { }

    public VideoWindow(LibVLC lib)
    {
        _lib = lib;
        InitializeComponent();
        _player = new MediaPlayer(_lib);
        // Frames are pulled out of libvlc into memory and drawn by Avalonia itself, so the picture is a normal
        // inline control that works on X11 and Wayland alike (no native child window).
        _player.SetVideoFormatCallbacks(OnFormat, null);
        _player.SetVideoCallbacks(OnLock, OnUnlock, OnDisplay);
        Instance = this;

        LoadState();
        PlayList.ItemsSource = _items;
        foreach (var s in Speeds) SpeedBox.Items.Add(s == 1 ? "1× (normal)" : $"{s:0.##}×");
        SpeedBox.SelectedIndex = Math.Max(0, Array.IndexOf(Speeds, _st.Speed));
        VolSlider.Value = _st.Volume;
        ListPanel.IsVisible = _st.ShowList;
        RefreshVolume();
        RefreshRepeat();
        RefreshSubs();
        _loading = false;

        SeekSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _updating || _length <= 0) return;
            SeekTo((long)(SeekSlider.Value / 1000.0 * _length));
        };
        VolSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _loading) return;
            _st.Volume = (int)VolSlider.Value;
            if (_st.Volume > 0) _st.Muted = false;
            RefreshVolume(); SaveState();
        };
        SpeedBox.SelectionChanged += (_, _) =>
        {
            if (_loading || SpeedBox.SelectedIndex < 0) return;
            _st.Speed = Speeds[SpeedBox.SelectedIndex];
            _player.SetRate((float)_st.Speed); SaveState();
        };
        PlayList.Tapped += (_, _) =>
        {
            if (PlayList.SelectedItem is PlayItem it) PlayIndex(_items.IndexOf(it));
        };

        // while a video plays, the screen stays on (System Settings → Power)
        var keepOn = "Video" + GetHashCode();
        _player.Playing += (_, _) => Ui(g => { _playing = true; IdleManager.Inhibit(keepOn, true); OnPlaying(); });
        _player.Paused += (_, _) => Ui(g => { _playing = false; IdleManager.Inhibit(keepOn, false); RefreshPlayState(); });
        _player.Stopped += (_, _) => Ui(g => { _playing = false; IdleManager.Inhibit(keepOn, false); RefreshPlayState(); });
        Closed += (_, _) => IdleManager.Inhibit(keepOn, false);
        _player.LengthChanged += (_, e) => { var l = e.Length; Ui(g => { _length = l; LenText.Text = Fmt(l); }); };
        _player.TimeChanged += (_, e) => { var t = e.Time; Ui(g => OnTime(t)); };
        _player.EndReached += (_, _) => Ui(g => OnEnded());
        _player.EncounteredError += (_, _) => Ui(g => ShowError("This file can't be played. It may be damaged or use a format libvlc doesn't support."));

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => Wake(), RoutingStrategies.Tunnel, true);
        AddHandler(DoubleTappedEvent, (s, e) =>
        {
            if (e.Source is Visual v && (Stage.IsVisualAncestorOf(v) || ReferenceEquals(v, Stage))) ToggleFullscreen();
        });
        Video.DoubleTapped += (_, _) => ToggleFullscreen();
        Video.PointerMoved += (_, _) => Wake();
        HoverStrip.PointerEntered += (_, _) => Wake();
        _idle.Tick += (_, _) => { _idle.Stop(); if (_fullscreen && _playing) HideChrome(); };
        Opened += (_, _) =>
        {
            // give the native video host a moment to create its window before libvlc is told to draw into it
            DispatcherTimer.RunOnce(() =>
            {
                if (_forceClose) return;
                _ready = true;
                if (_pendingIndex >= 0) { var i = _pendingIndex; _pendingIndex = -1; PlayIndex(i); }
            }, TimeSpan.FromMilliseconds(350));
        };
        Closing += OnClosing;
        Closed += OnClosed;
        UpdateTitle();
    }

    // ───────────────────────── helpers ─────────────────────────
    /// <summary>Runs on the UI thread, ignoring events that belong to an earlier file.</summary>
    private void Ui(Action<int> a)
    {
        var g = _gen;
        Dispatcher.UIThread.Post(() => { if (g == _gen && !_forceClose) a(g); });
    }

    private static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private PlayItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    private void UpdateTitle()
    {
        var c = Current;
        Title = c == null ? "Video" : $"{c.Title} — Video";
    }

    // ───────────────────────── state ─────────────────────────
    private void LoadState()
    {
        try { if (File.Exists(StatePath)) _st = JsonSerializer.Deserialize<VideoState>(File.ReadAllText(StatePath)) ?? new(); }
        catch { _st = new(); }
    }

    private void SaveState()
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(_st)); } catch { }
    }

    private double LoadResume(string path) => _st.Resume.FirstOrDefault(r => r.Path == path)?.Pos ?? 0;

    private void SaveResume()
    {
        var c = Current;
        if (c == null || _length <= 0) return;
        double pos = _time / 1000.0, len = _length / 1000.0;
        _st.Resume.RemoveAll(r => r.Path == c.Path);
        if (pos >= 5 && pos < len - 10)
        {
            _st.Resume.Add(new VideoResume { Path = c.Path, Pos = pos, At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            if (_st.Resume.Count > 200)
                _st.Resume = _st.Resume.OrderByDescending(r => r.At).Take(200).ToList();
        }
        SaveState();
    }

    // ───────────────────────── playlist ─────────────────────────
    public void AddFiles(IEnumerable<string> files, bool play)
    {
        int first = -1;
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (!FileOps.VideoExtensions.Contains(ext) && !FileOps.AudioExtensions.Contains(ext)) continue;
            var existing = _items.FirstOrDefault(i => i.Path == f);
            if (existing != null) { if (first < 0) first = _items.IndexOf(existing); continue; }
            _items.Add(new PlayItem { Path = f });
            if (first < 0) first = _items.Count - 1;
        }
        ListCount.Text = _items.Count == 0 ? "" : $"({_items.Count})";
        int target = first >= 0 ? first : (_index < 0 && _items.Count > 0 ? 0 : -1);
        if (play && target >= 0)
        {
            if (_ready) PlayIndex(target); else _pendingIndex = target;
        }
    }

    public void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = _prevState == WindowState.Minimized ? WindowState.Normal : _prevState;
        Show(); Activate();
    }

    private void PlayIndex(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        SaveResume();
        _gen++;
        foreach (var it in _items) it.Active = false;
        _index = i;
        var item = _items[i];
        item.Active = true;
        PlayList.SelectedItem = item;
        _subsAdded = false;
        _pendingSubs = FindSubs(item.Path);
        _resumeTo = LoadResume(item.Path);
        _time = 0; _length = 0;
        _updating = true; SeekSlider.Value = 0; _updating = false;
        TimeText.Text = "0:00"; LenText.Text = "0:00";
        UpdateTitle();

        var audio = FileOps.AudioExtensions.Contains(Path.GetExtension(item.Path).ToLowerInvariant());
        Video.IsVisible = !audio;
        _bmp = null; Video.Source = null;
        ShowOverlay(audio ? "🎵" : "", audio ? item.Title : null);

        try
        {
            using var media = new Media(_lib, item.Path, FromType.FromPath);
            _player.Play(media);
        }
        catch (Exception e) { ShowError("Could not start playback: " + e.Message); }
    }

    private void ShowOverlay(string icon, string? text)
    {
        Overlay.IsVisible = text != null;
        OverlayIcon.Text = icon;
        OverlayIcon.IsVisible = icon != "";
        OverlayText.Text = text ?? "";
    }

    private void ShowError(string msg)
    {
        Video.IsVisible = false;
        ShowOverlay("⚠", msg + "\n\nTry “Open with system player” below.");
        _playing = false; RefreshPlayState();
    }

    private static string? FindSubs(string media)
    {
        var dir = Path.GetDirectoryName(media);
        if (dir == null) return null;
        var baseName = Path.GetFileNameWithoutExtension(media);
        foreach (var ext in SubExts)
        {
            var p = Path.Combine(dir, baseName + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ───────────────────────── playback events ─────────────────────────
    private void OnPlaying()
    {
        var c = Current;
        if (c != null)
        {
            var audio = FileOps.AudioExtensions.Contains(Path.GetExtension(c.Path).ToLowerInvariant());
            if (!audio) ShowOverlay("", null);
        }
        ApplyVolume();
        _player.SetRate((float)_st.Speed);
        if (_resumeTo > 0 && _length > 0)
        {
            _player.Time = (long)(_resumeTo * 1000);
            _resumeTo = 0;
        }
        if (!_subsAdded)
        {
            _subsAdded = true;
            if (_pendingSubs != null) AttachSubs(_pendingSubs);
        }
        RefreshPlayState();
    }

    private void OnTime(long t)
    {
        _time = t;
        TimeText.Text = Fmt(t);
        if (_length > 0)
        {
            _updating = true;
            SeekSlider.Value = Math.Clamp(t * 1000.0 / _length, 0, 1000);
            _updating = false;
        }
        var now = Environment.TickCount64;
        if (now - _lastSave > 5000) { _lastSave = now; SaveResume(); }
        if (_resumeTo > 0 && _length > 0)
        {
            _player.Time = (long)(_resumeTo * 1000);
            _resumeTo = 0;
        }
    }

    private void OnEnded()
    {
        _playing = false;
        var c = Current;
        if (c != null) { _st.Resume.RemoveAll(r => r.Path == c.Path); SaveState(); }
        if (_st.Repeat == "one") { PlayIndex(_index); return; }
        if (_index + 1 < _items.Count) { PlayIndex(_index + 1); return; }
        if (_st.Repeat == "all" && _items.Count > 0) { PlayIndex(0); return; }
        RefreshPlayState();
    }

    // ───────────────────────── commands ─────────────────────────
    private void SeekTo(long ms)
    {
        if (_length <= 0) return;
        ms = Math.Clamp(ms, 0, _length);
        _player.Time = ms;
        _time = ms;
        TimeText.Text = Fmt(ms);
    }

    private void TogglePlay()
    {
        if (Current == null) { if (_items.Count > 0) PlayIndex(0); else _ = OpenFiles(); return; }
        if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped || _player.State == VLCState.Error) { PlayIndex(_index); return; }
        _player.Pause();   // toggles
    }

    private void Prev()
    {
        if (_items.Count == 0) return;
        if (_time > 3000) { SeekTo(0); return; }
        PlayIndex(_index > 0 ? _index - 1 : _st.Repeat == "all" ? _items.Count - 1 : 0);
    }

    private void Next()
    {
        if (_items.Count == 0) return;
        if (_index + 1 < _items.Count) PlayIndex(_index + 1);
        else if (_st.Repeat == "all") PlayIndex(0);
    }

    private void ApplyVolume()
    {
        _player.Volume = _st.Muted ? 0 : _st.Volume;
        _player.Mute = _st.Muted;
    }

    private void RefreshVolume()
    {
        VolText.Text = _st.Muted ? "mute" : _st.Volume + "%";
        MuteBtn.Content = _st.Muted || _st.Volume == 0 ? "🔇" : _st.Volume < 40 ? "🔉" : "🔊";
        ApplyVolume();
    }

    private void SetVolume(int v)
    {
        v = Math.Clamp(v, 0, 100);
        _st.Volume = v; if (v > 0) _st.Muted = false;
        _loading = true; VolSlider.Value = v; _loading = false;
        RefreshVolume(); SaveState();
    }

    private void ToggleMute() { _st.Muted = !_st.Muted; RefreshVolume(); SaveState(); }

    private void StepSpeed(int dir)
    {
        var i = Math.Clamp(Array.IndexOf(Speeds, _st.Speed) + dir, 0, Speeds.Length - 1);
        SpeedBox.SelectedIndex = i;   // selection handler applies + saves
    }

    private void CycleRepeat()
    {
        _st.Repeat = _st.Repeat switch { "off" => "all", "all" => "one", _ => "off" };
        RefreshRepeat(); SaveState();
    }

    private void RefreshRepeat() =>
        RepeatBtn.Content = "Repeat: " + _st.Repeat;

    private void RefreshPlayState()
    {
        PlayBtn.Content = _playing ? "⏸" : "▶";
        UpdateTray();
    }

    // ── subtitles ──
    private void AttachSubs(string path)
    {
        try
        {
            _player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
            DispatcherTimer.RunOnce(ApplySubs, TimeSpan.FromMilliseconds(400));
        }
        catch { }
    }

    private void ApplySubs()
    {
        if (_forceClose) return;
        try
        {
            if (!_st.SubsOn) { _player.SetSpu(-1); return; }
            var tracks = _player.SpuDescription.Where(d => d.Id >= 0).ToList();
            if (tracks.Count > 0) _player.SetSpu(tracks[0].Id);
        }
        catch { }
    }

    private void RefreshSubs() => SubsBtn.Content = _st.SubsOn ? "CC" : "CC ✕";

    private void ToggleSubs() { _st.SubsOn = !_st.SubsOn; RefreshSubs(); ApplySubs(); SaveState(); }

    private async Task LoadSubs()
    {
        var dir = Current != null ? Path.GetDirectoryName(Current.Path) : null;
        var opts = new FilePickerOpenOptions
        {
            Title = "Load subtitles",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Subtitles") { Patterns = SubExts.Select(e => "*" + e).ToList() } }
        };
        if (dir != null) opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);
        var r = await FileDialogs.Open(this, opts);
        var p = r.FirstOrDefault()?.TryGetLocalPath();
        if (p == null) return;
        _st.SubsOn = true; RefreshSubs(); AttachSubs(p);
    }

    // ── files ──
    private async Task OpenFiles()
    {
        var exts = FileOps.VideoExtensions.Concat(FileOps.AudioExtensions).Select(e => "*" + e).ToList();
        var r = await FileDialogs.Open(this, new FilePickerOpenOptions
        {
            Title = "Open video or music",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video and audio") { Patterns = exts },
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

    private void RemoveSelected()
    {
        if (PlayList.SelectedItem is not PlayItem it) return;
        var idx = _items.IndexOf(it);
        var wasCurrent = idx == _index;
        _items.RemoveAt(idx);
        if (idx < _index) _index--;
        ListCount.Text = _items.Count == 0 ? "" : $"({_items.Count})";
        if (wasCurrent)
        {
            _index = -1;
            if (_items.Count > 0) PlayIndex(Math.Min(idx, _items.Count - 1)); else StopAll();
        }
    }

    private void StopAll()
    {
        _gen++;
        SaveResume();
        _index = -1; _playing = false; _time = 0; _length = 0;
        Task.Run(() => { try { _player.Stop(); } catch { } });
        Video.IsVisible = false;
        ShowOverlay("🎬", "Drop videos or music here, or press O to open");
        TimeText.Text = "0:00"; LenText.Text = "0:00";
        _updating = true; SeekSlider.Value = 0; _updating = false;
        UpdateTitle(); RefreshPlayState();
    }

    private void OpenExternal()
    {
        var c = Current;
        if (c == null) return;
        try { Process.Start(new ProcessStartInfo("xdg-open", "\"" + c.Path + "\"") { UseShellExecute = false }); } catch { }
        if (_playing) _player.Pause();
    }

    // ── full screen ──
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            _listBefore = ListPanel.IsVisible;
            _fullscreen = true;
            ListPanel.IsVisible = false;
            WindowState = WindowState.FullScreen;
            Wake();
        }
        else
        {
            _fullscreen = false;
            _idle.Stop();
            WindowState = _prevState == WindowState.Minimized ? WindowState.Normal : _prevState;
            ControlsBar.IsVisible = true; HoverStrip.IsVisible = false;
            ListPanel.IsVisible = _listBefore;
            Cursor = Cursor.Default;
        }
    }

    private void HideChrome()
    {
        ControlsBar.IsVisible = false;
        HoverStrip.IsVisible = true;
        Cursor = new Cursor(StandardCursorType.None);
    }

    /// <summary>Any activity brings the controls back; in full screen they hide again after 2.5 s.</summary>
    private void Wake()
    {
        if (!_fullscreen) return;
        ControlsBar.IsVisible = true; HoverStrip.IsVisible = false;
        Cursor = Cursor.Default;
        _idle.Stop(); _idle.Start();
    }

    // ── tray ──
    private void UpdateTray()
    {
        var c = Current;
        if (c == null) { TrayService.Remove("video"); return; }
        TrayService.Upsert(new TrayEntry
        {
            Key = "video",
            Glyph = _playing ? "▶" : "⏸",
            Tooltip = (_playing ? "Playing: " : "Paused: ") + c.Title,
            Menu = new List<TrayMenuEntry>
            {
                new() { Id = "play", Label = _playing ? "Pause" : "Play" },
                new() { Id = "prev", Label = "Previous" },
                new() { Id = "next", Label = "Next" },
                new() { Separator = true },
                new() { Id = "show", Label = "Show Video" },
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

    // ───────────────────────── input ─────────────────────────
    private void OnCmd(object? sender, RoutedEventArgs e)
    {
        switch((sender as Control)?.Tag as string)
        {
            case "open": _ = OpenFiles(); break;
            case "play": TogglePlay(); break;
            case "prev": Prev(); break;
            case "next": Next(); break;
            case "mute": ToggleMute(); break;
            case "repeat": CycleRepeat(); break;
            case "togglesubs": ToggleSubs(); break;
            case "loadsubs": _ = LoadSubs(); break;
            case "playlist": ToggleList(); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "external": OpenExternal(); break;
            case "remove": RemoveSelected(); break;
            case "clear": _items.Clear(); ListCount.Text = ""; StopAll(); break;
        }
    }

    private void ToggleList()
    {
        ListPanel.IsVisible = !ListPanel.IsVisible;
        _st.ShowList = ListPanel.IsVisible;
        SaveState();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        Wake();
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        long step = shift ? 30000 : 5000;
        switch (e.Key)
        {
            case Key.Space: case Key.K: TogglePlay(); break;
            case Key.Left: SeekTo(_time - step); break;
            case Key.Right: SeekTo(_time + step); break;
            case Key.Up: SetVolume(_st.Volume + 5); break;
            case Key.Down: SetVolume(_st.Volume - 5); break;
            case Key.M: ToggleMute(); break;
            case Key.F: case Key.F11: ToggleFullscreen(); break;
            case Key.Escape: if (_fullscreen) ToggleFullscreen(); else return; break;
            case Key.OemComma: StepSpeed(-1); break;
            case Key.OemPeriod: StepSpeed(1); break;
            case Key.PageUp: Prev(); break;
            case Key.PageDown: Next(); break;
            case Key.O: _ = OpenFiles(); break;
            case Key.S: _ = LoadSubs(); break;
            case Key.V: ToggleSubs(); break;
            case Key.L: ToggleList(); break;
            case Key.R: if (ctrl) return; CycleRepeat(); break;
            case Key.Delete: if (PlayList.IsFocused || PlayList.IsKeyboardFocusWithin) RemoveSelected(); else return; break;
            default:
                int n = e.Key >= Key.D0 && e.Key <= Key.D9 ? e.Key - Key.D0
                      : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
                if (n < 0) return;
                SeekTo(_length * n / 10);
                break;
        }
        e.Handled = true;
    }

    // ───────────────────────── closing ─────────────────────────
    // ───────────────────────── frame pipeline ─────────────────────────
    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
        uint w = width, h = height;
        if (w > 1920) { h = (uint)(h * 1920.0 / w); w = 1920; }
        w = Math.Max(2, w & ~1u); h = Math.Max(2, h & ~1u);
        width = w; height = h; pitches = w * 4; lines = h;
        lock (_fl)
        {
            if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
            if (_front != IntPtr.Zero) Marshal.FreeHGlobal(_front);
            _bufSize = (long)w * 4 * h;
            _buf = Marshal.AllocHGlobal((IntPtr)_bufSize);
            _front = Marshal.AllocHGlobal((IntPtr)_bufSize);
            _fw = w; _fh = h; _fresh = false;
        }
        return 1;
    }

    private void OnCleanup(IntPtr opaque) { }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buf);
        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }

    private unsafe void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_fl)
        {
            if (_closedPlayer || _buf == IntPtr.Zero || _front == IntPtr.Zero) return;
            Buffer.MemoryCopy((void*)_buf, (void*)_front, _bufSize, _bufSize);
            _fresh = true;
        }
        if (_posted) return;
        _posted = true;
        Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
    }

    private unsafe void Present()
    {
        _posted = false;
        if (_forceClose) return;
        lock (_fl)
        {
            if (!_fresh || _front == IntPtr.Zero || _fw == 0) return;
            if (_bmp == null || _bmp.PixelSize.Width != _fw || _bmp.PixelSize.Height != _fh)
            {
                _bmp = new WriteableBitmap(new PixelSize((int)_fw, (int)_fh), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                Video.Source = _bmp;
            }
            using var fb = _bmp.Lock();
            int rowBytes = (int)_fw * 4;
            for (int y = 0; y < _fh; y++)
                Buffer.MemoryCopy((byte*)_front + (long)y * rowBytes, (byte*)fb.Address + (long)y * fb.RowBytes, rowBytes, rowBytes);
            _fresh = false;
        }
        Video.InvalidateVisual();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveResume();
        _forceClose = true;
        _idle.Stop();
        _gen++;
        TrayService.Remove("video");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(Instance, this)) Instance = null;
        TrayService.Remove("video");
        var p = _player;
        // Stop first, release the embedded surface, and only dispose after libvlc has finished tearing down its output.
        Task.Run(async () =>
        {
            try { p.Stop(); } catch { }
            await Task.Delay(600);
            await Task.Delay(400);
            lock (_fl) { _closedPlayer = true; }
            try { p.Dispose(); } catch { }
            lock (_fl)
            {
                if (_buf != IntPtr.Zero) Marshal.FreeHGlobal(_buf);
                if (_front != IntPtr.Zero) Marshal.FreeHGlobal(_front);
                _buf = _front = IntPtr.Zero;
            }
        });
    }
}
