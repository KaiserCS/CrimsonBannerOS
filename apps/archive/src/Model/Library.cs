// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeoplesArchive.Model;

/// <summary>A book on the shelf: its file, where it came from and under what terms, and where its reader is up to.</summary>
public sealed class ShelfBook
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Language { get; set; } = "";
    /// <summary>The file's name in the shelf's folder.</summary>
    public string File { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Licence { get; set; } = "";
    public DateTime Added { get; set; }
    public DateTime? LastRead { get; set; }
    public int Chapter { get; set; }
    /// <summary>How far down the chapter (0–1).</summary>
    public double Position { get; set; }
    public int Chapters { get; set; }
    public List<int> Bookmarks { get; set; } = new();

    [JsonIgnore] public double Progress => Chapters <= 0 ? 0 : Math.Clamp((Chapter + Position) / Chapters, 0, 1);
}

/// <summary>
/// The shelf: books in a folder of the person's own (Documents/People's Archive), with library.json beside them.
/// Books dropped into the folder by hand join the shelf the next time it's read.
/// </summary>
public sealed class Library
{
    public string Folder { get; }
    public List<ShelfBook> Books { get; private set; } = new();
    private string Index => Path.Combine(Folder, "library.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Library(string folder) { Folder = folder; }

    public static string DefaultFolder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.Combine(docs, "People's Archive");
        }
    }

    /// <summary>Reads the shelf, adds books found in the folder that it doesn't list, drops ones whose file is gone.</summary>
    public void Load()
    {
        Directory.CreateDirectory(Folder);
        try { Books = File.Exists(Index) ? JsonSerializer.Deserialize<List<ShelfBook>>(File.ReadAllText(Index), Json) ?? new() : new(); }
        catch (JsonException) { Books = new(); }
        Books.RemoveAll(b => !File.Exists(Path.Combine(Folder, b.File)));
        foreach (var f in Directory.GetFiles(Folder).Where(IsBook))
        {
            var name = Path.GetFileName(f);
            if (Books.Any(b => b.File == name)) continue;
            try { Books.Add(Describe(f, "This computer", "", "")); }
            catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException) { }
        }
        Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var tmp = Index + ".saving";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Books, Json));
        File.Move(tmp, Index, overwrite: true);
    }

    public static bool IsBook(string file) =>
        file.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>A shelf entry for a file, from what the book says about itself.</summary>
    public static ShelfBook Describe(string file, string source, string url, string licence)
    {
        using var b = Book.Open(file);
        return new ShelfBook
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Title = b.Title, Author = b.Author, Language = b.Language,
            File = Path.GetFileName(file), Source = source, SourceUrl = url,
            Licence = licence.Length > 0 ? licence : b.Rights, Added = DateTime.UtcNow, Chapters = b.Chapters.Count,
        };
    }

    /// <summary>Puts a downloaded (or chosen) file on the shelf: copied into the folder under a tidy name.</summary>
    public ShelfBook Add(string file, string source, string url, string licence, string? title = null, string? author = null)
    {
        Directory.CreateDirectory(Folder);
        var ext = Book.IsZip(file) ? ".epub" : ".txt";
        var info = Describe(file, source, url, licence);
        if (!string.IsNullOrWhiteSpace(title) && (info.Title.Length == 0 || info.Title == Path.GetFileNameWithoutExtension(file))) info.Title = title!;
        if (!string.IsNullOrWhiteSpace(author) && info.Author.Length == 0) info.Author = author!;
        var name = Safe(info.Author.Length > 0 ? $"{info.Title} - {info.Author}" : info.Title);
        var dest = Path.Combine(Folder, name + ext);
        for (int k = 2; File.Exists(dest) && Path.GetFullPath(dest) != Path.GetFullPath(file); k++) dest = Path.Combine(Folder, $"{name} {k}{ext}");
        if (Path.GetFullPath(dest) != Path.GetFullPath(file)) File.Copy(file, dest);
        info.File = Path.GetFileName(dest);
        Books.RemoveAll(b => b.File == info.File);
        Books.Add(info);
        Save();
        return info;
    }

    public void Remove(ShelfBook b)
    {
        var f = Path.Combine(Folder, b.File);
        if (File.Exists(f)) File.Delete(f);
        Books.Remove(b);
        Save();
    }

    public string PathOf(ShelfBook b) => Path.Combine(Folder, b.File);

    /// <summary>Already on the shelf from this address (so a search can say so).</summary>
    public ShelfBook? FromUrl(string url) => url.Length == 0 ? null : Books.FirstOrDefault(b => b.SourceUrl == url);

    public IEnumerable<ShelfBook> Search(string query)
    {
        var q = query.Trim();
        var list = q.Length == 0 ? Books
            : Books.Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Author.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || b.Source.Contains(q, StringComparison.OrdinalIgnoreCase));
        return list.OrderByDescending(b => b.LastRead ?? b.Added);
    }

    public static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '\\', '/' }).ToHashSet();
        var s = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray());
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.');
        if (s.Length > 120) s = s[..120].TrimEnd();
        return s.Length == 0 ? "Book" : s;
    }
}
