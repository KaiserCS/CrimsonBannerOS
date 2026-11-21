// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

public class AppVersionTests
{
    [Fact]
    public void A_release_parses_as_itself()
    {
        var v = AppVersion.Parse("26.11.0")!;
        Assert.Equal(new Version(26, 11, 0), v.Release);
        Assert.True(v.IsRelease);
        Assert.Equal("26.11.0", v.Full);
        Assert.Equal("26.11.0", v.Display);
        Assert.Equal("26.11.0", AppVersion.Parse("v26.11.0")!.Full);
    }

    [Fact]
    public void A_development_build_keeps_its_distance_and_commit()
    {
        var v = AppVersion.Parse("26.11.0+5.g1a2b3c4")!;
        Assert.False(v.IsRelease);
        Assert.Equal(5, v.CommitsAfter);
        Assert.Equal("1a2b3c4", v.Commit);
        Assert.False(v.LocalChanges);
        Assert.Equal("26.11.0+5.g1a2b3c4", v.Full);
        Assert.Equal("26.11.0 (development build 1a2b3c4, 5 changes after 26.11.0)", v.Display);

        var local = AppVersion.Parse("26.11.0+1.g1a2b3c4.local")!;
        Assert.True(local.LocalChanges);
        Assert.Equal("26.11.0+1.g1a2b3c4.local", local.Full);
        Assert.Contains("1 change after", local.Display);
        Assert.Contains("not committed", local.Display);
    }

    [Fact]
    public void Pacmans_form_and_a_shallow_build_parse_too()
    {
        var pk = AppVersion.Parse("26.11.0.r5.g1a2b3c4")!;
        Assert.Equal("26.11.0+5.g1a2b3c4", pk.Full);
        var shallow = AppVersion.Parse("26.11.0+g1a2b3c4")!;
        Assert.Equal(-1, shallow.CommitsAfter);
        Assert.False(shallow.IsRelease);
        Assert.Equal("26.11.0+g1a2b3c4", shallow.Full);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("26")]
    [InlineData("26.11.0-beta")]
    public void Things_that_arent_versions_are_null(string text) => Assert.Null(AppVersion.Parse(text));

    [Theory]
    [InlineData("26.12.0", "26.11.0", true)]
    [InlineData("26.11.1", "26.11.0+9.gaaaaaaa", true)]     // a release after the one a dev build is of
    [InlineData("26.11.0", "26.11.0+3.gaaaaaaa", false)]    // a dev build already has its release
    [InlineData("26.11.0", "26.11.0", false)]
    [InlineData("26.11.0", "26.12.0", false)]
    [InlineData("26.11.0", "1.3.0", true)]                  // from the old numbering
    [InlineData("v27.1.0", "26.12.4", true)]
    public void A_release_is_newer_by_the_same_order_pacman_uses(string latest, string installed, bool newer) =>
        Assert.Equal(newer, AppVersion.IsNewer(AppVersion.Parse(latest)!, AppVersion.Parse(installed)!));

    [Fact]
    public void The_updater_compares_tags_with_the_installed_version()
    {
        var dev = AppVersion.Parse("26.11.0+4.gabcdef1")!;
        var r = UpdateChecker.Compare("v26.12.0", "https://example.invalid/r", dev);
        Assert.True(r.Newer);
        Assert.Equal("26.12.0", r.Latest);
        Assert.Null(r.Error);
        Assert.False(UpdateChecker.Compare("v26.11.0", null, dev).Newer);
        Assert.NotNull(UpdateChecker.Compare("nightly", null, dev).Error);
    }

    [Fact]
    public void How_it_was_installed()
    {
        Assert.Equal(AppVersion.InstallKind.AppImage, AppVersion.KindOf("/home/u/RedStarOS.AppImage", "/tmp/.mount_x/usr/bin/"));
        Assert.Equal(AppVersion.InstallKind.Package, AppVersion.KindOf(null, "/usr/lib/crimson-banner/"));
        Assert.Equal(AppVersion.InstallKind.Source, AppVersion.KindOf(null, "/home/u/RedStarOS/bin/Release/net8.0/"));
        Assert.Equal(AppVersion.InstallKind.Source, AppVersion.KindOf(null, "/usr/lib/crimson-banner-old/"));
    }

    [Fact]
    public void A_programs_version_line()
    {
        Assert.Equal("26.11.0+5.g1a2b3c4", AppVersion.FromVersionOutput("redstar-comp 26.11.0+5.g1a2b3c4\n"));
        Assert.Equal("26.11.0", AppVersion.FromVersionOutput("redstar-online 26.11.0"));
        Assert.Null(AppVersion.FromVersionOutput("\n"));
    }

    [Fact]
    public void This_build_carries_the_version_from_VERSION()
    {
        // MSBuild reads VERSION into the assembly (Directory.Build.props); tools/version.sh adds the build part
        var v = AppVersion.FromAssembly(typeof(AppVersion).Assembly);
        var file = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "VERSION")).Trim();
        Assert.Equal(file, v.Short);
    }

    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "VERSION"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("VERSION not found above the test's folder");
    }
}
