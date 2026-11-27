// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Voice Memos: record from the microphone into ~/Music/Voice Memos (Ogg Opus), play memos back, rename and delete
/// them. Recording and playing go through GStreamer (gst-launch-1.0), the same media stack Red Star Online uses.
/// </summary>
public sealed class VoiceMemosWindow : Window
{
    public static string Folder => Path.Combine(AppPaths.UserFolder("Music"), "Voice Memos");

    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly Button _record = new() { Classes = { "primary" }, MinWidth = 120, Padding = new Thickness(14, 8) };
    private readonly Button _play = new() { MinWidth = 90 };
    private readonly TextBlock _time = new() { Classes = { "lcd-digits" }, Text = "0:00", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _status = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _empty = new()
    {
        Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), MaxWidth = 300,
        Children =
        {
            new IconView { Kind = "mic", Size = 24, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.55 },
            new TextBlock { Text = "No memos yet", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "Press Record and speak; press Stop when you're done. Each memo is saved here.", Classes = { "caption" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap },
        },
    };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Recorder? _rec, _player;

    private void RecordLook(bool recording) => _record.Content = recording ? Icons.Label("stop", "Stop") : Icons.Label("record", "Record");
    private void PlayLook(bool playing) => _play.Content = playing ? Icons.Label("stop", "Stop") : Icons.Label("play", "Play");

    public VoiceMemosWindow()
    {
        Title = "Voice Memos";
        Width = 520; Height = 580; MinWidth = 440; MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button B(string icon, string text, Action act) { var b = new Button { Content = Icons.Label(icon, text, 15) }; b.Click += (_, _) => act(); return b; }
        RecordLook(false);
        PlayLook(false);
        _record.Click += (_, _) => { if (_rec?.Running == true) StopRecording(); else StartRecording(); };
        _play.Click += (_, _) => { if (_player?.Running == true) _player.Stop(500); else Play(); };
        var header = new AppHeader { AppId = "voicememos", Heading = "Voice Memos" };
        header.Children.Add(B("folder", "Show in Files", () => WindowManager.Open(new FileManagerWindow(Folder), "files")));
        // the recorder: the big key and the time on the deck's display
        var deck = new DockPanel { Children = { _record } };
        DockPanel.SetDock(_record, Dock.Left);
        deck.Children.Add(new Border { Classes = { "lcd" }, Margin = new Thickness(12, 0, 0, 0), Child = _time });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0),
            Children = { _play, B("rename", "Rename…", () => _ = Rename()), B("trash", "Delete", Delete) } };
        var root = new DockPanel { Margin = new Thickness(18, 16, 18, 18) };
        var head = new StackPanel { Spacing = 10, Children = { header, deck, _status } };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(actions);
        root.Children.Add(new Border { Classes = { "panel" }, Padding = new Thickness(4), Margin = new Thickness(0, 12, 0, 0), Child = new Panel { Children = { _list, _empty } } });
        Content = root;
        _list.DoubleTapped += (_, _) => Play();
        _tick.Tick += (_, _) => { if (_rec is { Running: true } r) _time.Text = Recorder.Format(r.Elapsed); };
        Closing += (_, _) => { _rec?.Stop(); _player?.Stop(300); };
        Fill();
        if (Gst() == null) { _status.Text = Missing; _record.IsEnabled = _play.IsEnabled = false; }
        else _status.Text = "Memos are kept in Music/Voice Memos.";
    }

    private const string Missing = "Voice Memos needs GStreamer. Install gstreamer, gst-plugins-base and gst-plugins-good from Software (or with pacman), then open Voice Memos again.";

    private static string? Gst() => ProcessUtil.Which("gst-launch-1.0");

    /// <summary>The recording pipeline (public for the tests): the default microphone to Ogg Opus.</summary>
    public static List<string> RecordArgs(string file) => new()
    {
        "-e", "-q", "autoaudiosrc", "!", "audioconvert", "!", "audioresample", "!", "opusenc", "bitrate=64000", "!", "oggmux", "!",
        "filesink", "location=" + file,
    };

    private sealed record Memo(string Path)
    {
        public override string ToString()
        {
            var fi = new FileInfo(Path);
            return $"{System.IO.Path.GetFileNameWithoutExtension(Path)}    {fi.LastWriteTime:d MMM, HH:mm}, {Math.Max(1, fi.Length / 1024)} KB";
        }
    }

    private void Fill(string? select = null)
    {
        var memos = new List<Memo>();
        try
        {
            if (Directory.Exists(Folder))
                memos = Directory.GetFiles(Folder).Where(f => f.EndsWith(".ogg") || f.EndsWith(".opus") || f.EndsWith(".oga"))
                    .OrderByDescending(File.GetLastWriteTime).Select(f => new Memo(f)).ToList();
        }
        catch { }
        _list.ItemsSource = memos;
        _empty.IsVisible = memos.Count == 0;
        _list.SelectedItem = memos.FirstOrDefault(m => m.Path == select) ?? memos.FirstOrDefault();
    }

    private Memo? Selected => _list.SelectedItem as Memo;

    private void StartRecording()
    {
        if (Gst() is not { } gst) { _status.Text = Missing; return; }
        _player?.Stop(300);
        var file = Path.Combine(Folder, $"Memo {DateTime.Now:yyyy-MM-dd HH-mm-ss}.ogg");
        var rec = new Recorder(file, "voice-memos.log");
        rec.Ended += code => Dispatcher.UIThread.Post(() =>
        {
            _tick.Stop();
            RecordLook(false);
            _time.Text = "0:00";
            bool ok = File.Exists(file) && new FileInfo(file).Length > 200;
            if (!ok)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                _status.Text = $"Nothing was recorded (exit code {code}). Is a microphone connected? Details in ~/.config/redstaros/voice-memos.log.";
            }
            else _status.Text = "Saved " + Path.GetFileName(file) + ".";
            Fill(file);
        });
        if (!rec.Start(gst, RecordArgs(file))) { _status.Text = "Couldn't start GStreamer."; return; }
        _rec = rec;
        RecordLook(true);
        _status.Text = "Recording…";
        _tick.Start();
    }

    private void StopRecording() { _status.Text = "Finishing…"; _rec?.Stop(); }

    private void Play()
    {
        if (Selected is not { } m || Gst() is not { } gst || _rec?.Running == true) return;
        _player?.Stop(300);
        var p = new Recorder(m.Path, "voice-memos.log");
        p.Ended += _ => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_player, p)) PlayLook(false); });
        if (!p.Start(gst, new[] { "-q", "playbin", "uri=" + new Uri(m.Path).AbsoluteUri })) return;
        _player = p;
        PlayLook(true);
    }

    private async Task Rename()
    {
        if (Selected is not { } m) return;
        var name = await Dialogs.Prompt(this, "Rename memo", "Name", Path.GetFileNameWithoutExtension(m.Path));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = string.Concat(name.Trim().Split(Path.GetInvalidFileNameChars()));
        var to = Path.Combine(Folder, name + Path.GetExtension(m.Path));
        if (to == m.Path) return;
        try { File.Move(m.Path, to); Fill(to); }
        catch (Exception ex) { _status.Text = "Couldn't rename: " + ex.Message; }
    }

    private void Delete()
    {
        if (Selected is not { } m) return;
        _player?.Stop(200);
        try { FileOps.MoveToTrash(m.Path); _status.Text = "Moved to the Trash."; Fill(); }
        catch (Exception ex) { _status.Text = "Couldn't delete: " + ex.Message; }
    }
}
