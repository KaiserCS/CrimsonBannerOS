using System;
using System.Collections.Generic;
using Avalonia.Threading;
using RedStarOS.Views;

namespace RedStarOS.Services;

/// <summary>
/// Red Star Terminal is native (Views/TerminalWindow): one window with tabs. Launching it again opens a new tab
/// in the running window ("Open Terminal" on a folder opens there).
/// </summary>
public static class TerminalLauncher
{
    public static void Launch(string? dir = null) => Dispatcher.UIThread.Post(() => Window().NewTab(dir));

    /// <summary>Runs a program in a new tab (desktop entries with Terminal=true).</summary>
    public static void Run(IList<string> command, string? dir = null) =>
        Dispatcher.UIThread.Post(() => Window().NewTab(dir ?? AppPaths.Home, command));

    private static TerminalWindow Window()
    {
        if (TerminalWindow.Instance is { } w) { w.Raise(); return w; }
        w = new TerminalWindow();
        WindowManager.Open(w, "terminal");
        return w;
    }
}
