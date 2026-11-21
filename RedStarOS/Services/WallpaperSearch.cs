using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Online wallpaper search for the wallpaper picker. Two indexes, both free and without an account:
///  - Wallhaven (wallhaven.cc): a wallpaper library with full-resolution originals — only "safe for work" results
///    are ever requested.
///  - Openverse (openverse.org): Creative-Commons and public-domain photos; credits are saved next to the picture.
/// Thumbnails are cached in ~/.cache/redstaros/wallthumbs; chosen wallpapers are saved at full size in
/// ~/Pictures/Wallpapers so they keep working offline and show up under "My wallpapers".
/// </summary>
public static class WallpaperSearch
{
    public sealed record Result(
        string Source, string Id, string Title, string ThumbUrl, string FullUrl, int Width, int Height,
        string PageUrl, string? Creator, string? License, string? FileType)
    {
        public string Resolution => Width > 0 && Height > 0 ? $"{Width} × {Height}" : "";
        public string Quality => Width >= 3840 ? "4K" : Width >= 2560 ? "QHD" : Width >= 1920 ? "Full HD" : Width > 0 ? "HD" : "";
    }

    public sealed record Page(List<Result> Items, int PageNumber, int LastPage, int Total, string? Seed, string? Error);

    public sealed record Options(
        string Query, string Source = "wallhaven", string Sort = "relevance", int MinWidth = 1920, int MinHeight = 1080,
        bool General = true, bool Anime = false, bool People = false, string? Color = null, int PageNumber = 1, string? Seed = null);

    public static readonly string[] Sources = { "wallhaven", "openverse" };
    public static string SourceName(string id) => id == "openverse" ? "Openverse (Creative Commons)" : "Wallhaven";

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim ThumbGate = new(6);

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"RedStarOS/{AppVersion.Current.Short} (wallpaper search)");
        return h;
    }

    public static string SaveFolder
    {
        get
        {
            var d = Path.Combine(AppPaths.UserFolder("Pictures"), "Wallpapers");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string ThumbCache
    {
        get
        {
            var b = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(b)) b = Path.Combine(AppPaths.Home, ".cache");
            var d = Path.Combine(b, "redstaros", "wallthumbs");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    // ───────────────────────── search ─────────────────────────
    public static Task<Page> SearchAsync(Options o, CancellationToken ct = default) =>
        o.Source == "openverse" ? Openverse(o, ct) : Wallhaven(o, ct);

    private static async Task<Page> Wallhaven(Options o, CancellationToken ct)
    {
        try
        {
            string sort = o.Sort switch { "top" => "toplist", "latest" => "date_added", "random" => "random", "views" => "views", _ => string.IsNullOrWhiteSpace(o.Query) ? "toplist" : "relevance" };
            string cats = $"{(o.General ? 1 : 0)}{(o.Anime ? 1 : 0)}{(o.People ? 1 : 0)}";
            if (cats == "000") cats = "100";
            var url = new StringBuilder("https://wallhaven.cc/api/v1/search?purity=100");    // 100 = safe for work only
            url.Append("&categories=").Append(cats);
            url.Append("&sorting=").Append(sort).Append("&order=desc");
            if (sort == "toplist") url.Append("&topRange=1y");
            if (o.MinWidth > 0 && o.MinHeight > 0) url.Append("&atleast=").Append(o.MinWidth).Append('x').Append(o.MinHeight);
            url.Append("&ratios=landscape");
            if (!string.IsNullOrWhiteSpace(o.Query)) url.Append("&q=").Append(Uri.EscapeDataString(o.Query.Trim()));
            if (!string.IsNullOrEmpty(o.Color)) url.Append("&colors=").Append(o.Color.TrimStart('#'));
            if (!string.IsNullOrEmpty(o.Seed)) url.Append("&seed=").Append(o.Seed);
            url.Append("&page=").Append(Math.Max(1, o.PageNumber));

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url.ToString(), ct));
            var root = doc.RootElement;
            var items = new List<Result>();
            foreach (var d in root.GetProperty("data").EnumerateArray())
            {
                if (S(d, "purity") is { } p && p != "sfw") continue;         // belt and braces
                var thumbs = d.TryGetProperty("thumbs", out var t) ? t : default;
                string thumb = thumbs.ValueKind == JsonValueKind.Object ? (S(thumbs, "large") ?? S(thumbs, "small") ?? "") : "";
                var full = S(d, "path");
                if (full == null || thumb.Length == 0) continue;
                var category = S(d, "category") ?? "";
                items.Add(new Result("wallhaven", S(d, "id") ?? full, Capital(category) + " wallpaper", thumb, full,
                    I(d, "dimension_x"), I(d, "dimension_y"), S(d, "url") ?? "https://wallhaven.cc", null, null, S(d, "file_type")));
            }
            var meta = root.TryGetProperty("meta", out var m) ? m : default;
            int cur = meta.ValueKind == JsonValueKind.Object ? I(meta, "current_page") : o.PageNumber;
            int last = meta.ValueKind == JsonValueKind.Object ? I(meta, "last_page") : cur;
            int total = meta.ValueKind == JsonValueKind.Object ? I(meta, "total") : items.Count;
            string? seed = meta.ValueKind == JsonValueKind.Object ? S(meta, "seed") : null;
            return new Page(items, cur, last, total, seed ?? o.Seed, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Page(new(), o.PageNumber, o.PageNumber, 0, null, Friendly(ex)); }
    }

    private static async Task<Page> Openverse(Options o, CancellationToken ct)
    {
        try
        {
            var q = string.IsNullOrWhiteSpace(o.Query) ? "landscape" : o.Query.Trim();
            var url = $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(q)}&aspect_ratio=wide&size=large&mature=false&page_size=24&page={Math.Max(1, o.PageNumber)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            var root = doc.RootElement;
            var items = new List<Result>();
            foreach (var d in root.GetProperty("results").EnumerateArray())
            {
                int w = I(d, "width"), h = I(d, "height");
                if (w > 0 && h > 0 && (w < Math.Min(o.MinWidth, 1600) || h < Math.Min(o.MinHeight, 900))) continue;   // too small for a screen
                var full = S(d, "url");
                if (full == null) continue;
                var license = S(d, "license") is { } l ? ("CC " + l.ToUpperInvariant() + (S(d, "license_version") is { } v ? " " + v : "")).Replace("CC PDM", "Public domain").Replace("CC CC0", "CC0") : null;
                items.Add(new Result("openverse", S(d, "id") ?? full, S(d, "title") ?? "Photo", S(d, "thumbnail") ?? full, full,
                    w, h, S(d, "foreign_landing_url") ?? "https://openverse.org", S(d, "creator"), license, null));
            }
            int page = I(root, "page"), last = I(root, "page_count"), total = I(root, "result_count");
            return new Page(items, page == 0 ? o.PageNumber : page, last == 0 ? o.PageNumber : last, total, null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new Page(new(), o.PageNumber, o.PageNumber, 0, null, Friendly(ex)); }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "The wallpaper index is busy (too many searches). Wait a minute and try again.",
        HttpRequestException => "Couldn't reach the wallpaper index. Check your internet connection.",
        TaskCanceledException => "The wallpaper index took too long to answer.",
        _ => "Search failed: " + ex.Message
    };

    private static string? S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int I(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
        return 0;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ───────────────────────── files ─────────────────────────
    /// <summary>Downloads (or reuses) a thumbnail; returns the cached file, or null.</summary>
    public static async Task<string?> ThumbAsync(string url, CancellationToken ct = default)
    {
        var file = Path.Combine(ThumbCache, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
        if (File.Exists(file)) return file;
        await ThumbGate.WaitAsync(ct);
        try
        {
            if (File.Exists(file)) return file;
            var bytes = await Http.GetByteArrayAsync(url, ct);
            var tmp = file + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, file, true);
            return file;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { ThumbGate.Release(); }
    }

    /// <summary>Where a result is (or would be) saved.</summary>
    public static string LocalPath(Result r)
    {
        var ext = r.FileType switch { "image/png" => ".png", "image/webp" => ".webp", _ => null };
        if (ext == null)
        {
            var e = Path.GetExtension(new Uri(r.FullUrl).AbsolutePath).ToLowerInvariant();
            ext = e is ".jpg" or ".jpeg" or ".png" or ".webp" ? e : ".jpg";
        }
        var safeId = new string(r.Id.Where(char.IsLetterOrDigit).Take(40).ToArray());
        return Path.Combine(SaveFolder, $"{r.Source}-{safeId}{ext}");
    }

    public static bool IsSaved(Result r) => File.Exists(LocalPath(r));

    /// <summary>Downloads the full-size picture to ~/Pictures/Wallpapers (progress 0..1) and returns its path.</summary>
    public static async Task<string> DownloadAsync(Result r, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var target = LocalPath(r);
        if (File.Exists(target)) { progress?.Report(1); return target; }
        using var resp = await Http.GetAsync(r.FullUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        var tmp = target + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        File.Move(tmp, target, true);
        progress?.Report(1);
        if (r.Source == "openverse")
            try
            {
                File.AppendAllText(Path.Combine(SaveFolder, "credits.txt"),
                    $"{Path.GetFileName(target)} — “{r.Title}” by {r.Creator ?? "unknown"}, {r.License ?? "see source"}, {r.PageUrl}\n");
            }
            catch { }
        return target;
    }

    // ───────────────────────── recent searches ─────────────────────────
    private static string RecentFile => AppPaths.ConfigFile("wallpaper-searches.json");

    public static List<string> RecentSearches()
    {
        try { return File.Exists(RecentFile) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentFile)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Remember(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        var list = RecentSearches();
        list.RemoveAll(q => q.Equals(query, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, query);
        if (list.Count > 12) list.RemoveRange(12, list.Count - 12);
        try { File.WriteAllText(RecentFile, JsonSerializer.Serialize(list)); } catch { }
    }
}
