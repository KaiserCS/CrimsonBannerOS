using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;
using RedStarOS.Services;
using Avalonia.Input.Platform;

namespace RedStarOS.Views;

/// <summary>One open document: content, file, undo history and where the caret and scroll were.</summary>
public sealed class DocTab
{
    public RichDoc Doc { get; set; } = RichDoc.Empty();
    public string? Path { get; set; }
    public bool Dirty { get; set; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public int CaretPara, CaretOff, AnchorPara, AnchorOff;
    public double ScrollY;
    public sealed class Snap { public RichDoc Doc = null!; public int C, Co, A, Ao; }
    public List<Snap> Undo { get; } = new();
    public List<Snap> Redo { get; } = new();
    public string Title => Path == null ? "Untitled" : System.IO.Path.GetFileName(Path);
}

public readonly record struct DocPos(Para P, int Off);

public readonly record struct CharFormat(bool Bold, bool Italic, bool Underline, bool Strike, bool Sup, bool Sub,
    string? Font, double Size, string? Color, string? Highlight, string? Link);

/// <summary>
/// The rich-text editing surface for Red Star Writer: lays out the document with Avalonia's text engine,
/// draws caret/selection/tables/images, and implements editing, formatting, undo and find.
/// </summary>
public sealed class DocView : Control
{
    // ── layout bookkeeping ──
    private sealed class PL
    {
        public TextLayout? Layout;
        public int Ver = -1;
        public double LayoutW = -1;
        public double X, Y, W, H, LineH;
        public Rect Cell;          // table cell rectangle (doc coordinates), empty for top-level paragraphs
        public string? Marker;
        public double MarkerX;
    }

    private sealed class BL
    {
        public Block B = null!;
        public double Y, H;
        public double X, W;                 // images/tables
        public List<double> RowY = new();   // tables: top of each row, plus bottom at the end
        public List<double> ColX = new();
        public Bitmap? Bmp;
    }

    private Dictionary<Para, PL> _pl = new();
    private readonly List<BL> _bl = new();
    private List<Para> _flat = new();
    private readonly Dictionary<Para, int> _flatIndex = new();
    private readonly Dictionary<Para, (Block block, int r, int c)> _where = new();
    private readonly Dictionary<Block, Bitmap?> _bitmaps = new();
    private double _contentH;

    public DocTab Tab { get; private set; } = new();
    public bool PageView { get; private set; } = true;
    public bool Letter { get; private set; }
    public bool PageNumbers { get; set; } = true;
    private double _zoom = 1.0;
    public double Zoom { get => _zoom; set { _zoom = Math.Clamp(value, 0.5, 2.5); InvalidateMeasure(); InvalidateVisual(); ZoomChanged?.Invoke(); } }

    public event Action? Changed;
    public event Action? SelectionChanged;
    public event Action? ZoomChanged;
    public event Action<string>? OpenLinkRequested;

    private DocPos _caret, _anchor;
    private Block? _selImage;
    private CharFormat? _pending;
    private double _goalX = -1;
    private bool _caretOn = true;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private Rect _viewport = new(0, 0, 4000, 4000);
    private double _ox = 20, _oy = 20;
    private bool _dragging;
    private string _lastKind = "";
    private long _lastKindAt;
    private List<(Para p, int s, int e)> _matches = new();
    private int _matchCur = -1;
    private static List<Para>? _richClip;
    private static string? _richClipText;

    public static readonly string DefaultFont = "Noto Sans, DejaVu Sans, Liberation Sans, sans-serif";

    public DocView()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ClipToBounds = true;
        _blink.Tick += (_, _) => { _caretOn = !_caretOn; InvalidateVisual(); };
        _blink.Start();
        EffectiveViewportChanged += (_, e) => { _viewport = e.EffectiveViewport; InvalidateVisual(); };
        SetTab(new DocTab());
    }

    // ───────────────────────── tab / mode ─────────────────────────
    public void SetTab(DocTab tab)
    {
        Tab = tab;
        tab.Doc.EnsureValid();
        _bitmaps.Clear();
        _matches.Clear(); _matchCur = -1;
        Reindex();
        _selImage = null;
        _caret = PosFromFlat(tab.CaretPara, tab.CaretOff);
        _anchor = PosFromFlat(tab.AnchorPara, tab.AnchorOff);
        _pending = null;
        InvalidateAll();
    }

    public void SaveViewState()
    {
        Tab.CaretPara = _flatIndex.GetValueOrDefault(_caret.P); Tab.CaretOff = _caret.Off;
        Tab.AnchorPara = _flatIndex.GetValueOrDefault(_anchor.P); Tab.AnchorOff = _anchor.Off;
    }

    public void SetPageMode(bool page, bool letter)
    {
        PageView = page; Letter = letter;
        InvalidateAll();
    }

    private DocPos PosFromFlat(int idx, int off)
    {
        if (_flat.Count == 0) { Reindex(); }
        idx = Math.Clamp(idx, 0, _flat.Count - 1);
        var p = _flat[idx];
        return new DocPos(p, Math.Clamp(off, 0, p.Length));
    }

    private void InvalidateAll()
    {
        foreach (var v in _pl.Values) v.Ver = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Reindex()
    {
        Tab.Doc.EnsureValid();
        _flat = Tab.Doc.AllParas().ToList();
        _flatIndex.Clear(); _where.Clear();
        for (int i = 0; i < _flat.Count; i++) _flatIndex[_flat[i]] = i;
        foreach (var b in Tab.Doc.Blocks)
        {
            if (b.Kind == BlockKind.Para) _where[b.Para!] = (b, -1, -1);
            else if (b.Kind == BlockKind.Table)
                for (int r = 0; r < b.Rows!.Count; r++)
                    for (int c = 0; c < b.Rows[r].Count; c++) _where[b.Rows[r][c]] = (b, r, c);
        }
        if (_caret.P == null || !_flatIndex.ContainsKey(_caret.P)) _caret = new DocPos(_flat[0], 0);
        if (_anchor.P == null || !_flatIndex.ContainsKey(_anchor.P)) _anchor = _caret;
    }

    // ───────────────────────── geometry ─────────────────────────
    private double SheetW => PageView ? (Letter ? 816 : 794) : 900;
    private double PageH => Letter ? 1056 : 1123;
    private double MarginX => PageView ? 96 : 48;
    private double MarginY => PageView ? 96 : 40;
    private double ContentW => SheetW - 2 * MarginX;
    private const double Pt = 96.0 / 72.0;

    private double SizePx(Para p, Run? r)
    {
        var pt = r != null && r.Size > 0 ? r.Size : RichDoc.DefaultSize(p.Style);
        return pt * Pt;
    }

    private static IBrush BrushOf(string? hex, IBrush fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        return Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : fallback;
    }

    private static string BaseFont(Para p) =>
        p.Style == PStyle.Code ? "DejaVu Sans Mono, Liberation Mono, monospace" : DefaultFont;

    private TextRunProperties MakeProps(Para p, Run r)
    {
        var bold = r.Bold || RichDoc.DefaultBold(p.Style);
        var italic = r.Italic || RichDoc.DefaultItalic(p.Style);
        var fam = new FontFamily(string.IsNullOrEmpty(r.Font) ? BaseFont(p) : r.Font + "," + BaseFont(p));
        var tf = new Typeface(fam, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal);
        double size = SizePx(p, r);
        var deco = new TextDecorationCollection();
        if (r.Underline || r.Link != null) deco.Add(new TextDecoration { Location = TextDecorationLocation.Underline });
        if (r.Strike) deco.Add(new TextDecoration { Location = TextDecorationLocation.Strikethrough });
        IBrush fg = r.Link != null && r.Color == null ? new SolidColorBrush(Color.Parse("#0B57D0"))
                    : BrushOf(r.Color, p.Style == PStyle.Quote ? new SolidColorBrush(Color.Parse("#555555")) : Brushes.Black);
        var align = r.Sup ? BaselineAlignment.Superscript : r.Sub ? BaselineAlignment.Subscript : BaselineAlignment.Baseline;
        if (r.Sup || r.Sub) size *= 0.75;
        return new GenericTextRunProperties(tf, size, deco.Count > 0 ? deco : null, fg, null, align);
    }

    private double MaxSizePx(Para p)
    {
        double m = p.Runs.Count == 0 ? SizePx(p, null) : 0;
        foreach (var r in p.Runs) m = Math.Max(m, SizePx(p, r));
        return m;
    }

    private (double left, double markerX) ParaIndent(Para p)
    {
        double left = 0, mk = 0;
        if (p.List != ListKind.None) { left = 28 * (p.Level + 1); mk = left - 24; }
        else if (p.Level > 0) left = 28 * p.Level;
        if (p.Style == PStyle.Quote) left += 16;
        return (left, mk);
    }

    private PL EnsureLayout(Para p, double width)
    {
        if (!_pl.TryGetValue(p, out var pl)) { pl = new PL(); _pl[p] = pl; }
        if (pl.Ver == p.Version && Math.Abs(pl.LayoutW - width) < 0.01) return pl;
        pl.Ver = p.Version; pl.LayoutW = width;
        var text = p.Text;
        double maxPx = MaxSizePx(p);
        double lh = double.NaN;
        double lineH = maxPx * 1.3;
        if (Math.Abs(p.Spacing - 1.0) > 0.01) { lh = lineH * p.Spacing; lineH = lh; }
        pl.LineH = lineH;
        if (text.Length == 0)
        {
            pl.Layout = null;
            pl.H = lineH;
        }
        else
        {
            var first = p.Runs[0];
            var fp = (GenericTextRunProperties)MakeProps(p, first);
            var spans = new List<ValueSpan<TextRunProperties>>();
            int pos = 0;
            foreach (var r in p.Runs)
            {
                if (r.Text.Length == 0) continue;
                spans.Add(new ValueSpan<TextRunProperties>(pos, r.Text.Length, MakeProps(p, r)));
                pos += r.Text.Length;
            }
            var ta = p.Align switch { Align.Center => TextAlignment.Center, Align.Right => TextAlignment.Right, Align.Justify => TextAlignment.Justify, _ => TextAlignment.Left };
            pl.Layout = new TextLayout(text, fp.Typeface, fp.FontRenderingEmSize, Brushes.Black,
                textAlignment: ta, textWrapping: TextWrapping.Wrap, maxWidth: Math.Max(20, width), lineHeight: lh,
                textStyleOverrides: spans);
            pl.H = Math.Max(pl.Layout.Height, lineH);
        }
        return pl;
    }

    private bool _layoutDirty = true;

    private void Relayout()
    {
        var doc = Tab.Doc;
        var fresh = new Dictionary<Para, PL>();
        double top = MarginY, y = top;
        double L = MarginX, W = ContentW;
        _bl.Clear();
        var counters = new int[12];
        foreach (var b in doc.Blocks)
        {
            var bl = new BL { B = b, Y = y };
            switch (b.Kind)
            {
                case BlockKind.Para:
                {
                    var p = b.Para!;
                    var (left, mk) = ParaIndent(p);
                    // numbering
                    if (p.List == ListKind.Number)
                    {
                        int lv = Math.Clamp(p.Level, 0, 11);
                        counters[lv]++;
                        for (int k = lv + 1; k < 12; k++) counters[k] = 0;
                    }
                    else if (p.List == ListKind.None) Array.Clear(counters);
                    var pl = EnsureLayout(p, W - left);
                    fresh[p] = pl;
                    y += RichDoc.SpaceBefore(p.Style) * Pt * (b == doc.Blocks[0] ? 0 : 1);
                    pl.X = L + left; pl.Y = y; pl.W = W - left; pl.Cell = default;
                    pl.Marker = p.List == ListKind.Bullet ? (p.Level % 2 == 0 ? "•" : "◦") : p.List == ListKind.Number ? counters[Math.Clamp(p.Level, 0, 11)] + "." : null;
                    pl.MarkerX = L + mk;
                    bl.Y = y; bl.H = pl.H;
                    y += pl.H + RichDoc.SpaceAfter(p.Style) * Pt * (p.Style == PStyle.Code ? 0.2 : 1);
                    break;
                }
                case BlockKind.Table:
                {
                    Array.Clear(counters);
                    var rows = b.Rows!;
                    int cols = rows.Count == 0 ? 1 : rows.Max(r => r.Count);
                    double cw = W / cols;
                    const double pad = 6;
                    bl.X = L; bl.W = W; bl.ColX.Clear(); bl.RowY.Clear();
                    for (int c = 0; c <= cols; c++) bl.ColX.Add(L + c * cw);
                    double ty = y;
                    foreach (var row in rows)
                    {
                        bl.RowY.Add(ty);
                        double rh = 0;
                        foreach (var cell in row) { var pl = EnsureLayout(cell, cw - 2 * pad); rh = Math.Max(rh, pl.H); }
                        rh += 2 * pad;
                        for (int c = 0; c < row.Count; c++)
                        {
                            var pl = _pl[row[c]];
                            fresh[row[c]] = pl;
                            pl.X = L + c * cw + pad; pl.Y = ty + pad; pl.W = cw - 2 * pad;
                            pl.Cell = new Rect(L + c * cw, ty, cw, rh);
                            pl.Marker = null;
                        }
                        ty += rh;
                    }
                    bl.RowY.Add(ty);
                    bl.H = ty - y;
                    y = ty + 10;
                    break;
                }
                case BlockKind.Image:
                {
                    Array.Clear(counters);
                    if (!_bitmaps.TryGetValue(b, out var bmp))
                    {
                        try { bmp = b.Image != null ? new Bitmap(new MemoryStream(b.Image)) : null; } catch { bmp = null; }
                        _bitmaps[b] = bmp;
                    }
                    bl.Bmp = bmp;
                    double w = b.ImgW > 0 ? b.ImgW : (bmp?.Size.Width ?? 100);
                    double h = b.ImgH > 0 ? b.ImgH : (bmp?.Size.Height ?? 100);
                    if (w > W) { h *= W / w; w = W; }
                    bl.W = w; bl.H = h;
                    bl.X = b.ImgAlign == Align.Center ? L + (W - w) / 2 : b.ImgAlign == Align.Right ? L + W - w : L;
                    y += h + 8;
                    break;
                }
                case BlockKind.Rule:
                    Array.Clear(counters);
                    bl.H = 1; y += 18;
                    break;
                case BlockKind.PageBreak:
                    Array.Clear(counters);
                    bl.H = 18;
                    if (PageView)
                    {
                        double page = Math.Floor((y - 1) / PageH) + 1;
                        y = Math.Max(y + 18, page * PageH + MarginY);
                    }
                    else y += 18;
                    break;
            }
            _bl.Add(bl);
        }
        _pl = fresh;
        _contentH = Math.Max(y + MarginY - 20, PageView ? PageH : 400);
        if (PageView) _contentH = Math.Ceiling(_contentH / PageH) * PageH;
        _layoutDirty = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Relayout();
        return new Size(SheetW * _zoom + 40, _contentH * _zoom + 40);
    }

    private void RelayoutNow()
    {
        double before = _contentH;
        Relayout();
        if (Math.Abs(before - _contentH) > 0.5) InvalidateMeasure();
        InvalidateVisual();
    }

    // ───────────────────────── rendering ─────────────────────────
    private Point ToDoc(Point p) => new((p.X - _ox) / _zoom, (p.Y - _oy) / _zoom);

    public override void Render(DrawingContext ctx)
    {
        if (_layoutDirty) Relayout();
        _ox = Math.Max(20, (Bounds.Width - SheetW * _zoom) / 2);
        _oy = 20;
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#E6E6E6")), new Rect(Bounds.Size));
        using var tr = ctx.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_ox, _oy));

        var sheet = new Rect(0, 0, SheetW, _contentH);
        ctx.FillRectangle(Brushes.White, sheet);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#BBBBBB")), 1), sheet);

        double vTop = (_viewport.Top - _oy) / _zoom - 40, vBot = (_viewport.Bottom - _oy) / _zoom + 40;

        if (PageView)
        {
            var guide = new Pen(new SolidColorBrush(Color.Parse("#C0C0C0")), 1, new DashStyle(new double[] { 4, 4 }, 0));
            int pages = (int)Math.Ceiling(_contentH / PageH);
            for (int i = 1; i < pages; i++)
            {
                double gy = i * PageH;
                ctx.DrawLine(guide, new Point(0, gy), new Point(SheetW, gy));
                if (PageNumbers)
                    DrawSmall(ctx, i.ToString(), SheetW / 2, gy - MarginY / 2);
            }
            if (PageNumbers) DrawSmall(ctx, pages.ToString(), SheetW / 2, pages * PageH - MarginY / 2);
        }

        var selBrush = new SolidColorBrush(Color.FromArgb(110, 66, 133, 244));
        var (sa, sb) = Ordered();
        bool hasSel = !ReferenceEquals(sa.P, sb.P) || sa.Off != sb.Off;
        int ia = _flatIndex.GetValueOrDefault(sa.P), ib = _flatIndex.GetValueOrDefault(sb.P);

        foreach (var bl in _bl)
        {
            if (bl.Y + bl.H + 40 < vTop || bl.Y - 40 > vBot) continue;
            switch (bl.B.Kind)
            {
                case BlockKind.Para: DrawPara(ctx, bl.B.Para!, hasSel, ia, ib, sa, sb, selBrush); break;
                case BlockKind.Table:
                {
                    var pen = new Pen(new SolidColorBrush(Color.Parse("#8A8A8A")), 1);
                    foreach (var row in bl.B.Rows!) foreach (var c in row) if (_pl.TryGetValue(c, out var cpl) && cpl.Cell.Height > 0)
                    {
                        ctx.DrawRectangle(null, pen, cpl.Cell);
                    }
                    foreach (var row in bl.B.Rows!) foreach (var c in row) DrawPara(ctx, c, hasSel, ia, ib, sa, sb, selBrush);
                    break;
                }
                case BlockKind.Image:
                    if (bl.Bmp != null) ctx.DrawImage(bl.Bmp, new Rect(bl.Bmp.Size), new Rect(bl.X, bl.Y, bl.W, bl.H));
                    else ctx.DrawRectangle(null, new Pen(Brushes.Gray, 1), new Rect(bl.X, bl.Y, Math.Max(40, bl.W), Math.Max(40, bl.H)));
                    if (ReferenceEquals(_selImage, bl.B))
                        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#1A73E8")), 2), new Rect(bl.X - 1, bl.Y - 1, bl.W + 2, bl.H + 2));
                    break;
                case BlockKind.Rule:
                    ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#777777")), 1), new Point(MarginX, bl.Y + 8), new Point(MarginX + ContentW, bl.Y + 8));
                    break;
                case BlockKind.PageBreak:
                    ctx.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#999999")), 1, new DashStyle(new double[] { 2, 3 }, 0)), new Point(MarginX, bl.Y + 8), new Point(MarginX + ContentW, bl.Y + 8));
                    DrawSmall(ctx, "page break", SheetW / 2, bl.Y - 2);
                    break;
            }
        }

        // caret
        if (IsKeyboardFocusWithin || IsFocused)
        {
            if (_caretOn && !hasSel && _selImage == null && _pl.TryGetValue(_caret.P, out var cpl2))
            {
                var r = CaretRect(_caret, cpl2);
                ctx.DrawLine(new Pen(Brushes.Black, 1.2), new Point(r.X, r.Y), new Point(r.X, r.Bottom));
            }
        }
    }

    private static void DrawSmall(DrawingContext ctx, string text, double cx, double y)
    {
        var tl = new TextLayout(text, Typeface.Default, 11, new SolidColorBrush(Color.Parse("#888888")));
        tl.Draw(ctx, new Point(cx - tl.Width / 2, y));
    }

    private void DrawPara(DrawingContext ctx, Para p, bool hasSel, int ia, int ib, DocPos sa, DocPos sb, IBrush selBrush)
    {
        if (!_pl.TryGetValue(p, out var pl)) return;
        var origin = new Point(pl.X, pl.Y);
        if (p.Style == PStyle.Code && pl.Cell.Height == 0)
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#F1F3F4")), new Rect(pl.X - 6, pl.Y - 2, pl.W + 12, pl.H + 4));
        if (p.Style == PStyle.Quote && pl.Cell.Height == 0)
            ctx.FillRectangle(new SolidColorBrush(Color.Parse("#B0B7C3")), new Rect(pl.X - 14, pl.Y, 3, pl.H));

        // highlights
        if (pl.Layout != null)
        {
            int pos = 0;
            foreach (var r in p.Runs)
            {
                if (r.Highlight != null && r.Text.Length > 0)
                {
                    var hb = BrushOf(r.Highlight, Brushes.Yellow);
                    foreach (var rc in pl.Layout.HitTestTextRange(pos, r.Text.Length))
                        ctx.FillRectangle(hb, new Rect(rc.X + origin.X, rc.Y + origin.Y, rc.Width, rc.Height));
                }
                pos += r.Text.Length;
            }
        }
        // find matches
        if (_matches.Count > 0 && pl.Layout != null)
        {
            for (int i = 0; i < _matches.Count; i++)
            {
                var m = _matches[i];
                if (!ReferenceEquals(m.p, p)) continue;
                var mb = new SolidColorBrush(i == _matchCur ? Color.FromArgb(200, 255, 152, 0) : Color.FromArgb(150, 255, 235, 59));
                foreach (var rc in pl.Layout.HitTestTextRange(m.s, m.e - m.s))
                    ctx.FillRectangle(mb, new Rect(rc.X + origin.X, rc.Y + origin.Y, rc.Width, rc.Height));
            }
        }
        // selection
        if (hasSel && _flatIndex.TryGetValue(p, out var fi) && fi >= ia && fi <= ib)
        {
            int s = fi == ia ? sa.Off : 0;
            int e = fi == ib ? sb.Off : p.Length;
            if (pl.Layout != null && e > s)
            {
                foreach (var rc in pl.Layout.HitTestTextRange(s, e - s))
                    ctx.FillRectangle(selBrush, new Rect(rc.X + origin.X, rc.Y + origin.Y, rc.Width, rc.Height));
            }
            if (fi != ib)   // newline is selected: show a stub at the end of the last line
            {
                var er = CaretRect(new DocPos(p, p.Length), pl);
                ctx.FillRectangle(selBrush, new Rect(er.X, er.Y, 6, Math.Max(er.Height, pl.LineH)));
            }
        }
        // marker
        if (pl.Marker != null)
        {
            var size = SizePx(p, p.Runs.FirstOrDefault());
            var mt = new TextLayout(pl.Marker, new Typeface(DefaultFont), size, Brushes.Black);
            mt.Draw(ctx, new Point(pl.MarkerX + 20 - mt.Width, pl.Y));
        }
        pl.Layout?.Draw(ctx, origin);
    }

    private Rect CaretRect(DocPos pos, PL pl)
    {
        if (pl.Layout == null || pos.P.Length == 0)
        {
            double x = pl.X;
            if (pos.P.Align == Align.Center) x = pl.X + pl.W / 2;
            else if (pos.P.Align == Align.Right) x = pl.X + pl.W;
            return new Rect(x, pl.Y, 0, pl.LineH);
        }
        var r = pl.Layout.HitTestTextPosition(Math.Clamp(pos.Off, 0, pos.P.Length));
        return new Rect(pl.X + r.X, pl.Y + r.Y, 0, r.Height > 0 ? r.Height : pl.LineH);
    }

    // ───────────────────────── positions ─────────────────────────
    private (DocPos a, DocPos b) Ordered()
    {
        int ia = _flatIndex.GetValueOrDefault(_anchor.P), ic = _flatIndex.GetValueOrDefault(_caret.P);
        if (ia < ic || (ia == ic && _anchor.Off <= _caret.Off)) return (_anchor, _caret);
        return (_caret, _anchor);
    }

    public bool HasSelection { get { var (a, b) = Ordered(); return !ReferenceEquals(a.P, b.P) || a.Off != b.Off; } }

    private int Cmp(DocPos a, DocPos b)
    {
        int ia = _flatIndex.GetValueOrDefault(a.P), ib = _flatIndex.GetValueOrDefault(b.P);
        return ia != ib ? ia.CompareTo(ib) : a.Off.CompareTo(b.Off);
    }

    private DocPos HitTest(Point doc)
    {
        Para? best = null; double bestD = double.MaxValue;
        foreach (var p in _flat)
        {
            if (!_pl.TryGetValue(p, out var pl)) continue;
            Rect r = pl.Cell.Height > 0 ? pl.Cell : new Rect(MarginX, pl.Y, ContentW, pl.H);
            double d = 0;
            if (doc.Y < r.Top) d = r.Top - doc.Y; else if (doc.Y > r.Bottom) d = doc.Y - r.Bottom;
            if (pl.Cell.Height > 0)
            {
                if (doc.X < r.Left) d += (r.Left - doc.X) * 0.5; else if (doc.X > r.Right) d += (doc.X - r.Right) * 0.5;
            }
            if (d < bestD) { bestD = d; best = p; if (d == 0) break; }
        }
        best ??= _flat[0];
        var plb = _pl[best];
        if (plb.Layout == null || best.Length == 0) return new DocPos(best, 0);
        var hit = plb.Layout.HitTestPoint(new Point(doc.X - plb.X, doc.Y - plb.Y));
        int off = hit.CharacterHit.FirstCharacterIndex + hit.CharacterHit.TrailingLength;
        return new DocPos(best, Math.Clamp(off, 0, best.Length));
    }

    private Block? ImageAt(Point doc)
    {
        foreach (var bl in _bl)
            if (bl.B.Kind == BlockKind.Image && new Rect(bl.X, bl.Y, bl.W, bl.H).Contains(doc)) return bl.B;
        return null;
    }

    private void SetCaret(DocPos p, bool extend, bool keepGoal = false)
    {
        _caret = p;
        if (!extend) _anchor = p;
        _selImage = null;
        if (!keepGoal) _goalX = -1;
        _caretOn = true;
        if (!_suppressPending) _pending = null;
        InvalidateVisual();
        SelectionChanged?.Invoke();
        EnsureCaretVisible();
    }
    private bool _suppressPending;

    private void EnsureCaretVisible()
    {
        if (!_pl.TryGetValue(_caret.P, out var pl)) return;
        var r = CaretRect(_caret, pl);
        var vr = new Rect(r.X * _zoom + _ox - 4, r.Y * _zoom + _oy - 4, 40, r.Height * _zoom + 8);
        this.BringIntoView(vr);
    }

    // ───────────────────────── mouse ─────────────────────────
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) { if (pt.Properties.IsRightButtonPressed && !HasSelection) { } return; }
        var d = ToDoc(pt.Position);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var img = ImageAt(d);
        if (img != null) { _selImage = img; _anchor = _caret; InvalidateVisual(); SelectionChanged?.Invoke(); e.Handled = true; return; }
        var pos = HitTest(d);

        // Ctrl+click opens links
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var link = LinkAt(pos);
            if (link != null) { OpenLinkRequested?.Invoke(link); e.Handled = true; return; }
        }
        if (e.ClickCount == 2) { SelectWord(pos); e.Handled = true; return; }
        if (e.ClickCount >= 3) { _anchor = new DocPos(pos.P, 0); SetCaret(new DocPos(pos.P, pos.P.Length), true); e.Handled = true; return; }
        SetCaret(pos, shift);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var d = ToDoc(e.GetPosition(this));
        if (_dragging)
        {
            SetCaret(HitTest(d), true);
            return;
        }
        var pos = HitTest(d);
        Cursor = e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkAt(pos) != null
            ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging) { _dragging = false; e.Pointer.Capture(null); }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Zoom = _zoom + (e.Delta.Y > 0 ? 0.1 : -0.1);
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    private string? LinkAt(DocPos pos)
    {
        var p = pos.P;
        var r = p.RunAtChar(Math.Min(pos.Off, Math.Max(0, p.Length - 1)));
        return r?.Link;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'';

    private void SelectWord(DocPos pos)
    {
        var t = pos.P.Text;
        int s = Math.Min(pos.Off, t.Length), e = s;
        if (t.Length == 0) return;
        if (s == t.Length) s--; e = s;
        if (!IsWordChar(t[s])) { _anchor = new DocPos(pos.P, s); SetCaret(new DocPos(pos.P, s + 1), true); return; }
        while (s > 0 && IsWordChar(t[s - 1])) s--;
        while (e < t.Length && IsWordChar(t[e])) e++;
        _anchor = new DocPos(pos.P, s);
        SetCaret(new DocPos(pos.P, e), true);
    }

    // ───────────────────────── keyboard ─────────────────────────
    protected override void OnGotFocus(FocusChangedEventArgs e) { base.OnGotFocus(e); _caretOn = true; InvalidateVisual(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { base.OnLostFocus(e); InvalidateVisual(); }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (string.IsNullOrEmpty(e.Text)) return;
        var text = new string(e.Text.Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0) return;
        TypeText(text);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool handled = true;
        switch (e.Key)
        {
            case Key.Left: MoveHoriz(-1, ctrl, shift); break;
            case Key.Right: MoveHoriz(1, ctrl, shift); break;
            case Key.Up: MoveVert(-1, shift, false); break;
            case Key.Down: MoveVert(1, shift, false); break;
            case Key.PageUp: MoveVert(-1, shift, true); break;
            case Key.PageDown: MoveVert(1, shift, true); break;
            case Key.Home: if (ctrl) SetCaret(new DocPos(_flat[0], 0), shift); else MoveLineEdge(true, shift); break;
            case Key.End: if (ctrl) SetCaret(new DocPos(_flat[^1], _flat[^1].Length), shift); else MoveLineEdge(false, shift); break;
            case Key.Back: Backspace(ctrl); break;
            case Key.Delete: DeleteForward(ctrl); break;
            case Key.Enter: EnterKey(); break;
            case Key.Tab: TabKey(shift); break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.C when ctrl: _ = CopyAsync(false); break;
            case Key.X when ctrl: _ = CopyAsync(true); break;
            case Key.V when ctrl: _ = PasteAsync(); break;
            case Key.Z when ctrl && !shift: Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.Z when ctrl && shift: Redo(); break;
            case Key.B when ctrl: ToggleBold(); break;
            case Key.I when ctrl: ToggleItalic(); break;
            case Key.U when ctrl: ToggleUnderline(); break;
            case Key.L when ctrl: SetAlign(Align.Left); break;
            case Key.E when ctrl: SetAlign(Align.Center); break;
            case Key.R when ctrl: SetAlign(Align.Right); break;
            case Key.J when ctrl: SetAlign(Align.Justify); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    private void MoveHoriz(int dir, bool word, bool extend)
    {
        if (HasSelection && !extend)
        {
            var (a, b) = Ordered();
            SetCaret(dir < 0 ? a : b, false);
            return;
        }
        var p = _caret.P; int off = _caret.Off;
        if (dir < 0)
        {
            if (off > 0) off = word ? WordLeft(p.Text, off) : off - 1;
            else { var prev = PrevPara(p); if (prev == null) return; SetCaret(new DocPos(prev, prev.Length), extend); return; }
        }
        else
        {
            if (off < p.Length) off = word ? WordRight(p.Text, off) : off + 1;
            else { var next = NextPara(p); if (next == null) return; SetCaret(new DocPos(next, 0), extend); return; }
        }
        SetCaret(new DocPos(p, off), extend);
    }

    private static int WordLeft(string t, int off)
    {
        while (off > 0 && !IsWordChar(t[off - 1])) off--;
        while (off > 0 && IsWordChar(t[off - 1])) off--;
        return off;
    }

    private static int WordRight(string t, int off)
    {
        while (off < t.Length && !IsWordChar(t[off])) off++;
        while (off < t.Length && IsWordChar(t[off])) off++;
        return off;
    }

    private Para? PrevPara(Para p) { int i = _flatIndex[p]; return i > 0 ? _flat[i - 1] : null; }
    private Para? NextPara(Para p) { int i = _flatIndex[p]; return i + 1 < _flat.Count ? _flat[i + 1] : null; }

    private (int line, double lineTop, double lineH, int count) LineInfo(Para p, int off)
    {
        var pl = _pl[p];
        if (pl.Layout == null || p.Length == 0) return (0, 0, pl.LineH, 1);
        var lines = pl.Layout.TextLines;
        double top = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            bool last = i == lines.Count - 1;
            if (off < l.FirstTextSourceIndex + l.Length || last) return (i, top, l.Height, lines.Count);
            top += l.Height;
        }
        return (0, 0, pl.LineH, 1);
    }

    private void MoveVert(int dir, bool extend, bool page)
    {
        if (page)
        {
            var pl0 = _pl[_caret.P];
            var r0 = CaretRect(_caret, pl0);
            double dy = Math.Max(100, _viewport.Height / _zoom - 60) * dir;
            if (_goalX < 0) _goalX = r0.X;
            var pos = HitTest(new Point(_goalX, r0.Y + r0.Height / 2 + dy));
            SetCaret(pos, extend, true);
            return;
        }
        var p = _caret.P; var pl = _pl[p];
        var rc = CaretRect(_caret, pl);
        if (_goalX < 0) _goalX = rc.X;
        var (li, lt, lh, cnt) = LineInfo(p, _caret.Off);
        int target = li + dir;
        if (target >= 0 && target < cnt && pl.Layout != null)
        {
            // find top of target line
            double top = 0; var lines = pl.Layout.TextLines;
            for (int i = 0; i < target; i++) top += lines[i].Height;
            var hit = pl.Layout.HitTestPoint(new Point(_goalX - pl.X, top + lines[target].Height / 2));
            int off = hit.CharacterHit.FirstCharacterIndex + hit.CharacterHit.TrailingLength;
            SetCaret(new DocPos(p, Math.Clamp(off, 0, p.Length)), extend, true);
            return;
        }
        var np = dir < 0 ? PrevPara(p) : NextPara(p);
        if (np == null)
        {
            SetCaret(dir < 0 ? new DocPos(p, 0) : new DocPos(p, p.Length), extend);
            return;
        }
        var npl = _pl[np];
        if (npl.Layout == null || np.Length == 0) { SetCaret(new DocPos(np, 0), extend, true); return; }
        var ls = npl.Layout.TextLines;
        int tl = dir < 0 ? ls.Count - 1 : 0;
        double ttop = 0; for (int i = 0; i < tl; i++) ttop += ls[i].Height;
        var h2 = npl.Layout.HitTestPoint(new Point(_goalX - npl.X, ttop + ls[tl].Height / 2));
        int o2 = h2.CharacterHit.FirstCharacterIndex + h2.CharacterHit.TrailingLength;
        SetCaret(new DocPos(np, Math.Clamp(o2, 0, np.Length)), extend, true);
    }

    private void MoveLineEdge(bool start, bool extend)
    {
        var p = _caret.P; var pl = _pl[p];
        if (pl.Layout == null || p.Length == 0) { SetCaret(new DocPos(p, 0), extend); return; }
        var (li, _, _, _) = LineInfo(p, _caret.Off);
        var l = pl.Layout.TextLines[li];
        int off = start ? l.FirstTextSourceIndex : l.FirstTextSourceIndex + l.Length;
        if (!start && li < pl.Layout.TextLines.Count - 1) off = Math.Max(l.FirstTextSourceIndex, off - 1);
        SetCaret(new DocPos(p, Math.Clamp(off, 0, p.Length)), extend);
    }

    public void SelectAll()
    {
        _anchor = new DocPos(_flat[0], 0);
        SetCaret(new DocPos(_flat[^1], _flat[^1].Length), true);
    }

    // ───────────────────────── undo ─────────────────────────
    private DocTab.Snap Capture() => new()
    {
        Doc = Tab.Doc.Clone(),
        C = _flatIndex.GetValueOrDefault(_caret.P), Co = _caret.Off,
        A = _flatIndex.GetValueOrDefault(_anchor.P), Ao = _anchor.Off
    };

    /// <summary>Call before every change. Consecutive typing shares one undo step.</summary>
    private void Push(string kind)
    {
        var now = Environment.TickCount64;
        if (kind == "type" && _lastKind == "type" && now - _lastKindAt < 1500) { _lastKindAt = now; Tab.Redo.Clear(); return; }
        _lastKind = kind; _lastKindAt = now;
        Tab.Undo.Add(Capture());
        if (Tab.Undo.Count > 80) Tab.Undo.RemoveAt(0);
        Tab.Redo.Clear();
    }

    private void Modified()
    {
        Tab.Dirty = true;
        Reindex();
        _layoutDirty = true;
        RelayoutNow();
        _matches.Clear(); _matchCur = -1;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
        EnsureCaretVisible();
    }

    public void Undo()
    {
        if (Tab.Undo.Count == 0) return;
        Tab.Redo.Add(Capture());
        Restore(Tab.Undo[^1]); Tab.Undo.RemoveAt(Tab.Undo.Count - 1);
        _lastKind = "";
    }

    public void Redo()
    {
        if (Tab.Redo.Count == 0) return;
        Tab.Undo.Add(Capture());
        Restore(Tab.Redo[^1]); Tab.Redo.RemoveAt(Tab.Redo.Count - 1);
        _lastKind = "";
    }

    private void Restore(DocTab.Snap s)
    {
        Tab.Doc = s.Doc;
        _bitmaps.Clear();
        Reindex();
        _caret = PosFromFlat(s.C, s.Co); _anchor = PosFromFlat(s.A, s.Ao);
        _selImage = null; _pending = null;
        Tab.Dirty = true;
        _layoutDirty = true; RelayoutNow();
        Changed?.Invoke(); SelectionChanged?.Invoke();
        EnsureCaretVisible();
    }

    // ───────────────────────── editing ─────────────────────────
    private Run TypingFormat()
    {
        var f = _caret.P.FormatAt(_caret.Off);
        if (_pending is { } pf)
        {
            f.Bold = pf.Bold; f.Italic = pf.Italic; f.Underline = pf.Underline; f.Strike = pf.Strike;
            f.Sup = pf.Sup; f.Sub = pf.Sub; f.Font = pf.Font; f.Size = pf.Size; f.Color = pf.Color; f.Highlight = pf.Highlight;
        }
        // typing right after a link continues the link only in the middle of it
        if (f.Link != null && _caret.Off >= _caret.P.Length) f.Link = null;
        return f;
    }

    private void TypeText(string text)
    {
        Push("type");
        if (_selImage != null) { DeleteImage(_selImage); }
        else if (HasSelection) DeleteSelectionCore();
        var fmt = TypingFormat();
        _caret.P.Insert(_caret.Off, text, fmt);
        _caret = new DocPos(_caret.P, _caret.Off + text.Length);
        _anchor = _caret;
        _caretOn = true;
        _suppressPending = true;
        Modified();
        _suppressPending = false;
    }

    private void DeleteImage(Block b)
    {
        Tab.Doc.Blocks.Remove(b);
        _selImage = null;
        Tab.Doc.EnsureValid();
    }

    private bool IsTopLevel(Para p) => _where.TryGetValue(p, out var w) && w.r < 0;

    /// <summary>Delete the selected range (no undo push). Caret ends at the start.</summary>
    private void DeleteSelectionCore()
    {
        var (a, b) = Ordered();
        if (ReferenceEquals(a.P, b.P)) { a.P.Delete(a.Off, b.Off - a.Off); _caret = _anchor = a; return; }
        int ia = _flatIndex[a.P], ib = _flatIndex[b.P];
        bool simple = IsTopLevel(a.P) && IsTopLevel(b.P);
        var blocks = Tab.Doc.Blocks;
        if (simple)
        {
            var ba = _where[a.P].block; var bb = _where[b.P].block;
            int xa = blocks.IndexOf(ba), xb = blocks.IndexOf(bb);
            a.P.Delete(a.Off, a.P.Length - a.Off);
            b.P.Delete(0, b.Off);
            blocks.RemoveRange(xa + 1, xb - xa - 1);      // everything between (incl. tables/images)
            a.P.Append(b.P);
            blocks.Remove(bb);
        }
        else
        {
            a.P.Delete(a.Off, a.P.Length - a.Off);
            b.P.Delete(0, b.Off);
            var removeBlocks = new List<Block>();
            for (int i = ia + 1; i < ib; i++)
            {
                var p = _flat[i];
                if (IsTopLevel(p)) removeBlocks.Add(_where[p].block);
                else p.Delete(0, p.Length);
            }
            foreach (var rb in removeBlocks) blocks.Remove(rb);
        }
        _caret = _anchor = a;
        Tab.Doc.EnsureValid();
        Reindex();
        _caret = _anchor = new DocPos(_flatIndex.ContainsKey(a.P) ? a.P : _flat[0], _flatIndex.ContainsKey(a.P) ? a.Off : 0);
    }

    private void Backspace(bool word)
    {
        if (_selImage != null) { Push("del"); DeleteImage(_selImage); Modified(); return; }
        if (HasSelection) { Push("del"); DeleteSelectionCore(); Modified(); return; }
        var p = _caret.P;
        if (_caret.Off > 0)
        {
            Push("back");
            int from = word ? WordLeft(p.Text, _caret.Off) : _caret.Off - 1;
            p.Delete(from, _caret.Off - from);
            _caret = _anchor = new DocPos(p, from);
            Modified(); return;
        }
        if (p.List != ListKind.None) { Push("fmt"); p.List = ListKind.None; p.Level = 0; p.Touch(); Modified(); return; }
        if (p.Style != PStyle.Normal && IsTopLevel(p)) { Push("fmt"); p.Style = PStyle.Normal; p.Touch(); Modified(); return; }
        if (!IsTopLevel(p)) return;
        var blocks = Tab.Doc.Blocks;
        int bi = blocks.IndexOf(_where[p].block);
        if (bi == 0) return;
        var prev = blocks[bi - 1];
        Push("del");
        if (prev.Kind == BlockKind.Para)
        {
            int len = prev.Para!.Length;
            prev.Para.Append(p);
            blocks.RemoveAt(bi);
            Reindex();
            _caret = _anchor = new DocPos(prev.Para, len);
        }
        else
        {
            blocks.RemoveAt(bi - 1);
            Reindex();
        }
        Modified();
    }

    private void DeleteForward(bool word)
    {
        if (_selImage != null) { Push("del"); DeleteImage(_selImage); Modified(); return; }
        if (HasSelection) { Push("del"); DeleteSelectionCore(); Modified(); return; }
        var p = _caret.P;
        if (_caret.Off < p.Length)
        {
            Push("del");
            int to = word ? WordRight(p.Text, _caret.Off) : _caret.Off + 1;
            p.Delete(_caret.Off, to - _caret.Off);
            Modified(); return;
        }
        if (!IsTopLevel(p)) return;
        var blocks = Tab.Doc.Blocks;
        int bi = blocks.IndexOf(_where[p].block);
        if (bi + 1 >= blocks.Count) return;
        var next = blocks[bi + 1];
        Push("del");
        if (next.Kind == BlockKind.Para) { p.Append(next.Para!); blocks.RemoveAt(bi + 1); }
        else blocks.RemoveAt(bi + 1);
        Modified();
    }

    private void EnterKey()
    {
        Push("enter");
        if (_selImage != null) { var bi0 = Tab.Doc.Blocks.IndexOf(_selImage); _selImage = null; Tab.Doc.Blocks.Insert(bi0 + 1, Block.Of(new Para())); Reindex(); _caret = _anchor = new DocPos(Tab.Doc.Blocks[bi0 + 1].Para!, 0); Modified(); return; }
        if (HasSelection) DeleteSelectionCore();
        var p = _caret.P;
        if (!IsTopLevel(p))
        {
            // in a table: Enter moves to the next cell
            var np = NextPara(p);
            if (np != null) SetCaret(new DocPos(np, 0), false);
            return;
        }
        if (p.List != ListKind.None && p.Length == 0)
        {
            if (p.Level > 0) p.Level--; else p.List = ListKind.None;
            p.Touch(); Modified(); return;
        }
        bool atEnd = _caret.Off >= p.Length;
        var tail = p.SplitAt(_caret.Off);
        if (atEnd && p.Style is PStyle.H1 or PStyle.H2 or PStyle.H3) tail.Style = PStyle.Normal;
        var blocks = Tab.Doc.Blocks;
        int bi = blocks.IndexOf(_where[p].block);
        blocks.Insert(bi + 1, Block.Of(tail));
        Reindex();
        _caret = _anchor = new DocPos(tail, 0);
        Modified();
    }

    private void TabKey(bool shift)
    {
        var p = _caret.P;
        if (!IsTopLevel(p))
        {
            var (blk, r, c) = _where[p];
            if (!shift)
            {
                var np = NextPara(p);
                if (np != null && _where.TryGetValue(np, out var w) && ReferenceEquals(w.block, blk)) { SetCaret(new DocPos(np, 0), false); return; }
                // last cell: append a row
                Push("table");
                var row = new List<Para>(); for (int i = 0; i < blk.Rows![0].Count; i++) row.Add(new Para());
                blk.Rows.Add(row);
                Reindex();
                _caret = _anchor = new DocPos(row[0], 0);
                Modified();
            }
            else
            {
                var pp = PrevPara(p);
                if (pp != null && _where.TryGetValue(pp, out var w) && ReferenceEquals(w.block, blk)) SetCaret(new DocPos(pp, pp.Length), false);
            }
            return;
        }
        if (p.List != ListKind.None || (HasSelection && !ReferenceEquals(Ordered().a.P, Ordered().b.P)))
        {
            Indent(shift ? -1 : 1);
            return;
        }
        if (shift) return;
        TypeText("    ");
    }

    // ───────────────────────── clipboard ─────────────────────────
    public string SelectedText()
    {
        var (a, b) = Ordered();
        if (ReferenceEquals(a.P, b.P)) return a.P.Text.Substring(a.Off, b.Off - a.Off);
        var sb = new System.Text.StringBuilder();
        int ia = _flatIndex[a.P], ib = _flatIndex[b.P];
        for (int i = ia; i <= ib; i++)
        {
            var t = _flat[i].Text;
            int s = i == ia ? a.Off : 0, e = i == ib ? b.Off : t.Length;
            sb.Append(t, s, e - s);
            if (i != ib) sb.Append('\n');
        }
        return sb.ToString();
    }

    private List<Para> SelectionFragment()
    {
        var (a, b) = Ordered();
        var list = new List<Para>();
        int ia = _flatIndex[a.P], ib = _flatIndex[b.P];
        for (int i = ia; i <= ib; i++)
        {
            var p = _flat[i];
            int s = i == ia ? a.Off : 0, e = i == ib ? b.Off : p.Length;
            var c = p.Clone();
            if (e < c.Length) c.Delete(e, c.Length - e);
            if (s > 0) c.Delete(0, s);
            list.Add(c);
        }
        return list;
    }

    public async Task CopyAsync(bool cut)
    {
        if (!HasSelection) return;
        var text = SelectedText();
        _richClip = SelectionFragment();
        _richClipText = text;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb != null) await cb.SetTextAsync(text);
        if (cut) { Push("cut"); DeleteSelectionCore(); Modified(); }
    }

    public async Task PasteAsync()
    {
        var tl = TopLevel.GetTopLevel(this);
        var cb = tl?.Clipboard;
        if (cb == null) return;

        // an image on the clipboard?
        try
        {
            var png = await cb.TryGetValueAsync(DataFormat.CreateBytesPlatformFormat("image/png"));
            if (png is { Length: > 0 })
            {
                InsertImage(png);
                return;
            }
        }
        catch { }

        var text = await cb.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        Push("paste");
        if (HasSelection) DeleteSelectionCore();
        List<Para> frag;
        if (_richClip != null && _richClipText != null && _richClipText.Replace("\r\n", "\n") == text)
            frag = _richClip.Select(p => p.Clone()).ToList();
        else
        {
            // plain text: keep the surrounding formatting
            var fmt = TypingFormat();
            frag = new List<Para>();
            foreach (var line in text.Split('\n'))
            {
                var p = new Para();
                var clean = line.Replace("\t", "    ");
                if (clean.Length > 0) p.Runs.Add(fmt.CloneWith(clean));
                frag.Add(p);
            }
        }
        InsertFragment(frag);
        Modified();
    }

    private void InsertFragment(List<Para> frag)
    {
        var p = _caret.P;
        if (!IsTopLevel(p) || frag.Count == 1)
        {
            if (!IsTopLevel(p) && frag.Count > 1) { var f = new Para(); foreach (var q in frag) { if (f.Length > 0) f.Runs.Add(new Run { Text = " " }); f.Append(q); } frag = new List<Para> { f }; }
            int pos = _caret.Off;
            foreach (var r in frag[0].Runs) { p.Insert(pos, r.Text, r); pos += r.Text.Length; }
            _caret = _anchor = new DocPos(p, pos);
            return;
        }
        var tail = p.SplitAt(_caret.Off);
        foreach (var r in frag[0].Runs) p.Insert(p.Length, r.Text, r);
        var blocks = Tab.Doc.Blocks;
        int bi = blocks.IndexOf(_where[p].block);
        Para last = tail;
        int at = bi + 1;
        for (int i = 1; i < frag.Count; i++)
        {
            var np = frag[i].Clone();
            blocks.Insert(at++, Block.Of(np));
            last = np;
        }
        int caretOff = last.Length;
        if (frag.Count > 1)
        {
            last.Append(tail);
            Reindex();
            _caret = _anchor = new DocPos(last, caretOff);
        }
    }

    // ───────────────────────── formatting ─────────────────────────
    private IEnumerable<(Para p, int s, int e)> SelectedRanges()
    {
        var (a, b) = Ordered();
        int ia = _flatIndex[a.P], ib = _flatIndex[b.P];
        for (int i = ia; i <= ib; i++)
        {
            var p = _flat[i];
            int s = i == ia ? a.Off : 0, e = i == ib ? b.Off : p.Length;
            yield return (p, s, e);
        }
    }

    private IEnumerable<Para> SelectedParas() => SelectedRanges().Select(r => r.p);

    /// <summary>Format of the text at the caret / selection start, for the ribbon.</summary>
    public CharFormat CurrentFormat()
    {
        var (a, _) = Ordered();
        Run r;
        if (HasSelection) r = a.P.RunAtChar(Math.Min(a.Off, Math.Max(0, a.P.Length - 1))) ?? new Run();
        else r = a.P.FormatAt(a.Off);
        var p = a.P;
        var f = new CharFormat(r.Bold || RichDoc.DefaultBold(p.Style), r.Italic || RichDoc.DefaultItalic(p.Style), r.Underline, r.Strike, r.Sup, r.Sub, r.Font, r.Size > 0 ? r.Size : RichDoc.DefaultSize(p.Style), r.Color, r.Highlight, r.Link);
        if (_pending is { } pf && !HasSelection) f = pf;
        return f;
    }

    public Para CaretPara => _caret.P;

    private void ApplyRun(Action<Run> f)
    {
        if (!HasSelection)
        {
            var baseRun = _caret.P.FormatAt(_caret.Off);
            if (_pending is { } pf) { baseRun.Bold = pf.Bold; baseRun.Italic = pf.Italic; baseRun.Underline = pf.Underline; baseRun.Strike = pf.Strike; baseRun.Sup = pf.Sup; baseRun.Sub = pf.Sub; baseRun.Font = pf.Font; baseRun.Size = pf.Size; baseRun.Color = pf.Color; baseRun.Highlight = pf.Highlight; }
            f(baseRun);
            _pending = new CharFormat(baseRun.Bold, baseRun.Italic, baseRun.Underline, baseRun.Strike, baseRun.Sup, baseRun.Sub, baseRun.Font, baseRun.Size, baseRun.Color, baseRun.Highlight, null);
            SelectionChanged?.Invoke();
            return;
        }
        Push("fmt");
        foreach (var (p, s, e) in SelectedRanges()) if (e > s) p.Format(s, e - s, f);
        Modified();
    }

    private void Toggle(Func<Run, bool> get, Action<Run, bool> set)
    {
        bool all;
        if (HasSelection)
        {
            all = true;
            foreach (var (p, s, e) in SelectedRanges())
            {
                int pos = 0;
                foreach (var r in p.Runs)
                {
                    int rs = pos, re = pos + r.Text.Length; pos = re;
                    if (re <= s || rs >= e) continue;
                    if (!get(r)) all = false;
                }
            }
        }
        else
        {
            var cur = _pending != null ? new Run { Bold = _pending.Value.Bold, Italic = _pending.Value.Italic, Underline = _pending.Value.Underline, Strike = _pending.Value.Strike, Sup = _pending.Value.Sup, Sub = _pending.Value.Sub } : _caret.P.FormatAt(_caret.Off);
            all = get(cur);
        }
        ApplyRun(r => set(r, !all));
    }

    public void ToggleBold() => Toggle(r => r.Bold, (r, v) => r.Bold = v);
    public void ToggleItalic() => Toggle(r => r.Italic, (r, v) => r.Italic = v);
    public void ToggleUnderline() => Toggle(r => r.Underline, (r, v) => r.Underline = v);
    public void ToggleStrike() => Toggle(r => r.Strike, (r, v) => r.Strike = v);
    public void ToggleSup() => Toggle(r => r.Sup, (r, v) => { r.Sup = v; if (v) r.Sub = false; });
    public void ToggleSub() => Toggle(r => r.Sub, (r, v) => { r.Sub = v; if (v) r.Sup = false; });
    public void SetFont(string? f) => ApplyRun(r => r.Font = f);
    public void SetSize(double pt) => ApplyRun(r => r.Size = pt);
    public void SetColor(string? c) => ApplyRun(r => r.Color = c);
    public void SetHighlight(string? c) => ApplyRun(r => r.Highlight = c);

    public void ClearFormatting()
    {
        Push("fmt");
        foreach (var (p, s, e) in SelectedRanges())
        {
            if (e > s) p.Format(s, e - s, r => { r.Bold = r.Italic = r.Underline = r.Strike = r.Sup = r.Sub = false; r.Font = null; r.Size = 0; r.Color = null; r.Highlight = null; r.Link = null; });
            p.Style = PStyle.Normal; p.Align = Align.Left; p.Spacing = 1.0; p.Touch();
        }
        _pending = null;
        Modified();
    }

    public void SetParaStyle(PStyle st)
    {
        Push("fmt");
        foreach (var p in SelectedParas()) { p.Style = st; if (st != PStyle.Normal) { } p.Touch(); }
        Modified();
    }

    public void SetAlign(Align a)
    {
        Push("fmt");
        foreach (var p in SelectedParas()) { p.Align = a; p.Touch(); }
        if (_selImage != null) _selImage.ImgAlign = a;
        Modified();
    }

    public void ToggleList(ListKind kind)
    {
        Push("fmt");
        var ps = SelectedParas().ToList();
        bool all = ps.All(p => p.List == kind);
        foreach (var p in ps) { p.List = all ? ListKind.None : kind; if (all) p.Level = 0; p.Touch(); }
        Modified();
    }

    public void Indent(int d)
    {
        Push("fmt");
        foreach (var p in SelectedParas()) { p.Level = Math.Clamp(p.Level + d, 0, 8); p.Touch(); }
        Modified();
    }

    public void SetSpacing(double s)
    {
        Push("fmt");
        foreach (var p in SelectedParas()) { p.Spacing = s; p.Touch(); }
        Modified();
    }

    // ───────────────────────── inserts ─────────────────────────
    private int CaretBlockIndex()
    {
        var p = _caret.P;
        return Tab.Doc.Blocks.IndexOf(_where[p].block);
    }

    public void InsertLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        Push("link");
        if (HasSelection)
        {
            foreach (var (p, s, e) in SelectedRanges()) if (e > s) p.Format(s, e - s, r => r.Link = url);
        }
        else
        {
            var fmt = TypingFormat(); fmt.Link = url;
            _caret.P.Insert(_caret.Off, url, fmt);
            _caret = _anchor = new DocPos(_caret.P, _caret.Off + url.Length);
        }
        Modified();
    }

    public void RemoveLink()
    {
        if (!HasSelection) { var p = _caret.P; var r = p.RunAtChar(Math.Max(0, _caret.Off - 1)); if (r?.Link == null) return; Push("link"); r.Link = null; p.Normalize(); p.Touch(); Modified(); return; }
        Push("link");
        foreach (var (p, s, e) in SelectedRanges()) if (e > s) p.Format(s, e - s, r => r.Link = null);
        Modified();
    }

    public string? LinkAtCaret() => LinkAt(_caret);

    public void InsertImage(byte[] data)
    {
        double w = 0, h = 0;
        try { using var bmp = new Bitmap(new MemoryStream(data)); w = bmp.Size.Width; h = bmp.Size.Height; } catch { return; }
        if (w > 520) { h *= 520 / w; w = 520; }
        Push("image");
        if (HasSelection) DeleteSelectionCore();
        InsertBlockAtCaret(new Block { Kind = BlockKind.Image, Image = data, ImgW = w, ImgH = h });
    }

    public void InsertTable(int rows, int cols)
    {
        Push("table");
        if (HasSelection) DeleteSelectionCore();
        var t = Block.NewTable(Math.Clamp(rows, 1, 50), Math.Clamp(cols, 1, 12));
        InsertBlockAtCaret(t);
        _caret = _anchor = new DocPos(t.Rows![0][0], 0);
        Modified();
        SelectionChanged?.Invoke();
    }

    public void InsertRule() { Push("rule"); InsertBlockAtCaret(new Block { Kind = BlockKind.Rule }); }
    public void InsertPageBreak() { Push("pb"); InsertBlockAtCaret(new Block { Kind = BlockKind.PageBreak }); }

    /// <summary>Insert a block after the caret's paragraph (splitting it when the caret is mid-text).</summary>
    private void InsertBlockAtCaret(Block nb)
    {
        var p = _caret.P;
        var blocks = Tab.Doc.Blocks;
        int bi = IsTopLevel(p) ? blocks.IndexOf(_where[p].block) : blocks.IndexOf(_where[p].block);
        int at;
        if (IsTopLevel(p) && _caret.Off < p.Length && _caret.Off > 0)
        {
            var tail = p.SplitAt(_caret.Off);
            blocks.Insert(bi + 1, Block.Of(tail));
            at = bi + 1;
        }
        else if (IsTopLevel(p) && _caret.Off == 0 && p.Length > 0) at = bi;
        else at = bi + 1;
        blocks.Insert(at, nb);
        Tab.Doc.EnsureValid();
        Reindex();
        // caret goes to the paragraph following the new block
        var follow = at + 1 < blocks.Count && blocks[at + 1].Kind == BlockKind.Para ? blocks[at + 1].Para! : null;
        if (follow == null) { blocks.Insert(at + 1, Block.Of(new Para())); Reindex(); follow = blocks[at + 1].Para!; }
        _caret = _anchor = new DocPos(follow, 0);
        Modified();
    }

    public void TableAddRow(bool below)
    {
        var p = _caret.P;
        if (IsTopLevel(p)) return;
        var (blk, r, _) = _where[p];
        Push("table");
        var row = new List<Para>(); for (int i = 0; i < blk.Rows![0].Count; i++) row.Add(new Para());
        blk.Rows.Insert(below ? r + 1 : r, row);
        Modified();
    }

    public void TableAddCol()
    {
        var p = _caret.P;
        if (IsTopLevel(p)) return;
        var (blk, _, c) = _where[p];
        Push("table");
        foreach (var row in blk.Rows!) row.Insert(Math.Min(c + 1, row.Count), new Para());
        Modified();
    }

    public void TableDeleteRow()
    {
        var p = _caret.P;
        if (IsTopLevel(p)) return;
        var (blk, r, _) = _where[p];
        Push("table");
        if (blk.Rows!.Count <= 1) Tab.Doc.Blocks.Remove(blk); else blk.Rows.RemoveAt(r);
        Tab.Doc.EnsureValid(); Reindex();
        _caret = _anchor = new DocPos(_flat[0], 0);
        Modified();
    }

    public void TableDeleteCol()
    {
        var p = _caret.P;
        if (IsTopLevel(p)) return;
        var (blk, _, c) = _where[p];
        Push("table");
        if (blk.Rows![0].Count <= 1) Tab.Doc.Blocks.Remove(blk); else foreach (var row in blk.Rows) row.RemoveAt(Math.Min(c, row.Count - 1));
        Tab.Doc.EnsureValid(); Reindex();
        _caret = _anchor = new DocPos(_flat[0], 0);
        Modified();
    }

    public bool CaretInTable => !IsTopLevel(_caret.P);

    public void LoadDocument(RichDoc doc)
    {
        Tab.Doc = doc;
        doc.EnsureValid();
        Reindex();
        _caret = _anchor = new DocPos(_flat[0], 0);
        _bitmaps.Clear();
        InvalidateAll();
    }

    // ───────────────────────── find / replace ─────────────────────────
    public int FindAll(string q, bool matchCase)
    {
        _matches.Clear(); _matchCur = -1;
        if (!string.IsNullOrEmpty(q))
        {
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var p in _flat)
            {
                var t = p.Text; int i = 0;
                while ((i = t.IndexOf(q, i, cmp)) >= 0) { _matches.Add((p, i, i + q.Length)); i += Math.Max(1, q.Length); }
            }
        }
        InvalidateVisual();
        return _matches.Count;
    }

    public void ClearFind() { _matches.Clear(); _matchCur = -1; InvalidateVisual(); }
    public int MatchIndex => _matchCur;
    public int MatchCount => _matches.Count;

    /// <summary>Move to the next/previous match after the caret.</summary>
    public bool FindStep(bool forward)
    {
        if (_matches.Count == 0) return false;
        var (a, b) = Ordered();
        int found = -1;
        if (forward)
        {
            for (int i = 0; i < _matches.Count; i++)
                if (Cmp(new DocPos(_matches[i].p, _matches[i].s), b) >= 0) { found = i; break; }
            if (found < 0) found = 0;
        }
        else
        {
            for (int i = _matches.Count - 1; i >= 0; i--)
                if (Cmp(new DocPos(_matches[i].p, _matches[i].e), a) <= 0) { found = i; break; }
            if (found < 0) found = _matches.Count - 1;
        }
        SelectMatch(found);
        return true;
    }

    private void SelectMatch(int i)
    {
        _matchCur = i;
        var m = _matches[i];
        _anchor = new DocPos(m.p, m.s);
        SetCaret(new DocPos(m.p, m.e), true);
    }

    public bool ReplaceCurrent(string q, string with, bool matchCase)
    {
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (HasSelection && SelectedText().Equals(q, cmp))
        {
            Push("replace");
            var (a, _) = Ordered();
            var fmt = a.P.FormatAt(a.Off + 1);
            DeleteSelectionCore();
            _caret.P.Insert(_caret.Off, with, fmt);
            _caret = _anchor = new DocPos(_caret.P, _caret.Off + with.Length);
            Modified();
        }
        FindAll(q, matchCase);
        return FindStep(true);
    }

    public int ReplaceAll(string q, string with, bool matchCase)
    {
        if (string.IsNullOrEmpty(q)) return 0;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int n = 0;
        Push("replace");
        foreach (var p in _flat)
        {
            int i = 0;
            while (true)
            {
                var t = p.Text;
                i = t.IndexOf(q, i, cmp);
                if (i < 0) break;
                var fmt = p.FormatAt(i + 1);
                p.Delete(i, q.Length);
                p.Insert(i, with, fmt);
                i += Math.Max(1, with.Length); n++;
            }
        }
        if (n > 0) { _caret = _anchor = new DocPos(_flat[0], 0); Modified(); } else { Tab.Undo.RemoveAt(Tab.Undo.Count - 1); }
        return n;
    }

    public void GoTo(DocPos p) => SetCaret(p, false);
    public void FocusEditor() => Focus();

    /// <summary>Scroll offsets in the owning ScrollViewer are managed by the window.</summary>
    public RichDoc Document => Tab.Doc;
}
