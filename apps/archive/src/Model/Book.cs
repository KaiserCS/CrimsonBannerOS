// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace PeoplesArchive.Model;

public enum BlockKind { Heading, Paragraph, Quote, ListItem, Preformatted, Rule, Image, Caption }

/// <summary>A stretch of text with one style.</summary>
public sealed record Run(string Text, bool Italic = false, bool Bold = false, bool Mono = false, bool Small = false, string? Link = null);

/// <summary>One block of a chapter, ready to draw: a heading, a paragraph, a picture…</summary>
public sealed class Block
{
    public BlockKind Kind { get; init; }
    public int Level { get; init; }                    // headings 1–6; nesting depth of quotes and lists
    public List<Run> Runs { get; } = new();
    public string? Image { get; init; }                // a picture's path inside the book
    public string? Anchor { get; set; }                // an id a link can point at
    public string Text => string.Concat(Runs.Select(r => r.Text));
}

public sealed class Chapter
{
    public required string Title { get; init; }
    public required string Path { get; init; }         // inside the book (EPUB), or "" for a text file
}

public sealed class TocEntry
{
    public required string Title { get; init; }
    public required int Chapter { get; init; }
    public string? Fragment { get; init; }
    public int Depth { get; init; }
}

/// <summary>
/// A book opened for reading: an EPUB (its spine, contents and pictures, read from the zip as needed) or a plain
/// text file. Chapters become <see cref="Block"/>s, which the reading room draws.
/// </summary>
public sealed class Book : IDisposable
{
    public string Title { get; private set; } = "";
    public string Author { get; private set; } = "";
    public string Language { get; private set; } = "";
    public string Rights { get; private set; } = "";
    public string? CoverPath { get; private set; }
    public List<Chapter> Chapters { get; } = new();
    public List<TocEntry> Contents { get; } = new();

    private ZipArchive? _zip;
    private string? _text;

    public static Book Open(string file)
    {
        var b = new Book();
        if (IsZip(file)) b.LoadEpub(file);
        else b.LoadText(file);
        return b;
    }

    public static bool IsZip(string file)
    {
        using var f = File.OpenRead(file);
        var head = new byte[4];
        return f.Read(head, 0, 4) == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;
    }

    // ───────── EPUB ─────────

    private void LoadEpub(string file)
    {
        _zip = ZipFile.OpenRead(file);
        var container = Xml(Read("META-INF/container.xml") ?? throw new InvalidDataException("This EPUB has no META-INF/container.xml."));
        var opfPath = container.SelectSingleNode("//*[local-name()='rootfile']/@full-path")?.Value
                      ?? throw new InvalidDataException("This EPUB's container doesn't name its package.");
        var opf = Xml(Read(opfPath) ?? throw new InvalidDataException($"This EPUB's package {opfPath} is missing."));
        var baseDir = Dir(opfPath);

        string Meta(string name) => opf.SelectNodes($"//*[local-name()='metadata']/*[local-name()='{name}']")?.Cast<XmlNode>()
            .Select(n => Clean(n.InnerText)).FirstOrDefault(t => t.Length > 0) ?? "";
        Title = Meta("title");
        Author = string.Join(", ", opf.SelectNodes("//*[local-name()='metadata']/*[local-name()='creator']")?.Cast<XmlNode>()
            .Select(n => Clean(n.InnerText)).Where(t => t.Length > 0).Distinct() ?? Enumerable.Empty<string>());
        Language = Meta("language");
        Rights = Meta("rights");

        // the manifest: id → (path, type, properties)
        var items = new Dictionary<string, (string Path, string Type, string Props)>();
        foreach (XmlNode it in opf.SelectNodes("//*[local-name()='manifest']/*[local-name()='item']")!)
        {
            var id = it.Attributes?["id"]?.Value; var href = it.Attributes?["href"]?.Value;
            if (id == null || href == null) continue;
            items[id] = (Join(baseDir, href), it.Attributes?["media-type"]?.Value ?? "", it.Attributes?["properties"]?.Value ?? "");
        }
        // the cover: EPUB 3's cover-image, or EPUB 2's <meta name="cover">
        CoverPath = items.Values.FirstOrDefault(i => i.Props.Split(' ').Contains("cover-image")).Path;
        if (CoverPath == null && opf.SelectSingleNode("//*[local-name()='meta'][@name='cover']/@content")?.Value is { } cid && items.TryGetValue(cid, out var ci))
            CoverPath = ci.Path;

        // the spine: the chapters, in reading order
        foreach (XmlNode r in opf.SelectNodes("//*[local-name()='spine']/*[local-name()='itemref']")!)
        {
            var idref = r.Attributes?["idref"]?.Value;
            if (idref == null || !items.TryGetValue(idref, out var item)) continue;
            if (!item.Type.Contains("html", StringComparison.OrdinalIgnoreCase)) continue;
            Chapters.Add(new Chapter { Title = "", Path = item.Path });
        }
        if (Chapters.Count == 0) throw new InvalidDataException("This EPUB has nothing to read in its spine.");

        // the contents: EPUB 3's navigation document, else EPUB 2's NCX
        var nav = items.Values.FirstOrDefault(i => i.Props.Split(' ').Contains("nav"));
        if (nav.Path != null && Read(nav.Path) is { } navText) ReadNav(navText, Dir(nav.Path));
        if (Contents.Count == 0)
        {
            var tocId = opf.SelectSingleNode("//*[local-name()='spine']/@toc")?.Value;
            var ncx = tocId != null && items.TryGetValue(tocId, out var n) ? n.Path
                      : items.Values.FirstOrDefault(i => i.Type == "application/x-dtbncx+xml").Path;
            if (ncx != null && Read(ncx) is { } ncxText) ReadNcx(ncxText, Dir(ncx));
        }
        // each chapter's title: its first entry in the contents, else its first heading, else its number
        for (int i = 0; i < Chapters.Count; i++)
        {
            var t = Contents.FirstOrDefault(c => c.Chapter == i)?.Title;
            if (string.IsNullOrEmpty(t)) t = ChapterBlocks(i).FirstOrDefault(b => b.Kind == BlockKind.Heading)?.Text;
            Chapters[i] = new Chapter { Title = string.IsNullOrWhiteSpace(t) ? $"Part {i + 1}" : Trim(t, 80), Path = Chapters[i].Path };
        }
        if (Contents.Count == 0)
            for (int i = 0; i < Chapters.Count; i++) Contents.Add(new TocEntry { Title = Chapters[i].Title, Chapter = i });
        if (Title.Length == 0) Title = Path.GetFileNameWithoutExtension(file);
    }

    private void ReadNav(string text, string dir)
    {
        var doc = Xml(text);
        var toc = doc.SelectSingleNode("//*[local-name()='nav'][contains(@*[local-name()='type'], 'toc')]") ?? doc.SelectSingleNode("//*[local-name()='nav']");
        if (toc == null) return;
        void Walk(XmlNode list, int depth)
        {
            foreach (XmlNode li in list.ChildNodes)
            {
                if (li.LocalName != "li") continue;
                var a = li.SelectSingleNode("*[local-name()='a' or local-name()='span']");
                if (a?.Attributes?["href"]?.Value is { } href) Add(Clean(a.InnerText), Join(dir, href), depth);
                if (li.SelectSingleNode("*[local-name()='ol' or local-name()='ul']") is { } sub) Walk(sub, depth + 1);
            }
        }
        if (toc.SelectSingleNode(".//*[local-name()='ol' or local-name()='ul']") is { } top) Walk(top, 0);
    }

    private void ReadNcx(string text, string dir)
    {
        var doc = Xml(text);
        void Walk(XmlNode parent, int depth)
        {
            foreach (XmlNode p in parent.ChildNodes)
            {
                if (p.LocalName != "navPoint") continue;
                var label = p.SelectSingleNode("*[local-name()='navLabel']")?.InnerText ?? "";
                var src = p.SelectSingleNode("*[local-name()='content']/@src")?.Value;
                if (src != null) Add(Clean(label), Join(dir, src), depth);
                Walk(p, depth + 1);
            }
        }
        if (doc.SelectSingleNode("//*[local-name()='navMap']") is { } map) Walk(map, 0);
    }

    private void Add(string title, string target, int depth)
    {
        var hash = target.IndexOf('#');
        var path = hash >= 0 ? target[..hash] : target;
        var frag = hash >= 0 ? target[(hash + 1)..] : null;
        var ch = Chapters.FindIndex(c => c.Path == path);
        if (ch >= 0 && title.Length > 0) Contents.Add(new TocEntry { Title = Trim(title, 120), Chapter = ch, Fragment = frag, Depth = depth });
    }

    /// <summary>A file inside the book, as text (null when there's none).</summary>
    public string? Read(string path)
    {
        if (_zip == null) return null;
        var e = _zip.GetEntry(path) ?? _zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (e == null) return null;
        using var r = new StreamReader(e.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    /// <summary>A picture inside the book, as bytes (null when there's none).</summary>
    public byte[]? ReadBytes(string path)
    {
        if (_zip == null) return null;
        var e = _zip.GetEntry(path) ?? _zip.Entries.FirstOrDefault(x => string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (e == null || e.Length > 30_000_000) return null;
        using var s = e.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>A chapter as blocks to draw.</summary>
    public List<Block> ChapterBlocks(int index)
    {
        if (index < 0 || index >= Chapters.Count) return new();
        if (_text != null) return TextBlocks(_text);
        var path = Chapters[index].Path;
        var xhtml = Read(path);
        return xhtml == null ? new() { Para($"(This part of the book, {path}, is missing.)") } : Xhtml.ToBlocks(xhtml, Dir(path));
    }

    // ───────── plain text ─────────

    private void LoadText(string file)
    {
        _text = File.ReadAllText(file);
        var name = Path.GetFileNameWithoutExtension(file);
        Title = name;
        // Project Gutenberg's plain texts say what they are near the top
        var head = _text.Length > 4000 ? _text[..4000] : _text;
        if (Regex.Match(head, @"^Title:\s*(.+)$", RegexOptions.Multiline) is { Success: true } t) Title = t.Groups[1].Value.Trim();
        if (Regex.Match(head, @"^Author:\s*(.+)$", RegexOptions.Multiline) is { Success: true } a) Author = a.Groups[1].Value.Trim();
        Chapters.Add(new Chapter { Title = Title, Path = "" });
        Contents.Add(new TocEntry { Title = Title, Chapter = 0 });
    }

    /// <summary>Plain text: paragraphs between blank lines, their lines joined.</summary>
    public static List<Block> TextBlocks(string text)
    {
        var blocks = new List<Block>();
        foreach (var para in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
        {
            var t = Regex.Replace(para.Trim(), @"\s*\n\s*", " ");
            if (t.Length > 0) blocks.Add(Para(t));
        }
        return blocks;
    }

    private static Block Para(string text) { var b = new Block { Kind = BlockKind.Paragraph }; b.Runs.Add(new Run(text)); return b; }

    // ───────── helpers ─────────

    public static XmlDocument Xml(string text)
    {
        var doc = new XmlDocument { XmlResolver = null };
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var r = XmlReader.Create(new StringReader(Xhtml.Entities(text)), settings);
        doc.Load(r);
        return doc;
    }

    public static string Dir(string path) { var i = path.LastIndexOf('/'); return i < 0 ? "" : path[..(i + 1)]; }

    /// <summary>A link from a file in a folder, as a path from the book's root ("../" and "./" resolved).</summary>
    public static string Join(string dir, string href)
    {
        href = Uri.UnescapeDataString(href);
        var parts = new List<string>();
        foreach (var p in (href.StartsWith('/') ? href[1..] : dir + href).Split('/'))
        {
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != "." && p.Length > 0) parts.Add(p);
        }
        return string.Join('/', parts) + (href.EndsWith('/') ? "/" : "");
    }

    public static string Clean(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)].TrimEnd() + "…";

    public void Dispose() => _zip?.Dispose();
}
