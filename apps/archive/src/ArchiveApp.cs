// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using PeoplesArchive.Views;

namespace PeoplesArchive;

/// <summary>People's Archive: the shell's own look (apps/shared/Look.cs), in the theme and accent it uses.</summary>
public sealed class ArchiveApp : Application
{
    public static string? StartupFile { get; set; }

    public override void Initialize() => CrimsonBanner.Apps.Look.Use(this, "PeoplesArchive");

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new ArchiveWindow();
            desktop.MainWindow = w;
            if (StartupFile != null && File.Exists(StartupFile)) w.Opened += (_, _) => w.OpenFile(StartupFile);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
