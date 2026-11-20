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

    private readonly ListBox _list = new();
    private readonly Button _record = new() { Content = "● Record", Classes = { "primary" }, MinWidth = 120 };
    private readonly Button _play = new() { Content = "▶ Play", MinWidth = 90 };
    private readonly TextBlock _time = new() { FontSize = 26, FontWeight = FontWeight.SemiBold, Text = "0:00", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Recorder? _rec, _player;

    public VoiceMemosWindow()
    {
        Title = "Voice Memos";
        Width = 520; Height = 560; MinWidth = 420; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button B(string text, Action act) { var b = new Button { Content = text }; b.Click += (_, _) => act(); return b; }
        _record.Click += (_, _) => { if (_rec?.Running == true) StopRecording(); else StartRecording(); };
        _play.Click += (_, _) => { if (_player?.Running == true) _player.Stop(500); else Play(); };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _record, _time } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { _play, B("Rename…", () => _ = Rename()), B("Delete", Delete), B("Show in Files", () => WindowManager.Open(new FileManagerWindow(Folder), "files")) } };
        var root = new DockPanel { Margin = new Thickness(16) };
        var head = new StackPanel { Spacing = 10, Children = { top, _status } };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(actions);
        root.Children.Add(new Border { Margin = new Thickness(0, 12), Child = _list });
        Content = root;
        _list.DoubleTapped += (_, _) => Play();
        _tick.Tick += (_, _) => { if (_rec is { Running: true } r) _time.Text = Recorder.Format(r.Elapsed); };
        Closing += (_, _) => { _rec?.Stop(); _player?.Stop(300); };
        Fill();
        if (Gst() == null) { _status.Text = Missing; _record.IsEnabled = _play.IsEnabled = false; }
        else _status.Text = "Memos are kept in Music/Voice Memos.";
    }

    private const string Missing = "Voice Memos needs GStreamer's command-line tool (Arch/CachyOS: pacman -S gstreamer gst-plugins-base gst-plugins-good · Debian/Ubuntu: apt install gstreamer1.0-tools gstreamer1.0-plugins-good).";

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
            return $"{System.IO.Path.GetFileNameWithoutExtension(Path)}    ·  {fi.LastWriteTime:d MMM, HH:mm}  ·  {Math.Max(1, fi.Length / 1024)} KB";
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
            _record.Content = "● Record";
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
        _record.Content = "■ Stop";
        _status.Text = "Recording…";
        _tick.Start();
    }

    private void StopRecording() { _status.Text = "Finishing…"; _rec?.Stop(); }

    private void Play()
    {
        if (Selected is not { } m || Gst() is not { } gst || _rec?.Running == true) return;
        _player?.Stop(300);
        var p = new Recorder(m.Path, "voice-memos.log");
        p.Ended += _ => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_player, p)) _play.Content = "▶ Play"; });
        if (!p.Start(gst, new[] { "-q", "playbin", "uri=" + new Uri(m.Path).AbsoluteUri })) return;
        _player = p;
        _play.Content = "■ Stop";
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
