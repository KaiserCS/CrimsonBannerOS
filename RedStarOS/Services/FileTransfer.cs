using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Copying and moving with progress, cancelling, name conflicts and undo — used by Files.
///  • Moves on the same drive are renames (instant); across drives the data is copied, then the original removed.
///  • A name that already exists is replaced, kept as "name (2)", or skipped — the caller decides (one question).
///    A replaced item goes to the Trash, not away for good, so Undo can bring it back.
///  • Every finished operation can be undone (<see cref="Undo"/>): moves move back, copies and new items go to the
///    Trash, trashed items are restored, renames are reverted.
/// </summary>
public static class FileTransfer
{
    public enum Conflict { Replace, KeepBoth, Skip }

    public sealed class Progress
    {
        public long BytesDone, BytesTotal;
        public int ItemsDone, ItemsTotal;
        public string Current = "";
        public double Fraction => BytesTotal > 0 ? Math.Min(1, (double)BytesDone / BytesTotal) : ItemsTotal > 0 ? (double)ItemsDone / ItemsTotal : 0;
    }

    public sealed class Result
    {
        public readonly List<(string From, string To)> Done = new();
        public readonly List<string> Errors = new();
        public readonly List<string> ReplacedInTrash = new();
        public bool Cancelled;
    }

    /// <summary>What an undoable step did.</summary>
    public sealed record UndoStep(string Label, Action Revert);

    private static readonly Stack<UndoStep> UndoStack = new();
    public static event Action? UndoChanged;

    public static string? UndoLabel => UndoStack.Count > 0 ? UndoStack.Peek().Label : null;

    public static void Record(string label, Action revert)
    {
        UndoStack.Push(new UndoStep(label, revert));
        while (UndoStack.Count > 30) { var keep = UndoStack.Take(30).Reverse().ToList(); UndoStack.Clear(); foreach (var k in keep) UndoStack.Push(k); }
        UndoChanged?.Invoke();
    }

    /// <summary>Reverts the last operation; returns what was undone, or null.</summary>
    public static string? Undo()
    {
        if (UndoStack.Count == 0) return null;
        var step = UndoStack.Pop();
        UndoChanged?.Invoke();
        step.Revert();
        return step.Label;
    }

    /// <summary>Top-level names among <paramref name="sources"/> that already exist in <paramref name="dest"/>.</summary>
    public static List<string> Clashes(IEnumerable<string> sources, string dest, bool move) =>
        sources.Where(s => !SameFolder(s, dest))
               .Select(s => Path.GetFileName(s.TrimEnd('/')))
               .Where(n => File.Exists(Path.Combine(dest, n)) || Directory.Exists(Path.Combine(dest, n)))
               .ToList();

    private static bool SameFolder(string src, string dest) =>
        string.Equals(Path.GetDirectoryName(src.TrimEnd('/'))?.TrimEnd('/'), dest.TrimEnd('/'), StringComparison.Ordinal);

    public static Task<Result> Run(IReadOnlyList<string> sources, string dest, bool move, Conflict onClash,
                                   IProgress<Progress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var r = new Result();
        var p = new Progress { ItemsTotal = sources.Count };
        // how much data will really be copied (renames on the same drive cost nothing)
        foreach (var s in sources) p.BytesTotal += move && SameDevice(s, dest) ? 0 : SizeOf(s);
        var buffer = new byte[1 << 20];
        long lastReport = 0;
        void Report(bool force = false)
        {
            if (progress == null) return;
            var now = Environment.TickCount64;
            if (!force && now - lastReport < 80) return;
            lastReport = now;
            progress.Report(new Progress { BytesDone = p.BytesDone, BytesTotal = p.BytesTotal, ItemsDone = p.ItemsDone, ItemsTotal = p.ItemsTotal, Current = p.Current });
        }

        void CopyFile(string from, string to)
        {
            using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ct.IsCancellationRequested) { dst.Dispose(); try { File.Delete(to); } catch { } throw new OperationCanceledException(); }
                dst.Write(buffer, 0, n);
                p.BytesDone += n;
                Report();
            }
            try { File.SetLastWriteTime(to, File.GetLastWriteTime(from)); } catch { }
        }

        void CopyTree(string from, string to)
        {
            if (Directory.Exists(from))
            {
                Directory.CreateDirectory(to);
                foreach (var e in Directory.EnumerateFileSystemEntries(from))
                {
                    ct.ThrowIfCancellationRequested();
                    CopyTree(e, Path.Combine(to, Path.GetFileName(e)));
                }
            }
            else
            {
                p.Current = Path.GetFileName(from);
                CopyFile(from, to);
            }
        }

        // a cancelled copy leaves nothing half-done behind: the partial copy is removed (the original is untouched)
        void CopyOrClean(string from, string to)
        {
            try { CopyTree(from, to); }
            catch
            {
                try { if (Directory.Exists(to)) Directory.Delete(to, true); else if (File.Exists(to)) File.Delete(to); } catch { }
                throw;
            }
        }

        foreach (var src in sources)
        {
            if (ct.IsCancellationRequested) { r.Cancelled = true; break; }
            var name = Path.GetFileName(src.TrimEnd('/'));
            p.Current = name;
            Report(true);
            string? replacedNow = null;
            void PutBackReplaced()
            {
                if (replacedNow == null) return;
                try { FileOps.RestoreFromTrash(replacedNow); r.ReplacedInTrash.Remove(replacedNow); } catch { }
            }
            try
            {
                if (!File.Exists(src) && !Directory.Exists(src)) throw new FileNotFoundException("It no longer exists.");
                bool same = SameFolder(src, dest);
                if (move && same) { p.ItemsDone++; continue; }                              // already here
                if (Directory.Exists(src) && IsInside(dest, src)) throw new IOException("A folder can't go inside itself.");
                var target = Path.Combine(dest, name);
                if (same) target = FileOps.UniquePath(dest, name);                         // copy into its own folder = duplicate
                else if (File.Exists(target) || Directory.Exists(target))
                {
                    if (onClash == Conflict.Skip) { p.ItemsDone++; continue; }
                    if (onClash == Conflict.KeepBoth) target = FileOps.UniquePath(dest, name);
                    else { replacedNow = FileOps.MoveToTrash(target); r.ReplacedInTrash.Add(replacedNow); }   // replaced, but recoverable
                }

                if (move)
                {
                    bool renamed = false;
                    try
                    {
                        if (Directory.Exists(src)) Directory.Move(src, target); else File.Move(src, target);
                        renamed = true;
                    }
                    catch (IOException) when (!SameDevice(src, dest)) { }
                    if (!renamed)
                    {
                        CopyOrClean(src, target);
                        if (Directory.Exists(src)) Directory.Delete(src, true); else File.Delete(src);
                    }
                }
                else CopyOrClean(src, target);
                r.Done.Add((src, target));
            }
            catch (OperationCanceledException) { PutBackReplaced(); r.Cancelled = true; break; }
            catch (Exception ex) { PutBackReplaced(); r.Errors.Add($"{name}: {ex.Message}"); }
            p.ItemsDone++;
            Report(true);
        }
        return r;
    }, ct);

    /// <summary>Records how to undo a finished copy / move.</summary>
    public static void RecordTransfer(Result r, bool move, string dest)
    {
        if (r.Done.Count == 0) return;
        var done = r.Done.ToList();
        var replaced = r.ReplacedInTrash.ToList();
        string what = done.Count == 1 ? $"“{Path.GetFileName(done[0].To)}”" : $"{done.Count} items";
        Record((move ? "Move " : "Copy ") + what, () =>
        {
            foreach (var (from, to) in done)
            {
                try
                {
                    if (move)
                    {
                        if (File.Exists(from) || Directory.Exists(from)) continue;               // something else took its place
                        Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                        if (Directory.Exists(to)) Directory.Move(to, from); else if (File.Exists(to)) File.Move(to, from);
                    }
                    else if (File.Exists(to) || Directory.Exists(to)) FileOps.MoveToTrash(to);  // undoing a copy: the copy goes to the Trash
                }
                catch { }
            }
            foreach (var stored in replaced) try { FileOps.RestoreFromTrash(stored); } catch { }
        });
    }

    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                total += f.Length;
            return total;
        }
        catch { return 0; }
    }

    private static bool IsInside(string path, string folder)
    {
        var a = Path.GetFullPath(path).TrimEnd('/') + "/";
        var b = Path.GetFullPath(folder).TrimEnd('/') + "/";
        return a.StartsWith(b, StringComparison.Ordinal);
    }

    /// <summary>Same filesystem? (a move is then just a rename)</summary>
    private static bool SameDevice(string a, string b)
    {
        try
        {
            string? MountOf(string path)
            {
                var full = Path.GetFullPath(path);
                return File.ReadLines("/proc/mounts").Select(l => l.Split(' ')).Where(f => f.Length > 2).Select(f => f[1].Replace("\\040", " "))
                    .Where(m => full == m || full.StartsWith(m.TrimEnd('/') + "/") || m == "/").OrderByDescending(m => m.Length).FirstOrDefault();
            }
            return MountOf(a) == MountOf(b);
        }
        catch { return true; }
    }
}
