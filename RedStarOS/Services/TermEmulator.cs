using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RedStarOS.Services;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════
//  Red Star Terminal — xterm-compatible terminal emulator core (no UI code here).
//  Feed() takes the bytes a program writes; the screen, scrollback and modes are kept here and drawn by
//  Views/TerminalView. Replies a program asks for (cursor position, device attributes...) go out via Reply.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════

[Flags]
public enum CellFlags : ushort
{
    None = 0, Bold = 1, Dim = 2, Italic = 4, Underline = 8, Blink = 16, Inverse = 32, Hidden = 64, Strike = 128,
    DoubleUnderline = 256, Overline = 512
}

/// <summary>Colours: 0 = default, 0x01_0000nn = palette entry nn, 0x02_rrggbb = true colour.</summary>
public struct TermAttr : IEquatable<TermAttr>
{
    public uint Fg, Bg;
    public CellFlags Flags;
    public int Link;          // OSC 8 hyperlink id (0 = none)

    public const uint Default = 0;
    public static uint Palette(int i) => 0x01000000u | (uint)(i & 0xFF);
    public static uint Rgb(int r, int g, int b) => 0x02000000u | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF);

    public bool Equals(TermAttr o) => Fg == o.Fg && Bg == o.Bg && Flags == o.Flags && Link == o.Link;
    public override bool Equals(object? obj) => obj is TermAttr a && Equals(a);
    public override int GetHashCode() => HashCode.Combine(Fg, Bg, (int)Flags, Link);
    public static bool operator ==(TermAttr a, TermAttr b) => a.Equals(b);
    public static bool operator !=(TermAttr a, TermAttr b) => !a.Equals(b);
}

public struct TermCell
{
    public int Ch;            // Unicode code point, 0 = empty
    public byte Width;        // 1 normal, 2 = first half of a wide character, 0 = second half
    public TermAttr Attr;
    public string? Extra;     // combining marks that follow Ch

    public static TermCell Blank(TermAttr a) => new() { Ch = 0, Width = 1, Attr = new TermAttr { Bg = a.Bg } };
    public string Text => Ch == 0 ? " " : (char.ConvertFromUtf32(Ch) + Extra);
}

public sealed class TermLine
{
    public TermCell[] Cells;
    public bool Wrapped;      // the line continues on the next one (soft wrap)
    public int Version;       // bumped on every change (render cache key)

    public TermLine(int cols, TermAttr fill)
    {
        Cells = new TermCell[cols];
        for (int i = 0; i < cols; i++) Cells[i] = TermCell.Blank(fill);
    }

    public void Touch() => Version++;

    public void Clear(int from, int to, TermAttr fill)
    {
        from = Math.Max(0, from); to = Math.Min(Cells.Length, to);
        for (int i = from; i < to; i++) Cells[i] = TermCell.Blank(fill);
        Version++;
    }

    public void Resize(int cols, TermAttr fill)
    {
        if (cols == Cells.Length) return;
        var n = new TermCell[cols];
        for (int i = 0; i < cols; i++) n[i] = i < Cells.Length ? Cells[i] : TermCell.Blank(fill);
        if (cols > 0 && n[cols - 1].Width == 2) n[cols - 1] = TermCell.Blank(fill);
        Cells = n;
        Version++;
    }

    /// <summary>Number of cells up to the last non-blank one.</summary>
    public int UsedLength()
    {
        for (int i = Cells.Length - 1; i >= 0; i--)
            if (Cells[i].Ch != 0 || Cells[i].Attr.Bg != 0 || Cells[i].Width == 0) return i + 1;
        return 0;
    }

    public string GetText(int from = 0, int to = int.MaxValue, bool trimEnd = true)
    {
        to = Math.Min(to, Cells.Length);
        var sb = new StringBuilder();
        for (int i = Math.Max(0, from); i < to; i++)
        {
            ref var c = ref Cells[i];
            if (c.Width == 0) continue;
            sb.Append(c.Ch == 0 ? " " : c.Text);
        }
        return trimEnd ? sb.ToString().TrimEnd(' ') : sb.ToString();
    }
}

/// <summary>Fixed-capacity ring of lines (the scrollback).</summary>
public sealed class LineRing
{
    private TermLine?[] _a;
    private int _start, _count;
    public LineRing(int capacity) { _a = new TermLine?[Math.Max(1, capacity)]; }
    public int Count => _count;
    public int Capacity => _a.Length;
    public long Dropped { get; private set; }     // lines pushed out at the top since the start
    public TermLine this[int i] => _a[(_start + i) % _a.Length]!;
    public void Add(TermLine l)
    {
        if (_count < _a.Length) { _a[(_start + _count) % _a.Length] = l; _count++; }
        else { _a[_start] = l; _start = (_start + 1) % _a.Length; Dropped++; }
    }
    public TermLine? RemoveLast()
    {
        if (_count == 0) return null;
        var i = (_start + _count - 1) % _a.Length;
        var l = _a[i]; _a[i] = null; _count--;
        return l;
    }
    public void Clear() { Array.Clear(_a); _start = 0; _count = 0; }
}

public sealed class TermEmulator
{
    // ── public state ──
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public string Title { get; private set; } = "";
    public string? WorkingDirectory { get; private set; }        // from OSC 7
    public bool CursorVisible { get; private set; } = true;
    public int CursorStyle { get; private set; }                  // DECSCUSR 0-6 (0/1/2 block, 3/4 underline, 5/6 bar)
    public bool CursorBlink { get; private set; } = true;
    public bool AppCursorKeys { get; private set; }
    public bool AppKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool FocusReporting { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool AlternateScroll { get; private set; } = true;    // wheel sends arrow keys in the alternate screen
    public bool SyncOutput { get; private set; }                   // ?2026: hold drawing while the program updates
    public int MouseMode { get; private set; }                     // 0 off, 9 X10, 1000 press/release, 1002 drag, 1003 any motion
    public int MouseEncoding { get; private set; }                 // 0 default, 1005 UTF-8, 1006 SGR, 1015 urxvt
    public bool IsAltScreen => _buf == _alt;
    public int CursorX => Math.Min(_buf.X, Cols - 1);
    public int CursorY => _buf.Y;
    public readonly Dictionary<int, string> Links = new();
    public readonly Dictionary<int, uint> PaletteOverrides = new();   // OSC 4

    // colours the view uses for OSC 10/11/12 queries
    public Func<int, uint>? DefaultColorQuery;                          // 10 fg, 11 bg, 12 cursor → 0xRRGGBB

    public event Action<string>? Reply;          // bytes to send back to the program
    public event Action? Bell;
    public event Action? TitleChanged;
    public event Action<string>? ClipboardWrite; // OSC 52
    public event Action? AltScreenChanged;

    // ── buffers ──
    private sealed class Buffer
    {
        public TermLine[] Lines = Array.Empty<TermLine>();
        public LineRing? Scrollback;
        public int X, Y;
        public bool WrapPending;
        public int Top, Bottom;          // scroll region (inclusive)
        public Saved? Save;
    }
    private sealed class Saved
    {
        public int X, Y; public TermAttr Attr; public bool Origin, WrapPending, AutoWrap; public int G0, G1, GL;
    }

    private readonly Buffer _main = new(), _alt = new();
    private Buffer _buf;
    private TermAttr _attr;
    private bool _autoWrap = true, _origin, _insert, _lineFeedNewLine;
    private bool[] _tabs = Array.Empty<bool>();
    private int _g0, _g1, _gl;                          // charsets: 0 ASCII, 1 DEC special graphics
    private int _lastPrinted = ' ';
    private int _nextLink = 1;
    private readonly Dictionary<string, int> _linkIds = new();

    public LineRing Scrollback => _main.Scrollback!;
    public int ScrollbackCount => IsAltScreen ? 0 : _main.Scrollback!.Count;
    /// <summary>Lines dropped from the top of the scrollback so far (keeps selections stable).</summary>
    public long Dropped => _main.Scrollback!.Dropped;

    public TermEmulator(int cols, int rows, int scrollback = 10000)
    {
        Cols = Math.Max(2, cols); Rows = Math.Max(2, rows);
        _main.Scrollback = new LineRing(scrollback);
        InitBuffer(_main); InitBuffer(_alt);
        _buf = _main;
        ResetTabs();
    }

    private void InitBuffer(Buffer b)
    {
        b.Lines = new TermLine[Rows];
        for (int i = 0; i < Rows; i++) b.Lines[i] = new TermLine(Cols, default);
        b.X = b.Y = 0; b.WrapPending = false; b.Top = 0; b.Bottom = Rows - 1;
    }

    private void ResetTabs()
    {
        _tabs = new bool[Cols];
        for (int i = 8; i < Cols; i += 8) _tabs[i] = true;
    }

    // ── lines as one list: scrollback then screen ──
    public int TotalLines => ScrollbackCount + Rows;
    public TermLine GetLine(int index)
    {
        int sb = ScrollbackCount;
        return index < sb ? _main.Scrollback![index] : _buf.Lines[Math.Clamp(index - sb, 0, Rows - 1)];
    }
    public TermLine ScreenLine(int row) => _buf.Lines[row];

    // ═════════════════════════════════ input from the program ═════════════════════════════════
    private readonly Decoder _utf8 = new UTF8Encoding(false, false).GetDecoder();
    private char[] _chars = new char[4096];
    private char _highSurrogate;

    public void Feed(byte[] data, int count)
    {
        if (_chars.Length < count + 4) _chars = new char[count + 4];
        int n = _utf8.GetChars(data, 0, count, _chars, 0, false);
        for (int i = 0; i < n; i++)
        {
            char c = _chars[i];
            if (char.IsHighSurrogate(c)) { _highSurrogate = c; continue; }
            if (char.IsLowSurrogate(c))
            {
                if (_highSurrogate != 0) { Process(char.ConvertToUtf32(_highSurrogate, c)); _highSurrogate = '\0'; }
                continue;
            }
            _highSurrogate = '\0';
            Process(c);
        }
    }

    public void Feed(string s) { var b = Encoding.UTF8.GetBytes(s); Feed(b, b.Length); }

    // ── parser (after vt100.net's DEC parser) ──
    private enum St { Ground, Esc, EscInter, Csi, CsiIgnore, Osc, Str }
    private St _st = St.Ground;
    private readonly StringBuilder _inter = new();
    private readonly List<List<int>> _params = new();
    private char _private;          // '?', '>', '<', '=' or 0
    private bool _paramStarted;
    private readonly StringBuilder _osc = new();
    private bool _strEsc;           // saw ESC inside a string (ST is ESC \)

    private void Process(int cp)
    {
        // strings (OSC / DCS / APC...) eat everything up to BEL or ST
        if (_st == St.Osc || _st == St.Str)
        {
            if (cp == 0x07 && _st == St.Osc) { DispatchOsc(); _st = St.Ground; return; }
            if (_strEsc)
            {
                _strEsc = false;
                if (cp == '\\') { if (_st == St.Osc) DispatchOsc(); _st = St.Ground; return; }
                if (_st == St.Osc) DispatchOsc();
                _st = St.Ground;
                // fall through: treat as a new ESC sequence
                _st = St.Esc; _inter.Clear();
                Process(cp);
                return;
            }
            if (cp == 0x1B) { _strEsc = true; return; }
            if (cp == 0x18 || cp == 0x1A) { _st = St.Ground; return; }
            if (_st == St.Osc && _osc.Length < 1_000_000) { if (cp > 0xFFFF) _osc.Append(char.ConvertFromUtf32(cp)); else _osc.Append((char)cp); }
            return;
        }

        if (cp < 0x20 || cp == 0x7F)
        {
            if (cp == 0x1B) { _st = St.Esc; _inter.Clear(); return; }
            if (cp == 0x18 || cp == 0x1A) { _st = St.Ground; return; }
            if (cp == 0x7F) return;
            Control(cp);       // C0 controls work in the middle of escape sequences too
            return;
        }

        switch (_st)
        {
            case St.Ground:
                Print(cp);
                break;

            case St.Esc:
                if (cp >= 0x20 && cp <= 0x2F) { _inter.Append((char)cp); _st = St.EscInter; }
                else if (cp == '[') { _st = St.Csi; _params.Clear(); _private = '\0'; _paramStarted = false; _inter.Clear(); }
                else if (cp == ']') { _st = St.Osc; _osc.Clear(); _strEsc = false; }
                else if (cp == 'P' || cp == 'X' || cp == '^' || cp == '_') { _st = St.Str; _strEsc = false; }
                else { _st = St.Ground; EscDispatch((char)cp); }
                break;

            case St.EscInter:
                if (cp >= 0x20 && cp <= 0x2F) _inter.Append((char)cp);
                else { _st = St.Ground; EscDispatch((char)cp); }
                break;

            case St.Csi:
                if (cp >= '0' && cp <= '9')
                {
                    if (_params.Count == 0) _params.Add(new List<int> { -1 });
                    var g = _params[^1];
                    int v = g[^1];
                    g[^1] = Math.Min(99999, (v < 0 ? 0 : v * 10) + (cp - '0'));
                    _paramStarted = true;
                }
                else if (cp == ';')
                {
                    if (_params.Count == 0) _params.Add(new List<int> { -1 });
                    if (_params.Count < 32) _params.Add(new List<int> { -1 });
                    _paramStarted = true;
                }
                else if (cp == ':')
                {
                    if (_params.Count == 0) _params.Add(new List<int> { -1 });
                    if (_params[^1].Count < 8) _params[^1].Add(-1);
                    _paramStarted = true;
                }
                else if ((cp == '?' || cp == '>' || cp == '<' || cp == '=') && !_paramStarted && _private == '\0' && _inter.Length == 0) _private = (char)cp;
                else if (cp >= 0x20 && cp <= 0x2F) _inter.Append((char)cp);
                else if (cp >= 0x40 && cp <= 0x7E) { _st = St.Ground; try { CsiDispatch((char)cp); } catch { /* malformed */ } }
                else _st = St.CsiIgnore;
                break;

            case St.CsiIgnore:
                if (cp >= 0x40 && cp <= 0x7E) _st = St.Ground;
                break;
        }
    }

    private int P(int i, int def = 0)
    {
        if (i >= _params.Count) return def;
        int v = _params[i][0];
        return v < 0 ? def : v;
    }
    private int P1(int i) { int v = P(i, 1); return v == 0 ? 1 : v; }   // counts: 0 means 1

    // ═════════════════════════════════ controls ═════════════════════════════════
    private void Control(int cp)
    {
        switch (cp)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08: // BS
                if (_buf.WrapPending) _buf.WrapPending = false;
                else if (_buf.X > 0) _buf.X--;
                break;
            case 0x09: TabForward(1); break;
            case 0x0A: case 0x0B: case 0x0C:
                LineFeed();
                if (_lineFeedNewLine) { _buf.X = 0; }
                break;
            case 0x0D: _buf.X = 0; _buf.WrapPending = false; break;
            case 0x0E: _gl = 1; break;
            case 0x0F: _gl = 0; break;
        }
    }

    private void TabForward(int n)
    {
        _buf.WrapPending = false;
        while (n-- > 0)
        {
            int x = _buf.X + 1;
            while (x < Cols - 1 && !_tabs[x]) x++;
            _buf.X = Math.Min(x, Cols - 1);
        }
    }

    private void TabBack(int n)
    {
        _buf.WrapPending = false;
        while (n-- > 0 && _buf.X > 0)
        {
            int x = _buf.X - 1;
            while (x > 0 && !_tabs[x]) x--;
            _buf.X = x;
        }
    }

    public void LineFeed()
    {
        _buf.WrapPending = false;
        if (_buf.Y == _buf.Bottom) ScrollUp(1);
        else if (_buf.Y < Rows - 1) _buf.Y++;
    }

    private void ReverseIndex()
    {
        _buf.WrapPending = false;
        if (_buf.Y == _buf.Top) ScrollDown(1);
        else if (_buf.Y > 0) _buf.Y--;
    }

    private TermAttr Fill => new() { Bg = _attr.Bg };

    private void ScrollUp(int n)
    {
        n = Math.Min(n, _buf.Bottom - _buf.Top + 1);
        for (int k = 0; k < n; k++)
        {
            var top = _buf.Lines[_buf.Top];
            bool toScrollback = _buf == _main && _buf.Top == 0;
            for (int i = _buf.Top; i < _buf.Bottom; i++) _buf.Lines[i] = _buf.Lines[i + 1];
            TermLine fresh;
            if (toScrollback) { _main.Scrollback!.Add(top); fresh = new TermLine(Cols, Fill); }
            else { fresh = top; fresh.Clear(0, Cols, Fill); fresh.Wrapped = false; }
            _buf.Lines[_buf.Bottom] = fresh;
        }
        TouchAll();
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, _buf.Bottom - _buf.Top + 1);
        for (int k = 0; k < n; k++)
        {
            var bottom = _buf.Lines[_buf.Bottom];
            for (int i = _buf.Bottom; i > _buf.Top; i--) _buf.Lines[i] = _buf.Lines[i - 1];
            bottom.Clear(0, Cols, Fill); bottom.Wrapped = false;
            _buf.Lines[_buf.Top] = bottom;
        }
        TouchAll();
    }

    private void TouchAll() { foreach (var l in _buf.Lines) l.Touch(); }

    // ═════════════════════════════════ printing ═════════════════════════════════
    private static readonly string DecGraphics =
        // 0x5F-0x7E in the DEC special graphics set
        " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private void Print(int cp)
    {
        int cs = _gl == 0 ? _g0 : _g1;
        if (cs == 1 && cp >= 0x5F && cp <= 0x7E) cp = DecGraphics[cp - 0x5F];

        int w = CharWidth(cp);
        if (w == 0)
        {
            // combining mark: attach to the previous cell
            int px = _buf.WrapPending ? _buf.X : _buf.X - 1;
            if (px >= 0 && px < Cols)
            {
                var line = _buf.Lines[_buf.Y];
                if (line.Cells[px].Width == 0 && px > 0) px--;
                line.Cells[px].Extra += char.ConvertFromUtf32(cp);
                line.Touch();
            }
            return;
        }

        if (_buf.WrapPending)
        {
            if (_autoWrap)
            {
                _buf.Lines[_buf.Y].Wrapped = true;
                _buf.X = 0;
                LineFeed();
            }
            _buf.WrapPending = false;
        }
        if (w == 2 && _buf.X == Cols - 1)
        {
            if (_autoWrap)
            {
                _buf.Lines[_buf.Y].Cells[_buf.X] = TermCell.Blank(_attr);
                _buf.Lines[_buf.Y].Wrapped = true;
                _buf.X = 0;
                LineFeed();
            }
            else w = 1;
        }

        var ln = _buf.Lines[_buf.Y];
        if (_insert) InsertCells(ln, _buf.X, w);
        BreakWide(ln, _buf.X);
        if (w == 2) BreakWide(ln, _buf.X + 1);
        ln.Cells[_buf.X] = new TermCell { Ch = cp, Width = (byte)w, Attr = _attr };
        if (w == 2 && _buf.X + 1 < Cols) ln.Cells[_buf.X + 1] = new TermCell { Ch = 0, Width = 0, Attr = _attr };
        ln.Touch();
        _lastPrinted = cp;

        _buf.X += w;
        if (_buf.X >= Cols) { _buf.X = Cols - 1; _buf.WrapPending = true; }
    }

    /// <summary>Overwriting half of a wide character blanks the other half.</summary>
    private void BreakWide(TermLine ln, int x)
    {
        if (x < 0 || x >= Cols) return;
        var c = ln.Cells[x];
        if (c.Width == 0 && x > 0 && ln.Cells[x - 1].Width == 2) ln.Cells[x - 1] = TermCell.Blank(ln.Cells[x - 1].Attr);
        if (c.Width == 2 && x + 1 < Cols) ln.Cells[x + 1] = TermCell.Blank(ln.Cells[x + 1].Attr);
    }

    private void InsertCells(TermLine ln, int x, int n)
    {
        for (int i = Cols - 1; i >= x + n; i--) ln.Cells[i] = ln.Cells[i - n];
        for (int i = x; i < Math.Min(Cols, x + n); i++) ln.Cells[i] = TermCell.Blank(Fill);
        if (ln.Cells[Cols - 1].Width == 2) ln.Cells[Cols - 1] = TermCell.Blank(Fill);
        ln.Touch();
    }

    /// <summary>Display width of a code point: 0 (combining), 1, or 2 (East Asian wide / emoji).</summary>
    public static int CharWidth(int cp)
    {
        if (cp < 0x300) return 1;
        if (cp == 0x200D || (cp >= 0xFE00 && cp <= 0xFE0F) || (cp >= 0xE0100 && cp <= 0xE01EF)) return 0;   // ZWJ, variation selectors
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark) return 0;
        if (cat == UnicodeCategory.Format && cp != 0xAD) return 0;
        if (cp >= 0x1160 && cp <= 0x11FF) return 0;   // Hangul medial vowels / final consonants
        foreach (var (a, b) in Wide) { if (cp < a) break; if (cp <= b) return 2; }
        return 1;
    }

    private static readonly (int, int)[] Wide =
    {
        (0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3),
        (0x25FD, 0x25FE), (0x2614, 0x2615), (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1),
        (0x26AA, 0x26AB), (0x26BD, 0x26BE), (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA),
        (0x26F2, 0x26F3), (0x26F5, 0x26F5), (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B),
        (0x2728, 0x2728), (0x274C, 0x274C), (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797),
        (0x27B0, 0x27B0), (0x27BF, 0x27BF), (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55), (0x2E80, 0x303E),
        (0x3041, 0x33FF), (0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xA000, 0xA4CF), (0xA960, 0xA97F), (0xAC00, 0xD7A3),
        (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60), (0xFFE0, 0xFFE6), (0x16FE0, 0x16FE4),
        (0x17000, 0x18AFF), (0x1B000, 0x1B16F), (0x1F004, 0x1F004), (0x1F0CF, 0x1F0CF), (0x1F18E, 0x1F18E),
        (0x1F191, 0x1F19A), (0x1F200, 0x1F251), (0x1F300, 0x1F320), (0x1F32D, 0x1F335), (0x1F337, 0x1F37C),
        (0x1F37E, 0x1F393), (0x1F3A0, 0x1F3CA), (0x1F3CF, 0x1F3D3), (0x1F3E0, 0x1F3F0), (0x1F3F4, 0x1F3F4),
        (0x1F3F8, 0x1F43E), (0x1F440, 0x1F440), (0x1F442, 0x1F4FC), (0x1F4FF, 0x1F53D), (0x1F54B, 0x1F54E),
        (0x1F550, 0x1F567), (0x1F57A, 0x1F57A), (0x1F595, 0x1F596), (0x1F5A4, 0x1F5A4), (0x1F5FB, 0x1F64F),
        (0x1F680, 0x1F6C5), (0x1F6CC, 0x1F6CC), (0x1F6D0, 0x1F6D2), (0x1F6D5, 0x1F6D7), (0x1F6DC, 0x1F6DF),
        (0x1F6EB, 0x1F6EC), (0x1F6F4, 0x1F6FC), (0x1F7E0, 0x1F7EB), (0x1F7F0, 0x1F7F0), (0x1F90C, 0x1F93A),
        (0x1F93C, 0x1F945), (0x1F947, 0x1F9FF), (0x1FA70, 0x1FAFF), (0x20000, 0x2FFFD), (0x30000, 0x3FFFD),
    };

    // ═════════════════════════════════ ESC ═════════════════════════════════
    private void EscDispatch(char f)
    {
        string inter = _inter.ToString();
        if (inter.Length == 0)
        {
            switch (f)
            {
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(); break;
                case 'E': _buf.X = 0; LineFeed(); break;
                case 'M': ReverseIndex(); break;
                case 'H': if (_buf.X < Cols) _tabs[_buf.X] = true; break;
                case 'c': FullReset(); break;
                case '=': AppKeypad = true; break;
                case '>': AppKeypad = false; break;
            }
            return;
        }
        if (inter == "(" ) _g0 = f == '0' ? 1 : 0;
        else if (inter == ")") _g1 = f == '0' ? 1 : 0;
        else if (inter == "#" && f == '8')
        {
            // DECALN: fill the screen with E
            foreach (var l in _buf.Lines) { for (int i = 0; i < Cols; i++) l.Cells[i] = new TermCell { Ch = 'E', Width = 1 }; l.Touch(); }
            _buf.X = _buf.Y = 0;
        }
    }

    private void SaveCursor()
    {
        _buf.Save = new Saved { X = _buf.X, Y = _buf.Y, Attr = _attr, Origin = _origin, WrapPending = _buf.WrapPending, AutoWrap = _autoWrap, G0 = _g0, G1 = _g1, GL = _gl };
    }

    private void RestoreCursor()
    {
        var s = _buf.Save;
        if (s == null) { _buf.X = _buf.Y = 0; _attr = default; _origin = false; return; }
        _buf.X = Math.Min(s.X, Cols - 1); _buf.Y = Math.Min(s.Y, Rows - 1);
        _attr = s.Attr; _origin = s.Origin; _buf.WrapPending = s.WrapPending; _autoWrap = s.AutoWrap;
        _g0 = s.G0; _g1 = s.G1; _gl = s.GL;
    }

    public void FullReset()
    {
        if (IsAltScreen) { _buf = _main; AltScreenChanged?.Invoke(); }
        _main.Scrollback!.Clear();
        InitBuffer(_main); InitBuffer(_alt);
        _attr = default; _autoWrap = true; _origin = false; _insert = false; _lineFeedNewLine = false;
        AppCursorKeys = AppKeypad = BracketedPaste = FocusReporting = ReverseVideo = SyncOutput = false;
        CursorVisible = true; CursorStyle = 0; CursorBlink = true; MouseMode = 0; MouseEncoding = 0; AlternateScroll = true;
        _g0 = _g1 = _gl = 0;
        PaletteOverrides.Clear();
        ResetTabs();
    }

    private void SoftReset()
    {
        _attr = default; _autoWrap = true; _origin = false; _insert = false;
        AppCursorKeys = AppKeypad = false; CursorVisible = true;
        _buf.Top = 0; _buf.Bottom = Rows - 1; _g0 = _g1 = _gl = 0; _buf.Save = null;
    }

    // ═════════════════════════════════ CSI ═════════════════════════════════
    private void CsiDispatch(char f)
    {
        string inter = _inter.ToString();
        var b = _buf;
        if (_private == '?')
        {
            if (f == 'h' || f == 'l') { for (int i = 0; i < Math.Max(1, _params.Count); i++) DecMode(P(i), f == 'h'); }
            else if (f == 'J') EraseDisplay(P(0));
            else if (f == 'K') EraseLine(P(0));
            else if (f == 'n' && P(0) == 6) Reply?.Invoke($"\x1b[?{b.Y + 1};{Math.Min(b.X, Cols - 1) + 1}R");
            else if (f == 'p' && inter == "$") Reply?.Invoke($"\x1b[?{P(0)};{DecModeState(P(0))}$y");
            else if (f == 'u') Reply?.Invoke("\x1b[?0u");                  // kitty keyboard query: not supported
            return;
        }
        if (_private == '>')
        {
            if (f == 'c') Reply?.Invoke("\x1b[>0;276;0c");                   // secondary DA: "xterm 276"
            else if (f == 'q') Reply?.Invoke("\x1bP>|RedStarTerminal " + AppVersion.Current.Short + "\x1b\\");   // XTVERSION
            return;
        }
        if (_private != '\0') return;

        if (inter == " " && f == 'q')
        {
            int s = P(0);
            CursorStyle = s; CursorBlink = s == 0 || s % 2 == 1;
            return;
        }
        if (inter == "!" && f == 'p') { SoftReset(); return; }
        if (inter == "$" && f == 'p') { Reply?.Invoke($"\x1b[{P(0)};0$y"); return; }
        if (inter.Length > 0) return;

        switch (f)
        {
            case '@': { var ln = b.Lines[b.Y]; InsertCells(ln, Math.Min(b.X, Cols - 1), Math.Min(P1(0), Cols)); b.WrapPending = false; break; }
            case 'A': b.Y = Math.Max(b.Y < b.Top ? 0 : b.Top, b.Y - P1(0)); b.WrapPending = false; break;
            case 'B': case 'e': b.Y = Math.Min(b.Y > b.Bottom ? Rows - 1 : b.Bottom, b.Y + P1(0)); b.WrapPending = false; break;
            case 'C': case 'a': b.X = Math.Min(Cols - 1, b.X + P1(0)); b.WrapPending = false; break;
            case 'D': b.X = Math.Max(0, Math.Min(b.X, Cols - 1) - P1(0)); b.WrapPending = false; break;
            case 'E': b.X = 0; b.Y = Math.Min(b.Bottom, b.Y + P1(0)); b.WrapPending = false; break;
            case 'F': b.X = 0; b.Y = Math.Max(b.Top, b.Y - P1(0)); b.WrapPending = false; break;
            case 'G': case '`': b.X = Math.Clamp(P1(0) - 1, 0, Cols - 1); b.WrapPending = false; break;
            case 'H': case 'f': MoveTo(P1(1) - 1, P1(0) - 1); break;
            case 'I': TabForward(P1(0)); break;
            case 'Z': TabBack(P1(0)); break;
            case 'J': EraseDisplay(P(0)); break;
            case 'K': EraseLine(P(0)); break;
            case 'L': InsertLines(P1(0)); break;
            case 'M': DeleteLines(P1(0)); break;
            case 'P': DeleteChars(P1(0)); break;
            case 'S': ScrollUp(P1(0)); break;
            case 'T': if (_params.Count <= 1) ScrollDown(P1(0)); break;
            case 'X': { var ln = b.Lines[b.Y]; int x = Math.Min(b.X, Cols - 1); ln.Clear(x, x + P1(0), Fill); b.WrapPending = false; break; }
            case 'b': { int n = Math.Min(P1(0), Cols * Rows); for (int i = 0; i < n; i++) Print(_lastPrinted); break; }
            case 'c': if (P(0) == 0) Reply?.Invoke("\x1b[?62;22c"); break;     // VT220 with ANSI colour
            case 'd': MoveTo(Math.Min(b.X, Cols - 1), P1(0) - 1, absoluteRow: !_origin); break;
            case 'g': if (P(0) == 0) { if (b.X < Cols) _tabs[b.X] = false; } else if (P(0) == 3) Array.Clear(_tabs); break;
            case 'h': case 'l': for (int i = 0; i < Math.Max(1, _params.Count); i++) AnsiMode(P(i), f == 'h'); break;
            case 'm': Sgr(); break;
            case 'n':
                if (P(0) == 5) Reply?.Invoke("\x1b[0n");
                else if (P(0) == 6)
                {
                    int row = _origin ? b.Y - b.Top : b.Y;
                    Reply?.Invoke($"\x1b[{row + 1};{Math.Min(b.X, Cols - 1) + 1}R");
                }
                break;
            case 'r':
            {
                int top = P1(0) - 1, bottom = (P(1) == 0 ? Rows : P(1)) - 1;
                if (top < bottom && bottom < Rows) { b.Top = top; b.Bottom = bottom; MoveTo(0, 0); }
                break;
            }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't':
                if (P(0) == 18) Reply?.Invoke($"\x1b[8;{Rows};{Cols}t");
                else if (P(0) == 21) Reply?.Invoke($"\x1b]l{Title}\x1b\\");
                break;
        }
    }

    private void MoveTo(int x, int y, bool absoluteRow = false)
    {
        var b = _buf;
        if (_origin && !absoluteRow) { y = Math.Clamp(y + b.Top, b.Top, b.Bottom); }
        else y = Math.Clamp(y, 0, Rows - 1);
        b.X = Math.Clamp(x, 0, Cols - 1); b.Y = y; b.WrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        var b = _buf;
        int x = Math.Min(b.X, Cols - 1);
        switch (mode)
        {
            case 0:
                b.Lines[b.Y].Clear(x, Cols, Fill); b.Lines[b.Y].Wrapped = false;
                for (int i = b.Y + 1; i < Rows; i++) { b.Lines[i].Clear(0, Cols, Fill); b.Lines[i].Wrapped = false; }
                break;
            case 1:
                for (int i = 0; i < b.Y; i++) { b.Lines[i].Clear(0, Cols, Fill); b.Lines[i].Wrapped = false; }
                b.Lines[b.Y].Clear(0, x + 1, Fill);
                break;
            case 2:
                // like xterm/VTE: a full clear of the main screen keeps what was there in the scrollback
                if (b == _main)
                {
                    int used = Rows;
                    while (used > 0 && b.Lines[used - 1].UsedLength() == 0) used--;
                    for (int i = 0; i < used; i++) { _main.Scrollback!.Add(b.Lines[i]); b.Lines[i] = new TermLine(Cols, Fill); }
                }
                for (int i = 0; i < Rows; i++) { b.Lines[i].Clear(0, Cols, Fill); b.Lines[i].Wrapped = false; }
                break;
            case 3:
                _main.Scrollback!.Clear();
                TouchAll();
                break;
        }
        b.WrapPending = false;
    }

    private void EraseLine(int mode)
    {
        var b = _buf; var ln = b.Lines[b.Y];
        int x = Math.Min(b.X, Cols - 1);
        if (mode == 0) { ln.Clear(x, Cols, Fill); ln.Wrapped = false; }
        else if (mode == 1) ln.Clear(0, x + 1, Fill);
        else if (mode == 2) { ln.Clear(0, Cols, Fill); ln.Wrapped = false; }
        b.WrapPending = false;
    }

    private void InsertLines(int n)
    {
        var b = _buf;
        if (b.Y < b.Top || b.Y > b.Bottom) return;
        n = Math.Min(n, b.Bottom - b.Y + 1);
        for (int k = 0; k < n; k++)
        {
            var last = b.Lines[b.Bottom];
            for (int i = b.Bottom; i > b.Y; i--) b.Lines[i] = b.Lines[i - 1];
            last.Clear(0, Cols, Fill); last.Wrapped = false;
            b.Lines[b.Y] = last;
        }
        b.X = 0; b.WrapPending = false;
        TouchAll();
    }

    private void DeleteLines(int n)
    {
        var b = _buf;
        if (b.Y < b.Top || b.Y > b.Bottom) return;
        n = Math.Min(n, b.Bottom - b.Y + 1);
        for (int k = 0; k < n; k++)
        {
            var first = b.Lines[b.Y];
            for (int i = b.Y; i < b.Bottom; i++) b.Lines[i] = b.Lines[i + 1];
            first.Clear(0, Cols, Fill); first.Wrapped = false;
            b.Lines[b.Bottom] = first;
        }
        b.X = 0; b.WrapPending = false;
        TouchAll();
    }

    private void DeleteChars(int n)
    {
        var b = _buf; var ln = b.Lines[b.Y];
        int x = Math.Min(b.X, Cols - 1);
        n = Math.Min(n, Cols - x);
        BreakWide(ln, x);
        for (int i = x; i < Cols - n; i++) ln.Cells[i] = ln.Cells[i + n];
        for (int i = Cols - n; i < Cols; i++) ln.Cells[i] = TermCell.Blank(Fill);
        if (ln.Cells[x].Width == 0) ln.Cells[x] = TermCell.Blank(Fill);
        ln.Touch();
        b.WrapPending = false;
    }

    // ── modes ──
    private void AnsiMode(int m, bool on)
    {
        if (m == 4) _insert = on;
        else if (m == 20) _lineFeedNewLine = on;
    }

    private void DecMode(int m, bool on)
    {
        switch (m)
        {
            case 1: AppCursorKeys = on; break;
            case 5: ReverseVideo = on; TouchAll(); break;
            case 6: _origin = on; MoveTo(0, 0); break;
            case 7: _autoWrap = on; break;
            case 12: CursorBlink = on; break;
            case 25: CursorVisible = on; break;
            case 9: MouseMode = on ? 9 : 0; break;
            case 1000: case 1002: case 1003: MouseMode = on ? m : 0; break;
            case 1004: FocusReporting = on; break;
            case 1005: case 1006: case 1015: MouseEncoding = on ? m : 0; break;
            case 1007: AlternateScroll = on; break;
            case 2004: BracketedPaste = on; break;
            case 2026: SyncOutput = on; break;
            case 47: case 1047:
                if (on) EnterAlt(clear: m == 1047);
                else LeaveAlt(clear: m == 1047);
                break;
            case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
            case 1049:
                if (on) { SaveCursor(); EnterAlt(clear: true); }
                else { LeaveAlt(clear: true); RestoreCursor(); }
                break;
        }
    }

    private int DecModeState(int m) => m switch
    {
        1 => AppCursorKeys ? 1 : 2, 5 => ReverseVideo ? 1 : 2, 6 => _origin ? 1 : 2, 7 => _autoWrap ? 1 : 2,
        12 => CursorBlink ? 1 : 2, 25 => CursorVisible ? 1 : 2, 1004 => FocusReporting ? 1 : 2,
        9 or 1000 or 1002 or 1003 => MouseMode == m ? 1 : 2, 1006 => MouseEncoding == 1006 ? 1 : 2,
        47 or 1047 or 1049 => IsAltScreen ? 1 : 2, 2004 => BracketedPaste ? 1 : 2, 2026 => SyncOutput ? 1 : 2,
        _ => 0
    };

    private void EnterAlt(bool clear)
    {
        if (IsAltScreen) return;
        _alt.X = _main.X; _alt.Y = _main.Y; _alt.WrapPending = false;
        _alt.Top = 0; _alt.Bottom = Rows - 1;
        if (clear) foreach (var l in _alt.Lines) { l.Clear(0, Cols, Fill); l.Wrapped = false; }
        _buf = _alt;
        TouchAll();
        AltScreenChanged?.Invoke();
    }

    private void LeaveAlt(bool clear)
    {
        if (!IsAltScreen) return;
        if (clear) foreach (var l in _alt.Lines) { l.Clear(0, Cols, default); l.Wrapped = false; }
        _buf = _main;
        TouchAll();
        AltScreenChanged?.Invoke();
    }

    // ── SGR ──
    private void Sgr()
    {
        if (_params.Count == 0) { _attr = new TermAttr { Link = _attr.Link }; return; }
        for (int i = 0; i < _params.Count; i++)
        {
            var g = _params[i];
            int p = g[0] < 0 ? 0 : g[0];
            switch (p)
            {
                case 0: _attr = new TermAttr { Link = _attr.Link }; break;
                case 1: _attr.Flags |= CellFlags.Bold; break;
                case 2: _attr.Flags |= CellFlags.Dim; break;
                case 3: _attr.Flags |= CellFlags.Italic; break;
                case 4:
                    _attr.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
                    if (g.Count > 1) { if (g[1] == 2) _attr.Flags |= CellFlags.DoubleUnderline; else if (g[1] > 0) _attr.Flags |= CellFlags.Underline; }
                    else _attr.Flags |= CellFlags.Underline;
                    break;
                case 5: case 6: _attr.Flags |= CellFlags.Blink; break;
                case 7: _attr.Flags |= CellFlags.Inverse; break;
                case 8: _attr.Flags |= CellFlags.Hidden; break;
                case 9: _attr.Flags |= CellFlags.Strike; break;
                case 21: _attr.Flags |= CellFlags.DoubleUnderline; break;
                case 22: _attr.Flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: _attr.Flags &= ~CellFlags.Italic; break;
                case 24: _attr.Flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline); break;
                case 25: _attr.Flags &= ~CellFlags.Blink; break;
                case 27: _attr.Flags &= ~CellFlags.Inverse; break;
                case 28: _attr.Flags &= ~CellFlags.Hidden; break;
                case 29: _attr.Flags &= ~CellFlags.Strike; break;
                case 39: _attr.Fg = 0; break;
                case 49: _attr.Bg = 0; break;
                case 53: _attr.Flags |= CellFlags.Overline; break;
                case 55: _attr.Flags &= ~CellFlags.Overline; break;
                case 38: case 48: case 58:
                {
                    uint col;
                    int used = ExtendedColor(i, g, out col);
                    if (p == 38) _attr.Fg = col; else if (p == 48) _attr.Bg = col;   // 58 (underline colour) is read and ignored
                    i += used;
                    break;
                }
                case 59: break;
                default:
                    if (p >= 30 && p <= 37) _attr.Fg = TermAttr.Palette(p - 30);
                    else if (p >= 40 && p <= 47) _attr.Bg = TermAttr.Palette(p - 40);
                    else if (p >= 90 && p <= 97) _attr.Fg = TermAttr.Palette(p - 90 + 8);
                    else if (p >= 100 && p <= 107) _attr.Bg = TermAttr.Palette(p - 100 + 8);
                    break;
            }
        }
    }

    /// <summary>Reads 38/48 colour arguments (either 38:2::r:g:b / 38:5:n or 38;2;r;g;b / 38;5;n).
    /// Returns how many extra ';' parameters were consumed.</summary>
    private int ExtendedColor(int i, List<int> g, out uint col)
    {
        col = 0;
        if (g.Count > 1)
        {
            int mode = g[1];
            if (mode == 5 && g.Count > 2) col = TermAttr.Palette(Math.Max(0, g[2]));
            else if (mode == 2)
            {
                // 38:2:<colour space>:r:g:b  or  38:2:r:g:b
                int off = g.Count >= 6 ? 3 : 2;
                if (g.Count >= off + 3) col = TermAttr.Rgb(Math.Max(0, g[off]), Math.Max(0, g[off + 1]), Math.Max(0, g[off + 2]));
            }
            return 0;
        }
        int m = P(i + 1);
        if (m == 5) { col = TermAttr.Palette(P(i + 2)); return 2; }
        if (m == 2) { col = TermAttr.Rgb(P(i + 2), P(i + 3), P(i + 4)); return 4; }
        return 1;
    }

    // ═════════════════════════════════ OSC ═════════════════════════════════
    private void DispatchOsc()
    {
        var s = _osc.ToString();
        int semi = s.IndexOf(';');
        string code = semi < 0 ? s : s[..semi];
        string arg = semi < 0 ? "" : s[(semi + 1)..];
        switch (code)
        {
            case "0": case "2": Title = arg; TitleChanged?.Invoke(); break;
            case "7":
                try
                {
                    var u = new Uri(arg);
                    if (u.IsFile || u.Scheme == "file" || u.Scheme == "kitty-shell-cwd") WorkingDirectory = Uri.UnescapeDataString(u.AbsolutePath);
                }
                catch { }
                break;
            case "8":
            {
                // OSC 8 ; params ; uri
                int s2 = arg.IndexOf(';');
                string uri = s2 < 0 ? "" : arg[(s2 + 1)..];
                if (uri.Length == 0) _attr.Link = 0;
                else
                {
                    if (!_linkIds.TryGetValue(uri, out var id)) { id = _nextLink++; _linkIds[uri] = id; Links[id] = uri; }
                    _attr.Link = id;
                }
                break;
            }
            case "52":
            {
                int s2 = arg.IndexOf(';');
                string data = s2 < 0 ? arg : arg[(s2 + 1)..];
                if (data == "?" || data.Length == 0) break;      // reading the clipboard is not allowed
                try { ClipboardWrite?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(data))); } catch { }
                break;
            }
            case "4":
            {
                var parts = arg.Split(';');
                for (int k = 0; k + 1 < parts.Length; k += 2)
                {
                    if (!int.TryParse(parts[k], out var idx) || idx < 0 || idx > 255) continue;
                    if (parts[k + 1] == "?")
                    {
                        uint c = PaletteOverrides.TryGetValue(idx, out var o) ? o : (DefaultColorQuery?.Invoke(100 + idx) ?? 0);
                        Reply?.Invoke($"\x1b]4;{idx};{RgbSpec(c)}\x1b\\");
                    }
                    else if (ParseColorSpec(parts[k + 1], out var rgb)) { PaletteOverrides[idx] = rgb; TouchAll(); }
                }
                break;
            }
            case "104": if (arg.Length == 0) PaletteOverrides.Clear(); else foreach (var p in arg.Split(';')) if (int.TryParse(p, out var i)) PaletteOverrides.Remove(i); TouchAll(); break;
            case "10": case "11": case "12":
                if (arg == "?") Reply?.Invoke($"\x1b]{code};{RgbSpec(DefaultColorQuery?.Invoke(int.Parse(code)) ?? 0)}\x1b\\");
                break;
        }
    }

    private static string RgbSpec(uint c)
    {
        int r = (int)((c >> 16) & 0xFF), g = (int)((c >> 8) & 0xFF), b = (int)(c & 0xFF);
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }

    private static bool ParseColorSpec(string s, out uint rgb)
    {
        rgb = 0;
        try
        {
            if (s.StartsWith("rgb:"))
            {
                var p = s[4..].Split('/');
                if (p.Length != 3) return false;
                int C(string h) => Convert.ToInt32(h.Length > 2 ? h[..2] : h, 16) * (h.Length == 1 ? 17 : 1);
                rgb = ((uint)C(p[0]) << 16) | ((uint)C(p[1]) << 8) | (uint)C(p[2]);
                return true;
            }
            if (s.StartsWith("#") && s.Length == 7) { rgb = Convert.ToUInt32(s[1..], 16); return true; }
        }
        catch { }
        return false;
    }

    // ═════════════════════════════════ resize (with reflow) ═════════════════════════════════
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(2, cols); rows = Math.Max(2, rows);
        if (cols == Cols && rows == Rows) return;
        int oldCols = Cols, oldRows = Rows;
        Cols = cols; Rows = rows;

        ReflowMain(oldCols, oldRows);
        ResizeAlt(oldRows);
        ResetTabs();
        foreach (var b in new[] { _main, _alt })
        {
            b.Top = 0; b.Bottom = Rows - 1;
            if (b.Save != null) { b.Save.X = Math.Min(b.Save.X, Cols - 1); b.Save.Y = Math.Min(b.Save.Y, Rows - 1); }
        }
    }

    private void ResizeAlt(int oldRows)
    {
        var lines = new TermLine[Rows];
        for (int i = 0; i < Rows; i++)
        {
            var l = i < oldRows ? _alt.Lines[i] : new TermLine(Cols, default);
            l.Resize(Cols, default);
            lines[i] = l;
        }
        _alt.Lines = lines;
        _alt.X = Math.Min(_alt.X, Cols - 1); _alt.Y = Math.Min(_alt.Y, Rows - 1); _alt.WrapPending = false;
    }

    private void ReflowMain(int oldCols, int oldRows)
    {
        var m = _main;
        var sb = m.Scrollback!;
        // 1. the screen up to the last used line (or the cursor)
        int lastUsed = oldRows - 1;
        while (lastUsed > m.Y && m.Lines[lastUsed].UsedLength() == 0) lastUsed--;

        var all = new List<TermLine>(sb.Count + oldRows);
        for (int i = 0; i < sb.Count; i++) all.Add(sb[i]);
        int cursorLine = all.Count + m.Y;
        for (int i = 0; i <= lastUsed; i++) all.Add(m.Lines[i]);
        int cursorX = Math.Min(m.X, oldCols - 1) + (m.WrapPending ? 1 : 0);

        // 2. join soft-wrapped rows into logical lines, then wrap them at the new width
        var result = new List<TermLine>(all.Count);
        int newCursorRow = -1, newCursorCol = 0;
        int idx = 0;
        while (idx < all.Count)
        {
            var cells = new List<TermCell>();
            int start = idx;
            int cursorOffset = -1;
            while (true)
            {
                var l = all[idx];
                bool last = !l.Wrapped || idx == all.Count - 1;
                int len = last ? l.UsedLength() : oldCols;
                if (idx == cursorLine) { cursorOffset = cells.Count + cursorX; len = Math.Max(len, Math.Min(cursorX, oldCols)); }
                // a wide char pushed to the next row leaves a blank in the last column: drop it when re-joining
                if (!last && l.Cells[oldCols - 1].Ch == 0 && l.Cells[oldCols - 1].Width == 1 && idx + 1 < all.Count && all[idx + 1].Cells[0].Width == 2) len = oldCols - 1;
                for (int k = 0; k < len && k < l.Cells.Length; k++) cells.Add(l.Cells[k]);
                idx++;
                if (last) break;
            }

            if (cells.Count == 0)
            {
                if (cursorOffset >= 0) { newCursorRow = result.Count; newCursorCol = 0; }
                result.Add(new TermLine(Cols, default));
                continue;
            }
            int pos = 0;
            while (pos < cells.Count || (cursorOffset >= 0 && newCursorRow < 0 && pos <= cursorOffset))
            {
                var line = new TermLine(Cols, default);
                int col = 0;
                int rowStart = pos;
                while (pos < cells.Count && col < Cols)
                {
                    var c = cells[pos];
                    if (c.Width == 2 && col == Cols - 1) break;              // wide char doesn't fit: next row
                    line.Cells[col++] = c;
                    if (c.Width == 2 && pos + 1 < cells.Count && cells[pos + 1].Width == 0) { line.Cells[col++] = cells[pos + 1]; pos++; }
                    pos++;
                }
                if (cursorOffset >= 0 && newCursorRow < 0 && cursorOffset >= rowStart && (cursorOffset < pos || pos >= cells.Count))
                {
                    int cc = cursorOffset - rowStart;
                    if (cc >= Cols) { line.Wrapped = true; result.Add(line); newCursorRow = result.Count; newCursorCol = cc - Cols; result.Add(new TermLine(Cols, default)); pos = Math.Max(pos, cells.Count); break; }
                    newCursorRow = result.Count; newCursorCol = cc;
                }
                bool more = pos < cells.Count;
                line.Wrapped = more;
                result.Add(line);
                if (!more) break;
            }
        }
        if (newCursorRow < 0) { newCursorRow = Math.Max(0, result.Count - 1); newCursorCol = 0; }

        // 3. the last Rows lines (keeping the cursor on screen) become the screen, the rest the scrollback
        int screenStart = Math.Max(0, result.Count - Rows);
        if (newCursorRow < screenStart) screenStart = newCursorRow;
        if (newCursorRow >= screenStart + Rows) screenStart = newCursorRow - Rows + 1;
        sb.Clear();
        for (int i = 0; i < screenStart; i++) sb.Add(result[i]);
        m.Lines = new TermLine[Rows];
        for (int i = 0; i < Rows; i++)
            m.Lines[i] = screenStart + i < result.Count ? result[screenStart + i] : new TermLine(Cols, default);
        m.Y = Math.Clamp(newCursorRow - screenStart, 0, Rows - 1);
        m.X = Math.Clamp(newCursorCol, 0, Cols - 1);
        m.WrapPending = newCursorCol >= Cols;
        foreach (var l in m.Lines) l.Touch();
    }

    // ═════════════════════════════════ helpers for the view ═════════════════════════════════
    /// <summary>Clears the scrollback (and the screen above the cursor line), like "Clear" in other terminals.</summary>
    public void ClearScrollback()
    {
        _main.Scrollback!.Clear();
        TouchAll();
    }

    public string? LinkAt(int line, int col)
    {
        if (line < 0 || line >= TotalLines) return null;
        var l = GetLine(line);
        if (col < 0 || col >= l.Cells.Length) return null;
        int id = l.Cells[col].Attr.Link;
        return id != 0 && Links.TryGetValue(id, out var u) ? u : null;
    }
}
