using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// Camera app using ffmpeg and Video4Linux devices (/dev/video*).
/// Needs the 'ffmpeg' command; without it the window shows the "No Camera Detected" picture.
/// </summary>
public partial class CameraWindow : Window
{
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly string _tempFrame = Path.Combine(Path.GetTempPath(), $"redstar-cam-{Environment.ProcessId}.jpg");
    private string? _ffmpeg;
    private bool _busy;
    private Process? _recorder;
    private readonly DispatcherTimer _recTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _recStart;
    private bool _counting;
    private const string RecTrayKey = "camera:recording";

    public CameraWindow()
    {
        InitializeComponent();
        _ffmpeg = ProcessUtil.Which("ffmpeg");

        string[] devices;
        try { devices = Directory.GetFiles("/dev", "video*").OrderBy(d => d, StringComparer.Ordinal).ToArray(); }
        catch { devices = Array.Empty<string>(); }
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedIndex = devices.Length > 0 ? 0 : -1;

        if (_ffmpeg == null)
            SetUnavailable("The camera needs ffmpeg",
                "Install the ffmpeg package from Software (or run 'sudo pacman -S ffmpeg'), then open Camera again.");
        else if (devices.Length == 0)
            SetUnavailable("No camera found",
                "Plug in a webcam, or check that the built-in one is switched on, then open Camera again.");
        else
            StatusLabel.Text = "Camera ready.";

        TimerBox.ItemsSource = new[] { "Off", "3 s", "10 s" };
        TimerBox.SelectedIndex = 0;

        _previewTimer.Tick += async (_, _) => await GrabPreview();
        _recTimer.Tick += (_, _) => RecTime.Text = (DateTime.Now - _recStart).ToString(@"mm\:ss");
        Closing += (_, _) => Cleanup();
        RefreshStrip();
    }

    // ------------------------------------------------------------ recent photos

    private static string PicturesDir => Path.Combine(AppPaths.Home, "Pictures");

    private void RefreshStrip()
    {
        Strip.Children.Clear();
        StripEmpty.IsVisible = true;
        try
        {
            if (!Directory.Exists(PicturesDir)) return;
            var files = new DirectoryInfo(PicturesDir).GetFiles("RedStar_*.jpg")
                .OrderByDescending(f => f.LastWriteTime).Take(12);
            foreach (var f in files)
            {
                var path = f.FullName;
                var bmp = LoadThumb(path);
                if (bmp == null) continue;
                var btn = new Button { Padding = new Avalonia.Thickness(0), Content = new Image { Source = bmp, Width = 120, Height = 84, Stretch = Avalonia.Media.Stretch.UniformToFill } };
                btn.Classes.Add("flat");
                ToolTip.SetTip(btn, f.Name);
                btn.Click += (_, _) => FileOpener.Open(path);
                Strip.Children.Add(btn);
                StripEmpty.IsVisible = false;
            }
        }
        catch { /* the strip is optional */ }
    }

    private static Bitmap? LoadThumb(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, 240);
        }
        catch { return null; }
    }

    private void SetUnavailable(string title, string message)
    {
        UnavailableTitle.Text = title;
        UnavailableText.Text = message;
        Unavailable.IsVisible = true;
        StatusLabel.Text = "";
        PictureButton.IsEnabled = false;
        PreviewToggle.IsEnabled = false;
        RecordToggle.IsEnabled = false;
        DeviceBox.IsEnabled = false;
    }

    private string? Device => DeviceBox.SelectedItem as string;

    private async Task<bool> Capture(string outputPath)
    {
        if (_ffmpeg == null || Device == null) return false;
        var args = new[] { "-loglevel", "error", "-y", "-f", "v4l2", "-i", Device, "-frames:v", "1", outputPath };
        var (code, output) = await ProcessUtil.RunAsync(_ffmpeg, args, 8000);
        if (code != 0)
        {
            StatusLabel.Text = "The camera didn't take the picture: " + (output.Length > 0 ? output.Trim() : "ffmpeg stopped without saying why.");
            return false;
        }
        return File.Exists(outputPath);
    }

    private void ShowFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var bmp = new Bitmap(fs);
            var old = Preview.Source as IDisposable;
            Preview.Source = bmp;
            old?.Dispose();
        }
        catch { /* half-written frame: keep the previous one */ }
    }

    private async Task GrabPreview()
    {
        if (_busy || _recorder != null) return;
        _busy = true;
        try
        {
            if (await Capture(_tempFrame)) ShowFile(_tempFrame);
            else { PreviewToggle.IsChecked = false; _previewTimer.Stop(); }
        }
        finally { _busy = false; }
    }

    private async void OnPicture(object? s, RoutedEventArgs e)
    {
        if (_busy || _recorder != null || _counting) return;
        _busy = true;
        PictureButton.IsEnabled = false;
        try
        {
            await Countdown();
            var dir = PicturesDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"RedStar_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            if (await Capture(file))
            {
                ShowFile(file);
                StatusLabel.Text = "Saved " + file;
                Notifier.Show("Camera", "Picture saved: " + Path.GetFileName(file), "camera",
                    new NotificationAction("Open", () => FileOpener.Open(file)),
                    new NotificationAction("Share…", () => ShareService.ShareFile(file)));
                RefreshStrip();
            }
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally { _busy = false; PictureButton.IsEnabled = true; }
    }

    private async Task Countdown()
    {
        var secs = TimerBox.SelectedIndex switch { 1 => 3, 2 => 10, _ => 0 };
        if (secs == 0) return;
        _counting = true;
        for (int i = secs; i > 0; i--)
        {
            CountdownLabel.Text = i.ToString();
            CountdownLabel.IsVisible = true;
            await Task.Delay(1000);
        }
        CountdownLabel.IsVisible = false;
        _counting = false;
    }

    private void OnPreviewToggle(object? s, RoutedEventArgs e)
    {
        if (PreviewToggle.IsChecked == true) { _previewTimer.Start(); _ = GrabPreview(); }
        else _previewTimer.Stop();
    }

    private void OnRecordToggle(object? s, RoutedEventArgs e)
    {
        if (RecordToggle.IsChecked == true) StartRecording();
        else StopRecording();
    }

    private void StartRecording()
    {
        if (_ffmpeg == null || Device == null) { RecordToggle.IsChecked = false; return; }
        _previewTimer.Stop();
        PreviewToggle.IsChecked = false;
        try
        {
            var dir = Path.Combine(AppPaths.Home, "Videos");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"RedStar_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            var psi = new ProcessStartInfo(_ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-loglevel", "error", "-y", "-f", "v4l2", "-i", Device, file }) psi.ArgumentList.Add(a);
            _recorder = Process.Start(psi);
            StatusLabel.Text = "Recording to " + file;
            _recStart = DateTime.Now;
            RecTime.Text = "00:00";
            RecBadge.IsVisible = true;
            _recTimer.Start();
            TrayService.Upsert(new TrayEntry
            {
                Key = RecTrayKey, Glyph = "🔴", Tooltip = "Camera is recording",
                Menu = new() { new TrayMenuEntry { Id = "stop", Label = "Stop recording" }, new TrayMenuEntry { Id = "show", Label = "Show Camera" } },
                MenuSelected = id => { if (id == "stop") { RecordToggle.IsChecked = false; StopRecording(); } else Activate(); }
            });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Recording didn't start: " + ex.Message;
            RecordToggle.IsChecked = false;
            _recorder = null;
        }
    }

    private void StopRecording()
    {
        var p = _recorder;
        _recorder = null;
        _recTimer.Stop();
        RecBadge.IsVisible = false;
        TrayService.Remove(RecTrayKey);
        if (p == null) return;
        try
        {
            p.StandardInput.Write("q");       // ffmpeg finishes the file cleanly on 'q'
            p.StandardInput.Flush();
            if (!p.WaitForExit(4000)) p.Kill(true);
            StatusLabel.Text = "Recording saved in ~/Videos.";
            Notifier.Show("Camera", "Video saved in ~/Videos", "camera");
        }
        catch
        {
            try { p.Kill(true); } catch { }
        }
        p.Dispose();
    }

    private void Cleanup()
    {
        _previewTimer.Stop();
        StopRecording();
        try { if (File.Exists(_tempFrame)) File.Delete(_tempFrame); } catch { }
    }
}
