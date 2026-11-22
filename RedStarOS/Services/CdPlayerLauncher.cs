// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>CD Player: one window; music opened from elsewhere goes into it.</summary>
public static class CdPlayerLauncher
{
    public static void Launch(params string[] files)
    {
        var list = files.Where(f => !string.IsNullOrEmpty(f)).ToList();
        Dispatcher.UIThread.Post(() => LaunchCore(list));
    }

    private static void LaunchCore(List<string> files)
    {
        if (CdPlayerWindow.Instance is { } open)
        {
            if (files.Count > 0) open.AddFiles(files, true);
            open.Raise();
            return;
        }
        // without libVLC the window still opens, and says what to install
        var win = new CdPlayerWindow(VideoEngine.Get());
        WindowManager.Open(win, "cdplayer");
        if (files.Count > 0) win.AddFiles(files, true);
    }
}
