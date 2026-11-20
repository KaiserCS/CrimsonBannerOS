using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>
/// Red Star Video, on libVLC. When libvlc isn't installed the user is told how to get it.
/// </summary>
public static class VideoLauncher
{
    private static bool _warned;

    public static void Launch(params string[] files)
    {
        var list = files.Where(f => !string.IsNullOrEmpty(f)).ToList();
        Dispatcher.UIThread.Post(() => LaunchCore(list));
    }

    private static void LaunchCore(List<string> files)
    {
        if (VideoWindow.Instance is { } open)
        {
            if (files.Count > 0) open.AddFiles(files, true);
            open.Raise();
            return;
        }

        var lib = VideoEngine.Get();
        if (lib == null)
        {
            if (!_warned || files.Count > 0)
            {
                _warned = true;
                Notifier.Show("Video", "Video needs libVLC, which isn't installed. Install it with your package manager (vlc, or libvlc and its plugins), then open the video again.", "video");
            }
            return;
        }

        var win = new VideoWindow(lib);
        WindowManager.Open(win, "video");
        if (files.Count > 0) win.AddFiles(files, true);
    }
}
