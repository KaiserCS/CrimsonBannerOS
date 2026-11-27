using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;
using SkiaSharp;

namespace RedStarOS.Views;

public enum PaintTool { Pencil, Fill, Text, Eraser, Picker, Zoom, Brush, Select, Free, Shape }
public enum BrushKind { Round, Square, Calligraphy, Airbrush, Marker, Crayon }

/// <summary>
/// Red Star Paint: a raster editor modelled on Microsoft Paint — pencil, brushes, eraser, fill, colour picker, text,
/// shapes, rectangular and free-form selections, resizable canvas, zoom, undo/redo, open/save PNG/JPEG/BMP.
/// This file holds the window plumbing; the drawing tools live in PaintWindow.Tools.cs.
/// </summary>
public partial class PaintWindow : Window
{
    private static readonly string[] Row1 =
        { "000000", "7F7F7F", "880015", "ED1C24", "FF7F27", "FFF200", "22B14C", "00A2E8", "3F48CC", "A349A4", "B97A57", "C8BFE7", "FFAEC9", "99D9EA" };
    private static readonly string[] Row2 =
        { "FFFFFF", "C3C3C3", "585858", "FFC90E", "EFE4B0", "B5E61D", "7092BE", "9C5A3C", "6B46C1", "2F855A", "FFFFFF", "FFFFFF", "FFFFFF", "FFFFFF" };
    private const int FirstCustomSlot = 10;

    private PaintDoc _doc = new(800, 600);
    private SKColor _c1 = SKColors.Black;
    private SKColor _c2 = SKColors.White;
    private PaintTool _tool = PaintTool.Pencil;
    private PaintTool _prevTool = PaintTool.Pencil;
    private double _zoom = 1;
    private string? _path;
    private bool _modified;
    private bool _forceClose;
    private bool _zoomBusy;
    private SKBitmap? _clip;
    private int _nextCustom;
    private readonly Dictionary<PaintTool, ToggleButton> _toolButtons = new();
    private readonly List<Border> _swatches = new();
    private readonly Dictionary<StandardCursorType, Cursor> _cursors = new();
    private bool _refreshQueued;

    public PaintWindow()
    {
        InitializeComponent();
        Surface.Doc = _doc;
        BuildToolButtons();
        BuildPalette();

        BrushBox.ItemsSource = new[] { "Round brush", "Square brush", "Calligraphy", "Airbrush", "Marker", "Crayon" };
        BrushBox.SelectedIndex = 0;
        BrushBox.SelectionChanged += (_, _) => { if (IsLoaded) SetTool(PaintTool.Brush); };
        ShapeBox.ItemsSource = PaintShapes.All.Select(s => s.Name).ToArray();
        ShapeBox.SelectedIndex = 2;
        ShapeBox.SelectionChanged += (_, _) => { if (IsLoaded) { FinishShape(); SetTool(PaintTool.Shape); } };
        FillBox.ItemsSource = new[] { "Outline", "Outline + fill", "Solid fill" };
        FillBox.SelectedIndex = 0;

        var fonts = SKFontManager.Default.GetFontFamilies().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (fonts.Length == 0) fonts = new[] { "Sans" };
        FontBox.ItemsSource = fonts;
        var preferred = new[] { "DejaVu Sans", "Liberation Sans", "Noto Sans", "Ubuntu", "Arial" };
        FontBox.SelectedItem = preferred.FirstOrDefault(p => fonts.Contains(p)) ?? fonts[0];
        FontSizeBox.ItemsSource = new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72 };
        FontSizeBox.SelectedItem = 14;
        FontBox.SelectionChanged += (_, _) => ApplyTextStyle();
        FontSizeBox.SelectionChanged += (_, _) => ApplyTextStyle();
        BoldBtn.IsCheckedChanged += (_, _) => ApplyTextStyle();
        ItalicBtn.IsCheckedChanged += (_, _) => ApplyTextStyle();

        SizeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) SizeText.Text = $"Size: {(int)SizeSlider.Value} px";
        };
        ZoomSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_zoomBusy) SetZoom(Math.Pow(2, ZoomSlider.Value), null);
        };

        Sw1.PointerPressed += async (_, _) => { var c = await PaintDialogs.EditColor(this, _c1); if (c is SKColor k) { _c1 = k; UpdateSwatches(); } };
        Sw2.PointerPressed += async (_, _) => { var c = await PaintDialogs.EditColor(this, _c2); if (c is SKColor k) { _c2 = k; UpdateSwatches(); } };

        Surface.PointerPressed += OnDown;
        Surface.PointerMoved += OnMove;
        Surface.PointerReleased += OnUp;
        Surface.PointerCaptureLost += OnCaptureLost;
        Surface.PointerExited += (_, _) => PosText.Text = "";
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        KeyDown += OnKey;
        Closing += OnClosing;

        UpdateSwatches();
        SetTool(PaintTool.Pencil);
        Surface.Refresh();
        UpdateStatus();
        UpdateTitle();
    }

    // ------------------------------------------------------------ setup helpers

    private void BuildToolButtons()
    {
        var defs = new (PaintTool T, string Label, string Tip)[]
        {
            (PaintTool.Pencil, "edit|Pencil", "Pencil (1 px)"), (PaintTool.Fill, "fill|Fill", "Fill with colour"),
            (PaintTool.Text, "text-box|Text", "Text"), (PaintTool.Eraser, "clear-format|Erase", "Eraser"),
            (PaintTool.Picker, "palette|Pick", "Colour picker"), (PaintTool.Zoom, "zoom-in|Zoom", "Magnifier (left click in, right click out)"),
            (PaintTool.Brush, "brush|Brush", "Brushes"), (PaintTool.Select, "outline|Select", "Rectangle select"),
            (PaintTool.Free, "cut|Free", "Free-form select"), (PaintTool.Shape, "shape|Shape", "Shapes")
        };
        foreach (var d in defs)
        {
            var parts = d.Label.Split('|');
            var b = new ToggleButton { Content = Icons.Label(parts[0], parts[1], 14), Margin = new Thickness(1), Padding = new Thickness(6, 3), FontSize = 12, MinWidth = 66 };
            ToolTip.SetTip(b, d.Tip);
            var tool = d.T;
            b.Click += (_, _) => SetTool(tool);
            _toolButtons[tool] = b;
            ToolsPanel.Children.Add(b);
        }
    }

    private void BuildPalette()
    {
        int index = 0;
        foreach (var hex in Row1.Concat(Row2))
        {
            var color = SKColor.Parse(hex);
            var sw = new Border
            {
                Width = 20, Height = 20, Margin = new Thickness(1), CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#66808080")),
                Background = new SolidColorBrush(ToAv(color)), Tag = color
            };
            sw.PointerPressed += (s, e) =>
            {
                var b = (Border)s!;
                var c = (SKColor)b.Tag!;
                var props = e.GetCurrentPoint(b).Properties;
                if (props.IsRightButtonPressed) _c2 = c; else _c1 = c;
                UpdateSwatches();
            };
            _swatches.Add(sw);
            Palette.Children.Add(sw);
            index++;
        }
    }

    private static Color ToAv(SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);

    private void UpdateSwatches()
    {
        Sw1.Background = new SolidColorBrush(ToAv(_c1));
        Sw2.Background = new SolidColorBrush(ToAv(_c2));
        if (_textBox != null) _textBox.Foreground = new SolidColorBrush(ToAv(_c1));
    }

    private void OnSwapColors(object? s, RoutedEventArgs e) { (_c1, _c2) = (_c2, _c1); UpdateSwatches(); }

    private async void OnEditColors(object? s, RoutedEventArgs e)
    {
        var c = await PaintDialogs.EditColor(this, _c1);
        if (c is not SKColor k) return;
        _c1 = k;
        // remember it in one of the custom palette slots
        int slot = Row1.Length + FirstCustomSlot + (_nextCustom++ % (Row2.Length - FirstCustomSlot));
        _swatches[slot].Tag = k;
        _swatches[slot].Background = new SolidColorBrush(ToAv(k));
        UpdateSwatches();
    }

    private void OnTransparentChanged(object? s, RoutedEventArgs e)
    {
        RefreshFloat();
    }

    private Cursor CursorOf(StandardCursorType t)
    {
        if (!_cursors.TryGetValue(t, out var c)) _cursors[t] = c = new Cursor(t);
        return c;
    }

    private void SetTool(PaintTool t)
    {
        CommitText();
        FinishShape();
        if (t != PaintTool.Select && t != PaintTool.Free) CommitSelection();
        if (t != _tool && _tool != PaintTool.Picker && _tool != PaintTool.Zoom) _prevTool = _tool;
        _tool = t;
        foreach (var (tool, b) in _toolButtons) b.IsChecked = tool == t;
        Surface.Cursor = CursorOf(t == PaintTool.Text ? StandardCursorType.Ibeam : StandardCursorType.Cross);
    }

    private void RequestRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => { _refreshQueued = false; Surface.Refresh(); }, DispatcherPriority.Render);
    }

    private void MarkModified()
    {
        if (_modified) return;
        _modified = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _path == null ? "Untitled" : Path.GetFileName(_path);
        Title = $"{(_modified ? "*" : "")}{name} - Paint";
    }

    private void UpdateStatus()
    {
        SizeText2.Text = $"Canvas: {_doc.Width} × {_doc.Height} px";
        SelText.Text = _sel == null ? "" : $"Selection: {_sel.Rect.Width} × {_sel.Rect.Height}";
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    // ------------------------------------------------------------ zoom

    private void SetZoom(double z, Point? anchorInScroller)
    {
        z = Math.Clamp(z, 0.125, 8);
        if (Math.Abs(z - _zoom) < 0.0001) return;
        CommitText();
        var anchor = anchorInScroller ?? new Point(Scroller.Viewport.Width / 2, Scroller.Viewport.Height / 2);
        double ix = (Scroller.Offset.X + anchor.X - 12) / _zoom, iy = (Scroller.Offset.Y + anchor.Y - 12) / _zoom;
        _zoom = z;
        Surface.Zoom = z;
        _zoomBusy = true;
        ZoomSlider.Value = Math.Log2(z);
        _zoomBusy = false;
        UpdateStatus();
        Dispatcher.UIThread.Post(() => Scroller.Offset = new Vector(Math.Max(0, ix * z + 12 - anchor.X), Math.Max(0, iy * z + 12 - anchor.Y)),
            DispatcherPriority.Background);
    }

    private void ZoomStep(bool inwards, Point? anchor)
    {
        double[] steps = { 0.125, 0.25, 0.5, 1, 2, 3, 4, 6, 8 };
        double next = inwards ? steps.FirstOrDefault(s => s > _zoom + 0.001, 8) : steps.LastOrDefault(s => s < _zoom - 0.001, 0.125);
        SetZoom(next, anchor);
    }

    private void FitToWindow()
    {
        double vw = Math.Max(50, Scroller.Viewport.Width - 40), vh = Math.Max(50, Scroller.Viewport.Height - 40);
        SetZoom(Math.Min(8, Math.Min(vw / _doc.Width, vh / _doc.Height)), null);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        ZoomStep(e.Delta.Y > 0, e.GetPosition(Scroller));
        e.Handled = true;
    }

    // ------------------------------------------------------------ menu + keyboard

    private async void OnMenu(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as Control)?.Tag as string;
        if (tag != null) await RunCommand(tag);
    }

    private async Task RunCommand(string cmd)
    {
        switch (cmd)
        {
            case "new": await NewImage(); break;
            case "open": await OpenImage(); break;
            case "pastefrom": await PasteFromFile(); break;
            case "save": await SaveImage(false); break;
            case "saveas": await SaveImage(true); break;
            case "close": Close(); break;
            case "undo": DoUndo(); break;
            case "redo": DoRedo(); break;
            case "cut": CopySelection(); DeleteSelection(); break;
            case "copy": CopySelection(); break;
            case "paste": PasteClip(); break;
            case "delete": DeleteSelection(); break;
            case "selectall": SelectAll(); break;
            case "deselect": CommitAll(); break;
            case "crop": CropToSelection(); break;
            case "resize": await ResizeSkewImage(); break;
            case "canvas": await ChangeCanvasSize(); break;
            case "rotr": ApplyOp(b => PaintDoc.Transform(b, SKMatrix.CreateRotationDegrees(90), BgFor(b))); break;
            case "rotl": ApplyOp(b => PaintDoc.Transform(b, SKMatrix.CreateRotationDegrees(270), BgFor(b))); break;
            case "rot180": ApplyOp(b => PaintDoc.Transform(b, SKMatrix.CreateRotationDegrees(180), BgFor(b))); break;
            case "fliph": ApplyOp(b => PaintDoc.Transform(b, SKMatrix.CreateScale(-1, 1), BgFor(b))); break;
            case "flipv": ApplyOp(b => PaintDoc.Transform(b, SKMatrix.CreateScale(1, -1), BgFor(b))); break;
            case "invert": ApplyOp(b => { var c = b.Copy(); PaintDoc.InvertColors(c); return c; }); break;
            case "clear": ClearImage(); break;
            case "zoomin": ZoomStep(true, null); break;
            case "zoomout": ZoomStep(false, null); break;
            case "zoom100": SetZoom(1, null); break;
            case "fit": FitToWindow(); break;
            case "grid": Surface.ShowGrid = GridItem.IsChecked == true; Surface.InvalidateVisual(); break;
            case "status": StatusBar.IsVisible = StatusItem.IsChecked == true; break;
        }
    }

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) { if (e.Key == Key.Escape) { CancelText(); e.Handled = true; } return; }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        string? cmd = null;
        if (ctrl)
        {
            cmd = e.Key switch
            {
                Key.N when shift => "clear",
                Key.N => "new",
                Key.O => "open",
                Key.S when shift => "saveas",
                Key.S => "save",
                Key.Z => "undo",
                Key.Y => "redo",
                Key.X when shift => "crop",
                Key.X => "cut",
                Key.C => "copy",
                Key.V => "paste",
                Key.A => "selectall",
                Key.W => "resize",
                Key.E => "canvas",
                Key.I when shift => "invert",
                Key.OemPlus or Key.Add => "zoomin",
                Key.OemMinus or Key.Subtract => "zoomout",
                Key.D0 or Key.NumPad0 => "zoom100",
                Key.G => "gridtoggle",
                _ => null
            };
        }
        else if (e.Key == Key.Delete) cmd = "delete";
        else if (e.Key == Key.Escape) { if (_stage != 0) CancelShape(); else cmd = "deselect"; e.Handled = true; }
        else if (e.Key == Key.Enter && _stage != 0) { FinishShape(); e.Handled = true; }
        if (cmd == null) return;
        e.Handled = true;
        if (cmd == "gridtoggle") { GridItem.IsChecked = GridItem.IsChecked != true; await RunCommand("grid"); }
        else await RunCommand(cmd);
    }

    // ------------------------------------------------------------ history

    private void DoUndo()
    {
        if (_stage != 0) { CancelShape(); return; }
        if (_textBox != null) { CancelText(); return; }
        DropSelection();
        if (!_doc.CanUndo) return;
        _doc.Undo();
        AfterDocChanged(true);
    }

    private void DoRedo()
    {
        CommitText();
        DropSelection();
        if (!_doc.CanRedo) return;
        _doc.Redo();
        AfterDocChanged(true);
    }

    private void AfterDocChanged(bool modified)
    {
        if (modified) MarkModified();
        Surface.Refresh();
        UpdateStatus();
    }

    // ------------------------------------------------------------ files

    private async Task<bool> ConfirmDiscard()
    {
        if (!_modified) return true;
        var name = _path == null ? "Untitled" : Path.GetFileName(_path);
        int r = await Dialogs.Ask(this, "Paint", $"Save changes to {name}?", "Save", "Don't save", "Cancel");
        if (r == 0) return await SaveImage(false);
        return r == 1;
    }

    private void LoadDocument(SKBitmap bmp, string? path)
    {
        DropSelection();
        CancelText();
        CancelShapeSilently();
        _doc.ClearHistory();
        _doc.Replace(bmp);
        _path = path;
        _modified = false;
        Surface.Refresh();
        UpdateStatus();
        UpdateTitle();
    }

    private async Task NewImage()
    {
        if (!await ConfirmDiscard()) return;
        LoadDocument(PaintDoc.Create(_doc.Width, _doc.Height, SKColors.White), null);
    }

    private static FilePickerFileType ImageTypes => new("Images")
    {
        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp" }
    };

    private async Task OpenImage()
    {
        if (!await ConfirmDiscard()) return;
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        { Title = "Open image", AllowMultiple = false, FileTypeFilter = new[] { ImageTypes } });
        if (files.Count == 0) return;
        await OpenFile(files[0]);
    }

    /// <summary>Opens an image by path (used by "Open with" hooks as well as the dialog).</summary>
    public async Task OpenPath(string path)
    {
        try
        {
            await using var s = File.OpenRead(path);
            var bmp = PaintDoc.Load(s);
            if (bmp == null) { await Dialogs.ShowMessage(this, "Paint", "That file is not a picture Paint can read."); return; }
            LoadDocument(bmp, path);
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Paint", "Could not open the file: " + ex.Message); }
    }

    private async Task OpenFile(FileDialogs.ChosenItem file)
    {
        try
        {
            await using var s = await file.OpenReadAsync();
            var bmp = PaintDoc.Load(s);
            if (bmp == null) { await Dialogs.ShowMessage(this, "Paint", "That file is not a picture Paint can read."); return; }
            LoadDocument(bmp, file.TryGetLocalPath());
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Paint", "Could not open the file: " + ex.Message); }
    }

    private async Task PasteFromFile()
    {
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        { Title = "Paste from", AllowMultiple = false, FileTypeFilter = new[] { ImageTypes } });
        if (files.Count == 0) return;
        try
        {
            await using var s = await files[0].OpenReadAsync();
            var bmp = PaintDoc.Load(s);
            if (bmp == null) { await Dialogs.ShowMessage(this, "Paint", "That file is not a picture Paint can read."); return; }
            BeginPaste(bmp);
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Paint", "Could not open the file: " + ex.Message); }
    }

    private async Task<bool> SaveImage(bool saveAs)
    {
        CommitAll();
        string? target = saveAs ? null : _path;
        FileDialogs.ChosenItem? chosen = null;
        if (target == null)
        {
            chosen = await FileDialogs.Save(this, new FilePickerSaveOptions
            {
                Title = "Save picture",
                SuggestedFileName = _path != null ? Path.GetFileName(_path) : $"Drawing_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("Bitmap image") { Patterns = new[] { "*.bmp" } }
                }
            });
            if (chosen == null) return false;
        }
        try
        {
            var name = chosen?.Name ?? Path.GetFileName(target!);
            var bytes = PaintDoc.Encode(_doc.Bmp, Path.GetExtension(name));
            if (chosen != null)
            {
                await using var stream = await chosen.OpenWriteAsync();
                try { stream.SetLength(0); } catch { }
                await stream.WriteAsync(bytes);
                _path = chosen.TryGetLocalPath() ?? name;
            }
            else await File.WriteAllBytesAsync(target!, bytes);
            _modified = false;
            UpdateTitle();
            Notifier.Show("Paint", "Saved " + name, "paint");
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Paint", "Could not save the picture: " + ex.Message);
            return false;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_modified || _forceClose) return;
        e.Cancel = true;
        if (await ConfirmDiscard()) { _forceClose = true; Close(); }
    }

    // ------------------------------------------------------------ canvas / image operations

    private SKColor BgFor(SKBitmap b) => _sel is { Lifted: true } && ReferenceEquals(b, _sel.Content) ? SKColors.Transparent : _c2;

    private async Task ChangeCanvasSize()
    {
        CommitAll();
        var r = await PaintDialogs.CanvasSize(this, _doc.Width, _doc.Height);
        if (r is not { } size) return;
        _doc.PushUndo();
        _doc.ResizeCanvas(size.W, size.H, _c2);
        AfterDocChanged(true);
    }

    private async Task ResizeSkewImage()
    {
        CommitText();
        FinishShape();
        bool onSel = _sel != null;
        int w = onSel ? _sel!.Rect.Width : _doc.Width, h = onSel ? _sel!.Rect.Height : _doc.Height;
        var r = await PaintDialogs.ResizeSkew(this, w, h, onSel);
        if (r == null) return;
        ApplyOp(b =>
        {
            var fill = BgFor(b);
            var scaled = PaintDoc.Scale(b, r.Width, r.Height, fill);
            if (Math.Abs(r.SkewX) < 0.01 && Math.Abs(r.SkewY) < 0.01) return scaled;
            var m = SKMatrix.CreateSkew((float)Math.Tan(-r.SkewX * Math.PI / 180), (float)Math.Tan(r.SkewY * Math.PI / 180));
            var skewed = PaintDoc.Transform(scaled, m, fill);
            scaled.Dispose();
            return skewed;
        });
    }

    /// <summary>Runs a bitmap → bitmap operation on the selection if there is one, otherwise on the whole picture.</summary>
    private void ApplyOp(Func<SKBitmap, SKBitmap> op)
    {
        CommitText();
        FinishShape();
        if (_sel != null)
        {
            Lift(false);
            var old = _sel.Content!;
            var nc = op(old);
            _sel.Content = nc;
            _sel.Rect = new SKRectI(_sel.Rect.Left, _sel.Rect.Top, _sel.Rect.Left + nc.Width, _sel.Rect.Top + nc.Height);
            _sel.OrigW = nc.Width; _sel.OrigH = nc.Height;
            _sel.Poly = null;
            if (!ReferenceEquals(old, nc)) old.Dispose();
            RefreshFloat();
            SyncSelection();
            MarkModified();
            return;
        }
        _doc.PushUndo();
        var nb = op(_doc.Bmp);
        _doc.Replace(nb);
        AfterDocChanged(true);
    }

    private void ClearImage()
    {
        CommitAll();
        _doc.PushUndo();
        _doc.Bmp.Erase(_c2);
        AfterDocChanged(true);
    }
}
