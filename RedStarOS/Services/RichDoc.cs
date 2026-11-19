using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedStarOS.Services;

public enum PStyle { Normal, H1, H2, H3, Quote, Code }
public enum ListKind { None, Bullet, Number }
public enum Align { Left, Center, Right, Justify }
public enum BlockKind { Para, Table, Image, Rule, PageBreak }

/// <summary>A stretch of text with uniform character formatting.</summary>
public sealed class Run
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strike { get; set; }
    public bool Sup { get; set; }
    public bool Sub { get; set; }
    public string? Font { get; set; }
    public double Size { get; set; }          // points; 0 = style default
    public string? Color { get; set; }        // #RRGGBB
    public string? Highlight { get; set; }
    public string? Link { get; set; }

    public Run Clone() => (Run)MemberwiseClone();

    public Run CloneWith(string text) { var r = Clone(); r.Text = text; return r; }

    public bool SameFormat(Run o) =>
        Bold == o.Bold && Italic == o.Italic && Underline == o.Underline && Strike == o.Strike &&
        Sup == o.Sup && Sub == o.Sub && Font == o.Font && Size == o.Size && Color == o.Color &&
        Highlight == o.Highlight && Link == o.Link;
}

public sealed class Para
{
    public PStyle Style { get; set; }
    public Align Align { get; set; }
    public ListKind List { get; set; }
    public int Level { get; set; }
    public double Spacing { get; set; } = 1.0;
    public List<Run> Runs { get; set; } = new();

    [JsonIgnore] public int Version { get; private set; }
    [JsonIgnore] public string Text => string.Concat(Runs.Select(r => r.Text));
    [JsonIgnore] public int Length { get { int n = 0; foreach (var r in Runs) n += r.Text.Length; return n; } }

    public void Touch() => Version++;

    public Para Clone()
    {
        var p = new Para { Style = Style, Align = Align, List = List, Level = Level, Spacing = Spacing };
        foreach (var r in Runs) p.Runs.Add(r.Clone());
        return p;
    }

    /// <summary>Copy of the paragraph settings (not the text).</summary>
    public Para Shell()
    {
        return new Para { Style = Style, Align = Align, List = List, Level = Level, Spacing = Spacing };
    }

    /// <summary>Make sure a run boundary exists at <paramref name="off"/>; returns the index of the run starting there.</summary>
    private int SplitRun(int off)
    {
        int pos = 0;
        for (int i = 0; i < Runs.Count; i++)
        {
            var r = Runs[i];
            if (off == pos) return i;
            if (off < pos + r.Text.Length)
            {
                int k = off - pos;
                var tail = r.CloneWith(r.Text.Substring(k));
                r.Text = r.Text.Substring(0, k);
                Runs.Insert(i + 1, tail);
                return i + 1;
            }
            pos += r.Text.Length;
        }
        return Runs.Count;
    }

    public void Insert(int off, string text, Run fmt)
    {
        if (text.Length == 0) return;
        off = Math.Clamp(off, 0, Length);
        int idx = SplitRun(off);
        // extend the previous run if the format matches
        if (idx > 0 && Runs[idx - 1].SameFormat(fmt)) Runs[idx - 1].Text += text;
        else Runs.Insert(idx, fmt.CloneWith(text));
        Normalize();
        Touch();
    }

    public void Delete(int start, int len)
    {
        if (len <= 0) return;
        int end = Math.Min(Length, start + len);
        start = Math.Max(0, start);
        int a = SplitRun(start);
        int b = SplitRun(end);
        Runs.RemoveRange(a, b - a);
        Normalize();
        Touch();
    }

    public void Format(int start, int len, Action<Run> f)
    {
        if (len <= 0) return;
        int end = Math.Min(Length, start + len);
        int a = SplitRun(start);
        int b = SplitRun(end);
        for (int i = a; i < b; i++) f(Runs[i]);
        Normalize();
        Touch();
    }

    /// <summary>Format that typing at <paramref name="off"/> should continue.</summary>
    public Run FormatAt(int off)
    {
        if (Runs.Count == 0) return new Run();
        int pos = 0;
        Run? prev = null;
        foreach (var r in Runs)
        {
            if (off <= pos + r.Text.Length && off > pos) { prev = r; break; }
            pos += r.Text.Length;
        }
        var src = prev ?? Runs[0];
        var c = src.CloneWith("");
        return c;
    }

    public Para SplitAt(int off)
    {
        off = Math.Clamp(off, 0, Length);
        int idx = SplitRun(off);
        var tail = Shell();
        tail.Runs.AddRange(Runs.Skip(idx));
        Runs.RemoveRange(idx, Runs.Count - idx);
        Touch();
        return tail;
    }

    public void Append(Para other)
    {
        Runs.AddRange(other.Runs.Select(r => r.Clone()));
        Normalize();
        Touch();
    }

    public void Normalize()
    {
        for (int i = Runs.Count - 1; i >= 0; i--)
            if (Runs[i].Text.Length == 0 && Runs.Count > 1) Runs.RemoveAt(i);
        if (Runs.Count == 1 && Runs[0].Text.Length == 0) { Runs.Clear(); return; }
        for (int i = Runs.Count - 1; i > 0; i--)
            if (Runs[i - 1].SameFormat(Runs[i])) { Runs[i - 1].Text += Runs[i].Text; Runs.RemoveAt(i); }
    }

    /// <summary>The run covering the character at <paramref name="i"/>.</summary>
    public Run? RunAtChar(int i)
    {
        int pos = 0;
        foreach (var r in Runs)
        {
            if (i < pos + r.Text.Length) return r;
            pos += r.Text.Length;
        }
        return null;
    }

    public static Para FromText(string text, PStyle style = PStyle.Normal)
    {
        var p = new Para { Style = style };
        if (text.Length > 0) p.Runs.Add(new Run { Text = text });
        return p;
    }
}

public sealed class Block
{
    public BlockKind Kind { get; set; }
    public Para? Para { get; set; }
    public List<List<Para>>? Rows { get; set; }         // table cells (one paragraph each)
    public byte[]? Image { get; set; }
    public double ImgW { get; set; }
    public double ImgH { get; set; }
    public Align ImgAlign { get; set; }

    public Block Clone()
    {
        var b = new Block { Kind = Kind, Image = Image, ImgW = ImgW, ImgH = ImgH, ImgAlign = ImgAlign };
        if (Para != null) b.Para = Para.Clone();
        if (Rows != null) b.Rows = Rows.Select(r => r.Select(c => c.Clone()).ToList()).ToList();
        return b;
    }

    public static Block Of(Para p) => new() { Kind = BlockKind.Para, Para = p };

    public static Block NewTable(int rows, int cols)
    {
        var b = new Block { Kind = BlockKind.Table, Rows = new() };
        for (int r = 0; r < rows; r++)
        {
            var row = new List<Para>();
            for (int c = 0; c < cols; c++) row.Add(new Para());
            b.Rows.Add(row);
        }
        return b;
    }
}

public sealed class RichDoc
{
    public List<Block> Blocks { get; set; } = new();

    public static RichDoc Empty()
    {
        var d = new RichDoc();
        d.Blocks.Add(Block.Of(new Para()));
        return d;
    }

    public IEnumerable<Para> AllParas()
    {
        foreach (var b in Blocks)
        {
            if (b.Kind == BlockKind.Para && b.Para != null) yield return b.Para;
            else if (b.Kind == BlockKind.Table && b.Rows != null)
                foreach (var row in b.Rows) foreach (var c in row) yield return c;
        }
    }

    public RichDoc Clone() => new() { Blocks = Blocks.Select(b => b.Clone()).ToList() };

    /// <summary>Guarantee at least one paragraph and a paragraph after a trailing table/image so the caret has somewhere to go.</summary>
    public void EnsureValid()
    {
        if (Blocks.Count == 0) Blocks.Add(Block.Of(new Para()));
        if (Blocks[^1].Kind != BlockKind.Para) Blocks.Add(Block.Of(new Para()));
    }

    public string PlainText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in Blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Para: sb.AppendLine(b.Para!.Text); break;
                case BlockKind.Table:
                    foreach (var row in b.Rows!) sb.AppendLine(string.Join("\t", row.Select(c => c.Text)));
                    break;
            }
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    public (int words, int chars) Counts()
    {
        int words = 0, chars = 0;
        foreach (var p in AllParas())
        {
            var t = p.Text;
            chars += t.Length;
            bool inWord = false;
            foreach (var ch in t)
            {
                if (char.IsWhiteSpace(ch)) inWord = false;
                else if (!inWord) { inWord = true; words++; }
            }
        }
        return (words, chars);
    }

    private static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    public string ToJson() => JsonSerializer.Serialize(this, Opts);
    public static RichDoc? FromJson(string json)
    {
        try { var d = JsonSerializer.Deserialize<RichDoc>(json, Opts); d?.EnsureValid(); return d; }
        catch { return null; }
    }

    // ── style metrics shared by the editor, HTML export and PDF ──
    public static double DefaultSize(PStyle s) => s switch { PStyle.H1 => 22, PStyle.H2 => 17, PStyle.H3 => 14, PStyle.Code => 10, _ => 11 };
    public static bool DefaultBold(PStyle s) => s is PStyle.H1 or PStyle.H2 or PStyle.H3;
    public static bool DefaultItalic(PStyle s) => s == PStyle.Quote;
    public static double SpaceAfter(PStyle s) => s switch { PStyle.H1 => 10, PStyle.H2 => 8, PStyle.H3 => 6, _ => 6 };   // points
    public static double SpaceBefore(PStyle s) => s switch { PStyle.H1 => 12, PStyle.H2 => 10, PStyle.H3 => 8, _ => 0 };
}
