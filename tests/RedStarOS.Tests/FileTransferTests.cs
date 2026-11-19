using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

[Collection("environment")]     // uses its own XDG_DATA_HOME (Trash)
public class FileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rs-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private string A => Path.Combine(_root, "a");
    private string B => Path.Combine(_root, "b");

    public FileTransferTests()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(_root, "data"));
        Directory.CreateDirectory(A); Directory.CreateDirectory(B);
        File.WriteAllText(Path.Combine(A, "one.txt"), "one");
        Directory.CreateDirectory(Path.Combine(A, "folder", "inner"));
        File.WriteAllBytes(Path.Combine(A, "folder", "inner", "big.bin"), new byte[3_000_000]);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _oldData);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Task<FileTransfer.Result> Run(IReadOnlyList<string> s, string d, bool move, FileTransfer.Conflict c = FileTransfer.Conflict.KeepBoth, IProgress<FileTransfer.Progress>? p = null) =>
        FileTransfer.Run(s, d, move, c, p, CancellationToken.None);

    [Fact]
    public async Task Copies_folders_with_progress()
    {
        var reports = new List<FileTransfer.Progress>();
        var r = await Run(new[] { Path.Combine(A, "folder"), Path.Combine(A, "one.txt") }, B, false, p: new SyncProgress(reports));
        Assert.Empty(r.Errors);
        Assert.Equal(3_000_000, new FileInfo(Path.Combine(B, "folder", "inner", "big.bin")).Length);
        Assert.True(File.Exists(Path.Combine(A, "one.txt")));                    // copy keeps the original
        Assert.Contains(reports, x => x.BytesTotal >= 3_000_003 && x.BytesDone > 0);
    }

    [Fact]
    public async Task Copying_into_the_same_folder_duplicates()
    {
        var r = await Run(new[] { Path.Combine(A, "one.txt") }, A, false);
        Assert.Single(r.Done);
        Assert.NotEqual(Path.Combine(A, "one.txt"), r.Done[0].To);
        Assert.Equal("one", File.ReadAllText(r.Done[0].To));
    }

    [Fact]
    public async Task Replace_sends_the_old_one_to_the_Trash_and_undo_brings_it_back()
    {
        File.WriteAllText(Path.Combine(B, "one.txt"), "old");
        Assert.Single(FileTransfer.Clashes(new[] { Path.Combine(A, "one.txt") }, B, false));
        var r = await Run(new[] { Path.Combine(A, "one.txt") }, B, false, FileTransfer.Conflict.Replace);
        Assert.Equal("one", File.ReadAllText(Path.Combine(B, "one.txt")));
        Assert.Single(r.ReplacedInTrash);
        FileTransfer.RecordTransfer(r, false, B);
        FileTransfer.Undo();
        Assert.Equal("old", File.ReadAllText(Path.Combine(B, "one.txt")));
    }

    [Fact]
    public async Task Keep_both_and_skip()
    {
        File.WriteAllText(Path.Combine(B, "one.txt"), "old");
        var both = await Run(new[] { Path.Combine(A, "one.txt") }, B, false, FileTransfer.Conflict.KeepBoth);
        Assert.NotEqual(Path.Combine(B, "one.txt"), both.Done[0].To);
        var skip = await Run(new[] { Path.Combine(A, "one.txt") }, B, false, FileTransfer.Conflict.Skip);
        Assert.Empty(skip.Done);
        Assert.Equal("old", File.ReadAllText(Path.Combine(B, "one.txt")));
    }

    [Fact]
    public async Task Moves_and_undo_moves_back()
    {
        var r = await Run(new[] { Path.Combine(A, "folder") }, B, true);
        Assert.False(Directory.Exists(Path.Combine(A, "folder")));
        Assert.True(File.Exists(Path.Combine(B, "folder", "inner", "big.bin")));
        FileTransfer.RecordTransfer(r, true, B);
        Assert.StartsWith("Move", FileTransfer.UndoLabel);
        FileTransfer.Undo();
        Assert.True(File.Exists(Path.Combine(A, "folder", "inner", "big.bin")));
        Assert.False(Directory.Exists(Path.Combine(B, "folder")));
    }

    [Fact]
    public async Task A_folder_cant_go_inside_itself()
    {
        var r = await Run(new[] { Path.Combine(A, "folder") }, Path.Combine(A, "folder", "inner"), false);
        Assert.Single(r.Errors);
        Assert.False(Directory.Exists(Path.Combine(A, "folder", "inner", "folder")));
    }

    private sealed class SyncProgress(List<FileTransfer.Progress> into) : IProgress<FileTransfer.Progress>
    {
        public void Report(FileTransfer.Progress value) { lock (into) into.Add(value); }
    }
}
