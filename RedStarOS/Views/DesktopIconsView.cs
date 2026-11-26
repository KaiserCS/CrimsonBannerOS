// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>
/// The desktop's icons, as on Windows: the Desktop folder's files and folders on a grid down the left, dragged
/// wherever you like (and dropped on a folder to move into it), chosen by clicking, Ctrl+clicking or drawing a box,
/// renamed in place, with the menus and keys of a file window: Enter, F2, Delete, Ctrl+A, Ctrl+C/X/V, F5.
/// </summary>
public sealed class DesktopIconsView : Canvas
{
    public sealed class Item
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public bool IsDir { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public LinuxApp? App { get; init; }            // a .desktop launcher
        public string Label => App?.Name ?? Name;
        internal Border? View;
        internal (int Col, int Row) Cell;
    }

    private readonly Func<string> _folder;
    private readonly string? _layoutFile;
    private DesktopLayout _layout;
    private List<Item> _items = new();
    private readonly HashSet<Item> _selected = new();
    private Item? _anchor;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _reload = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Point? _press;
    private bool _dragging;
    private Border? _band;
    private Item? _renaming;
    private static readonly Dictionary<string, IImage?> Thumbs = new();

    public event Action<Item>? Activated;
    /// <summary>Asks for the desktop's own menu (wallpaper, display, new, view, sort), at a point.</summary>
    public event Func<ContextMenu>? DesktopMenu;

    public DesktopLayout Layout => _layout;
    public IReadOnlyList<Item> Items => _items;
    public IReadOnlyCollection<Item> Selected => _selected;
    public const double Pad = 10;

    public DesktopIconsView(Func<string> folder, string? layoutFile = null)
    {
        _folder = folder;
        _layoutFile = layoutFile;
        _layout = DesktopLayout.Load(layoutFile);
        Background = Brushes.Transparent;                 // so empty space takes clicks and the menu
        Focusable = true;
        ClipToBounds = true;
        _reload.Tick += (_, _) => { _reload.Stop(); Reload(); };
        SizeChanged += (_, _) => LayOut();
        ContextRequested += OnMenu;
        AttachedToVisualTree += (_, _) => Watch();
        DetachedFromVisualTree += (_, _) => { _watcher?.Dispose(); _watcher = null; };
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var dir = _folder();
            if (!Directory.Exists(dir)) return;
            _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, EnableRaisingEvents = true };
            void Later(object? s, EventArgs e) => Dispatcher.UIThread.Post(() => { _reload.Stop(); _reload.Start(); });
            _watcher.Created += Later; _watcher.Deleted += Later; _watcher.Renamed += Later; _watcher.Changed += Later;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    // ───────── items and places ─────────

    public void Reload()
    {
        var keep = _selected.Select(i => i.Name).ToHashSet();
        var list = new List<Item>();
        try
        {
            var dir = new DirectoryInfo(_folder());
            if (dir.Exists)
                foreach (var fsi in dir.EnumerateFileSystemInfos())
                {
                    if (fsi.Name.StartsWith('.')) continue;
                    bool isDir = fsi is DirectoryInfo;
                    LinuxApp? app = !isDir && fsi.Name.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? DesktopEntries.ParseFile(fsi.FullName) : null;
                    list.Add(new Item
                    {
                        Name = fsi.Name, Path = fsi.FullName, IsDir = isDir, App = app,
                        Size = fsi is FileInfo f ? f.Length : 0, Modified = fsi.LastWriteTime,
                    });
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _items = DesktopLayout.Ordered(list, _layout.Sort, i => i.Name, i => i.IsDir, i => i.Size, i => i.Modified).ToList();
        _selected.Clear();
        foreach (var i in _items) if (keep.Contains(i.Name)) _selected.Add(i);
        Children.Clear();
        foreach (var i in _items) { i.View = Build(i); Children.Add(i.View); }
        LayOut();
    }

    private (int Rows, int Cols) Grid()
    {
        var (w, h, _) = DesktopLayout.CellSize(_layout.Size);
        double height = Bounds.Height > 0 ? Bounds.Height : 800, width = Bounds.Width > 0 ? Bounds.Width : 1200;
        return (Math.Max(1, (int)((height - 2 * Pad) / h)), Math.Max(1, (int)((width - 2 * Pad) / w)));
    }

    private void LayOut()
    {
        var (rows, cols) = Grid();
        var at = _layout.Place(_items.Select(i => i.Name).ToList(), rows, cols);
        var (w, h, _) = DesktopLayout.CellSize(_layout.Size);
        foreach (var i in _items)
        {
            i.Cell = at[i.Name];
            if (i.View == null) continue;
            SetLeft(i.View, Pad + i.Cell.Col * w);
            SetTop(i.View, Pad + i.Cell.Row * h);
            i.View.RenderTransform = null;
            i.View.IsVisible = _layout.ShowIcons;        // hidden icons: the desktop and its menu stay
            Mark(i);
        }
    }

    private void KeepPlaces()
    {
        _layout.Keep(_items.ToDictionary(i => i.Name, i => i.Cell));
        _layout.Save(_layoutFile);
    }

    public void SetSize(DesktopIconSize s) { _layout.Size = s; _layout.Save(_layoutFile); Reload(); }
    public void SetSort(DesktopSort s)
    {
        // sorting lays the icons out afresh, in the new order
        _layout.Sort = s; _layout.Cells.Clear(); _layout.Save(_layoutFile); Reload();
    }
    public void SetAutoArrange(bool on) { _layout.AutoArrange = on; if (on) _layout.Cells.Clear(); _layout.Save(_layoutFile); Reload(); }
    public void SetShowIcons(bool on) { _layout.ShowIcons = on; _layout.Save(_layoutFile); LayOut(); }

    // ───────── how an icon looks ─────────

    private Border Build(Item i)
    {
        var (w, h, size) = DesktopLayout.CellSize(_layout.Size);
        var label = new TextBlock
        {
            Text = i.Label, Foreground = Brushes.White, FontSize = _layout.Size == DesktopIconSize.Small ? 11.5 : 12.5,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 3, OffsetX = 0, OffsetY = 1, Color = Colors.Black, Opacity = 0.95 },
        };
        var stack = new StackPanel { Spacing = 5, Width = w - 8, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Picture(i, size));
        stack.Children.Add(label);
        var b = new Border
        {
            Width = w - 4, Height = h - 4, Padding = new Thickness(2, 6, 2, 2), CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1), Child = stack, Tag = i, Background = Brushes.Transparent,
        };
        ToolTip.SetTip(b, i.Name);
        ToolTip.SetShowDelay(b, 900);
        b.PointerEntered += (_, _) => { if (!_selected.Contains(i)) b.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)); };
        b.PointerExited += (_, _) => Mark(i);
        return b;
    }

    private void Mark(Item i)
    {
        if (i.View == null) return;
        bool on = _selected.Contains(i);
        var accent = ThemeService.AccentColor;
        i.View.Background = on ? new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B)) : Brushes.Transparent;
        i.View.BorderBrush = on ? new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B)) : Brushes.Transparent;
    }

    /// <summary>A folder is the folder picture; a launcher its program's icon; a photo itself; anything else its kind's icon on the graphite plate.</summary>
    private static Control Picture(Item i, double size)
    {
        IImage? img = null;
        if (i.IsDir) img = AppIcons.Picture("files");
        else if (i.App != null) img = StartMenu.LinuxIcon(i.App);
        else if (FileOps.ImageExtensions.Contains(System.IO.Path.GetExtension(i.Path).ToLowerInvariant())) img = Thumb(i, size);
        if (img != null)
        {
            var image = new Image { Source = img, Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            return image;
        }
        var tile = AppIcons.Tile(i.App != null ? "apps" : FileOps.IconName(i.Path, false), size);
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        return tile;
    }

    private static IImage? Thumb(Item i, double size)
    {
        var key = i.Path + "|" + i.Modified.Ticks;
        lock (Thumbs)
        {
            if (Thumbs.TryGetValue(key, out var t)) return t;
            try
            {
                if (i.Size > 40_000_000) return Thumbs[key] = null;
                using var fs = File.OpenRead(i.Path);
                t = Bitmap.DecodeToWidth(fs, (int)(size * 2), BitmapInterpolationMode.HighQuality);
            }
            catch (Exception) { t = null; }
            if (Thumbs.Count > 200) Thumbs.Clear();
            return Thumbs[key] = t;
        }
    }

    // ───────── choosing, dragging and the box ─────────

    private Item? ItemAt(Point p) =>
        _items.FirstOrDefault(i => i.View is { IsVisible: true } && new Rect(GetLeft(i.View), GetTop(i.View), i.View.Width, i.View.Height).Contains(p));

    public void Select(IEnumerable<Item> items, bool add = false)
    {
        if (!add) _selected.Clear();
        foreach (var i in items) _selected.Add(i);
        foreach (var i in _items) Mark(i);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_renaming != null) Commit();
        Focus();
        var p = e.GetPosition(this);
        var hit = ItemAt(p);
        var props = e.GetCurrentPoint(this).Properties;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (hit == null)
        {
            if (!ctrl) Select(Array.Empty<Item>());
            if (props.IsLeftButtonPressed) { _press = p; e.Pointer.Capture(this); }
            return;
        }
        if (e.ClickCount == 2 && props.IsLeftButtonPressed) { Activated?.Invoke(hit); e.Handled = true; return; }
        if (shift && _anchor != null)
        {
            int a = _items.IndexOf(_anchor), b = _items.IndexOf(hit);
            Select(_items.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1), ctrl);
        }
        else if (ctrl) { if (!_selected.Remove(hit)) _selected.Add(hit); Mark(hit); _anchor = hit; }
        else { if (!_selected.Contains(hit)) Select(new[] { hit }); _anchor = hit; }
        if (props.IsLeftButtonPressed) { _press = p; e.Pointer.Capture(this); }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_press is not { } start) return;
        var p = e.GetPosition(this);
        var d = p - start;
        if (!_dragging && Math.Abs(d.X) < 5 && Math.Abs(d.Y) < 5) return;
        _dragging = true;
        if (_selected.Count > 0 && ItemAt(start) is { } grabbed && _selected.Contains(grabbed))
        {
            foreach (var i in _selected) if (i.View != null) { i.View.RenderTransform = new TranslateTransform(d.X, d.Y); i.View.Opacity = 0.75; i.View.ZIndex = 5; }
            return;
        }
        // drawing a box: everything it touches is chosen
        if (_band == null)
        {
            var accent = ThemeService.AccentColor;
            _band = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
                BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(1), IsHitTestVisible = false, ZIndex = 10,
            };
            Children.Add(_band);
        }
        var r = new Rect(start, p).Normalize();
        SetLeft(_band, r.X); SetTop(_band, r.Y); _band.Width = r.Width; _band.Height = r.Height;
        Select(_items.Where(i => i.View != null && r.Intersects(new Rect(GetLeft(i.View), GetTop(i.View), i.View.Width, i.View.Height))),
               e.KeyModifiers.HasFlag(KeyModifiers.Control));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var start = _press;
        _press = null;
        if (_band != null) { Children.Remove(_band); _band = null; }
        if (!_dragging || start == null) { _dragging = false; return; }
        _dragging = false;
        var p = e.GetPosition(this);
        if (ItemAt(start.Value) is not { } grabbed || !_selected.Contains(grabbed)) return;
        foreach (var i in _selected) if (i.View != null) { i.View.Opacity = 1; i.View.ZIndex = 0; }

        // dropped on a folder that isn't being moved: into it
        if (_items.FirstOrDefault(i => i.IsDir && !_selected.Contains(i) && i.View != null &&
                new Rect(GetLeft(i.View), GetTop(i.View), i.View.Width, i.View.Height).Contains(p)) is { } folder)
        {
            foreach (var i in _selected.ToList())
                try { FileOps.MoveInto(i.Path, folder.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notifier.Show("Desktop", $"“{i.Name}” couldn't be moved: {ex.Message}", "files"); }
            Reload();
            return;
        }
        var (w, h, _) = DesktopLayout.CellSize(_layout.Size);
        int dc = (int)Math.Round((p.X - start.Value.X) / w), dr = (int)Math.Round((p.Y - start.Value.Y) / h);
        if (_layout.AutoArrange || (dc == 0 && dr == 0)) { LayOut(); return; }
        var (rows, cols) = Grid();
        var moved = DesktopLayout.Move(_items.ToDictionary(i => i.Name, i => i.Cell), _selected.Select(i => i.Name).ToList(), dc, dr, rows, cols);
        foreach (var i in _items) i.Cell = moved[i.Name];
        KeepPlaces();
        LayOut();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_band != null) { Children.Remove(_band); _band = null; }
        if (_dragging) { foreach (var i in _selected) if (i.View != null) { i.View.Opacity = 1; i.View.ZIndex = 0; } LayOut(); }
        _press = null; _dragging = false;
    }

    // ───────── menus ─────────

    private void OnMenu(object? sender, ContextRequestedEventArgs e)
    {
        var hit = e.TryGetPosition(this, out var p) ? ItemAt(p) : null;
        ContextMenu? menu;
        if (hit != null)
        {
            if (!_selected.Contains(hit)) Select(new[] { hit });
            menu = ItemMenu();
        }
        else
        {
            Select(Array.Empty<Item>());
            menu = DesktopMenu?.Invoke();
        }
        if (menu == null) return;
        menu.PlacementTarget = this;
        menu.Open(this);
        e.Handled = true;
    }

    private static MenuItem Entry(string header, string? icon, Action act, string? keys = null, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = keys != null ? KeyGesture.Parse(keys) : null };
        if (icon != null) m.Icon = Icons.Glyph(icon, 16);
        m.Click += (_, _) => act();
        return m;
    }

    private ContextMenu ItemMenu()
    {
        var sel = _selected.ToList();
        var one = sel.Count == 1 ? sel[0] : null;
        var items = new List<Control>
        {
            Entry("Open", null, () => { foreach (var i in sel) Activated?.Invoke(i); }, "Enter"),
        };
        if (one is { IsDir: false, App: null }) items.Add(Entry("Open with Files", "folder", () => WindowManager.Open(new FileManagerWindow(_folder()), "files")));
        items.Add(new Separator());
        items.Add(Entry("Cut", "cut", () => ToClipboard(cut: true), "Ctrl+X"));
        items.Add(Entry("Copy", "copy", () => ToClipboard(cut: false), "Ctrl+C"));
        items.Add(new Separator());
        items.Add(Entry("Rename", "rename", () => { if (one != null) Rename(one); }, "F2", one != null));
        items.Add(Entry("Delete", "trash", Delete, "Delete"));
        items.Add(new Separator());
        items.Add(Entry("Show in Files", "folder", () => WindowManager.Open(new FileManagerWindow(_folder()), "files")));
        return new ContextMenu { ItemsSource = items };
    }

    // ───────── doing things ─────────

    public void ToClipboard(bool cut)
    {
        if (_selected.Count == 0) return;
        FileClipboard.Paths = _selected.Select(i => i.Path).ToList();
        FileClipboard.IsCut = cut;
        foreach (var i in _items) if (i.View != null) i.View.Opacity = cut && _selected.Contains(i) ? 0.55 : 1;
    }

    public bool CanPaste => FileClipboard.Paths.Count > 0;

    public void Paste()
    {
        var dir = _folder();
        Directory.CreateDirectory(dir);
        foreach (var p in FileClipboard.Paths.ToList())
            try { if (FileClipboard.IsCut) FileOps.MoveInto(p, dir); else FileOps.CopyInto(p, dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notifier.Show("Desktop", $"“{System.IO.Path.GetFileName(p)}” couldn't be pasted: {ex.Message}", "files"); }
        if (FileClipboard.IsCut) FileClipboard.Paths = new();
        Reload();
    }

    public void Delete()
    {
        foreach (var i in _selected.ToList())
            try { FileOps.MoveToTrash(i.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notifier.Show("Desktop", $"“{i.Name}” couldn't be moved to the bin: {ex.Message}", "files"); }
        Reload();
    }

    /// <summary>Makes a new folder or file with a free name, then lets it be named in place.</summary>
    public void New(string name, bool folder, string content = "")
    {
        var dir = _folder();
        try
        {
            Directory.CreateDirectory(dir);
            var path = FileOps.UniquePath(dir, name);
            if (folder) Directory.CreateDirectory(path); else File.WriteAllText(path, content);
            Reload();
            if (_items.FirstOrDefault(i => i.Path == path) is { } made) { Select(new[] { made }); Rename(made); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notifier.Show("Desktop", ex.Message, "files"); }
    }

    public void Rename(Item i)
    {
        if (i.View?.Child is not StackPanel stack || stack.Children.Count < 2) return;
        _renaming = i;
        var ext = i.IsDir ? "" : System.IO.Path.GetExtension(i.Name);
        var box = new TextBox
        {
            Text = i.Name, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MinHeight = 0,
            Padding = new Thickness(3, 2), AcceptsReturn = false,
        };
        stack.Children[1] = box;
        box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectionStart = 0;
            box.SelectionEnd = ext.Length > 0 && i.Name.Length > ext.Length ? i.Name.Length - ext.Length : i.Name.Length;
        });
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { _renaming = null; Reload(); Focus(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => { if (_renaming == i) Commit(); };
    }

    private void Commit()
    {
        var i = _renaming;
        _renaming = null;
        if (i?.View?.Child is not StackPanel { Children.Count: >= 2 } stack || stack.Children[1] is not TextBox box) { Reload(); return; }
        var name = (box.Text ?? "").Trim();
        if (name.Length > 0 && name != i.Name && name.IndexOfAny(new[] { '/', '\0' }) < 0)
        {
            var to = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(i.Path)!, name);
            try
            {
                if (File.Exists(to) || Directory.Exists(to)) Notifier.Show("Desktop", $"There's already something called “{name}” on the desktop.", "files");
                else
                {
                    if (i.IsDir) Directory.Move(i.Path, to); else File.Move(i.Path, to);
                    // it keeps its place under its new name
                    if (_layout.Cells.Remove(i.Name, out var cell)) { _layout.Cells[name] = cell; _layout.Save(_layoutFile); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notifier.Show("Desktop", $"“{i.Name}” couldn't be renamed: {ex.Message}", "files"); }
        }
        Reload();
        if (_items.FirstOrDefault(x => x.Name == name) is { } renamed) Select(new[] { renamed });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_renaming != null || e.Handled) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter: foreach (var i in _selected.ToList()) Activated?.Invoke(i); break;
            case Key.F2: if (_selected.Count == 1) Rename(_selected.First()); break;
            case Key.Delete: Delete(); break;
            case Key.F5: Reload(); break;
            case Key.A when ctrl: Select(_items); break;
            case Key.C when ctrl: ToClipboard(cut: false); break;
            case Key.X when ctrl: ToClipboard(cut: true); break;
            case Key.V when ctrl: if (CanPaste) Paste(); break;
            case Key.Left or Key.Right or Key.Up or Key.Down: Step(e.Key); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Arrow keys: to the nearest icon that way.</summary>
    private void Step(Key k)
    {
        if (_items.Count == 0) return;
        var from = _anchor ?? _selected.FirstOrDefault();
        if (from == null) { Select(new[] { _items[0] }); _anchor = _items[0]; return; }
        var (dc, dr) = k switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), _ => (0, 1) };
        var next = _items.Where(i => i != from)
            .Where(i => dc != 0 ? Math.Sign(i.Cell.Col - from.Cell.Col) == dc : Math.Sign(i.Cell.Row - from.Cell.Row) == dr)
            .OrderBy(i => Math.Abs(i.Cell.Col - from.Cell.Col) * (dc != 0 ? 1 : 4) + Math.Abs(i.Cell.Row - from.Cell.Row) * (dr != 0 ? 1 : 4))
            .FirstOrDefault();
        if (next == null) return;
        Select(new[] { next });
        _anchor = next;
    }
}
