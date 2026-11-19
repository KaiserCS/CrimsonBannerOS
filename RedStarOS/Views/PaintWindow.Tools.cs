using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using RedStarOS.Services;

namespace RedStarOS.Views;

/// <summary>Pointer handling and the drawing tools of Red Star Paint.</summary>
public partial class PaintWindow
{
    private sealed class Sel
    {
        public SKRectI Rect;
        public SKBitmap? Content;          // lifted pixels (null until the selection is first moved/pasted)
        public List<SKPoint>? Poly;        // free-form outline relative to the original top-left
        public int OrigW, OrigH;
        public bool Lifted;
    }

    private enum Drag { None, Define, DefineFree, Move, Resize, CanvasGrip, TextRect }

    private Sel? _sel;
    private Drag _drag;
    private SKPointI _dragStart;
    private SKPoint _dragStartF;
    private SKRectI _dragRect0;
    private Grip _grip;
    private bool _moved, _copyDrag;
    private List<SKPoint>? _freePts;

    // freehand strokes
    private bool _drawing;
    private SKPoint _last;
    private SKColor _strokeColor;
    private List<SKPoint>? _pts;
    private SKBitmap? _base;
    private readonly Random _rnd = new();

    // shapes: 0 idle, 1 dragging, 2 curve waits for bend 1, 3 dragging bend 1, 4 waits for bend 2, 5 dragging bend 2, 6 polygon open
    private int _stage;
    private bool _pressed, _shapeRight;
    private SKPoint _a, _b, _cp1, _cp2;
    private readonly List<SKPoint> _poly = new();
    private SKPoint _cursorPt;

    // text
    private TextBox? _textBox;
    private Point _textStart;

    private static SKPoint Snap(SKPoint p) => new(MathF.Floor(p.X) + 0.5f, MathF.Floor(p.Y) + 0.5f);
    private static SKPoint Centre(SKPoint p) => Snap(p);

    private SKPoint ToImage(Point screen) => new((float)(screen.X / _zoom), (float)(screen.Y / _zoom));
    private static SKPointI Floor(SKPoint p) => new((int)Math.Floor(p.X), (int)Math.Floor(p.Y));
    private ShapeKind SelectedShape() => PaintShapes.All[Math.Max(0, ShapeBox.SelectedIndex)].Kind;
    private BrushKind SelectedBrush() => (BrushKind)Math.Max(0, BrushBox.SelectedIndex);

    // ------------------------------------------------------------ pointer: press

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(Surface).Properties;
        bool left = props.IsLeftButtonPressed, right = props.IsRightButtonPressed;
        if (!left && !right) return;
        var screen = e.GetPosition(Surface);
        var p = ToImage(screen);
        var pi = Floor(p);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        // a curve or polygon that is still being built takes the click
        if (_tool == PaintTool.Shape && _stage != 0)
        {
            ShapePress(p, e.ClickCount, right, e);
            return;
        }

        var grip = left ? Surface.HitGrip(screen) : Grip.None;
        if (grip != Grip.None)
        {
            CommitText();
            FinishShape();
            if (_sel != null)
            {
                Lift(false);
                _drag = Drag.Resize;
                _grip = grip;
                _dragStart = pi;
                _dragRect0 = _sel.Rect;
            }
            else
            {
                _drag = Drag.CanvasGrip;
                _grip = grip;
                _dragStartF = p;
                Surface.Ghost = new PixelSize(_doc.Width, _doc.Height);
            }
            e.Pointer.Capture(Surface);
            return;
        }

        switch (_tool)
        {
            case PaintTool.Pencil:
            case PaintTool.Brush:
            case PaintTool.Eraser:
                BeginStroke(p, right, e);
                break;

            case PaintTool.Fill:
                CommitAll();
                if (pi.X < 0 || pi.Y < 0 || pi.X >= _doc.Width || pi.Y >= _doc.Height) break;
                _doc.PushUndo();
                _doc.FloodFill(pi.X, pi.Y, right ? _c2 : _c1);
                AfterDocChanged(true);
                break;

            case PaintTool.Picker:
                var col = _doc.GetColor(pi.X, pi.Y);
                if (right) _c2 = col; else _c1 = col;
                UpdateSwatches();
                SetTool(_prevTool);
                break;

            case PaintTool.Zoom:
                ZoomStep(left, e.GetPosition(Scroller));
                break;

            case PaintTool.Text:
                CommitAll();
                BeginText(screen);
                e.Pointer.Capture(Surface);
                break;

            case PaintTool.Select:
            case PaintTool.Free:
                if (left && _sel != null && p.X >= _sel.Rect.Left && p.X < _sel.Rect.Right && p.Y >= _sel.Rect.Top && p.Y < _sel.Rect.Bottom)
                {
                    _drag = Drag.Move;
                    _dragStart = pi;
                    _dragRect0 = _sel.Rect;
                    _moved = false;
                    _copyDrag = ctrl;
                }
                else
                {
                    CommitSelection();
                    _dragStart = new SKPointI(Math.Clamp(pi.X, 0, _doc.Width), Math.Clamp(pi.Y, 0, _doc.Height));
                    if (_tool == PaintTool.Select) _drag = Drag.Define;
                    else
                    {
                        _drag = Drag.DefineFree;
                        _freePts = new List<SKPoint> { ClampPoint(p) };
                        Surface.Marquee = _freePts;
                    }
                }
                e.Pointer.Capture(Surface);
                break;

            case PaintTool.Shape:
                CommitAll();
                _doc.PushUndo();
                _base = _doc.Bmp.Copy();
                _a = _b = Snap(p);
                _shapeRight = right;
                _stage = 1;
                _pressed = true;
                if (SelectedShape() == ShapeKind.Polygon) { _poly.Clear(); _poly.Add(_a); _poly.Add(_a); }
                e.Pointer.Capture(Surface);
                break;
        }
    }

    private SKPoint ClampPoint(SKPoint p) =>
        new(Math.Clamp(p.X, 0, _doc.Width), Math.Clamp(p.Y, 0, _doc.Height));

    // ------------------------------------------------------------ pointer: move

    private void OnMove(object? sender, PointerEventArgs e)
    {
        var screen = e.GetPosition(Surface);
        var p = ToImage(screen);
        var pi = Floor(p);
        PosText.Text = pi.X >= 0 && pi.Y >= 0 && pi.X < _doc.Width && pi.Y < _doc.Height ? $"{pi.X}, {pi.Y} px" : "";
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_drawing) { ContinueStroke(p); return; }

        switch (_drag)
        {
            case Drag.Define:
            {
                var c = new SKPointI(Math.Clamp(pi.X, 0, _doc.Width), Math.Clamp(pi.Y, 0, _doc.Height));
                var r = new SKRectI(Math.Min(_dragStart.X, c.X), Math.Min(_dragStart.Y, c.Y), Math.Max(_dragStart.X, c.X), Math.Max(_dragStart.Y, c.Y));
                _sel = r.Width > 0 && r.Height > 0 ? new Sel { Rect = r, OrigW = r.Width, OrigH = r.Height } : null;
                SyncSelection();
                return;
            }
            case Drag.DefineFree:
            {
                var cp = ClampPoint(p);
                var lastPt = _freePts![^1];
                if (Math.Abs(cp.X - lastPt.X) + Math.Abs(cp.Y - lastPt.Y) >= 1) _freePts.Add(cp);
                Surface.InvalidateVisual();
                return;
            }
            case Drag.Move:
            {
                int dx = pi.X - _dragStart.X, dy = pi.Y - _dragStart.Y;
                if (!_moved && (dx != 0 || dy != 0)) { Lift(_copyDrag); _moved = true; }
                if (_moved && _sel != null)
                {
                    var r0 = _dragRect0;
                    _sel.Rect = new SKRectI(r0.Left + dx, r0.Top + dy, r0.Right + dx, r0.Bottom + dy);
                    SyncSelection();
                }
                return;
            }
            case Drag.Resize:
            {
                if (_sel == null) return;
                int dx = pi.X - _dragStart.X, dy = pi.Y - _dragStart.Y;
                var r0 = _dragRect0;
                int l = r0.Left, t = r0.Top, rt = r0.Right, b = r0.Bottom;
                if (_grip is Grip.NW or Grip.W or Grip.SW) l = Math.Min(r0.Left + dx, r0.Right - 1);
                if (_grip is Grip.NE or Grip.E or Grip.SE) rt = Math.Max(r0.Right + dx, r0.Left + 1);
                if (_grip is Grip.NW or Grip.N or Grip.NE) t = Math.Min(r0.Top + dy, r0.Bottom - 1);
                if (_grip is Grip.SW or Grip.S or Grip.SE) b = Math.Max(r0.Bottom + dy, r0.Top + 1);
                if (shift && r0.Width > 0 && r0.Height > 0)
                {
                    double ratio = r0.Width / (double)r0.Height;
                    int w = rt - l;
                    int h = Math.Max(1, (int)Math.Round(w / ratio));
                    if (_grip is Grip.NW or Grip.N or Grip.NE) t = b - h; else b = t + h;
                }
                _sel.Rect = new SKRectI(l, t, rt, b);
                SyncSelection();
                return;
            }
            case Drag.CanvasGrip:
            {
                int dx = (int)Math.Round(p.X - _dragStartF.X), dy = (int)Math.Round(p.Y - _dragStartF.Y);
                int w = _doc.Width + (_grip is Grip.E or Grip.SE ? dx : 0);
                int h = _doc.Height + (_grip is Grip.S or Grip.SE ? dy : 0);
                Surface.Ghost = new PixelSize(Math.Clamp(w, 1, 8000), Math.Clamp(h, 1, 8000));
                Surface.InvalidateVisual();
                SizeText2.Text = $"Canvas: {Surface.Ghost.Value.Width} × {Surface.Ghost.Value.Height} px";
                return;
            }
            case Drag.TextRect:
            {
                if (_textBox == null) return;
                double x = Math.Min(_textStart.X, screen.X), y = Math.Min(_textStart.Y, screen.Y);
                Avalonia.Controls.Canvas.SetLeft(_textBox, x);
                Avalonia.Controls.Canvas.SetTop(_textBox, y);
                _textBox.Width = Math.Max(20, Math.Abs(screen.X - _textStart.X));
                _textBox.Height = Math.Max(20, Math.Abs(screen.Y - _textStart.Y));
                return;
            }
        }

        if (_tool == PaintTool.Shape && _base != null && (_pressed || _stage == 6))
        {
            switch (_stage)
            {
                case 1:
                    _b = Constrain(_a, Snap(p), shift);
                    if (SelectedShape() == ShapeKind.Polygon) _poly[^1] = _b;
                    break;
                case 3: _cp1 = _cp2 = p; break;
                case 5: _cp2 = p; break;
                case 6: _cursorPt = Snap(p); break;
            }
            ShapePreview(false);
            return;
        }

        UpdateCursor(screen, p);
    }

    private SKPoint Constrain(SKPoint a, SKPoint p, bool shift)
    {
        if (!shift) return p;
        float dx = p.X - a.X, dy = p.Y - a.Y;
        if (SelectedShape() is ShapeKind.Line or ShapeKind.Curve or ShapeKind.Polygon)
        {
            double ang = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return new SKPoint(a.X + len * (float)Math.Cos(ang), a.Y + len * (float)Math.Sin(ang));
        }
        float s = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new SKPoint(a.X + (dx < 0 ? -s : s), a.Y + (dy < 0 ? -s : s));
    }

    private void UpdateCursor(Point screen, SKPoint p)
    {
        StandardCursorType t = _tool == PaintTool.Text ? StandardCursorType.Ibeam : StandardCursorType.Cross;
        var g = _stage == 0 ? Surface.HitGrip(screen) : Grip.None;
        if (g != Grip.None)
        {
            t = g switch
            {
                Grip.E or Grip.W => StandardCursorType.SizeWestEast,
                Grip.N or Grip.S => StandardCursorType.SizeNorthSouth,
                Grip.NW => StandardCursorType.TopLeftCorner,
                Grip.NE => StandardCursorType.TopRightCorner,
                Grip.SW => StandardCursorType.BottomLeftCorner,
                _ => StandardCursorType.BottomRightCorner
            };
        }
        else if (_sel != null && _tool is PaintTool.Select or PaintTool.Free &&
                 p.X >= _sel.Rect.Left && p.X < _sel.Rect.Right && p.Y >= _sel.Rect.Top && p.Y < _sel.Rect.Bottom)
            t = StandardCursorType.SizeAll;
        Surface.Cursor = CursorOf(t);
    }

    // ------------------------------------------------------------ pointer: release

    private void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        if (_drawing) EndStroke();

        var drag = _drag;
        _drag = Drag.None;
        switch (drag)
        {
            case Drag.Define:
                UpdateStatus();
                break;
            case Drag.DefineFree:
                FinishFreeSelection();
                break;
            case Drag.Move:
            case Drag.Resize:
                if (_sel != null) MarkModified();
                break;
            case Drag.CanvasGrip:
                var ghost = Surface.Ghost;
                Surface.Ghost = null;
                if (ghost is PixelSize gs && (gs.Width != _doc.Width || gs.Height != _doc.Height))
                {
                    _doc.PushUndo();
                    _doc.ResizeCanvas(gs.Width, gs.Height, _c2);
                    AfterDocChanged(true);
                }
                else { Surface.InvalidateVisual(); UpdateStatus(); }
                break;
            case Drag.TextRect:
                FinishTextRect();
                break;
        }

        if (_pressed)
        {
            _pressed = false;
            var kind = SelectedShape();
            switch (_stage)
            {
                case 1:
                    if (kind == ShapeKind.Curve) { _cp1 = _a; _cp2 = _b; _stage = 2; }
                    else if (kind == ShapeKind.Polygon) { _stage = 6; _cursorPt = _b; }
                    else if (Math.Abs(_b.X - _a.X) < 1 && Math.Abs(_b.Y - _a.Y) < 1) CancelShape();
                    else EndShape();
                    break;
                case 3: _stage = 4; break;
                case 5: EndShape(); break;
            }
        }
        e.Pointer.Capture(null);
    }

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drawing) EndStroke();
        if (_drag is Drag.Define or Drag.DefineFree or Drag.CanvasGrip)
        {
            Surface.Ghost = null;
            Surface.Marquee = null;
            _drag = Drag.None;
            Surface.InvalidateVisual();
        }
    }

    // ------------------------------------------------------------ freehand strokes

    private void BeginStroke(SKPoint p, bool right, PointerPressedEventArgs e)
    {
        CommitAll();
        _doc.PushUndo();
        _drawing = true;
        _strokeColor = right ? _c2 : _c1;
        _last = p;
        if (_tool == PaintTool.Brush && SelectedBrush() == BrushKind.Marker)
        {
            _base = _doc.Bmp.Copy();
            _pts = new List<SKPoint> { p, p };
            DrawMarker();
        }
        else StrokeSegment(p, p, true);
        e.Pointer.Capture(Surface);
        RequestRefresh();
    }

    private void ContinueStroke(SKPoint p)
    {
        if (_pts != null) { _pts.Add(p); DrawMarker(); }
        else StrokeSegment(_last, p, false);
        _last = p;
        RequestRefresh();
    }

    private void EndStroke()
    {
        _drawing = false;
        _pts = null;
        _base?.Dispose();
        _base = null;
        MarkModified();
        RequestRefresh();
    }

    private void DrawMarker()
    {
        if (_base == null || _pts == null) return;
        _doc.Restore(_base);
        using var c = new SKCanvas(_doc.Bmp);
        using var path = PaintShapes.Polyline(_pts, false);
        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke, IsAntialias = true, Color = _strokeColor.WithAlpha(110),
            StrokeWidth = Math.Max(2f, (float)SizeSlider.Value * 1.6f), StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round
        };
        c.DrawPath(path, paint);
    }

    private void StrokeSegment(SKPoint a, SKPoint b, bool first)
    {
        float size = (float)SizeSlider.Value;
        using var c = new SKCanvas(_doc.Bmp);
        float dist = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        int steps = Math.Max(1, (int)Math.Ceiling(dist));

        if (_tool == PaintTool.Pencil)
        {
            if (first)
            {
                using var fill = new SKPaint { Color = _strokeColor, IsAntialias = false, Style = SKPaintStyle.Fill };
                var f = Floor(a);
                c.DrawRect(new SKRect(f.X, f.Y, f.X + 1, f.Y + 1), fill);
            }
            else
            {
                using var line = new SKPaint { Color = _strokeColor, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                c.DrawLine(Centre(a), Centre(b), line);
            }
            return;
        }

        if (_tool == PaintTool.Eraser)
        {
            float es = Math.Max(4f, size * 2);
            using var fill = new SKPaint { Color = _c2, IsAntialias = false, Style = SKPaintStyle.Fill };
            for (int i = first ? 0 : 1; i <= (first ? 0 : steps); i++)
            {
                float t = first ? 0 : i / (float)steps;
                float x = MathF.Round(a.X + (b.X - a.X) * t - es / 2), y = MathF.Round(a.Y + (b.Y - a.Y) * t - es / 2);
                c.DrawRect(new SKRect(x, y, x + es, y + es), fill);
            }
            return;
        }

        switch (SelectedBrush())
        {
            case BrushKind.Round:
            {
                if (first)
                {
                    using var fill = new SKPaint { Color = _strokeColor, IsAntialias = true, Style = SKPaintStyle.Fill };
                    c.DrawCircle(a.X, a.Y, size / 2, fill);
                }
                else
                {
                    using var line = new SKPaint
                    { Color = _strokeColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = size, StrokeCap = SKStrokeCap.Round };
                    c.DrawLine(a, b, line);
                }
                break;
            }
            case BrushKind.Square:
            {
                using var fill = new SKPaint { Color = _strokeColor, IsAntialias = false, Style = SKPaintStyle.Fill };
                for (int i = 0; i <= (first ? 0 : steps); i++)
                {
                    float t = first ? 0 : i / (float)steps;
                    float x = MathF.Round(a.X + (b.X - a.X) * t - size / 2), y = MathF.Round(a.Y + (b.Y - a.Y) * t - size / 2);
                    c.DrawRect(new SKRect(x, y, x + size, y + size), fill);
                }
                break;
            }
            case BrushKind.Calligraphy:
            {
                var nib = new SKPoint(size * 0.5f, -size * 0.5f);
                using var fill = new SKPaint { Color = _strokeColor, IsAntialias = true, Style = SKPaintStyle.Fill };
                using var path = new SKPath();
                path.MoveTo(a.X + nib.X, a.Y + nib.Y);
                path.LineTo(b.X + nib.X, b.Y + nib.Y);
                path.LineTo(b.X - nib.X, b.Y - nib.Y);
                path.LineTo(a.X - nib.X, a.Y - nib.Y);
                path.Close();
                c.DrawPath(path, fill);
                using var edge = new SKPaint { Color = _strokeColor, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
                c.DrawLine(a.X - nib.X, a.Y - nib.Y, a.X + nib.X, a.Y + nib.Y, edge);
                c.DrawLine(b.X - nib.X, b.Y - nib.Y, b.X + nib.X, b.Y + nib.Y, edge);
                break;
            }
            case BrushKind.Airbrush:
            case BrushKind.Crayon:
            {
                bool crayon = SelectedBrush() == BrushKind.Crayon;
                using var dot = new SKPaint { Color = _strokeColor, IsAntialias = false, Style = SKPaintStyle.Fill };
                float radius = crayon ? size * 0.8f + 1 : size * 1.4f + 2;
                int n = first ? 1 : Math.Max(1, (int)(dist / 2));
                int dots = crayon ? (int)(size * 2) + 6 : (int)(size * 1.2f) + 3;
                float d = crayon ? 2f : 1f;
                for (int i = 0; i < n; i++)
                {
                    float t = n == 1 ? 0 : i / (float)(n - 1);
                    float cx = a.X + (b.X - a.X) * t, cy = a.Y + (b.Y - a.Y) * t;
                    for (int k = 0; k < dots; k++)
                    {
                        if (crayon && _rnd.NextDouble() < 0.35) continue;
                        double ang = _rnd.NextDouble() * Math.PI * 2, rr = Math.Sqrt(_rnd.NextDouble()) * radius;
                        float x = MathF.Floor(cx + (float)(Math.Cos(ang) * rr)), y = MathF.Floor(cy + (float)(Math.Sin(ang) * rr));
                        c.DrawRect(new SKRect(x, y, x + d, y + d), dot);
                    }
                }
                break;
            }
        }
    }

    // ------------------------------------------------------------ shapes

    private void ShapePress(SKPoint p, int clickCount, bool right, PointerPressedEventArgs e)
    {
        switch (_stage)
        {
            case 2: _stage = 3; _cp1 = _cp2 = p; _pressed = true; e.Pointer.Capture(Surface); ShapePreview(false); break;
            case 4: _stage = 5; _cp2 = p; _pressed = true; e.Pointer.Capture(Surface); ShapePreview(false); break;
            case 6:
                var pt = Snap(p);
                bool nearStart = _poly.Count > 2 && Math.Abs(pt.X - _poly[0].X) * _zoom < 6 && Math.Abs(pt.Y - _poly[0].Y) * _zoom < 6;
                if (clickCount >= 2 || right || nearStart) FinishShape();
                else { _poly.Add(pt); _cursorPt = pt; ShapePreview(false); }
                break;
        }
    }

    private void ShapePreview(bool closedFinal)
    {
        if (_base == null) return;
        _doc.Restore(_base);
        var fg = _shapeRight ? _c2 : _c1;
        var bg = _shapeRight ? _c1 : _c2;
        var kind = SelectedShape();
        var mode = (FillMode)Math.Max(0, FillBox.SelectedIndex);
        float size = (float)SizeSlider.Value;
        bool smooth = SmoothBox.IsChecked == true;
        using var c = new SKCanvas(_doc.Bmp);
        switch (kind)
        {
            case ShapeKind.Line:
                using (var path = PaintShapes.Line(_a, _b)) PaintShapes.Paint(c, path, fg, bg, mode, size, smooth, false);
                break;
            case ShapeKind.Curve:
                using (var path = PaintShapes.Curve(_a, _b, _stage >= 3 ? _cp1 : _a, _stage >= 3 ? _cp2 : _b))
                    PaintShapes.Paint(c, path, fg, bg, mode, size, smooth, false);
                break;
            case ShapeKind.Polygon:
            {
                var pts = new List<SKPoint>(_poly);
                if (_stage == 6 && !closedFinal) pts.Add(_cursorPt);
                using var path = PaintShapes.Polyline(pts, closedFinal);
                PaintShapes.Paint(c, path, fg, bg, mode, size, smooth, closedFinal && pts.Count > 2);
                break;
            }
            default:
            {
                var r = new SKRect(Math.Min(_a.X, _b.X), Math.Min(_a.Y, _b.Y), Math.Max(_a.X, _b.X), Math.Max(_a.Y, _b.Y));
                using var path = PaintShapes.Box(kind, r);
                PaintShapes.Paint(c, path, fg, bg, mode, size, smooth, true);
                break;
            }
        }
        RequestRefresh();
    }

    /// <summary>Accepts whatever shape is in progress (open polygons are closed).</summary>
    private void FinishShape()
    {
        if (_stage == 0) return;
        if (SelectedShape() == ShapeKind.Polygon && _poly.Count > 2) ShapePreview(true);
        else if (SelectedShape() == ShapeKind.Polygon) { CancelShape(); return; }
        EndShape();
    }

    private void EndShape()
    {
        _stage = 0;
        _pressed = false;
        _base?.Dispose();
        _base = null;
        _poly.Clear();
        MarkModified();
        AfterDocChanged(true);
    }

    private void CancelShape()
    {
        if (_stage == 0) return;
        if (_base != null) _doc.Restore(_base);
        _doc.DiscardUndo();
        CancelShapeSilently();
        AfterDocChanged(false);
    }

    private void CancelShapeSilently()
    {
        _stage = 0;
        _pressed = false;
        _base?.Dispose();
        _base = null;
        _poly.Clear();
    }

    // ------------------------------------------------------------ text

    private string TextFamily => FontBox.SelectedItem as string ?? "Sans";
    private float TextPoints => FontSizeBox.SelectedItem is int n ? n : 14;

    private void BeginText(Point screen)
    {
        _textStart = screen;
        _textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.CornflowerBlue,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            MinWidth = 20, MinHeight = 20,
            Width = 20, Height = 20,
            Foreground = new SolidColorBrush(ToAv(_c1)),
            CaretBrush = new SolidColorBrush(ToAv(_c1))
        };
        Avalonia.Controls.Canvas.SetLeft(_textBox, screen.X);
        Avalonia.Controls.Canvas.SetTop(_textBox, screen.Y);
        Overlay.Children.Add(_textBox);
        ApplyTextStyle();
        _drag = Drag.TextRect;
    }

    private void FinishTextRect()
    {
        if (_textBox == null) return;
        if (_textBox.Width < 40) _textBox.Width = Math.Max(160, 40);
        if (_textBox.Height < 30) _textBox.Height = Math.Max(70, 30);
        var box = _textBox;
        Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Background);
    }

    private void ApplyTextStyle()
    {
        if (_textBox == null) return;
        _textBox.FontFamily = new FontFamily(TextFamily);
        _textBox.FontSize = Math.Max(4, TextPoints * 96.0 / 72.0 * _zoom);
        _textBox.FontWeight = BoldBtn.IsChecked == true ? FontWeight.Bold : FontWeight.Normal;
        _textBox.FontStyle = ItalicBtn.IsChecked == true ? FontStyle.Italic : FontStyle.Normal;
    }

    private void CommitText()
    {
        if (_textBox == null) return;
        var box = _textBox;
        _textBox = null;
        _drag = _drag == Drag.TextRect ? Drag.None : _drag;
        Overlay.Children.Remove(box);
        var text = box.Text ?? "";
        if (text.Trim().Length == 0) return;
        float x = (float)(Avalonia.Controls.Canvas.GetLeft(box) / _zoom), y = (float)(Avalonia.Controls.Canvas.GetTop(box) / _zoom);
        float w = (float)(box.Width / _zoom), h = (float)(box.Height / _zoom);
        _doc.PushUndo();
        using (var c = new SKCanvas(_doc.Bmp))
            PaintDoc.DrawText(c, text, new SKRect(x, y, x + w, y + h), TextFamily, TextPoints * 96f / 72f,
                BoldBtn.IsChecked == true, ItalicBtn.IsChecked == true, UnderBtn.IsChecked == true,
                _c1, OpaqueBox.IsChecked == true ? _c2 : null);
        AfterDocChanged(true);
    }

    private void CancelText()
    {
        if (_textBox == null) return;
        Overlay.Children.Remove(_textBox);
        _textBox = null;
        if (_drag == Drag.TextRect) _drag = Drag.None;
    }

    // ------------------------------------------------------------ selections

    private void CommitAll()
    {
        CommitText();
        FinishShape();
        CommitSelection();
    }

    private void SyncSelection()
    {
        Surface.SelRect = _sel?.Rect;
        Surface.SelPoly = _sel?.Poly;
        Surface.SelOrigW = _sel?.OrigW ?? 1;
        Surface.SelOrigH = _sel?.OrigH ?? 1;
        Surface.InvalidateVisual();
        UpdateStatus();
    }

    private SKBitmap Effective(out bool owned)
    {
        var c = _sel!.Content!;
        if (TransparentBox.IsChecked == true) { owned = true; return PaintDoc.KeyOut(c, _c2); }
        owned = false;
        return c;
    }

    private void RefreshFloat()
    {
        if (_sel is not { Lifted: true, Content: not null }) { Surface.SetFloat(null); return; }
        var eff = Effective(out var owned);
        Surface.SetFloat(eff);
        if (owned) eff.Dispose();
    }

    private SKBitmap MakeContent(Sel s)
    {
        var r = s.Rect;
        var dst = PaintDoc.Create(r.Width, r.Height, SKColors.Transparent);
        using var c = new SKCanvas(dst);
        if (s.Poly != null)
        {
            using var path = PaintShapes.Polyline(s.Poly, true);
            c.ClipPath(path, SKClipOperation.Intersect, false);
        }
        c.DrawBitmap(_doc.Bmp, -r.Left, -r.Top);
        return dst;
    }

    /// <summary>Picks the selected pixels up so they can be moved; leaves a hole in colour 2 unless copying.</summary>
    private void Lift(bool keepOriginal)
    {
        if (_sel == null || _sel.Lifted) return;
        _doc.PushUndo();
        _sel.Content = MakeContent(_sel);
        if (!keepOriginal)
        {
            var r = _sel.Rect;
            using var c = new SKCanvas(_doc.Bmp);
            if (_sel.Poly != null)
            {
                using var path = PaintShapes.Polyline(_sel.Poly, true);
                path.Transform(SKMatrix.CreateTranslation(r.Left, r.Top));
                c.ClipPath(path, SKClipOperation.Intersect, false);
                c.DrawColor(_c2);
            }
            else
            {
                using var fill = new SKPaint { Color = _c2, Style = SKPaintStyle.Fill, IsAntialias = false };
                c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), fill);
            }
        }
        _sel.Lifted = true;
        RefreshFloat();
        RequestRefresh();
    }

    private void CommitSelection()
    {
        if (_sel == null) return;
        var s = _sel;
        _sel = null;
        if (s.Lifted && s.Content != null)
        {
            var eff = TransparentBox.IsChecked == true ? PaintDoc.KeyOut(s.Content, _c2) : s.Content;
            using (var c = new SKCanvas(_doc.Bmp))
            {
                var r = s.Rect;
                if (r.Width == eff.Width && r.Height == eff.Height) c.DrawBitmap(eff, r.Left, r.Top);
                else
                {
                    using var p = new SKPaint { IsAntialias = true };
                    c.DrawBitmapSmooth(eff, new SKRect(r.Left, r.Top, r.Right, r.Bottom), p);
                }
            }
            if (!ReferenceEquals(eff, s.Content)) eff.Dispose();
            MarkModified();
        }
        s.Content?.Dispose();
        Surface.SetFloat(null);
        SyncSelection();
        Surface.Refresh();
    }

    /// <summary>Forgets the selection without drawing it back (undo, delete).</summary>
    private void DropSelection()
    {
        if (_sel == null) { Surface.SetFloat(null); return; }
        _sel.Content?.Dispose();
        _sel = null;
        Surface.SetFloat(null);
        SyncSelection();
    }

    private void FinishFreeSelection()
    {
        var pts = _freePts;
        _freePts = null;
        Surface.Marquee = null;
        if (pts == null || pts.Count < 3) { Surface.InvalidateVisual(); return; }
        float l = pts.Min(q => q.X), t = pts.Min(q => q.Y), r = pts.Max(q => q.X), b = pts.Max(q => q.Y);
        var rect = new SKRectI((int)Math.Floor(l), (int)Math.Floor(t), (int)Math.Ceiling(r), (int)Math.Ceiling(b));
        if (rect.Width < 1 || rect.Height < 1) { Surface.InvalidateVisual(); return; }
        _sel = new Sel
        {
            Rect = rect, OrigW = rect.Width, OrigH = rect.Height,
            Poly = pts.Select(q => new SKPoint(q.X - rect.Left, q.Y - rect.Top)).ToList()
        };
        SyncSelection();
    }

    private void SelectAll()
    {
        SetTool(PaintTool.Select);
        CommitSelection();
        _sel = new Sel { Rect = new SKRectI(0, 0, _doc.Width, _doc.Height), OrigW = _doc.Width, OrigH = _doc.Height };
        SyncSelection();
    }

    private void CopySelection()
    {
        if (_sel == null) return;
        _clip?.Dispose();
        _clip = _sel.Lifted && _sel.Content != null ? _sel.Content.Copy() : MakeContent(_sel);
    }

    private void PasteClip()
    {
        if (_clip == null) return;
        BeginPaste(_clip.Copy());
    }

    private void BeginPaste(SKBitmap bmp)
    {
        SetTool(PaintTool.Select);
        CommitAll();
        _doc.PushUndo();
        if (bmp.Width > _doc.Width || bmp.Height > _doc.Height)
            _doc.ResizeCanvas(Math.Max(bmp.Width, _doc.Width), Math.Max(bmp.Height, _doc.Height), _c2);
        int x = Math.Clamp((int)(Scroller.Offset.X / _zoom), 0, Math.Max(0, _doc.Width - bmp.Width));
        int y = Math.Clamp((int)(Scroller.Offset.Y / _zoom), 0, Math.Max(0, _doc.Height - bmp.Height));
        _sel = new Sel
        {
            Rect = new SKRectI(x, y, x + bmp.Width, y + bmp.Height), Content = bmp,
            OrigW = bmp.Width, OrigH = bmp.Height, Lifted = true
        };
        RefreshFloat();
        SyncSelection();
        AfterDocChanged(true);
    }

    private void DeleteSelection()
    {
        if (_sel == null) return;
        if (_sel.Lifted) { DropSelection(); MarkModified(); Surface.Refresh(); return; }
        _doc.PushUndo();
        var r = _sel.Rect;
        using (var c = new SKCanvas(_doc.Bmp))
        {
            if (_sel.Poly != null)
            {
                using var path = PaintShapes.Polyline(_sel.Poly, true);
                path.Transform(SKMatrix.CreateTranslation(r.Left, r.Top));
                c.ClipPath(path, SKClipOperation.Intersect, false);
                c.DrawColor(_c2);
            }
            else
            {
                using var fill = new SKPaint { Color = _c2, Style = SKPaintStyle.Fill, IsAntialias = false };
                c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom), fill);
            }
        }
        DropSelection();
        AfterDocChanged(true);
    }

    private void CropToSelection()
    {
        if (_sel == null) return;
        var r = _sel.Rect;
        CommitSelection();
        r = new SKRectI(Math.Clamp(r.Left, 0, _doc.Width), Math.Clamp(r.Top, 0, _doc.Height),
                        Math.Clamp(r.Right, 0, _doc.Width), Math.Clamp(r.Bottom, 0, _doc.Height));
        if (r.Width < 1 || r.Height < 1) return;
        _doc.PushUndo();
        _doc.Replace(PaintDoc.CopyRegion(_doc.Bmp, r));
        AfterDocChanged(true);
    }
}
