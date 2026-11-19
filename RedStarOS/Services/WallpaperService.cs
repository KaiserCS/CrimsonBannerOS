using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RedStarOS.Services;

public static class WallpaperService
{
    public static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif" };
    public static event Action? Changed;

    public static string DefaultFolder =>
        SettingsStore.Current.WallpaperFolder is { Length: > 0 } f && Directory.Exists(f)
            ? f
            : ImagesIn(WallpaperSearch.SaveFolder, 1).Count > 0 ? WallpaperSearch.SaveFolder : CollectionFolder;

    /// <summary>Set the wallpaper; null restores the built-in one.</summary>
    public static void Set(string? path)
    {
        SettingsStore.Current.WallpaperPath = path;
        SettingsStore.Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// The Red Star collection: five 4K (3840 × 2160) wallpapers shipped inside the app, copied out the first time
    /// they're needed. Older, lower-resolution copies from earlier versions are replaced.
    /// </summary>
    public static string CollectionFolder
    {
        get
        {
            var dir = Path.Combine(AppPaths.DataHome, "redstaros", "wallpapers");
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var old in new[] { "dawn", "night", "rays", "snow", "ribbon" })
                {
                    var f = Path.Combine(dir, $"red-star-{old}.jpg");
                    if (!File.Exists(f)) continue;
                    if (SettingsStore.Current.WallpaperPath == f)          // keep the user's choice, in 4K
                    {
                        SettingsStore.Current.WallpaperPath = Path.Combine(dir, $"red-star-{old}-4k.jpg");
                        SettingsStore.Save();
                    }
                    File.Delete(f);
                }
                foreach (var uri in Avalonia.Platform.AssetLoader.GetAssets(new Uri("avares://RedStarOS/Assets/Wallpapers/"), null))
                {
                    var target = Path.Combine(dir, Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath)));
                    if (File.Exists(target)) continue;
                    using var src = Avalonia.Platform.AssetLoader.Open(uri);
                    using var dst = File.Create(target);
                    src.CopyTo(dst);
                }
            }
            catch { }
            return dir;
        }
    }

    public static List<string> ImagesIn(string folder, int max = 300)
    {
        try
        {
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.EnumerateFiles(folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Next()
    {
        var files = ImagesIn(DefaultFolder);
        if (files.Count == 0) return;
        var cur = SettingsStore.Current.WallpaperPath;
        var idx = cur == null ? -1 : files.IndexOf(cur);
        Set(files[(idx + 1) % files.Count]);
    }
}
