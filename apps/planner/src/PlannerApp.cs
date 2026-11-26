// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CrimsonPlanner.Views;

namespace CrimsonPlanner;

/// <summary>Planner: the shell's own look (apps/shared/Look.cs), in the theme and accent it uses.</summary>
public sealed class PlannerApp : Application
{
    public static string? StartupFile { get; set; }

    public override void Initialize()
    {
        CrimsonBanner.Apps.Look.Use(this, "CrimsonPlanner");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new PlannerWindow(StartupFile);
        base.OnFrameworkInitializationCompleted();
    }
}
