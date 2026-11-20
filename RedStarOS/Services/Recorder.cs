// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RedStarOS.Services;

/// <summary>
/// A recording program (gst-launch-1.0, wf-recorder, ffmpeg) run until the user stops it. Stopping sends SIGINT, which
/// these programs take as "finish the file properly" (GStreamer with -e sends end-of-stream; wf-recorder and ffmpeg
/// write their trailer), and only kills it if it hasn't ended a few seconds later. Its messages go to a log file, not
/// a pipe (see RedStarOnline: a pipe whose reader goes away kills the writer).
/// </summary>
public sealed class Recorder
{
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
    private const int SIGINT = 2;

    private Process? _p;
    public string File { get; }
    public string Log { get; }
    public DateTime Started { get; private set; }
    public bool Running => _p is { HasExited: false };
    /// <summary>Raised on a background thread when the program ends (by itself or after Stop), with its exit code.</summary>
    public event Action<int>? Ended;

    public Recorder(string file, string logName)
    {
        File = file;
        Log = AppPaths.ConfigFile(logName);
    }

    public bool Start(string exe, IEnumerable<string> args)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            // sh only redirects: the program's output into the log, nothing to us
            var line = new StringBuilder("exec ").Append(Quote(exe));
            foreach (var a in args) line.Append(' ').Append(Quote(a));
            line.Append(" >>").Append(Quote(Log)).Append(" 2>&1 </dev/null");
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(line.ToString());
            System.IO.File.AppendAllText(Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
            _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _p.Exited += (_, _) => { int code = -1; try { code = _p.ExitCode; } catch { } Ended?.Invoke(code); };
            if (!_p.Start()) return false;
            Started = DateTime.Now;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("recorder: " + ex.Message);
            return false;
        }
    }

    public TimeSpan Elapsed => Running ? DateTime.Now - Started : TimeSpan.Zero;

    /// <summary>Asks it to finish the file; kills it if it's still there after <paramref name="graceMs"/>.</summary>
    public void Stop(int graceMs = 4000)
    {
        var p = _p;
        if (p == null) return;
        try
        {
            if (p.HasExited) return;
            kill(p.Id, SIGINT);
            System.Threading.Tasks.Task.Run(() =>
            {
                try { if (!p.WaitForExit(graceMs)) p.Kill(); } catch { }
            });
        }
        catch { }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>"2:05" or "1:02:05".</summary>
    public static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
