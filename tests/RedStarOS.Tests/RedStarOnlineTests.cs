// SPDX-License-Identifier: GPL-3.0-or-later
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>The shell's side of Red Star Online (the GTK + WebKitGTK browser, its own process).</summary>
[Collection("environment")]     // sets REDSTAR_ONLINE, writes browser-data.json
public class RedStarOnlineTests
{
    [Fact]
    public void Its_windows_are_the_Red_Star_Online_app()
    {
        Assert.True(RedStarOnline.IsItsWindow("org.redstaros.Online", "org.redstaros.Online"));     // Wayland app id
        Assert.True(RedStarOnline.IsItsWindow("redstar-online", "org.redstaros.Online"));           // X11 class
        Assert.False(RedStarOnline.IsItsWindow("firefox", "Firefox"));
    }

    [Theory]
    [InlineData(1, "exit code 1")]
    [InlineData(136, "signal 8 (SIGFPE)")]           // what .NET reports for a process a signal ended
    [InlineData(139, "signal 11 (SIGSEGV)")]
    [InlineData(134, "signal 6 (SIGABRT)")]
    public void Says_how_it_ended(int code, string text) => Assert.Equal(text, RedStarOnline.Describe(code));

    [Fact]
    public void Pages_and_private_windows()
    {
        Assert.Empty(RedStarOnline.Arguments(null, false));
        Assert.Equal(new[] { "https://example.com/" }, RedStarOnline.Arguments("https://example.com/", false));
        Assert.Equal(new[] { "--private" }, RedStarOnline.Arguments(null, true));
        Assert.Equal(new[] { "--private", "https://example.com/" }, RedStarOnline.Arguments("https://example.com/", true));
    }

    [Fact]
    public void Found_through_REDSTAR_ONLINE()
    {
        var fake = Path.Combine(Path.GetTempPath(), "redstar-online-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(fake, "#!/bin/sh\n");
        var old = Environment.GetEnvironmentVariable("REDSTAR_ONLINE");
        try
        {
            Environment.SetEnvironmentVariable("REDSTAR_ONLINE", fake);
            Assert.Equal(fake, RedStarOnline.ProgramPath());
            Assert.True(RedStarOnline.Available);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDSTAR_ONLINE", old);
            File.Delete(fake);
        }
    }

    [Fact]
    public void The_shell_sees_what_the_browser_saved()
    {
        var path = AppPaths.ConfigFile("browser-data.json");
        const string saved = "{\"Bookmarks\":[{\"Title\":\"From the browser\",\"Url\":\"https://online.test/\",\"Added\":1}],"
            + "\"History\":[],\"Settings\":{\"SearchEngine\":\"Brave\"},\"Session\":[]}";
        // the shell looks at most once a second, and never over changes of its own not yet written; other tests
        // (windows opened in parallel) may leave some, and write them over the browser's file: write those first,
        // and the browser's file again if it was overwritten, until the shell has looked
        for (int i = 0; i < 80 && !BrowserData.Store.Bookmarks.Any(b => b.Url == "https://online.test/"); i++)
        {
            if (BrowserData.HasUnsavedChanges) BrowserData.FlushNow();
            if (!File.Exists(path) || !File.ReadAllText(path).Contains("online.test"))
            {
                // Crimson Online (another process) writes a bookmark, in the same format
                File.WriteAllText(path, saved);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5 + i));
            }
            Thread.Sleep(100);
        }
        Assert.Contains(BrowserData.Store.Bookmarks, b => b.Url == "https://online.test/");
        Assert.Equal("Brave", BrowserData.Settings.SearchEngine);
    }
}
