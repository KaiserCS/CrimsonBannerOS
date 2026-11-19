using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Archives in Files: zip (built in), tar / tar.gz / tar.xz / tar.bz2 / tar.zst (the tar program), 7z and rar
/// (7z / 7za / 7zz, unrar or bsdtar when installed).
///  • Browse: an archive opens like a folder — it is unpacked once into ~/.cache/redstaros/archives (reused until
///    the archive changes), so its files can be previewed, opened and copied out.
///  • Extract here / to a folder: a single top-level item lands directly, several get a folder named after the archive.
///  • Compress: selected items to .zip or .tar.gz.
/// </summary>
public static class Archives
{
    private static readonly string[] MultiExt = { ".tar.gz", ".tar.xz", ".tar.bz2", ".tar.zst", ".tar.lz", ".tar.lzma" };
    private static readonly string[] SingleExt = { ".zip", ".tar", ".tgz", ".txz", ".tbz2", ".tbz", ".tzst", ".7z", ".rar", ".jar", ".cbz", ".cbr" };

    public static bool IsArchive(string path)
    {
        var n = Path.GetFileName(path).ToLowerInvariant();
        return MultiExt.Any(n.EndsWith) || SingleExt.Any(n.EndsWith);
    }

    /// <summary>"photos.tar.gz" → "photos".</summary>
    public static string BaseName(string path)
    {
        var n = Path.GetFileName(path);
        var lower = n.ToLowerInvariant();
        foreach (var e in MultiExt.Concat(SingleExt))
            if (lower.EndsWith(e)) return n[..^e.Length];
        return Path.GetFileNameWithoutExtension(n);
    }

    private static string Kind(string path)
    {
        var n = Path.GetFileName(path).ToLowerInvariant();
        if (n.EndsWith(".zip") || n.EndsWith(".jar") || n.EndsWith(".cbz")) return "zip";
        if (n.EndsWith(".7z")) return "7z";
        if (n.EndsWith(".rar") || n.EndsWith(".cbr")) return "rar";
        return "tar";
    }

    /// <summary>What's needed to open this archive, when it's missing (for a friendly message); null when fine.</summary>
    public static string? MissingTool(string path) => Kind(path) switch
    {
        "7z" when Tool("7z", "7za", "7zz", "bsdtar") == null => "7z archives need p7zip (package 7zip / p7zip) or bsdtar (libarchive).",
        "rar" when Tool("unrar", "7z", "7zz", "bsdtar") == null => "RAR archives need unrar, 7-Zip or bsdtar.",
        "tar" when Tool("tar", "bsdtar") == null => "tar archives need the tar program.",
        _ => null
    };

    private static string? Tool(params string[] names) => names.Select(ProcessUtil.Which).FirstOrDefault(p => p != null);

    /// <summary>Unpacks <paramref name="archive"/> into the (existing, empty) folder <paramref name="dest"/>.</summary>
    public static async Task ExtractInto(string archive, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        switch (Kind(archive))
        {
            case "zip":
                await Task.Run(() => ZipFile.ExtractToDirectory(archive, dest, overwriteFiles: false), ct);   // guards against ../ paths
                break;
            case "tar":
                await RunTool(Tool("tar", "bsdtar")!, new[] { "-xf", archive, "-C", dest, "--no-same-owner" }, ct);
                break;
            case "7z":
                if (Tool("7z", "7za", "7zz") is { } sz) await RunTool(sz, new[] { "x", "-y", "-o" + dest, archive }, ct);
                else await RunTool(Tool("bsdtar")!, new[] { "-xf", archive, "-C", dest }, ct);
                break;
            case "rar":
                if (Tool("unrar") is { } ur) await RunTool(ur, new[] { "x", "-o-", "-y", archive, dest.TrimEnd('/') + "/" }, ct);
                else if (Tool("7z", "7zz") is { } sz2) await RunTool(sz2, new[] { "x", "-y", "-o" + dest, archive }, ct);
                else await RunTool(Tool("bsdtar")!, new[] { "-xf", archive, "-C", dest }, ct);
                break;
        }
    }

    /// <summary>
    /// Extracts next to the archive (or into <paramref name="folder"/>): one top-level item goes there directly,
    /// several get a folder named after the archive. Returns what was created.
    /// </summary>
    public static async Task<string> Extract(string archive, string? folder, CancellationToken ct)
    {
        if (MissingTool(archive) is { } why) throw new IOException(why);
        var parent = folder ?? Path.GetDirectoryName(archive)!;
        var temp = Path.Combine(parent, "." + BaseName(archive) + ".extracting-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            await ExtractInto(archive, temp, ct);
            var top = Directory.EnumerateFileSystemEntries(temp).ToList();
            string result;
            if (top.Count == 1)
            {
                result = FileOps.UniquePath(parent, Path.GetFileName(top[0]));
                if (Directory.Exists(top[0])) Directory.Move(top[0], result); else File.Move(top[0], result);
                Directory.Delete(temp, true);
            }
            else
            {
                result = FileOps.UniquePath(parent, BaseName(archive));
                Directory.Move(temp, result);
            }
            return result;
        }
        catch
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            throw;
        }
    }

    /// <summary>The unpacked copy used to browse an archive (made once, until the archive changes).</summary>
    public static async Task<string> BrowseCopy(string archive, CancellationToken ct)
    {
        if (MissingTool(archive) is { } why) throw new IOException(why);
        var fi = new FileInfo(archive);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks)))[..16];
        var root = Path.Combine(CacheDir, key);
        var dir = Path.Combine(root, BaseName(archive));
        if (File.Exists(Path.Combine(root, ".complete"))) return dir;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        await ExtractInto(archive, dir, ct);
        await File.WriteAllTextAsync(Path.Combine(root, ".complete"), fi.FullName, ct);
        File.WriteAllText(Path.Combine(root, ".source"), fi.FullName);
        TrimCache();
        return dir;
    }

    /// <summary>The archive a browse copy came from, when <paramref name="path"/> is inside one.</summary>
    public static string? SourceOf(string path)
    {
        var cache = CacheDir.TrimEnd('/') + "/";
        if (!path.StartsWith(cache)) return null;
        var key = path[cache.Length..].Split('/')[0];
        try { return File.ReadAllText(Path.Combine(CacheDir, key, ".source")).Trim(); } catch { return null; }
    }

    public static string CacheDir
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(b)) b = Path.Combine(AppPaths.Home, ".cache");
            return Path.Combine(b, "redstaros", "archives");
        }
    }

    /// <summary>Keeps the browse cache to the 8 most recent archives.</summary>
    private static void TrimCache()
    {
        try
        {
            foreach (var d in new DirectoryInfo(CacheDir).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).Skip(8))
                d.Delete(true);
        }
        catch { }
    }

    /// <summary>Packs <paramref name="items"/> (all in one folder) into <paramref name="target"/> (.zip or .tar.gz).</summary>
    public static async Task Compress(IReadOnlyList<string> items, string target, CancellationToken ct)
    {
        if (items.Count == 0) return;
        var parent = Path.GetDirectoryName(items[0].TrimEnd('/'))!;
        if (target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() =>
            {
                try
                {
                    using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
                    foreach (var item in items)
                    {
                        if (Directory.Exists(item))
                        {
                            var name = Path.GetFileName(item.TrimEnd('/'));
                            zip.CreateEntry(name + "/");
                            foreach (var f in Directory.EnumerateFileSystemEntries(item, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                            {
                                ct.ThrowIfCancellationRequested();
                                var rel = name + "/" + Path.GetRelativePath(item, f).Replace('\\', '/');
                                if (Directory.Exists(f)) zip.CreateEntry(rel + "/");
                                else zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
                            }
                        }
                        else zip.CreateEntryFromFile(item, Path.GetFileName(item), CompressionLevel.Optimal);
                    }
                }
                catch { try { File.Delete(target); } catch { } throw; }
            }, ct);
        }
        else
        {
            var tar = Tool("tar", "bsdtar") ?? throw new IOException("Making a .tar.gz needs the tar program.");
            var args = new List<string> { "-czf", target, "-C", parent };
            args.AddRange(items.Select(i => Path.GetFileName(i.TrimEnd('/'))));
            try { await RunTool(tar, args, ct); }
            catch { try { File.Delete(target); } catch { } throw; }
        }
    }

    /// <summary>Runs an archive tool; throws with its message when it fails; killed when cancelled.</summary>
    private static async Task RunTool(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException(Path.GetFileName(exe) + " could not start.");
        p.StandardInput.Close();                                // never wait for a password prompt
        var err = p.StandardError.ReadToEndAsync();
        var _ = p.StandardOutput.ReadToEndAsync();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            var msg = (await err).Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "";
            throw new IOException($"{Path.GetFileName(exe)} failed{(msg.Length > 0 ? ": " + msg : "")} (code {p.ExitCode}).");
        }
    }
}
