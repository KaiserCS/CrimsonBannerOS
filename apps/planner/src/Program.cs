// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;

namespace CrimsonPlanner;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // no telemetry or network of any kind: Planner works on this computer alone
        PlannerApp.StartupFile = args.FirstOrDefault(a => !a.StartsWith('-'));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PlannerApp>().UsePlatformDetect().With(new X11PlatformOptions { WmClass = "crimson-planner" });
}
