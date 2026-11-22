// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RedStarOS.Services;

/// <summary>
/// REDSTAR_TRACE_POINTER=file: every time the pointer enters or leaves one of the shell's buttons, menu items or list
/// rows, a line in that file (milliseconds, enter/exit, which control, where). For chasing hover bugs: a highlight
/// that blinks shows up as an exit and an enter of the same control while the pointer is still on it.
/// </summary>
public static class PointerTrace
{
    private static StreamWriter? _out;
    private const long MaxBytes = 8 << 20;           // stops there: a few minutes of hovering is plenty

    /// <summary>Where Settings → Diagnostics → "Record pointer activity" writes it.</summary>
    public static string LogPath => AppPaths.ConfigFile("pointer-trace.log");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Install()
    {
        var path = Environment.GetEnvironmentVariable("REDSTAR_TRACE_POINTER");
        bool fromSetting = false;
        if (string.IsNullOrEmpty(path) && SettingsStore.Current.TracePointer) { path = LogPath; fromSetting = true; }
        if (string.IsNullOrEmpty(path) || _out != null) return;
        try { _out = new StreamWriter(path, append: !fromSetting) { AutoFlush = true }; }      // a fresh log each start
        catch { return; }
        InputElement.PointerEnteredEvent.AddClassHandler<Control>((c, e) => Log("enter", c, e), handledEventsToo: true);
        InputElement.PointerExitedEvent.AddClassHandler<Control>((c, e) => Log("exit", c, e), handledEventsToo: true);
        // every 25th move a window gets, so a log without enters still shows whether the pointer reached the shell
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>((t, e) =>
        {
            if (++_moves % 25 != 0 || _out == null || _out.BaseStream.Length > MaxBytes) return;
            var p = e.GetPosition(t);
            lock (_out) _out.WriteLine($"{Clock.ElapsedMilliseconds}\tmove\t{t.GetType().Name}:{t.Bounds.Width:0}x{t.Bounds.Height:0}\t{(e.Source as Control)?.GetType().Name}\t{p.X:0.0},{p.Y:0.0}");
        }, handledEventsToo: true);
        lock (_out) _out.WriteLine($"{Clock.ElapsedMilliseconds}\tstart\tRedStarOS {Environment.ProcessId}\t-\t-");
    }

    private static int _moves;

    private static void Log(string what, Control c, PointerEventArgs e)
    {
        if (_out == null || !Traced(c) || _out.BaseStream.Length > MaxBytes) return;
        var top = TopLevel.GetTopLevel(c);
        var p = top != null ? e.GetPosition(top) : default;
        lock (_out) _out.WriteLine($"{Clock.ElapsedMilliseconds}\t{what}\t{Describe(c)}\t{top?.GetType().Name}\t{p.X:0.0},{p.Y:0.0}");
    }

    public static bool Traced(Control c) => c is Button or ToggleButton or ListBoxItem or MenuItem or TabItem or ComboBoxItem;

    public static string Describe(Control c)
    {
        var label = c.Name ?? ToolTip.GetTip(c) as string ?? (c as ContentControl)?.Content as string ?? "";
        label = label.Replace('\t', ' ').Replace('\n', ' ');
        return $"{c.GetType().Name}#{c.GetHashCode():x}:{label}";
    }
}
