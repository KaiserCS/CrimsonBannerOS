// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace RedStarOS.Services;

/// <summary>
/// Show desktop, as on Windows: the first press minimises every window, the next brings back the ones it minimised
/// (if none has been opened or restored since). Peek makes every window nearly see-through while the pointer rests on
/// the button, and solid again when it leaves.
/// </summary>
public static class ShowDesktop
{
    private static List<long> _hidden = new();

    /// <summary>Whether the last press hid windows that the next one would bring back.</summary>
    public static bool Showing => _hidden.Count > 0;

    /// <summary>What a press does, given the windows now (pure, for the tests): hide these, or bring back those.</summary>
    public static (List<long> Hide, List<long> Restore) Plan(IReadOnlyList<X11Desktop.XWin> windows, IReadOnlyCollection<long> hidden, long desktop)
    {
        var visible = windows.Where(w => !w.Minimized && w.Id != desktop).Select(w => w.Id).ToList();
        if (visible.Count == 0 && hidden.Count > 0)
            return (new(), hidden.Where(id => windows.Any(w => w.Id == id)).ToList());
        return (visible, new());
    }

    public static void Toggle()
    {
        long self = WindowManager.Desktop?.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0;
        X11Desktop.ListWindows(list =>
        {
            var (hide, restore) = Plan(list, _hidden, self);
            if (restore.Count > 0)
            {
                // back in the order they were stacked, the top one last (and so on top)
                foreach (var id in Enumerable.Reverse(restore)) X11Desktop.Activate(id);
                _hidden = new();
                return;
            }
            _hidden = hide;
            foreach (var id in hide) X11Desktop.SnapWindow(id, "minimize");
        });
    }

    private static bool _peeking;

    public static void Peek(bool on)
    {
        if (on == _peeking) return;
        if (on && !SettingsStore.Current.PeekDesktop) return;
        _peeking = on;
        if (CompIpc.Connected) { CompIpc.Send(on ? "peek\ton" : "peek\toff"); return; }
        // X11: _NET_WM_WINDOW_OPACITY, which redstar-wm passes to the frame and the compositor (picom) honours
        long self = WindowManager.Desktop?.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0;
        X11Desktop.ListWindows(list =>
        {
            foreach (var w in list.Where(w => !w.Minimized && w.Id != self)) X11Desktop.SetOpacity(w.Id, on ? 0.08 : null);
        });
    }
}
