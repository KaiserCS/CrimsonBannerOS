using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;
using SkiaSharp;
using Avalonia.Input.Platform;

namespace RedStarOS.Views;

/// <summary>A thumbnail in the strip; the picture arrives from a background loader.</summary>
public sealed class ThumbItem : INotifyPropertyChanged
{
    private Bitmap? _thumb;
    public string Path { get; set; } = "";
    public Bitmap? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Image viewer: browses the whole folder (arrow keys, thumbnail strip), fit (large pictures shrunk to the window) or
/// zoom to fit (every picture filling it, small ones enlarged), actual size, wheel zoom, drag or Ctrl+arrows to pan, rotate and flip (honours EXIF orientation), slideshow, full screen, rename, trash, wallpaper, Paint.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private const double FitCap = 1.0;          // "fit" never enlarges small pictures; "zoom to fit" does

    private readonly List<string> _files = new();
    private readonly ObservableCollection<ThumbItem> _thumbs = new();
    private int _index = -1;
    private string? _path;
    private Bitmap? _bitmap;
    private int _rot;                           // 0, 90, 180, 270 (clockwise)
    private bool _flipH, _flipV;                // applied to the picture before rotating
    private double _zoom = 1;
    private bool _fit = true;
    private bool _fill = SettingsStore.Current.ImageZoomToFit;   // fitting enlarges small pictures too (kept)

    /// <summary>The zoom shown (1 = actual size), and where the picture is scrolled to (for the tests).</summary>
    public double Zoom => _zoom;
    public Vector ScrollOffset => Scroller.Offset;
    private bool _syncing;
    private int _gen;
    private DispatcherTimer? _slide;
    private bool _full;
    private bool _stripWanted = true;
    private WindowState _stateBeforeFull;
    private bool _panning;
    private Point _panStart;
    private Vector _panOffset;

    public ImageViewerWindow()
    {
        InitializeComponent();
        ThumbList.ItemsSource = _thumbs;
        ThumbList.SelectionChanged += (_, _) =>
        {
            if (_syncing || ThumbList.SelectedIndex < 0 || ThumbList.SelectedIndex == _index) return;
            _ = GoTo(ThumbList.SelectedIndex);
        };
        StripHost.IsVisible = false;

        Scroller.SizeChanged += (_, _) => { if (_fit) ApplyView(); };
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        Picture.PointerPressed += OnPicDown;
        Picture.PointerMoved += OnPicMove;
        Picture.PointerReleased += (_, e) => { _panning = false; e.Pointer.Capture(null); };

        DragDrop.SetAllowDrop(Stage, true);
        Stage.AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);   // before the thumbnail strip or scroller take the arrows
        Closed += (_, _) => { StopSlideshow(); _gen++; _bitmap?.Dispose(); };
        UpdateStatus();
    }

    // ------------------------------------------------------------ opening

    /// <summary>Shows an image from disk and lets you browse the other pictures in its folder (File Manager, desktop icons).</summary>
    public void LoadFile(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(path) ?? "";
            _files.Clear();
            _files.AddRange(WallpaperService.ImagesIn(folder, 500));
            if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase)) _files.Add(path);
            _index = _files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            BuildStrip();
            if (!Show(_files[_index]))
                _ = Dialogs.ShowMessage(this, "Image Viewer", "Could not open that image.");
        }
        catch (Exception ex)
        {
            _ = Dialogs.ShowMessage(this, "Image Viewer", "Could not open that image: " + ex.Message);
        }
    }

    private bool Show(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            Bitmap bmp;
            using (var ms = new MemoryStream(data)) bmp = new Bitmap(ms);
            var old = _bitmap;
            _bitmap = bmp;
            _path = path;
            ApplyOrigin(ReadOrigin(data));
            _fit = true;
            Picture.Source = bmp;
            old?.Dispose();
            Hint.IsVisible = false;
            Title = Path.GetFileName(path) + " - Image Viewer";
            ApplyView();
            SyncStrip();
            UpdateStatus();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task GoTo(int index)
    {
        if (_files.Count == 0) return;
        index = ((index % _files.Count) + _files.Count) % _files.Count;
        _index = index;
        if (!Show(_files[index]))
        {
            // unreadable: drop it from the list and move on (once)
            _files.RemoveAt(index);
            if (index < _thumbs.Count) _thumbs.RemoveAt(index);
            if (_files.Count == 0) { Clear(); return; }
            _index = Math.Min(index, _files.Count - 1);
            if (!Show(_files[_index])) Clear();
        }
        await Task.CompletedTask;
    }

    private void Go(int delta) { if (_files.Count > 1) _ = GoTo(_index + delta); }

    private void Clear()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _path = null;
        Picture.Source = null;
        Hint.IsVisible = true;
        _index = -1;
        StopSlideshow();
        Title = "Image Viewer";
        UpdateStatus();
    }

    private async Task OpenDialog()
    {
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        { Title = "Open Image", AllowMultiple = false, FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } });
        var local = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (local != null) LoadFile(local);
    }

#pragma warning disable CS0618
    private void OnDrop(object? sender, DragEventArgs e)
    {
        var item = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        var local = item?.TryGetLocalPath();
        if (local != null && FileOps.ImageExtensions.Contains(Path.GetExtension(local).ToLowerInvariant())) LoadFile(local);
    }
#pragma warning restore CS0618

    // ------------------------------------------------------------ orientation (EXIF aware)

    private static int ReadOrigin(byte[] data)
    {
        try
        {
            using var sd = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(sd);
            return codec == null ? 1 : (int)codec.EncodedOrigin;
        }
        catch { return 1; }
    }

    private void ApplyOrigin(int origin)
    {
        (_rot, _flipH, _flipV) = origin switch
        {
            2 => (0, true, false),
            3 => (180, false, false),
            4 => (0, false, true),
            5 => (270, true, false),
            6 => (90, false, false),
            7 => (90, true, false),
            8 => (270, false, false),
            _ => (0, false, false)
        };
    }

    // ------------------------------------------------------------ view

    private bool Sideways => _rot % 180 != 0;

    private void ApplyView()
    {
        if (_bitmap == null) { UpdateStatus(); return; }
        double w = _bitmap.PixelSize.Width, h = _bitmap.PixelSize.Height;
        double ew = Sideways ? h : w, eh = Sideways ? w : h;
        if (_fit)
        {
            double vw = Math.Max(50, Scroller.Bounds.Width - 4), vh = Math.Max(50, Scroller.Bounds.Height - 4);
            _zoom = Math.Min(vw / ew, vh / eh);
            if (!_fill) _zoom = Math.Min(FitCap, _zoom);
        }
        Picture.Width = Math.Max(1, w * _zoom);
        Picture.Height = Math.Max(1, h * _zoom);
        Holder.LayoutTransform = new TransformGroup
        {
            Children = { new ScaleTransform(_flipH ? -1 : 1, _flipV ? -1 : 1), new RotateTransform(_rot) }
        };
        RenderOptions.SetBitmapInterpolationMode(Picture, _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        UpdateStatus();
    }

    private void SetZoom(double z, Point? anchor)
    {
        if (_bitmap == null) return;
        z = Math.Clamp(z, 0.02, 32);
        var a = anchor ?? new Point(Scroller.Bounds.Width / 2, Scroller.Bounds.Height / 2);
        double old = _zoom;
        double ix = (Scroller.Offset.X + a.X) / old, iy = (Scroller.Offset.Y + a.Y) / old;
        _fit = false;
        _zoom = z;
        ApplyView();
        Dispatcher.UIThread.Post(() => Scroller.Offset = new Vector(Math.Max(0, ix * z - a.X), Math.Max(0, iy * z - a.Y)), DispatcherPriority.Background);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_bitmap == null) return;
        SetZoom(e.Delta.Y > 0 ? _zoom * 1.15 : _zoom / 1.15, e.GetPosition(Scroller));
        e.Handled = true;
    }

    private void OnPicDown(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Picture).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            if (_fit) SetZoom(1, e.GetPosition(Scroller)); else { _fit = true; ApplyView(); }
            return;
        }
        _panning = true;
        _panStart = e.GetPosition(Scroller);
        _panOffset = Scroller.Offset;
        e.Pointer.Capture(Picture);
    }

    private void OnPicMove(object? sender, PointerEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(Scroller);
        Scroller.Offset = new Vector(Math.Max(0, _panOffset.X - (p.X - _panStart.X)), Math.Max(0, _panOffset.Y - (p.Y - _panStart.Y)));
    }

    /// <summary>Moves a zoomed picture by a share of the window (Ctrl+arrows); nothing when it all fits.</summary>
    public void Pan(double dx, double dy)
    {
        var max = new Vector(Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width), Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        var o = Scroller.Offset + new Vector(dx * Scroller.Viewport.Width, dy * Scroller.Viewport.Height);
        Scroller.Offset = new Vector(Math.Clamp(o.X, 0, max.X), Math.Clamp(o.Y, 0, max.Y));
    }

    private void SetFit(bool fill)
    {
        _fit = true;
        _fill = fill;
        SettingsStore.Current.ImageZoomToFit = fill;
        SettingsStore.Save();
        ApplyView();
    }

    private void Rotate(int degrees)
    {
        _rot = ((_rot + degrees) % 360 + 360) % 360;
        ApplyView();
    }

    private void FlipHorizontal() { if (Sideways) _flipV = !_flipV; else _flipH = !_flipH; ApplyView(); }
    private void FlipVertical() { if (Sideways) _flipH = !_flipH; else _flipV = !_flipV; ApplyView(); }

    private void UpdateStatus()
    {
        ZoomText.Text = _bitmap == null ? "" : $"{Math.Round(_zoom * 100)}%";
        if (_bitmap == null || _path == null) { StatusText.Text = "No image"; return; }
        long size = 0;
        DateTime when = default;
        try { var fi = new FileInfo(_path); size = fi.Length; when = fi.LastWriteTime; } catch { }
        var sizeText = size >= 1_048_576 ? $"{size / 1_048_576.0:0.#} MB" : $"{Math.Max(1, size / 1024)} KB";
        StatusText.Text = $"{Path.GetFileName(_path)}   •   {_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height} px   •   {sizeText}   •   {when:d MMM yyyy HH:mm}" +
                          (_files.Count > 1 ? $"   •   {_index + 1} of {_files.Count}" : "");
    }

    // ------------------------------------------------------------ thumbnail strip

    private void BuildStrip()
    {
        int gen = ++_gen;
        _thumbs.Clear();
        foreach (var f in _files) _thumbs.Add(new ThumbItem { Path = f });
        StripHost.IsVisible = _files.Count > 1 && !_full && _stripWanted;
        var items = _thumbs.ToList();
        Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (gen != _gen) return;
                try
                {
                    using var fs = File.OpenRead(item.Path);
                    var bmp = Bitmap.DecodeToWidth(fs, 140, BitmapInterpolationMode.MediumQuality);
                    Dispatcher.UIThread.Post(() => { if (gen == _gen) item.Thumb = bmp; else bmp.Dispose(); });
                }
                catch { }
            }
        });
    }

    private void SyncStrip()
    {
        _syncing = true;
        if (_index >= 0 && _index < _thumbs.Count)
        {
            ThumbList.SelectedIndex = _index;
            ThumbList.ScrollIntoView(_index);
        }
        _syncing = false;
    }

    // ------------------------------------------------------------ slideshow / full screen

    private void ToggleSlideshow()
    {
        if (_slide != null) { StopSlideshow(); return; }
        if (_files.Count < 2) return;
        _slide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _slide.Tick += (_, _) => Go(1);
        _slide.Start();
        SlideBtn.Content = "⏸ Stop";
    }

    private void StopSlideshow()
    {
        _slide?.Stop();
        _slide = null;
        SlideBtn.Content = "▶ Slideshow";
    }

    private void ToggleFullScreen()
    {
        _full = !_full;
        if (_full) { _stateBeforeFull = WindowState; WindowState = WindowState.FullScreen; }
        else WindowState = _stateBeforeFull == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFull;
        Toolbar.IsVisible = !_full;
        StatusBar.IsVisible = !_full;
        StripHost.IsVisible = !_full && _files.Count > 1 && _stripWanted;
        if (_fit) ApplyView();
    }

        // ------------------------------------------------------------ commands

    private async void OnCmd(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag) await Run(tag);
    }

    private async Task Run(string cmd)
    {
        switch (cmd)
        {
            case "open": await OpenDialog(); break;
            case "prev": Go(-1); break;
            case "next": Go(1); break;
            case "rotl": Rotate(-90); break;
            case "rotr": Rotate(90); break;
            case "fliph": FlipHorizontal(); break;
            case "flipv": FlipVertical(); break;
            case "fit": SetFit(fill: false); break;
            case "zoomfit": SetFit(fill: true); break;
            case "actual": SetZoom(1, null); break;
            case "zoomin": SetZoom(_zoom * 1.25, null); break;
            case "zoomout": SetZoom(_zoom / 1.25, null); break;
            case "slideshow": ToggleSlideshow(); break;
            case "fullscreen": ToggleFullScreen(); break;
            case "thumbs":
                _stripWanted = !_stripWanted;
                StripHost.IsVisible = _stripWanted && _files.Count > 1 && !_full;
                break;
            case "paint":
                if (_path != null)
                {
                    var paint = new PaintWindow();
                    WindowManager.Open(paint, "paint");
                    _ = paint.OpenPath(_path);
                }
                break;
            case "rename": await RenameCurrent(); break;
            case "delete": await TrashCurrent(); break;
            case "wallpaper":
                if (_path != null) { WallpaperService.Set(_path); Notifier.Show("Image Viewer", "Wallpaper changed", "imageviewer"); }
                break;
            case "copypath":
                if (_path != null && Clipboard != null) await Clipboard.SetTextAsync(_path);
                break;
            case "saverot": await SaveTransformed(); break;
            case "share": if (_path != null) ShareService.ShowMenu(this, System.IO.Path.GetFileName(_path), file: _path); break;
        }
    }

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // Ctrl+arrows scroll a zoomed picture (a fifth of the window; with Shift, most of it)
        if (ctrl && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = shift ? 0.8 : 0.2;
            Pan(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
            e.Handled = true;
            return;
        }
        string? cmd = e.Key switch
        {
            Key.O when ctrl => "open",
            Key.Left or Key.PageUp => "prev",
            Key.Right or Key.PageDown or Key.Space => "next",
            Key.R when shift => "rotl",
            Key.R => "rotr",
            Key.L => "rotl",
            Key.H => "fliph",
            Key.V => "flipv",
            Key.F when shift => "zoomfit",
            Key.F or Key.D9 => "fit",
            Key.D0 or Key.NumPad0 => "actual",
            Key.OemPlus or Key.Add => "zoomin",
            Key.OemMinus or Key.Subtract => "zoomout",
            Key.S => "slideshow",
            Key.T => "thumbs",
            Key.E => "paint",
            Key.F2 => "rename",
            Key.Delete => "delete",
            Key.F11 => "fullscreen",
            _ => null
        };
        if (e.Key == Key.Home && _files.Count > 0) { _ = GoTo(0); e.Handled = true; return; }
        if (e.Key == Key.End && _files.Count > 0) { _ = GoTo(_files.Count - 1); e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            if (_slide != null) StopSlideshow();
            else if (_full) ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (cmd == null) return;
        e.Handled = true;
        await Run(cmd);
    }

    // ------------------------------------------------------------ file actions

    private async Task RenameCurrent()
    {
        if (_path == null) return;
        var name = await Dialogs.Prompt(this, "Rename", "New file name:", Path.GetFileName(_path));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/'))
        { await Dialogs.ShowMessage(this, "Rename", "That name has characters a file name can't contain."); return; }
        var target = Path.Combine(Path.GetDirectoryName(_path)!, name);
        if (File.Exists(target) && !string.Equals(target, _path, StringComparison.Ordinal))
        { await Dialogs.ShowMessage(this, "Rename", "A file with that name already exists."); return; }
        try
        {
            File.Move(_path, target);
            _path = target;
            if (_index >= 0 && _index < _files.Count) { _files[_index] = target; _thumbs[_index].Path = target; }
            Title = name + " - Image Viewer";
            UpdateStatus();
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Rename", "Could not rename: " + ex.Message); }
    }

    private async Task TrashCurrent()
    {
        if (_path == null) return;
        int r = await Dialogs.Ask(this, "Move to Trash", $"Move “{Path.GetFileName(_path)}” to the Trash?", "Move to Trash", "Cancel");
        if (r != 0) return;
        try
        {
            FileOps.MoveToTrash(_path);
            int i = _index;
            _files.RemoveAt(i);
            if (i < _thumbs.Count) _thumbs.RemoveAt(i);
            if (_files.Count == 0) { Clear(); return; }
            await GoTo(Math.Min(i, _files.Count - 1));
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Image Viewer", "Could not move the file to the Trash: " + ex.Message); }
    }

    /// <summary>Bakes the current rotation/flip into the file (PNG/JPEG/BMP in place; other formats become a new PNG).</summary>
    private async Task SaveTransformed()
    {
        if (_path == null) return;
        try
        {
            using var src = SKBitmap.Decode(_path);
            if (src == null) throw new InvalidOperationException("The picture could not be decoded.");
            int w = src.Width, h = src.Height;
            int nw = Sideways ? h : w, nh = Sideways ? w : h;
            using var dst = new SKBitmap(new SKImageInfo(nw, nh, SKColorType.Bgra8888, SKAlphaType.Premul));
            dst.Erase(SKColors.Transparent);
            using (var c = new SKCanvas(dst))
            {
                c.Translate(nw / 2f, nh / 2f);
                c.RotateDegrees(_rot);
                c.Scale(_flipH ? -1 : 1, _flipV ? -1 : 1);
                c.Translate(-w / 2f, -h / 2f);
                c.DrawBitmap(src, 0, 0);
            }
            var ext = Path.GetExtension(_path).ToLowerInvariant();
            var keep = ext is ".png" or ".jpg" or ".jpeg" or ".bmp";
            var target = keep ? _path : Path.ChangeExtension(_path, null) + "_rotated.png";
            var bytes = PaintDoc.Encode(dst, keep ? ext : ".png");
            await File.WriteAllBytesAsync(target, bytes);
            Notifier.Show("Image Viewer", "Saved " + Path.GetFileName(target), "imageviewer");
            LoadFile(target);
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Image Viewer", "Could not save the picture: " + ex.Message); }
    }
}
