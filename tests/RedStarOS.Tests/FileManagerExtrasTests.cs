using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Archives, batch rename, Recent (xbel), network names — the services behind Files' newer features.</summary>
public class FileManagerExtrasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rs-extras-" + Guid.NewGuid().ToString("N"));

    public FileManagerExtrasTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeFolder()
    {
        var a = Path.Combine(_root, "src", "photos");
        Directory.CreateDirectory(Path.Combine(a, "inner"));
        File.WriteAllText(Path.Combine(a, "one.txt"), "one");
        File.WriteAllText(Path.Combine(a, "inner", "two.txt"), "two");
        return a;
    }

    [Fact]
    public void Archive_names()
    {
        Assert.True(Archives.IsArchive("/x/a.zip"));
        Assert.True(Archives.IsArchive("/x/a.TAR.GZ"));
        Assert.True(Archives.IsArchive("/x/a.7z"));
        Assert.False(Archives.IsArchive("/x/a.txt"));
        Assert.Equal("photos", Archives.BaseName("/x/photos.tar.gz"));
        Assert.Equal("photos.v2", Archives.BaseName("/x/photos.v2.zip"));
    }

    [Fact]
    public async Task Zip_round_trip_keeps_one_top_folder()
    {
        var folder = MakeFolder();
        var zip = Path.Combine(_root, "photos.zip");
        await Archives.Compress(new[] { folder }, zip, CancellationToken.None);
        Assert.True(File.Exists(zip));

        var outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(outDir);
        var made = await Archives.Extract(zip, outDir, CancellationToken.None);
        Assert.Equal(Path.Combine(outDir, "photos"), made);
        Assert.Equal("one", File.ReadAllText(Path.Combine(made, "one.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(made, "inner", "two.txt")));
        // nothing temporary left behind
        Assert.Single(Directory.GetFileSystemEntries(outDir));
    }

    [Fact]
    public async Task Several_top_items_get_a_folder_named_after_the_archive()
    {
        var a = Path.Combine(_root, "a.txt"); File.WriteAllText(a, "a");
        var b = Path.Combine(_root, "b.txt"); File.WriteAllText(b, "b");
        var zip = Path.Combine(_root, "letters.zip");
        await Archives.Compress(new[] { a, b }, zip, CancellationToken.None);
        var outDir = Path.Combine(_root, "out2");
        Directory.CreateDirectory(outDir);
        var made = await Archives.Extract(zip, outDir, CancellationToken.None);
        Assert.Equal(Path.Combine(outDir, "letters"), made);
        Assert.True(File.Exists(Path.Combine(made, "a.txt")));
        Assert.True(File.Exists(Path.Combine(made, "b.txt")));
    }

    [Fact]
    public async Task Tar_gz_round_trip_when_tar_is_installed()
    {
        if (ProcessUtil.Which("tar") == null) return;
        var folder = MakeFolder();
        var tgz = Path.Combine(_root, "photos.tar.gz");
        await Archives.Compress(new[] { folder }, tgz, CancellationToken.None);
        var outDir = Path.Combine(_root, "out3");
        Directory.CreateDirectory(outDir);
        var made = await Archives.Extract(tgz, outDir, CancellationToken.None);
        Assert.Equal("two", File.ReadAllText(Path.Combine(made, "inner", "two.txt")));
    }

    [Fact]
    public void Batch_rename_numbering_and_case()
    {
        Assert.Equal("Trip 007", BatchRename.Number("Trip ###", 7, "x"));
        Assert.Equal("pic 03", BatchRename.Number("* ##", 3, "pic"));
        Assert.Equal("Holiday 12", BatchRename.Number("Holiday", 12, "x"));
        Assert.Equal("Hello World-Foo", BatchRename.ChangeCase("hello wORLD-foo", BatchRename.CaseKind.Title));
        Assert.Equal(("archive", ".tar.gz"), BatchRename.Split("archive.tar.gz"));
        Assert.Equal((".bashrc", ""), BatchRename.Split(".bashrc"));
    }

    [Fact]
    public void Batch_rename_preview_keeps_extensions_and_flags_clashes()
    {
        var a = Path.Combine(_root, "IMG_1.jpg"); File.WriteAllText(a, "1");
        var b = Path.Combine(_root, "IMG_2.jpg"); File.WriteAllText(b, "2");
        File.WriteAllText(Path.Combine(_root, "Beach 1.jpg"), "taken");

        var o = new BatchRename.Options { Mode = BatchRename.Mode.Number, Pattern = "Beach #", Start = 1 };
        var items = BatchRename.Preview(new[] { a, b }, o);
        Assert.Equal("Beach 1.jpg", items[0].NewName);
        Assert.NotNull(items[0].Problem);                 // taken by a file that isn't being renamed
        Assert.Equal("Beach 2.jpg", items[1].NewName);
        Assert.Null(items[1].Problem);

        var same = BatchRename.Preview(new[] { a, b }, new BatchRename.Options { Mode = BatchRename.Mode.Replace, Find = "IMG_", ReplaceWith = "" , Regex = false });
        Assert.Equal(new[] { "1.jpg", "2.jpg" }, same.Select(i => i.NewName));

        var bad = BatchRename.Preview(new[] { a }, new BatchRename.Options { Mode = BatchRename.Mode.Replace, Find = "(", Regex = true });
        Assert.NotNull(bad[0].Problem);
    }

    [Fact]
    public void Batch_rename_can_swap_names_and_undo()
    {
        var a = Path.Combine(_root, "a.txt"); File.WriteAllText(a, "A");
        var b = Path.Combine(_root, "b.txt"); File.WriteAllText(b, "B");
        var items = new List<BatchRename.Item> { new(a, "b.txt", null), new(b, "a.txt", null) };
        var errors = new List<string>();
        var done = BatchRename.Apply(items, errors);
        Assert.Empty(errors);
        Assert.Equal(2, done.Count);
        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A", File.ReadAllText(b));

        BatchRename.Revert(done);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void Recent_files_from_xbel()
    {
        var doc = Path.Combine(_root, "report.odt");
        File.WriteAllText(doc, "x");
        var xbel = Path.Combine(_root, "recently-used.xbel");
        File.WriteAllText(xbel, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <xbel version="1.0" xmlns:bookmark="http://www.freedesktop.org/standards/desktop-bookmarks">
              <bookmark href="{new Uri(doc).AbsoluteUri}" added="2026-01-02T10:00:00Z" modified="2026-01-03T10:00:00Z" visited="2026-01-04T10:00:00Z" />
              <bookmark href="https://example.com/" added="2026-01-02T10:00:00Z" />
            </xbel>
            """);
        var list = Places.ReadXbel(xbel);
        var hit = Assert.Single(list);
        Assert.Equal(doc, hit.Path);
        Assert.Equal(2026, hit.When.Year);
    }

    [Fact]
    public void Network_names()
    {
        var (label, kind, uri) = RemoteMounts.ParseGvfsName("smb-share:server=nas,share=photos");
        Assert.Equal("photos on nas", label);
        Assert.Equal("smb", kind);
        Assert.Equal("smb://nas/photos", uri);

        (label, kind, _) = RemoteMounts.ParseGvfsName("sftp:host=example.org,user=kim");
        Assert.Equal("kim@example.org", label);
        Assert.Equal("sftp", kind);

        Assert.Equal(new[] { "gdrive", "box" }, RemoteMounts.ParseRemotes("gdrive:\nbox:\n\n"));
    }

    [Fact]
    public void Folders_have_the_directory_type()
    {
        Assert.Equal("inode/directory", DesktopEntries.MimeFor(_root));
        Assert.Null(FileOpener.RedStarAppFor("/x/unknown.qqq"));
        Assert.Equal("Image Viewer", FileOpener.RedStarAppFor("/x/a.png"));
    }

    [Fact]
    public void Dialog_filters_match_and_name_the_extension()
    {
        var docs = new RedStarOS.Views.FileChooserWindow.Filter("Documents", new[] { "*.docx", "*.txt" });
        Assert.True(docs.Matches("Report.DOCX"));
        Assert.False(docs.Matches("photo.png"));
        Assert.Equal(".docx", docs.Extension);
        var all = new RedStarOS.Views.FileChooserWindow.Filter("All files", new[] { "*" });
        Assert.True(all.All);
        Assert.True(all.Matches("anything"));
        Assert.Null(all.Extension);
    }

    [Fact]
    public void Dialogs_start_in_the_suggested_folder_or_the_one_last_open_in_Files()
    {
        Assert.Equal(_root, RedStarOS.Views.FileDialogs.StartFolder(_root));
        var file = Path.Combine(_root, "x.txt");
        File.WriteAllText(file, "x");
        Assert.Equal(_root, RedStarOS.Views.FileDialogs.StartFolder(file));
        var before = SettingsStore.Current.LastFolder;
        try
        {
            SettingsStore.Current.LastFolder = _root;
            Assert.Equal(_root, RedStarOS.Views.FileDialogs.StartFolder(null));
        }
        finally { SettingsStore.Current.LastFolder = before; }
    }
}
