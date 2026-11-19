using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;

namespace RedStarOS.Views;

/// <summary>Mouse and keyboard editing for Red Star Slides: select, move with smart guides, resize, rotate, in-place text.</summary>
public partial class SlidesWindow
{
    private enum DragKind { None, Move, Resize, Rotate }

    private DragKind _drag;
    private SlideHandle _handle;
    private SKPoint _dragStart;
    private double _ox, _oy, _ow, _oh, _orot;
    private bool _dragPushed;
    private TextBox? _editBox;
    private SlideItem? _editItem;
    private DateTime _lastNudge = DateTime.MinValue;
    private readonly Dictionary<StandardCursorType, Cursor> _cursors = new();

    private Cursor CursorOf(StandardCursorType t)
    {
        if (!_cursors.TryGetValue(t, out var c)) _cursors[t] = c = new Cursor(t);
        return c;
    }

    // ------------------------------------------------------------ pointer

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        Surface.Focus();
        CommitEdit();
        if (!e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed) return;
        var screen = e.GetPosition(Surface);
        var sp = Surface.ToSlide(screen);

        if (_sel != null)
        {
            var h = Surface.HitHandle(screen);
            if (h != SlideHandle.None)
            {
                _drag = h == SlideHandle.Rotate ? DragKind.Rotate : DragKind.Resize;
                _handle = h;
                BeginDrag(sp);
                e.Pointer.Capture(Surface);
                return;
            }
        }

        var hit = Surface.HitItem(sp);
        if (hit == null) { Select(null); return; }
        Select(hit);
        if (e.ClickCount == 2 && hit.Type != "image") { BeginEdit(hit); return; }
        _drag = DragKind.Move;
        BeginDrag(sp);
        e.Pointer.Capture(Surface);
    }

    private void BeginDrag(SKPoint sp)
    {
        _dragStart = sp;
        _ox = _sel!.X; _oy = _sel.Y; _ow = _sel.W; _oh = _sel.H; _orot = _sel.Rot;
        _dragPushed = false;
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        var screen = e.GetPosition(Surface);
        var sp = Surface.ToSlide(screen);

        if (_drag == DragKind.None || _sel == null)
        {
            var h = Surface.HitHandle(screen);
            StandardCursorType t = h switch
            {
                SlideHandle.None => Surface.HitItem(sp) != null ? StandardCursorType.SizeAll : StandardCursorType.Arrow,
                SlideHandle.Rotate => StandardCursorType.Hand,
                SlideHandle.E or SlideHandle.W => StandardCursorType.SizeWestEast,
                SlideHandle.N or SlideHandle.S => StandardCursorType.SizeNorthSouth,
                SlideHandle.NW or SlideHandle.SE => StandardCursorType.TopLeftCorner,
                _ => StandardCursorType.TopRightCorner
            };
            Surface.Cursor = CursorOf(t);
            return;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        double dx = sp.X - _dragStart.X, dy = sp.Y - _dragStart.Y;
        if (!_dragPushed)
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 2 / Math.Max(0.01, Surface.Scale)) return;
            PushUndo();
            _dragPushed = true;
        }

        Surface.GuidesX.Clear();
        Surface.GuidesY.Clear();
        switch (_drag)
        {
            case DragKind.Move:
                _sel.X = SnapAxis(_ox + dx, _sel.W, _deck.Width, true);
                _sel.Y = SnapAxis(_oy + dy, _sel.H, _deck.Height, false);
                break;
            case DragKind.Rotate:
            {
                double cx = _ox + _ow / 2, cy = _oy + _oh / 2;
                double ang = Math.Atan2(sp.Y - cy, sp.X - cx) * 180 / Math.PI + 90;
                ang = ((ang % 360) + 360) % 360;
                if (shift) ang = Math.Round(ang / 15) * 15 % 360;
                _sel.Rot = ang;
                break;
            }
            case DragKind.Resize:
                ResizeDrag(dx, dy, shift);
                break;
        }
        Surface.Redraw();
        MarkThumb(_cur);
        MarkModified();
    }

    private void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag != DragKind.None)
        {
            _drag = DragKind.None;
            Surface.GuidesX.Clear();
            Surface.GuidesY.Clear();
            Surface.Redraw();
            SyncRibbon();
        }
        e.Pointer.Capture(null);
    }

    /// <summary>Snaps an item edge/centre to the slide edges/centre and to other items, drawing a guide when it does.</summary>
    private double SnapAxis(double pos, double size, double total, bool horizontal)
    {
        double tol = 8 / Math.Max(0.01, Surface.Scale);
        var targets = new List<double> { 0, total / 2, total };
        foreach (var it in CurSlide.Items)
        {
            if (it == _sel) continue;
            if (horizontal) { targets.Add(it.X); targets.Add(it.X + it.W / 2); targets.Add(it.X + it.W); }
            else { targets.Add(it.Y); targets.Add(it.Y + it.H / 2); targets.Add(it.Y + it.H); }
        }
        double bestDelta = double.MaxValue, bestGuide = 0;
        foreach (var feature in new[] { pos, pos + size / 2, pos + size })
            foreach (var t in targets)
            {
                double d = t - feature;
                if (Math.Abs(d) < Math.Abs(bestDelta) && Math.Abs(d) <= tol) { bestDelta = d; bestGuide = t; }
            }
        if (bestDelta == double.MaxValue) return pos;
        (horizontal ? Surface.GuidesX : Surface.GuidesY).Add(bestGuide);
        return pos + bestDelta;
    }

    private void ResizeDrag(double dxSlide, double dySlide, bool shift)
    {
        var it = _sel!;
        double a = -_orot * Math.PI / 180;
        double dx = dxSlide * Math.Cos(a) - dySlide * Math.Sin(a);
        double dy = dxSlide * Math.Sin(a) + dySlide * Math.Cos(a);
        double l = -_ow / 2, r = _ow / 2, t = -_oh / 2, b = _oh / 2;
        bool west = _handle is SlideHandle.NW or SlideHandle.W or SlideHandle.SW;
        bool east = _handle is SlideHandle.NE or SlideHandle.E or SlideHandle.SE;
        bool north = _handle is SlideHandle.NW or SlideHandle.N or SlideHandle.NE;
        bool south = _handle is SlideHandle.SW or SlideHandle.S or SlideHandle.SE;
        if (west) l += dx;
        if (east) r += dx;
        if (north) t += dy;
        if (south) b += dy;
        const double min = 12;
        if (r - l < min) { if (west) l = r - min; else r = l + min; }
        if (b - t < min) { if (north) t = b - min; else b = t + min; }

        bool corner = (west || east) && (north || south);
        if (corner && (shift || it.Type == "image") && _oh > 0)
        {
            double ratio = _ow / _oh, nw = r - l, nh = b - t;
            if (nw / ratio > nh) nh = nw / ratio; else nw = nh * ratio;
            if (west) l = r - nw; else r = l + nw;
            if (north) t = b - nh; else b = t + nh;
        }

        double newW = r - l, newH = b - t, lcx = (l + r) / 2, lcy = (t + b) / 2;
        double ca = Math.Cos(_orot * Math.PI / 180), sa = Math.Sin(_orot * Math.PI / 180);
        double gcx = _ox + _ow / 2 + lcx * ca - lcy * sa, gcy = _oy + _oh / 2 + lcx * sa + lcy * ca;
        it.W = newW;
        it.H = newH;
        it.X = gcx - newW / 2;
        it.Y = gcy - newH / 2;
    }

    // ------------------------------------------------------------ in-place text editing

    private void BeginEdit(SlideItem item)
    {
        if (item.Type == "image") return;
        CommitEdit();
        _editItem = item;
        Surface.HideText = item;
        Surface.Redraw();

        double s = Surface.Scale;
        var bg = SlideRenderer.Parse(CurSlide.Background, SKColors.White);
        var fieldBrush = new SolidColorBrush(Color.FromArgb(235, bg.Red, bg.Green, bg.Blue));
        var fore = new SolidColorBrush(Color.Parse(string.IsNullOrEmpty(item.Color) ? "#000000" : item.Color));
        var box = new TextBox
        {
            Text = item.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily(item.Font),
            FontSize = Math.Max(6, item.Size * s),
            FontWeight = item.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = item.Italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = fore,
            CaretBrush = fore,
            Background = fieldBrush,
            BorderBrush = new SolidColorBrush(Color.Parse("#2B8CFF")),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(12 * s),
            Width = item.W * s,
            Height = item.H * s,
            MinHeight = 24,
            TextAlignment = item.Align switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
            VerticalContentAlignment = item.VAlign switch
            {
                "middle" => Avalonia.Layout.VerticalAlignment.Center,
                "bottom" => Avalonia.Layout.VerticalAlignment.Bottom,
                _ => Avalonia.Layout.VerticalAlignment.Top
            },
            RenderTransform = new RotateTransform(item.Rot)
        };
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            box.Resources[key] = fieldBrush;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            box.Resources[key] = fore;

        Avalonia.Controls.Canvas.SetLeft(box, Surface.Origin.X + item.X * s);
        Avalonia.Controls.Canvas.SetTop(box, Surface.Origin.Y + item.Y * s);
        box.LostFocus += (_, _) => CommitEdit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CommitEdit(); Surface.Focus(); e.Handled = true; }
        };
        _editBox = box;
        Overlay.Children.Add(box);
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.CaretIndex = (box.Text ?? "").Length;
        }, DispatcherPriority.Background);
    }

    private void CommitEdit()
    {
        if (_editBox == null) return;
        var box = _editBox;
        var item = _editItem;
        _editBox = null;
        _editItem = null;
        Overlay.Children.Remove(box);
        Surface.HideText = null;
        var text = box.Text ?? "";
        if (item != null && item.Text != text)
        {
            PushUndo();
            item.Text = text;
            Changed();
        }
        else Surface.Redraw();
    }

    // ------------------------------------------------------------ keyboard

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        string? cmd = null;

        if (ctrl)
        {
            cmd = e.Key switch
            {
                Key.N => "new",
                Key.O => "open",
                Key.S when shift => "saveas",
                Key.S => "save",
                Key.Z => "undo",
                Key.Y => "redo",
                Key.X => "cut",
                Key.C => "copy",
                Key.V => "paste",
                Key.D => "dupitem",
                Key.M => "newslide",
                _ => null
            };
            if (cmd == null && _sel != null && e.Key is Key.B or Key.I or Key.U)
            {
                var it = _sel;
                ApplyToSel(i => { if (e.Key == Key.B) i.Bold = !it.Bold; else if (e.Key == Key.I) i.Italic = !it.Italic; else i.Underline = !it.Underline; });
                e.Handled = true;
                return;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.F5: cmd = shift ? "presentcur" : "present"; break;
                case Key.Delete:
                case Key.Back:
                    cmd = _sel != null ? "delitem" : "delslide";
                    if (_sel == null && !ThumbList.IsKeyboardFocusWithin) cmd = null;
                    break;
                case Key.Escape: Select(null); e.Handled = true; return;
                case Key.PageDown: GoTo(_cur + 1); e.Handled = true; return;
                case Key.PageUp: GoTo(_cur - 1); e.Handled = true; return;
                case Key.Enter:
                case Key.F2:
                    if (_sel != null) { BeginEdit(_sel); e.Handled = true; }
                    return;
                case Key.Tab:
                {
                    var items = CurSlide.Items;
                    if (items.Count > 0)
                    {
                        int i = _sel == null ? -1 : items.IndexOf(_sel);
                        Select(items[(i + (shift ? -1 : 1) + items.Count) % items.Count]);
                    }
                    e.Handled = true;
                    return;
                }
                case Key.Left or Key.Right or Key.Up or Key.Down:
                    if (_sel != null && !ThumbList.IsKeyboardFocusWithin)
                    {
                        Nudge(e.Key, shift ? 10 : 1);
                        e.Handled = true;
                    }
                    return;
            }
        }

        if (cmd == null) return;
        e.Handled = true;
        await Run(cmd);
    }

    private void Nudge(Key key, double amount)
    {
        if (_sel == null) return;
        if ((DateTime.Now - _lastNudge).TotalMilliseconds > 700) PushUndo();
        _lastNudge = DateTime.Now;
        switch (key)
        {
            case Key.Left: _sel.X -= amount; break;
            case Key.Right: _sel.X += amount; break;
            case Key.Up: _sel.Y -= amount; break;
            default: _sel.Y += amount; break;
        }
        MarkModified();
        Surface.Redraw();
        MarkThumb(_cur);
    }
}
