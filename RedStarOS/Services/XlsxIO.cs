using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RedStarOS.Services;

/// <summary>Reads and writes the first worksheet of .xlsx workbooks (values, formulas, basic formatting, sizes, frozen panes).</summary>
public static class XlsxIO
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const double PxPerChar = 7.0;

    // ================================================================== write

    private sealed class StyleTable
    {
        public readonly List<CellFormat> Formats = new();
        public readonly List<string> Fonts = new() { "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" };
        public readonly List<string> Fills = new()
        {
            "<fill><patternFill patternType=\"none\"/></fill>",
            "<fill><patternFill patternType=\"gray125\"/></fill>"
        };
        public readonly List<string> Borders = new() { "<border><left/><right/><top/><bottom/><diagonal/></border>" };
        public readonly List<string> NumFmts = new();       // custom format codes, ids start at 164
        public readonly List<string> Xfs = new() { "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" };
        private readonly Dictionary<string, int> _xfIndex = new();

        private static int Index(List<string> list, string item)
        {
            int i = list.IndexOf(item);
            if (i >= 0) return i;
            list.Add(item);
            return list.Count - 1;
        }

        private static string Argb(uint c) => (c | 0xFF000000u).ToString("X8", Inv);

        private static string FormatCode(CellFormat f)
        {
            string dec(int d, int dflt) => (d < 0 ? dflt : d) <= 0 ? "" : "." + new string('0', d < 0 ? dflt : d);
            switch (f.Num)
            {
                case NumFmt.Number: return "0" + dec(f.Dec, 2);
                case NumFmt.Thousands: return "#,##0" + dec(f.Dec, 2);
                case NumFmt.Currency: return "\"$\"#,##0" + dec(f.Dec, 2);
                case NumFmt.Percent: return "0" + dec(f.Dec, 0) + "%";
                case NumFmt.Date: return "yyyy\\-mm\\-dd";
                case NumFmt.Text: return "@";
                default: return f.Dec >= 0 ? "0" + dec(f.Dec, 0) : "General";
            }
        }

        public int Xf(CellFormat? f)
        {
            if (f == null || f.IsDefault) return 0;
            var code = FormatCode(f);
            int numId;
            switch (code)
            {
                case "General": numId = 0; break;
                case "0": numId = 1; break;
                case "0.00": numId = 2; break;
                case "#,##0": numId = 3; break;
                case "#,##0.00": numId = 4; break;
                case "0%": numId = 9; break;
                case "0.00%": numId = 10; break;
                case "@": numId = 49; break;
                default: numId = 164 + Index(NumFmts, code); break;
            }

            var font = new StringBuilder("<font>");
            if (f.Bold) font.Append("<b/>");
            if (f.Italic) font.Append("<i/>");
            if (f.Underline) font.Append("<u/>");
            font.Append("<sz val=\"11\"/>");
            if (f.Color != 0) font.Append("<color rgb=\"").Append(Argb(f.Color)).Append("\"/>");
            font.Append("<name val=\"Calibri\"/></font>");
            int fontId = Index(Fonts, font.ToString());

            int fillId = 0;
            if (f.Fill != 0)
                fillId = Index(Fills, "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"" + Argb(f.Fill) + "\"/><bgColor indexed=\"64\"/></patternFill></fill>");

            int borderId = 0;
            if (f.Borders != 0)
            {
                string edge(string name, bool on) => on ? "<" + name + " style=\"thin\"><color auto=\"1\"/></" + name + ">" : "<" + name + "/>";
                borderId = Index(Borders, "<border>" + edge("left", (f.Borders & 1) != 0) + edge("right", (f.Borders & 4) != 0) +
                                          edge("top", (f.Borders & 2) != 0) + edge("bottom", (f.Borders & 8) != 0) + "<diagonal/></border>");
            }

            var xf = new StringBuilder();
            xf.Append("<xf numFmtId=\"").Append(numId).Append("\" fontId=\"").Append(fontId).Append("\" fillId=\"").Append(fillId)
              .Append("\" borderId=\"").Append(borderId).Append("\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\"");
            if (f.Align != 0)
            {
                var h = f.Align == 1 ? "left" : f.Align == 2 ? "center" : "right";
                xf.Append(" applyAlignment=\"1\"><alignment horizontal=\"").Append(h).Append("\"/></xf>");
            }
            else xf.Append("/>");
            var s = xf.ToString();
            if (!_xfIndex.TryGetValue(s, out var idx))
            {
                Xfs.Add(s);
                idx = Xfs.Count - 1;
                _xfIndex[s] = idx;
            }
            return idx;
        }
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == '&') sb.Append("&amp;");
            else if (ch == '<') sb.Append("&lt;");
            else if (ch == '>') sb.Append("&gt;");
            else if (ch == '"') sb.Append("&quot;");
            else if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') { }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    public static void Save(SheetModel m, string path)
    {
        var styles = new StyleTable();
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        sheet.Append("<sheetViews><sheetView workbookViewId=\"0\">");
        if (m.FrozenRows > 0 || m.FrozenCols > 0)
        {
            var tl = FormulaEvaluator.CellName(m.FrozenRows, m.FrozenCols);
            string pane = m.FrozenRows > 0 && m.FrozenCols > 0 ? "bottomRight" : (m.FrozenRows > 0 ? "bottomLeft" : "topRight");
            sheet.Append("<pane");
            if (m.FrozenCols > 0) sheet.Append(" xSplit=\"").Append(m.FrozenCols).Append('"');
            if (m.FrozenRows > 0) sheet.Append(" ySplit=\"").Append(m.FrozenRows).Append('"');
            sheet.Append(" topLeftCell=\"").Append(tl).Append("\" activePane=\"").Append(pane).Append("\" state=\"frozen\"/>");
        }
        sheet.Append("</sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"18\"/>");

        if (m.ColWidths.Count > 0)
        {
            sheet.Append("<cols>");
            foreach (var kv in m.ColWidths.OrderBy(k => k.Key))
            {
                double w = Math.Max(1, (kv.Value - 5) / PxPerChar);
                sheet.Append("<col min=\"").Append(kv.Key + 1).Append("\" max=\"").Append(kv.Key + 1).Append("\" width=\"")
                     .Append(w.ToString("0.##", Inv)).Append("\" customWidth=\"1\"/>");
            }
            sheet.Append("</cols>");
        }

        sheet.Append("<sheetData>");
        int curRow = -1;
        var rowsWithHeight = new HashSet<int>(m.RowHeights.Keys);
        var cells = m.AllCells().ToList();
        int ci = 0;
        var rowNumbers = new SortedSet<int>(cells.Select(c => c.Row));
        foreach (var r in rowsWithHeight) rowNumbers.Add(r);

        foreach (var r in rowNumbers)
        {
            sheet.Append("<row r=\"").Append(r + 1).Append('"');
            if (m.RowHeights.TryGetValue(r, out var h))
                sheet.Append(" ht=\"").Append((h * 0.75).ToString("0.##", Inv)).Append("\" customHeight=\"1\"");
            sheet.Append('>');
            curRow = r;
            while (ci < cells.Count && cells[ci].Row == curRow)
            {
                var (row, col, cell) = cells[ci++];
                int s = styles.Xf(cell.Fmt);
                string reference = FormulaEvaluator.CellName(row, col);
                string raw = cell.Raw;
                sheet.Append("<c r=\"").Append(reference).Append('"');
                if (s != 0) sheet.Append(" s=\"").Append(s).Append('"');

                if (raw.Length == 0) sheet.Append("/>");
                else if (raw[0] == '=' && raw.Length > 1)
                {
                    var v = m.Eval.Eval(row, col);
                    string f = "<f>" + Esc(raw.Substring(1)) + "</f>";
                    if (v is double d) sheet.Append('>').Append(f).Append("<v>").Append(d.ToString("R", Inv)).Append("</v></c>");
                    else if (v is bool b) sheet.Append(" t=\"b\">").Append(f).Append("<v>").Append(b ? 1 : 0).Append("</v></c>");
                    else if (v is CellError e) sheet.Append(" t=\"e\">").Append(f).Append("<v>").Append(Esc(e.Code)).Append("</v></c>");
                    else sheet.Append(" t=\"str\">").Append(f).Append("<v>").Append(Esc(v as string ?? "")).Append("</v></c>");
                }
                else if (raw[0] == '\'')
                    sheet.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Esc(raw.Substring(1))).Append("</t></is></c>");
                else if (double.TryParse(raw, NumberStyles.Float, Inv, out var num) && !double.IsNaN(num) && !double.IsInfinity(num))
                    sheet.Append("><v>").Append(num.ToString("R", Inv)).Append("</v></c>");
                else
                    sheet.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Esc(raw)).Append("</t></is></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        var st = new StringBuilder();
        st.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (styles.NumFmts.Count > 0)
        {
            st.Append("<numFmts count=\"").Append(styles.NumFmts.Count).Append("\">");
            for (int i = 0; i < styles.NumFmts.Count; i++)
                st.Append("<numFmt numFmtId=\"").Append(164 + i).Append("\" formatCode=\"").Append(Esc(styles.NumFmts[i])).Append("\"/>");
            st.Append("</numFmts>");
        }
        st.Append("<fonts count=\"").Append(styles.Fonts.Count).Append("\">").Append(string.Concat(styles.Fonts)).Append("</fonts>");
        st.Append("<fills count=\"").Append(styles.Fills.Count).Append("\">").Append(string.Concat(styles.Fills)).Append("</fills>");
        st.Append("<borders count=\"").Append(styles.Borders.Count).Append("\">").Append(string.Concat(styles.Borders)).Append("</borders>");
        st.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        st.Append("<cellXfs count=\"").Append(styles.Xfs.Count).Append("\">").Append(string.Concat(styles.Xfs)).Append("</cellXfs>");
        st.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

        const string head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
        var contentTypes = head + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>";
        var rels = head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
        var wb = head + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
        var wbRels = head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>";

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        AddEntry(zip, "[Content_Types].xml", contentTypes);
        AddEntry(zip, "_rels/.rels", rels);
        AddEntry(zip, "xl/workbook.xml", wb);
        AddEntry(zip, "xl/_rels/workbook.xml.rels", wbRels);
        AddEntry(zip, "xl/styles.xml", st.ToString());
        AddEntry(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    // ================================================================== read

    private static ZipArchiveEntry? Find(ZipArchive zip, string name)
    {
        name = name.TrimStart('/').Replace('\\', '/');
        var e = zip.GetEntry(name);
        if (e != null) return e;
        return zip.Entries.FirstOrDefault(x => string.Equals(x.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument? Load(ZipArchive zip, string name)
    {
        var e = Find(zip, name);
        if (e == null) return null;
        using var s = e.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var xr = XmlReader.Create(s, settings);
        return XDocument.Load(xr);
    }

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    private static string TextOf(XElement si)
    {
        var sb = new StringBuilder();
        foreach (var t in si.Descendants(Ns + "t"))
        {
            if (t.Parent != null && t.Parent.Name == Ns + "rPh") continue;
            sb.Append(t.Value);
        }
        return sb.ToString();
    }

    private static uint? ParseRgb(XElement? color)
    {
        if (color == null) return null;
        var rgb = Attr(color, "rgb");
        if (rgb.Length == 8 && uint.TryParse(rgb, NumberStyles.HexNumber, Inv, out var v)) return v | 0xFF000000u;
        if (rgb.Length == 6 && uint.TryParse(rgb, NumberStyles.HexNumber, Inv, out var v2)) return v2 | 0xFF000000u;
        return null;
    }

    private static void ApplyNumFmt(CellFormat f, int id, string? code)
    {
        switch (id)
        {
            case 1: f.Num = NumFmt.Number; f.Dec = 0; return;
            case 2: f.Num = NumFmt.Number; f.Dec = 2; return;
            case 3: f.Num = NumFmt.Thousands; f.Dec = 0; return;
            case 4: f.Num = NumFmt.Thousands; f.Dec = 2; return;
            case 9: f.Num = NumFmt.Percent; f.Dec = 0; return;
            case 10: f.Num = NumFmt.Percent; f.Dec = 2; return;
            case 14: case 15: case 16: case 17: case 22: f.Num = NumFmt.Date; return;
            case 49: f.Num = NumFmt.Text; return;
        }
        if (string.IsNullOrEmpty(code)) return;

        // strip quoted literals and escaped characters before inspecting the code
        var plain = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < code.Length; i++)
        {
            char ch = code[i];
            if (ch == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (ch == '\\') { i++; continue; }
            plain.Append(ch);
        }
        var p = plain.ToString().Split(';')[0];
        var lower = p.ToLowerInvariant();
        int dot = p.IndexOf('.');
        int dec = 0;
        if (dot >= 0) for (int i = dot + 1; i < p.Length && (p[i] == '0' || p[i] == '#'); i++) dec++;

        if (lower.Contains('%')) { f.Num = NumFmt.Percent; f.Dec = dec; }
        else if (lower == "@") f.Num = NumFmt.Text;
        else if ((lower.Contains('y') || lower.Contains('d') || lower.Contains('m')) && !lower.Contains('0') && !lower.Contains('#') && !lower.Contains("general"))
            f.Num = NumFmt.Date;
        else if (code.Contains('$') || code.Contains('€') || code.Contains('£')) { f.Num = NumFmt.Currency; f.Dec = dec; }
        else if (lower.Contains("#,##")) { f.Num = NumFmt.Thousands; f.Dec = dec; }
        else if (lower.Contains('0') && !lower.Contains("general")) { f.Num = NumFmt.Number; f.Dec = dec; }
    }

    private static List<CellFormat?> ReadStyles(ZipArchive zip)
    {
        var result = new List<CellFormat?>();
        var doc = Load(zip, "xl/styles.xml");
        if (doc?.Root == null) return result;
        var root = doc.Root;

        var numFmts = new Dictionary<int, string>();
        var nf = root.Element(Ns + "numFmts");
        if (nf != null)
            foreach (var e in nf.Elements(Ns + "numFmt"))
                if (int.TryParse(Attr(e, "numFmtId"), out var id)) numFmts[id] = Attr(e, "formatCode");

        var fonts = new List<(bool b, bool i, bool u, uint? color)>();
        var fe = root.Element(Ns + "fonts");
        if (fe != null)
            foreach (var f in fe.Elements(Ns + "font"))
            {
                var u = f.Element(Ns + "u");
                bool underline = u != null && Attr(u, "val") != "none";
                fonts.Add((IsOn(f.Element(Ns + "b")), IsOn(f.Element(Ns + "i")), underline, ParseRgb(f.Element(Ns + "color"))));
            }

        var fills = new List<uint?>();
        var fl = root.Element(Ns + "fills");
        if (fl != null)
            foreach (var f in fl.Elements(Ns + "fill"))
            {
                var pf = f.Element(Ns + "patternFill");
                uint? c = null;
                if (pf != null && Attr(pf, "patternType") == "solid") c = ParseRgb(pf.Element(Ns + "fgColor"));
                fills.Add(c);
            }

        var borders = new List<byte>();
        var be = root.Element(Ns + "borders");
        if (be != null)
            foreach (var b in be.Elements(Ns + "border"))
            {
                byte mask = 0;
                if (HasEdge(b, "left")) mask |= 1;
                if (HasEdge(b, "top")) mask |= 2;
                if (HasEdge(b, "right")) mask |= 4;
                if (HasEdge(b, "bottom")) mask |= 8;
                borders.Add(mask);
            }

        var xfs = root.Element(Ns + "cellXfs");
        if (xfs == null) return result;
        foreach (var xf in xfs.Elements(Ns + "xf"))
        {
            var f = new CellFormat();
            int.TryParse(Attr(xf, "numFmtId"), out var nid);
            int.TryParse(Attr(xf, "fontId"), out var fid);
            int.TryParse(Attr(xf, "fillId"), out var fillId);
            int.TryParse(Attr(xf, "borderId"), out var bid);
            numFmts.TryGetValue(nid, out var code);
            ApplyNumFmt(f, nid, code);
            if (fid >= 0 && fid < fonts.Count)
            {
                var ft = fonts[fid];
                f.Bold = ft.b; f.Italic = ft.i; f.Underline = ft.u;
                if (ft.color.HasValue && ft.color.Value != 0xFF000000u) f.Color = ft.color.Value;
            }
            if (fillId >= 0 && fillId < fills.Count && fills[fillId].HasValue) f.Fill = fills[fillId]!.Value;
            if (bid >= 0 && bid < borders.Count) f.Borders = borders[bid];
            var al = xf.Element(Ns + "alignment");
            if (al != null)
            {
                var h = Attr(al, "horizontal");
                f.Align = h == "left" ? (byte)1 : h == "center" ? (byte)2 : h == "right" ? (byte)3 : (byte)0;
            }
            result.Add(f.IsDefault ? null : f);
        }
        return result;
    }

    private static bool IsOn(XElement? e)
    {
        if (e == null) return false;
        var v = Attr(e, "val");
        return v != "0" && v != "false";
    }

    private static bool HasEdge(XElement border, string name)
    {
        var e = border.Element(Ns + name);
        return e != null && Attr(e, "style").Length > 0;
    }

    private static (int row, int col)? ParseRef(string r)
    {
        int i = 0;
        while (i < r.Length && char.IsLetter(r[i])) i++;
        if (i == 0 || i == r.Length) return null;
        if (!int.TryParse(r.AsSpan(i), NumberStyles.None, Inv, out var row)) return null;
        return (row - 1, FormulaEvaluator.ParseColumn(r.Substring(0, i)));
    }

    public static void Load(SheetModel m, string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

        // locate the first worksheet through the workbook relationships
        string sheetPath = "xl/worksheets/sheet1.xml";
        var wb = Load(zip, "xl/workbook.xml");
        var wbRels = Load(zip, "xl/_rels/workbook.xml.rels");
        if (wb?.Root != null && wbRels?.Root != null)
        {
            var first = wb.Root.Element(Ns + "sheets")?.Elements(Ns + "sheet").FirstOrDefault();
            var rid = first == null ? null : (string?)first.Attribute(RelNs + "id");
            if (rid != null)
            {
                var rel = wbRels.Root.Elements().FirstOrDefault(e => Attr(e, "Id") == rid);
                if (rel != null)
                {
                    var target = Attr(rel, "Target");
                    sheetPath = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                }
            }
        }

        var sheetDoc = Load(zip, sheetPath);
        if (sheetDoc?.Root == null) throw new InvalidDataException("This workbook has no readable worksheet.");

        var shared = new List<string>();
        var ssDoc = Load(zip, "xl/sharedStrings.xml");
        if (ssDoc?.Root != null)
            foreach (var si in ssDoc.Root.Elements(Ns + "si")) shared.Add(TextOf(si));

        var styles = ReadStyles(zip);

        m.Clear();
        var cells = new List<(int, int, Cell)>();
        var sharedFormulas = new Dictionary<string, (int row, int col, string formula)>();

        var root = sheetDoc.Root;

        var cols = root.Element(Ns + "cols");
        if (cols != null)
            foreach (var c in cols.Elements(Ns + "col"))
            {
                if (!int.TryParse(Attr(c, "min"), out var min) || !int.TryParse(Attr(c, "max"), out var max)) continue;
                if (!double.TryParse(Attr(c, "width"), NumberStyles.Float, Inv, out var w)) continue;
                if (Attr(c, "hidden") == "1") continue;
                double px = Math.Round(w * PxPerChar + 5);
                if (Math.Abs(px - SheetModel.DefaultColWidth) < 0.5) continue;
                for (int i = min - 1; i < Math.Min(max, SheetModel.MaxCols); i++) m.ColWidths[i] = px;
            }

        var view = root.Element(Ns + "sheetViews")?.Element(Ns + "sheetView")?.Element(Ns + "pane");
        if (view != null && Attr(view, "state") == "frozen")
        {
            if (double.TryParse(Attr(view, "xSplit"), NumberStyles.Float, Inv, out var xs)) m.FrozenCols = (int)xs;
            if (double.TryParse(Attr(view, "ySplit"), NumberStyles.Float, Inv, out var ys)) m.FrozenRows = (int)ys;
        }

        var data = root.Element(Ns + "sheetData");
        if (data != null)
        {
            int autoRow = 0;
            foreach (var row in data.Elements(Ns + "row"))
            {
                int rowIndex = int.TryParse(Attr(row, "r"), out var rn) ? rn - 1 : autoRow;
                autoRow = rowIndex + 1;
                if (rowIndex < 0 || rowIndex >= SheetModel.MaxRows) continue;

                if (Attr(row, "customHeight") == "1" && double.TryParse(Attr(row, "ht"), NumberStyles.Float, Inv, out var ht))
                {
                    double px = Math.Round(ht / 0.75);
                    if (Math.Abs(px - SheetModel.DefaultRowHeight) >= 0.5) m.RowHeights[rowIndex] = px;
                }
                if (Attr(row, "hidden") == "1") m.HiddenRows.Add(rowIndex);

                int autoCol = 0;
                foreach (var c in row.Elements(Ns + "c"))
                {
                    int colIndex = autoCol;
                    var pr = ParseRef(Attr(c, "r"));
                    if (pr.HasValue) { colIndex = pr.Value.col; if (pr.Value.row != rowIndex && pr.Value.row >= 0) rowIndex = pr.Value.row; }
                    autoCol = colIndex + 1;
                    if (colIndex < 0 || colIndex >= SheetModel.MaxCols) continue;

                    string type = Attr(c, "t");
                    var vEl = c.Element(Ns + "v");
                    string v = vEl?.Value ?? "";
                    var fEl = c.Element(Ns + "f");
                    string raw = "";

                    if (fEl != null)
                    {
                        string ft = fEl.Value;
                        string ftype = Attr(fEl, "t");
                        string si = Attr(fEl, "si");
                        if (ftype == "shared" && si.Length > 0)
                        {
                            if (ft.Length > 0) sharedFormulas[si] = (rowIndex, colIndex, "=" + ft);
                            else if (sharedFormulas.TryGetValue(si, out var master))
                                ft = FormulaEvaluator.ShiftFormula(master.formula, rowIndex - master.row, colIndex - master.col, SheetModel.MaxRows, SheetModel.MaxCols).Substring(1);
                        }
                        if (ft.Length > 0) raw = "=" + ft.Replace("_xlfn.", "").Replace("_xlws.", "");
                    }

                    if (raw.Length == 0)
                    {
                        switch (type)
                        {
                            case "s":
                                if (int.TryParse(v, out var idx) && idx >= 0 && idx < shared.Count) raw = AsText(shared[idx]);
                                break;
                            case "inlineStr":
                                { var isEl = c.Element(Ns + "is"); raw = isEl == null ? "" : AsText(TextOf(isEl)); }
                                break;
                            case "str": raw = AsText(v); break;
                            case "b": raw = v == "1" ? "=TRUE" : "=FALSE"; break;
                            case "e": raw = AsText(v); break;
                            default:
                                raw = v;
                                break;
                        }
                    }

                    CellFormat? fmt = null;
                    if (int.TryParse(Attr(c, "s"), out var sIdx) && sIdx >= 0 && sIdx < styles.Count) fmt = styles[sIdx];
                    if (raw.Length == 0 && fmt == null) continue;
                    cells.Add((rowIndex, colIndex, new Cell { Raw = raw, Fmt = fmt }));
                }
            }
        }
        m.BulkSet(cells);
    }

    /// <summary>Text that could be mistaken for a number or formula is stored with a leading apostrophe.</summary>
    private static string AsText(string s)
    {
        if (s.Length == 0) return s;
        if (s[0] == '=' || s[0] == '\'') return "'" + s;
        if (double.TryParse(s, NumberStyles.Float, Inv, out _)) return "'" + s;
        return s;
    }
}
