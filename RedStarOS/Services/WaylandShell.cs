using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RedStarOS.Services;

/// <summary>
/// Phase 4 of the Wayland move: Red Star's own windows drawn natively on Wayland (Avalonia.Wayland) instead of through
/// Xwayland. Experimental and opt-in: Settings → Desktop &amp; Taskbar, or REDSTAR_NATIVE_WAYLAND=1 (0 turns it off).
/// Only inside redstar-comp, which gives the shell's windows what plain Wayland can't (the desktop's place at the
/// bottom, the taskbar's reserved space, menus above other windows).
///
/// Falling back: Avalonia doesn't fall back to X11 by itself, so Red Star does. If starting natively throws, it starts
/// again through Xwayland right away; if it crashes before the desktop has been up for a few seconds, the next start
/// uses Xwayland. Either way a notification says so.
/// </summary>
public static class WaylandShell
{
    /// <summary>This run draws natively on Wayland.</summary>
    public static bool Active { get; private set; }

    /// <summary>Why this run went back to Xwayland (shown once), or null.</summary>
    public static string? FellBackReason { get; private set; } = Environment.GetEnvironmentVariable("REDSTAR_WAYLAND_SHELL_FAILED") is { Length: > 0 } r ? r : null;

    private static string Marker => AppPaths.ConfigFile("wayland-shell-starting");

    /// <summary>The decision (public for the tests).</summary>
    public static bool Decide(string? env, bool setting, bool inComp, bool waylandDisplay, bool crashedLastTime)
    {
        if (!inComp || !waylandDisplay || env == "0") return false;
        if (env == "1") return true;
        return setting && !crashedLastTime;
    }

    /// <summary>Called first thing: whether to start natively (and if so, remembers that a start is under way).</summary>
    public static bool ShouldUse()
    {
        bool crashed = false;
        try
        {
            if (File.Exists(Marker))
            {
                crashed = true;
                File.Delete(Marker);
                FellBackReason ??= "Crimson Banner OS stopped while starting natively on Wayland last time, so this time it uses Xwayland.";
            }
        }
        catch { }
        bool use = Decide(Environment.GetEnvironmentVariable("REDSTAR_NATIVE_WAYLAND"), InputIsolation.SettingFlag("NativeWaylandShell"),
            InputIsolation.InRedStarComp, Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }, crashed);
        if (use)
        {
            Active = true;
            try { Directory.CreateDirectory(Path.GetDirectoryName(Marker)!); File.WriteAllText(Marker, DateTime.Now.ToString("o")); } catch { }
        }
        return use;
    }

    /// <summary>
    /// Natively on Wayland, a window Red Star draws without a frame (its Alt+Tab switcher, the sign-in screen, a slide
    /// show) has to say so to redstar-comp, which otherwise frames it. Matched by title, so the title must be its own.
    /// </summary>
    public static void Frameless(Avalonia.Controls.Window w, string title)
    {
        w.Title = title;
        if (Active) CompIpc.Role("bare", title);
    }

    // ─────────── a stable identity for each of Red Star's windows ───────────
    // Titles repeat (two "Files — Documents"), so each window is named once: its title becomes an invisible marker
    // (U+2063) + "rs:" + a token for a moment, redstar-comp binds the token to that window for good (never showing
    // that title) and answers "token <token> <id>", and the real title comes back.

    public const string TokenMark = "\u2063rs:";
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avalonia.Controls.Window, string> TokenOf = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Avalonia.Controls.Window, string> RealTitle = new();

    /// <summary>Names a Red Star window to redstar-comp (natively on Wayland only; once per window).</summary>
    public static void Name(Avalonia.Controls.Window w)
    {
        if (!Active || TokenOf.TryGetValue(w, out _)) return;
        var token = Guid.NewGuid().ToString("N")[..16];
        TokenOf.Add(w, token);
        void Apply()
        {
            var real = w.Title ?? "";
            if (real.StartsWith(TokenMark, StringComparison.Ordinal)) return;
            RealTitle.AddOrUpdate(w, real);
            w.Title = TokenMark + token;
            // the real title comes back once redstar-comp has the token (or after 2 s, whatever happens)
            int ticks = 0;
            var t = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            t.Tick += (_, _) =>
            {
                if (!CompIpc.Tokens.ContainsKey(token) && ++ticks < 80) return;
                t.Stop();
                if (w.Title == TokenMark + token) w.Title = real;      // unless the app set a new title meanwhile
                RealTitle.Remove(w);
            };
            t.Start();
        }
        if (w.IsVisible) Apply(); else w.Opened += (_, _) => Apply();
    }

    /// <summary>redstar-comp's id for a Red Star window it was named to, or null.</summary>
    public static long? CompIdOf(Avalonia.Controls.Window w) =>
        TokenOf.TryGetValue(w, out var t) && CompIpc.Tokens.TryGetValue(t, out var id) ? id : null;

    /// <summary>The title to show for a Red Star window (never the moment's naming title).</summary>
    public static string? VisibleTitle(Avalonia.Controls.Window w) =>
        w.Title is { } t && t.StartsWith(TokenMark, StringComparison.Ordinal) ? (RealTitle.TryGetValue(w, out var r) ? r : "") : w.Title;

    /// <summary>The desktop has been up for a while: native Wayland works on this machine.</summary>
    public static void Started()
    {
        try { if (File.Exists(Marker)) File.Delete(Marker); } catch { }
    }

    /// <summary>Starting natively failed outright: run Red Star again through Xwayland and return its exit code.</summary>
    public static int RestartThroughXwayland(string[] args, Exception why)
    {
        Started();                                       // not a crash to remember: this run is handled
        Console.Error.WriteLine("redstaros: native Wayland failed, using Xwayland: " + why.Message);
        var self = Environment.ProcessPath ?? "RedStarOS";
        var psi = new ProcessStartInfo(self) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet") psi.ArgumentList.Add(typeof(WaylandShell).Assembly.Location);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["REDSTAR_NATIVE_WAYLAND"] = "0";
        psi.Environment["REDSTAR_WAYLAND_SHELL_FAILED"] = "Crimson Banner OS couldn't start natively on Wayland (" + why.Message + "), so it uses Xwayland.";
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch { return 1; }
    }
}
