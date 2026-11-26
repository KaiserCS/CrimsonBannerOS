// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CrimsonAtlas.Model;
using CrimsonAtlas.Views;

namespace CrimsonAtlas;

/// <summary>Atlas: the shell's own look (apps/shared/Look.cs), in the theme and accent it uses.</summary>
public sealed class AtlasApp : Application
{
    /// <summary>A map file to open (Atlas is offered for .map files): copied into the maps folder first.</summary>
    public static string? StartupFile { get; set; }

    public override void Initialize() => CrimsonBanner.Apps.Look.Use(this, "CrimsonAtlas");

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new MapStore();
            string? open = null;
            if (StartupFile is { } f && File.Exists(f) && MapFile.IsMapFile(f))
            {
                try
                {
                    Directory.CreateDirectory(store.Folder);
                    var dest = Path.Combine(store.Folder, Path.GetFileName(f));
                    if (Path.GetFullPath(f) != Path.GetFullPath(dest)) File.Copy(f, dest, overwrite: true);
                    open = dest;
                }
                catch (IOException) { open = null; }
            }
            var w = new AtlasWindow(store, offline: false);
            if (open != null) w.Opened += (_, _) => w.OpenMap(open);
            desktop.MainWindow = w;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
