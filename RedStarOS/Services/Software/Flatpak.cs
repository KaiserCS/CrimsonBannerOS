// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace RedStarOS.Services.Software;

/// <summary>
/// Flatpak, through its own command (flatpak), and what Flathub offers, from the AppStream data flatpak keeps on disk
/// (no web API). New programs go into your own installation (--user), which needs no administrator password;
/// programs already installed system-wide are updated and removed where they are (polkit asks then). Only free
/// software is listed (<see cref="FreeLicences"/>).
/// </summary>
public static class Flatpak
{
    public const string Flathub = "flathub", FlathubRepo = "https://dl.flathub.org/repo/flathub.flatpakrepo";

    public static string? Program => ProcessUtil.Which("flatpak");
    public static bool Available => Program != null;

    public static string Arch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "i386",
        _ => "x86_64",
    };

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string UserDir => Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".local", "share"), "flatpak");
    public const string SystemDir = "/var/lib/flatpak";

    /// <summary>Flathub in your own installation (added when missing), and its catalogue brought up to date.</summary>
    public static async Task<string?> PrepareAsync(CancellationToken ct)
    {
        if (Program is not { } fp) return "Flatpak isn't installed (Arch/CachyOS: pacman -S flatpak).";
        var (code, tail) = await Runner.RunAsync(fp, new[] { "remote-add", "--user", "--if-not-exists", Flathub, FlathubRepo }, null, ct);
        if (code != 0) return "Couldn't add Flathub: " + Last(tail);
        (code, tail) = await Runner.RunAsync(fp, new[] { "update", "--user", "--appstream", "--noninteractive", Flathub }, null, ct);
        return code == 0 ? null : "Couldn't fetch Flathub's catalogue: " + Last(tail);
    }

    /// <summary>The free programs in Flathub's catalogue, from whichever installations have it.</summary>
    public static List<SoftwareItem> Catalogue(string remote = Flathub)
    {
        var byId = new Dictionary<string, SoftwareItem>();
        foreach (var root in new[] { UserDir, SystemDir })
        {
            var dir = Path.Combine(root, "appstream", remote, Arch, "active");
            var file = new[] { "appstream.xml.gz", "appstream.xml" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
            if (file == null) continue;
            try
            {
                using var fs = File.OpenRead(file);
                using Stream s = file.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(fs, CompressionMode.Decompress) : (Stream)fs;
                foreach (var item in ParseAppStream(XDocument.Load(s), dir, remote))
                    byId.TryAdd(item.Id, item);
            }
            catch (Exception) { /* a damaged file: the other installation may have a good one */ }
        }
        return byId.Values.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>AppStream's components → programs; only applications, only free software.</summary>
    public static IEnumerable<SoftwareItem> ParseAppStream(XDocument doc, string? iconDir, string remote)
    {
        XNamespace xml = XNamespace.Xml;
        foreach (var c in doc.Root?.Elements("component") ?? Enumerable.Empty<XElement>())
        {
            var type = (string?)c.Attribute("type");
            if (type != "desktop-application" && type != "desktop") continue;
            string? Plain(string name) => c.Elements(name).FirstOrDefault(e => e.Attribute(xml + "lang") == null)?.Value.Trim();
            var id = Plain("id");
            var bundle = c.Elements("bundle").FirstOrDefault(b => (string?)b.Attribute("type") == "flatpak")?.Value.Trim();
            var name = Plain("name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || bundle == null) continue;
            var licence = Plain("project_license");
            if (!FreeLicences.IsFree(licence)) continue;
            // the app id is the bundle's second part (app/org.example.App/x86_64/stable)
            var parts = bundle.Split('/');
            var appId = parts.Length >= 2 ? parts[1] : id;
            if (appId.EndsWith(".desktop", StringComparison.Ordinal)) appId = appId[..^8];
            string? icon = null;
            if (iconDir != null)
                foreach (var ic in c.Elements("icon").Where(i => (string?)i.Attribute("type") == "cached")
                             .OrderByDescending(i => (int?)i.Attribute("width") ?? 0))
                {
                    var w = (int?)ic.Attribute("width") ?? 64;
                    var p = Path.Combine(iconDir, "icons", $"{w}x{w}", ic.Value.Trim());
                    if (File.Exists(p)) { icon = p; break; }
                }
            yield return new SoftwareItem
            {
                Source = SoftwareSource.Flatpak,
                Id = appId,
                Name = name,
                Summary = Plain("summary") ?? "",
                Description = DescriptionText(c.Elements("description").FirstOrDefault(e => e.Attribute(xml + "lang") == null)),
                License = licence,
                Developer = c.Element("developer")?.Element("name")?.Value.Trim() ?? Plain("developer_name"),
                Homepage = c.Elements("url").FirstOrDefault(u => (string?)u.Attribute("type") == "homepage")?.Value.Trim(),
                Version = c.Element("releases")?.Elements("release").FirstOrDefault()?.Attribute("version")?.Value,
                IconPath = icon,
                Categories = c.Element("categories")?.Elements("category").Select(x => x.Value.Trim()).ToList() ?? new(),
                Methods = { new InstallMethod(InstallMethod.Flatpak) { Remote = remote, Ref = appId } },
            };
        }
    }

    /// <summary>AppStream's description markup (p, ul/ol li) as plain text.</summary>
    public static string DescriptionText(XElement? d)
    {
        if (d == null) return "";
        XNamespace xml = XNamespace.Xml;
        var sb = new StringBuilder();
        foreach (var e in d.Elements().Where(e => e.Attribute(xml + "lang") == null))
        {
            if (e.Name.LocalName == "p") sb.AppendLine(Squash(e.Value)).AppendLine();
            else if (e.Name.LocalName is "ul" or "ol")
            {
                foreach (var li in e.Elements("li").Where(l => l.Attribute(xml + "lang") == null)) sb.AppendLine("• " + Squash(li.Value));
                sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
    }

    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public sealed record Installed(string Id, string Name, string Version, string Branch, string Installation, string Origin);

    /// <summary>`flatpak list --app --columns=application,name,version,branch,installation,origin` → its rows.</summary>
    public static List<Installed> ParseList(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(c => c.Length >= 6 && c[0].Contains('.'))
            .Select(c => new Installed(c[0].Trim(), c[1].Trim(), c[2].Trim(), c[3].Trim(), c[4].Trim(), c[5].Trim()))
            .ToList();

    public static async Task<List<Installed>> InstalledAsync()
    {
        if (Program is not { } fp) return new();
        var (code, output) = await ProcessUtil.RunAsync(fp, new[] { "list", "--app", "--columns=application,name,version,branch,installation,origin" }, 20000);
        return code == 0 ? ParseList(output) : new();
    }

    /// <summary>The ids of installed programs that have an update waiting.</summary>
    public static async Task<HashSet<string>> UpdatesAsync()
    {
        var set = new HashSet<string>();
        if (Program is not { } fp) return set;
        foreach (var where in new[] { "--user", "--system" })
        {
            var (code, output) = await ProcessUtil.RunAsync(fp, new[] { "remote-ls", where, "--updates", "--app", "--columns=application" }, 60000);
            if (code == 0) foreach (var l in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) if (l.Contains('.')) set.Add(l.Trim());
        }
        return set;
    }

    public static async Task<string?> InstallAsync(InstallMethod m, Action<int>? progress, CancellationToken ct)
    {
        if (Program is not { } fp) return "Flatpak isn't installed (Arch/CachyOS: pacman -S flatpak).";
        var remote = m.Remote ?? Flathub;
        if (m.RemoteUrl is { } url && url.StartsWith("https://", StringComparison.Ordinal))
        {
            var (c, t) = await Runner.RunAsync(fp, new[] { "remote-add", "--user", "--if-not-exists", remote, url }, null, ct);
            if (c != 0) return $"Couldn't add {remote}: " + Last(t);
        }
        var (code, tail) = await Runner.RunAsync(fp, new[] { "install", "--user", "--noninteractive", "-y", remote, m.Ref! }, progress, ct);
        return code == 0 ? null : Last(tail);
    }

    /// <summary>Removes it from the installation it's in ("user" or "system").</summary>
    public static async Task<string?> RemoveAsync(string id, string? installation, CancellationToken ct)
    {
        if (Program is not { } fp) return "Flatpak isn't installed.";
        var (code, tail) = await Runner.RunAsync(fp, new[] { "uninstall", Where(installation), "--noninteractive", "-y", id }, null, ct);
        return code == 0 ? null : Last(tail);
    }

    public static async Task<string?> UpdateAsync(string? id, string? installation, Action<int>? progress, CancellationToken ct)
    {
        if (Program is not { } fp) return "Flatpak isn't installed.";
        var args = new List<string> { "update", "--noninteractive", "-y" };
        if (id != null) { args.Insert(1, Where(installation)); args.Add(id); }
        var (code, tail) = await Runner.RunAsync(fp, args, progress, ct);
        return code == 0 ? null : Last(tail);
    }

    public static bool Run(string id) => Program is { } fp && ProcessUtil.StartDetached(fp, new[] { "run", id });

    private static string Where(string? installation) => installation == "system" ? "--system" : "--user";
    private static string Last(string tail) => tail.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "it didn't work";
}
