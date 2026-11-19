using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RedStarOS.Services;

/// <summary>Clipboard shared by all File Manager windows.</summary>
public static class FileClipboard
{
    public static List<string> Paths { get; set; } = new();
    public static bool IsCut { get; set; }
}

public static class FileOps
{
    public static string UniquePath(string dir, string name)
    {
        var target = Path.Combine(dir, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        if (Directory.Exists(Path.Combine(dir, name))) { stem = name; ext = ""; }
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} (copy{(i > 1 ? " " + i : "")}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public static string CopyInto(string source, string destDir)
    {
        var name = Path.GetFileName(source.TrimEnd('/'));
        var target = UniquePath(destDir, name);
        if (Directory.Exists(source))
        {
            if (IsInside(destDir, source)) throw new IOException("Cannot copy a folder into itself.");
            CopyDirectory(source, target);
        }
        else File.Copy(source, target);
        return target;
    }

    public static string MoveInto(string source, string destDir)
    {
        var name = Path.GetFileName(source.TrimEnd('/'));
        var target = UniquePath(destDir, name);
        if (Directory.Exists(source))
        {
            if (IsInside(destDir, source)) throw new IOException("Cannot move a folder into itself.");
            try { Directory.Move(source, target); }
            catch (IOException)
            {
                CopyDirectory(source, target);
                Directory.Delete(source, true);
            }
        }
        else
        {
            try { File.Move(source, target); }
            catch (IOException)
            {
                File.Copy(source, target);
                File.Delete(source);
            }
        }
        return target;
    }

    private static bool IsInside(string path, string parent)
    {
        var p = Path.GetFullPath(path).TrimEnd('/') + "/";
        var q = Path.GetFullPath(parent).TrimEnd('/') + "/";
        return p.StartsWith(q, StringComparison.Ordinal);
    }

    /// <summary>Freedesktop.org trash: ~/.local/share/Trash/{files,info}.</summary>
    /// <summary>Moves a file or folder to the Trash. Returns its name inside the Trash (for restoring / undo).</summary>
    public static string MoveToTrash(string path)
    {
        path = Path.GetFullPath(path.TrimEnd('/'));
        var trash = Path.Combine(AppPaths.DataHome, "Trash");
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var name = Path.GetFileName(path);
        var target = Path.Combine(files, name);
        for (int i = 2; File.Exists(target) || Directory.Exists(target); i++)
            target = Path.Combine(files, $"{name}.{i}");
        var storedName = Path.GetFileName(target);

        var escaped = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        File.WriteAllText(Path.Combine(info, storedName + ".trashinfo"),
            $"[Trash Info]\nPath={escaped}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");

        if (Directory.Exists(path))
        {
            try { Directory.Move(path, target); }
            catch (IOException) { CopyDirectory(path, target); Directory.Delete(path, true); }
        }
        else
        {
            try { File.Move(path, target); }
            catch (IOException) { File.Copy(path, target); File.Delete(path); }
        }
        return storedName;
    }

    public static string TrashFolder => Path.Combine(AppPaths.DataHome, "Trash", "files");
    private static string TrashInfo(string storedName) => Path.Combine(AppPaths.DataHome, "Trash", "info", storedName + ".trashinfo");

    /// <summary>Where a trashed item came from and when it was deleted (from its .trashinfo).</summary>
    public static (string? Origin, DateTime? Deleted) TrashOrigin(string storedName)
    {
        try
        {
            var f = TrashInfo(storedName);
            if (!File.Exists(f)) return (null, null);
            string? origin = null; DateTime? when = null;
            foreach (var line in File.ReadLines(f))
            {
                if (line.StartsWith("Path=")) origin = Uri.UnescapeDataString(line[5..]);
                else if (line.StartsWith("DeletionDate=") && DateTime.TryParse(line[13..], out var d)) when = d;
            }
            return (origin, when);
        }
        catch { return (null, null); }
    }

    /// <summary>Puts a trashed item back where it was (with a new name if that place is taken). Returns the path.</summary>
    public static string RestoreFromTrash(string storedName)
    {
        var src = Path.Combine(TrashFolder, storedName);
        var (origin, _) = TrashOrigin(storedName);
        if (origin == null) throw new IOException("It isn't known where this item came from.");
        var dir = Path.GetDirectoryName(origin) ?? AppPaths.Home;
        Directory.CreateDirectory(dir);
        var target = File.Exists(origin) || Directory.Exists(origin) ? UniquePath(dir, Path.GetFileName(origin)) : origin;
        if (Directory.Exists(src))
        {
            try { Directory.Move(src, target); }
            catch (IOException) { CopyDirectory(src, target); Directory.Delete(src, true); }
        }
        else
        {
            try { File.Move(src, target); }
            catch (IOException) { File.Copy(src, target); File.Delete(src); }
        }
        try { File.Delete(TrashInfo(storedName)); } catch { }
        return target;
    }

    /// <summary>Deletes a trashed item for good.</summary>
    public static void DeleteFromTrash(string storedName)
    {
        var p = Path.Combine(TrashFolder, storedName);
        if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p);
        try { File.Delete(TrashInfo(storedName)); } catch { }
    }

    public static int TrashCount()
    {
        try { return Directory.Exists(TrashFolder) ? Directory.EnumerateFileSystemEntries(TrashFolder).Count() : 0; }
        catch { return 0; }
    }

    public static void EmptyTrash()
    {
        if (!Directory.Exists(TrashFolder)) return;
        foreach (var e in Directory.EnumerateFileSystemEntries(TrashFolder).ToList())
            try { DeleteFromTrash(Path.GetFileName(e)); } catch { }
    }

    /// <summary>Vector icon name for a file or folder (Services/Icons).</summary>
    public static string IconName(string path, bool isDir)
    {
        if (isDir) return "folder";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".svg" or ".tif" or ".tiff" or ".ico" => "image",
            ".mp3" or ".ogg" or ".flac" or ".wav" or ".m4a" or ".opus" or ".aac" => "music",
            ".mp4" or ".mkv" or ".avi" or ".webm" or ".mov" or ".wmv" or ".flv" => "film",
            ".zip" or ".tar" or ".gz" or ".xz" or ".7z" or ".rar" or ".bz2" or ".zst" or ".deb" or ".rpm" => "archive",
            ".csv" or ".xlsx" or ".xls" or ".ods" or ".tsv" => "sheet",
            ".pptx" or ".odp" or ".rsslides" => "slides",
            ".txt" or ".md" or ".log" or ".rtf" or ".docx" or ".doc" or ".odt" or ".pdf" => "document",
            ".cs" or ".py" or ".js" or ".ts" or ".rs" or ".c" or ".cpp" or ".h" or ".sh" or ".json" or ".xml" or ".html" or ".css"
                or ".java" or ".go" or ".rb" or ".php" or ".yml" or ".yaml" or ".toml" or ".ini" or ".conf" => "code",
            ".appimage" or ".exe" or ".bin" or ".run" => "apps",
            _ => "file"
        };
    }

    /// <summary>A friendly kind for the Kind column ("PNG image", "Folder", "PDF document"…).</summary>
    public static string KindOf(string path, bool isDir)
    {
        if (isDir) return "Folder";
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return "File";
        return IconName(path, false) switch
        {
            "image" => ext.ToUpperInvariant() + " image",
            "music" => ext.ToUpperInvariant() + " audio",
            "film" => ext.ToUpperInvariant() + " video",
            "archive" => ext.ToUpperInvariant() + " archive",
            "sheet" => "Spreadsheet",
            "slides" => "Presentation",
            "code" => ext.ToUpperInvariant() + " source",
            "apps" => "Program",
            "document" => ext switch { "pdf" => "PDF document", "txt" => "Text", "md" => "Markdown", "log" => "Log", _ => ext.ToUpperInvariant() + " document" },
            _ => ext.ToUpperInvariant() + " file"
        };
    }

    public static string IconFor(string path, bool isDir)
    {
        if (isDir) return "📁";
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".webp": case ".svg": return "🖼";
            case ".txt": case ".md": case ".log": case ".rtf": return "📝";
            case ".mp3": case ".ogg": case ".flac": case ".wav": case ".m4a": return "🎵";
            case ".mp4": case ".mkv": case ".avi": case ".webm": case ".mov": return "🎬";
            case ".zip": case ".tar": case ".gz": case ".xz": case ".7z": case ".rar": return "📦";
            case ".pdf": return "📕";
            case ".csv": case ".xlsx": case ".ods": return "📊";
            case ".rsslides": return "📽";
            case ".docx": return "📝";
            case ".cs": case ".py": case ".js": case ".rs": case ".c": case ".cpp": case ".h": case ".sh": case ".json": case ".xml": case ".html": case ".css": return "💻";
            case ".appimage": return "⚙";
            default: return "📄";
        }
    }

    public static readonly string[] TextExtensions =
        { ".txt", ".md", ".log", ".csv", ".json", ".xml", ".html", ".css", ".js", ".cs", ".py", ".rs", ".c", ".cpp", ".h", ".sh", ".ini", ".conf", ".yml", ".yaml", ".toml" };
    public static readonly string[] VideoExtensions =
        { ".mp4", ".m4v", ".mkv", ".webm", ".ogv", ".mov", ".avi", ".mpg", ".mpeg", ".wmv", ".flv", ".3gp" };
    public static readonly string[] AudioExtensions =
        { ".mp3", ".flac", ".wav", ".m4a", ".opus", ".aac", ".ogg" };
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }
}
