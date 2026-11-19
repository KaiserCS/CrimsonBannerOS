using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using RedStarOS.Services;
using Avalonia.Input.Platform;

namespace RedStarOS.Views;

/// <summary>
/// Virtualised spreadsheet grid: only the visible cells are drawn, so 100,000 rows x 702 columns cost nothing.
/// Handles selection, keyboard navigation, resizing, freeze panes, the fill handle and the clipboard.
/// Cell editing itself is delegated to the host window through <see cref="EditRequested"/>.
/// </summary>
public sealed class SheetGrid : Control
{
    public const double RowHeaderW = 54;
    public const double ColHeaderH = 26;
    private const double FontSize = 13;

    private SheetModel _m = new();
    private double[] _colX = new double[SheetModel.MaxCols + 1];
    private double[] _rowY = new double[SheetModel.MaxRows + 1];

    public double ScrollX { get; private set; }
    public double ScrollY { get; private set; }

    public int AncR { get; private set; }
    public int AncC { get; private set; }
    public int CurR { get; private set; }
    public int CurC { get; private set; }

    public int SelR1 => Math.Min(AncR, CurR);
    public int SelR2 => Math.Max(AncR, CurR);
    public int SelC1 => Math.Min(AncC, CurC);
    public int SelC2 => Math.Max(AncC, CurC);

    public event Action<int, int, string?>? EditRequested;
    public event Action? SelectionChanged;
    public event Action? MetricsChanged;
    public event Action<char>? ShortcutRequested;

    private enum Drag { None, Select, SelectCols, SelectRows, ResizeCol, ResizeRow, Fill }
    private Drag _drag = Drag.None;
    private int _resizeIndex;
    private double _resizeStart, _resizeOrigSize;
    private (int r1, int c1, int r2, int c2)? _fillTo;
    private (int r1, int c1, int r2, int c2)? _copyRect;

    private Cell?[,]? _clipSnap;
    private string? _clipTsv;
    private int _clipR, _clipC;
    private bool _clipCut;

    private static readonly Cursor CurEw = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CurNs = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor CurArrow = new(StandardCursorType.Arrow);
    private static readonly Cursor CurCross = new(StandardCursorType.Cross);

    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();

    public SheetGrid()
    {
        Focusable = true;
        ClipToBounds = true;
        Attach(_m);
    }

    public SheetModel Model => _m;

    public void Attach(SheetModel model)
    {
        _m = model;
        _m.Changed += () => { InvalidateVisual(); SelectionChanged?.Invoke(); };
        _m.LayoutChanged += () => { RebuildLayout(); ClampScroll(); InvalidateVisual(); };
        RebuildLayout();
        AncR = AncC = CurR = CurC = 0;
        ScrollX = ScrollY = 0;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ layout maths

    private void RebuildLayout()
    {
        for (int c = 0; c < SheetModel.MaxCols; c++) _colX[c + 1] = _colX[c] + _m.ColWidth(c);
        for (int r = 0; r < SheetModel.MaxRows; r++) _rowY[r + 1] = _rowY[r] + _m.RowHeight(r);
    }

    private double FrozenW => _colX[_m.FrozenCols];
    private double FrozenH => _rowY[_m.FrozenRows];

    public double ColW(int c) => _colX[c + 1] - _colX[c];
    public double RowH(int r) => _rowY[r + 1] - _rowY[r];

    public double ViewX(int c)
    {
        int fc = _m.FrozenCols;
        return c < fc ? RowHeaderW + _colX[c] : RowHeaderW + FrozenW + _colX[c] - _colX[fc] - ScrollX;
    }

    public double ViewY(int r)
    {
        int fr = _m.FrozenRows;
        return r < fr ? ColHeaderH + _rowY[r] : ColHeaderH + FrozenH + _rowY[r] - _rowY[fr] - ScrollY;
    }

    public Rect CellRect(int r, int c) => new Rect(ViewX(c), ViewY(r), ColW(c), RowH(r));

    private int ExtentRows => Math.Min(SheetModel.MaxRows, Math.Max(Math.Max(_m.UsedRows + 60, CurR + 40), 200));
    private int ExtentCols => Math.Min(SheetModel.MaxCols, Math.Max(Math.Max(_m.UsedCols + 8, CurC + 4), 26));

    private double ViewW => Math.Max(0, Bounds.Width - RowHeaderW - FrozenW);
    private double ViewH => Math.Max(0, Bounds.Height - ColHeaderH - FrozenH);

    public double MaxScrollX => Math.Max(0, _colX[ExtentCols] - _colX[_m.FrozenCols] - ViewW);
    public double MaxScrollY => Math.Max(0, _rowY[ExtentRows] - _rowY[_m.FrozenRows] - ViewH);
    public double ViewportW => ViewW;
    public double ViewportH => ViewH;
    public double ExtentW => Math.Max(0, _colX[ExtentCols] - _colX[_m.FrozenCols]);
    public double ExtentH => Math.Max(0, _rowY[ExtentRows] - _rowY[_m.FrozenRows]);

    private void ClampScroll()
    {
        ScrollX = Math.Max(0, Math.Min(ScrollX, MaxScrollX));
        ScrollY = Math.Max(0, Math.Min(ScrollY, MaxScrollY));
        MetricsChanged?.Invoke();
    }

    public void SetScroll(double x, double y)
    {
        ScrollX = x; ScrollY = y;
        ClampScroll();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) ClampScroll();
    }

    /// <summary>Largest index i in [0, n-1] with a[i] &lt;= v.</summary>
    private static int Locate(double[] a, double v, int n)
    {
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] <= v) lo = mid + 1; else hi = mid;
        }
        return Math.Max(0, Math.Min(n - 1, lo - 1));
    }

    private int ColAtX(double x)
    {
        if (x < RowHeaderW) return -1;
        double xx = x - RowHeaderW;
        int fc = _m.FrozenCols;
        if (fc > 0 && xx < FrozenW) return Math.Min(fc - 1, Locate(_colX, xx, SheetModel.MaxCols));
        return Math.Max(fc, Locate(_colX, xx - FrozenW + ScrollX + _colX[fc], SheetModel.MaxCols));
    }

    private int RowAtY(double y)
    {
        if (y < ColHeaderH) return -1;
        double yy = y - ColHeaderH;
        int fr = _m.FrozenRows;
        if (fr > 0 && yy < FrozenH) return Math.Min(fr - 1, Locate(_rowY, yy, SheetModel.MaxRows));
        return Math.Max(fr, Locate(_rowY, yy - FrozenH + ScrollY + _rowY[fr], SheetModel.MaxRows));
    }

    private void Visible(out int r0, out int r1, out int c0, out int c1)
    {
        int fr = _m.FrozenRows, fc = _m.FrozenCols;
        double top = _rowY[fr] + ScrollY, left = _colX[fc] + ScrollX;
        r0 = Math.Max(fr, Locate(_rowY, top, SheetModel.MaxRows));
        r1 = Math.Min(SheetModel.MaxRows, Locate(_rowY, top + ViewH, SheetModel.MaxRows) + 1);
        c0 = Math.Max(fc, Locate(_colX, left, SheetModel.MaxCols));
        c1 = Math.Min(SheetModel.MaxCols, Locate(_colX, left + ViewW, SheetModel.MaxCols) + 1);
    }

    // ------------------------------------------------------------------ selection

    public (int r1, int c1, int r2, int c2) SelectionForEdit()
    {
        int r1 = SelR1, r2 = SelR2, c1 = SelC1, c2 = SelC2;
        if (r2 - r1 > 5000) r2 = Math.Max(r1, _m.UsedRows - 1);
        if (c2 - c1 > 100) c2 = Math.Max(c1, _m.UsedCols - 1);
        return (r1, c1, r2, c2);
    }

    public void Select(int r, int c, bool extend)
    {
        r = Math.Max(0, Math.Min(SheetModel.MaxRows - 1, r));
        c = Math.Max(0, Math.Min(SheetModel.MaxCols - 1, c));
        CurR = r; CurC = c;
        if (!extend) { AncR = r; AncC = c; }
        EnsureVisible(CurR, CurC);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void SelectRange(int r1, int c1, int r2, int c2)
    {
        AncR = r1; AncC = c1; CurR = r2; CurC = c2;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void EnsureVisible(int r, int c)
    {
        int fr = _m.FrozenRows, fc = _m.FrozenCols;
        if (r >= fr)
        {
            double top = _rowY[r] - _rowY[fr], bottom = _rowY[r + 1] - _rowY[fr];
            if (top < ScrollY) ScrollY = top;
            else if (bottom > ScrollY + ViewH) ScrollY = bottom - ViewH;
        }
        if (c >= fc)
        {
            double left = _colX[c] - _colX[fc], right = _colX[c + 1] - _colX[fc];
            if (left < ScrollX) ScrollX = left;
            else if (right > ScrollX + ViewW) ScrollX = right - ViewW;
        }
        ClampScroll();
    }

    private bool InRange(int r, int c) => r >= 0 && c >= 0 && r < SheetModel.MaxRows && c < SheetModel.MaxCols;

    private (int, int) Jump(int r, int c, int dr, int dc)
    {
        int nr = r + dr, nc = c + dc;
        if (!InRange(nr, nc)) return (r, c);
        if (!_m.IsEmpty(r, c) && !_m.IsEmpty(nr, nc))
        {
            while (InRange(r + dr, c + dc) && !_m.IsEmpty(r + dr, c + dc)) { r += dr; c += dc; }
            return (r, c);
        }
        r = nr; c = nc;
        while (InRange(r + dr, c + dc) && _m.IsEmpty(r, c)) { r += dr; c += dc; }
        return (r, c);
    }

    private void Move(int dr, int dc, bool extend, bool jump)
    {
        int r = CurR, c = CurC;
        if (jump) (r, c) = Jump(r, c, dr, dc);
        else { r += dr; c += dc; }
        r = Math.Max(0, Math.Min(SheetModel.MaxRows - 1, r));
        c = Math.Max(0, Math.Min(SheetModel.MaxCols - 1, c));
        // skip rows hidden by a filter
        while (_m.HiddenRows.Contains(r) && dr != 0 && r + Math.Sign(dr) >= 0 && r + Math.Sign(dr) < SheetModel.MaxRows) r += Math.Sign(dr);
        Select(r, c, extend);
    }

    private int PageRows()
    {
        int n = (int)(ViewH / SheetModel.DefaultRowHeight) - 1;
        return Math.Max(1, n);
    }

    // ------------------------------------------------------------------ brushes

    private bool Dark => ActualThemeVariant == ThemeVariant.Dark;

    private SolidColorBrush Brush(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var b))
        {
            b = new SolidColorBrush(Color.FromUInt32(argb));
            _brushes[argb] = b;
        }
        return b;
    }

    private static readonly Color Accent = Color.FromRgb(0x2F, 0x7B, 0xF5);

    // ------------------------------------------------------------------ rendering

    private FormattedText Text(string s, CellFormat? f, IBrush brush, double size = FontSize, bool forceBold = false)
    {
        var tf = new Typeface(FontFamily.Default,
            f != null && f.Italic ? FontStyle.Italic : FontStyle.Normal,
            (f != null && f.Bold) || forceBold ? FontWeight.Bold : FontWeight.Normal);
        return new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush);
    }

    private static double Pix(double v) => Math.Floor(v) + 0.5;

    public override void Render(DrawingContext ctx)
    {
        double W = Bounds.Width, H = Bounds.Height;
        bool dark = Dark;
        var bg = Brush(dark ? 0xFF1E1F24u : 0xFFFFFFFFu);
        ctx.DrawRectangle(bg, null, new Rect(0, 0, W, H));
        if (W < 10 || H < 10) return;

        int fr = _m.FrozenRows, fc = _m.FrozenCols;
        Visible(out int r0, out int r1, out int c0, out int c1);
        double hx = RowHeaderW, hy = ColHeaderH;
        double fx = hx + FrozenW, fy = hy + FrozenH;

        // four panes: frozen corner, frozen rows, frozen columns, scrolling body
        if (fr > 0 && fc > 0) DrawPane(ctx, 0, fr, 0, fc, new Rect(hx, hy, FrozenW, FrozenH));
        if (fr > 0) DrawPane(ctx, 0, fr, c0, c1, new Rect(fx, hy, Math.Max(0, W - fx), FrozenH));
        if (fc > 0) DrawPane(ctx, r0, r1, 0, fc, new Rect(hx, fy, FrozenW, Math.Max(0, H - fy)));
        DrawPane(ctx, r0, r1, c0, c1, new Rect(fx, fy, Math.Max(0, W - fx), Math.Max(0, H - fy)));

        DrawHeaders(ctx, r0, r1, c0, c1);

        // freeze lines
        var freezePen = new Pen(Brush(dark ? 0xFF8A8D99u : 0xFF7A7D88u), 2);
        if (fr > 0) ctx.DrawLine(freezePen, new Point(hx, fy), new Point(W, fy));
        if (fc > 0) ctx.DrawLine(freezePen, new Point(fx, hy), new Point(fx, H));
    }

    private bool PaneRect(int qr0, int qr1, int qc0, int qc1, int r1, int c1, int r2, int c2, out Rect rect)
    {
        rect = default;
        int rs = Math.Max(r1, qr0), re = Math.Min(r2, qr1 - 1), cs = Math.Max(c1, qc0), ce = Math.Min(c2, qc1 - 1);
        if (rs > re || cs > ce) return false;
        double x0 = ViewX(cs), x1 = ViewX(ce) + ColW(ce), y0 = ViewY(rs), y1 = ViewY(re) + RowH(re);
        rect = new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        return true;
    }

    private void DrawPane(DrawingContext ctx, int rA, int rB, int cA, int cB, Rect clip)
    {
        if (clip.Width <= 0 || clip.Height <= 0 || rA >= rB || cA >= cB) return;
        bool dark = Dark;
        var gridPen = new Pen(Brush(dark ? 0xFF35373Fu : 0xFFDADCE2u), 1);
        var defaultText = Brush(dark ? 0xFFE8E8ECu : 0xFF1B1B1Fu);
        var errBrush = Brush(0xFFD64545u);

        using (ctx.PushClip(clip))
        {
            // grid lines
            for (int r = rA; r < rB; r++)
            {
                double h = RowH(r);
                if (h <= 0) continue;
                double y = Pix(ViewY(r) + h);
                ctx.DrawLine(gridPen, new Point(clip.Left, y), new Point(clip.Right, y));
            }
            for (int c = cA; c < cB; c++)
            {
                double x = Pix(ViewX(c) + ColW(c));
                ctx.DrawLine(gridPen, new Point(x, clip.Top), new Point(x, clip.Bottom));
            }

            // fills first, then borders and text
            for (int r = rA; r < rB; r++)
            {
                double h = RowH(r);
                if (h <= 0) continue;
                double y = ViewY(r);
                for (int c = cA; c < cB; c++)
                {
                    var f = _m.GetFmt(r, c);
                    if (f == null || f.Fill == 0) continue;
                    ctx.DrawRectangle(Brush(f.Fill), null, new Rect(ViewX(c), y, ColW(c), h));
                }
            }

            for (int r = rA; r < rB; r++)
            {
                double h = RowH(r);
                if (h <= 0) continue;
                double y = ViewY(r);
                for (int c = cA; c < cB; c++)
                {
                    double x = ViewX(c), w = ColW(c);
                    var f = _m.GetFmt(r, c);

                    if (f != null && f.Borders != 0)
                    {
                        var bp = new Pen(Brush(dark ? 0xFFD0D0D8u : 0xFF202024u), 1);
                        if ((f.Borders & 1) != 0) ctx.DrawLine(bp, new Point(Pix(x), y), new Point(Pix(x), y + h));
                        if ((f.Borders & 2) != 0) ctx.DrawLine(bp, new Point(x, Pix(y)), new Point(x + w, Pix(y)));
                        if ((f.Borders & 4) != 0) ctx.DrawLine(bp, new Point(Pix(x + w), y), new Point(Pix(x + w), y + h));
                        if ((f.Borders & 8) != 0) ctx.DrawLine(bp, new Point(x, Pix(y + h)), new Point(x + w, Pix(y + h)));
                    }

                    if (_m.IsEmpty(r, c)) continue;
                    string text = _m.Display(r, c, out bool isNum, out bool isErr, out bool isBool);
                    if (text.Length == 0) continue;

                    IBrush fg;
                    if (isErr) fg = errBrush;
                    else if (f != null && f.Color != 0) fg = Brush(f.Color);
                    else if (f != null && f.Fill != 0) fg = Brush(Luma(f.Fill) > 0.55 ? 0xFF1B1B1Fu : 0xFFF2F2F5u);
                    else fg = defaultText;

                    var ft = Text(text, f, fg);
                    byte align = f?.Align ?? 0;
                    if (align == 0) align = isNum ? (byte)3 : (isErr || isBool ? (byte)2 : (byte)1);

                    if (isNum && ft.Width > w - 6)
                    {
                        ft = Text("###", f, fg);
                    }

                    double avail = w;
                    if (align == 1 && !isNum)
                    {
                        // text may spill into empty neighbours on the right
                        for (int cc = c + 1; cc < cB && _m.IsEmpty(r, cc) && avail < ft.Width + 8; cc++) avail += ColW(cc);
                    }

                    double tx = align == 1 ? x + 4 : (align == 2 ? x + (avail - ft.Width) / 2 : x + w - ft.Width - 4);
                    double ty = y + (h - ft.Height) / 2;
                    using (ctx.PushClip(new Rect(x + 1, y + 1, Math.Max(0, avail - 1), Math.Max(0, h - 1))))
                    {
                        ctx.DrawText(ft, new Point(tx, ty));
                        if (f != null && f.Underline)
                            ctx.DrawLine(new Pen(fg, 1), new Point(tx, Pix(ty + ft.Height)), new Point(tx + ft.Width, Pix(ty + ft.Height)));
                    }
                }
            }

            DrawSelection(ctx, rA, rB, cA, cB);
        }
    }

    private static double Luma(uint argb)
    {
        double r = (argb >> 16 & 0xFF) / 255.0, g = (argb >> 8 & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    private void DrawSelection(DrawingContext ctx, int rA, int rB, int cA, int cB)
    {
        var tint = new SolidColorBrush(Color.FromArgb(0x30, Accent.R, Accent.G, Accent.B));
        var line = new Pen(new SolidColorBrush(Accent), 2);

        if (PaneRect(rA, rB, cA, cB, SelR1, SelC1, SelR2, SelC2, out var sel))
        {
            bool multi = SelR1 != SelR2 || SelC1 != SelC2;
            if (multi) ctx.DrawRectangle(tint, null, sel);
            ctx.DrawRectangle(null, line, new Rect(sel.X, sel.Y, Math.Max(1, sel.Width), Math.Max(1, sel.Height)));

            // fill handle in the corner of the selection
            int hr = SelR2, hc = SelC2;
            if (hr >= rA && hr < rB && hc >= cA && hc < cB)
            {
                var hrect = new Rect(sel.Right - 4, sel.Bottom - 4, 8, 8);
                ctx.DrawRectangle(new SolidColorBrush(Accent), new Pen(Brush(Dark ? 0xFF1E1F24u : 0xFFFFFFFFu), 1), hrect);
            }
        }

        if (_copyRect.HasValue)
        {
            var cr = _copyRect.Value;
            if (PaneRect(rA, rB, cA, cB, cr.r1, cr.c1, cr.r2, cr.c2, out var rect))
                ctx.DrawRectangle(null, new Pen(Brush(Dark ? 0xFFFFFFFFu : 0xFF000000u), 1.5, DashStyle.Dash), rect);
        }

        if (_fillTo.HasValue)
        {
            var ft = _fillTo.Value;
            if (PaneRect(rA, rB, cA, cB, ft.r1, ft.c1, ft.r2, ft.c2, out var rect))
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Accent), 1.5, DashStyle.Dash), rect);
        }
    }

    private void DrawHeaders(DrawingContext ctx, int r0, int r1, int c0, int c1)
    {
        bool dark = Dark;
        var headBg = Brush(dark ? 0xFF2A2C33u : 0xFFF1F2F5u);
        var headSel = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x55 : (byte)0x40, Accent.R, Accent.G, Accent.B));
        var headFg = Brush(dark ? 0xFFB8BAC4u : 0xFF555862u);
        var border = new Pen(Brush(dark ? 0xFF44464Fu : 0xFFC9CBD3u), 1);
        double W = Bounds.Width, H = Bounds.Height;
        int fr = _m.FrozenRows, fc = _m.FrozenCols;

        ctx.DrawRectangle(headBg, null, new Rect(0, 0, RowHeaderW, ColHeaderH));
        ctx.DrawLine(border, new Point(0, Pix(ColHeaderH)), new Point(W, Pix(ColHeaderH)));
        ctx.DrawLine(border, new Point(Pix(RowHeaderW), 0), new Point(Pix(RowHeaderW), H));

        void ColHead(int from, int to, Rect clip)
        {
            using (ctx.PushClip(clip))
            {
                for (int c = from; c < to; c++)
                {
                    double x = ViewX(c), w = ColW(c);
                    bool sel = c >= SelC1 && c <= SelC2;
                    var rect = new Rect(x, 0, w, ColHeaderH);
                    ctx.DrawRectangle(headBg, null, rect);
                    if (sel) ctx.DrawRectangle(headSel, null, rect);
                    var ft = Text(FormulaEvaluator.ColumnName(c), null, headFg, 12, sel);
                    ctx.DrawText(ft, new Point(x + (w - ft.Width) / 2, (ColHeaderH - ft.Height) / 2));
                    ctx.DrawLine(border, new Point(Pix(x + w), 0), new Point(Pix(x + w), ColHeaderH));
                }
            }
        }

        void RowHead(int from, int to, Rect clip)
        {
            using (ctx.PushClip(clip))
            {
                for (int r = from; r < to; r++)
                {
                    double h = RowH(r);
                    if (h <= 0) continue;
                    double y = ViewY(r);
                    bool sel = r >= SelR1 && r <= SelR2;
                    var rect = new Rect(0, y, RowHeaderW, h);
                    ctx.DrawRectangle(headBg, null, rect);
                    if (sel) ctx.DrawRectangle(headSel, null, rect);
                    var ft = Text((r + 1).ToString(CultureInfo.InvariantCulture), null, headFg, 12, sel);
                    ctx.DrawText(ft, new Point((RowHeaderW - ft.Width) / 2, y + (h - ft.Height) / 2));
                    ctx.DrawLine(border, new Point(0, Pix(y + h)), new Point(RowHeaderW, Pix(y + h)));
                }
            }
        }

        if (fc > 0) ColHead(0, fc, new Rect(RowHeaderW, 0, FrozenW, ColHeaderH));
        ColHead(c0, c1, new Rect(RowHeaderW + FrozenW, 0, Math.Max(0, W - RowHeaderW - FrozenW), ColHeaderH));
        if (fr > 0) RowHead(0, fr, new Rect(0, ColHeaderH, RowHeaderW, FrozenH));
        RowHead(r0, r1, new Rect(0, ColHeaderH + FrozenH, RowHeaderW, Math.Max(0, H - ColHeaderH - FrozenH)));
    }

    // ------------------------------------------------------------------ pointer

    private (int, int) CellAtClamped(Point p)
    {
        double x = Math.Max(RowHeaderW + 1, Math.Min(Bounds.Width - 1, p.X));
        double y = Math.Max(ColHeaderH + 1, Math.Min(Bounds.Height - 1, p.Y));
        return (RowAtY(y), ColAtX(x));
    }

    private bool OverFillHandle(Point p)
    {
        int r = SelR2, c = SelC2;
        double x = ViewX(c) + ColW(c), y = ViewY(r) + RowH(r);
        return Math.Abs(p.X - x) <= 6 && Math.Abs(p.Y - y) <= 6 && p.X > RowHeaderW && p.Y > ColHeaderH;
    }

    private bool ColBorderAt(Point p, out int col)
    {
        col = -1;
        if (p.Y >= ColHeaderH || p.X <= RowHeaderW) return false;
        int c = ColAtX(p.X);
        if (c < 0) return false;
        double right = ViewX(c) + ColW(c), left = ViewX(c);
        if (Math.Abs(p.X - right) <= 4) { col = c; return true; }
        if (c > 0 && Math.Abs(p.X - left) <= 4) { col = c - 1; return true; }
        return false;
    }

    private bool RowBorderAt(Point p, out int row)
    {
        row = -1;
        if (p.X >= RowHeaderW || p.Y <= ColHeaderH) return false;
        int r = RowAtY(p.Y);
        if (r < 0) return false;
        double bottom = ViewY(r) + RowH(r), top = ViewY(r);
        if (Math.Abs(p.Y - bottom) <= 4) { row = r; return true; }
        if (r > 0 && Math.Abs(p.Y - top) <= 4) { row = r - 1; return true; }
        return false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        Focus();
        var p = e.GetPosition(this);
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        if (ColBorderAt(p, out int bc))
        {
            if (e.ClickCount >= 2) { AutoFitColumn(bc); return; }
            _drag = Drag.ResizeCol; _resizeIndex = bc; _resizeStart = p.X; _resizeOrigSize = ColW(bc);
            e.Pointer.Capture(this);
            return;
        }
        if (RowBorderAt(p, out int br))
        {
            _drag = Drag.ResizeRow; _resizeIndex = br; _resizeStart = p.Y; _resizeOrigSize = RowH(br);
            e.Pointer.Capture(this);
            return;
        }
        if (OverFillHandle(p))
        {
            _drag = Drag.Fill; _fillTo = null;
            e.Pointer.Capture(this);
            return;
        }

        if (p.X < RowHeaderW && p.Y < ColHeaderH)
        {
            SelectRange(0, 0, SheetModel.MaxRows - 1, SheetModel.MaxCols - 1);
            return;
        }
        if (p.Y < ColHeaderH)
        {
            int c = ColAtX(p.X);
            if (c < 0) return;
            int anchor = shift ? AncC : c;
            AncR = 0; AncC = anchor; CurR = SheetModel.MaxRows - 1; CurC = c;
            _drag = Drag.SelectCols;
            e.Pointer.Capture(this);
            InvalidateVisual(); SelectionChanged?.Invoke();
            return;
        }
        if (p.X < RowHeaderW)
        {
            int r = RowAtY(p.Y);
            if (r < 0) return;
            int anchor = shift ? AncR : r;
            AncR = anchor; AncC = 0; CurR = r; CurC = SheetModel.MaxCols - 1;
            _drag = Drag.SelectRows;
            e.Pointer.Capture(this);
            InvalidateVisual(); SelectionChanged?.Invoke();
            return;
        }

        var (row, col) = (RowAtY(p.Y), ColAtX(p.X));
        if (row < 0 || col < 0) return;
        if (e.ClickCount >= 2)
        {
            Select(row, col, false);
            EditRequested?.Invoke(row, col, null);
            return;
        }
        Select(row, col, shift);
        _drag = Drag.Select;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        switch (_drag)
        {
            case Drag.None:
                if (ColBorderAt(p, out _)) Cursor = CurEw;
                else if (RowBorderAt(p, out _)) Cursor = CurNs;
                else if (OverFillHandle(p)) Cursor = CurCross;
                else Cursor = CurArrow;
                return;

            case Drag.Select:
                {
                    AutoScroll(p);
                    var (r, c) = CellAtClamped(p);
                    if (r != CurR || c != CurC)
                    {
                        CurR = r; CurC = c;
                        InvalidateVisual(); SelectionChanged?.Invoke();
                    }
                    return;
                }
            case Drag.SelectCols:
                {
                    AutoScroll(new Point(p.X, ColHeaderH + 5));
                    int c = ColAtX(Math.Max(RowHeaderW + 1, Math.Min(Bounds.Width - 1, p.X)));
                    if (c >= 0 && c != CurC) { CurC = c; InvalidateVisual(); SelectionChanged?.Invoke(); }
                    return;
                }
            case Drag.SelectRows:
                {
                    AutoScroll(new Point(RowHeaderW + 5, p.Y));
                    int r = RowAtY(Math.Max(ColHeaderH + 1, Math.Min(Bounds.Height - 1, p.Y)));
                    if (r >= 0 && r != CurR) { CurR = r; InvalidateVisual(); SelectionChanged?.Invoke(); }
                    return;
                }
            case Drag.ResizeCol:
                {
                    double w = Math.Max(20, _resizeOrigSize + (p.X - _resizeStart));
                    _m.ColWidths[_resizeIndex] = Math.Round(w);
                    RebuildLayout(); ClampScroll(); InvalidateVisual();
                    return;
                }
            case Drag.ResizeRow:
                {
                    double h = Math.Max(10, _resizeOrigSize + (p.Y - _resizeStart));
                    _m.RowHeights[_resizeIndex] = Math.Round(h);
                    RebuildLayout(); ClampScroll(); InvalidateVisual();
                    return;
                }
            case Drag.Fill:
                {
                    AutoScroll(p);
                    var (r, c) = CellAtClamped(p);
                    int r1 = SelR1, r2 = SelR2, c1 = SelC1, c2 = SelC2;
                    int dr = r < r1 ? r - r1 : (r > r2 ? r - r2 : 0);
                    int dc = c < c1 ? c - c1 : (c > c2 ? c - c2 : 0);
                    if (dr == 0 && dc == 0) _fillTo = null;
                    else if (Math.Abs(dr) >= Math.Abs(dc)) _fillTo = (Math.Min(r1, r), c1, Math.Max(r2, r), c2);
                    else _fillTo = (r1, Math.Min(c1, c), r2, Math.Max(c2, c));
                    InvalidateVisual();
                    return;
                }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.Fill && _fillTo.HasValue)
        {
            var t = _fillTo.Value;
            _fillTo = null;
            _m.Fill(SelR1, SelC1, SelR2, SelC2, t.r1, t.c1, t.r2, t.c2);
            SelectRange(t.r1, t.c1, t.r2, t.c2);
        }
        else if (_drag == Drag.ResizeCol || _drag == Drag.ResizeRow)
        {
            _m.RaiseLayoutChanged();
        }
        _drag = Drag.None;
        _fillTo = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private void AutoScroll(Point p)
    {
        double dx = 0, dy = 0;
        if (p.X > Bounds.Width - 12) dx = 28; else if (p.X < RowHeaderW + FrozenW + 6) dx = -28;
        if (p.Y > Bounds.Height - 12) dy = 28; else if (p.Y < ColHeaderH + FrozenH + 6) dy = -28;
        if (dx != 0 || dy != 0) SetScroll(ScrollX + dx, ScrollY + dy);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        double dy = -e.Delta.Y * SheetModel.DefaultRowHeight * 3;
        double dx = -e.Delta.X * 60;
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && dx == 0) { dx = dy * 1.5; dy = 0; }
        SetScroll(ScrollX + dx, ScrollY + dy);
        e.Handled = true;
    }

    public void AutoFitColumn(int c)
    {
        double best = 40;
        int rows = Math.Min(_m.UsedRows, 3000);
        for (int r = 0; r < rows; r++)
        {
            if (_m.IsEmpty(r, c)) continue;
            string text = _m.Display(r, c, out _, out _, out _);
            var ft = Text(text, _m.GetFmt(r, c), Brushes.Black);
            best = Math.Max(best, ft.Width + 16);
        }
        _m.ColWidths[c] = Math.Min(500, Math.Ceiling(best));
        _m.RaiseLayoutChanged();
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        EditRequested?.Invoke(AncR, AncC, e.Text);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        e.Handled = true;

        switch (e.Key)
        {
            case Key.Left: Move(0, -1, shift, ctrl); return;
            case Key.Right: Move(0, 1, shift, ctrl); return;
            case Key.Up: Move(-1, 0, shift, ctrl); return;
            case Key.Down: Move(1, 0, shift, ctrl); return;
            case Key.PageDown: Move(PageRows(), 0, shift, false); return;
            case Key.PageUp: Move(-PageRows(), 0, shift, false); return;
            case Key.Home:
                if (ctrl) Select(0, 0, shift); else Select(CurR, 0, shift);
                return;
            case Key.End:
                if (ctrl) Select(Math.Max(0, _m.UsedRows - 1), Math.Max(0, _m.UsedCols - 1), shift);
                else Select(CurR, Math.Max(0, _m.UsedCols - 1), shift);
                return;
            case Key.Tab:
                {
                    // Tab walks right inside a multi-cell selection, wrapping to the next row
                    Move(0, shift ? -1 : 1, false, false);
                    return;
                }
            case Key.Enter:
                Move(shift ? -1 : 1, 0, false, false);
                return;
            case Key.F2:
                EditRequested?.Invoke(AncR, AncC, null);
                return;
            case Key.Delete:
            case Key.Back:
                {
                    var s = SelectionForEdit();
                    _m.ClearContents(s.r1, s.c1, s.r2, s.c2);
                    return;
                }
            case Key.Escape:
                _copyRect = null; _clipCut = false;
                InvalidateVisual();
                return;
        }

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C: Copy(false); return;
                case Key.X: Copy(true); return;
                case Key.V: Paste(); return;
                case Key.Z: _m.Undo(); return;
                case Key.Y: _m.Redo(); return;
                case Key.A: SelectRange(0, 0, SheetModel.MaxRows - 1, SheetModel.MaxCols - 1); return;
                case Key.B: ShortcutRequested?.Invoke('B'); return;
                case Key.I: ShortcutRequested?.Invoke('I'); return;
                case Key.U: ShortcutRequested?.Invoke('U'); return;
                case Key.D:
                    if (SelR2 > SelR1) { _m.Fill(SelR1, SelC1, SelR1, SelC2, SelR1, SelC1, SelR2, SelC2); InvalidateVisual(); }
                    return;
                case Key.R:
                    if (SelC2 > SelC1) { _m.Fill(SelR1, SelC1, SelR2, SelC1, SelR1, SelC1, SelR2, SelC2); InvalidateVisual(); }
                    return;
            }
        }

        e.Handled = false;
    }

    // ------------------------------------------------------------------ clipboard

    public async void Copy(bool cut)
    {
        var s = SelectionForEdit();
        int r1 = s.r1, c1 = s.c1, r2 = s.r2, c2 = s.c2;
        // a selection beyond the data only needs the part that holds something
        if (r1 >= _m.UsedRows && c1 >= _m.UsedCols) { r2 = r1; c2 = c1; }
        var tsv = _m.RangeToTsv(r1, c1, r2, c2);
        _clipTsv = tsv;
        _clipSnap = _m.Snapshot(r1, c1, r2, c2);
        _clipR = r1; _clipC = c1; _clipCut = cut;
        _copyRect = (r1, c1, r2, c2);
        InvalidateVisual();
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb != null)
        {
            try { await cb.SetTextAsync(tsv); } catch (Exception) { /* clipboard unavailable */ }
        }
    }

    public async void Paste()
    {
        string? text = null;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb != null)
        {
            try { text = await cb.TryGetTextAsync(); } catch (Exception) { /* ignore */ }
        }
        int r0 = SelR1, c0 = SelC1;

        if (_clipSnap != null && (string.IsNullOrEmpty(text) || text == _clipTsv))
        {
            var snap = _clipSnap;
            int rows = snap.GetLength(0), cols = snap.GetLength(1);
            _m.BeginEdit();
            if (_clipCut)
            {
                _m.ClearContents(_clipR, _clipC, _clipR + rows - 1, _clipC + cols - 1);
                _m.PasteSnapshot(snap, r0, c0, r0, c0);   // moved, not copied: references stay as they were
            }
            else _m.PasteSnapshot(snap, _clipR, _clipC, r0, c0);
            _m.EndEdit();
            if (_clipCut) { _clipSnap = null; _clipTsv = null; _copyRect = null; _clipCut = false; }
            SelectRange(r0, c0, Math.Min(SheetModel.MaxRows - 1, r0 + rows - 1), Math.Min(SheetModel.MaxCols - 1, c0 + cols - 1));
        }
        else if (!string.IsNullOrEmpty(text))
        {
            var (rows, cols) = _m.PasteText(r0, c0, text);
            SelectRange(r0, c0, Math.Min(SheetModel.MaxRows - 1, r0 + Math.Max(1, rows) - 1), Math.Min(SheetModel.MaxCols - 1, c0 + Math.Max(1, cols) - 1));
        }
        InvalidateVisual();
    }
}
