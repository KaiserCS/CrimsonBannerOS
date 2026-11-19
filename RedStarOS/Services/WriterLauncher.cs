using System;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>Red Star Writer: the native rich-text window. One window, files open as tabs.</summary>
public static class WriterLauncher
{
    public static void Launch(string? file = null)
    {
        if (RichWriterWindow.Instance is { } open)
        {
            if (!string.IsNullOrEmpty(file)) open.OpenPath(file);
            open.Raise();
            return;
        }
        var w = new RichWriterWindow();
        WindowManager.Open(w, "writer");
        if (!string.IsNullOrEmpty(file)) w.OpenPath(file);
    }

    public static void OpenHtml(string title, string html)
    {
        if (RichWriterWindow.Instance is not { } w)
        {
            w = new RichWriterWindow();
            WindowManager.Open(w, "writer");
        }
        w.OpenHtmlDocument(title, html);
        w.Raise();
    }
}
