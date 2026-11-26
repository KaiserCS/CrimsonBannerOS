// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using PeoplesArchive.Model;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>People's Archive (apps/archive): books read from EPUB and text, the sources' feeds and pages read from
/// samples, the shelf, and the network through a stand-in server (no network in tests).</summary>
public class ArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "archive-test-" + Guid.NewGuid().ToString("N"));
    public ArchiveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // a 1×1 PNG
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    private string Epub3()
    {
        var f = Path.Combine(_dir, "book.epub");
        Samples.WriteEpub(f, "A Test Book", "Ann Author", new[]
        {
            ("Opening", "<p>It was&nbsp;a  dark\n   night &mdash; <em>very</em> dark.</p><blockquote><p>A quoted line.</p></blockquote><hr/><p><img src=\"../images/cover.png\" alt=\"x\"/></p>"),
            ("Lists", "<ol><li>First</li><li>Second <strong>strong</strong></li></ol><ul><li>Dot</li></ul><pre>  kept   as is\nline two</pre><table><tr><th>A</th><td>B</td></tr></table>"),
        }, coverPng: Png);
        return f;
    }

    [Fact]
    public void An_EPUB_3_is_read_into_chapters_contents_and_blocks()
    {
        using var b = Book.Open(Epub3());
        Assert.Equal("A Test Book", b.Title);
        Assert.Equal("Ann Author", b.Author);
        Assert.Equal("en", b.Language);
        Assert.Equal("OEBPS/images/cover.png", b.CoverPath);
        Assert.NotNull(b.ReadBytes(b.CoverPath!));
        Assert.Equal(new[] { "Opening", "Lists" }, b.Chapters.Select(c => c.Title));
        Assert.Equal(new[] { "Opening", "Lists" }, b.Contents.Select(c => c.Title));

        var one = b.ChapterBlocks(0);
        Assert.Equal(BlockKind.Heading, one[0].Kind);
        Assert.Equal("Opening", one[0].Text);
        Assert.Equal("It was a dark night — very dark.", one[1].Text);       // entities, and spaces collapsed
        Assert.Contains(one[1].Runs, r => r.Italic && r.Text == "very");
        Assert.Equal(BlockKind.Quote, one[2].Kind);
        Assert.Equal(BlockKind.Rule, one[3].Kind);
        var img = Assert.Single(one, x => x.Kind == BlockKind.Image);
        Assert.Equal("OEBPS/images/cover.png", img.Image);                            // ../ resolved from text/

        var two = b.ChapterBlocks(1);
        Assert.Equal(new[] { "1. First", "2. Second strong", "• Dot" }, two.Where(x => x.Kind == BlockKind.ListItem).Select(x => x.Text));
        Assert.Contains(two.Single(x => x.Text.StartsWith("2.")).Runs, r => r.Bold && r.Text == "strong");
        Assert.Equal("  kept   as is\nline two", two.Single(x => x.Kind == BlockKind.Preformatted).Text);
        Assert.Equal("A   ·   B", two.Last().Text);
    }

    [Fact]
    public void An_EPUB_2_with_an_NCX_and_its_files_in_folders_is_read_too()
    {
        var f = Path.Combine(_dir, "old.epub");
        using (var zip = ZipFile.Open(f, ZipArchiveMode.Create))
        {
            void Put(string p, string t) { using var w = new StreamWriter(zip.CreateEntry(p).Open()); w.Write(t); }
            Put("mimetype", "application/epub+zip");
            Put("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Put("OEBPS/content.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Old  Book</dc:title><dc:creator>One</dc:creator><dc:creator>Two</dc:creator><meta name=\"cover\" content=\"img\"/></metadata>" +
                "<manifest><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/><item id=\"a\" href=\"Text/a.html\" media-type=\"application/xhtml+xml\"/><item id=\"b\" href=\"Text/b%20two.html\" media-type=\"application/xhtml+xml\"/><item id=\"img\" href=\"Images/c.png\" media-type=\"image/png\"/></manifest>" +
                "<spine toc=\"ncx\"><itemref idref=\"a\"/><itemref idref=\"b\"/></spine></package>");
            Put("OEBPS/toc.ncx", "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\"><navMap><navPoint><navLabel><text>The A</text></navLabel><content src=\"Text/a.html\"/><navPoint><navLabel><text>Inside A</text></navLabel><content src=\"Text/a.html#deep\"/></navPoint></navPoint><navPoint><navLabel><text>The B</text></navLabel><content src=\"Text/b%20two.html\"/></navPoint></navMap></ncx>");
            Put("OEBPS/Text/a.html", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><p>One.</p><h3 id=\"deep\">Deep</h3><p>Two&hellip;</p></body></html>");
            Put("OEBPS/Text/b two.html", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><div>Loose text in a div.<br/>After a break.</div></body></html>");
            using (var s = zip.CreateEntry("OEBPS/Images/c.png").Open()) s.Write(Png);
        }
        using var b = Book.Open(f);
        Assert.Equal("Old Book", b.Title);
        Assert.Equal("One, Two", b.Author);
        Assert.Equal("OEBPS/Images/c.png", b.CoverPath);
        Assert.Equal(new[] { "The A", "The B" }, b.Chapters.Select(c => c.Title));
        var inside = b.Contents.Single(c => c.Title == "Inside A");
        Assert.Equal((0, "deep", 1), (inside.Chapter, inside.Fragment, inside.Depth));
        Assert.Equal("deep", b.ChapterBlocks(0).Single(x => x.Kind == BlockKind.Heading).Anchor);
        Assert.Equal("Two…", b.ChapterBlocks(0).Last().Text);
        Assert.Equal("Loose text in a div.\nAfter a break.", b.ChapterBlocks(1).Single().Text);
    }

    [Fact]
    public void A_plain_text_book_is_paragraphs_and_a_Gutenberg_header_names_it()
    {
        var f = Path.Combine(_dir, "pg.txt");
        File.WriteAllText(f, "Title: The Text\nAuthor: Some Writer\n\nFirst para\ncontinues here.\n\n\nSecond.\n");
        using var b = Book.Open(f);
        Assert.Equal("The Text", b.Title);
        Assert.Equal("Some Writer", b.Author);
        Assert.Equal(new[] { "Title: The Text Author: Some Writer", "First para continues here.", "Second." }, b.ChapterBlocks(0).Select(x => x.Text));
    }

    private const string Feed = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:opds="http://opds-spec.org/2010/catalog">
          <id>https://www.gutenberg.org/ebooks/search.opds/?query=marx</id>
          <title>Books: marx</title>
          <link rel="search" type="application/opensearchdescription+xml" href="/catalog/osd-books.xml"/>
          <link rel="next" type="application/atom+xml;profile=opds-catalog" href="/ebooks/search.opds/?query=marx&amp;start_index=26"/>
          <entry>
            <title>Sort Alphabetically by Title</title>
            <link type="application/atom+xml;profile=opds-catalog" rel="http://opds-spec.org/sort/new" href="/ebooks/search.opds/?query=marx&amp;sort_order=title"/>
          </entry>
          <entry>
            <title>The Communist Manifesto</title>
            <author><name>Marx, Karl</name></author>
            <author><name>Engels, Friedrich</name></author>
            <link type="application/atom+xml;profile=opds-catalog" rel="subsection" href="/ebooks/61.opds"/>
          </entry>
          <entry>
            <title>Value, Price and Profit</title>
            <author><name>Marx, Karl</name></author>
            <dcterms:language>en</dcterms:language>
            <dcterms:rights>Public domain in the USA.</dcterms:rights>
            <summary>An address of 1865.</summary>
            <link rel="http://opds-spec.org/acquisition" type="application/epub+zip" href="/ebooks/4041.epub.noimages"/>
            <link rel="http://opds-spec.org/acquisition" type="application/epub+zip" href="/ebooks/4041.epub3.images"/>
            <link rel="http://opds-spec.org/acquisition" type="application/x-mobipocket-ebook" href="/ebooks/4041.kf8.images"/>
            <link rel="alternate" type="text/html" href="/ebooks/4041"/>
          </entry>
        </feed>
        """;

    [Fact]
    public void An_OPDS_feed_gives_its_books_its_next_page_and_how_to_search()
    {
        var f = Opds.Parse(Feed, "https://www.gutenberg.org/ebooks/search.opds/?query=marx", "PG", "pd");
        Assert.Equal("https://www.gutenberg.org/ebooks/search.opds/?query=marx&start_index=26", f.Next);
        Assert.Equal("https://www.gutenberg.org/catalog/osd-books.xml", f.SearchDescription);
        Assert.Equal(2, f.Entries.Count);                                     // the sort order isn't a book
        var m = f.Entries[0];
        Assert.Equal(("The Communist Manifesto", "Marx, Karl, Engels, Friedrich"), (m.Title, m.Author));
        Assert.Null(m.Download);
        Assert.Equal("https://www.gutenberg.org/ebooks/61.opds", m.Feed);
        Assert.Equal("https://www.gutenberg.org/ebooks/61", m.Page);
        var v = f.Entries[1];
        Assert.Equal("https://www.gutenberg.org/ebooks/4041.epub3.images", v.Download);   // the newest EPUB, with pictures
        Assert.Equal("https://www.gutenberg.org/ebooks/4041", v.Page);
        Assert.Equal(("en", "Public domain in the USA.", "An address of 1865."), (v.Language, v.Licence, v.Summary));

        var osd = """<OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/"><Url type="text/html" template="/x?q={searchTerms}"/><Url type="application/atom+xml" template="/ebooks/search.opds/?query={searchTerms}"/></OpenSearchDescription>""";
        Assert.Equal("https://www.gutenberg.org/ebooks/search.opds/?query={searchTerms}", Opds.SearchTemplateFrom(osd, "https://www.gutenberg.org/catalog/osd-books.xml"));
    }

    [Fact]
    public void Wikisource_search_lists_whole_works_first_with_their_EPUB_export()
    {
        var src = new WikisourceSource("en");
        var json = """{"continue":{"sroffset":30,"continue":"-||"},"query":{"search":[{"title":"The Iron Heel/Chapter 1","snippet":"the <span class=\"searchmatch\">Iron</span> Heel"},{"title":"The Iron Heel","snippet":"A novel &amp; more"}]}}""";
        var r = WikisourceSource.Parse(json, src);
        Assert.Equal(new[] { "The Iron Heel", "The Iron Heel/Chapter 1" }, r.Books.Select(b => b.Title));
        Assert.Equal("A novel & more", r.Books[0].Summary);
        Assert.Equal("https://ws-export.wmcloud.org/?format=epub-3&lang=en&page=The_Iron_Heel", r.Books[0].Download);
        Assert.Equal("https://en.wikisource.org/wiki/The_Iron_Heel/Chapter_1", r.Books[1].Page);
        Assert.Equal("30", r.Next);
        Assert.Contains("srsearch=iron%20heel", src.SearchUrl("iron heel", 0));
    }

    [Fact]
    public void The_Marxists_Internet_Archive_index_gives_each_EPUB_once_with_its_title_and_writer()
    {
        var html = """
            <tr><td><a href="../archive/althusser/index.htm">Louis Althusser</a></td>
            <td>For Marx <a href="althusser/For_Marx_-_Louis_Althusser.epub">epub</a> <a href="althusser/For_Marx_-_Louis_Althusser.pdf">pdf</a></td></tr>
            <tr><td>Bukharin</td><td>ABC <a href='bukharin/abc-of-communism.epub'>epub</a> <a href="bukharin/abc-of-communism.epub">again</a></td></tr>
            """;
        var list = MiaSource.Parse(html, MiaSource.IndexUrl, null);
        Assert.Equal(2, list.Count);
        var a = list.Single(l => l.Title == "For Marx");
        Assert.Equal("Louis Althusser", a.Author);
        Assert.Equal("https://www.marxists.org/ebooks/althusser/For_Marx_-_Louis_Althusser.epub", a.Download);
        var b = list.Single(l => l.Author == "Bukharin");
        Assert.Equal("abc-of-communism", b.Title);
    }

    [Fact]
    public void The_shelf_takes_books_finds_them_again_and_forgets_ones_whose_file_is_gone()
    {
        var lib = new Library(Path.Combine(_dir, "shelf"));
        lib.Load();
        var b = lib.Add(Epub3(), "Project Gutenberg", "https://www.gutenberg.org/ebooks/1", "pd");
        Assert.Equal("A Test Book - Ann Author.epub", b.File);
        Assert.True(File.Exists(lib.PathOf(b)));
        Assert.Same(b, lib.FromUrl("https://www.gutenberg.org/ebooks/1"));
        Assert.Single(lib.Search("ann"));
        Assert.Empty(lib.Search("nobody"));
        // a book put in the folder by hand joins; one whose file went leaves
        File.Copy(Epub3(), Path.Combine(lib.Folder, "dropped.epub"));
        File.Delete(lib.PathOf(b));
        var again = new Library(lib.Folder);
        again.Load();
        var only = Assert.Single(again.Books);
        Assert.Equal(("dropped.epub", "This computer", 2), (only.File, only.Source, only.Chapters));
        again.Remove(only);
        Assert.False(File.Exists(Path.Combine(lib.Folder, "dropped.epub")));
    }

    private sealed class Stand : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Asked = new();
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
        public Stand(Func<HttpRequestMessage, HttpResponseMessage> answer) { _answer = answer; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Asked.Add(r); return Task.FromResult(_answer(r)); }
    }

    [Fact]
    public async Task A_download_must_be_a_book_not_a_web_page_and_says_who_is_asking()
    {
        var epub = File.ReadAllBytes(Epub3());
        var stand = new Stand(r => r.RequestUri!.AbsolutePath.EndsWith(".epub")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(epub) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<!DOCTYPE html><html>Busy</html>", Encoding.UTF8, "text/html") });
        using var net = new Net(stand);
        var ok = Path.Combine(_dir, "ok.epub");
        await net.Download("https://example.org/a.epub", ok, null, CancellationToken.None, user: "reader@example.org");
        Assert.True(Book.IsZip(ok));
        Assert.Contains("PeoplesArchive/1.0", stand.Asked[0].Headers.UserAgent.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("reader@example.org:")), stand.Asked[0].Headers.Authorization!.ToString());
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => net.Download("https://example.org/page", Path.Combine(_dir, "bad"), null, CancellationToken.None));
        Assert.Contains("web page", e.Message);
    }

    [Fact]
    public async Task An_OPDS_source_searches_with_its_template_and_finds_a_book_file_through_the_book_feed()
    {
        var stand = new Stand(r =>
        {
            var u = r.RequestUri!.ToString();
            var body = u.Contains("query=marx") ? Feed
                : u.EndsWith("/ebooks/61.opds") ? Feed.Replace("4041", "61")
                : "<feed xmlns=\"http://www.w3.org/2005/Atom\"/>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var net = new Net(stand);
        var pg = Sources.Gutenberg();
        var r = await pg.Search(net, "marx", null, CancellationToken.None);
        Assert.Equal("https://www.gutenberg.org/ebooks/search.opds/?query=marx", stand.Asked[0].RequestUri!.ToString());
        Assert.Equal(2, r.Books.Count);
        Assert.NotNull(r.Next);
        var file = await pg.DownloadFor(net, r.Books[0], CancellationToken.None);
        Assert.Equal("https://www.gutenberg.org/ebooks/61.epub3.images", file);
    }

    [Fact]
    public void Added_catalogues_are_kept_and_join_the_sources()
    {
        var cfg = Path.Combine(_dir, "cfg");
        Sources.SaveCatalogues(new List<SavedCatalogue> { new() { Name = "Standard Ebooks", Url = "https://standardebooks.org/feeds/opds", User = "me@example.org" }, new() { Name = "Broken", Url = "not a url" } }, cfg);
        var all = Sources.All("fr", cfg);
        Assert.Equal(new[] { "Project Gutenberg", "Wikisource", "Marxists Internet Archive", "Standard Ebooks" }, all.Select(s => s.Name));
        Assert.Equal("fr", ((WikisourceSource)all[1]).Language);
        var se = (OpdsSource)all[3];
        Assert.Equal(("me@example.org", true), (se.User, se.Custom));
    }
}
