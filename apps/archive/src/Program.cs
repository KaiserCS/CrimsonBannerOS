// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;

namespace PeoplesArchive;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ArchiveApp.StartupFile = args.FirstOrDefault(a => !a.StartsWith('-'));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ArchiveApp>().UsePlatformDetect().With(new X11PlatformOptions { WmClass = "crimson-archive" });
}
