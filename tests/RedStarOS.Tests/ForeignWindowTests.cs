using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Other programs on the taskbar: matching a window (WM_CLASS) to its installed app.</summary>
[Collection("environment")]     // changes XDG_DATA_DIRS
public class ForeignWindowTests
{
    private static string MakeApps()
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-apps-" + Guid.NewGuid().ToString("N"));
        var apps = Path.Combine(root, "applications");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "org.gnome.TextEditor.desktop"), "[Desktop Entry]\nType=Application\nName=Text Editor\nExec=gnome-text-editor %U\nIcon=org.gnome.TextEditor\n");
        File.WriteAllText(Path.Combine(apps, "zz-firefox-test.desktop"), "[Desktop Entry]\nType=Application\nName=Firefox Test\nExec=/usr/lib/firefox/firefox %u\nStartupWMClass=firefox-test\nIcon=firefox\n");
        File.WriteAllText(Path.Combine(apps, "gimp-test.desktop"), "[Desktop Entry]\nType=Application\nName=GIMP Test\nExec=gimp-test %U\n");
        Environment.SetEnvironmentVariable("XDG_DATA_DIRS", root + ":" + (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/share"));
        DesktopEntries.Reload();
        return apps;
    }

    [Fact]
    public void Windows_are_matched_to_their_installed_app()
    {
        var dir = MakeApps();
        Assert.Equal("Firefox Test", DesktopEntries.ForWindow("Navigator", "firefox-test")?.Name);            // StartupWMClass
        Assert.Equal("Text Editor", DesktopEntries.ForWindow("gnome-text-editor", "org.gnome.TextEditor")?.Name); // file name
        Assert.Equal("GIMP Test", DesktopEntries.ForWindow("gimp-test", "Gimp-test")?.Name);                      // program name
        Assert.Null(DesktopEntries.ForWindow("no-such-program-xyz", "NoSuchProgramXyz"));
        var app = DesktopEntries.ForWindow("Navigator", "firefox-test")!;
        Assert.Equal("linux:" + Path.Combine(dir, "zz-firefox-test.desktop"), app.Id);
        Assert.Equal("Firefox Test", DesktopEntries.FromId(app.Id)?.Name);                                   // pins resolve back
    }
}
