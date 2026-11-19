using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RedStarOS.Services;

public sealed class FormulaException : Exception
{
    public FormulaException(string message) : base(message) { }
}

/// <summary>An error value stored in a cell (#DIV/0!, #NAME? ...).</summary>
public sealed class CellError
{
    public string Code { get; }
    public CellError(string code) { Code = code; }
    public override string ToString() => Code;
}

/// <summary>A rectangular block of cells (zero-based, inclusive).</summary>
public sealed class RangeRef
{
    public int R1, C1, R2, C2;
    public RangeRef(int r1, int c1, int r2, int c2)
    {
        R1 = Math.Min(r1, r2); R2 = Math.Max(r1, r2);
        C1 = Math.Min(c1, c2); C2 = Math.Max(c1, c2);
    }
    public int Rows => R2 - R1 + 1;
    public int Cols => C2 - C1 + 1;
}

/// <summary>
/// Spreadsheet formula engine. Values are double, string, bool, CellError or null (blank).
/// Supports + - * / ^ % &amp; comparisons, A1 / $A$1 references, ranges, and ~80 functions
/// (SUM, IF, VLOOKUP, SUMIF, COUNTIF, TEXT, DATE ...). Results are cached until <see cref="Invalidate"/>.
/// </summary>
public sealed class FormulaEvaluator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex RefRx = new(@"^(\$?)([A-Za-z]{1,3})(\$?)(\d{1,7})$", RegexOptions.Compiled);

    private readonly Func<int, int, string> _raw;
    private readonly int _rows, _cols;
    private readonly Dictionary<(int, int), object?> _cache = new();
    private readonly Dictionary<string, Node> _parsed = new();
    private readonly HashSet<(int, int)> _stack = new();
    private int _curRow, _curCol;
    private readonly Random _rng = new();

    /// <summary>Optional: number of rows / columns that contain data, used to keep huge ranges cheap.</summary>
    public Func<int> UsedRows { get; set; } = () => int.MaxValue;
    public Func<int> UsedCols { get; set; } = () => int.MaxValue;

    public FormulaEvaluator(Func<int, int, string> raw, int rows, int cols)
    {
        _raw = raw;
        _rows = rows;
        _cols = cols;
    }

    public void Invalidate() => _cache.Clear();

    // ------------------------------------------------------------------ names

    public static string ColumnName(int col)
    {
        var s = "";
        col++;
        while (col > 0)
        {
            int m = (col - 1) % 26;
            s = (char)('A' + m) + s;
            col = (col - 1) / 26;
        }
        return s;
    }

    public static int ParseColumn(string letters)
    {
        int c = 0;
        foreach (var ch in letters.ToUpperInvariant()) c = c * 26 + (ch - 'A' + 1);
        return c - 1;
    }

    public static string CellName(int row, int col) => ColumnName(col) + (row + 1);

    /// <summary>Formats a plain number the way a General cell shows it.</summary>
    public static string Format(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "#NUM!";
        if (v == 0) return "0";
        var a = Math.Abs(v);
        if (a >= 1e15 || a < 1e-9) return v.ToString("0.#####E+00", Inv);
        return Math.Round(v, 10).ToString("0.##########", Inv);
    }

    /// <summary>Moves relative references in a formula by (dRow, dCol), like copy/paste or fill does.</summary>
    public static string ShiftFormula(string f, int dRow, int dCol, int maxRows, int maxCols)
    {
        if (string.IsNullOrEmpty(f) || f[0] != '=' || (dRow == 0 && dCol == 0)) return f;
        var sb = new StringBuilder();
        int i = 0;
        while (i < f.Length)
        {
            char ch = f[i];
            if (ch == '"')
            {
                int j = i + 1;
                while (j < f.Length)
                {
                    if (f[j] == '"')
                    {
                        if (j + 1 < f.Length && f[j + 1] == '"') { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                int end = Math.Min(j + 1, f.Length);
                sb.Append(f, i, end - i);
                i = end;
                continue;
            }
            if (char.IsLetter(ch) || ch == '$')
            {
                bool prevOk = i == 0 || !(char.IsLetterOrDigit(f[i - 1]) || f[i - 1] == '_' || f[i - 1] == '.');
                int j = i;
                while (j < f.Length && (char.IsLetterOrDigit(f[j]) || f[j] == '$' || f[j] == '_')) j++;
                var tok = f.Substring(i, j - i);
                var m = RefRx.Match(tok);
                int k = j;
                while (k < f.Length && f[k] == ' ') k++;
                bool isFunc = k < f.Length && f[k] == '(';
                if (prevOk && m.Success && !isFunc)
                {
                    bool absCol = m.Groups[1].Value.Length > 0, absRow = m.Groups[3].Value.Length > 0;
                    int col = ParseColumn(m.Groups[2].Value);
                    int row = int.Parse(m.Groups[4].Value, Inv) - 1;
                    if (!absCol) col += dCol;
                    if (!absRow) row += dRow;
                    if (col < 0 || row < 0 || col >= maxCols || row >= maxRows) sb.Append("#REF!");
                    else sb.Append(absCol ? "$" : "").Append(ColumnName(col)).Append(absRow ? "$" : "").Append(row + 1);
                }
                else sb.Append(tok);
                i = j;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ AST

    private abstract class Node { }
    private sealed class NumNode : Node { public double V; }
    private sealed class StrNode : Node { public string V = ""; }
    private sealed class BoolNode : Node { public bool V; }
    private sealed class RefNode : Node { public int R, C; }
    private sealed class RangeNode : Node { public int R1, C1, R2, C2; }
    private sealed class UnaryNode : Node { public char Op; public Node A = null!; }
    private sealed class PercentNode : Node { public Node A = null!; }
    private sealed class BinNode : Node { public string Op = ""; public Node A = null!, B = null!; }
    private sealed class FuncNode : Node { public string Name = ""; public List<Node> Args = new(); }

    private sealed class Parser
    {
        private readonly string _s;
        private int _p;
        private readonly int _maxRows, _maxCols;

        public Parser(string s, int maxRows, int maxCols) { _s = s; _maxRows = maxRows; _maxCols = maxCols; }

        public Node Parse()
        {
            var n = Comparison();
            Ws();
            if (_p < _s.Length) throw new FormulaException("#ERROR!");
            return n;
        }

        private void Ws() { while (_p < _s.Length && char.IsWhiteSpace(_s[_p])) _p++; }

        private bool Peek(string t)
        {
            Ws();
            return string.CompareOrdinal(_s, _p, t, 0, t.Length) == 0;
        }

        private bool Eat(string t)
        {
            if (Peek(t)) { _p += t.Length; return true; }
            return false;
        }

        private Node Comparison()
        {
            var l = Concat();
            while (true)
            {
                string? op = null;
                foreach (var o in new[] { "<>", "<=", ">=", "=", "<", ">" })
                    if (Peek(o)) { op = o; break; }
                if (op == null) return l;
                _p += op.Length;
                var r = Concat();
                l = new BinNode { Op = op, A = l, B = r };
            }
        }

        private Node Concat()
        {
            var l = Additive();
            while (Eat("&")) l = new BinNode { Op = "&", A = l, B = Additive() };
            return l;
        }

        private Node Additive()
        {
            var l = Term();
            while (true)
            {
                if (Eat("+")) l = new BinNode { Op = "+", A = l, B = Term() };
                else if (Eat("-")) l = new BinNode { Op = "-", A = l, B = Term() };
                else return l;
            }
        }

        private Node Term()
        {
            var l = Power();
            while (true)
            {
                if (Eat("*")) l = new BinNode { Op = "*", A = l, B = Power() };
                else if (Eat("/")) l = new BinNode { Op = "/", A = l, B = Power() };
                else return l;
            }
        }

        private Node Power()
        {
            var l = Unary();
            while (Eat("^")) l = new BinNode { Op = "^", A = l, B = Unary() };
            return l;
        }

        private Node Unary()
        {
            if (Eat("-")) return new UnaryNode { Op = '-', A = Unary() };
            if (Eat("+")) return Unary();
            return Postfix();
        }

        private Node Postfix()
        {
            var n = Primary();
            while (Eat("%")) n = new PercentNode { A = n };
            return n;
        }

        private Node Primary()
        {
            Ws();
            if (_p >= _s.Length) throw new FormulaException("#ERROR!");
            char c = _s[_p];

            if (c == '(')
            {
                _p++;
                var n = Comparison();
                if (!Eat(")")) throw new FormulaException("#ERROR!");
                return n;
            }

            if (c == '"')
            {
                _p++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (_p >= _s.Length) throw new FormulaException("#ERROR!");
                    if (_s[_p] == '"')
                    {
                        if (_p + 1 < _s.Length && _s[_p + 1] == '"') { sb.Append('"'); _p += 2; continue; }
                        _p++;
                        break;
                    }
                    sb.Append(_s[_p++]);
                }
                return new StrNode { V = sb.ToString() };
            }

            if (char.IsDigit(c) || c == '.')
            {
                int start = _p;
                while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) _p++;
                if (_p < _s.Length && (_s[_p] == 'e' || _s[_p] == 'E'))
                {
                    int save = _p;
                    _p++;
                    if (_p < _s.Length && (_s[_p] == '+' || _s[_p] == '-')) _p++;
                    if (_p < _s.Length && char.IsDigit(_s[_p])) { while (_p < _s.Length && char.IsDigit(_s[_p])) _p++; }
                    else _p = save;
                }
                if (!double.TryParse(_s.AsSpan(start, _p - start), NumberStyles.Float, Inv, out var num))
                    throw new FormulaException("#ERROR!");
                return new NumNode { V = num };
            }

            if (c == '#')
            {
                int start = _p;
                _p++;
                while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '/' || _s[_p] == '!' || _s[_p] == '?')) _p++;
                throw new FormulaException(_s.Substring(start, _p - start).ToUpperInvariant());
            }

            if (char.IsLetter(c) || c == '$' || c == '_')
            {
                int start = _p;
                while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '$' || _s[_p] == '_' || _s[_p] == '.')) _p++;
                var id = _s.Substring(start, _p - start);

                int save = _p;
                Ws();
                if (_p < _s.Length && _s[_p] == '(')
                {
                    _p++;
                    var f = new FuncNode { Name = id.ToUpperInvariant() };
                    Ws();
                    if (Eat(")")) return f;
                    while (true)
                    {
                        f.Args.Add(Comparison());
                        if (Eat(",") || Eat(";")) continue;
                        if (Eat(")")) break;
                        throw new FormulaException("#ERROR!");
                    }
                    return f;
                }
                _p = save;

                var up = id.ToUpperInvariant();
                if (up == "TRUE") return new BoolNode { V = true };
                if (up == "FALSE") return new BoolNode { V = false };

                var m = RefRx.Match(id);
                if (!m.Success) throw new FormulaException("#NAME?");
                var a = ToCell(m);
                Ws();
                if (_p < _s.Length && _s[_p] == ':')
                {
                    _p++;
                    Ws();
                    int s2 = _p;
                    while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '$')) _p++;
                    var m2 = RefRx.Match(_s.Substring(s2, _p - s2));
                    if (!m2.Success) throw new FormulaException("#NAME?");
                    var b = ToCell(m2);
                    return new RangeNode
                    {
                        R1 = Math.Min(a.Item1, b.Item1), C1 = Math.Min(a.Item2, b.Item2),
                        R2 = Math.Max(a.Item1, b.Item1), C2 = Math.Max(a.Item2, b.Item2)
                    };
                }
                return new RefNode { R = a.Item1, C = a.Item2 };
            }

            throw new FormulaException("#ERROR!");
        }

        private (int, int) ToCell(Match m)
        {
            int col = ParseColumn(m.Groups[2].Value);
            int row = int.Parse(m.Groups[4].Value, Inv) - 1;
            if (col < 0 || col >= _maxCols || row < 0 || row >= _maxRows) throw new FormulaException("#REF!");
            return (row, col);
        }
    }

    // ------------------------------------------------------------------ evaluation

    /// <summary>Value of a cell: double, string, bool, CellError or null when blank.</summary>
    public object? Eval(int row, int col)
    {
        if (_cache.TryGetValue((row, col), out var cached)) return cached;

        var raw = _raw(row, col);
        object? val;
        if (string.IsNullOrEmpty(raw)) val = null;
        else if (raw[0] == '=' && raw.Length > 1)
        {
            if (_stack.Count > 1200) return new CellError("#DEPTH!");
            if (!_stack.Add((row, col))) return new CellError("#CIRC!");
            int sr = _curRow, sc = _curCol;
            _curRow = row; _curCol = col;
            try
            {
                if (!_parsed.TryGetValue(raw, out var node))
                {
                    node = new Parser(raw.Substring(1), _rows, _cols).Parse();
                    _parsed[raw] = node;
                }
                val = ScalarOf(EvalNode(node));
            }
            catch (FormulaException ex) { val = new CellError(ex.Message); }
            catch (Exception) { val = new CellError("#ERROR!"); }
            finally
            {
                _stack.Remove((row, col));
                _curRow = sr; _curCol = sc;
            }
        }
        else if (raw[0] == '\'') val = raw.Substring(1);
        else if (double.TryParse(raw, NumberStyles.Float, Inv, out var d) && !double.IsNaN(d) && !double.IsInfinity(d)) val = d;
        else val = raw;

        _cache[(row, col)] = val;
        return val;
    }

    private object? CellVal(int r, int c)
    {
        var v = Eval(r, c);
        if (v is CellError e) throw new FormulaException(e.Code);
        return v;
    }

    private object? ScalarOf(object? v)
    {
        if (v is RangeRef rr)
        {
            if (rr.Rows == 1 && rr.Cols == 1) return CellVal(rr.R1, rr.C1);
            throw new FormulaException("#VALUE!");
        }
        return v;
    }

    private object? EvalNode(Node n)
    {
        switch (n)
        {
            case NumNode x: return x.V;
            case StrNode s: return s.V;
            case BoolNode b: return b.V;
            case RefNode r: return CellVal(r.R, r.C);
            case RangeNode g: return new RangeRef(g.R1, g.C1, g.R2, g.C2);
            case UnaryNode u: { var v = ToNum(ScalarOf(EvalNode(u.A))); return u.Op == '-' ? -v : v; }
            case PercentNode p: return ToNum(ScalarOf(EvalNode(p.A))) / 100.0;
            case BinNode bn: return EvalBin(bn);
            case FuncNode f: return CallFunc(f);
        }
        throw new FormulaException("#ERROR!");
    }

    private object? EvalBin(BinNode b)
    {
        var a = ScalarOf(EvalNode(b.A));
        var c = ScalarOf(EvalNode(b.B));
        switch (b.Op)
        {
            case "+": return Check(ToNum(a) + ToNum(c));
            case "-": return Check(ToNum(a) - ToNum(c));
            case "*": return Check(ToNum(a) * ToNum(c));
            case "/":
                {
                    var d = ToNum(c);
                    if (d == 0) throw new FormulaException("#DIV/0!");
                    return Check(ToNum(a) / d);
                }
            case "^": return Check(Math.Pow(ToNum(a), ToNum(c)));
            case "&": return ToStr(a) + ToStr(c);
            case "=": return Cmp(a, c) == 0;
            case "<>": return Cmp(a, c) != 0;
            case "<": return Cmp(a, c) < 0;
            case ">": return Cmp(a, c) > 0;
            case "<=": return Cmp(a, c) <= 0;
            case ">=": return Cmp(a, c) >= 0;
        }
        throw new FormulaException("#ERROR!");
    }

    private static double Check(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormulaException("#NUM!");
        return v;
    }

    private static int Rank(object o) => o is double ? 0 : o is string ? 1 : 2;

    private static int Cmp(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) a = b is string ? "" : (b is bool ? (object)false : 0.0);
        if (b == null) b = a is string ? "" : (a is bool ? (object)false : 0.0);
        int ra = Rank(a), rb = Rank(b);
        if (ra != rb) return ra.CompareTo(rb);
        if (a is double x && b is double y) return x.CompareTo(y);
        if (a is string s && b is string t) return string.Compare(s, t, StringComparison.OrdinalIgnoreCase);
        if (a is bool p && b is bool q) return p.CompareTo(q);
        return 0;
    }

    public static double ToNum(object? v)
    {
        switch (v)
        {
            case null: return 0;
            case double d: return d;
            case bool b: return b ? 1 : 0;
            case string s:
                {
                    var t = s.Trim();
                    if (t.EndsWith("%") && double.TryParse(t.TrimEnd('%'), NumberStyles.Float, Inv, out var pc)) return pc / 100.0;
                    if (double.TryParse(t, NumberStyles.Float, Inv, out var d2)) return d2;
                    throw new FormulaException("#VALUE!");
                }
        }
        throw new FormulaException("#VALUE!");
    }

    private static string ToStr(object? v)
    {
        switch (v)
        {
            case null: return "";
            case double d: return Format(d);
            case bool b: return b ? "TRUE" : "FALSE";
            default: return v.ToString() ?? "";
        }
    }

    private static bool ToBool(object? v)
    {
        switch (v)
        {
            case null: return false;
            case bool b: return b;
            case double d: return d != 0;
            case string s:
                if (s.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return true;
                if (s.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return false;
                throw new FormulaException("#VALUE!");
        }
        throw new FormulaException("#VALUE!");
    }

    // ------------------------------------------------------------------ ranges

    private IEnumerable<(int, int)> Cells(RangeRef rr)
    {
        int lastR = Math.Min(rr.R2, Math.Max(0, UsedRows()) - 1);
        int lastC = Math.Min(rr.C2, Math.Max(0, UsedCols()) - 1);
        for (int r = rr.R1; r <= lastR; r++)
            for (int c = rr.C1; c <= lastC; c++)
                yield return (r, c);
    }

    private RangeRef AsRange(Node n)
    {
        var v = EvalNode(n);
        if (v is RangeRef rr) return rr;
        throw new FormulaException("#VALUE!");
    }

    /// <summary>Numbers found in all arguments: range cells that hold numbers, plus directly typed values.</summary>
    private List<double> Nums(FuncNode f)
    {
        var list = new List<double>();
        foreach (var arg in f.Args)
        {
            var v = EvalNode(arg);
            if (v is RangeRef rr)
            {
                foreach (var (r, c) in Cells(rr))
                {
                    var cv = CellVal(r, c);
                    if (cv is double d) list.Add(d);
                }
            }
            else if (v != null) list.Add(ToNum(v));
        }
        return list;
    }

    private IEnumerable<object?> Values(Node arg)
    {
        var v = EvalNode(arg);
        if (v is RangeRef rr)
        {
            foreach (var (r, c) in Cells(rr)) yield return CellVal(r, c);
        }
        else yield return v;
    }

    private object? RangeGet(RangeRef rr, int dr, int dc) => CellVal(rr.R1 + dr, rr.C1 + dc);

    // ------------------------------------------------------------------ criteria (COUNTIF / SUMIF / filters)

    /// <summary>Builds a predicate from text like "&gt;5", "&lt;=3", "&lt;&gt;x", "abc*".</summary>
    public static Func<object?, bool> MakeCriteria(object? crit)
    {
        if (crit is double or bool)
        {
            double d = crit is bool bb ? (bb ? 1.0 : 0.0) : (double)crit;
            return v => v is double x && x == d;
        }
        var s = crit?.ToString() ?? "";
        string op = "=", rest = s;
        foreach (var o in new[] { "<>", "<=", ">=", "<", ">", "=" })
            if (s.StartsWith(o, StringComparison.Ordinal)) { op = o; rest = s.Substring(o.Length); break; }
        bool isNum = double.TryParse(rest, NumberStyles.Float, Inv, out var rd);

        return v =>
        {
            if (isNum)
            {
                if (v is double x)
                {
                    switch (op)
                    {
                        case "=": return x == rd;
                        case "<>": return x != rd;
                        case "<": return x < rd;
                        case ">": return x > rd;
                        case "<=": return x <= rd;
                        default: return x >= rd;
                    }
                }
                return op == "<>";
            }
            string vs = v == null ? "" : (v is double dd ? Format(dd) : (v is bool b ? (b ? "TRUE" : "FALSE") : v.ToString() ?? ""));
            if (op == "=" || op == "<>")
            {
                bool m = Wild(vs, rest);
                return op == "=" ? m : !m;
            }
            int cmp = string.Compare(vs, rest, StringComparison.OrdinalIgnoreCase);
            switch (op)
            {
                case "<": return cmp < 0;
                case ">": return cmp > 0;
                case "<=": return cmp <= 0;
                default: return cmp >= 0;
            }
        };
    }

    private static bool Wild(string text, string pattern)
    {
        if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
            return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
        var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    // ------------------------------------------------------------------ functions

    private object? CallFunc(FuncNode f)
    {
        var a = f.Args;
        object? Arg(int i)
        {
            if (i >= a.Count) throw new FormulaException("#N/A");
            return ScalarOf(EvalNode(a[i]));
        }
        double Num(int i) => ToNum(Arg(i));
        void Need(int min, int max)
        {
            if (a.Count < min || a.Count > max) throw new FormulaException("#N/A");
        }

        switch (f.Name)
        {
            // ---- aggregates
            case "SUM": return Check(Nums(f).Sum());
            case "PRODUCT": { double p = 1; foreach (var x in Nums(f)) p *= x; return Check(p); }
            case "AVERAGE":
            case "AVG":
                {
                    var n = Nums(f);
                    if (n.Count == 0) throw new FormulaException("#DIV/0!");
                    return n.Average();
                }
            case "MIN": { var n = Nums(f); return n.Count == 0 ? 0.0 : n.Min(); }
            case "MAX": { var n = Nums(f); return n.Count == 0 ? 0.0 : n.Max(); }
            case "COUNT":
                {
                    int cnt = 0;
                    foreach (var arg in a)
                    {
                        var v = EvalNode(arg);
                        if (v is RangeRef rr) { foreach (var (r, c) in Cells(rr)) if (CellVal(r, c) is double) cnt++; }
                        else if (v is double) cnt++;
                    }
                    return (double)cnt;
                }
            case "COUNTA":
                {
                    int cnt = 0;
                    foreach (var arg in a) foreach (var v in Values(arg)) if (v != null && !(v is string s && s.Length == 0)) cnt++;
                    return (double)cnt;
                }
            case "COUNTBLANK":
                {
                    Need(1, 1);
                    var rr = AsRange(a[0]);
                    int filled = 0;
                    foreach (var (r, c) in Cells(rr)) { var v = CellVal(r, c); if (v != null && !(v is string s && s.Length == 0)) filled++; }
                    return (double)(rr.Rows * rr.Cols - filled);
                }
            case "MEDIAN":
                {
                    var n = Nums(f);
                    if (n.Count == 0) throw new FormulaException("#NUM!");
                    n.Sort();
                    int m = n.Count / 2;
                    return n.Count % 2 == 1 ? n[m] : (n[m - 1] + n[m]) / 2;
                }
            case "STDEV":
                {
                    var n = Nums(f);
                    if (n.Count < 2) throw new FormulaException("#DIV/0!");
                    double mean = n.Average();
                    return Math.Sqrt(n.Sum(x => (x - mean) * (x - mean)) / (n.Count - 1));
                }
            case "LARGE":
            case "SMALL":
                {
                    Need(2, 2);
                    var n = new List<double>();
                    foreach (var v in Values(a[0])) if (v is double d) n.Add(d);
                    int k = (int)Num(1);
                    if (k < 1 || k > n.Count) throw new FormulaException("#NUM!");
                    n.Sort();
                    return f.Name == "SMALL" ? n[k - 1] : n[n.Count - k];
                }
            case "SUMPRODUCT":
                {
                    if (a.Count < 1) throw new FormulaException("#N/A");
                    var ranges = a.Select(AsRange).ToList();
                    int rows = ranges[0].Rows, cols = ranges[0].Cols;
                    foreach (var rg in ranges) if (rg.Rows != rows || rg.Cols != cols) throw new FormulaException("#VALUE!");
                    double total = 0;
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            double p = 1;
                            foreach (var rg in ranges) { var v = RangeGet(rg, r, c); p *= v is double d ? d : 0; }
                            total += p;
                        }
                    return total;
                }
            case "COUNTIF":
                {
                    Need(2, 2);
                    var rr = AsRange(a[0]);
                    var pred = MakeCriteria(Arg(1));
                    int cnt = 0;
                    foreach (var (r, c) in Cells(rr)) if (pred(CellVal(r, c))) cnt++;
                    return (double)cnt;
                }
            case "SUMIF":
            case "AVERAGEIF":
                {
                    Need(2, 3);
                    var rr = AsRange(a[0]);
                    var pred = MakeCriteria(Arg(1));
                    var sr = a.Count == 3 ? AsRange(a[2]) : rr;
                    double sum = 0; int cnt = 0;
                    foreach (var (r, c) in Cells(rr))
                    {
                        if (!pred(CellVal(r, c))) continue;
                        int tr = sr.R1 + (r - rr.R1), tc = sr.C1 + (c - rr.C1);
                        if (tr >= _rows || tc >= _cols) continue;
                        if (CellVal(tr, tc) is double d) { sum += d; cnt++; }
                    }
                    if (f.Name == "AVERAGEIF") { if (cnt == 0) throw new FormulaException("#DIV/0!"); return sum / cnt; }
                    return sum;
                }
            case "COUNTIFS":
            case "SUMIFS":
                {
                    bool isSum = f.Name == "SUMIFS";
                    int first = isSum ? 1 : 0;
                    if (a.Count < first + 2 || (a.Count - first) % 2 != 0) throw new FormulaException("#N/A");
                    var sumR = isSum ? AsRange(a[0]) : null;
                    var pairs = new List<(RangeRef, Func<object?, bool>)>();
                    for (int i = first; i < a.Count; i += 2) pairs.Add((AsRange(a[i]), MakeCriteria(Arg(i + 1))));
                    var baseR = pairs[0].Item1;
                    double sum = 0; int cnt = 0;
                    for (int r = 0; r < baseR.Rows; r++)
                        for (int c = 0; c < baseR.Cols; c++)
                        {
                            bool ok = true;
                            foreach (var (rg, pr) in pairs)
                            {
                                if (r >= rg.Rows || c >= rg.Cols || !pr(RangeGet(rg, r, c))) { ok = false; break; }
                            }
                            if (!ok) continue;
                            cnt++;
                            if (sumR != null && r < sumR.Rows && c < sumR.Cols && RangeGet(sumR, r, c) is double d) sum += d;
                        }
                    return isSum ? sum : (double)cnt;
                }

            // ---- math
            case "ABS": Need(1, 1); return Math.Abs(Num(0));
            case "SQRT": { Need(1, 1); var v = Num(0); if (v < 0) throw new FormulaException("#NUM!"); return Math.Sqrt(v); }
            case "POWER": Need(2, 2); return Check(Math.Pow(Num(0), Num(1)));
            case "EXP": Need(1, 1); return Check(Math.Exp(Num(0)));
            case "LN": { Need(1, 1); var v = Num(0); if (v <= 0) throw new FormulaException("#NUM!"); return Math.Log(v); }
            case "LOG10": { Need(1, 1); var v = Num(0); if (v <= 0) throw new FormulaException("#NUM!"); return Math.Log10(v); }
            case "LOG":
                {
                    Need(1, 2);
                    var v = Num(0);
                    var bs = a.Count > 1 ? Num(1) : 10;
                    if (v <= 0 || bs <= 0 || bs == 1) throw new FormulaException("#NUM!");
                    return Math.Log(v) / Math.Log(bs);
                }
            case "MOD":
                {
                    Need(2, 2);
                    var x = Num(0); var y = Num(1);
                    if (y == 0) throw new FormulaException("#DIV/0!");
                    return x - y * Math.Floor(x / y);
                }
            case "INT": Need(1, 1); return Math.Floor(Num(0));
            case "TRUNC": { Need(1, 2); var d = a.Count > 1 ? (int)Num(1) : 0; var p = Math.Pow(10, d); return Math.Truncate(Num(0) * p) / p; }
            case "SIGN": Need(1, 1); return (double)Math.Sign(Num(0));
            case "PI": return Math.PI;
            case "RAND": return _rng.NextDouble();
            case "RANDBETWEEN": { Need(2, 2); var lo = (int)Math.Ceiling(Num(0)); var hi = (int)Math.Floor(Num(1)); if (hi < lo) throw new FormulaException("#NUM!"); return (double)_rng.Next(lo, hi + 1); }
            case "ROUND":
            case "ROUNDUP":
            case "ROUNDDOWN":
                {
                    Need(1, 2);
                    var v = Num(0);
                    int d = a.Count > 1 ? (int)Num(1) : 0;
                    double p = Math.Pow(10, d);
                    double x = v * p;
                    if (f.Name == "ROUND") x = Math.Round(x, MidpointRounding.AwayFromZero);
                    else if (f.Name == "ROUNDUP") x = x < 0 ? Math.Floor(x) : Math.Ceiling(x);
                    else x = Math.Truncate(x);
                    return Check(x / p);
                }
            case "CEILING": { Need(1, 2); var s = a.Count > 1 ? Num(1) : 1; if (s == 0) return 0.0; return Math.Ceiling(Num(0) / s) * s; }
            case "FLOOR": { Need(1, 2); var s = a.Count > 1 ? Num(1) : 1; if (s == 0) throw new FormulaException("#DIV/0!"); return Math.Floor(Num(0) / s) * s; }

            // ---- logic
            case "IF":
                {
                    Need(2, 3);
                    bool t = ToBool(Arg(0));
                    if (t) return ScalarOf(EvalNode(a[1]));
                    return a.Count > 2 ? ScalarOf(EvalNode(a[2])) : false;
                }
            case "IFERROR":
                {
                    Need(2, 2);
                    try { return ScalarOf(EvalNode(a[0])); }
                    catch (FormulaException) { return ScalarOf(EvalNode(a[1])); }
                }
            case "IFNA":
                {
                    Need(2, 2);
                    try { return ScalarOf(EvalNode(a[0])); }
                    catch (FormulaException ex) when (ex.Message == "#N/A") { return ScalarOf(EvalNode(a[1])); }
                }
            case "AND": { bool r = true; foreach (var arg in a) foreach (var v in Values(arg)) if (v != null) r &= ToBool(v); return r; }
            case "OR": { bool r = false; foreach (var arg in a) foreach (var v in Values(arg)) if (v != null) r |= ToBool(v); return r; }
            case "NOT": Need(1, 1); return !ToBool(Arg(0));
            case "TRUE": return true;
            case "FALSE": return false;
            case "CHOOSE":
                {
                    if (a.Count < 2) throw new FormulaException("#N/A");
                    int idx = (int)Num(0);
                    if (idx < 1 || idx >= a.Count) throw new FormulaException("#VALUE!");
                    return ScalarOf(EvalNode(a[idx]));
                }
            case "ISNUMBER": Need(1, 1); return SafeIs(a[0], v => v is double);
            case "ISTEXT": Need(1, 1); return SafeIs(a[0], v => v is string);
            case "ISBLANK": Need(1, 1); return SafeIs(a[0], v => v == null);
            case "ISERROR":
                {
                    Need(1, 1);
                    try { ScalarOf(EvalNode(a[0])); return false; } catch (FormulaException) { return true; }
                }

            // ---- lookup
            case "VLOOKUP":
            case "HLOOKUP":
                {
                    Need(3, 4);
                    bool v = f.Name == "VLOOKUP";
                    var key = Arg(0);
                    var rr = AsRange(a[1]);
                    int idx = (int)Num(2);
                    bool approx = a.Count < 4 || ToBool(Arg(3));
                    int lines = v ? rr.Rows : rr.Cols, depth = v ? rr.Cols : rr.Rows;
                    if (idx < 1 || idx > depth) throw new FormulaException("#REF!");
                    int found = -1;
                    for (int i = 0; i < lines; i++)
                    {
                        var cell = v ? RangeGet(rr, i, 0) : RangeGet(rr, 0, i);
                        int cmp = Cmp(cell, key);
                        if (!approx) { if (cmp == 0) { found = i; break; } }
                        else
                        {
                            if (cmp <= 0 && !(cell == null)) found = i;
                            if (cmp > 0) break;
                        }
                    }
                    if (found < 0) throw new FormulaException("#N/A");
                    return v ? RangeGet(rr, found, idx - 1) : RangeGet(rr, idx - 1, found);
                }
            case "INDEX":
                {
                    Need(2, 3);
                    var rr = AsRange(a[0]);
                    int r = (int)Num(1);
                    int c = a.Count > 2 ? (int)Num(2) : 1;
                    if (rr.Rows == 1 && a.Count == 2) { c = r; r = 1; }
                    if (r < 1 || c < 1 || r > rr.Rows || c > rr.Cols) throw new FormulaException("#REF!");
                    return RangeGet(rr, r - 1, c - 1);
                }
            case "MATCH":
                {
                    Need(2, 3);
                    var key = Arg(0);
                    var rr = AsRange(a[1]);
                    int type = a.Count > 2 ? (int)Num(2) : 1;
                    int n = Math.Max(rr.Rows, rr.Cols);
                    bool col = rr.Cols == 1;
                    int found = -1;
                    for (int i = 0; i < n; i++)
                    {
                        var cell = col ? RangeGet(rr, i, 0) : RangeGet(rr, 0, i);
                        int cmp = Cmp(cell, key);
                        if (type == 0) { if (cmp == 0) { found = i; break; } }
                        else if (type > 0) { if (cmp <= 0 && cell != null) found = i; else if (cmp > 0) break; }
                        else { if (cmp >= 0) found = i; else break; }
                    }
                    if (found < 0) throw new FormulaException("#N/A");
                    return (double)(found + 1);
                }
            case "ROW":
                if (a.Count == 0) return (double)(_curRow + 1);
                { var v = EvalNode(a[0]); if (v is RangeRef rr) return (double)(rr.R1 + 1); throw new FormulaException("#VALUE!"); }
            case "COLUMN":
                if (a.Count == 0) return (double)(_curCol + 1);
                { var v = EvalNode(a[0]); if (v is RangeRef rr) return (double)(rr.C1 + 1); throw new FormulaException("#VALUE!"); }
            case "ROWS": { Need(1, 1); return (double)AsRange(a[0]).Rows; }
            case "COLUMNS": { Need(1, 1); return (double)AsRange(a[0]).Cols; }

            // ---- text
            case "LEN": Need(1, 1); return (double)ToStr(Arg(0)).Length;
            case "UPPER": Need(1, 1); return ToStr(Arg(0)).ToUpperInvariant();
            case "LOWER": Need(1, 1); return ToStr(Arg(0)).ToLowerInvariant();
            case "PROPER":
                {
                    Need(1, 1);
                    var s = ToStr(Arg(0));
                    var sb = new StringBuilder();
                    bool up = true;
                    foreach (var ch in s)
                    {
                        sb.Append(up ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
                        up = !char.IsLetter(ch);
                    }
                    return sb.ToString();
                }
            case "TRIM": Need(1, 1); return Regex.Replace(ToStr(Arg(0)).Trim(), @"\s+", " ");
            case "CONCAT":
            case "CONCATENATE":
                {
                    var sb = new StringBuilder();
                    foreach (var arg in a) foreach (var v in Values(arg)) sb.Append(ToStr(v));
                    return sb.ToString();
                }
            case "LEFT": { Need(1, 2); var s = ToStr(Arg(0)); int n = a.Count > 1 ? (int)Num(1) : 1; if (n < 0) throw new FormulaException("#VALUE!"); return s.Substring(0, Math.Min(n, s.Length)); }
            case "RIGHT": { Need(1, 2); var s = ToStr(Arg(0)); int n = a.Count > 1 ? (int)Num(1) : 1; if (n < 0) throw new FormulaException("#VALUE!"); n = Math.Min(n, s.Length); return s.Substring(s.Length - n); }
            case "MID":
                {
                    Need(3, 3);
                    var s = ToStr(Arg(0));
                    int start = (int)Num(1), n = (int)Num(2);
                    if (start < 1 || n < 0) throw new FormulaException("#VALUE!");
                    if (start > s.Length) return "";
                    return s.Substring(start - 1, Math.Min(n, s.Length - start + 1));
                }
            case "FIND":
            case "SEARCH":
                {
                    Need(2, 3);
                    var what = ToStr(Arg(0)); var within = ToStr(Arg(1));
                    int start = a.Count > 2 ? (int)Num(2) - 1 : 0;
                    if (start < 0 || start > within.Length) throw new FormulaException("#VALUE!");
                    int i = within.IndexOf(what, start, f.Name == "FIND" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    if (i < 0) throw new FormulaException("#VALUE!");
                    return (double)(i + 1);
                }
            case "SUBSTITUTE":
                {
                    Need(3, 3);
                    var s = ToStr(Arg(0)); var oldT = ToStr(Arg(1)); var newT = ToStr(Arg(2));
                    return oldT.Length == 0 ? s : s.Replace(oldT, newT);
                }
            case "REPT": { Need(2, 2); int n = (int)Num(1); if (n < 0 || n > 10000) throw new FormulaException("#VALUE!"); return new StringBuilder().Insert(0, ToStr(Arg(0)), n).ToString(); }
            case "EXACT": Need(2, 2); return ToStr(Arg(0)) == ToStr(Arg(1));
            case "VALUE": Need(1, 1); return ToNum(Arg(0));
            case "TEXT": Need(2, 2); return TextFmt(Num(0), ToStr(Arg(1)));

            // ---- dates
            case "TODAY": return Math.Floor(DateTime.Now.ToOADate());
            case "NOW": return DateTime.Now.ToOADate();
            case "DATE":
                {
                    Need(3, 3);
                    int y = (int)Num(0), m = (int)Num(1), d = (int)Num(2);
                    try { return new DateTime(y, 1, 1).AddMonths(m - 1).AddDays(d - 1).ToOADate(); }
                    catch (ArgumentOutOfRangeException) { throw new FormulaException("#NUM!"); }
                }
            case "YEAR": Need(1, 1); return (double)DateFromSerial(Num(0)).Year;
            case "MONTH": Need(1, 1); return (double)DateFromSerial(Num(0)).Month;
            case "DAY": Need(1, 1); return (double)DateFromSerial(Num(0)).Day;
            case "WEEKDAY": Need(1, 2); return (double)((int)DateFromSerial(Num(0)).DayOfWeek + 1);
        }
        throw new FormulaException("#NAME?");
    }

    private bool SafeIs(Node n, Func<object?, bool> test)
    {
        try { return test(ScalarOf(EvalNode(n))); }
        catch (FormulaException) { return false; }
    }

    private static DateTime DateFromSerial(double v)
    {
        try { return DateTime.FromOADate(v); }
        catch (ArgumentException) { throw new FormulaException("#NUM!"); }
    }

    /// <summary>Subset of Excel's TEXT(): 0, 0.00, #,##0.00, $#,##0, 0%, yyyy-mm-dd ...</summary>
    public static string TextFmt(double v, string f)
    {
        if (f.Length == 0) return Format(v);
        bool hasDigitPlaceholder = f.IndexOf('0') >= 0 || f.IndexOf('#') >= 0;
        if (!hasDigitPlaceholder && Regex.IsMatch(f, "[ymdhs]", RegexOptions.IgnoreCase))
        {
            var dt = DateFromSerial(v);
            var net = f.Replace("mm", f.IndexOf(':') >= 0 ? "mm" : "MM").Replace("AM/PM", "tt");
            try { return dt.ToString(net, Inv); } catch (FormatException) { return Format(v); }
        }
        bool pct = f.IndexOf('%') >= 0, thousands = f.IndexOf(',') >= 0;
        int dot = f.IndexOf('.');
        int dec = 0;
        if (dot >= 0) for (int i = dot + 1; i < f.Length && (f[i] == '0' || f[i] == '#'); i++) dec++;
        int p = 0;
        while (p < f.Length && f[p] != '0' && f[p] != '#' && f[p] != ',') p++;
        var prefix = f.Substring(0, p).Replace("\"", "");
        var num = (pct ? v * 100 : v).ToString((thousands ? "#,##0" : "0") + (dec > 0 ? "." + new string('0', dec) : ""), Inv);
        return prefix + num + (pct ? "%" : "");
    }
}
