using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RedStarOS.Services;

public enum NumFmt { General, Number, Thousands, Currency, Percent, Date, Text }

/// <summary>Formatting of one cell. Instances are treated as immutable once stored in the sheet.</summary>
public sealed class CellFormat
{
    public bool Bold, Italic, Underline;
    public uint Color;        // 0 = automatic, otherwise 0xAARRGGBB
    public uint Fill;         // 0 = none
    public byte Align;        // 0 general, 1 left, 2 centre, 3 right
    public byte Borders;      // 1 left, 2 top, 4 right, 8 bottom
    public NumFmt Num;
    public int Dec = -1;      // -1 = default decimals for the number format

    public CellFormat Clone() => (CellFormat)MemberwiseClone();

    public bool IsDefault => !Bold && !Italic && !Underline && Color == 0 && Fill == 0 && Align == 0 &&
                             Borders == 0 && Num == NumFmt.General && Dec == -1;

    public bool SameAs(CellFormat? o)
    {
        if (o == null) return IsDefault;
        return Bold == o.Bold && Italic == o.Italic && Underline == o.Underline && Color == o.Color && Fill == o.Fill &&
               Align == o.Align && Borders == o.Borders && Num == o.Num && Dec == o.Dec;
    }
}

public sealed class Cell
{
    public string Raw = "";
    public CellFormat? Fmt;
    public Cell Copy() => new Cell { Raw = Raw, Fmt = Fmt };
}

/// <summary>Sparse spreadsheet data, formatting, layout, undo/redo and formula evaluation.</summary>
public sealed class SheetModel
{
    public const int MaxRows = 100000;
    public const int MaxCols = 702;          // A .. ZZ
    public const double DefaultColWidth = 96;
    public const double DefaultRowHeight = 24;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Dictionary<long, Cell> _cells = new();
    private int _usedRows, _usedCols;
    private bool _usedDirty;

    public Dictionary<int, double> ColWidths { get; } = new();
    public Dictionary<int, double> RowHeights { get; } = new();
    public HashSet<int> HiddenRows { get; } = new();
    public int FrozenRows { get; set; }
    public int FrozenCols { get; set; }
    public FormulaEvaluator Eval { get; }

    public event Action? Changed;
    public event Action? LayoutChanged;

    public SheetModel()
    {
        Eval = new FormulaEvaluator((r, c) => GetRaw(r, c), MaxRows, MaxCols)
        {
            UsedRows = () => UsedRows,
            UsedCols = () => UsedCols
        };
    }

    private static long Key(int r, int c) => ((long)r << 10) | (uint)c;

    // ------------------------------------------------------------------ basic access

    public string GetRaw(int r, int c) => _cells.TryGetValue(Key(r, c), out var cell) ? cell.Raw : "";
    public CellFormat? GetFmt(int r, int c) => _cells.TryGetValue(Key(r, c), out var cell) ? cell.Fmt : null;
    public Cell? GetCell(int r, int c) => _cells.TryGetValue(Key(r, c), out var cell) ? cell : null;
    public bool IsEmpty(int r, int c) => string.IsNullOrEmpty(GetRaw(r, c));

    public int UsedRows { get { RefreshUsed(); return _usedRows; } }
    public int UsedCols { get { RefreshUsed(); return _usedCols; } }

    private void RefreshUsed()
    {
        if (!_usedDirty) return;
        int mr = 0, mc = 0;
        foreach (var kv in _cells)
        {
            if (kv.Value.Raw.Length == 0 && kv.Value.Fmt == null) continue;
            int r = (int)(kv.Key >> 10) + 1, c = (int)(kv.Key & 1023) + 1;
            if (r > mr) mr = r;
            if (c > mc) mc = c;
        }
        _usedRows = mr; _usedCols = mc; _usedDirty = false;
    }

    public double ColWidth(int c) => ColWidths.TryGetValue(c, out var w) ? w : DefaultColWidth;
    public double RowHeight(int r) => HiddenRows.Contains(r) ? 0 : (RowHeights.TryGetValue(r, out var h) ? h : DefaultRowHeight);

    public void RaiseLayoutChanged() => LayoutChanged?.Invoke();

    public void Clear()
    {
        _cells.Clear();
        ColWidths.Clear(); RowHeights.Clear(); HiddenRows.Clear();
        FrozenRows = FrozenCols = 0;
        _undo.Clear(); _redo.Clear();
        _usedDirty = true;
        Eval.Invalidate();
        LayoutChanged?.Invoke();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ undo / redo

    private sealed class Change { public long Key; public Cell? Old, New; }
    private sealed class Group { public List<Change> Items = new(); public Dictionary<long, Change> Index = new(); }

    private readonly List<Group> _undo = new(), _redo = new();
    private Group? _open;
    private int _depth;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Starts (or joins) an edit group. Always pair with <see cref="EndEdit"/>.</summary>
    public void BeginEdit()
    {
        if (_depth++ == 0) _open = new Group();
    }

    public void EndEdit()
    {
        if (--_depth > 0) return;
        _depth = 0;
        var g = _open;
        _open = null;
        if (g == null || g.Items.Count == 0) return;
        _undo.Add(g);
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
        AfterChange();
    }

    private void AfterChange()
    {
        _usedDirty = true;
        Eval.Invalidate();
        Changed?.Invoke();
    }

    private void Put(long key, Cell? n)
    {
        _cells.TryGetValue(key, out var old);
        if (n != null && n.Raw.Length == 0 && (n.Fmt == null || n.Fmt.IsDefault)) n = null;
        if (ReferenceEquals(old, n)) return;
        if (old != null && n != null && old.Raw == n.Raw && (old.Fmt?.SameAs(n.Fmt) ?? (n.Fmt == null || n.Fmt.IsDefault))) return;
        if (old == null && n == null) return;

        if (_open != null)
        {
            if (_open.Index.TryGetValue(key, out var existing)) existing.New = n;
            else
            {
                var ch = new Change { Key = key, Old = old, New = n };
                _open.Items.Add(ch);
                _open.Index[key] = ch;
            }
        }
        if (n == null) _cells.Remove(key); else _cells[key] = n;
    }

    public void SetCell(int r, int c, Cell? n)
    {
        if (r < 0 || c < 0 || r >= MaxRows || c >= MaxCols) return;
        bool own = _depth == 0;
        if (own) BeginEdit();
        Put(Key(r, c), n);
        if (own) EndEdit();
    }

    public void SetRaw(int r, int c, string raw)
    {
        var cur = GetCell(r, c);
        SetCell(r, c, new Cell { Raw = raw, Fmt = cur?.Fmt });
    }

    /// <summary>Applies a formatting change to one cell.</summary>
    public void UpdateFmt(int r, int c, Action<CellFormat> change)
    {
        var cur = GetCell(r, c);
        var f = cur?.Fmt?.Clone() ?? new CellFormat();
        change(f);
        SetCell(r, c, new Cell { Raw = cur?.Raw ?? "", Fmt = f.IsDefault ? null : f });
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var g = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (int i = g.Items.Count - 1; i >= 0; i--)
        {
            var ch = g.Items[i];
            if (ch.Old == null) _cells.Remove(ch.Key); else _cells[ch.Key] = ch.Old;
        }
        _redo.Add(g);
        AfterChange();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var g = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var ch in g.Items)
        {
            if (ch.New == null) _cells.Remove(ch.Key); else _cells[ch.Key] = ch.New;
        }
        _undo.Add(g);
        AfterChange();
        return true;
    }

    // ------------------------------------------------------------------ typed input

    /// <summary>Applies text typed by the user: recognises %, currency, dates and thousands separators.</summary>
    public void Enter(int r, int c, string typed)
    {
        var cur = GetCell(r, c);
        var fmt = cur?.Fmt;
        string raw = typed;

        if (typed.Length > 0 && typed[0] != '=' && typed[0] != '\'' && (fmt == null || fmt.Num == NumFmt.General))
        {
            var t = typed.Trim();
            if (t.EndsWith("%") && double.TryParse(t.TrimEnd('%'), NumberStyles.Float, Inv, out var pc))
            {
                raw = FormulaEvaluator.Format(pc / 100.0);
                fmt = With(fmt, f => { f.Num = NumFmt.Percent; if (f.Dec < 0) f.Dec = t.Contains('.') ? t.Length - t.IndexOf('.') - 2 : 0; });
            }
            else if (t.StartsWith("$") && double.TryParse(t.Substring(1), NumberStyles.Float | NumberStyles.AllowThousands, Inv, out var cu))
            {
                raw = FormulaEvaluator.Format(cu);
                fmt = With(fmt, f => f.Num = NumFmt.Currency);
            }
            else if (t.Contains(',') && double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, Inv, out var th) && !t.StartsWith(","))
            {
                raw = FormulaEvaluator.Format(th);
            }
            else if (LooksLikeDate(t) && DateTime.TryParse(t, Inv, DateTimeStyles.None, out var dt))
            {
                raw = FormulaEvaluator.Format(Math.Floor(dt.ToOADate()));
                fmt = With(fmt, f => f.Num = NumFmt.Date);
            }
        }
        SetCell(r, c, new Cell { Raw = raw, Fmt = fmt });
    }

    private static bool LooksLikeDate(string t)
    {
        if (t.Length < 6 || t.Length > 12) return false;
        int digits = 0, seps = 0;
        foreach (var ch in t)
        {
            if (char.IsDigit(ch)) digits++;
            else if (ch == '-' || ch == '/') seps++;
            else return false;
        }
        return seps == 2 && digits >= 4;
    }

    private static CellFormat With(CellFormat? f, Action<CellFormat> change)
    {
        var n = f?.Clone() ?? new CellFormat();
        change(n);
        return n;
    }

    // ------------------------------------------------------------------ display

    public string Display(int r, int c, out bool isNumber, out bool isError, out bool isBool)
    {
        isNumber = isError = isBool = false;
        var v = Eval.Eval(r, c);
        if (v == null) return "";
        if (v is CellError e) { isError = true; return e.Code; }
        if (v is bool b) { isBool = true; return b ? "TRUE" : "FALSE"; }
        if (v is double d)
        {
            isNumber = true;
            return FormatNumber(d, GetFmt(r, c));
        }
        return (string)v;
    }

    public static string FormatNumber(double d, CellFormat? f)
    {
        var kind = f?.Num ?? NumFmt.General;
        int dec = f?.Dec ?? -1;
        switch (kind)
        {
            case NumFmt.Number: return d.ToString("F" + (dec < 0 ? 2 : dec), Inv);
            case NumFmt.Thousands: return d.ToString("#,##0" + Zeros(dec < 0 ? 2 : dec), Inv);
            case NumFmt.Currency:
                {
                    var body = Math.Abs(d).ToString("#,##0" + Zeros(dec < 0 ? 2 : dec), Inv);
                    return (d < 0 ? "-$" : "$") + body;
                }
            case NumFmt.Percent: return (d * 100).ToString("F" + (dec < 0 ? 0 : dec), Inv) + "%";
            case NumFmt.Date:
                try { return DateTime.FromOADate(d).ToString("yyyy-MM-dd", Inv); }
                catch (ArgumentException) { return FormulaEvaluator.Format(d); }
            default:
                if (dec >= 0) return d.ToString("F" + dec, Inv);
                return FormulaEvaluator.Format(d);
        }
    }

    private static string Zeros(int n) => n <= 0 ? "" : "." + new string('0', n);

    // ------------------------------------------------------------------ CSV / TSV

    /// <summary>Parses delimited text with optional "quoted" fields.</summary>
    public static List<List<string>> ParseDelimited(string text, char delim)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"' && sb.Length == 0) { quoted = true; any = true; }
            else if (ch == delim) { row.Add(sb.ToString()); sb.Clear(); any = true; }
            else if (ch == '\r') { }
            else if (ch == '\n')
            {
                row.Add(sb.ToString()); sb.Clear();
                rows.Add(row); row = new List<string>(); any = false;
            }
            else { sb.Append(ch); any = true; }
        }
        if (any || sb.Length > 0 || row.Count > 0) { row.Add(sb.ToString()); rows.Add(row); }
        return rows;
    }

    public static string Quote(string s, char delim)
    {
        if (s.IndexOf(delim) >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public void LoadCsv(string text)
    {
        Clear();
        var rows = ParseDelimited(text, DetectDelimiter(text));
        BeginEdit();
        for (int r = 0; r < rows.Count && r < MaxRows; r++)
            for (int c = 0; c < rows[r].Count && c < MaxCols; c++)
            {
                var s = rows[r][c];
                if (s.Length == 0) continue;
                // keep text that would otherwise be read as a formula or a number as text
                if (s[0] == '=' || s[0] == '\'') s = "'" + s;
                Put(Key(r, c), new Cell { Raw = s });
            }
        _depth = 0; _open = null;       // initial load is not undoable
        _undo.Clear(); _redo.Clear();
        AfterChange();
    }

    private static char DetectDelimiter(string text)
    {
        int end = text.IndexOf('\n');
        var head = end < 0 ? text : text.Substring(0, end);
        int commas = head.Count(ch => ch == ','), semis = head.Count(ch => ch == ';'), tabs = head.Count(ch => ch == '\t');
        if (tabs > commas && tabs >= semis) return '\t';
        if (semis > commas) return ';';
        return ',';
    }

    /// <summary>Calculated values as CSV (formulas are exported as their results).</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        int rows = UsedRows, cols = UsedCols;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(Quote(Display(r, c, out _, out _, out _), ','));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public string RangeToTsv(int r1, int c1, int r2, int c2)
    {
        var sb = new StringBuilder();
        for (int r = r1; r <= r2; r++)
        {
            for (int c = c1; c <= c2; c++)
            {
                if (c > c1) sb.Append('\t');
                sb.Append(Quote(Display(r, c, out _, out _, out _), '\t'));
            }
            if (r < r2) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Pastes plain text from another program; returns the size pasted (rows, cols).</summary>
    public (int, int) PasteText(int r0, int c0, string text)
    {
        var rows = ParseDelimited(text.TrimEnd('\n', '\r'), '\t');
        int maxC = 0;
        BeginEdit();
        for (int i = 0; i < rows.Count; i++)
        {
            for (int j = 0; j < rows[i].Count; j++)
            {
                int r = r0 + i, c = c0 + j;
                if (r >= MaxRows || c >= MaxCols) continue;
                Enter(r, c, rows[i][j]);
            }
            maxC = Math.Max(maxC, rows[i].Count);
        }
        EndEdit();
        return (rows.Count, maxC);
    }

    // ------------------------------------------------------------------ editing operations

    public void ClearContents(int r1, int c1, int r2, int c2)
    {
        BeginEdit();
        foreach (var key in _cells.Keys.ToList())
        {
            int r = (int)(key >> 10), c = (int)(key & 1023);
            if (r < r1 || r > r2 || c < c1 || c > c2) continue;
            var cell = _cells[key];
            Put(key, new Cell { Raw = "", Fmt = cell.Fmt });
        }
        EndEdit();
    }

    /// <summary>Copies the cells of a block (for internal copy/paste).</summary>
    public Cell?[,] Snapshot(int r1, int c1, int r2, int c2)
    {
        var a = new Cell?[r2 - r1 + 1, c2 - c1 + 1];
        for (int r = r1; r <= r2; r++)
            for (int c = c1; c <= c2; c++)
            {
                var cell = GetCell(r, c);
                a[r - r1, c - c1] = cell?.Copy();
            }
        return a;
    }

    /// <summary>Pastes a snapshot taken at (srcR, srcC), moving relative references.</summary>
    public void PasteSnapshot(Cell?[,] snap, int srcR, int srcC, int destR, int destC, bool valuesOnly = false)
    {
        BeginEdit();
        int rows = snap.GetLength(0), cols = snap.GetLength(1);
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < cols; j++)
            {
                int r = destR + i, c = destC + j;
                if (r >= MaxRows || c >= MaxCols) continue;
                var s = snap[i, j];
                if (s == null) { SetCell(r, c, null); continue; }
                var raw = FormulaEvaluator.ShiftFormula(s.Raw, destR - srcR, destC - srcC, MaxRows, MaxCols);
                SetCell(r, c, new Cell { Raw = raw, Fmt = valuesOnly ? GetFmt(r, c) : s.Fmt });
            }
        EndEdit();
    }

    // ------------------------------------------------------------------ fill

    private static int Mod(int a, int n) => ((a % n) + n) % n;

    private static bool TryTrailingNumber(string s, out string prefix, out long num, out int width)
    {
        prefix = ""; num = 0; width = 0;
        int i = s.Length;
        while (i > 0 && char.IsDigit(s[i - 1])) i--;
        if (i == s.Length || s.Length - i > 15) return false;
        prefix = s.Substring(0, i);
        width = s.Length - i;
        return long.TryParse(s.Substring(i), NumberStyles.None, Inv, out num);
    }

    /// <summary>Extends the block (r1,c1)-(r2,c2) to the target block that contains it.</summary>
    public void Fill(int r1, int c1, int r2, int c2, int tr1, int tc1, int tr2, int tc2)
    {
        bool vertical = tc1 == c1 && tc2 == c2 && (tr1 != r1 || tr2 != r2);
        bool horizontal = tr1 == r1 && tr2 == r2 && (tc1 != c1 || tc2 != c2);
        if (!vertical && !horizontal) return;

        BeginEdit();
        if (vertical)
        {
            int n = r2 - r1 + 1;
            for (int c = c1; c <= c2; c++)
            {
                var src = new List<Cell?>();
                for (int r = r1; r <= r2; r++) src.Add(GetCell(r, c));
                for (int t = tr1; t <= tr2; t++)
                {
                    if (t >= r1 && t <= r2) continue;
                    int k = Mod(t - r1, n);
                    SetCell(t, c, FillOne(src, k, t - r1, n, t - (r1 + k), 0));
                }
            }
        }
        else
        {
            int n = c2 - c1 + 1;
            for (int r = r1; r <= r2; r++)
            {
                var src = new List<Cell?>();
                for (int c = c1; c <= c2; c++) src.Add(GetCell(r, c));
                for (int t = tc1; t <= tc2; t++)
                {
                    if (t >= c1 && t <= c2) continue;
                    int k = Mod(t - c1, n);
                    SetCell(r, t, FillOne(src, k, t - c1, n, 0, t - (c1 + k)));
                }
            }
        }
        EndEdit();
    }

    private Cell? FillOne(List<Cell?> src, int k, int offset, int n, int dRow, int dCol)
    {
        var s = src[k];
        if (s == null) return null;
        string raw = s.Raw;

        if (raw.Length > 0 && raw[0] != '=')
        {
            // linear series of numbers
            bool allNum = true;
            var nums = new List<double>();
            foreach (var x in src)
            {
                if (x != null && x.Raw.Length > 0 && x.Raw[0] != '=' && double.TryParse(x.Raw, NumberStyles.Float, Inv, out var d)) nums.Add(d);
                else { allNum = false; break; }
            }
            if (allNum && n >= 2)
            {
                double step = (nums[n - 1] - nums[0]) / (n - 1);
                double v = nums[0] + step * offset;
                return new Cell { Raw = FormulaEvaluator.Format(Math.Round(v, 10)), Fmt = s.Fmt };
            }
            if (n == 1)
            {
                if (allNum) return new Cell { Raw = raw, Fmt = s.Fmt };
                if (TryTrailingNumber(raw, out var prefix, out var num, out var width))
                {
                    long next = num + offset;
                    if (next < 0) next = 0;
                    return new Cell { Raw = prefix + next.ToString().PadLeft(width, '0'), Fmt = s.Fmt };
                }
            }
        }
        return new Cell { Raw = FormulaEvaluator.ShiftFormula(raw, dRow, dCol, MaxRows, MaxCols), Fmt = s.Fmt };
    }

    // ------------------------------------------------------------------ sort / filter

    private static int CompareValues(object? a, object? b)
    {
        // blanks last, numbers before text
        if (a == null && b == null) return 0;
        if (a == null) return 1;
        if (b == null) return -1;
        if (a is CellError) a = a.ToString();
        if (b is CellError) b = b.ToString();
        if (a is double x && b is double y) return x.CompareTo(y);
        if (a is double) return -1;
        if (b is double) return 1;
        return string.Compare(a?.ToString(), b?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void SortRows(int r1, int r2, int c1, int c2, int keyCol, bool ascending)
    {
        if (r2 <= r1) return;
        int count = r2 - r1 + 1;
        var cells = new Cell?[count][];
        var keys = new object?[count];
        for (int i = 0; i < count; i++)
        {
            int r = r1 + i;
            cells[i] = new Cell?[c2 - c1 + 1];
            for (int c = c1; c <= c2; c++) cells[i][c - c1] = GetCell(r, c)?.Copy();
            keys[i] = Eval.Eval(r, keyCol);
        }
        var order = Enumerable.Range(0, count).ToList();
        order.Sort((p, q) =>
        {
            int cmp;
            if (ascending) cmp = CompareValues(keys[p], keys[q]);
            else if (keys[p] == null || keys[q] == null) cmp = CompareValues(keys[p], keys[q]);   // blanks stay last
            else cmp = -CompareValues(keys[p], keys[q]);
            return cmp != 0 ? cmp : p.CompareTo(q);
        });

        BeginEdit();
        for (int i = 0; i < count; i++)
            for (int c = c1; c <= c2; c++)
                SetCell(r1 + i, c, cells[order[i]][c - c1]);
        EndEdit();
    }

    /// <summary>Hides data rows whose value in the column does not satisfy the condition.</summary>
    public void ApplyFilter(int col, string op, string value, int firstRow, int lastRow)
    {
        HiddenRows.Clear();
        Func<object?, bool> pred;
        switch (op)
        {
            case "contains": pred = MakeContains(value); break;
            case "equals": pred = FormulaEvaluator.MakeCriteria("=" + value); break;
            case "not equal": pred = FormulaEvaluator.MakeCriteria("<>" + value); break;
            case ">": pred = FormulaEvaluator.MakeCriteria(">" + value); break;
            case "<": pred = FormulaEvaluator.MakeCriteria("<" + value); break;
            case "blank": pred = v => v == null || (v is string s && s.Length == 0); break;
            case "not blank": pred = v => !(v == null || (v is string s && s.Length == 0)); break;
            default: pred = _ => true; break;
        }
        for (int r = firstRow; r <= lastRow; r++)
            if (!pred(Eval.Eval(r, col))) HiddenRows.Add(r);
        LayoutChanged?.Invoke();
    }

    private static Func<object?, bool> MakeContains(string needle)
    {
        return v =>
        {
            string s = v == null ? "" : (v is double d ? FormulaEvaluator.Format(d) : v.ToString() ?? "");
            return s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        };
    }

    public void ClearFilter()
    {
        if (HiddenRows.Count == 0) return;
        HiddenRows.Clear();
        LayoutChanged?.Invoke();
    }

    // ------------------------------------------------------------------ misc

    /// <summary>Every non-empty cell, for exporters.</summary>
    public IEnumerable<(int Row, int Col, Cell Cell)> AllCells()
    {
        foreach (var kv in _cells.OrderBy(k => k.Key))
            yield return ((int)(kv.Key >> 10), (int)(kv.Key & 1023), kv.Value);
    }

    /// <summary>Loads cells directly (used by importers, not undoable).</summary>
    public void BulkSet(IEnumerable<(int Row, int Col, Cell Cell)> cells)
    {
        foreach (var (r, c, cell) in cells)
        {
            if (r < 0 || c < 0 || r >= MaxRows || c >= MaxCols) continue;
            if (cell.Raw.Length == 0 && (cell.Fmt == null || cell.Fmt.IsDefault)) continue;
            _cells[Key(r, c)] = cell;
        }
        _undo.Clear(); _redo.Clear();
        AfterChange();
        LayoutChanged?.Invoke();
    }
}
