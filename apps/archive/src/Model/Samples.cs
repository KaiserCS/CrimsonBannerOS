// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Security;
using System.Text;

namespace PeoplesArchive.Model;

/// <summary>
/// Small EPUBs for the tests and the screenshots, made here so neither needs the network: the openings of three
/// public-domain books (the 1888 English Manifesto, Pride and Prejudice, The Iron Heel).
/// </summary>
public static class Samples
{
    /// <summary>Writes an EPUB 3: its chapters (title, XHTML body), a navigation document, and a cover if given.</summary>
    public static void WriteEpub(string file, string title, string author, IReadOnlyList<(string Title, string Body)> chapters, string language = "en", byte[]? coverPng = null)
    {
        if (File.Exists(file)) File.Delete(file);
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        // the mimetype first and stored, as EPUB asks
        var mt = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mt.Open(), new UTF8Encoding(false))) w.Write("application/epub+zip");
        void Put(string path, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Put("META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        var nav = new StringBuilder();
        for (int i = 0; i < chapters.Count; i++)
        {
            var (ct, body) = chapters[i];
            var name = $"text/chapter-{i + 1}.xhtml";
            Put("OEBPS/" + name, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE html>
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" xml:lang="{language}">
                <head><title>{SecurityElement.Escape(ct)}</title></head>
                <body><section id="c{i + 1}"><h2>{SecurityElement.Escape(ct)}</h2>
                {body}
                </section></body></html>
                """);
            manifest.Append($"""<item id="c{i + 1}" href="{name}" media-type="application/xhtml+xml"/>""").Append('\n');
            spine.Append($"""<itemref idref="c{i + 1}"/>""").Append('\n');
            nav.Append($"""<li><a href="{name}">{SecurityElement.Escape(ct)}</a></li>""").Append('\n');
        }
        Put("OEBPS/nav.xhtml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head><title>Contents</title></head>
            <body><nav epub:type="toc"><ol>
            {nav}</ol></nav></body></html>
            """);
        if (coverPng != null)
        {
            using var s = zip.CreateEntry("OEBPS/images/cover.png").Open();
            s.Write(coverPng);
            manifest.Append("""<item id="cover" href="images/cover.png" media-type="image/png" properties="cover-image"/>""").Append('\n');
        }
        Put("OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="id">urn:peoples-archive:sample:{SecurityElement.Escape(title)}</dc:identifier>
                <dc:title>{SecurityElement.Escape(title)}</dc:title>
                <dc:creator>{SecurityElement.Escape(author)}</dc:creator>
                <dc:language>{language}</dc:language>
                <dc:rights>Public domain</dc:rights>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            {manifest}  </manifest>
              <spine>
            {spine}  </spine>
            </package>
            """);
    }

    /// <summary>A shelf of three books in a folder (for the screenshots), one of them part-read.</summary>
    public static Library Shelf(string folder)
    {
        Directory.CreateDirectory(folder);
        var lib = new Library(folder);
        lib.Load();
        var tmp = Path.Combine(folder, ".making");
        Directory.CreateDirectory(tmp);
        void Make(string title, string author, string source, string url, string licence, params (string, string)[] chapters)
        {
            var f = Path.Combine(tmp, Library.Safe(title) + ".epub");
            WriteEpub(f, title, author, chapters);
            lib.Add(f, source, url, licence);
        }
        Make("Manifesto of the Communist Party", "Karl Marx, Friedrich Engels", "Marxists Internet Archive",
            "https://www.marxists.org/ebooks/marx/manifesto.epub", "Free to read and share, as the Marxists Internet Archive marks each work",
            ("Preamble", """
                <p>A spectre is haunting Europe — the spectre of communism. All the powers of old Europe have entered into a holy alliance to exorcise this spectre: Pope and Tsar, Metternich and Guizot, French Radicals and German police-spies.</p>
                <p>Where is the party in opposition that has not been decried as communistic by its opponents in power? Where is the opposition that has not hurled back the branding reproach of communism, against the more advanced opposition parties, as well as against its reactionary adversaries?</p>
                <p>Two things result from this fact:</p>
                <ol><li>Communism is already acknowledged by all European powers to be itself a power.</li>
                <li>It is high time that Communists should openly, in the face of the whole world, publish their views, their aims, their tendencies, and meet this nursery tale of the spectre of communism with a manifesto of the party itself.</li></ol>
                """),
            ("I. Bourgeois and Proletarians", """
                <p>The history of all hitherto existing society is the history of class struggles.</p>
                <p>Freeman and slave, patrician and plebeian, lord and serf, guild-master and journeyman, in a word, oppressor and oppressed, stood in constant opposition to one another, carried on an uninterrupted, now hidden, now open fight, a fight that each time ended, either in a revolutionary reconstitution of society at large, or in the common ruin of the contending classes.</p>
                <p>In the earlier epochs of history, we find almost everywhere a complicated arrangement of society into various orders, a manifold gradation of social rank. In ancient Rome we have patricians, knights, plebeians, slaves; in the Middle Ages, feudal lords, vassals, guild-masters, journeymen, apprentices, serfs; in almost all of these classes, again, subordinate gradations.</p>
                <blockquote><p>The modern bourgeois society that has sprouted from the ruins of feudal society has not done away with class antagonisms. It has but established new classes, new conditions of oppression, new forms of struggle in place of the old ones.</p></blockquote>
                """));
        Make("Pride and Prejudice", "Jane Austen", "Project Gutenberg", "https://www.gutenberg.org/ebooks/1342",
            "Public domain in the USA (check your country's law), under Project Gutenberg's terms",
            ("Chapter I", """
                <p>It is a truth universally acknowledged, that a single man in possession of a good fortune must be in want of a wife.</p>
                <p>However little known the feelings or views of such a man may be on his first entering a neighbourhood, this truth is so well fixed in the minds of the surrounding families, that he is considered as the rightful property of some one or other of their daughters.</p>
                <p>“My dear Mr. Bennet,” said his lady to him one day, “have you heard that Netherfield Park is let at last?”</p>
                """));
        Make("The Iron Heel", "Jack London", "Wikisource", "https://en.wikisource.org/wiki/The_Iron_Heel",
            "Public domain or CC BY-SA, as marked on Wikisource",
            ("Foreword", """
                <p>It cannot be said that the Everhard Manuscript is an important historical document. To the historian it bristles with errors — not errors of fact, but errors of interpretation.</p>
                """));
        Directory.Delete(tmp, true);
        var m = lib.Books.First(b => b.Title.StartsWith("Manifesto"));
        m.Chapter = 1; m.Position = 0.4; m.LastRead = DateTime.UtcNow;
        lib.Save();
        return lib;
    }
}
