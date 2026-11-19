using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

[Collection("environment")]     // changes XDG_DATA_HOME: don't run alongside other tests that read it
public class FileManagerTests
{
    [Fact]
    public void Trash_restores_items_where_they_were()
    {
        // a private Trash and folder: never touch the real user's Trash
        var sandbox = Path.Combine(Path.GetTempPath(), "rs-trash-test-" + Guid.NewGuid().ToString("N"));
        var oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(sandbox, "data"));
        try { TrashRoundTrip(Path.Combine(sandbox, "docs")); }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldData);
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static void TrashRoundTrip(string dir)
    {
        Assert.StartsWith(Path.GetTempPath(), FileOps.TrashFolder);          // really sandboxed
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "notes 1.txt");
        File.WriteAllText(file, "hello");

        FileOps.MoveToTrash(file);
        Assert.False(File.Exists(file));
        var stored = Path.GetFileName(Directory.GetFiles(FileOps.TrashFolder).Single(f => Path.GetFileName(f).StartsWith("notes 1.txt")));
        Assert.Equal(file, FileOps.TrashOrigin(stored).Origin);              // spaces survive the URL encoding
        Assert.True(FileOps.TrashCount() >= 1);

        var back = FileOps.RestoreFromTrash(stored);
        Assert.Equal(file, back);
        Assert.Equal("hello", File.ReadAllText(file));
        Assert.False(File.Exists(Path.Combine(FileOps.TrashFolder, stored)));

        // restoring onto a name that is taken again keeps both
        FileOps.MoveToTrash(file);
        File.WriteAllText(file, "new one");
        stored = Path.GetFileName(Directory.GetFiles(FileOps.TrashFolder).First(f => Path.GetFileName(f).StartsWith("notes 1.txt")));
        var second = FileOps.RestoreFromTrash(stored);
        Assert.NotEqual(file, second);
        Assert.Equal("hello", File.ReadAllText(second));

        FileOps.MoveToTrash(second);
        FileOps.EmptyTrash();
        Assert.Equal(0, FileOps.TrashCount());
    }

    [Theory]
    [InlineData("photo.JPG", "image", "JPG image")]
    [InlineData("report.pdf", "document", "PDF document")]
    [InlineData("song.flac", "music", "FLAC audio")]
    [InlineData("backup.tar", "archive", "TAR archive")]
    [InlineData("main.rs", "code", "RS source")]
    [InlineData("budget.xlsx", "sheet", "Spreadsheet")]
    [InlineData("README", "file", "File")]
    public void Files_get_a_kind_and_an_icon(string name, string icon, string kind)
    {
        Assert.Equal(icon, FileOps.IconName(name, false));
        Assert.Equal(kind, FileOps.KindOf(name, false));
        Assert.Equal("folder", FileOps.IconName("x", true));
    }
}
