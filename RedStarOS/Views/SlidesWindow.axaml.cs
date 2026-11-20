using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RedStarOS.Services;
using SkiaSharp;

namespace RedStarOS.Views;

public sealed class SlideThumb : INotifyPropertyChanged
{
    private Bitmap? _image;
    public int Number { get; set; }
    public Bitmap? Image
    {
        get => _image;
        set { _image = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Red Star Slides: a PowerPoint-style presentation editor — slide list, layouts and themes, text boxes, shapes and pictures
/// with move / resize / rotate, speaker notes, transitions, full-screen slide show, undo/redo, PDF and PNG export.
/// Files are saved as .rsslides (JSON with embedded pictures). This file holds the window plumbing and deck operations;
/// pointer editing lives in SlidesWindow.Edit.cs.
/// </summary>
public partial class SlidesWindow : Window
{
    private static readonly int[] Sizes = { 12, 14, 16, 18, 20, 24, 28, 32, 36, 40, 44, 48, 54, 60, 66, 72, 88, 96, 120 };
    private static readonly string[] Presets =
    {
        "#FFFFFF", "#F2F2F2", "#D9D9D9", "#A6A6A6", "#7F7F7F", "#595959", "#262626", "#000000",
        "#C8102E", "#ED1C24", "#FF7F27", "#FFC90E", "#FFF200", "#B5E61D", "#22B14C", "#2F855A",
        "#00A2E8", "#1E6FD9", "#3F48CC", "#0B2545", "#6B46C1", "#A349A4", "#FFAEC9", "#B97A57",
        "#FDE2E4", "#FFE8CC", "#FFF3BF", "#D8F5A2", "#C5F6FA", "#D0EBFF", "#E5DBFF", "#F1F3F5"
    };

    private Deck _deck = new();
    private int _cur;
    private SlideItem? _sel;
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string? _path;
    private bool _modified, _forceClose, _syncing;
    private string _font = "Sans";
    private readonly ObservableCollection<SlideThumb> _thumbs = new();
    private readonly HashSet<int> _dirtyThumbs = new();
    private DispatcherTimer? _thumbTimer;
    private readonly List<Action> _refreshers = new();

    private static string? _clipItem, _clipImageId, _clipImageData;

    private Slide CurSlide => _deck.Slides.Count > 0 ? _deck.Slides[Math.Clamp(_cur, 0, _deck.Slides.Count - 1)] : _emptySlide;
    private static readonly Slide _emptySlide = new();
    private SlideTheme DeckTheme => SlideThemes.Get(_deck.Theme);

    public SlidesWindow()
    {
        InitializeComponent();

        var fonts = SKFontManager.Default.GetFontFamilies().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        if (fonts.Length == 0) fonts = new[] { "Sans" };
        _font = new[] { "DejaVu Sans", "Liberation Sans", "Noto Sans", "Ubuntu", "Arial" }.FirstOrDefault(p => fonts.Contains(p)) ?? fonts[0];
        FontBox.ItemsSource = fonts;
        FontBox.SelectedItem = _font;
        SizeBox.ItemsSource = Sizes;
        SizeBox.SelectedItem = 32;
        VAlignBox.ItemsSource = new[] { "Top", "Middle", "Bottom" };
        VAlignBox.SelectedIndex = 0;
        LineWBox.ItemsSource = new[] { 0, 1, 2, 3, 4, 6, 8, 12 };
        LineWBox.SelectedItem = 2;
        LayoutBox.ItemsSource = SlideLayouts.Names;
        LayoutBox.SelectedIndex = 1;
        ShapeBox.ItemsSource = PaintShapes.All.Where(s => s.Kind is not (ShapeKind.Curve or ShapeKind.Polygon)).Select(s => s.Name).ToArray();
        ShapeBox.SelectedIndex = 1;
        ThemeBox.ItemsSource = SlideThemes.All.Select(t => t.Name).ToArray();
        TransitionBox.ItemsSource = new[] { "None", "Fade", "Push" };
        TransitionBox.SelectedIndex = 0;

        TextColorHost.Content = MakeColorButton("Text colour", () => _sel?.Color ?? DeckTheme.Text, h => ApplyToSel(i => i.Color = h), false);
        FillHost.Content = MakeColorButton("Fill colour", () => _sel?.Fill ?? "", h => ApplyToSel(i => i.Fill = h), true);
        LineHost.Content = MakeColorButton("Outline colour", () => _sel?.Stroke ?? "", h => ApplyToSel(i => { i.Stroke = h; if (h != "" && i.StrokeW <= 0) i.StrokeW = 2; }), true);
        BgHost.Content = MakeColorButton("Slide background", () => CurSlide.Background, h => Edit(() => CurSlide.Background = h), false);

        FontBox.SelectionChanged += (_, _) => { if (FontBox.SelectedItem is string f) { _font = f; ApplyToSel(i => i.Font = f); } };
        SizeBox.SelectionChanged += (_, _) => { if (SizeBox.SelectedItem is int n) ApplyToSel(i => i.Size = n); };
        BoldBtn.IsCheckedChanged += (_, _) => ApplyToSel(i => i.Bold = BoldBtn.IsChecked == true);
        ItalicBtn.IsCheckedChanged += (_, _) => ApplyToSel(i => i.Italic = ItalicBtn.IsChecked == true);
        UnderBtn.IsCheckedChanged += (_, _) => ApplyToSel(i => i.Underline = UnderBtn.IsChecked == true);
        BulletBtn.IsCheckedChanged += (_, _) => ApplyToSel(i => i.Bullets = BulletBtn.IsChecked == true);
        AlignL.Click += (_, _) => ApplyToSel(i => i.Align = "left");
        AlignC.Click += (_, _) => ApplyToSel(i => i.Align = "center");
        AlignR.Click += (_, _) => ApplyToSel(i => i.Align = "right");
        VAlignBox.SelectionChanged += (_, _) => ApplyToSel(i => i.VAlign = VAlignBox.SelectedIndex switch { 1 => "middle", 2 => "bottom", _ => "top" });
        LineWBox.SelectionChanged += (_, _) => { if (LineWBox.SelectedItem is int w) ApplyToSel(i => i.StrokeW = w); };
        RotBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyRotation(); Surface.Focus(); e.Handled = true; } };
        RotBox.LostFocus += (_, _) => ApplyRotation();
        ThemeBox.SelectionChanged += (_, _) => { if (!_syncing && ThemeBox.SelectedItem is string t) ApplyTheme(t); };
        TransitionBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || TransitionBox.SelectedItem is not string t) return;
            Edit(() => CurSlide.Transition = t);
        };
        NotesBox.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _syncing || _deck.Slides.Count == 0) return;
            CurSlide.Notes = NotesBox.Text ?? "";
            MarkModified();
        };

        ThumbList.ItemsSource = _thumbs;
        ThumbList.SelectionChanged += (_, _) =>
        {
            if (_syncing || ThumbList.SelectedIndex < 0 || ThumbList.SelectedIndex == _cur) return;
            GoTo(ThumbList.SelectedIndex);
        };

        Surface.PointerPressed += OnDown;
        Surface.PointerMoved += OnMove;
        Surface.PointerReleased += OnUp;
        KeyDown += OnKey;
        Closing += OnClosing;

        NewDeck();
    }

    // ------------------------------------------------------------ colour buttons

    private Button MakeColorButton(string tip, Func<string> get, Action<string> set, bool allowNone)
    {
        var swatch = new Border { Width = 28, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#88808080")) };
        var btn = new Button { Content = swatch, Padding = new Thickness(4) };
        ToolTip.SetTip(btn, tip);
        var flyout = new Flyout { Placement = PlacementMode.Bottom };

        void Refresh()
        {
            var h = get();
            if (string.IsNullOrEmpty(h)) { swatch.Background = Brushes.Transparent; swatch.Child = new TextBlock { Text = "∅", FontSize = 11, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center }; }
            else { swatch.Child = null; swatch.Background = new SolidColorBrush(Color.Parse(h)); }
        }

        var grid = new UniformGrid { Columns = 8 };
        foreach (var hex in Presets)
        {
            var h = hex;
            var b = new Button { Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(1), Background = new SolidColorBrush(Color.Parse(h)) };
            b.Click += (_, _) => { flyout.Hide(); set(h); Refresh(); };
            grid.Children.Add(b);
        }
        var stack = new StackPanel { Spacing = 6, Children = { grid } };
        if (allowNone)
        {
            var none = new Button { Content = "No colour", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            none.Click += (_, _) => { flyout.Hide(); set(""); Refresh(); };
            stack.Children.Add(none);
        }
        var more = new Button { Content = "More colours…", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        more.Click += async (_, _) =>
        {
            flyout.Hide();
            var c = await PaintDialogs.EditColor(this, SlideRenderer.Parse(get(), SKColors.Black));
            if (c is SKColor k) { set(SlideRenderer.ToHex(k)); Refresh(); }
        };
        stack.Children.Add(more);
        flyout.Content = stack;
        btn.Flyout = flyout;
        _refreshers.Add(Refresh);
        Refresh();
        return btn;
    }

    // ------------------------------------------------------------ deck + slide navigation

    private void NewDeck()
    {
        _deck = new Deck { Theme = "Crimson Banner" };
        _deck.Slides.Add(SlideLayouts.Make("Title Slide", DeckTheme, _font));
        _undo.Clear();
        _redo.Clear();
        _path = null;
        _modified = false;
        _cur = 0;
        _sel = null;
        Surface.Deck = _deck;
        RebuildThumbs();
        GoTo(0);
        UpdateTitle();
    }

    private void GoTo(int index)
    {
        CommitEdit();
        _cur = Math.Clamp(index, 0, _deck.Slides.Count - 1);
        _sel = null;
        Surface.Slide = CurSlide;
        Surface.Selected = null;
        Surface.GuidesX.Clear();
        Surface.GuidesY.Clear();
        Surface.Redraw();
        _syncing = true;
        ThumbList.SelectedIndex = _cur;
        _syncing = false;
        SyncRibbon();
        UpdateStatus();
    }

    private void Select(SlideItem? item)
    {
        _sel = item;
        Surface.Selected = item;
        Surface.Redraw();
        SyncRibbon();
        UpdateStatus();
    }

    private void SyncRibbon()
    {
        _syncing = true;
        var it = _sel;
        if (it != null)
        {
            FontBox.SelectedItem = it.Font;
            SizeBox.SelectedItem = Sizes.OrderBy(s => Math.Abs(s - it.Size)).First();
            BoldBtn.IsChecked = it.Bold;
            ItalicBtn.IsChecked = it.Italic;
            UnderBtn.IsChecked = it.Underline;
            BulletBtn.IsChecked = it.Bullets;
            AlignL.IsChecked = it.Align == "left";
            AlignC.IsChecked = it.Align == "center";
            AlignR.IsChecked = it.Align == "right";
            VAlignBox.SelectedIndex = it.VAlign switch { "middle" => 1, "bottom" => 2, _ => 0 };
            LineWBox.SelectedItem = new[] { 0, 1, 2, 3, 4, 6, 8, 12 }.OrderBy(w => Math.Abs(w - it.StrokeW)).First();
            RotBox.Text = Math.Round(it.Rot).ToString();
        }
        ThemeBox.SelectedItem = _deck.Theme;
        TransitionBox.SelectedItem = CurSlide.Transition;
        if (NotesBox.Text != CurSlide.Notes) NotesBox.Text = CurSlide.Notes;
        foreach (var r in _refreshers) r();
        _syncing = false;
    }

    private void UpdateStatus()
    {
        var selText = _sel == null ? "" : $"   •   {(_sel.Type == "image" ? "Picture" : _sel.Type == "shape" ? _sel.Shape : "Text box")} selected";
        StatusText.Text = $"Slide {_cur + 1} of {_deck.Slides.Count}   •   Theme: {_deck.Theme}{selText}";
    }

    private void UpdateTitle()
    {
        var name = _path == null ? "Untitled" : Path.GetFileNameWithoutExtension(_path);
        Title = $"{(_modified ? "*" : "")}{name} - Slides";
    }

    private void MarkModified()
    {
        _modified = true;
        UpdateTitle();
    }

    // ------------------------------------------------------------ thumbnails

    private void RebuildThumbs()
    {
        _thumbs.Clear();
        for (int i = 0; i < _deck.Slides.Count; i++)
        {
            _thumbs.Add(new SlideThumb { Number = i + 1 });
            RenderThumb(i);
        }
    }

    private void RenderThumb(int i)
    {
        if (i < 0 || i >= _thumbs.Count || i >= _deck.Slides.Count) return;
        int w = 224, h = Math.Max(16, 224 * _deck.Height / _deck.Width);
        using var sk = SlideRenderer.Render(_deck, _deck.Slides[i], w, h, false);
        _thumbs[i].Image = PaintCanvas.ToAvalonia(sk);
    }

    private void MarkThumb(int i)
    {
        _dirtyThumbs.Add(i);
        _thumbTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _thumbTimer.Tick -= OnThumbTick;
        _thumbTimer.Tick += OnThumbTick;
        _thumbTimer.Start();
    }

    private void OnThumbTick(object? s, EventArgs e)
    {
        _thumbTimer?.Stop();
        foreach (var i in _dirtyThumbs.ToList()) RenderThumb(i);
        _dirtyThumbs.Clear();
    }

    // ------------------------------------------------------------ edits + undo

    private string Snapshot() => JsonSerializer.Serialize(new DeckState { Theme = _deck.Theme, Slides = _deck.Slides });

    private void PushUndo()
    {
        _undo.Add(Snapshot());
        if (_undo.Count > 80) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Restore(string snapshot)
    {
        var st = JsonSerializer.Deserialize<DeckState>(snapshot)!;
        _deck.Theme = st.Theme;
        _deck.Slides = st.Slides;
        if (_deck.Slides.Count == 0) _deck.Slides.Add(SlideLayouts.Make("Blank", DeckTheme, _font));
        RebuildThumbs();
        GoTo(Math.Min(_cur, _deck.Slides.Count - 1));
        MarkModified();
    }

    private void DoUndo()
    {
        CommitEdit();
        if (_undo.Count == 0) return;
        _redo.Add(Snapshot());
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(s);
    }

    private void DoRedo()
    {
        CommitEdit();
        if (_redo.Count == 0) return;
        _undo.Add(Snapshot());
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(s);
    }

    /// <summary>Records an undo step, runs the change and refreshes the view.</summary>
    private void Edit(Action change, bool structural = false)
    {
        PushUndo();
        change();
        Changed(structural);
    }

    private void Changed(bool structural = false)
    {
        MarkModified();
        if (structural) RebuildThumbs(); else MarkThumb(_cur);
        Surface.Slide = CurSlide;
        Surface.Selected = _sel;
        Surface.Redraw();
        SyncRibbon();
        UpdateStatus();
    }

    private void ApplyToSel(Action<SlideItem> change)
    {
        if (_syncing || _sel == null) return;
        var it = _sel;
        Edit(() => change(it));
    }

    private void ApplyRotation()
    {
        if (_syncing || _sel == null) return;
        if (!double.TryParse(RotBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return;
        v = ((v % 360) + 360) % 360;
        if (Math.Abs(v - _sel.Rot) < 0.01) return;
        var it = _sel;
        Edit(() => it.Rot = v);
    }

    private void ApplyTheme(string name)
    {
        var old = DeckTheme;
        var nt = SlideThemes.Get(name);
        if (nt == old) return;
        Edit(() =>
        {
            foreach (var s in _deck.Slides)
            {
                if (string.Equals(s.Background, old.Bg, StringComparison.OrdinalIgnoreCase)) s.Background = nt.Bg;
                foreach (var it in s.Items)
                {
                    if (string.Equals(it.Color, old.Text, StringComparison.OrdinalIgnoreCase)) it.Color = nt.Text;
                    if (string.Equals(it.Fill, old.Accent, StringComparison.OrdinalIgnoreCase)) it.Fill = nt.Accent;
                    if (string.Equals(it.Stroke, old.Accent, StringComparison.OrdinalIgnoreCase)) it.Stroke = nt.Accent;
                }
            }
            _deck.Theme = name;
        }, true);
    }

    // ------------------------------------------------------------ slide commands

    private void NewSlide()
    {
        CommitEdit();
        var layout = LayoutBox.SelectedItem as string ?? "Title and Content";
        Edit(() => _deck.Slides.Insert(_cur + 1, SlideLayouts.Make(layout, DeckTheme, _font)), true);
        GoTo(_cur + 1);
    }

    private void DuplicateSlide()
    {
        CommitEdit();
        var copy = JsonSerializer.Deserialize<Slide>(JsonSerializer.Serialize(CurSlide))!;
        Edit(() => _deck.Slides.Insert(_cur + 1, copy), true);
        GoTo(_cur + 1);
    }

    private void DeleteSlide()
    {
        CommitEdit();
        int at = _cur;
        Edit(() =>
        {
            if (_deck.Slides.Count == 1) _deck.Slides[0] = SlideLayouts.Make("Blank", DeckTheme, _font);
            else _deck.Slides.RemoveAt(at);
        }, true);
        GoTo(Math.Min(at, _deck.Slides.Count - 1));
    }

    private void MoveSlide(int delta)
    {
        CommitEdit();
        int to = _cur + delta;
        if (to < 0 || to >= _deck.Slides.Count) return;
        int from = _cur;
        Edit(() => { var s = _deck.Slides[from]; _deck.Slides.RemoveAt(from); _deck.Slides.Insert(to, s); }, true);
        GoTo(to);
    }

    // ------------------------------------------------------------ insert

    private SlideItem NewTextItem() => new()
    {
        Type = "text", X = (_deck.Width - 600) / 2.0, Y = _deck.Height / 2.0 - 50, W = 600, H = 100, Size = 32,
        Font = _font, Color = DeckTheme.Text, Align = "left", VAlign = "top"
    };

    private void AddTextBox()
    {
        CommitEdit();
        var it = NewTextItem();
        Edit(() => CurSlide.Items.Add(it));
        Select(it);
        BeginEdit(it);
    }

    private void AddShape()
    {
        CommitEdit();
        var name = PaintShapes.All.Where(s => s.Kind is not (ShapeKind.Curve or ShapeKind.Polygon)).ElementAtOrDefault(Math.Max(0, ShapeBox.SelectedIndex)).Kind;
        bool line = name == ShapeKind.Line;
        var it = new SlideItem
        {
            Type = "shape", Shape = name.ToString(), W = line ? 400 : 300, H = line ? 4 : 200,
            X = (_deck.Width - (line ? 400 : 300)) / 2.0, Y = (_deck.Height - (line ? 4 : 200)) / 2.0,
            Fill = line ? "" : DeckTheme.Accent, Stroke = line ? DeckTheme.Accent : "", StrokeW = line ? 4 : 2,
            Font = _font, Size = 28, Color = "#FFFFFF", Align = "center", VAlign = "middle"
        };
        Edit(() => CurSlide.Items.Add(it));
        Select(it);
    }

    private async Task AddPicture()
    {
        CommitEdit();
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        { Title = "Insert picture", AllowMultiple = false, FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } });
        if (files.Count == 0) return;
        try
        {
            using var stream = await files[0].OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            ms.Position = 0;
            using var src = SKBitmap.Decode(ms);
            if (src == null) throw new InvalidOperationException("That file is not a picture.");
            SKBitmap use = src;
            SKBitmap? scaled = null;
            int max = 1600;
            if (src.Width > max || src.Height > max)
            {
                double k = max / (double)Math.Max(src.Width, src.Height);
                scaled = PaintDoc.Scale(src, (int)(src.Width * k), (int)(src.Height * k), SKColors.Transparent);
                use = scaled;
            }
            // re-draw into a Bgra bitmap so Skia can always encode it
            using var flat = PaintDoc.Create(use.Width, use.Height, SKColors.Transparent);
            using (var c = new SKCanvas(flat)) c.DrawBitmap(use, 0, 0);
            var ext = Path.GetExtension(files[0].Name).ToLowerInvariant();
            var bytes = PaintDoc.Encode(flat, ext is ".jpg" or ".jpeg" ? ".jpg" : ".png");
            scaled?.Dispose();

            var id = Guid.NewGuid().ToString("N");
            _deck.Images[id] = Convert.ToBase64String(bytes);
            double fit = Math.Min(1.0, Math.Min(_deck.Width * 0.7 / flat.Width, _deck.Height * 0.7 / flat.Height));
            double w = flat.Width * fit, h = flat.Height * fit;
            var item = new SlideItem { Type = "image", ImageId = id, W = w, H = h, X = (_deck.Width - w) / 2, Y = (_deck.Height - h) / 2, StrokeW = 0 };
            Edit(() => CurSlide.Items.Add(item));
            Select(item);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Slides", "Could not insert the picture: " + ex.Message);
        }
    }

    // ------------------------------------------------------------ arrange / clipboard

    private void Arrange(string how)
    {
        if (_sel == null) return;
        var items = CurSlide.Items;
        int i = items.IndexOf(_sel);
        if (i < 0) return;
        int to = how switch { "front" => items.Count - 1, "back" => 0, "forward" => Math.Min(items.Count - 1, i + 1), _ => Math.Max(0, i - 1) };
        if (to == i) return;
        var it = _sel;
        Edit(() => { items.RemoveAt(i); items.Insert(to, it); });
    }

    private void CopyItem()
    {
        if (_sel == null) return;
        _clipItem = JsonSerializer.Serialize(_sel);
        _clipImageId = null;
        _clipImageData = null;
        if (_sel.Type == "image" && _deck.Images.TryGetValue(_sel.ImageId, out var data)) { _clipImageId = _sel.ImageId; _clipImageData = data; }
    }

    private void PasteItem()
    {
        if (_clipItem == null) return;
        CommitEdit();
        var it = JsonSerializer.Deserialize<SlideItem>(_clipItem)!;
        if (it.Type == "image")
        {
            if (_clipImageId == null || _clipImageData == null) return;
            _deck.Images[_clipImageId] = _clipImageData;
        }
        it.X += 24;
        it.Y += 24;
        Edit(() => CurSlide.Items.Add(it));
        Select(it);
    }

    private void DeleteItem()
    {
        if (_sel == null) return;
        var it = _sel;
        CommitEdit();
        _sel = null;
        Edit(() => CurSlide.Items.Remove(it));
        Select(null);
    }

    private void DuplicateItem()
    {
        if (_sel == null) return;
        var clone = _sel.Clone();
        clone.X += 24;
        clone.Y += 24;
        Edit(() => CurSlide.Items.Add(clone));
        Select(clone);
    }

    // ------------------------------------------------------------ menu

    private async void OnMenu(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag) await Run(tag);
    }

    private async Task Run(string cmd)
    {
        switch (cmd)
        {
            case "new": if (await ConfirmDiscard()) NewDeck(); break;
            case "open": await OpenDeck(); break;
            case "save": await SaveDeck(false); break;
            case "saveas": await SaveDeck(true); break;
            case "pdf": await ExportPdf(); break;
            case "pngs": await ExportImages(); break;
            case "close": Close(); break;
            case "undo": DoUndo(); break;
            case "redo": DoRedo(); break;
            case "cut": CopyItem(); DeleteItem(); break;
            case "copy": CopyItem(); break;
            case "paste": PasteItem(); break;
            case "dupitem": DuplicateItem(); break;
            case "delitem": DeleteItem(); break;
            case "newslide": NewSlide(); break;
            case "dupslide": DuplicateSlide(); break;
            case "delslide": DeleteSlide(); break;
            case "slideup": MoveSlide(-1); break;
            case "slidedown": MoveSlide(1); break;
            case "addtext": AddTextBox(); break;
            case "addshape": AddShape(); break;
            case "addimage": await AddPicture(); break;
            case "front": case "back": case "forward": case "backward": Arrange(cmd); break;
            case "present": Present(0); break;
            case "presentcur": Present(_cur); break;
            case "notes": NotesHost.IsVisible = NotesItem.IsChecked == true; break;
        }
    }

    private void Present(int start)
    {
        CommitEdit();
        var show = new SlideShowWindow(_deck, start);
        show.Show();
    }

    // ------------------------------------------------------------ files

    private async Task<bool> ConfirmDiscard()
    {
        if (!_modified) return true;
        var name = _path == null ? "Untitled" : Path.GetFileName(_path);
        int r = await Dialogs.Ask(this, "Slides", $"Save changes to {name}?", "Save", "Don't save", "Cancel");
        if (r == 0) return await SaveDeck(false);
        return r == 1;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_modified || _forceClose) return;
        e.Cancel = true;
        if (await ConfirmDiscard()) { _forceClose = true; Close(); }
    }

    private static FilePickerFileType DeckType => new("Crimson Banner presentation") { Patterns = new[] { "*.rsslides" } };

    private async Task OpenDeck()
    {
        if (!await ConfirmDiscard()) return;
        var files = await FileDialogs.Open(this, new FilePickerOpenOptions
        { Title = "Open presentation", AllowMultiple = false, FileTypeFilter = new[] { DeckType } });
        var local = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (local != null) await OpenPath(local);
    }

    /// <summary>Opens a .rsslides file (File Manager, desktop icons).</summary>
    public async Task OpenPath(string path)
    {
        try
        {
            var deck = JsonSerializer.Deserialize<Deck>(await File.ReadAllTextAsync(path));
            if (deck == null) throw new InvalidOperationException("The file is empty.");
            if (deck.Width < 100 || deck.Height < 100) { deck.Width = 1280; deck.Height = 720; }
            if (deck.Slides.Count == 0) deck.Slides.Add(SlideLayouts.Make("Blank", SlideThemes.Get(deck.Theme), _font));
            _deck = deck;
            _undo.Clear();
            _redo.Clear();
            _path = path; RecentDocs.Add(path);
            _modified = false;
            _cur = 0;
            Surface.Deck = _deck;
            RebuildThumbs();
            GoTo(0);
            UpdateTitle();
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Slides", "Could not open that presentation: " + ex.Message);
        }
    }

    private async Task<bool> SaveDeck(bool saveAs)
    {
        CommitEdit();
        string? target = saveAs ? null : _path;
        if (target == null)
        {
            var file = await FileDialogs.Save(this, new FilePickerSaveOptions
            {
                Title = "Save presentation",
                SuggestedFileName = _path != null ? Path.GetFileName(_path) : "Presentation.rsslides",
                DefaultExtension = "rsslides",
                FileTypeChoices = new[] { DeckType }
            });
            target = file?.TryGetLocalPath();
            if (target == null) return false;
            if (!target.EndsWith(".rsslides", StringComparison.OrdinalIgnoreCase)) target += ".rsslides";
        }
        try
        {
            var used = _deck.Slides.SelectMany(s => s.Items).Where(i => i.Type == "image").Select(i => i.ImageId).ToHashSet();
            var copy = new Deck
            {
                Width = _deck.Width, Height = _deck.Height, Theme = _deck.Theme, Slides = _deck.Slides,
                Images = _deck.Images.Where(kv => used.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
            };
            await File.WriteAllTextAsync(target, JsonSerializer.Serialize(copy));
            _path = target; RecentDocs.Add(target);
            _modified = false;
            UpdateTitle();
            Notifier.Show("Slides", "Saved " + Path.GetFileName(target), "slides");
            return true;
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessage(this, "Slides", "Could not save the presentation: " + ex.Message);
            return false;
        }
    }

    private async Task ExportPdf()
    {
        CommitEdit();
        var file = await FileDialogs.Save(this, new FilePickerSaveOptions
        {
            Title = "Export as PDF",
            SuggestedFileName = (_path != null ? Path.GetFileNameWithoutExtension(_path) : "Presentation") + ".pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = new[] { new FilePickerFileType("PDF document") { Patterns = new[] { "*.pdf" } } }
        });
        if (file == null) return;
        try
        {
            using var ms = new MemoryStream();
            using (var wstream = new SKManagedWStream(ms))
            using (var doc = SKDocument.CreatePdf(wstream))
            {
                float pw = 960f, ph = 960f * _deck.Height / _deck.Width;
                foreach (var s in _deck.Slides)
                {
                    var c = doc.BeginPage(pw, ph);
                    c.Scale(pw / _deck.Width);
                    SlideRenderer.Draw(c, _deck, s, false);
                    doc.EndPage();
                }
                doc.Close();
            }
            await using var stream = await file.OpenWriteAsync();
            try { stream.SetLength(0); } catch { }
            await stream.WriteAsync(ms.ToArray());
            Notifier.Show("Slides", "Exported " + file.Name, "slides");
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Slides", "Could not export the PDF: " + ex.Message); }
    }

    private async Task ExportImages()
    {
        CommitEdit();
        var folders = await FileDialogs.OpenFolder(this, new FolderPickerOpenOptions { Title = "Export slides to…", AllowMultiple = false });
        var dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (dir == null) return;
        try
        {
            int w = 1920, h = 1920 * _deck.Height / _deck.Width;
            for (int i = 0; i < _deck.Slides.Count; i++)
            {
                using var sk = SlideRenderer.Render(_deck, _deck.Slides[i], w, h, false);
                await File.WriteAllBytesAsync(Path.Combine(dir, $"slide-{i + 1:00}.png"), PaintDoc.Encode(sk, ".png"));
            }
            Notifier.Show("Slides", $"Exported {_deck.Slides.Count} images", "slides");
        }
        catch (Exception ex) { await Dialogs.ShowMessage(this, "Slides", "Could not export the images: " + ex.Message); }
    }
}
