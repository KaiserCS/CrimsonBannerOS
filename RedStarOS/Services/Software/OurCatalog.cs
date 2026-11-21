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
/// Crimson Banner OS's own software (software/catalog.json, README "Our own software"): programs made for the
/// system that don't belong in every install, such as large specialised tools. The catalogue is published with
/// each release on GitHub Releases (the release's catalog.json); the shell keeps the last one it fetched, and has the
/// copy it was built with for when it can't reach it. Each program lists its ways to install: our pacman
/// repository, a Flatpak or an AppImage with its checksum; the first one this computer can use is offered.
/// </summary>
public static class OurCatalog
{
    public const string DefaultUrl = "https://github.com/KaiserCS/RedStarOS-Linux/releases/latest/download/catalog.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string CachePath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "redstaros", "catalog.json");

    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "software", "catalog.json");

    /// <summary>The catalogue: fetched now when it can be (into the cache), else the cached one, else the bundled one.</summary>
    public static async Task<(List<SoftwareItem> Items, string? Note)> LoadAsync(string? url, bool fetch, CancellationToken ct = default)
    {
        string? note = null;
        if (fetch)
        {
            try
            {
                Http.DefaultRequestHeaders.UserAgent.TryParseAdd("CrimsonBanner-Software/" + AppVersion.Current.Short);
                var text = await Http.GetStringAsync(string.IsNullOrWhiteSpace(url) ? DefaultUrl : url, ct);
                var items = Parse(text);  // only cache what parses
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                await File.WriteAllTextAsync(CachePath, text, ct);
                return (items, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                note = "Couldn't fetch the latest list (" + ex.Message + "); showing the last one.";
            }
        }
        foreach (var path in new[] { CachePath, BundledPath })
        {
            try { if (File.Exists(path)) return (Parse(await File.ReadAllTextAsync(path, ct)), note); }
            catch (Exception) { /* a damaged cache: try the next */ }
        }
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
                Methods = methods,
            };
            if (item.Id.Length == 0 || item.Name.Length == 0 || methods.Count == 0 || !FreeLicences.IsFree(item.License)) continue;
            list.Add(item);
        }
        return list;
    }

    /// <summary>The first way to install it that this computer has the tools for.</summary>
    public static InstallMethod? Usable(SoftwareItem item, Func<string, string?> which) =>
        item.Methods.FirstOrDefault(m => m.Kind switch
        {
            InstallMethod.Pacman => which("pacman") != null,
            InstallMethod.Flatpak => which("flatpak") != null,
            InstallMethod.AppImage => true,
            _ => false,
        });
}
