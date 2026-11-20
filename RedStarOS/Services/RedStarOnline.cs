// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;

namespace RedStarOS.Services;

/// <summary>
/// Red Star Online, the GTK 4 + WebKitGTK browser (browser/): its own native window, sharing bookmarks, history and
/// settings with the shell (browser-data.json). Used when it's installed, unless Settings → Default apps says to keep
/// the previous browser (Electron), which stays until the new one has everything.
/// </summary>
public static class RedStarOnline
{
    /// <summary>Its Wayland app id and X11 class.</summary>
    public const string WindowId = "org.redstaros.Online";

    /// <summary>The program: $REDSTAR_ONLINE, next to Red Star, then on PATH (/usr/local/bin after install-session.sh).</summary>
    public static string? ProgramPath()
    {
        if (Environment.GetEnvironmentVariable("REDSTAR_ONLINE") is { Length: > 0 } env && File.Exists(env)) return env;
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "" })
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "redstar-online"))) return Path.Combine(dir, "redstar-online");
        return ProcessUtil.Which("redstar-online");
    }

    public static bool Available => !SettingsStore.Current.ClassicBrowser && ProgramPath() != null;

    /// <summary>A window of Red Star Online (so the taskbar shows it as the Red Star Online app).</summary>
    public static bool IsItsWindow(string instance, string cls) =>
        string.Equals(instance, WindowId, StringComparison.OrdinalIgnoreCase) || string.Equals(cls, WindowId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The arguments for opening a page, or a private window.</summary>
    public static string[] Arguments(string? url, bool privateWindow)
    {
        if (privateWindow) return string.IsNullOrEmpty(url) ? new[] { "--private" } : new[] { "--private", url };
        return string.IsNullOrEmpty(url) ? Array.Empty<string>() : new[] { url };
    }

    /// <summary>Where its messages go: ~/.config/redstaros/online.log.</summary>
    public static string LogPath => AppPaths.ConfigFile("online.log");

    /// <summary>Raised (on a background thread) when it ended badly within seconds of starting: the url it was
    /// opening, and why.</summary>
    public static event Action<string?, string>? CrashedAtStart;

    /// <summary>"exit code 1", "signal 8 (SIGFPE)"… from a process's exit code as .NET reports it.</summary>
    public static string Describe(int code)
    {
        if (code > 128 && code < 160)
        {
            var sig = code - 128;
            var name = sig switch { 4 => "SIGILL", 6 => "SIGABRT", 8 => "SIGFPE", 9 => "SIGKILL", 11 => "SIGSEGV", 15 => "SIGTERM", _ => "" };
            return name.Length > 0 ? $"signal {sig} ({name})" : $"signal {sig}";
        }
        return "exit code " + code;
    }

    /// <summary>Opens Red Star Online (a running one opens the page as a new tab). False when it isn't there.</summary>
    public static bool Launch(string? url, bool privateWindow = false)
    {
        if (!Available || ProgramPath() is not { } exe) return false;
        try
        {
            // Its messages go straight to the log file: the browser opens it itself (REDSTAR_ONLINE_LOG) and WebKit's page
            // processes inherit it. Not through a pipe to us: a pipe whose reading end goes away kills whatever writes to it
            // next (SIGPIPE, no core dump) — WebKit's page process, as soon as a video made GStreamer say something.
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var a in Arguments(url, privateWindow)) psi.ArgumentList.Add(a);
            var log = LogPath;
            psi.Environment["REDSTAR_ONLINE_LOG"] = log;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var started = DateTime.UtcNow;
            Write(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} starting {exe} {string.Join(' ', psi.ArgumentList)}");
            p.Exited += (_, _) =>
            {
                lock (Running) Running.Remove(p);
                int code;
                try { code = p.ExitCode; } catch { return; }
                Write(log, $"{DateTime.Now:HH:mm:ss} it ended: {Describe(code)}");
                // a second launch hands its page to the running browser and ends at once with 0: that's fine
                if (code != 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(15)) CrashedAtStart?.Invoke(url, Describe(code));
                p.Dispose();
            };
            if (!p.Start()) return false;
            lock (Running) Running.Add(p);             // kept until it ends, so its Exited event comes
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("online: couldn't start " + exe + ": " + ex.Message);
            return false;
        }
    }

    private static readonly object LogLock = new();
    private static readonly System.Collections.Generic.HashSet<Process> Running = new();

    private static void Write(string log, string line)
    {
        try { lock (LogLock) File.AppendAllText(log, line + "\n"); } catch { }
    }
}
