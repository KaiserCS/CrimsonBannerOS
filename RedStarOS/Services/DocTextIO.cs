using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RedStarOS.Services;

/// <summary>HTML, Markdown and plain-text import/export for the native Writer.</summary>
public static class DocTextIO
{
    // ═════════════════════════ plain text ═════════════════════════
    public static RichDoc FromText(string text)
    {
        var d = new RichDoc();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            d.Blocks.Add(Block.Of(Para.FromText(line.Replace("\t", "    "))));
        d.EnsureValid();
        return d;
    }

    // ═════════════════════════ HTML export ═════════════════════════
    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string RunHtml(Run r, Para p)
    {
        if (r.Text.Length == 0) return "";
        var css = new StringBuilder();
        if (!string.IsNullOrEmpty(r.Font)) css.Append($"font-family:'{r.Font}';");
        if (r.Size > 0) css.Append($"font-size:{r.Size.ToString("0.#", CultureInfo.InvariantCulture)}pt;");
        if (r.Color != null) css.Append($"color:{r.Color};");
        if (r.Highlight != null) css.Append($"background-color:{r.Highlight};");
        var h = E(r.Text);
        if (css.Length > 0) h = $"<span style=\"{css}\">{h}</span>";
        if (r.Bold && !RichDoc.DefaultBold(p.Style)) h = $"<strong>{h}</strong>";
        if (r.Italic) h = $"<em>{h}</em>";
        if (r.Underline) h = $"<u>{h}</u>";
        if (r.Strike) h = $"<s>{h}</s>";
        if (r.Sup) h = $"<sup>{h}</sup>";
        if (r.Sub) h = $"<sub>{h}</sub>";
        if (r.Link != null) h = $"<a href=\"{E(r.Link)}\">{h}</a>";
        return h;
    }

    private static string Inner(Para p) => string.Concat(p.Runs.Select(r => RunHtml(r, p)));

    private static string AlignCss(Para p) =>
        (p.Align == Align.Left ? "" : $"text-align:{(p.Align == Align.Justify ? "justify" : p.Align.ToString().ToLowerInvariant())};") +
        (Math.Abs(p.Spacing - 1) > 0.01 ? $"line-height:{(p.Spacing * 1.3).ToString("0.##", CultureInfo.InvariantCulture)};" : "");

    public static string ToHtml(RichDoc d, string title)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>").Append(E(title)).Append("</title>\n");
        sb.Append("<style>body{font-family:'Noto Sans','DejaVu Sans',Arial,sans-serif;font-size:11pt;max-width:800px;margin:2em auto;padding:0 1em;line-height:1.4}" +
                  "blockquote{border-left:3px solid #b0b7c3;margin-left:0;padding-left:1em;color:#555;font-style:italic}pre{background:#f1f3f4;padding:.4em .6em;font-size:10pt;margin:0}" +
                  "table{border-collapse:collapse}td{border:1px solid #8a8a8a;padding:4px 8px}</style></head><body>\n");
        var stack = new Stack<(string tag, int level)>();   // open lists
        void CloseLists(int toLevel)
        {
            while (stack.Count > 0 && stack.Peek().level > toLevel) sb.Append($"</li></{stack.Pop().tag}>");
        }
        foreach (var b in d.Blocks)
        {
            if (b.Kind != BlockKind.Para || b.Para!.List == ListKind.None) { CloseLists(-1); }
            switch (b.Kind)
            {
                case BlockKind.Para:
                {
                    var p = b.Para!;
                    if (p.List != ListKind.None)
                    {
                        var tag = p.List == ListKind.Bullet ? "ul" : "ol";
                        if (stack.Count > 0 && stack.Peek().level == p.Level && stack.Peek().tag != tag) CloseLists(p.Level - 1);
                        if (stack.Count > 0 && stack.Peek().level == p.Level) sb.Append("</li>");
                        else if (stack.Count > 0 && stack.Peek().level > p.Level) { CloseLists(p.Level); if (stack.Count > 0 && stack.Peek().level == p.Level) sb.Append("</li>"); }
                        if (stack.Count == 0 || stack.Peek().level < p.Level) { stack.Push((tag, p.Level)); sb.Append($"<{tag}>"); }
                        sb.Append($"<li style=\"{AlignCss(p)}\">").Append(Inner(p));
                        break;
                    }
                    var inner = Inner(p);
                    var css = AlignCss(p);
                    var cs = css.Length > 0 ? $" style=\"{css}\"" : "";
                    switch (p.Style)
                    {
                        case PStyle.H1: sb.Append($"<h1{cs}>{inner}</h1>\n"); break;
                        case PStyle.H2: sb.Append($"<h2{cs}>{inner}</h2>\n"); break;
                        case PStyle.H3: sb.Append($"<h3{cs}>{inner}</h3>\n"); break;
                        case PStyle.Quote: sb.Append($"<blockquote{cs}>{inner}</blockquote>\n"); break;
                        case PStyle.Code: sb.Append($"<pre>{inner}</pre>\n"); break;
                        default: sb.Append(p.Length == 0 ? "<p>&nbsp;</p>\n" : $"<p{cs}>{inner}</p>\n"); break;
                    }
                    break;
                }
                case BlockKind.Table:
                    sb.Append("<table>");
                    foreach (var row in b.Rows!)
                    {
                        sb.Append("<tr>");
                        foreach (var c in row) sb.Append("<td>").Append(Inner(c)).Append("</td>");
                        sb.Append("</tr>");
                    }
                    sb.Append("</table>\n");
                    break;
                case BlockKind.Image:
                    if (b.Image != null)
                    {
                        var mime = b.Image.Length > 1 && b.Image[0] == 0x89 ? "image/png" : "image/jpeg";
                        sb.Append($"<p style=\"text-align:{(b.ImgAlign == Align.Center ? "center" : b.ImgAlign == Align.Right ? "right" : "left")}\"><img src=\"data:{mime};base64,{Convert.ToBase64String(b.Image)}\" width=\"{(int)b.ImgW}\" height=\"{(int)b.ImgH}\"></p>\n");
                    }
                    break;
                case BlockKind.Rule: sb.Append("<hr>\n"); break;
                case BlockKind.PageBreak: sb.Append("<div style=\"page-break-after:always\"></div>\n"); break;
            }
        }
        CloseLists(-1);
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    // ═════════════════════════ HTML import ═════════════════════════
    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000", ["white"] = "#FFFFFF", ["red"] = "#FF0000", ["green"] = "#008000", ["blue"] = "#0000FF",
        ["yellow"] = "#FFFF00", ["orange"] = "#FFA500", ["purple"] = "#800080", ["gray"] = "#808080", ["grey"] = "#808080",
        ["pink"] = "#FFC0CB", ["brown"] = "#A52A2A", ["cyan"] = "#00FFFF", ["magenta"] = "#FF00FF", ["navy"] = "#000080", ["teal"] = "#008080"
    };

    public static string? ParseColor(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        if (v.StartsWith("#"))
        {
            if (v.Length == 4) return "#" + string.Concat(v.Skip(1).Select(c => $"{c}{c}")).ToUpperInvariant();
            if (v.Length == 7) return v.ToUpperInvariant();
            return null;
        }
        var m = Regex.Match(v, @"rgba?\(\s*(\d+)[\s,]+(\d+)[\s,]+(\d+)");
        if (m.Success) return $"#{int.Parse(m.Groups[1].Value):X2}{int.Parse(m.Groups[2].Value):X2}{int.Parse(m.Groups[3].Value):X2}";
        return NamedColors.TryGetValue(v, out var c) ? c : null;
    }

    private static Dictionary<string, string> ParseCss(string? style)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(style)) return d;
        foreach (var part in style.Split(';'))
        {
            var i = part.IndexOf(':');
            if (i > 0) d[part[..i].Trim()] = part[(i + 1)..].Trim();
        }
        return d;
    }

    private sealed class Tok { public string? Tag; public bool Close, Self; public Dictionary<string, string> Attr = new(StringComparer.OrdinalIgnoreCase); public string? Text; }

    private static List<Tok> Tokenize(string html)
    {
        var toks = new List<Tok>();
        int i = 0;
        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
                {
                    int e = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = e < 0 ? html.Length : e + 3; continue;
                }
                int end = html.IndexOf('>', i);
                if (end < 0) { toks.Add(new Tok { Text = html[i..] }); break; }
                var inner = html[(i + 1)..end];
                i = end + 1;
                if (inner.StartsWith("!") || inner.StartsWith("?")) continue;
                var t = new Tok();
                if (inner.StartsWith("/")) { t.Close = true; inner = inner[1..]; }
                if (inner.EndsWith("/")) { t.Self = true; inner = inner[..^1]; }
                var m = Regex.Match(inner, @"^\s*([A-Za-z][A-Za-z0-9]*)");
                if (!m.Success) continue;
                t.Tag = m.Groups[1].Value.ToLowerInvariant();
                foreach (Match am in Regex.Matches(inner[m.Length..], @"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+)))?"))
                    t.Attr[am.Groups[1].Value] = WebUtility.HtmlDecode(am.Groups[2].Success ? am.Groups[2].Value : am.Groups[3].Success ? am.Groups[3].Value : am.Groups[4].Value);
                toks.Add(t);
                if (!t.Close && (t.Tag == "script" || t.Tag == "style"))
                {
                    int ce = html.IndexOf("</" + t.Tag, i, StringComparison.OrdinalIgnoreCase);
                    i = ce < 0 ? html.Length : ce;
                }
            }
            else
            {
                int next = html.IndexOf('<', i);
                if (next < 0) next = html.Length;
                toks.Add(new Tok { Text = html[i..next] });
                i = next;
            }
        }
        return toks;
    }

    public static RichDoc FromHtml(string html, string? baseDir = null)
    {
        var toks = Tokenize(html);
        var doc = new RichDoc();
        var blocks = doc.Blocks;

        var fmt = new Stack<Run>(); fmt.Push(new Run());
        var lists = new Stack<ListKind>();
        Para? cur = null;
        PStyle blockStyle = PStyle.Normal; Align blockAlign = Align.Left; int quoteDepth = 0; bool inPre = false;
        bool skip = false;            // inside head/title/script/style
        Block? table = null; List<Para>? row = null; Para? cell = null;
        var tagFmtDepth = new Stack<(string tag, bool pushed)>();

        void Flush()
        {
            if (cell != null) return;
            if (cur != null) { cur.Normalize(); blocks.Add(Block.Of(cur)); }
            cur = null;
        }
        Para NewPara(ListKind lk = ListKind.None)
        {
            if (cell != null) return cell;
            var p = new Para { Style = quoteDepth > 0 && blockStyle == PStyle.Normal ? PStyle.Quote : blockStyle, Align = blockAlign };
            if (lk != ListKind.None) { p.List = lk; p.Level = Math.Max(0, lists.Count - 1); }
            return p;
        }
        ListKind pendingList = ListKind.None;
        Para Cur()
        {
            if (cell != null) return cell;
            if (cur == null) { cur = NewPara(pendingList); pendingList = ListKind.None; }
            return cur;
        }
        Align AlignOf(Tok t)
        {
            var css = ParseCss(t.Attr.GetValueOrDefault("style"));
            var a = css.GetValueOrDefault("text-align") ?? t.Attr.GetValueOrDefault("align");
            return a?.ToLowerInvariant() switch { "center" => Align.Center, "right" => Align.Right, "justify" => Align.Justify, _ => Align.Left };
        }
        Run Derive(Tok t)
        {
            var r = fmt.Peek().Clone();
            switch (t.Tag)
            {
                case "b": case "strong": r.Bold = true; break;
                case "i": case "em": case "cite": r.Italic = true; break;
                case "u": case "ins": r.Underline = true; break;
                case "s": case "strike": case "del": r.Strike = true; break;
                case "sup": r.Sup = true; r.Sub = false; break;
                case "sub": r.Sub = true; r.Sup = false; break;
                case "code": case "tt": case "kbd": r.Font ??= "DejaVu Sans Mono"; break;
                case "a": if (t.Attr.TryGetValue("href", out var h)) { r.Link = h; } break;
                case "font":
                    if (t.Attr.TryGetValue("color", out var fc)) r.Color = ParseColor(fc) ?? r.Color;
                    if (t.Attr.TryGetValue("face", out var ff)) r.Font = ff.Split(',')[0].Trim().Trim('\'', '"');
                    break;
            }
            var css = ParseCss(t.Attr.GetValueOrDefault("style"));
            if (css.TryGetValue("color", out var col)) r.Color = ParseColor(col) ?? r.Color;
            if (css.TryGetValue("background-color", out var bg) || css.TryGetValue("background", out bg)) r.Highlight = ParseColor(bg) ?? r.Highlight;
            if (css.TryGetValue("font-family", out var fam)) r.Font = fam.Split(',')[0].Trim().Trim('\'', '"');
            if (css.TryGetValue("font-size", out var fs))
            {
                var m = Regex.Match(fs, @"([\d.]+)\s*(px|pt)");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    r.Size = m.Groups[2].Value == "px" ? Math.Round(n * 0.75 * 2) / 2 : n;
            }
            if (css.TryGetValue("font-weight", out var fw) && (fw == "bold" || (int.TryParse(fw, out var w) && w >= 600))) r.Bold = true;
            if (css.TryGetValue("font-style", out var fst) && fst == "italic") r.Italic = true;
            if (css.TryGetValue("text-decoration", out var td)) { if (td.Contains("underline")) r.Underline = true; if (td.Contains("line-through")) r.Strike = true; }
            return r;
        }

        foreach (var t in toks)
        {
            if (t.Text != null)
            {
                if (skip) continue;
                var txt = WebUtility.HtmlDecode(t.Text);
                if (inPre)
                {
                    var lines = txt.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0) { Flush(); pendingList = ListKind.None; }
                        if (lines[i].Length > 0) { var c = Cur(); c.Runs.Add(fmt.Peek().CloneWith(lines[i].Replace("\t", "    "))); }
                    }
                    continue;
                }
                bool hadNbsp = txt.Contains('\u00A0');
                txt = Regex.Replace(txt.Replace('\u00A0', ' '), @"\s+", " ");
                if (txt.Trim().Length == 0 && cur == null && cell == null) { if (hadNbsp) Cur(); continue; }
                if (cur == null && cell == null) txt = txt.TrimStart();
                if (txt.Length == 0) continue;
                var p = Cur();
                if (p.Length == 0) txt = txt.TrimStart();
                if (txt.Length > 0) p.Runs.Add(fmt.Peek().CloneWith(txt));
                continue;
            }

            var tag = t.Tag!;
            if (tag == "head" || tag == "title" || tag == "script" || tag == "style") { skip = !t.Close; continue; }
            if (tag == "body" || tag == "html") { if (!t.Close) skip = false; continue; }
            if (skip) continue;

            bool isInline = tag is "b" or "strong" or "i" or "em" or "cite" or "u" or "ins" or "s" or "strike" or "del" or "sup" or "sub" or "code" or "tt" or "kbd" or "a" or "font" or "span" or "mark" or "small" or "big" or "label";
            if (isInline)
            {
                if (t.Close) { if (fmt.Count > 1) fmt.Pop(); }
                else if (!t.Self)
                {
                    var r = Derive(t);
                    if (tag == "mark") r.Highlight ??= "#FFFF00";
                    fmt.Push(r);
                }
                continue;
            }

            switch (tag)
            {
                case "br":
                    if (cell != null) cell.Runs.Add(fmt.Peek().CloneWith(" "));
                    else { Flush(); }
                    break;
                case "hr": Flush(); if (cell == null) blocks.Add(new Block { Kind = BlockKind.Rule }); break;
                case "img":
                {
                    if (!t.Attr.TryGetValue("src", out var src)) break;
                    byte[]? data = null;
                    var m = Regex.Match(src, @"^data:image/[a-z+]+;base64,(.+)$", RegexOptions.Singleline);
                    if (m.Success) { try { data = Convert.FromBase64String(m.Groups[1].Value); } catch { } }
                    else if (baseDir != null && !src.Contains("://"))
                    {
                        var fp = Path.Combine(baseDir, WebUtility.UrlDecode(src));
                        if (File.Exists(fp)) data = File.ReadAllBytes(fp);
                    }
                    if (data == null) break;
                    double.TryParse(t.Attr.GetValueOrDefault("width"), out var w); double.TryParse(t.Attr.GetValueOrDefault("height"), out var h);
                    Flush();
                    if (cell == null) blocks.Add(new Block { Kind = BlockKind.Image, Image = data, ImgW = w, ImgH = h, ImgAlign = blockAlign });
                    break;
                }
                case "table":
                    if (t.Close) { if (table != null && table.Rows!.Count > 0) { int cols = table.Rows.Max(r => r.Count); foreach (var r in table.Rows) while (r.Count < cols) r.Add(new Para()); blocks.Add(table); } table = null; row = null; cell = null; }
                    else { Flush(); table = new Block { Kind = BlockKind.Table, Rows = new() }; }
                    break;
                case "tr":
                    if (table == null) break;
                    if (t.Close) { if (row != null && row.Count > 0) table.Rows!.Add(row); row = null; cell = null; }
                    else { row = new List<Para>(); cell = null; }
                    break;
                case "td": case "th":
                    if (table == null) break;
                    if (t.Close) { cell?.Normalize(); cell = null; }
                    else
                    {
                        row ??= new List<Para>();
                        cell = new Para(); row.Add(cell);
                        if (tag == "th") fmt.Push(new Run { Bold = true }); else fmt.Push(fmt.Peek().Clone());
                    }
                    if (t.Close && fmt.Count > 1) fmt.Pop();
                    break;
                case "ul": case "ol":
                    Flush();
                    if (t.Close) { if (lists.Count > 0) lists.Pop(); }
                    else lists.Push(tag == "ul" ? ListKind.Bullet : ListKind.Number);
                    pendingList = ListKind.None;
                    break;
                case "li":
                    Flush();
                    if (t.Close) { pendingList = ListKind.None; blockAlign = Align.Left; }
                    else { blockStyle = PStyle.Normal; blockAlign = AlignOf(t); pendingList = lists.Count > 0 ? lists.Peek() : ListKind.Bullet; if (lists.Count == 0) lists.Push(ListKind.Bullet); }
                    break;
                case "blockquote":
                    Flush();
                    quoteDepth += t.Close ? -1 : 1; if (quoteDepth < 0) quoteDepth = 0;
                    break;
                case "pre":
                    Flush();
                    inPre = !t.Close;
                    blockStyle = t.Close ? PStyle.Normal : PStyle.Code;
                    break;
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    Flush();
                    if (t.Close) { blockStyle = PStyle.Normal; blockAlign = Align.Left; }
                    else { blockStyle = tag == "h1" ? PStyle.H1 : tag == "h2" ? PStyle.H2 : PStyle.H3; blockAlign = AlignOf(t); }
                    break;
                case "p": case "div": case "section": case "article": case "header": case "footer": case "main": case "nav": case "aside": case "figure": case "figcaption": case "dd": case "dt":
                    Flush();
                    if (t.Close) { blockAlign = Align.Left; }
                    else
                    {
                        blockAlign = AlignOf(t);
                        var css = ParseCss(t.Attr.GetValueOrDefault("style"));
                        if (css.TryGetValue("page-break-after", out var pb) && pb == "always" && cell == null) blocks.Add(new Block { Kind = BlockKind.PageBreak });
                    }
                    break;
            }
        }
        Flush();
        // an empty <p>&nbsp;</p> leaves a paragraph with a single space: trim to empty
        foreach (var b in blocks) if (b.Kind == BlockKind.Para && b.Para!.Text.Trim().Length == 0 && b.Para.Text.Length > 0) b.Para.Runs.Clear();
        doc.EnsureValid();
        return doc;
    }

    // ═════════════════════════ Markdown export ═════════════════════════
    private static string MdInline(Para p)
    {
        var sb = new StringBuilder();
        foreach (var r in p.Runs)
        {
            if (r.Text.Length == 0) continue;
            var t = r.Text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("[", "\\[");
            var lead = t.Length - t.TrimStart().Length; var trail = t.Length - t.TrimEnd().Length;
            var core = t.Trim();
            if (core.Length == 0) { sb.Append(t); continue; }
            if (r.Strike) core = $"~~{core}~~";
            if (r.Italic && r.Bold) core = $"***{core}***"; else if (r.Bold && !RichDoc.DefaultBold(p.Style)) core = $"**{core}**"; else if (r.Italic && p.Style != PStyle.Quote) core = $"*{core}*";
            if (r.Underline && r.Link == null) core = $"<u>{core}</u>";
            if (r.Link != null) core = $"[{core}]({r.Link})";
            sb.Append(new string(' ', lead)).Append(core).Append(new string(' ', trail));
        }
        return sb.ToString();
    }

    public static string ToMarkdown(RichDoc d)
    {
        var sb = new StringBuilder();
        bool inCode = false, lastList = false;
        void CloseCode() { if (inCode) { sb.Append("```\n\n"); inCode = false; } }
        foreach (var b in d.Blocks)
        {
            if (b.Kind == BlockKind.Para && b.Para!.Style == PStyle.Code && b.Para.List == ListKind.None)
            {
                if (!inCode) { if (lastList) sb.Append('\n'); sb.Append("```\n"); inCode = true; }
                sb.Append(b.Para.Text).Append('\n');
                lastList = false;
                continue;
            }
            CloseCode();
            switch (b.Kind)
            {
                case BlockKind.Para:
                {
                    var p = b.Para!;
                    var t = MdInline(p);
                    if (p.List != ListKind.None)
                    {
                        sb.Append(new string(' ', p.Level * 2)).Append(p.List == ListKind.Bullet ? "- " : "1. ").Append(t).Append('\n');
                        lastList = true; break;
                    }
                    if (lastList) sb.Append('\n');
                    lastList = false;
                    var prefix = p.Style switch { PStyle.H1 => "# ", PStyle.H2 => "## ", PStyle.H3 => "### ", PStyle.Quote => "> ", _ => "" };
                    if (t.Length == 0) { sb.Append('\n'); break; }
                    sb.Append(prefix).Append(t).Append("\n\n");
                    break;
                }
                case BlockKind.Table:
                {
                    if (lastList) sb.Append('\n'); lastList = false;
                    bool first = true;
                    foreach (var row in b.Rows!)
                    {
                        sb.Append("| ").Append(string.Join(" | ", row.Select(c => MdInline(c).Replace("|", "\\|")))).Append(" |\n");
                        if (first) { sb.Append("|").Append(string.Concat(row.Select(_ => " --- |"))).Append('\n'); first = false; }
                    }
                    sb.Append('\n');
                    break;
                }
                case BlockKind.Image:
                    if (lastList) sb.Append('\n'); lastList = false;
                    if (b.Image != null)
                    {
                        var mime = b.Image.Length > 1 && b.Image[0] == 0x89 ? "image/png" : "image/jpeg";
                        sb.Append($"![image](data:{mime};base64,{Convert.ToBase64String(b.Image)})\n\n");
                    }
                    break;
                case BlockKind.Rule: if (lastList) sb.Append('\n'); lastList = false; sb.Append("---\n\n"); break;
                case BlockKind.PageBreak: if (lastList) sb.Append('\n'); lastList = false; sb.Append("<!-- pagebreak -->\n\n"); break;
            }
        }
        CloseCode();
        return sb.ToString().TrimEnd() + "\n";
    }

    // ═════════════════════════ Markdown import ═════════════════════════
    public static void AddInline(Para p, string s, Run? baseFmt = null)
    {
        var f = baseFmt?.Clone() ?? new Run();
        var buf = new StringBuilder();
        bool bold = false, ital = false, strike = false, under = false;
        string? link = null;
        void Emit()
        {
            if (buf.Length == 0) return;
            var r = new Run { Text = buf.ToString(), Bold = f.Bold || bold, Italic = f.Italic || ital, Strike = strike, Underline = under, Link = link };
            p.Runs.Add(r); buf.Clear();
        }
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) { buf.Append(s[++i]); continue; }
            if (c == '`')
            {
                int e = s.IndexOf('`', i + 1);
                if (e > i) { Emit(); p.Runs.Add(new Run { Text = s[(i + 1)..e], Font = "DejaVu Sans Mono", Highlight = "#F1F3F4" }); i = e; continue; }
            }
            if (c == '*' || c == '_')
            {
                int n = 1; while (i + n < s.Length && s[i + n] == c) n++;
                if (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1]) && i + n < s.Length && char.IsLetterOrDigit(s[i + n])) { buf.Append(c, n); i += n - 1; continue; }
                Emit();
                if (n >= 3) { bold = !bold; ital = !ital; }
                else if (n == 2) bold = !bold; else ital = !ital;
                i += n - 1; continue;
            }
            if (c == '~' && i + 1 < s.Length && s[i + 1] == '~') { Emit(); strike = !strike; i++; continue; }
            if (c == '<' && string.CompareOrdinal(s, i, "<u>", 0, 3) == 0) { Emit(); under = true; i += 2; continue; }
            if (c == '<' && string.CompareOrdinal(s, i, "</u>", 0, 4) == 0) { Emit(); under = false; i += 3; continue; }
            if (c == '[')
            {
                var m = Regex.Match(s[i..], @"^\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)");
                if (m.Success)
                {
                    Emit();
                    var inner = new Para(); AddInline(inner, m.Groups[1].Value, f);
                    foreach (var r in inner.Runs) { r.Link = m.Groups[2].Value; p.Runs.Add(r); }
                    i += m.Length - 1; continue;
                }
            }
            buf.Append(c);
        }
        Emit();
        p.Normalize();
    }

    public static RichDoc FromMarkdown(string md)
    {
        var doc = new RichDoc();
        var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var para = new List<string>();
        void FlushPara()
        {
            if (para.Count == 0) return;
            var p = new Para();
            AddInline(p, string.Join(" ", para.Select(l => l.Trim())));
            doc.Blocks.Add(Block.Of(p));
            para.Clear();
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                FlushPara();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    doc.Blocks.Add(Block.Of(Para.FromText(lines[i].Replace("\t", "    "), PStyle.Code)));
                    i++;
                }
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { FlushPara(); continue; }
            if (Regex.IsMatch(line, @"^\s{0,3}([-*_])(\s*\1){2,}\s*$")) { FlushPara(); doc.Blocks.Add(new Block { Kind = BlockKind.Rule }); continue; }
            if (line.Trim() == "<!-- pagebreak -->") { FlushPara(); doc.Blocks.Add(new Block { Kind = BlockKind.PageBreak }); continue; }
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*?)\s*#*\s*$");
            if (h.Success)
            {
                FlushPara();
                var p = new Para { Style = h.Groups[1].Length == 1 ? PStyle.H1 : h.Groups[1].Length == 2 ? PStyle.H2 : PStyle.H3 };
                AddInline(p, h.Groups[2].Value);
                doc.Blocks.Add(Block.Of(p)); continue;
            }
            if (line.StartsWith(">"))
            {
                FlushPara();
                var p = new Para { Style = PStyle.Quote };
                AddInline(p, line.TrimStart('>').Trim());
                doc.Blocks.Add(Block.Of(p)); continue;
            }
            var li = Regex.Match(line, @"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
            if (li.Success)
            {
                FlushPara();
                var p = new Para { List = char.IsDigit(li.Groups[2].Value[0]) ? ListKind.Number : ListKind.Bullet, Level = Math.Min(8, li.Groups[1].Length / 2) };
                AddInline(p, li.Groups[3].Value);
                doc.Blocks.Add(Block.Of(p)); continue;
            }
            var img = Regex.Match(line.Trim(), @"^!\[[^\]]*\]\((data:image/[a-z+]+;base64,[^)]+)\)$");
            if (img.Success)
            {
                FlushPara();
                try
                {
                    var data = Convert.FromBase64String(img.Groups[1].Value[(img.Groups[1].Value.IndexOf(',') + 1)..]);
                    doc.Blocks.Add(new Block { Kind = BlockKind.Image, Image = data });
                } catch { }
                continue;
            }
            if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*\|?\s*:?-{2,}"))
            {
                FlushPara();
                var tb = new Block { Kind = BlockKind.Table, Rows = new() };
                List<string> Cells(string l) => l.Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("\\|", "|")).ToList();
                tb.Rows.Add(Cells(line).Select(c => { var p = new Para(); AddInline(p, c); return p; }).ToList());
                i += 2;
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    tb.Rows.Add(Cells(lines[i]).Select(c => { var p = new Para(); AddInline(p, c); return p; }).ToList());
                    i++;
                }
                i--;
                int cols = tb.Rows.Max(r => r.Count);
                foreach (var r in tb.Rows) while (r.Count < cols) r.Add(new Para());
                doc.Blocks.Add(tb); continue;
            }
            para.Add(line);
        }
        FlushPara();
        doc.EnsureValid();
        return doc;
    }
}
