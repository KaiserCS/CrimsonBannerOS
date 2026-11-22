// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services.Software;

/// <summary>
/// Crimson Banner OS's own software (apps/README.md): programs made for the system that don't belong in every
/// install, such as large specialised tools. tools/publish-app.sh publishes the catalogue, signed with the
/// project's key, on GitHub Releases ("apps"); it's only used when its signature checks out
/// (<see cref="CatalogSignature"/>). The shell keeps the last good one it fetched, and has the copy it was built
/// with for when it can't reach it. Each program lists its ways to install: our pacman
/// repository, a Flatpak or an AppImage with its checksum; the first one this computer can use is offered.
/// </summary>
public static class OurCatalog
{
    /// <summary>The "apps" release, kept up to date in place by tools/publish-app.sh (not an OS release).</summary>
    public const string DefaultUrl = "https://github.com/KaiserCS/RedStarOS-Linux/releases/download/apps/catalog.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string CachePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "redstaros", "catalog.json");

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "software", "catalog.json");

    /// <summary>The catalogue: fetched now when it can be and its signature checks out (then cached), else the cached
    /// one (checked again), else the one this build came with.</summary>
    public static async Task<(List<SoftwareItem> Items, string? Note)> LoadAsync(string? url, bool fetch, CancellationToken ct = default)
    {
        string? note = null;
        var keyed = CatalogSignature.HasKey();
        if (fetch)
        {
            try
            {
                Http.DefaultRequestHeaders.UserAgent.TryParseAdd("CrimsonBanner-Software/" + AppVersion.Current.Short);
                var u = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url;
                var data = await Http.GetByteArrayAsync(u, ct);
                byte[]? sig = null;
                try { sig = await Http.GetByteArrayAsync(u + ".sig", ct); } catch (HttpRequestException) { }
                string? bad = !keyed ? null : sig == null ? "it isn't signed" : await CatalogSignature.VerifyAsync(data, sig);
                if (bad != null) note = "The latest list wasn't used: " + bad + ".";
                else
                {
                    var items = Parse(System.Text.Encoding.UTF8.GetString(data));  // only cache what parses
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                    await File.WriteAllBytesAsync(CachePath, data, ct);
                    if (sig != null) await File.WriteAllBytesAsync(CachePath + ".sig", sig, ct); else File.Delete(CachePath + ".sig");
                    return (items, keyed ? null : "This build has no project key, so the list couldn't be checked.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                note = "Couldn't fetch the latest list (" + ex.Message + "); showing the last one.";
            }
        }
        // the last one fetched: checked again (the cache is only a file in your home folder)
        try
        {
            if (File.Exists(CachePath))
            {
                var data = await File.ReadAllBytesAsync(CachePath, ct);
                var ok = !keyed || (File.Exists(CachePath + ".sig") && await CatalogSignature.VerifyAsync(data, await File.ReadAllBytesAsync(CachePath + ".sig", ct)) == null);
                if (ok) return (Parse(System.Text.Encoding.UTF8.GetString(data)), note);
            }
        }
        catch (Exception) { /* damaged: the bundled one */ }
        // the one this build came with (part of the signed package)
        try { if (File.Exists(BundledPath)) return (Parse(await File.ReadAllTextAsync(BundledPath, ct)), note); }
        catch (Exception) { }
        return (new(), note ?? "The list of Crimson Banner software isn't available.");
    }

    /// <summary>Reads catalog.json (format 1). Entries without an id, a name or a way to install are left out, as are
    /// AppImages without a sha256 and programs that aren't free software.</summary>
    public static List<SoftwareItem> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Number && f.GetInt32() > 1)
            throw new FormatException("this catalogue is a newer format than this version of Crimson Banner OS reads");
        var list = new List<SoftwareItem>();
        if (!root.TryGetProperty("software", out var sw) || sw.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in sw.EnumerateArray())
        {
            string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var methods = new List<InstallMethod>();
            if (e.TryGetProperty("install", out var inst) && inst.ValueKind == JsonValueKind.Array)
                foreach (var m in inst.EnumerateArray())
                {
                    string? M(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var kind = M("kind");
                    var method = kind switch
                    {
                        InstallMethod.Pacman when M("package") is { Length: > 0 } => new InstallMethod(kind) { Package = M("package") },
                        InstallMethod.Flatpak when M("ref") is { Length: > 0 } => new InstallMethod(kind) { Ref = M("ref"), Remote = M("remote") ?? "flathub", RemoteUrl = M("remoteUrl") },
                        InstallMethod.AppImage when M("url") is { Length: > 0 } u && u.StartsWith("https://", StringComparison.Ordinal)
                                                    && M("sha256") is { Length: 64 } => new InstallMethod(kind) { Url = M("url"), Sha256 = M("sha256")!.ToLowerInvariant() },
                        _ => null,
                    };
                    if (method != null) methods.Add(method);
                }
            var item = new SoftwareItem
            {
                Source = SoftwareSource.CrimsonBanner,
                Id = S("id") ?? "", Name = S("name") ?? "", Summary = S("summary") ?? "", Description = S("description") ?? "",
                License = S("license"), Developer = S("developer"), Homepage = S("homepage"), Version = S("version"), IconUrl = S("icon"),
                Categories = e.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    ? cats.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).ToList() : new(),
                Screenshots = e.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array
                    ? shots.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String && c.GetString()!.StartsWith("https://", StringComparison.Ordinal)).Select(c => c.GetString()!).ToList() : new(),
                Methods = methods,
            };
            if (item.Id.Length == 0 || item.Name.Length == 0 || methods.Count == 0 || !FreeLicences.IsFree(item.License)) continue;
            list.Add(item);
        }
        return list;
    }

    public const string AppsRepo = "crimson-banner-apps";

    public static bool AppsRepoEnabled(string pacmanConf = "/etc/pacman.conf")
    {
        try { return File.ReadLines(pacmanConf).Any(l => l.Trim() == "[" + AppsRepo + "]"); } catch { return false; }
    }

    /// <summary>Adds [crimson-banner-apps] to pacman (the administrator password is asked for) and refreshes the
    /// package lists. Null when it worked.</summary>
    public static async Task<string?> EnableAppsRepoAsync(CancellationToken ct)
    {
        if (AppsRepoEnabled()) return null;
        var helper = ProcessUtil.Which("crimson-apps-repo");
        var pkexec = ProcessUtil.Which("pkexec");
        if (helper == null || pkexec == null) return "crimson-apps-repo or pkexec is missing, so the apps repository can't be added";
        var (code, output) = await ProcessUtil.RunAsync(pkexec, new[] { helper, "enable" }, 300000);
        if (code != 0) return code == 126 || code == 127 ? "the password wasn't given" : output.Split('\n').LastOrDefault() ?? "it didn't work";
        return await SystemUpdates.RefreshAsync(ct);
    }

    /// <summary>The first way to install it that this computer has the tools for.</summary>
    public static InstallMethod? Usable(SoftwareItem item, Func<string, string?> which, bool? appsRepoEnabled = null) =>
        item.Methods.FirstOrDefault(m => m.Kind switch
        {
            // on Arch, from [crimson-banner-apps]: there already, or crimson-apps-repo can add it
            InstallMethod.Pacman => which("pacman") != null && ((appsRepoEnabled ?? AppsRepoEnabled()) || which("crimson-apps-repo") != null),
            InstallMethod.Flatpak => which("flatpak") != null,
            InstallMethod.AppImage => true,
            _ => false,
        });
}
