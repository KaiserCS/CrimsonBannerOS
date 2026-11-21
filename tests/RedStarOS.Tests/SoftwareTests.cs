// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RedStarOS.Services.Software;
using Xunit;

namespace RedStarOS.Tests;

public class FreeLicencesTests
{
    [Theory]
    [InlineData("GPL-3.0-or-later")]
    [InlineData("GPL-3.0+")]
    [InlineData("MIT")]
    [InlineData("(MIT OR Apache-2.0) AND GPL-2.0-or-later")]
    [InlineData("GPL-2.0-only WITH Classpath-exception-2.0")]
    [InlineData("MIT OR LicenseRef-proprietary")]           // free under its MIT option
    [InlineData("CC0-1.0 AND MPL-2.0")]
    public void Free(string spdx) => Assert.Equal(FreeLicences.Verdict.Free, FreeLicences.Judge(spdx));

    [Theory]
    [InlineData("LicenseRef-proprietary")]
    [InlineData("LicenseRef-proprietary=https://example.com/eula")]
    [InlineData("MIT AND LicenseRef-proprietary")]           // needs both
    [InlineData("CC-BY-NC-4.0")]                             // not a free licence
    [InlineData("Some-Unknown-1.0")]                          // unknown: never presented as free
    [InlineData("(MIT")]
    [InlineData("MIT AND")]
    public void Not_free(string spdx) => Assert.Equal(FreeLicences.Verdict.NotFree, FreeLicences.Judge(spdx));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Unstated(string? spdx) => Assert.Equal(FreeLicences.Verdict.Unstated, FreeLicences.Judge(spdx));
}

public class FlatpakTests
{
    private const string AppStream = """
        <?xml version="1.0" encoding="UTF-8"?>
        <components version="0.8" origin="flathub">
          <component type="desktop-application">
            <id>org.gnome.Mahjongg</id>
            <name>Mahjongg</name>
            <name xml:lang="de">Mahjongg (de)</name>
            <summary>Match tiles and clear the board</summary>
            <description><p>A worthy   challenge.</p><p xml:lang="de">Nein</p><ul><li>Many layouts</li><li>Themes</li></ul></description>
            <project_license>GPL-3.0-or-later</project_license>
            <developer id="org.gnome"><name>The GNOME Project</name></developer>
            <url type="homepage">https://wiki.gnome.org/Apps/Mahjongg</url>
            <icon type="cached" width="64" height="64">org.gnome.Mahjongg.png</icon>
            <icon type="cached" width="128" height="128">org.gnome.Mahjongg.png</icon>
            <categories><category>Game</category><category>BoardGame</category></categories>
            <releases><release version="48.1" timestamp="1"/><release version="48.0"/></releases>
            <bundle type="flatpak" runtime="org.gnome.Platform/x86_64/48">app/org.gnome.Mahjongg/x86_64/stable</bundle>
          </component>
          <component type="desktop-application">
            <id>com.example.Closed</id>
            <name>Closed</name>
            <project_license>LicenseRef-proprietary</project_license>
            <bundle type="flatpak">app/com.example.Closed/x86_64/stable</bundle>
          </component>
          <component type="runtime"><id>org.gnome.Platform</id><name>GNOME</name><project_license>LGPL-2.1</project_license></component>
          <component type="desktop">
            <id>org.example.Old.desktop</id>
            <name>Old style</name>
            <project_license>MIT</project_license>
            <bundle type="flatpak">app/org.example.Old/x86_64/stable</bundle>
          </component>
        </components>
        """;

    [Fact]
    public void AppStream_gives_the_free_applications_with_their_details()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "icons", "128x128"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "icons", "128x128", "org.gnome.Mahjongg.png"), new byte[] { 1 });
            var items = Flatpak.ParseAppStream(XDocument.Parse(AppStream), dir.FullName, "flathub").ToList();
            Assert.Equal(new[] { "org.gnome.Mahjongg", "org.example.Old" }, items.Select(i => i.Id));
            var m = items[0];
            Assert.Equal("Mahjongg", m.Name);
            Assert.Equal("Match tiles and clear the board", m.Summary);
            Assert.Equal("A worthy challenge.\n\n• Many layouts\n• Themes", m.Description.Replace("\r", ""));
            Assert.Equal("The GNOME Project", m.Developer);
            Assert.Equal("48.1", m.Version);
            Assert.Equal(Path.Combine(dir.FullName, "icons", "128x128", "org.gnome.Mahjongg.png"), m.IconPath);
            Assert.Equal(new[] { "Game", "BoardGame" }, m.Categories);
            Assert.Equal("org.gnome.Mahjongg", m.Methods.Single().Ref);
            Assert.Equal("flathub", m.Methods.Single().Remote);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Flatpak_list_rows()
    {
        var rows = Flatpak.ParseList("org.gnome.Mahjongg\tMahjongg\t48.1\tstable\tuser\tflathub\n" +
                                     "org.kde.kate\tKate\t25.08\tstable\tsystem\tflathub\nnot a row\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new Flatpak.Installed("org.kde.kate", "Kate", "25.08", "stable", "system", "flathub"), rows[1]);
    }
}

public class OurCatalogTests
{
    private const string Catalog = """
        {
          "format": 1,
          "software": [
            { "id": "planner", "name": "Planner", "summary": "Economic computation for central planning", "license": "GPL-3.0-or-later",
              "version": "1.0.0", "categories": ["Science"],
              "install": [
                { "kind": "pacman", "package": "crimson-planner" },
                { "kind": "flatpak", "ref": "org.crimsonbanner.Planner", "remote": "crimson-banner", "remoteUrl": "https://example.org/x.flatpakrepo" },
                { "kind": "appimage", "url": "https://example.org/Planner.AppImage", "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }
              ] },
            { "id": "nochecksum", "name": "No checksum", "license": "MIT", "install": [ { "kind": "appimage", "url": "https://example.org/a.AppImage" } ] },
            { "id": "plainhttp", "name": "Plain HTTP", "license": "MIT", "install": [ { "kind": "appimage", "url": "http://example.org/a.AppImage", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" } ] },
            { "id": "closed", "name": "Closed", "license": "LicenseRef-proprietary", "install": [ { "kind": "pacman", "package": "closed" } ] },
            { "id": "", "name": "No id", "license": "MIT", "install": [ { "kind": "pacman", "package": "x" } ] },
          ]
        }
        """;

    [Fact]
    public void Only_free_complete_and_checksummed_entries()
    {
        var items = OurCatalog.Parse(Catalog);
        var p = Assert.Single(items);
        Assert.Equal("planner", p.Id);
        Assert.Equal(3, p.Methods.Count);
        Assert.Equal("a" + new string('a', 63), p.Methods[2].Sha256);
        Assert.Equal("crimson-banner", p.Methods[1].Remote);
    }

    [Fact]
    public void The_first_usable_way_to_install()
    {
        var p = OurCatalog.Parse(Catalog).Single();
        Assert.Equal(InstallMethod.Pacman, OurCatalog.Usable(p, n => n == "pacman" ? "/usr/bin/pacman" : null)!.Kind);
        Assert.Equal(InstallMethod.Flatpak, OurCatalog.Usable(p, n => n == "flatpak" ? "/usr/bin/flatpak" : null)!.Kind);
        Assert.Equal(InstallMethod.AppImage, OurCatalog.Usable(p, _ => null)!.Kind);
    }

    [Fact]
    public void A_newer_format_is_refused_rather_than_misread() =>
        Assert.Throws<FormatException>(() => OurCatalog.Parse("""{ "format": 2, "software": [] }"""));

    [Fact]
    public void The_shipped_catalogue_parses()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "software", "catalog.json"))) d = d.Parent;
        Assert.NotNull(d);
        OurCatalog.Parse(File.ReadAllText(Path.Combine(d!.FullName, "software", "catalog.json")));
    }
}

public class AppImagesTests
{
    [Fact]
    public void AppImageHubs_feed_gives_projects_with_a_GitHub_release_and_no_closed_licence()
    {
        const string feed = """
            { "items": [
              { "name": "Kdenlive", "description": "<p>Video editor. Does lots.</p>", "license": "GPL-3.0-or-later",
                "categories": ["AudioVideo"], "authors": [ { "name": "KDE", "url": "https://kdenlive.org" } ],
                "links": [ { "type": "GitHub", "url": "KDE/kdenlive" }, { "type": "Download", "url": "https://github.com/KDE/kdenlive/releases" } ],
                "icons": [ "Kdenlive/icons/128x128/kdenlive.png" ] },
              { "name": "NoSource", "license": "MIT", "links": [ { "type": "Download", "url": "https://example.org" } ] },
              { "name": "Closed", "license": "LicenseRef-proprietary", "links": [ { "type": "GitHub", "url": "a/b" } ] },
              { "name": "Unstated", "license": null, "links": [ { "type": "GitHub", "url": "c/d" } ] },
              { "name": "Kdenlive", "license": "MIT", "links": [ { "type": "GitHub", "url": "e/f" } ] }
            ] }
            """;
        var items = AppImages.ParseFeed(feed);
        Assert.Equal(new[] { "Kdenlive", "Unstated" }, items.Select(i => i.Name));
        var k = items[0];
        Assert.Equal("KDE/kdenlive", k.Methods.Single().GitHub);
        Assert.Equal("Video editor.", k.Summary);
        Assert.Equal(AppImages.IconBase + "Kdenlive/icons/128x128/kdenlive.png", k.IconUrl);
        Assert.Equal("https://kdenlive.org", k.Homepage);
        Assert.Equal(FreeLicences.Verdict.Unstated, items[1].Licence);
    }

    private const string Release = """
        { "tag_name": "v25.08.1", "assets": [
          { "name": "app-25.08.1-i386.AppImage", "browser_download_url": "https://github.com/x/i386", "size": 1 },
          { "name": "app-25.08.1-aarch64.AppImage", "browser_download_url": "https://github.com/x/arm", "size": 2 },
          { "name": "app-25.08.1-x86_64.AppImage", "browser_download_url": "https://github.com/x/x64", "size": 3, "digest": "sha256:abc123" },
          { "name": "app-25.08.1-x86_64.AppImage.zsync", "browser_download_url": "https://github.com/x/zsync", "size": 4 },
          { "name": "app.tar.gz", "browser_download_url": "https://github.com/x/tgz", "size": 5 }
        ] }
        """;

    [Fact]
    public void The_release_asset_for_this_architecture()
    {
        var x = AppImages.PickAsset(Release, "x86_64")!;
        Assert.Equal("https://github.com/x/x64", x.Url);
        Assert.Equal("v25.08.1", x.Tag);
        Assert.Equal("abc123", x.Sha256);
        Assert.Equal("https://github.com/x/arm", AppImages.PickAsset(Release, "aarch64")!.Url);
        // one AppImage that doesn't say its architecture is taken as it is
        Assert.Equal("https://github.com/y/one", AppImages.PickAsset("""{ "tag_name": "1", "assets": [ { "name": "One.AppImage", "browser_download_url": "https://github.com/y/one" } ] }""", "x86_64")!.Url);
        Assert.Null(AppImages.PickAsset("""{ "tag_name": "1", "assets": [ { "name": "One-armhf.AppImage", "browser_download_url": "https://github.com/y/one" } ] }""", "x86_64"));
    }

    [Fact]
    public void Only_a_real_AppImage_is_kept()
    {
        var f = Path.GetTempFileName();
        try
        {
            var bytes = new byte[64];
            new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }.CopyTo(bytes, 0);
            bytes[8] = (byte)'A'; bytes[9] = (byte)'I'; bytes[10] = 2;
            File.WriteAllBytes(f, bytes);
            Assert.True(AppImages.IsAppImage(f));
            bytes[10] = 0; File.WriteAllBytes(f, bytes);
            Assert.False(AppImages.IsAppImage(f));
            File.WriteAllText(f, "<html>not found</html>");
            Assert.False(AppImages.IsAppImage(f));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void The_start_menu_entry_runs_the_file_and_reads_back()
    {
        var text = AppImages.DesktopEntry("Kden\nlive", "/home/u/Applications/My \"App\" $x.AppImage", "/i/k.png", "AudioVideo;", null);
        var d = AppImages.ReadDesktop(text);
        Assert.Equal("Kden live", d["Name"]);
        Assert.Equal("\"/home/u/Applications/My \\\\\"App\\\\\" \\\\$x.AppImage\" %U", d["Exec"]);
        Assert.Equal("/i/k.png", d["Icon"]);
        Assert.Equal("AudioVideo;", d["Categories"]);
        Assert.Equal("Utility;", AppImages.ReadDesktop(AppImages.DesktopEntry("A", "/a.AppImage", null, null, null))["Categories"]);
    }

    [Fact]
    public void A_desktop_files_own_section_only()
    {
        var d = AppImages.ReadDesktop("[Desktop Entry]\nName=App\nName[de]=Anw\nExec=app\n[Desktop Action new]\nName=New window\n");
        Assert.Equal("App", d["Name"]);
        Assert.False(d.ContainsKey("Name[de]"));
    }

    [Theory]
    [InlineData("Kdenlive", "Kdenlive")]
    [InlineData("My App / 2", "My-App-2")]
    [InlineData("../..", "app")]
    public void Safe_file_names(string name, string safe) => Assert.Equal(safe, AppImages.SafeName(name));
}
