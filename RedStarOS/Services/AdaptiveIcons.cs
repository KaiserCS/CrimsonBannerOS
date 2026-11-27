// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>
/// The rest of the Crimson Banner icon theme. The theme draws programs with a general job itself (icons/CrimsonBanner,
/// branding/icon-theme.py); every other installed program, Firefox or Steam or LibreOffice, keeps its own icon, set on
/// the same graphite tile, so it matches without being redrawn. Those pictures go in your own copy of the theme
/// (~/.local/share/icons/CrimsonBanner, which GTK, Qt and the shell read together with the system's), made again when a
/// program is installed or its icon changes, and only while Crimson Banner's theme is the one in use.
/// SVG-only icons are drawn with rsvg-convert (librsvg, there with GTK) into ~/.cache/redstaros/icons, which also lets
/// the Start menu show them instead of an initial.
/// </summary>
public static class AdaptiveIcons
{
    public static readonly int[] Sizes = { 32, 48, 64, 128 };

    /// <summary>How much of the tile a program's own icon covers (its longer side).</summary>
    public const double Inset = 0.64;

    public static string UserThemeDir => Path.Combine(AppPaths.DataHome, "icons", LookAndFeel.OurIconTheme);

    private static string CacheDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c : Path.Combine(AppPaths.Home, ".cache"), "redstaros", "icons");

    /// <summary>Where an SVG-only icon's PNG is kept (it may not be made yet).</summary>
    public static string SvgPicture(string icon) => Path.Combine(CacheDir, icon + ".png");

    /// <summary>Raised on the UI thread when new pictures were written (the Start menu and taskbar look again).</summary>
    public static event Action? Changed;

    private static bool _running;
    private static string? _done;      // the program list and theme last done

    /// <summary>Brings the pictures up to date in the background, unless nothing has changed since last time.
    /// Called on the UI thread (the program list is read there; the work happens off it).</summary>
    public static void RefreshSoon()
    {
        if (_running) return;
        var apps = DesktopEntries.All();
        var theme = LookAndFeel.EffectiveIconTheme(SettingsStore.Current);
        var key = DesktopEntries.CurrentSignature + "|" + theme;
        if (key == _done) return;
        _running = true;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(apps, theme); _done = key; }
            catch (Exception e) { Console.Error.WriteLine("icons: " + e.Message); }
            finally { _running = false; }
        });
    }

    /// <summary>One program's picture to make: its icon name and where its own icon is.</summary>
    public sealed record Job(string Icon, string Source);

    /// <summary>
    /// Which of the programs' icons need a picture: named icons (not paths) that the theme doesn't draw, whose own
    /// icon can be found, and whose picture is missing or older than that icon. Public for the tests.
    /// </summary>
    public static List<Job> Plan(IEnumerable<string?> icons, ISet<string> drawn, Func<string, string?> original, string themeDir)
    {
        var jobs = new List<Job>();
        foreach (var icon in icons.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!.Trim()).Distinct())
        {
            if (Path.IsPathRooted(icon) || icon.Contains('/') || drawn.Contains(icon)) continue;
            if (original(icon) is not { } src) continue;
            var made = Path.Combine(themeDir, "128x128", "apps", icon + ".png");
            if (File.Exists(made) && File.GetLastWriteTimeUtc(made) >= File.GetLastWriteTimeUtc(src)) continue;
            jobs.Add(new Job(icon, src));
        }
        return jobs;
    }

    private static async Task RefreshAsync(IReadOnlyList<LinuxApp> apps, string? theme)
    {
        bool wrote = false;

        // SVG-only icons as PNGs, whatever the theme: the shell can't read SVG
        if (ProcessUtil.Which("rsvg-convert") is { } rsvg)
            foreach (var icon in apps.Select(a => a.Icon).OfType<string>().Where(i => !Path.IsPathRooted(i)).Distinct())
            {
                var png = SvgPicture(icon);
                if (File.Exists(png) || DesktopEntries.OriginalIconPath(icon) is not { } src || !src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(CacheDir);
                var (code, _) = await ProcessUtil.RunAsync(rsvg, new[] { "-w", "256", "-h", "256", "-a", "-o", png, src }, 5000);
                if (code != 0) try { File.Delete(png); } catch { }
                else wrote = true;
            }

        if (theme == LookAndFeel.OurIconTheme && LookAndFeel.SystemThemeDir(LookAndFeel.OurIconTheme) is { } system)
        {
            var drawn = ReadNames(Path.Combine(system, "names.txt"));
            var dir = UserThemeDir;
            // your copy lists the same folders as the system's, so the two read as one theme
            Directory.CreateDirectory(dir);
            var index = Path.Combine(dir, "index.theme");
            var want = File.ReadAllText(Path.Combine(system, "index.theme"));
            if (!File.Exists(index) || File.ReadAllText(index) != want) File.WriteAllText(index, want);

            string? Source(string icon) => DesktopEntries.OriginalIconPath(icon) is { } p && p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? (File.Exists(SvgPicture(icon)) ? SvgPicture(icon) : null)
                : DesktopEntries.OriginalIconPath(icon);
            var icons = apps.Select(a => a.Icon).ToList();
            foreach (var job in Plan(icons, drawn, Source, dir))
            {
                await Dispatcher.UIThread.InvokeAsync(() => Write(job, dir), DispatcherPriority.Background);
                wrote = true;
            }
            Prune(dir, icons.OfType<string>().ToHashSet());
        }

        if (wrote)
            Dispatcher.UIThread.Post(() => { DesktopEntries.ForgetIcons(); Changed?.Invoke(); });
    }

    private static HashSet<string> ReadNames(string path)
    {
        try { return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(); }
        catch { return new HashSet<string>(); }
    }

    /// <summary>Pictures of programs no longer installed go.</summary>
    private static void Prune(string dir, HashSet<string> icons)
    {
        foreach (var s in Sizes)
        {
            var d = Path.Combine(dir, $"{s}x{s}", "apps");
            if (!Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.png"))
                if (!icons.Contains(Path.GetFileNameWithoutExtension(f)))
                    try { File.Delete(f); } catch { }
        }
    }

    private static void Write(Job job, string dir)
    {
        Bitmap own;
        try { own = new Bitmap(job.Source); }
        catch (Exception e) { Console.Error.WriteLine($"icons: {job.Icon}: {e.Message}"); return; }
        using (own)
            foreach (var s in Sizes)
            {
                var path = Path.Combine(dir, $"{s}x{s}", "apps", job.Icon + ".png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var picture = Draw(own, s);
                picture.Save(path);
            }
    }

    /// <summary>A program's own icon on the graphite tile, <paramref name="size"/> pixels square.</summary>
    public static RenderTargetBitmap Draw(Bitmap own, int size)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            // the tile as the app pictures draw it: 240 of 256, corners of 54, a fine light rim
            double inner = size * 240.0 / 256, off = (size - inner) / 2, r = inner * 0.225;
            ctx.DrawRectangle(AppIcons.Plate, new Pen(AppIcons.Rim, Math.Max(1, size / 128.0)), new Rect(off, off, inner, inner), r, r);
            var src = new Rect(own.Size);
            double box = size * Inset, scale = box / Math.Max(src.Width, src.Height);
            double w = src.Width * scale, h = src.Height * scale;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(own, src, new Rect((size - w) / 2, (size - h) / 2, w, h));
        }
        return rtb;
    }
}
