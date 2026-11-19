using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace RedStarOS.Services;

public sealed class DocxInfo
{
    public bool Letter { get; set; }
    public bool PageNumbers { get; set; }
}

/// <summary>Reads and writes .docx (WordprocessingML) for the native Writer: styles, lists, tables, images, links, page numbers.</summary>
public static class DocxIO
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    // ═════════════════════════ export ═════════════════════════
    public static void Save(RichDoc doc, string path, bool letter, bool pageNumbers)
    {
        var rels = new List<(string id, string type, string target, bool external)>();
        var media = new List<(string name, byte[] data)>();
        var numbering = new List<int>();       // numIds for numbered groups
        int nextRel = 10, nextNum = 100, nextImg = 1;
        string Rel(string type, string target, bool ext = false) { var id = "rId" + nextRel++; rels.Add((id, type, target, ext)); return id; }

        var body = new StringBuilder();
        int curNum = 0; bool prevNumbered = false;

        foreach (var b in doc.Blocks)
        {
            switch (b.Kind)
            {
                case BlockKind.Para:
                {
                    var p = b.Para!;
                    int numId = 0;
                    if (p.List == ListKind.Bullet) { numId = 1; prevNumbered = false; }
                    else if (p.List == ListKind.Number)
                    {
                        if (!prevNumbered) { curNum = nextNum++; numbering.Add(curNum); }
                        numId = curNum; prevNumbered = true;
                    }
                    else prevNumbered = false;
                    body.Append(ParaXml(p, numId, Rel));
                    break;
                }
                case BlockKind.Table:
                {
                    prevNumbered = false;
                    var rows = b.Rows!;
                    int cols = rows.Count == 0 ? 1 : rows.Max(r => r.Count);
                    int cw = 9000 / cols;
                    body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>");
                    foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
                        body.Append($"<w:{side} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8A8A8A\"/>");
                    body.Append("</w:tblBorders></w:tblPr><w:tblGrid>");
                    for (int c = 0; c < cols; c++) body.Append($"<w:gridCol w:w=\"{cw}\"/>");
                    body.Append("</w:tblGrid>");
                    foreach (var row in rows)
                    {
                        body.Append("<w:tr>");
                        for (int c = 0; c < cols; c++)
                        {
                            var cell = c < row.Count ? row[c] : new Para();
                            body.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{cw}\" w:type=\"dxa\"/></w:tcPr>");
                            body.Append(ParaXml(cell, 0, Rel));
                            body.Append("</w:tc>");
                        }
                        body.Append("</w:tr>");
                    }
                    body.Append("</w:tbl><w:p/>");
                    break;
                }
                case BlockKind.Image:
                {
                    prevNumbered = false;
                    if (b.Image == null) break;
                    bool png = b.Image.Length > 4 && b.Image[0] == 0x89;
                    var name = $"image{nextImg++}." + (png ? "png" : "jpeg");
                    media.Add((name, b.Image));
                    var rid = Rel("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", "media/" + name);
                    long cx = (long)(Math.Max(1, b.ImgW) * 9525), cy = (long)(Math.Max(1, b.ImgH) * 9525);
                    var jc = b.ImgAlign == Align.Center ? "center" : b.ImgAlign == Align.Right ? "right" : "left";
                    body.Append($"<w:p><w:pPr><w:jc w:val=\"{jc}\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{nextImg}\" name=\"Picture {nextImg}\"/>" +
                        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
                        $"<pic:nvPicPr><pic:cNvPr id=\"{nextImg}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                        $"<pic:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                        $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
                    break;
                }
                case BlockKind.Rule:
                    prevNumbered = false;
                    body.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"777777\"/></w:pBdr></w:pPr></w:p>");
                    break;
                case BlockKind.PageBreak:
                    prevNumbered = false;
                    body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                    break;
            }
        }

        string footerRel = "";
        if (pageNumbers) footerRel = $"<w:footerReference w:type=\"default\" r:id=\"{Rel("http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer", "footer1.xml")}\"/>";
        var pg = letter ? "<w:pgSz w:w=\"12240\" w:h=\"15840\"/>" : "<w:pgSz w:w=\"11906\" w:h=\"16838\"/>";
        var sect = $"<w:sectPr>{footerRel}{pg}<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>";

        var docXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"" + W + "\" xmlns:r=\"" + R + "\" xmlns:wp=\"" + WP + "\" xmlns:a=\"" + A + "\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
            "<w:body>" + body + sect + "</w:body></w:document>";

        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = e.Open(); var bytes = new UTF8Encoding(false).GetBytes(content); s.Write(bytes, 0, bytes.Length);
            }
            var ct = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>");
            if (pageNumbers) ct.Append("<Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>");
            ct.Append("</Types>");
            Add("[Content_Types].xml", ct.ToString());
            Add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"" + PR + "\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml", docXml);
            Add("word/styles.xml", StylesXml());
            Add("word/numbering.xml", NumberingXml(numbering));
            var rx = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"" + PR + "\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
            foreach (var (id, type, target, ext) in rels)
                rx.Append($"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{Esc(target)}\"{(ext ? " TargetMode=\"External\"" : "")}/>");
            rx.Append("</Relationships>");
            Add("word/_rels/document.xml.rels", rx.ToString());
            if (pageNumbers)
                Add("word/footer1.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:ftr xmlns:w=\"" + W + "\"><w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr>" +
                    "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p></w:ftr>");
            foreach (var (name, data) in media)
            {
                var e = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression);
                using var s = e.Open(); s.Write(data, 0, data.Length);
            }
        }
        File.Move(tmp, path, true);
    }

    private static string ParaXml(Para p, int numId, Func<string, string, bool, string> rel)
    {
        var sb = new StringBuilder("<w:p><w:pPr>");
        var style = p.Style switch { PStyle.H1 => "Heading1", PStyle.H2 => "Heading2", PStyle.H3 => "Heading3", PStyle.Quote => "Quote", PStyle.Code => "CodeBlock", _ => null };
        if (style != null) sb.Append($"<w:pStyle w:val=\"{style}\"/>");
        if (numId > 0) sb.Append($"<w:numPr><w:ilvl w:val=\"{Math.Clamp(p.Level, 0, 8)}\"/><w:numId w:val=\"{numId}\"/></w:numPr>");
        if (Math.Abs(p.Spacing - 1.0) > 0.01) sb.Append($"<w:spacing w:line=\"{(int)(240 * p.Spacing)}\" w:lineRule=\"auto\"/>");
        if (numId == 0 && p.Level > 0) sb.Append($"<w:ind w:left=\"{p.Level * 720}\"/>");
        if (p.Align != Align.Left) sb.Append($"<w:jc w:val=\"{(p.Align == Align.Center ? "center" : p.Align == Align.Right ? "right" : "both")}\"/>");
        sb.Append("</w:pPr>");
        int i = 0;
        while (i < p.Runs.Count)
        {
            var r = p.Runs[i];
            if (r.Link != null)
            {
                int j = i;
                var inner = new StringBuilder();
                while (j < p.Runs.Count && p.Runs[j].Link == r.Link) { inner.Append(RunXml(p.Runs[j], true)); j++; }
                bool anchor = r.Link.StartsWith("#");
                var rid = rel("http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", r.Link, true);
                sb.Append($"<w:hyperlink r:id=\"{rid}\">{inner}</w:hyperlink>");
                i = j;
            }
            else { sb.Append(RunXml(r, false)); i++; }
        }
        sb.Append("</w:p>");
        return sb.ToString();
    }

    private static string Hex(string? c) => (c ?? "").TrimStart('#').ToUpperInvariant();

    private static string RunXml(Run r, bool link)
    {
        if (r.Text.Length == 0) return "";
        var pr = new StringBuilder();
        if (link) pr.Append("<w:rStyle w:val=\"Hyperlink\"/>");
        if (!string.IsNullOrEmpty(r.Font)) pr.Append($"<w:rFonts w:ascii=\"{Esc(r.Font)}\" w:hAnsi=\"{Esc(r.Font)}\" w:cs=\"{Esc(r.Font)}\"/>");
        if (r.Bold) pr.Append("<w:b/>");
        if (r.Italic) pr.Append("<w:i/>");
        if (r.Strike) pr.Append("<w:strike/>");
        if (r.Color != null) pr.Append($"<w:color w:val=\"{Hex(r.Color)}\"/>");
        if (r.Size > 0) pr.Append($"<w:sz w:val=\"{(int)Math.Round(r.Size * 2)}\"/>");
        if (r.Highlight != null) pr.Append($"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{Hex(r.Highlight)}\"/>");
        if (r.Underline) pr.Append("<w:u w:val=\"single\"/>");
        if (r.Sup) pr.Append("<w:vertAlign w:val=\"superscript\"/>");
        else if (r.Sub) pr.Append("<w:vertAlign w:val=\"subscript\"/>");
        return $"<w:r>{(pr.Length > 0 ? "<w:rPr>" + pr + "</w:rPr>" : "")}<w:t xml:space=\"preserve\">{Esc(r.Text)}</w:t></w:r>";
    }

    private static string StylesXml()
    {
        string Heading(string id, string name, int sz, int before, int after) =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"{before}\" w:after=\"{after}\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"{sz}\"/></w:rPr></w:style>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"" + W + "\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
            Heading("Heading1", "heading 1", 44, 240, 200) + Heading("Heading2", "heading 2", 34, 200, 160) + Heading("Heading3", "heading 3", 28, 160, 120) +
            "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"B0B7C3\"/></w:pBdr><w:ind w:left=\"360\"/></w:pPr><w:rPr><w:i/><w:color w:val=\"555555\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"CodeBlock\"><w:name w:val=\"Source Code\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F1F3F4\"/><w:spacing w:after=\"0\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Courier New\" w:hAnsi=\"Courier New\" w:cs=\"Courier New\"/><w:sz w:val=\"20\"/></w:rPr></w:style>" +
            "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:rPr><w:color w:val=\"0B57D0\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
            "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8A8A8A\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8A8A8A\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8A8A8A\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8A8A8A\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8A8A8A\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8A8A8A\"/></w:tblBorders><w:tblCellMar><w:left w:w=\"108\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
            "</w:styles>";
    }

    private static string NumberingXml(List<int> numberedGroups)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering xmlns:w=\"" + W + "\">");
        string Abs(int id, bool bullet)
        {
            var s = new StringBuilder($"<w:abstractNum w:abstractNumId=\"{id}\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
            for (int l = 0; l < 9; l++)
            {
                var fmt = bullet ? "bullet" : l % 3 == 0 ? "decimal" : l % 3 == 1 ? "lowerLetter" : "lowerRoman";
                var txt = bullet ? (l % 2 == 0 ? "•" : "◦") : $"%{l + 1}.";
                s.Append($"<w:lvl w:ilvl=\"{l}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{fmt}\"/><w:lvlText w:val=\"{txt}\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{720 * (l + 1)}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
            }
            return s.Append("</w:abstractNum>").ToString();
        }
        sb.Append(Abs(1, true)).Append(Abs(2, false));
        sb.Append("<w:num w:numId=\"1\"><w:abstractNumId w:val=\"1\"/></w:num>");
        foreach (var n in numberedGroups)
            sb.Append($"<w:num w:numId=\"{n}\"><w:abstractNumId w:val=\"2\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>");
        sb.Append("</w:numbering>");
        return sb.ToString();
    }

    // ═════════════════════════ import ═════════════════════════
    private sealed class Ctx
    {
        public Dictionary<string, string> Rels = new();
        public Dictionary<string, byte[]> Media = new();
        public Dictionary<string, PStyle> Styles = new();
        public Dictionary<string, (bool bold, bool italic, double size, string? color)> StyleRun = new();
        public Dictionary<string, bool> NumBullet = new();   // "numId:ilvl" -> bullet?
    }

    public static (RichDoc doc, DocxInfo info) Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string? Text(string name)
        {
            var e = zip.GetEntry(name); if (e == null) return null;
            using var s = e.Open(); using var r = new StreamReader(s, Encoding.UTF8); return r.ReadToEnd();
        }
        var docText = Text("word/document.xml") ?? throw new InvalidDataException("Not a Word document (word/document.xml missing).");
        var ctx = new Ctx();

        var relText = Text("word/_rels/document.xml.rels");
        if (relText != null)
            foreach (var rel in XDocument.Parse(relText).Descendants(PR + "Relationship"))
            {
                var id = (string?)rel.Attribute("Id"); var tg = (string?)rel.Attribute("Target");
                if (id != null && tg != null) ctx.Rels[id] = tg;
            }
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("word/media/")))
        {
            using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
            ctx.Media["media/" + e.Name] = ms.ToArray();
        }
        var stylesText = Text("word/styles.xml");
        if (stylesText != null) ReadStyles(XDocument.Parse(stylesText), ctx);
        var numText = Text("word/numbering.xml");
        if (numText != null) ReadNumbering(XDocument.Parse(numText), ctx);

        var xd = XDocument.Parse(docText);
        var body = xd.Root?.Element(W + "body") ?? throw new InvalidDataException("Empty document.");
        var doc = new RichDoc();
        var info = new DocxInfo();

        foreach (var el in body.Elements())
        {
            if (el.Name == W + "p") ReadParagraph(el, ctx, doc.Blocks, null);
            else if (el.Name == W + "tbl") ReadTable(el, ctx, doc.Blocks);
            else if (el.Name == W + "sectPr")
            {
                var pg = el.Element(W + "pgSz");
                if (pg != null && int.TryParse((string?)pg.Attribute(W + "w"), out var wv)) info.Letter = wv >= 12000;
            }
        }
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("word/footer")))
        {
            var t = Text(e.FullName);
            if (t != null && (t.Contains("PAGE") || t.Contains("fldSimple"))) info.PageNumbers = true;
        }
        doc.EnsureValid();
        return (doc, info);
    }

    private static void ReadStyles(XDocument xd, Ctx ctx)
    {
        foreach (var st in xd.Descendants(W + "style"))
        {
            var id = (string?)st.Attribute(W + "styleId");
            var name = ((string?)st.Element(W + "name")?.Attribute(W + "val") ?? "").ToLowerInvariant();
            if (id == null) continue;
            PStyle? ps = null;
            if (name.StartsWith("heading 1") || name == "title") ps = PStyle.H1;
            else if (name.StartsWith("heading 2") || name == "subtitle") ps = PStyle.H2;
            else if (name.StartsWith("heading 3") || name.StartsWith("heading 4") || name.StartsWith("heading 5")) ps = PStyle.H3;
            else if (name.Contains("quote")) ps = PStyle.Quote;
            else if (name.Contains("code") || name.Contains("preformatted") || name.Contains("plain text")) ps = PStyle.Code;
            if (ps != null) ctx.Styles[id] = ps.Value;
        }
    }

    private static void ReadNumbering(XDocument xd, Ctx ctx)
    {
        var abs = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var an in xd.Descendants(W + "abstractNum"))
        {
            var id = (string?)an.Attribute(W + "abstractNumId") ?? "";
            var d = new Dictionary<string, bool>();
            foreach (var lvl in an.Elements(W + "lvl"))
            {
                var il = (string?)lvl.Attribute(W + "ilvl") ?? "0";
                var fmt = (string?)lvl.Element(W + "numFmt")?.Attribute(W + "val") ?? "";
                d[il] = fmt == "bullet" || fmt == "none";
            }
            abs[id] = d;
        }
        foreach (var n in xd.Descendants(W + "num"))
        {
            var nid = (string?)n.Attribute(W + "numId") ?? "";
            var aid = (string?)n.Element(W + "abstractNumId")?.Attribute(W + "val") ?? "";
            if (!abs.TryGetValue(aid, out var d)) continue;
            foreach (var kv in d) ctx.NumBullet[nid + ":" + kv.Key] = kv.Value;
        }
    }

    private static bool On(XElement? e)
    {
        if (e == null) return false;
        var v = (string?)e.Attribute(W + "val");
        return v == null || !(v == "0" || v == "false" || v == "off");
    }

    private static readonly Dictionary<string, string> HiNames = new()
    {
        ["yellow"] = "#FFFF00", ["green"] = "#00FF00", ["cyan"] = "#00FFFF", ["magenta"] = "#FF00FF", ["blue"] = "#0000FF",
        ["red"] = "#FF0000", ["darkBlue"] = "#000080", ["darkCyan"] = "#008080", ["darkGreen"] = "#008000", ["darkMagenta"] = "#800080",
        ["darkRed"] = "#800000", ["darkYellow"] = "#808000", ["darkGray"] = "#808080", ["lightGray"] = "#C0C0C0", ["black"] = "#000000"
    };

    private static Run ReadRunProps(XElement? rpr, Ctx ctx)
    {
        var run = new Run();
        if (rpr == null) return run;
        run.Bold = On(rpr.Element(W + "b"));
        run.Italic = On(rpr.Element(W + "i"));
        run.Strike = On(rpr.Element(W + "strike")) || On(rpr.Element(W + "dstrike"));
        var u = rpr.Element(W + "u");
        run.Underline = u != null && (string?)u.Attribute(W + "val") != "none";
        var va = (string?)rpr.Element(W + "vertAlign")?.Attribute(W + "val");
        run.Sup = va == "superscript"; run.Sub = va == "subscript";
        var col = (string?)rpr.Element(W + "color")?.Attribute(W + "val");
        if (!string.IsNullOrEmpty(col) && col != "auto" && col.Length == 6) run.Color = "#" + col.ToUpperInvariant();
        var sz = (string?)rpr.Element(W + "sz")?.Attribute(W + "val");
        if (double.TryParse(sz, NumberStyles.Float, CultureInfo.InvariantCulture, out var half) && half > 0) run.Size = half / 2;
        var font = (string?)rpr.Element(W + "rFonts")?.Attribute(W + "ascii");
        if (!string.IsNullOrEmpty(font)) run.Font = font;
        var hi = (string?)rpr.Element(W + "highlight")?.Attribute(W + "val");
        if (hi != null && HiNames.TryGetValue(hi, out var hc)) run.Highlight = hc;
        var fill = (string?)rpr.Element(W + "shd")?.Attribute(W + "fill");
        if (run.Highlight == null && !string.IsNullOrEmpty(fill) && fill != "auto" && fill.Length == 6) run.Highlight = "#" + fill.ToUpperInvariant();
        return run;
    }

    private static void ReadTable(XElement tbl, Ctx ctx, List<Block> blocks)
    {
        var b = new Block { Kind = BlockKind.Table, Rows = new() };
        foreach (var tr in tbl.Elements(W + "tr"))
        {
            var row = new List<Para>();
            foreach (var tc in tr.Elements(W + "tc"))
            {
                var tmp = new List<Block>();
                foreach (var p in tc.Elements(W + "p")) ReadParagraph(p, ctx, tmp, null);
                var cell = new Para();
                bool first = true;
                foreach (var tb in tmp.Where(x => x.Kind == BlockKind.Para))
                {
                    if (!first && tb.Para!.Length > 0 && cell.Length > 0) cell.Runs.Add(new Run { Text = " " });
                    foreach (var r in tb.Para!.Runs) cell.Runs.Add(r);
                    first = false;
                }
                cell.Normalize();
                row.Add(cell);
            }
            if (row.Count > 0) b.Rows.Add(row);
        }
        if (b.Rows.Count == 0) return;
        int cols = b.Rows.Max(r => r.Count);
        foreach (var r in b.Rows) while (r.Count < cols) r.Add(new Para());
        blocks.Add(b);
    }

    private static void ReadParagraph(XElement p, Ctx ctx, List<Block> blocks, object? _)
    {
        var para = new Para();
        var ppr = p.Element(W + "pPr");
        Run baseRun = new();
        if (ppr != null)
        {
            var sid = (string?)ppr.Element(W + "pStyle")?.Attribute(W + "val");
            if (sid != null && ctx.Styles.TryGetValue(sid, out var ps)) para.Style = ps;
            var jc = (string?)ppr.Element(W + "jc")?.Attribute(W + "val");
            para.Align = jc switch { "center" => Align.Center, "right" or "end" => Align.Right, "both" or "distribute" => Align.Justify, _ => Align.Left };
            var line = (string?)ppr.Element(W + "spacing")?.Attribute(W + "line");
            var rule = (string?)ppr.Element(W + "spacing")?.Attribute(W + "lineRule");
            if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var ln) && (rule == null || rule == "auto"))
                para.Spacing = Math.Clamp(ln / 240.0, 0.8, 3.0);
            var num = ppr.Element(W + "numPr");
            if (num != null)
            {
                var nid = (string?)num.Element(W + "numId")?.Attribute(W + "val") ?? "0";
                var il = (string?)num.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
                if (nid != "0")
                {
                    bool bullet = ctx.NumBullet.TryGetValue(nid + ":" + il, out var bb) ? bb : true;
                    para.List = bullet ? ListKind.Bullet : ListKind.Number;
                    int.TryParse(il, out var lv); para.Level = Math.Clamp(lv, 0, 8);
                }
            }
            else
            {
                var left = (string?)ppr.Element(W + "ind")?.Attribute(W + "left");
                if (int.TryParse(left, out var lw) && lw >= 360) para.Level = Math.Clamp(lw / 720, 0, 8);
            }
            // horizontal rule (empty paragraph with a bottom border)
            if (ppr.Element(W + "pBdr")?.Element(W + "bottom") != null && !p.Descendants(W + "t").Any())
            {
                blocks.Add(new Block { Kind = BlockKind.Rule });
                return;
            }
            if (ppr.Element(W + "shd") != null && para.Style == PStyle.Normal && (string?)ppr.Element(W + "shd")?.Attribute(W + "fill") is { Length: 6 }) { }
        }

        void Flush()
        {
            para.Normalize();
            blocks.Add(Block.Of(para));
            para = new Para { Style = para.Style, Align = para.Align, List = para.List, Level = para.Level, Spacing = para.Spacing };
        }
        bool emittedExtra = false;

        void WalkRuns(IEnumerable<XElement> children, string? link)
        {
            foreach (var ch in children)
            {
                if (ch.Name == W + "r") ReadRun(ch, link);
                else if (ch.Name == W + "hyperlink")
                {
                    var rid = (string?)ch.Attribute(R + "id");
                    string? target = rid != null && ctx.Rels.TryGetValue(rid, out var t) ? t : null;
                    var anchor = (string?)ch.Attribute(W + "anchor");
                    if (target == null && anchor != null) target = "#" + anchor;
                    WalkRuns(ch.Elements(), target ?? link);
                }
                else if (ch.Name == W + "ins" || ch.Name == W + "smartTag" || ch.Name == W + "sdt" || ch.Name == W + "sdtContent" || ch.Name == W + "fldSimple")
                    WalkRuns(ch.Elements(), link);
            }
        }

        void ReadRun(XElement r, string? link)
        {
            var rpr = r.Element(W + "rPr");
            var props = ReadRunProps(rpr, ctx);
            var rsid = (string?)rpr?.Element(W + "rStyle")?.Attribute(W + "val");
            if (link != null) props.Link = link;
            var sb = new StringBuilder();
            foreach (var c in r.Elements())
            {
                if (c.Name == W + "t") sb.Append((string)c);
                else if (c.Name == W + "tab") sb.Append("    ");
                else if (c.Name == W + "br")
                {
                    if ((string?)c.Attribute(W + "type") == "page")
                    {
                        if (sb.Length > 0) { para.Runs.Add(props.CloneWith(sb.ToString())); sb.Clear(); }
                        if (para.Length > 0) Flush();
                        blocks.Add(new Block { Kind = BlockKind.PageBreak });
                        emittedExtra = true;
                    }
                    else sb.Append(' ');
                }
                else if (c.Name == W + "drawing")
                {
                    if (sb.Length > 0) { para.Runs.Add(props.CloneWith(sb.ToString())); sb.Clear(); }
                    var blip = c.Descendants(A + "blip").FirstOrDefault();
                    var rid = (string?)blip?.Attribute(R + "embed");
                    if (rid != null && ctx.Rels.TryGetValue(rid, out var target) && ctx.Media.TryGetValue(target.TrimStart('/').Replace("word/", ""), out var data))
                    {
                        var ext = c.Descendants(WP + "extent").FirstOrDefault();
                        double w = 0, h = 0;
                        if (ext != null) { double.TryParse((string?)ext.Attribute("cx"), out w); double.TryParse((string?)ext.Attribute("cy"), out h); w /= 9525; h /= 9525; }
                        if (para.Length > 0) Flush();
                        blocks.Add(new Block { Kind = BlockKind.Image, Image = data, ImgW = w, ImgH = h, ImgAlign = para.Align });
                        emittedExtra = true;
                    }
                }
            }
            if (sb.Length > 0) para.Runs.Add(props.CloneWith(sb.ToString()));
        }

        WalkRuns(p.Elements(), null);
        // keep empty paragraphs (blank lines) except right after an image/page-break emitted from this same paragraph
        if (para.Length > 0 || !emittedExtra) { para.Normalize(); blocks.Add(Block.Of(para)); }
    }
}
