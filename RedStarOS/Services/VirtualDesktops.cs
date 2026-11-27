// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace RedStarOS.Services;

/// <summary>
/// Virtual desktops, as on Windows 10: several desktops, each with its own windows, one shown at a time. The window
/// manager keeps them (redstar-wm or any EWMH one through <see cref="X11Desktop"/>; redstar-comp through
/// <see cref="CompIpc"/>); this is the shell's view of them, and the one place that asks for changes. Their names are
/// the shell's (settings), and so is how many there are from one session to the next.
/// </summary>
public static class VirtualDesktops
{
    public const int Max = 20;

    public static int Count { get; private set; } = 1;
    public static int Current { get; private set; }

    /// <summary>The desktop each window is on, by its id (X11 window id, or redstar-comp's native id); -1: every desktop.</summary>
    public static IReadOnlyDictionary<long, int> Desks => _desks;
    private static Dictionary<long, int> _desks = new();

    /// <summary>The count, the shown desktop, a window's desktop or a name changed (UI thread).</summary>
    public static event Action? Changed;
    /// <summary>Another desktop is shown (UI thread): the switch's little sign.</summary>
    public static event Action<int>? Switched;

    private static bool _restored;

    // ─────────── what the window manager says ───────────
    /// <summary>The window manager's state (UI thread): any part may be left out. merge: desks adds to what's known.</summary>
    public static void FromWindowManager(int? count, int? current, Dictionary<long, int>? desks, bool merge = false)
    {
        bool changed = false;
        int wasCurrent = Current;
        if (count is int c && c != Count)
        {
            Count = Math.Clamp(c, 1, Max);
            if (Current >= Count) Current = Count - 1;
            changed = true;
        }
        if (current is int cur && cur != Current) { Current = Math.Clamp(cur, 0, Count - 1); changed = true; }
        if (desks != null)
        {
            if (merge)
            {
                foreach (var (id, d) in desks)
                    if (!_desks.TryGetValue(id, out var old) || old != d) { _desks = new Dictionary<long, int>(_desks) { [id] = d }; changed = true; }
            }
            else if (!_desks.Count.Equals(desks.Count) || desks.Any(kv => !_desks.TryGetValue(kv.Key, out var o) || o != kv.Value))
            {
                _desks = new Dictionary<long, int>(desks);
                changed = true;
            }
        }
        if (count != null) Restore();
        if (!changed) return;
        if (count != null && SettingsStore.Current.DesktopCount != Count && _restored)
        {
            SettingsStore.Current.DesktopCount = Count;
            SettingsStore.Save();
        }
        Changed?.Invoke();
        if (Current != wasCurrent) Switched?.Invoke(Current);
    }

    /// <summary>The first time the window manager is heard from: as many desktops as last time, with their names.</summary>
    private static void Restore()
    {
        if (_restored) return;
        _restored = true;
        int want = Math.Clamp(SettingsStore.Current.DesktopCount, 1, Max);
        if (want > 1 && want != Count) SetCount(want);
        PublishNames();
    }

    /// <summary>For the tests: forget everything (and don't bring back a saved count, which other tests may have left).</summary>
    public static void Reset()
    {
        Count = 1; Current = 0; _desks = new(); _restored = true;
    }

    // ─────────── names ───────────
    public static string Name(int i)
    {
        var names = SettingsStore.Current.DesktopNames;
        return i >= 0 && i < names.Count && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"Desktop {i + 1}";
    }

    public static void Rename(int i, string name)
    {
        if (i < 0 || i >= Max) return;
        var names = SettingsStore.Current.DesktopNames;
        while (names.Count <= i) names.Add("");
        name = name.Trim();
        names[i] = name == $"Desktop {i + 1}" ? "" : name;
        SettingsStore.Save();
        PublishNames();
        Changed?.Invoke();
    }

    private static void PublishNames()
    {
        if (!CompIpc.Connected && X11Desktop.Available)
            X11Desktop.SetDesktopNames(Enumerable.Range(0, Count).Select(Name).ToList());
    }

    // ─────────── windows ───────────
    /// <summary>The id the window manager knows a taskbar window by (0 when it has none yet).</summary>
    public static long IdOf(TaskItem t) => t switch
    {
        NativeTask n => WaylandShell.CompIdOf(n.Window) ?? n.Window.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0,
        X11Task x => x.Window,
        _ => 0,
    };

    /// <summary>The desktop a window is on (-1: all). One the window manager hasn't placed yet is on the shown one.</summary>
    public static int DeskOf(long id) => id != 0 && _desks.TryGetValue(id, out var d) ? d : Current;
    public static int DeskOf(TaskItem t) => DeskOf(IdOf(t));

    public static bool IsOn(TaskItem t, int desk) { int d = DeskOf(t); return d < 0 || d == desk; }
    public static bool OnCurrent(TaskItem t) => IsOn(t, Current);

    /// <summary>The taskbar's windows on a desktop, in the taskbar's order.</summary>
    public static List<TaskItem> TasksOn(int desk) => WindowManager.Tasks.Where(t => IsOn(t, desk)).ToList();

    // ─────────── changes ───────────
    public static void Switch(int d)
    {
        if (d < 0 || d >= Count || d == Current) return;
        if (CompIpc.Connected) CompIpc.SwitchDesktop(d); else X11Desktop.SwitchDesktop(d);
        // shown at once (the window manager confirms): the taskbar and the switch's sign don't wait for it
        FromWindowManager(null, d, null);
    }

    public static void Next() => Switch(Current + 1);
    public static void Previous() => Switch(Current - 1);

    public static void Move(long id, int d)
    {
        if (id == 0) return;
        if (CompIpc.Connected) CompIpc.MoveToDesktop(id, d); else X11Desktop.MoveToDesktop(id, d);
        FromWindowManager(null, null, new Dictionary<long, int> { [id] = d }, merge: true);
    }

    public static void Move(TaskItem t, int d) => Move(IdOf(t), d);

    /// <summary>Adds a desktop at the end and returns its number (or -1 at the most there can be).</summary>
    public static int Add()
    {
        if (Count >= Max) return -1;
        SetCount(Count + 1);
        return Count - 1;
    }

    private static void SetCount(int n)
    {
        n = Math.Clamp(n, 1, Max);
        if (CompIpc.Connected) CompIpc.SetDesktopCount(n); else X11Desktop.SetDesktopCount(n);
        FromWindowManager(n, Math.Min(Current, n - 1), null);
        PublishNames();
    }

    /// <summary>
    /// Where each window goes when desktop d closes (public for the tests): its windows join the desktop to its left
    /// (the first desktop's go to the next one), and every desktop after it moves down one. Only windows that move.
    /// </summary>
    public static Dictionary<long, int> ClosePlan(IReadOnlyDictionary<long, int> desks, int d)
    {
        var plan = new Dictionary<long, int>();
        foreach (var (id, desk) in desks)
        {
            if (desk < 0 || desk < d) continue;
            int to = desk == d ? Math.Max(0, d - 1) : desk - 1;
            if (to != desk) plan[id] = to;
        }
        return plan;
    }

    /// <summary>The desktop shown after desktop d closes, when `current` was shown (public for the tests).</summary>
    public static int CurrentAfterClose(int current, int d) => current < d ? current : Math.Max(0, current - 1);

    /// <summary>Closes desktop d, as Windows does: its windows aren't closed, they move to the desktop on its left.</summary>
    public static void Close(int d)
    {
        if (Count <= 1 || d < 0 || d >= Count) return;
        foreach (var (id, to) in ClosePlan(_desks, d)) Move(id, to);
        int next = CurrentAfterClose(Current, d);
        var names = SettingsStore.Current.DesktopNames;
        if (d < names.Count) { names.RemoveAt(d); SettingsStore.Save(); }
        if (next != Current)
        {
            if (CompIpc.Connected) CompIpc.SwitchDesktop(next); else X11Desktop.SwitchDesktop(next);
            FromWindowManager(null, next, null);
        }
        SetCount(Count - 1);
    }
}
