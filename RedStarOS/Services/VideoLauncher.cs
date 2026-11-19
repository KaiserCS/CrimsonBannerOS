using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>
/// Red Star Video. The native LibVLC player is primary; if libvlc isn't installed we fall back to the
/// Electron player ("video" folder) and tell the user how to get the native one.
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
            if (!_warned)
            {
                _warned = true;
                Notifier.Show("Red Star Video", "libvlc is not installed, so the classic player is used. Install it (e.g. 'sudo apt install vlc libvlc-dev') for the native player.", "video");
            }
            ElectronLauncher.Launch("video", "redstar-video", "video", "Red Star Video", files);
            return;
        }

        var win = new VideoWindow(lib);
        WindowManager.Open(win, "video");
        if (files.Count > 0) win.AddFiles(files, true);
    }
}
