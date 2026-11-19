using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// Print takes a picture of every screen, Shift+Print of the active window, saved in ~/Pictures/Screenshots.
/// Inside redstar-comp this uses grim (Wayland screencopy: Wayland and X11 windows alike); on X11, the first of
/// scrot, maim or ImageMagick's import that is installed (only scrot can take just the active window).
/// </summary>
public static class Screenshots
{
    public static string Folder => Path.Combine(AppPaths.UserFolder("Pictures"), "Screenshots");

    /// <summary>The file name for a picture taken now (public for the tests).</summary>
    public static string NewPath(DateTime when) => Path.Combine(Folder, $"Screenshot {when:yyyy-MM-dd HH-mm-ss}.png");

    /// <summary>The program and arguments that take it, or null when none is installed (public for the tests).
    /// <paramref name="window"/>: just the active window — inside redstar-comp its frame's geometry ("x,y wxh").</summary>
    public static (string Exe, List<string> Args)? Command(string file, bool window, string? geometry, bool wayland, Func<string, string?> which)
    {
        if (wayland)
        {
            if (which("grim") is not { } grim) return null;
            var a = new List<string>();
            if (window && geometry != null) { a.Add("-g"); a.Add(geometry); }
            a.Add(file);
            return (grim, a);
        }
        if (which("scrot") is { } scrot) return (scrot, window ? new List<string> { "-u", "-o", file } : new List<string> { "-o", file });
        if (which("maim") is { } maim) return (maim, new List<string> { file });
        if (which("import") is { } import) return (import, new List<string> { "-window", "root", file });
        return null;
    }

    /// <summary>"screenshot" or "screenshot-window\t&lt;x,y wxh&gt;" from the global shortcuts.</summary>
    public static async Task Take(string shortcut)
    {
        string? geometry = null;
        var tab = shortcut.IndexOf('\t');
        if (tab > 0) geometry = shortcut[(tab + 1)..];
        bool wayland = InputIsolation.InRedStarComp;
        var file = NewPath(DateTime.Now);
        var cmd = Command(file, shortcut.StartsWith("screenshot-window"), geometry, wayland, ProcessUtil.Which);
        if (cmd == null)
        {
            Notifier.Show("Screenshot", wayland ? "Install grim to take screenshots (Arch: pacman -S grim · Debian/Ubuntu: apt install grim)."
                : "Install scrot, maim or ImageMagick to take screenshots.", "photos");
            return;
        }
        try { Directory.CreateDirectory(Folder); } catch { }
        var (code, output) = await ProcessUtil.RunAsync(cmd.Value.Exe, cmd.Value.Args);
        if (code != 0 || !File.Exists(file))
        {
            Notifier.Show("Screenshot", "The screenshot didn't work: " + (output.Trim().Length > 0 ? output.Trim() : $"{Path.GetFileName(cmd.Value.Exe)} failed"), "photos");
            return;
        }
        SystemSounds.Play("camera");
        Notifier.Show("Screenshot saved", Path.GetFileName(file), "photos",
            new NotificationAction("Open", () => FileOpener.Open(file)),
            new NotificationAction("Show in Files", () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                WindowManager.Open(new Views.FileManagerWindow(Folder), "files"))));
    }
}
