// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PeoplesArchive.Model;

/// <summary>An EPUB chapter (XHTML) as blocks: headings, paragraphs, quotes, lists, pictures, with italic, bold and
/// monospaced runs. Styling the book itself brings (CSS) is left out: the reading room sets the type.</summary>
public static class Xhtml
{
    private readonly record struct Style(bool Italic, bool Bold, bool Mono, bool Small, string? Link, bool Pre, int Quote, int List);

    public static List<Block> ToBlocks(string xhtml, string dir)
    {
        var doc = Book.Xml(xhtml);
        var body = doc.SelectSingleNode("//*[local-name()='body']") ?? doc.DocumentElement;
        var w = new Walker(dir);
        if (body != null) w.Walk(body, default);
        w.Flush();
        return w.Blocks;
    }

    /// <summary>HTML's named characters (&amp;nbsp;, &amp;mdash;…) as numbers, which XML reads without HTML's list.</summary>
    public static string Entities(string text) => Regex.Replace(text, @"&([A-Za-z][A-Za-z0-9]*);", m =>
    {
        var name = m.Groups[1].Value;
        if (name is "amp" or "lt" or "gt" or "quot" or "apos") return m.Value;
        var s = WebUtility.HtmlDecode(m.Value);
        if (s == m.Value || s.Length == 0) return "";
        return string.Concat(Enumerable.Range(0, s.Length).Where(i => !char.IsLowSurrogate(s[i])).Select(i => $"&#{char.ConvertToUtf32(s, i)};"));
    });

    private sealed class Walker
    {
        private readonly string _dir;
        private Block? _cur;
        private string? _anchor;
        private readonly int[] _counters = new int[16];
        public List<Block> Blocks { get; } = new();

        public Walker(string dir) { _dir = dir; }

        public void Flush()
        {
            if (_cur == null) return;
            var b = _cur; _cur = null;
            // no space at the edges of a block
            while (b.Runs.Count > 0 && b.Runs[^1].Text.TrimEnd().Length == 0) b.Runs.RemoveAt(b.Runs.Count - 1);
            if (b.Runs.Count > 0 && b.Kind != BlockKind.Preformatted) b.Runs[^1] = b.Runs[^1] with { Text = b.Runs[^1].Text.TrimEnd() };
            if (b.Text.Trim().Length == 0) return;
            Blocks.Add(b);
        }

        private void Start(BlockKind kind, int level, Style s)
        {
            Flush();
            if (kind == BlockKind.Paragraph && s.Quote > 0) { kind = BlockKind.Quote; level = s.Quote; }
            _cur = new Block { Kind = kind, Level = level, Anchor = _anchor };
            _anchor = null;
        }

        private void Text(string t, Style s)
        {
            // HTML's own whitespace only: a no-break space (&nbsp;) stays
            if (!s.Pre) t = Regex.Replace(t, @"[ \t\r\n\f]+", " ");
            if (_cur == null)
            {
                if (t.Trim().Length == 0) return;
                Start(BlockKind.Paragraph, 0, s);
            }
            if (!s.Pre && (_cur!.Runs.Count == 0 || _cur.Runs[^1].Text.EndsWith(' ') || _cur.Runs[^1].Text.EndsWith('\n'))) t = t.TrimStart(' ', '\t', '\r', '\n', '\f');
            if (t.Length == 0) return;
            var run = new Run(t, s.Italic, s.Bold, s.Mono || s.Pre, s.Small, s.Link);
            if (_cur!.Runs.Count > 0 && _cur.Runs[^1] is var last && last with { Text = "" } == run with { Text = "" })
                _cur.Runs[^1] = last with { Text = last.Text + t };
            else _cur.Runs.Add(run);
        }

        public void Walk(XmlNode node, Style s)
        {
            foreach (XmlNode n in node.ChildNodes)
            {
                if (n.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                {
                    Text(n.Value ?? "", s);
                    continue;
                }
                if (n.NodeType != XmlNodeType.Element) continue;
                var name = n.LocalName.ToLowerInvariant();
                if (n.Attributes?["id"]?.Value is { Length: > 0 } id && _cur == null) _anchor = id;
                switch (name)
                {
                    case "head": case "script": case "style": case "title": case "noscript": case "template":
                        break;
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        Start(BlockKind.Heading, name[1] - '0', s);
                        Walk(n, s with { Bold = true });
                        Flush();
                        break;
                    case "p":
                        Start(BlockKind.Paragraph, 0, s);
                        Walk(n, s);
                        Flush();
                        break;
                    case "blockquote":
                        Flush();
                        Walk(n, s with { Quote = s.Quote + 1 });
                        Flush();
                        break;
                    case "ul": case "ol":
                        Flush();
                        if (s.List + 1 < _counters.Length) _counters[s.List + 1] = name == "ol" ? 1 : 0;
                        Walk(n, s with { List = s.List + 1 });
                        Flush();
                        break;
                    case "li":
                    {
                        Start(BlockKind.ListItem, Math.Max(1, s.List), s);
                        int depth = Math.Min(Math.Max(1, s.List), _counters.Length - 1);
                        Text(_counters[depth] > 0 ? $"{_counters[depth]++}. " : "• ", s with { Italic = false, Bold = false });
                        Walk(n, s);
                        Flush();
                        break;
                    }
                    case "pre":
                        Start(BlockKind.Preformatted, 0, s);
                        Walk(n, s with { Pre = true, Mono = true });
                        Flush();
                        break;
                    case "hr":
                        Flush();
                        Blocks.Add(new Block { Kind = BlockKind.Rule });
                        break;
                    case "br":
                        if (_cur != null) _cur.Runs.Add(new Run("\n", Mono: s.Pre));
                        break;
                    case "img": case "image":
                    {
                        var src = n.Attributes?["src"]?.Value ?? n.Attributes?["href", "http://www.w3.org/1999/xlink"]?.Value ?? n.Attributes?["xlink:href"]?.Value;
                        if (src is { Length: > 0 } && !src.StartsWith("data:", StringComparison.Ordinal))
                        {
                            Flush();
                            Blocks.Add(new Block { Kind = BlockKind.Image, Image = Book.Join(_dir, src) });
                        }
                        else if (n.Attributes?["alt"]?.Value is { Length: > 0 } alt) Text(alt, s);
                        break;
                    }
                    case "figcaption": case "caption":
                        Start(BlockKind.Caption, 0, s);
                        Walk(n, s with { Italic = true });
                        Flush();
                        break;
                    case "table":
                        Flush();
                        foreach (XmlNode row in n.SelectNodes(".//*[local-name()='tr']")!)
                        {
                            Start(BlockKind.Paragraph, 0, s);
                            bool first = true;
                            foreach (XmlNode cell in row.ChildNodes)
                            {
                                if (cell.LocalName is not ("td" or "th")) continue;
                                if (!first) Text(" · ", s);
                                first = false;
                                Walk(cell, s with { Bold = s.Bold || cell.LocalName == "th" });
                            }
                            Flush();
                        }
                        break;
                    case "div": case "section": case "article": case "header": case "footer": case "main": case "aside":
                    case "figure": case "nav": case "address": case "center": case "dl": case "dd": case "dt": case "body": case "svg":
                        Flush();
                        Walk(n, s);
                        Flush();
                        break;
                    case "em": case "i": case "cite": case "dfn": case "var":
                        Walk(n, s with { Italic = !s.Italic });
                        break;
                    case "strong": case "b":
                        Walk(n, s with { Bold = true });
                        break;
                    case "code": case "tt": case "kbd": case "samp":
                        Walk(n, s with { Mono = true });
                        break;
                    case "sup": case "sub": case "small":
                        Walk(n, s with { Small = true });
                        break;
                    case "a":
                        Walk(n, s with { Link = n.Attributes?["href"]?.Value ?? s.Link });
                        break;
                    default:
                        Walk(n, s);
                        break;
                }
            }
        }
    }
}
