using System;
using System.IO;
using System.Linq;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>Opens a file in the right Red Star app, or with the system default.</summary>
public static class FileOpener
{
    public static void Open(string path)
    {
        // a different program chosen in Settings → Default apps (or "Always use this app" in Open with)
        if (DefaultApps.KindFor(path) is { } kind && DefaultApps.TryOpenWithLinuxApp(kind.Id, path)) return;
        if (!OpenInRedStar(path, pdfInBrowser: DefaultApps.Get("pdf") == "redstar")) SystemUtil.OpenExternal(path);
    }

    /// <summary>The Red Star app that opens this kind of file, or null (for "Open with").</summary>
    public static string? RedStarAppFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") return "Crimson Online";
        if (FileOps.ImageExtensions.Contains(ext)) return "Image Viewer";
        if (ext == ".docx" || (FileOps.TextExtensions.Contains(ext) && ext != ".csv")) return "Writer";
        if (FileOps.VideoExtensions.Contains(ext) || FileOps.AudioExtensions.Contains(ext)) return "Video";
        if (ext is ".csv" or ".tsv" or ".xlsx") return "Sheets";
        if (ext == ".rsslides") return "Slides";
        return null;
    }

    /// <summary>Opens the file in its Red Star app; false when no Red Star app handles it.</summary>
    public static bool OpenInRedStar(string path, bool pdfInBrowser = true)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf" && pdfInBrowser)
        {
            BrowserLauncher.Launch(new Uri(Path.GetFullPath(path)).AbsoluteUri);    // Chromium's PDF viewer
            return true;
        }
        if (FileOps.ImageExtensions.Contains(ext))
        {
            var viewer = new ImageViewerWindow();
            WindowManager.Open(viewer);
            viewer.LoadFile(path);
        }
        else if (ext == ".docx" || (FileOps.TextExtensions.Contains(ext) && ext != ".csv"))
        {
            WriterLauncher.Launch(path);
        }
        else if (FileOps.VideoExtensions.Contains(ext) || FileOps.AudioExtensions.Contains(ext))
        {
            VideoLauncher.Launch(path);
        }
        else if (ext == ".csv" || ext == ".tsv" || ext == ".xlsx")
        {
            var sheet = new SpreadsheetWindow();
            WindowManager.Open(sheet);
            sheet.OpenPath(path);
        }
        else if (ext == ".rsslides")
        {
            var slides = new SlidesWindow();
            WindowManager.Open(slides);
            _ = slides.OpenPath(path);
        }
        else return false;
        return true;
    }
}
