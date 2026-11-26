// SPDX-License-Identifier: GPL-3.0-or-later
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Our own apps (apps/, from Software's Crimson Banner tab) are told apart from other Linux programs, so the
/// Start menu lists them with the shell's own apps, in its own categories.</summary>
public class DesktopEntriesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "desktop-test-" + Guid.NewGuid().ToString("N"));
    public DesktopEntriesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private LinuxApp? Entry(string file, string body)
    {
        var p = Path.Combine(_dir, file);
        File.WriteAllText(p, "[Desktop Entry]\nType=Application\n" + body);
        return DesktopEntries.ParseFile(p);
    }

    [Fact]
    public void Our_apps_are_ours_by_their_file_name_or_their_mark_and_others_are_not()
    {
        Assert.True(Entry("crimson-planner.desktop", "Name=Planner\nExec=crimson-planner %f\nCategories=Office;Science;\n")!.Ours);
        Assert.True(Entry("org.example.Thing.desktop", "Name=Thing\nExec=thing\nX-CrimsonBanner-App=true\n")!.Ours);
        Assert.False(Entry("firefox.desktop", "Name=Firefox\nExec=firefox %u\nCategories=Network;WebBrowser;\n")!.Ours);
        Assert.False(Entry("other.desktop", "Name=Other\nExec=other\nX-CrimsonBanner-App=false\n")!.Ours);
    }

    [Theory]
    [InlineData("Office;Science;", "RSOffice")]
    [InlineData("Office;Viewer;Education;Literature;", "RSOffice")]
    [InlineData("AudioVideo;Player;", "RSMedia")]
    [InlineData("Game;BoardGame;", "RSGames")]
    [InlineData("Network;Chat;", "RSOnline")]
    [InlineData("System;Monitor;", "System")]
    [InlineData("Utility;", "RSUtilities")]
    [InlineData("", "RSUtilities")]
    public void An_app_of_ours_goes_in_the_shell_s_own_category(string categories, string expected) =>
        Assert.Equal(expected, DesktopEntries.ShellCategory(categories));
}
