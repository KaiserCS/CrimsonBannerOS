using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using RedStarOS.Services;
using Avalonia.Input.Platform;

namespace RedStarOS.Views;

/// <summary>
/// One terminal: a shell on a pty (PtySession), the xterm emulator (TermEmulator) and this control, which draws
/// the screen and turns keyboard / mouse input into what terminal programs expect.
/// </summary>
public sealed class TerminalView : Control
{
    public TermEmulator Emu { get; private set; }
    public PtySession? Pty { get; private set; }
    public bool HasExited { get; private set; }

    public event Action? TitleChanged;
    public event Action<int>? Exited;
    public event Action? BellRung;
    public event Action<string>? CommandRequested;      // "newtab", "find", "closetab"... handled by the window
    public event Action? ViewChanged;                   // scroll position / size (for the scrollbar)
    public event Action? FocusEntered;                  // this pane got the keyboard (split panes track the active one)

    public TerminalProfile Profile { get; private set; } = TerminalProfiles.DefaultProfile;
    private bool EffectiveBlink => !SettingsStore.Current.ReduceMotion && (Emu.CursorStyle == 0 ? Profile.CursorBlink : Emu.CursorBlink);

    private static readonly Regex UrlRx = new(@"(https?|ftp|file)://[^\s<>""'`|{}\\^]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── appearance ──
    private Typeface _face, _faceBold, _faceItalic, _faceBoldItalic;
    private double _fontSize = 14;
    private double _cw = 8, _ch = 17, _baseline = 13;
    private const double Pad = 6;
    private uint[] _palette = new uint[256];
    private uint _fg, _bg, _cursorColor, _selBg;
    private bool _dark = true;
    private int _themeVersion;

    // ── scrolling / selection ──
    private long _viewTopAbs;           // absolute index (scrollback lines dropped so far + index) of the top visible line
    private bool _follow = true;        // stick to the bottom while output arrives
    private (long Line, int Col)? _selA, _selB;
    private bool _selecting, _selWord, _selLine;
    private bool _blinkOn = true;
    private readonly DispatcherTimer _blink;
    private bool _bellFlash;

    // ── output from the pty ──
    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private int _drainQueued;
    private bool _renderQueued;
    private DateTime _syncSince;

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        SetFonts(Profile.FontFamily);
        Emu = new TermEmulator(80, 24, Profile.Scrollback);
        HookEmu();
        ApplyTheme(ThemeService.IsDark, SettingsStore.Current.Accent);
        MeasureCell();
        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; if (IsFocused && EffectiveBlink) InvalidateVisual(); };
        _ime = new ImeClient(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        AttachedToVisualTree += (_, _) => _blink.Start();
        DetachedFromVisualTree += (_, _) => _blink.Stop();
    }

    private void HookEmu()
    {
        Emu.Reply += s => Pty?.Write(s);
        Emu.TitleChanged += () => TitleChanged?.Invoke();
        Emu.Bell += () => { if (!Profile.VisualBell) { BellRung?.Invoke(); return; } _bellFlash = true; InvalidateVisual(); DispatcherTimer.RunOnce(() => { _bellFlash = false; InvalidateVisual(); }, TimeSpan.FromMilliseconds(120)); BellRung?.Invoke(); };
        Emu.ClipboardWrite += text => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
        Emu.AltScreenChanged += () => { _follow = true; ClearSelection(); ScrollToBottom(); };
        Emu.DefaultColorQuery = which => which switch
        {
            10 => _fg & 0xFFFFFF, 11 => _bg & 0xFFFFFF, 12 => _cursorColor & 0xFFFFFF,
            _ => which >= 100 && which < 356 ? _palette[which - 100] & 0xFFFFFF : 0
        };
    }

    // ═════════════════════════════════ start / stop ═════════════════════════════════
    public void Start(string? cwd, IList<string>? command = null)
    {
        var (cols, rows) = GridSize(Bounds.Size);
        Emu.Resize(cols, rows);
        var pty = new PtySession();
        pty.Output += (data, n) =>
        {
            _incoming.Enqueue(data);
            if (Interlocked.Exchange(ref _drainQueued, 1) == 0) Dispatcher.UIThread.Post(Drain, DispatcherPriority.Input);
        };
        pty.Exited += code => Dispatcher.UIThread.Post(() =>
        {
            Drain();
            HasExited = true;
            Exited?.Invoke(code);
        });
        Pty = pty;
        try
        {
            pty.Start(cols, rows, cwd, command);
            if (!pty.JobControl) Emu.Feed("\u001b[33m(setsid and python3 were not found: job control is not available)\u001b[0m\r\n");
        }
        catch (Exception ex)
        {
            Emu.Feed("\u001b[31mCouldn't start the shell: " + ex.Message + "\u001b[0m\r\n");
            HasExited = true;
        }
    }

    public void Close()
    {
        Pty?.Dispose();
        Pty = null;
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _drainQueued, 0);
        int budget = 4 * 1024 * 1024;           // keep the window responsive when a program floods the terminal
        long droppedBefore = Emu.Dropped;
        while (budget > 0 && _incoming.TryDequeue(out var chunk))
        {
            Emu.Feed(chunk, chunk.Length);
            budget -= chunk.Length;
        }
        if (!_incoming.IsEmpty && Interlocked.Exchange(ref _drainQueued, 1) == 0) Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);
        if (_follow) ScrollToBottom();
        else if (Emu.Dropped != droppedBefore) ClampView();
        _blinkOn = true;
        if (Emu.SyncOutput)
        {
            if (_syncSince == default) _syncSince = DateTime.UtcNow;
            if ((DateTime.UtcNow - _syncSince).TotalMilliseconds < 150) return;   // program is mid-update: wait for the end
        }
        _syncSince = default;
        QueueRender();
        if (_preedit != null || IsFocused) _ime.CursorMoved();
    }

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() => { _renderQueued = false; InvalidateVisual(); ViewChanged?.Invoke(); }, DispatcherPriority.Render);
    }

    // ═════════════════════════════════ theme / font ═════════════════════════════════
    private static readonly uint[] DarkAnsi =
    {
        0x1C1C1C, 0xF4707C, 0x7BD88F, 0xF2C811, 0x5AA9F5, 0xC792EA, 0x56D4DD, 0xD8D8D8,
        0x6B6B6B, 0xFF99A4, 0xA5E8B5, 0xFCE66D, 0x8CC4FF, 0xDDB3F5, 0x8FE8EE, 0xFFFFFF
    };
    private static readonly uint[] LightAnsi =
    {
        0x1A1A1A, 0xC50F1F, 0x107C10, 0x946B00, 0x0050B3, 0x881798, 0x0E7C86, 0x8A8A8A,
        0x5F5F5F, 0xE81123, 0x13A10E, 0xB88700, 0x0078D4, 0xB146C2, 0x2AA7B3, 0xC8C8C8
    };

    private void SetFonts(string family)
    {
        var f = new FontFamily(string.IsNullOrWhiteSpace(family) ? TerminalProfiles.DefaultFonts : family + ", " + TerminalProfiles.DefaultFonts);
        _face = new Typeface(f);
        _faceBold = new Typeface(f, FontStyle.Normal, FontWeight.Bold);
        _faceItalic = new Typeface(f, FontStyle.Italic);
        _faceBoldItalic = new Typeface(f, FontStyle.Italic, FontWeight.Bold);
    }

    /// <summary>Uses a profile's font, colours, cursor and bell (zoom is added to its font size).</summary>
    public void ApplyProfile(TerminalProfile p, double zoom)
    {
        Profile = p;
        if (Pty == null && Emu.Scrollback.Capacity != Math.Max(100, p.Scrollback))
        {
            // not started yet: the scrollback size can still change
            Emu = new TermEmulator(Emu.Cols, Emu.Rows, Math.Clamp(p.Scrollback, 100, 200000));
            HookEmu();
        }
        SetFonts(p.FontFamily);
        _fontSize = Math.Clamp(p.FontSize + zoom, 7, 40);
        ApplyTheme(ThemeService.IsDark, SettingsStore.Current.Accent);
        MeasureCell();
        OnResized();
        InvalidateVisual();
    }

    public void ApplyTheme(bool dark, string accentHex)
    {
        var scheme = TerminalProfiles.Scheme(Profile.Scheme);
        _dark = scheme.FollowsTheme ? dark : Lum(scheme.Bg) < 0.5;
        var ansi = scheme.Ansi ?? (dark ? DarkAnsi : LightAnsi);
        for (int i = 0; i < 16; i++) _palette[i] = ansi[i];
        int[] steps = { 0, 95, 135, 175, 215, 255 };
        for (int i = 0; i < 216; i++) _palette[16 + i] = (uint)((steps[i / 36] << 16) | (steps[i / 6 % 6] << 8) | steps[i % 6]);
        for (int i = 0; i < 24; i++) { int v = 8 + i * 10; _palette[232 + i] = (uint)((v << 16) | (v << 8) | v); }
        _fg = scheme.FollowsTheme ? (dark ? 0xE6E6E6u : 0x1A1A1Au) : scheme.Fg;
        _bg = scheme.FollowsTheme ? (dark ? 0x1C1C1Cu : 0xFFFFFFu) : scheme.Bg;
        uint accent = 0x0078D4;
        if (Color.TryParse(accentHex, out var ac)) accent = (uint)((ac.R << 16) | (ac.G << 8) | ac.B);
        _cursorColor = scheme.Cursor ?? accent;
        _selBg = accent;
        _themeVersion++;
        _brushes.Clear();
        InvalidateVisual();
    }

    private static double Lum(uint c) => (0.299 * ((c >> 16) & 0xFF) + 0.587 * ((c >> 8) & 0xFF) + 0.114 * (c & 0xFF)) / 255;

    public uint BackgroundRgb => _bg;

    public double FontSizeValue
    {
        get => _fontSize;
        set { _fontSize = Math.Clamp(value, 7, 40); MeasureCell(); OnResized(); InvalidateVisual(); }
    }

    private void MeasureCell()
    {
        var ft = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, _fontSize, Brushes.Black);
        _cw = Math.Max(1, ft.WidthIncludingTrailingWhitespace / 10);
        _ch = Math.Ceiling(ft.Height * 1.05);
        _baseline = ft.Baseline;
        _rowCache.Clear();
    }

    public (int cols, int rows) GridSize(Size s)
    {
        if (s.Width < 10 || s.Height < 10) return (80, 24);
        return (Math.Max(10, (int)((s.Width - Pad * 2) / _cw)), Math.Max(3, (int)((s.Height - Pad * 2) / _ch)));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        OnResized();
    }

    private void OnResized()
    {
        var (cols, rows) = GridSize(Bounds.Size);
        if (cols == Emu.Cols && rows == Emu.Rows) return;
        Emu.Resize(cols, rows);
        Pty?.Resize(cols, rows);
        _rowCache.Clear();
        if (_follow) ScrollToBottom(); else ClampView();
        QueueRender();
    }

    // ═════════════════════════════════ scrolling ═════════════════════════════════
    public int ViewTop => (int)Math.Clamp(_viewTopAbs - Emu.Dropped, 0, Math.Max(0, Emu.TotalLines - Emu.Rows));
    public int MaxViewTop => Math.Max(0, Emu.TotalLines - Emu.Rows);

    public void ScrollToBottom()
    {
        _viewTopAbs = Emu.Dropped + MaxViewTop;
        _follow = true;
    }

    public void ScrollTo(int top)
    {
        top = Math.Clamp(top, 0, MaxViewTop);
        _viewTopAbs = Emu.Dropped + top;
        _follow = top >= MaxViewTop;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ScrollBy(int lines) => ScrollTo(ViewTop + lines);

    private void ClampView()
    {
        long min = Emu.Dropped, max = Emu.Dropped + MaxViewTop;
        _viewTopAbs = Math.Clamp(_viewTopAbs, min, max);
    }

    // ═════════════════════════════════ drawing ═════════════════════════════════
    private readonly Dictionary<uint, IBrush> _brushes = new();
    private IBrush Brush(uint rgb, byte alpha = 255)
    {
        uint key = ((uint)alpha << 24) | (rgb & 0xFFFFFF);
        if (!_brushes.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)).ToImmutable();
            _brushes[key] = b;
        }
        return b;
    }

    private uint Resolve(uint c, bool fg, bool bold)
    {
        int kind = (int)(c >> 24);
        if (kind == 0) return fg ? _fg : _bg;
        if (kind == 1)
        {
            int i = (int)(c & 0xFF);
            if (bold && fg && i < 8 && Profile.BoldIsBright) i += 8;    // bold draws the bright colour, like xterm
            return Emu.PaletteOverrides.TryGetValue(i, out var o) ? o : _palette[i];
        }
        return c & 0xFFFFFF;
    }

    private sealed class Run { public double X; public double W; public uint Bg; public bool HasBg; public FormattedText? Text; public uint Fg; public CellFlags Flags; public bool Underline, Strike, Overline; public bool Link; }
    private sealed class RowCache { public int Version; public int Theme; public string SelKey = ""; public List<Run> Runs = new(); }
    private readonly Dictionary<TermLine, RowCache> _rowCache = new();

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        bool reverse = Emu.ReverseVideo;
        ctx.FillRectangle(Brush(reverse ? _fg : _bg), bounds);
        if (_rowCache.Count > 4000) _rowCache.Clear();

        int top = ViewTop;
        int rows = Emu.Rows;
        var sel = SelectionRange();
        for (int r = 0; r < rows; r++)
        {
            int idx = top + r;
            if (idx >= Emu.TotalLines) break;
            var line = Emu.GetLine(idx);
            long abs = Emu.Dropped + idx;
            int selFrom = -1, selTo = -1;
            if (sel is { } s && abs >= s.a.Line && abs <= s.b.Line)
            {
                selFrom = abs == s.a.Line ? s.a.Col : 0;
                selTo = abs == s.b.Line ? s.b.Col : Emu.Cols;
            }
            string selKey = selFrom < 0 ? "" : selFrom + ":" + selTo;
            if (!_rowCache.TryGetValue(line, out var rc) || rc.Version != line.Version || rc.Theme != _themeVersion || rc.SelKey != selKey || reverse)
            {
                rc = BuildRow(line, selFrom, selTo, reverse);
                rc.Version = line.Version; rc.Theme = _themeVersion; rc.SelKey = selKey;
                _rowCache[line] = rc;
            }
            double y = Pad + r * _ch;
            foreach (var run in rc.Runs)
                if (run.HasBg) ctx.FillRectangle(Brush(run.Bg), new Rect(Pad + run.X, y, run.W + 0.5, _ch));
            foreach (var run in rc.Runs)
            {
                if (run.Text != null) ctx.DrawText(run.Text, new Point(Pad + run.X, y + (_ch - run.Text.Height) / 2));
                if (run.Underline || run.Link) ctx.DrawLine(new Pen(Brush(run.Fg), 1), new Point(Pad + run.X, y + _ch - 2.5), new Point(Pad + run.X + run.W, y + _ch - 2.5));
                if (run.Flags.HasFlag(CellFlags.DoubleUnderline)) ctx.DrawLine(new Pen(Brush(run.Fg), 1), new Point(Pad + run.X, y + _ch - 0.5), new Point(Pad + run.X + run.W, y + _ch - 0.5));
                if (run.Strike) ctx.DrawLine(new Pen(Brush(run.Fg), 1), new Point(Pad + run.X, y + _ch / 2), new Point(Pad + run.X + run.W, y + _ch / 2));
                if (run.Overline) ctx.DrawLine(new Pen(Brush(run.Fg), 1), new Point(Pad + run.X, y + 0.5), new Point(Pad + run.X + run.W, y + 0.5));
            }
        }

        DrawCursor(ctx, top);
        DrawPreedit(ctx, top);
        if (_bellFlash) ctx.FillRectangle(Brush(_dark ? 0xFFFFFFu : 0x000000u, 28), bounds);
    }

    private RowCache BuildRow(TermLine line, int selFrom, int selTo, bool reverse)
    {
        var rc = new RowCache();
        var cells = line.Cells;
        int n = Math.Min(cells.Length, Emu.Cols);
        int i = 0;
        var text = new StringBuilder();
        while (i < n)
        {
            ref var c0 = ref cells[i];
            if (c0.Width == 0) { i++; continue; }
            var a = c0.Attr;
            bool selected = i >= selFrom && i < selTo;
            CellColors(a, selected, reverse, out uint fg, out uint bg, out bool hasBg, out byte fgAlpha);

            // a run: same look, plain narrow characters (wide / non-ASCII ones are drawn on their own at their cell)
            int start = i;
            text.Clear();
            bool simple = c0.Ch < 0x2500 && c0.Width == 1 && c0.Extra == null;
            if (simple)
            {
                while (i < n)
                {
                    ref var c = ref cells[i];
                    if (c.Width != 1 || c.Extra != null || c.Ch >= 0x2500) break;
                    if (c.Attr != a || (i >= selFrom && i < selTo) != selected) break;
                    text.Append(c.Ch == 0 ? ' ' : (char)c.Ch);
                    i++;
                }
            }
            else
            {
                text.Append(c0.Ch == 0 ? " " : c0.Text);
                i += c0.Width == 2 ? 2 : 1;
            }
            int cols = i - start;
            var run = new Run { X = start * _cw, W = cols * _cw, Bg = bg, HasBg = hasBg, Fg = fg, Flags = a.Flags,
                Underline = (a.Flags & CellFlags.Underline) != 0, Strike = (a.Flags & CellFlags.Strike) != 0,
                Overline = (a.Flags & CellFlags.Overline) != 0, Link = a.Link != 0 };
            string s = text.ToString();
            if ((a.Flags & CellFlags.Hidden) == 0 && s.Trim().Length > 0)
            {
                bool bold = (a.Flags & CellFlags.Bold) != 0, italic = (a.Flags & CellFlags.Italic) != 0;
                var face = bold ? (italic ? _faceBoldItalic : _faceBold) : (italic ? _faceItalic : _face);
                run.Text = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _fontSize, Brush(fg, fgAlpha));
                if (!simple && run.Text.WidthIncludingTrailingWhitespace > run.W * 1.15 && run.W > 0)
                {
                    // a glyph from a fallback font that is too wide for its cell(s): shrink it to fit
                    run.Text.SetFontSize(_fontSize * run.W / run.Text.WidthIncludingTrailingWhitespace);
                }
            }
            rc.Runs.Add(run);
        }
        return rc;
    }

    private void CellColors(TermAttr a, bool selected, bool reverse, out uint fg, out uint bg, out bool hasBg, out byte alpha)
    {
        bool bold = (a.Flags & CellFlags.Bold) != 0;
        fg = Resolve(a.Fg, true, bold);
        bg = Resolve(a.Bg, false, false);
        hasBg = a.Bg != 0;
        bool inv = ((a.Flags & CellFlags.Inverse) != 0) ^ reverse;
        if (inv) { (fg, bg) = (bg, fg); hasBg = true; }
        alpha = (a.Flags & CellFlags.Dim) != 0 ? (byte)150 : (byte)255;
        if (selected)
        {
            bg = _selBg; hasBg = true;
            fg = 0xFFFFFF; alpha = 255;
        }
    }

    private void DrawCursor(DrawingContext ctx, int top)
    {
        if (!Emu.CursorVisible || HasExited) return;
        int row = Emu.ScrollbackCount + Emu.CursorY - top;
        if (row < 0 || row >= Emu.Rows) return;
        int col = Emu.CursorX;
        var line = Emu.ScreenLine(Emu.CursorY);
        int w = col < line.Cells.Length && line.Cells[col].Width == 2 ? 2 : 1;
        var rect = new Rect(Pad + col * _cw, Pad + row * _ch, _cw * w, _ch);
        var brush = Brush(_cursorColor);
        if (!IsFocused) { ctx.DrawRectangle(null, new Pen(brush, 1), rect.Deflate(0.5)); return; }
        if (EffectiveBlink && !_blinkOn) return;
        int style = Emu.CursorStyle;
        if (style == 0) style = Profile.CursorShape == "Underline" ? 4 : Profile.CursorShape == "Bar" ? 6 : 2;
        if (style == 3 || style == 4) ctx.FillRectangle(brush, new Rect(rect.X, rect.Bottom - 2, rect.Width, 2));
        else if (style == 5 || style == 6) ctx.FillRectangle(brush, new Rect(rect.X, rect.Y, 2, rect.Height));
        else
        {
            ctx.FillRectangle(brush, rect);
            var c = col < line.Cells.Length ? line.Cells[col] : default;
            if (c.Ch != 0 && c.Width != 0)
            {
                var ft = new FormattedText(c.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    (c.Attr.Flags & CellFlags.Bold) != 0 ? _faceBold : _face, _fontSize, Brush(_bg));
                ctx.DrawText(ft, new Point(rect.X, rect.Y + (_ch - ft.Height) / 2));
            }
        }
    }

    // ═════════════════════════════════ input methods (IME) ═════════════════════════════════
    private readonly ImeClient _ime;
    private string? _preedit;

    /// <summary>Lets input methods (Chinese, Japanese, Korean...) show the text being composed at the cursor and place
    /// their candidate window there. The finished text arrives as normal TextInput.</summary>
    private sealed class ImeClient : TextInputMethodClient
    {
        private readonly TerminalView _v;
        public ImeClient(TerminalView v) { _v = v; }
        public override Visual TextViewVisual => _v;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => _v.CursorRect();
        public override TextSelection Selection { get => new TextSelection(0, 0); set { } }
        public override void SetPreeditText(string? preeditText)
        {
            _v._preedit = string.IsNullOrEmpty(preeditText) ? null : preeditText;
            _v.InvalidateVisual();
        }
        public void CursorMoved() => RaiseCursorRectangleChanged();
    }

    private Rect CursorRect()
    {
        int row = Emu.ScrollbackCount + Emu.CursorY - ViewTop;
        return new Rect(Pad + Emu.CursorX * _cw, Pad + Math.Clamp(row, 0, Emu.Rows - 1) * _ch, _cw, _ch);
    }

    private void DrawPreedit(DrawingContext ctx, int top)
    {
        if (_preedit == null) return;
        var r = CursorRect();
        var ft = new FormattedText(_preedit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, _fontSize, Brush(_fg));
        var box = new Rect(r.X, r.Y, Math.Max(ft.WidthIncludingTrailingWhitespace, _cw), _ch);
        ctx.FillRectangle(Brush(_bg), box);
        ctx.DrawText(ft, new Point(r.X, r.Y + (_ch - ft.Height) / 2));
        ctx.DrawLine(new Pen(Brush(_cursorColor), 1.5), new Point(box.X, box.Bottom - 1.5), new Point(box.Right, box.Bottom - 1.5));
    }

    // ═════════════════════════════════ selection ═════════════════════════════════
    private ((long Line, int Col) a, (long Line, int Col) b)? SelectionRange()
    {
        if (_selA is not { } a || _selB is not { } b) return null;
        if (a == b && !_selWord && !_selLine) return null;
        if (b.Line < a.Line || (b.Line == a.Line && b.Col < a.Col)) (a, b) = (b, a);
        if (_selLine) { a = (a.Line, 0); b = (b.Line, Emu.Cols); }
        return (a, b);
    }

    public bool HasSelection => SelectionRange() != null;

    public void ClearSelection() { _selA = _selB = null; _selWord = _selLine = false; InvalidateVisual(); }

    public void SelectAll()
    {
        _selA = (Emu.Dropped, 0);
        _selB = (Emu.Dropped + Emu.TotalLines - 1, Emu.Cols);
        _selWord = false; _selLine = false;
        InvalidateVisual();
    }

    public string SelectedText()
    {
        if (SelectionRange() is not { } s) return "";
        var sb = new StringBuilder();
        for (long abs = s.a.Line; abs <= s.b.Line; abs++)
        {
            int idx = (int)(abs - Emu.Dropped);
            if (idx < 0 || idx >= Emu.TotalLines) continue;
            var line = Emu.GetLine(idx);
            int from = abs == s.a.Line ? s.a.Col : 0;
            int to = abs == s.b.Line ? s.b.Col : Emu.Cols;
            bool lastLine = abs == s.b.Line;
            sb.Append(line.GetText(from, to, trimEnd: !line.Wrapped || lastLine));
            if (!lastLine && !line.Wrapped) sb.Append('\n');
        }
        return sb.ToString();
    }

    private (long Line, int Col) CellAt(Point p, bool roundToNearest)
    {
        int row = (int)Math.Floor((p.Y - Pad) / _ch);
        row = Math.Clamp(row, 0, Emu.Rows - 1);
        double fx = (p.X - Pad) / _cw;
        int col = roundToNearest ? (int)Math.Round(fx) : (int)Math.Floor(fx);
        col = Math.Clamp(col, 0, Emu.Cols);
        return (Emu.Dropped + ViewTop + row, col);
    }

    private static bool IsWordChar(int ch) => ch != 0 && (char.IsLetterOrDigit(char.ConvertFromUtf32(ch), 0) || "-_.~/:@%+#?=&".IndexOf((char)Math.Min(ch, 0xFFFF)) >= 0);

    private void ExpandWord()
    {
        if (_selA is not { } a || _selB is not { } b) return;
        if (b.Line < a.Line || (b.Line == a.Line && b.Col < a.Col)) (a, b) = (b, a);
        var la = Emu.GetLine((int)Math.Clamp(a.Line - Emu.Dropped, 0, Emu.TotalLines - 1));
        var lb = Emu.GetLine((int)Math.Clamp(b.Line - Emu.Dropped, 0, Emu.TotalLines - 1));
        int x0 = Math.Min(a.Col, Emu.Cols - 1), x1 = Math.Max(Math.Min(b.Col, Emu.Cols - 1), x0);
        if (IsWordChar(la.Cells[x0].Ch)) while (x0 > 0 && IsWordChar(la.Cells[x0 - 1].Ch)) x0--;
        if (IsWordChar(lb.Cells[x1].Ch)) while (x1 < Emu.Cols - 1 && IsWordChar(lb.Cells[x1 + 1].Ch)) x1++;
        _selA = (a.Line, x0); _selB = (b.Line, x1 + 1);
    }

    // ═════════════════════════════════ clipboard ═════════════════════════════════
    public async void CopySelection()
    {
        var t = SelectedText();
        if (t.Length == 0) return;
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb != null) await cb.SetTextAsync(t);
    }

    public async void PasteClipboard()
    {
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb == null) return;
        string? text = null;
        try { text = await cb.TryGetTextAsync(); } catch { }
        if (!string.IsNullOrEmpty(text)) Paste(text);
    }

    public void Paste(string text)
    {
        if (Pty == null) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (Emu.BracketedPaste) text = "\u001b[200~" + text.Replace("\u001b[201~", "") + "\u001b[201~";
        _follow = true; ScrollToBottom();
        Pty.Write(text);
    }

    // ═════════════════════════════════ find ═════════════════════════════════
    /// <summary>Selects the next match (searching up from the selection, or down when forward). Returns false if none.</summary>
    public bool Find(string query, bool backwards, bool matchCase)
    {
        if (string.IsNullOrEmpty(query)) return false;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int total = Emu.TotalLines;
        int startLine; int startCol;
        if (SelectionRange() is { } s)
        {
            startLine = (int)(s.a.Line - Emu.Dropped);
            startCol = backwards ? s.a.Col - 1 : s.a.Col + 1;
        }
        else { startLine = backwards ? total - 1 : 0; startCol = backwards ? int.MaxValue : 0; }
        for (int k = 0; k <= total; k++)
        {
            int li = backwards ? startLine - k : startLine + k;
            li = ((li % total) + total) % total;
            var text = Emu.GetLine(li).GetText(0, int.MaxValue, false);
            int pos;
            try
            {
            if (k == 0)
                pos = backwards ? (startCol < 0 ? -1 : text.LastIndexOf(query, Math.Min(startCol, Math.Max(0, text.Length - 1)), cmp)) : (startCol < text.Length ? text.IndexOf(query, Math.Max(0, startCol), cmp) : -1);
            else pos = backwards ? text.LastIndexOf(query, cmp) : text.IndexOf(query, cmp);
            }
            catch (ArgumentOutOfRangeException) { pos = -1; }
            if (pos < 0) continue;
            int col = ColumnOfTextIndex(Emu.GetLine(li), pos);
            int endCol = ColumnOfTextIndex(Emu.GetLine(li), pos + query.Length);
            _selA = (Emu.Dropped + li, col); _selB = (Emu.Dropped + li, endCol);
            _selWord = _selLine = false;
            if (li < ViewTop || li >= ViewTop + Emu.Rows) ScrollTo(li - Emu.Rows / 2);
            InvalidateVisual();
            return true;
        }
        return false;
    }

    private static int ColumnOfTextIndex(TermLine line, int index)
    {
        int ti = 0;
        for (int c = 0; c < line.Cells.Length; c++)
        {
            ref var cell = ref line.Cells[c];
            if (cell.Width == 0) continue;
            if (ti >= index) return c;
            ti += cell.Ch == 0 ? 1 : cell.Text.Length;
        }
        return line.Cells.Length;
    }

    // ═════════════════════════════════ keyboard ═════════════════════════════════
    private bool _altPending;
    private string? _altFallback;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Pty == null) return;
        var m = e.KeyModifiers;
        bool ctrl = m.HasFlag(KeyModifiers.Control), shift = m.HasFlag(KeyModifiers.Shift), alt = m.HasFlag(KeyModifiers.Alt);

        // terminal-window shortcuts
        if (ctrl && shift)
        {
            switch (e.Key)
            {
                case Key.C: CopySelection(); e.Handled = true; return;
                case Key.V: PasteClipboard(); e.Handled = true; return;
                case Key.A: SelectAll(); e.Handled = true; return;
                case Key.F: CommandRequested?.Invoke("find"); e.Handled = true; return;
                case Key.T: CommandRequested?.Invoke("newtab"); e.Handled = true; return;
                case Key.W: CommandRequested?.Invoke("closepane"); e.Handled = true; return;
                case Key.E: CommandRequested?.Invoke("splitright"); e.Handled = true; return;
                case Key.O: CommandRequested?.Invoke("splitdown"); e.Handled = true; return;
                case Key.Z: CommandRequested?.Invoke("zoompane"); e.Handled = true; return;
                case Key.K: Emu.ClearScrollback(); ScrollToBottom(); InvalidateVisual(); e.Handled = true; return;
            }
        }
        if (shift && !ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.PageUp: ScrollBy(-(Emu.Rows - 1)); e.Handled = true; return;
                case Key.PageDown: ScrollBy(Emu.Rows - 1); e.Handled = true; return;
                case Key.Home: ScrollTo(0); e.Handled = true; return;
                case Key.End: ScrollToBottom(); InvalidateVisual(); ViewChanged?.Invoke(); e.Handled = true; return;
                case Key.Insert: PasteClipboard(); e.Handled = true; return;
            }
        }
        if (ctrl && !shift && e.Key == Key.Insert) { CopySelection(); e.Handled = true; return; }

        string? seq = KeySequence(e.Key, ctrl, shift, alt);
        if (seq != null)
        {
            Send(seq);
            e.Handled = true;
            return;
        }
        // Alt+character: ESC prefix. The character itself comes with TextInput (it respects the keyboard layout).
        if (alt && !ctrl && IsCharKey(e.Key))
        {
            _altPending = true;
            _altFallback = KeyChar(e.Key, shift);
            e.Handled = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (_altPending && _altFallback != null) { Send("\u001b" + _altFallback); }
                _altPending = false;
            }, DispatcherPriority.Background);
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Pty == null || string.IsNullOrEmpty(e.Text)) return;
        var t = e.Text;
        if (_altPending) { t = "\u001b" + t; _altPending = false; }
        Send(t);
        e.Handled = true;
    }

    private void Send(string s)
    {
        if (Pty == null || HasExited) return;
        if (!_follow || ViewTop != MaxViewTop) { ScrollToBottom(); InvalidateVisual(); ViewChanged?.Invoke(); }
        if (HasSelection && !_selecting) ClearSelection();
        _blinkOn = true;
        Pty.Write(s);
    }

    private static bool IsCharKey(Key k) => (k >= Key.A && k <= Key.Z) || (k >= Key.D0 && k <= Key.D9) ||
        k is Key.OemMinus or Key.OemPlus or Key.OemComma or Key.OemPeriod or Key.OemQuestion or Key.OemSemicolon or Key.OemQuotes or Key.OemOpenBrackets or Key.OemCloseBrackets or Key.OemPipe or Key.OemTilde or Key.OemBackslash;

    private static string? KeyChar(Key k, bool shift)
    {
        if (k >= Key.A && k <= Key.Z) { char c = (char)('a' + (k - Key.A)); return (shift ? char.ToUpperInvariant(c) : c).ToString(); }
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        return k switch
        {
            Key.OemMinus => shift ? "_" : "-", Key.OemPlus => shift ? "+" : "=", Key.OemComma => shift ? "<" : ",",
            Key.OemPeriod => shift ? ">" : ".", Key.OemQuestion => shift ? "?" : "/", Key.OemSemicolon => shift ? ":" : ";",
            Key.OemQuotes => shift ? "\"" : "'", Key.OemOpenBrackets => shift ? "{" : "[", Key.OemCloseBrackets => shift ? "}" : "]",
            Key.OemPipe or Key.OemBackslash => shift ? "|" : "\\", Key.OemTilde => shift ? "~" : "`",
            _ => null
        };
    }

    /// <summary>The bytes xterm sends for a special key (or a Ctrl+key), or null when TextInput should handle it.</summary>
    private string? KeySequence(Key k, bool ctrl, bool shift, bool alt)
    {
        const string E = "\u001b";
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        string Csi(string final) => mod > 1 ? $"{E}[1;{mod}{final}" : (Emu.AppCursorKeys ? $"{E}O{final}" : $"{E}[{final}");
        string Tilde(int n) => mod > 1 ? $"{E}[{n};{mod}~" : $"{E}[{n}~";
        string Ss3(string final) => mod > 1 ? $"{E}[1;{mod}{final}" : $"{E}O{final}";
        string A(string s) => alt ? E + s : s;

        switch (k)
        {
            case Key.Up: return Csi("A");
            case Key.Down: return Csi("B");
            case Key.Right: return Csi("C");
            case Key.Left: return Csi("D");
            case Key.Home: return Csi("H");
            case Key.End: return Csi("F");
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return Ss3("P");
            case Key.F2: return Ss3("Q");
            case Key.F3: return Ss3("R");
            case Key.F4: return Ss3("S");
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
            case Key.Enter: return A("\r");
            case Key.Back: return A(ctrl ? "\b" : "\u007f");
            case Key.Tab: return shift ? $"{E}[Z" : (ctrl ? null : A("\t"));
            case Key.Escape: return A(E);
        }
        if (ctrl && !shift && !alt)       // Ctrl+Alt is AltGr on many layouts: its character comes as text
        {
            if (k >= Key.A && k <= Key.Z) return A(((char)(k - Key.A + 1)).ToString());
            switch (k)
            {
                case Key.Space: case Key.D2: return A("\0");
                case Key.OemOpenBrackets: case Key.D3: return A(E);
                case Key.OemPipe: case Key.OemBackslash: case Key.D4: return A("\u001c");
                case Key.OemCloseBrackets: case Key.D5: return A("\u001d");
                case Key.D6: return A("\u001e");
                case Key.OemMinus: case Key.OemQuestion: case Key.D7: return A("\u001f");
                case Key.D8: return A("\u007f");
            }
        }
        if (ctrl && shift && k == Key.Space) return "\0";
        return null;
    }

    // ═════════════════════════════════ mouse ═════════════════════════════════
    private int _lastMouseCol = -1, _lastMouseRow = -1;
    private int _pressedButton = -1;

    private bool Reporting(KeyModifiers m) => Emu.MouseMode != 0 && !m.HasFlag(KeyModifiers.Shift);

    private void SendMouse(int button, bool press, Point p, KeyModifiers m, bool motion = false)
    {
        int col = Math.Clamp((int)((p.X - Pad) / _cw), 0, Emu.Cols - 1);
        int row = Math.Clamp((int)((p.Y - Pad) / _ch), 0, Emu.Rows - 1);
        if (motion && col == _lastMouseCol && row == _lastMouseRow) return;
        _lastMouseCol = col; _lastMouseRow = row;
        int b = button;
        if (Emu.MouseMode == 9) { if (!press || motion) return; }
        else
        {
            if (m.HasFlag(KeyModifiers.Shift)) b |= 4;
            if (m.HasFlag(KeyModifiers.Alt)) b |= 8;
            if (m.HasFlag(KeyModifiers.Control)) b |= 16;
        }
        if (motion) b |= 32;
        if (Emu.MouseEncoding == 1006)
        {
            Pty?.Write($"\u001b[<{b};{col + 1};{row + 1}{(press ? 'M' : 'm')}");
            return;
        }
        if (!press) b = (b & ~3) | 3;     // release: button 3 in the older encodings
        if (Emu.MouseEncoding == 1015) { Pty?.Write($"\u001b[{b + 32};{col + 1};{row + 1}M"); return; }
        if (Emu.MouseEncoding == 1005)
        {
            Pty?.Write("\u001b[M" + (char)(b + 32) + (char)(col + 33) + (char)(row + 33));
            return;
        }
        if (col > 222 || row > 222) return;
        Pty?.Write(new byte[] { 0x1b, (byte)'[', (byte)'M', (byte)(b + 32), (byte)(col + 33), (byte)(row + 33) });
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        var props = pt.Properties;
        var pos = pt.Position;
        var mods = e.KeyModifiers;

        if (props.IsRightButtonPressed && !Reporting(mods))
        {
            ShowMenu(pos);
            e.Handled = true;
            return;
        }
        if (Reporting(mods))
        {
            int btn = props.IsLeftButtonPressed ? 0 : props.IsMiddleButtonPressed ? 1 : 2;
            _pressedButton = btn;
            SendMouse(btn, true, pos, mods);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (props.IsMiddleButtonPressed)
        {
            var t = SelectedText();
            if (t.Length > 0) Paste(t); else PasteClipboard();
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (mods.HasFlag(KeyModifiers.Control) && LinkUnder(pos) is { } link)
        {
            OpenLink(link);
            e.Handled = true;
            return;
        }

        var cell = CellAt(pos, roundToNearest: e.ClickCount == 1);
        if (e.ClickCount >= 3)
        {
            _selLine = true; _selWord = false;
            _selA = _selB = (cell.Line, 0);
        }
        else if (e.ClickCount == 2)
        {
            _selWord = true; _selLine = false;
            _selA = _selB = CellAt(pos, false);
            ExpandWord();
        }
        else if (mods.HasFlag(KeyModifiers.Shift) && _selA != null)
        {
            _selB = cell;
        }
        else
        {
            _selWord = _selLine = false;
            _selA = _selB = cell;
        }
        _selecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetCurrentPoint(this);
        var pos = pt.Position;
        if (Emu.MouseMode != 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if ((Emu.MouseMode == 1002 && _pressedButton >= 0) || Emu.MouseMode == 1003)
                SendMouse(_pressedButton >= 0 ? _pressedButton : 3, true, pos, e.KeyModifiers, motion: true);
            return;
        }
        Cursor = e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkUnder(pos) != null ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);
        if (!_selecting) return;
        // drag above / below the view scrolls
        if (pos.Y < 0) ScrollBy(-1); else if (pos.Y > Bounds.Height) ScrollBy(1);
        if (_selLine) _selB = (CellAt(pos, false).Line, Emu.Cols);
        else if (_selWord) { _selB = CellAt(pos, false); ExpandWord(); }
        else _selB = CellAt(pos, true);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedButton >= 0)
        {
            SendMouse(_pressedButton, false, e.GetPosition(this), e.KeyModifiers);
            _pressedButton = -1;
        }
        if (_selecting)
        {
            _selecting = false;
            if (!HasSelection) ClearSelection();
        }
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int lines = (int)Math.Round(-e.Delta.Y * 3);
        if (lines == 0) lines = e.Delta.Y > 0 ? -1 : 1;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            CommandRequested?.Invoke(e.Delta.Y > 0 ? "zoomin" : "zoomout");
        }
        else if (Reporting(e.KeyModifiers) && Emu.MouseMode != 9)
        {
            var pos = e.GetPosition(this);
            for (int i = 0; i < Math.Abs(lines); i++) SendMouse(lines < 0 ? 64 : 65, true, pos, e.KeyModifiers);
        }
        else if (Emu.IsAltScreen && Emu.AlternateScroll)
        {
            var key = lines < 0 ? (Emu.AppCursorKeys ? "\u001bOA" : "\u001b[A") : (Emu.AppCursorKeys ? "\u001bOB" : "\u001b[B");
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Abs(lines); i++) sb.Append(key);
            Pty?.Write(sb.ToString());
        }
        else ScrollBy(lines);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        FocusEntered?.Invoke();
        _blinkOn = true;
        if (Emu.FocusReporting) Pty?.Write("\u001b[I");
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (Emu.FocusReporting) Pty?.Write("\u001b[O");
        InvalidateVisual();
    }

    // ═════════════════════════════════ links & menu ═════════════════════════════════
    private string? LinkUnder(Point p)
    {
        var (line, col) = CellAt(p, false);
        int idx = (int)(line - Emu.Dropped);
        if (Emu.LinkAt(idx, col) is { } osc8) return osc8;
        if (idx < 0 || idx >= Emu.TotalLines) return null;
        // plain URLs in the text (joined across soft-wrapped rows)
        int first = idx;
        while (first > 0 && Emu.GetLine(first - 1).Wrapped) first--;
        var sb = new StringBuilder();
        int offset = 0;
        for (int i = first; i < Emu.TotalLines; i++)
        {
            var l = Emu.GetLine(i);
            if (i == idx) offset = sb.Length + TextIndexOfColumn(l, col);
            sb.Append(l.GetText(0, int.MaxValue, trimEnd: !l.Wrapped));
            if (!l.Wrapped || i - first > 20) break;
        }
        foreach (Match m in UrlRx.Matches(sb.ToString()))
        {
            if (offset >= m.Index && offset < m.Index + m.Length)
                return m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '\'', '"', '!', '?');
        }
        return null;
    }

    private static int TextIndexOfColumn(TermLine line, int col)
    {
        int ti = 0;
        for (int c = 0; c < Math.Min(col, line.Cells.Length); c++)
        {
            ref var cell = ref line.Cells[c];
            if (cell.Width == 0) continue;
            ti += cell.Ch == 0 ? 1 : cell.Text.Length;
        }
        return ti;
    }

    private static void OpenLink(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            BrowserLauncher.Launch(url);
        else if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { FileOpener.Open(new Uri(url).LocalPath); } catch { }
        }
        else ProcessUtil.StartDetached("xdg-open", new[] { url }, AppPaths.Home);
    }

    private void ShowMenu(Point pos)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string? gesture, Action act, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (gesture != null) mi.InputGesture = KeyGesture.Parse(gesture);
            mi.Click += (_, _) => act();
            return mi;
        }
        var link = LinkUnder(pos);
        if (link != null)
        {
            menu.Items.Add(Item("Open link", null, () => OpenLink(link)));
            menu.Items.Add(Item("Copy link", null, () => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(link)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item("Copy", "Ctrl+Shift+C", CopySelection, HasSelection));
        menu.Items.Add(Item("Paste", "Ctrl+Shift+V", PasteClipboard));
        menu.Items.Add(Item("Select all", "Ctrl+Shift+A", SelectAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Find…", "Ctrl+Shift+F", () => CommandRequested?.Invoke("find")));
        menu.Items.Add(Item("Clear scrollback", "Ctrl+Shift+K", () => { Emu.ClearScrollback(); ScrollToBottom(); InvalidateVisual(); ViewChanged?.Invoke(); }));
        menu.Items.Add(Item("Reset terminal", null, () => { Emu.FullReset(); Pty?.Write("\u000c"); InvalidateVisual(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("New tab", "Ctrl+Shift+T", () => CommandRequested?.Invoke("newtab")));
        menu.Items.Add(Item("Split right", "Ctrl+Shift+E", () => CommandRequested?.Invoke("splitright")));
        menu.Items.Add(Item("Split down", "Ctrl+Shift+O", () => CommandRequested?.Invoke("splitdown")));
        menu.Items.Add(Item("Close pane", "Ctrl+Shift+W", () => CommandRequested?.Invoke("closepane")));
        menu.Items.Add(Item("Open folder in File Manager", null, () => CommandRequested?.Invoke("files")));
        menu.Items.Add(Item("Zoom in", "Ctrl+OemPlus", () => CommandRequested?.Invoke("zoomin")));
        menu.Items.Add(Item("Zoom out", "Ctrl+OemMinus", () => CommandRequested?.Invoke("zoomout")));
        menu.Items.Add(Item("Normal size", "Ctrl+D0", () => CommandRequested?.Invoke("zoomreset")));
        menu.Open(this);
    }

    /// <summary>The folder the shell is in now (OSC 7, else /proc).</summary>
    public string? CurrentDirectory() => Emu.WorkingDirectory ?? Pty?.CurrentDirectory();
}
