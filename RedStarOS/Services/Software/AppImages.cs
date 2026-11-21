// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services.Software;

/// <summary>
/// AppImages: AppImageHub's catalogue (appimage.github.io/feed.json, a community list) for finding them, each
/// program's own latest GitHub release for downloading them, ~/Applications for keeping them, and a Start menu entry
/// with the program's own name and icon. A download is checked against the checksum its release or our catalogue
/// gives, and must really be an AppImage, before it's kept. Only free software is listed (<see cref="FreeLicences"/>).
/// </summary>
public static class AppImages
{
    public const string FeedUrl = "https://appimage.github.io/feed.json";
    public const string IconBase = "https://appimage.github.io/database/";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.TryParseAdd("CrimsonBanner-Software/" + AppVersion.Current.Short);
        return h;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Folder => Path.Combine(Home, "Applications");
    private static string DataHome => Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".local", "share");
    public static string DesktopDir => Path.Combine(DataHome, "applications");
    public static string IconDir => Path.Combine(DataHome, "icons", "crimson-banner-appimages");
    public static string RecordsPath => Path.Combine(AppPaths.ConfigDir, "appimages.json");
    private static string FeedCache => Path.Combine(Path.GetDirectoryName(OurCatalog.CachePath)!, "appimagehub.json");

    // ── what's installed ──

    /// <summary>An AppImage the software centre installed (or was given).</summary>
    public sealed class Record
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public string? Version { get; set; }
        public string? GitHub { get; set; }           // owner/repo, for updates
        public string? Source { get; set; }           // "appimagehub", "crimson-banner" or "file"
        public string? Desktop { get; set; }
        public string? Icon { get; set; }
        public DateTime Installed { get; set; }
    }

    public static List<Record> Records()
    {
        try { return File.Exists(RecordsPath) ? JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(RecordsPath)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    private static void Save(List<Record> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecordsPath)!);
        var tmp = RecordsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, RecordsPath, true);
    }

    /// <summary>Installed ones whose file is still there (a file deleted by hand drops out).</summary>
    public static List<Record> InstalledRecords() => Records().Where(r => File.Exists(r.File)).ToList();

    // ── the catalogue ──

    public static async Task<(List<SoftwareItem> Items, string? Note)> CatalogueAsync(bool fetch, CancellationToken ct)
    {
        string? note = null, text = null;
        var stale = !File.Exists(FeedCache) || DateTime.UtcNow - File.GetLastWriteTimeUtc(FeedCache) > TimeSpan.FromDays(1);
        if (fetch || stale)
        {
            try
            {
                text = await Http.GetStringAsync(FeedUrl, ct);
                ParseFeed(text);
                Directory.CreateDirectory(Path.GetDirectoryName(FeedCache)!);
                await File.WriteAllTextAsync(FeedCache, text, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                text = null;
                note = "Couldn't fetch AppImageHub's list (" + ex.Message + ")" + (File.Exists(FeedCache) ? "; showing the last one." : ".");
            }
        }
        if (text == null && File.Exists(FeedCache)) text = await File.ReadAllTextAsync(FeedCache, ct);
        return (text == null ? new() : ParseFeed(text), note);
    }

    /// <summary>AppImageHub's feed → the programs that name their GitHub project (where the download comes from).
    /// Not-free ones are left out; ones that don't state a licence are kept, marked, for the window to show or not.</summary>
    public static List<SoftwareItem> ParseFeed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<SoftwareItem>();
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in items.EnumerateArray())
        {
            string? S(JsonElement el, string n) => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var name = S(e, "name");
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            string? github = null;
            if (e.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
                foreach (var l in links.EnumerateArray())
                    if (S(l, "type") == "GitHub" && S(l, "url") is { } u && Regex.IsMatch(u, @"^[\w.-]+/[\w.-]+$")) { github = u; break; }
            if (github == null) continue;
            var licence = S(e, "license");
            if (FreeLicences.Judge(licence) == FreeLicences.Verdict.NotFree) continue;
            string? icon = null;
            if (e.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Array)
                icon = icons.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null).FirstOrDefault(i => !string.IsNullOrEmpty(i));
            string? author = null, homepage = null;
            if (e.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array && authors.GetArrayLength() > 0)
            { author = S(authors[0], "name"); homepage = S(authors[0], "url"); }
            var desc = Regex.Replace(S(e, "description") ?? "", "<[^>]+>", " ");
            desc = Regex.Replace(System.Net.WebUtility.HtmlDecode(desc), @"[ \t]+", " ").Trim();
            list.Add(new SoftwareItem
            {
                Source = SoftwareSource.AppImage,
                Id = name,
                Name = name,
                Summary = FirstSentence(desc),
                Description = desc,
                License = licence,
                Developer = author,
                Homepage = homepage ?? "https://github.com/" + github,
                IconUrl = icon == null ? null : icon.StartsWith("http", StringComparison.Ordinal) ? icon : IconBase + icon,
                Categories = e.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    ? cats.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList() : new(),
                Methods = { new InstallMethod(InstallMethod.AppImage) { GitHub = github } },
            });
        }
        return list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string FirstSentence(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0) line = line[..(dot + 1)];
        return line.Length > 140 ? line[..137] + "…" : line;
    }

    // ── a release's AppImage ──

    public sealed record Asset(string Tag, string Name, string Url, long Size, string? Sha256);

    private static readonly string[] X64 = { "x86_64", "x86-64", "amd64", "x64" };
    private static readonly string[] Arm64 = { "aarch64", "arm64" };
    private static readonly string[] OtherArch = { "i386", "i686", "armhf", "armv7", "armv7l", "arm32" };

    /// <summary>From a GitHub release (its JSON): the AppImage for this computer's architecture, if there is one.</summary>
    public static Asset? PickAsset(string releaseJson, string arch)
    {
        using var doc = JsonDocument.Parse(releaseJson);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;
        var mine = arch == "aarch64" ? Arm64 : X64;
        var others = (arch == "aarch64" ? X64 : Arm64).Concat(OtherArch).ToArray();
        var candidates = new List<(int Score, Asset A)>();
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)) continue;
            var lower = name.ToLowerInvariant();
            bool isMine = mine.Any(lower.Contains), isOther = others.Any(o => Regex.IsMatch(lower, $@"(^|[^a-z0-9]){Regex.Escape(o)}([^a-z0-9]|$)"));
            if (isOther && !isMine) continue;
            string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } d && d.StartsWith("sha256:", StringComparison.Ordinal) ? d[7..] : null;
            candidates.Add((isMine ? 2 : 1, new Asset(tag, name, a.GetProperty("browser_download_url").GetString() ?? "",
                a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0, sha)));
        }
        return candidates.OrderByDescending(c => c.Score).Select(c => c.A).FirstOrDefault(a => a.Url.StartsWith("https://", StringComparison.Ordinal));
    }

    public static async Task<Asset?> LatestAsync(string github, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{github}/releases/latest", ct);
        return PickAsset(json, Flatpak.Arch);
    }

    // ── installing ──

    /// <summary>Installs from AppImageHub (the GitHub project's latest release) or our catalogue (a URL and its
    /// sha256). Null when it worked, else why not.</summary>
    public static async Task<string?> InstallAsync(SoftwareItem item, InstallMethod m, Action<int>? progress, CancellationToken ct)
    {
        try
        {
            string url, version; string? sha;
            if (m.GitHub is { } gh)
            {
                var asset = await LatestAsync(gh, ct);
                if (asset == null) return $"{gh}'s latest release has no AppImage for this computer.";
                (url, version, sha) = (asset.Url, asset.Tag, asset.Sha256);
            }
            else if (m.Url is { } u && m.Sha256 is { } s) (url, version, sha) = (u, item.Version ?? "", s);
            else return "There's nothing to download.";

            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, SafeName(item.Name) + ".AppImage");
            var part = file + ".part";
            await DownloadAsync(url, part, sha, progress, ct);
            File.Move(part, file, true);
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            var rec = await IntegrateAsync(file, item.Name, ct);
            rec.Id = item.Id; rec.Version = version; rec.GitHub = m.GitHub;
            rec.Source = item.Source == SoftwareSource.CrimsonBanner ? "crimson-banner" : "appimagehub";
            Keep(rec);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>An AppImage you have: copied into ~/Applications (unless it's there already) and given a Start menu entry.</summary>
    public static async Task<string?> AddFileAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!IsAppImage(path)) return "That file isn't an AppImage.";
            Directory.CreateDirectory(Folder);
            var file = Path.GetFullPath(path).StartsWith(Folder + "/", StringComparison.Ordinal) ? path : Path.Combine(Folder, Path.GetFileName(path));
            if (file != path) File.Copy(path, file, true);
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            var rec = await IntegrateAsync(file, Path.GetFileNameWithoutExtension(file), ct);
            rec.Id = "file:" + Path.GetFileName(file); rec.Source = "file";
            Keep(rec);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static async Task DownloadAsync(string url, string target, string? sha256, Action<int>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(target))
        {
            var buf = new byte[1 << 16];
            long got = 0; int n, last = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                hash.AppendData(buf, 0, n);
                got += n;
                if (total > 0 && (int)(got * 100 / total) is var pct && pct != last) { last = pct; progress?.Invoke(pct); }
            }
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (sha256 != null && !actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidDataException("The download doesn't match its checksum, so it wasn't kept.");
        }
        if (!IsAppImage(target))
        {
            File.Delete(target);
            throw new InvalidDataException("The download isn't an AppImage, so it wasn't kept.");
        }
    }

    /// <summary>An ELF program with AppImage's mark ("AI" and type 1 or 2 at byte 8).</summary>
    public static bool IsAppImage(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var b = new byte[11];
            if (f.Read(b, 0, 11) < 11) return false;
            return b[0] == 0x7F && b[1] == (byte)'E' && b[2] == (byte)'L' && b[3] == (byte)'F' && b[8] == (byte)'A' && b[9] == (byte)'I' && (b[10] == 1 || b[10] == 2);
        }
        catch { return false; }
    }

    /// <summary>The program's own .desktop and icon, taken out of the AppImage (its --appimage-extract), become a Start
    /// menu entry that runs it. When that can't be done, the entry has the name it was given.</summary>
    private static async Task<Record> IntegrateAsync(string file, string fallbackName, CancellationToken ct)
    {
        var id = SafeName(Path.GetFileNameWithoutExtension(file)).ToLowerInvariant();
        string name = fallbackName; string? icon = null, categories = null, comment = null;
        var tmp = Directory.CreateTempSubdirectory("crimson-appimage-");
        try
        {
            await RunIn(tmp.FullName, file, "*.desktop", ct);
            await RunIn(tmp.FullName, file, ".DirIcon", ct);
            var root = Path.Combine(tmp.FullName, "squashfs-root");
            var desktop = Directory.Exists(root) ? Directory.GetFiles(root, "*.desktop").FirstOrDefault() : null;
            if (desktop != null)
            {
                var entry = ReadDesktop(File.ReadAllText(desktop));
                name = entry.GetValueOrDefault("Name") ?? name;
                categories = entry.GetValueOrDefault("Categories");
                comment = entry.GetValueOrDefault("Comment");
            }
            var dirIcon = Path.Combine(root, ".DirIcon");
            if (File.Exists(dirIcon) || new FileInfo(dirIcon).LinkTarget != null)
            {
                // .DirIcon is often a link to the real picture, which has to be taken out too
                var target = new FileInfo(dirIcon).LinkTarget;
                if (target != null) { await RunIn(tmp.FullName, file, target.TrimStart('/'), ct); }
                var real = target != null ? Path.Combine(root, target.TrimStart('/')) : dirIcon;
                if (File.Exists(real))
                {
                    Directory.CreateDirectory(IconDir);
                    var ext = IsSvg(real) ? ".svg" : ".png";
                    icon = Path.Combine(IconDir, id + ext);
                    File.Copy(real, icon, true);
                }
            }
        }
        catch (Exception) { /* a Start menu entry with just the name is still useful */ }
        finally { try { tmp.Delete(true); } catch { } }

        Directory.CreateDirectory(DesktopDir);
        var desktopFile = Path.Combine(DesktopDir, "crimson-appimage-" + id + ".desktop");
        File.WriteAllText(desktopFile, DesktopEntry(name, file, icon, categories, comment));
        return new Record { Name = name, File = file, Desktop = desktopFile, Icon = icon, Installed = DateTime.Now };
    }

    private static async Task RunIn(string dir, string file, string pattern, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--appimage-extract");
        psi.ArgumentList.Add(pattern);
        foreach (var k in new[] { "APPIMAGE", "APPDIR", "ARGV0", "OWD" }) psi.Environment.Remove(k);
        using var p = System.Diagnostics.Process.Start(psi);
        if (p == null) return;
        _ = p.StandardOutput.ReadToEndAsync(); _ = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } ct.ThrowIfCancellationRequested(); }
    }

    private static bool IsSvg(string path)
    {
        try { var head = File.ReadAllText(path).TrimStart(); return head.StartsWith("<svg", StringComparison.Ordinal) || head.StartsWith("<?xml", StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>The [Desktop Entry] keys of a .desktop file (no translations).</summary>
    public static Dictionary<string, string> ReadDesktop(string text)
    {
        var d = new Dictionary<string, string>();
        bool inEntry = false;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith('[')) { inEntry = l == "[Desktop Entry]"; continue; }
            if (!inEntry || l.StartsWith('#')) continue;
            var eq = l.IndexOf('=');
            if (eq <= 0) continue;
            var k = l[..eq].Trim();
            if (k.Contains('[')) continue;
            d.TryAdd(k, l[(eq + 1)..].Trim());
        }
        return d;
    }

    /// <summary>The Start menu entry for an installed AppImage.</summary>
    public static string DesktopEntry(string name, string file, string? icon, string? categories, string? comment)
    {
        static string Clean(string s) => s.Replace('\n', ' ').Replace('\r', ' ');
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\\\\\").Replace("\"", "\\\\\"").Replace("`", "\\\\`").Replace("$", "\\\\$") + "\"";
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\nType=Application\n");
        sb.Append("Name=").Append(Clean(name)).Append('\n');
        if (!string.IsNullOrWhiteSpace(comment)) sb.Append("Comment=").Append(Clean(comment)).Append('\n');
        sb.Append("Exec=").Append(Quote(file)).Append(" %U\n");
        sb.Append("TryExec=").Append(Clean(file)).Append('\n');
        if (icon != null) sb.Append("Icon=").Append(Clean(icon)).Append('\n');
        sb.Append("Categories=").Append(string.IsNullOrWhiteSpace(categories) ? "Utility;" : Clean(categories)).Append('\n');
        sb.Append("X-CrimsonBanner-AppImage=true\n");
        return sb.ToString();
    }

    private static void Keep(Record rec)
    {
        var list = Records().Where(r => r.Id != rec.Id && r.File != rec.File).ToList();
        list.Add(rec);
        Save(list);
    }

    public static string? Remove(Record rec)
    {
        try
        {
            foreach (var f in new[] { rec.File, rec.Desktop, rec.Icon })
                if (f != null && File.Exists(f)) File.Delete(f);
            Save(Records().Where(r => r.Id != rec.Id).ToList());
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Installed ones from GitHub whose latest release is newer than what's here (by tag).</summary>
    public static async Task<Dictionary<string, Asset>> UpdatesAsync(CancellationToken ct)
    {
        var found = new Dictionary<string, Asset>();
        foreach (var r in InstalledRecords().Where(r => r.GitHub != null))
        {
            try
            {
                var a = await LatestAsync(r.GitHub!, ct);
                if (a != null && a.Tag.Length > 0 && a.Tag != r.Version) found[r.Id] = a;
            }
            catch (Exception) { /* offline, or the project moved: no update shown */ }
        }
        return found;
    }

    public static bool Run(Record rec) => ProcessUtil.StartDetached(rec.File, Array.Empty<string>());

    public static string SafeName(string s)
    {
        var clean = Regex.Replace(s, @"[^\w.+-]+", "-").Trim('-', '.');
        return clean.Length == 0 ? "app" : clean.Length > 80 ? clean[..80] : clean;
    }
}
