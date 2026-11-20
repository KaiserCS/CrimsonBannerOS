// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Screen Recorder: the whole screen or an area, with or without the microphone, to an MP4 in ~/Videos/Recordings.
/// Inside redstar-comp it uses wf-recorder (Wayland screencopy); on X11, FFmpeg's x11grab. Both are free software run
/// as separate programs; neither sends anything anywhere.
/// </summary>
public sealed class ScreenRecorderWindow : Window
{
    public static string Folder => Path.Combine(AppPaths.UserFolder("Videos"), "Recordings");

    private readonly ComboBox _mode = new() { ItemsSource = new[] { "The whole screen", "An area I select" }, SelectedIndex = 0, MinWidth = 200 };
    private readonly CheckBox _mic = new() { Content = "Record the microphone too" };
    private readonly Button _go = new() { Content = "Start recording", Classes = { "primary" } };
    private readonly TextBlock _time = new() { FontSize = 28, FontWeight = Avalonia.Media.FontWeight.SemiBold, Text = "0:00" };
    private readonly TextBlock _status = new() { Classes = { "caption" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _after = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Recorder? _rec;
    private string? _last;

    public ScreenRecorderWindow()
    {
        Title = "Screen Recorder";
        Width = 480; Height = 380; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Button B(string text, Action act) { var b = new Button { Content = text }; b.Click += (_, _) => act(); return b; }
        _after.Children.Add(B("Play", () => { if (_last != null) FileOpener.Open(_last); }));
        _after.Children.Add(B("Show in Files", () => WindowManager.Open(new FileManagerWindow(Folder), "files")));
        _go.Click += (_, _) => { if (_rec?.Running == true) Stop(); else _ = Start(); };
        Content = new StackPanel
        {
            Spacing = 12, Margin = new Thickness(18),
            Children = { _mode, _mic, _time, _go, _status, _after }
        };
        _tick.Tick += (_, _) => { if (_rec is { Running: true } r) _time.Text = Recorder.Format(r.Elapsed); };
        Closing += (_, _) => _rec?.Stop();
        var tool = Tool();
        _status.Text = tool == null ? MissingText() : $"Saved in Videos/Recordings with {Path.GetFileName(tool)}.";
        _go.IsEnabled = tool != null;
    }

    private static string? Tool() => InputIsolation.InRedStarComp ? ProcessUtil.Which("wf-recorder") : ProcessUtil.Which("ffmpeg");

    private static string MissingText() => InputIsolation.InRedStarComp
        ? "Recording inside redstar-comp needs wf-recorder (Arch/CachyOS: pacman -S wf-recorder · Debian/Ubuntu: apt install wf-recorder)."
        : "Recording on X11 needs FFmpeg (pacman -S ffmpeg · apt install ffmpeg).";

    /// <summary>The program and its arguments (public for the tests). <paramref name="area"/>: x, y, width, height in
    /// screen coordinates; null for everything.</summary>
    public static (string Exe, List<string> Args) Command(bool wayland, string exe, string file, PixelRect? area, PixelRect screen, bool mic, string display)
    {
        var a = new List<string>();
        if (wayland)
        {
            if (area is { } r) { a.Add("-g"); a.Add($"{r.X},{r.Y} {r.Width}x{r.Height}"); }
            if (mic) a.Add("--audio");
            a.Add("-f"); a.Add(file);
            return (exe, a);
        }
        var g = area ?? screen;
        int w = g.Width / 2 * 2, h = g.Height / 2 * 2;       // H.264 wants even sizes
        a.AddRange(new[] { "-hide_banner", "-loglevel", "warning", "-y", "-f", "x11grab", "-framerate", "30", "-video_size", $"{w}x{h}",
                           "-i", $"{display}+{g.X},{g.Y}" });
        if (mic) a.AddRange(new[] { "-f", "pulse", "-i", "default" });
        a.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p" });
        if (mic) a.AddRange(new[] { "-c:a", "aac", "-b:a", "128k" });
        a.Add(file);
        return (exe, a);
    }

    private async Task Start()
    {
        var exe = Tool();
        if (exe == null) { _status.Text = MissingText(); return; }
        bool wayland = InputIsolation.InRedStarComp;
        var screen = Screens.All.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b));
        PixelRect? area = null;
        if (_mode.SelectedIndex == 1)
        {
            Hide();
            await Task.Delay(400);
            var temp = Path.Combine(Path.GetTempPath(), $"redstar-rec-{Guid.NewGuid():N}.png");
            var error = await Screenshots.Capture(temp);
            if (error != null) { Show(); _status.Text = error; return; }
            using (var shot = new Bitmap(temp))
            {
                var picker = new AreaPickerWindow(shot);
                picker.Show();
                var r = await picker.Result;
                if (r is { } px)
                {
                    // the picture's pixels → screen coordinates (Wayland's are logical: the picture is at the output's scale)
                    double s = wayland ? shot.PixelSize.Width / (double)Math.Max(1, ScreenLogicalWidth()) : 1;
                    area = new PixelRect((int)(px.X / s), (int)(px.Y / s), Math.Max(2, (int)(px.Width / s)), Math.Max(2, (int)(px.Height / s)));
                }
            }
            try { File.Delete(temp); } catch { }
            Show();
            if (area == null) { _status.Text = "Cancelled."; return; }
        }
        var file = Path.Combine(Folder, $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        var (cmd, args) = Command(wayland, exe, file, area, screen, _mic.IsChecked == true, Environment.GetEnvironmentVariable("DISPLAY") ?? ":0");
        var rec = new Recorder(file, "recorder.log");
        rec.Ended += code => Dispatcher.UIThread.Post(() => Finished(rec, code));
        if (!rec.Start(cmd, args)) { _status.Text = "Couldn't start " + Path.GetFileName(cmd) + "."; return; }
        _rec = rec;
        _go.Content = "Stop recording";
        _status.Text = "Recording… this window can be minimised; open it again from the taskbar to stop.";
        _after.IsVisible = false;
        _mode.IsEnabled = _mic.IsEnabled = false;
        _tick.Start();
    }

    private int ScreenLogicalWidth()
    {
        var all = Screens.All;
        if (all.Count == 0) return 1;
        return all.Max(s => (int)Math.Round((s.Bounds.X + s.Bounds.Width) / s.Scaling));
    }

    private void Stop()
    {
        _status.Text = "Finishing the file…";
        _rec?.Stop();
    }

    private void Finished(Recorder rec, int code)
    {
        _tick.Stop();
        _go.Content = "Start recording";
        _mode.IsEnabled = _mic.IsEnabled = true;
        var ok = File.Exists(rec.File) && new FileInfo(rec.File).Length > 0;
        if (ok)
        {
            _last = rec.File;
            _status.Text = $"Saved {Path.GetFileName(rec.File)} ({Recorder.Format(DateTime.Now - rec.Started)}).";
            _after.IsVisible = true;
            Notifier.Show("Recording saved", Path.GetFileName(rec.File), "recorder",
                new NotificationAction("Play", () => FileOpener.Open(rec.File)));
        }
        else _status.Text = $"The recording didn't work (exit code {code.ToString(CultureInfo.InvariantCulture)}); details in ~/.config/redstaros/recorder.log.";
        _time.Text = "0:00";
    }
}
