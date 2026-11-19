using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services;

public static class ProcessUtil
{
    public static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(d, name);
                if (File.Exists(p)) return p;
            }
            catch { /* ignore bad PATH entries */ }
        }
        return null;
    }

    /// <summary>Run a program and capture stdout+stderr. Returns (-1, message) if it could not run.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> args, int timeoutMs = 10000)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (-1, "Could not start " + file);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (-1, "Timed out.");
            }
            var text = (await stdout + await stderr).Trim();
            return (p.ExitCode, text);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Start a detached process, hiding AppImage-specific variables from it.</summary>
    public static bool StartDetached(string file, IEnumerable<string> args, string? workDir = null,
        IDictionary<string, string>? env = null)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;
            psi.Environment.Remove("APPIMAGE");
            psi.Environment.Remove("APPDIR");
            psi.Environment.Remove("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
            if (env != null)
                foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Notifier.Show("Could not start " + Path.GetFileName(file), ex.Message);
            return false;
        }
    }

    private static readonly string[] Terminals =
    {
        "x-terminal-emulator", "gnome-terminal", "konsole", "xfce4-terminal",
        "kitty", "alacritty", "tilix", "foot", "xterm"
    };

    public static void OpenTerminal(string? dir = null)
    {
        foreach (var t in Terminals)
        {
            var path = Which(t);
            if (path != null)
            {
                StartDetached(path, Array.Empty<string>(), dir ?? AppPaths.Home);
                return;
            }
        }
        Notifier.Show("Terminal", "No terminal emulator was found.");
    }

    /// <summary>Run a command inside a terminal window (for Terminal=true launchers).</summary>
    public static void RunInTerminal(string file, IList<string> args)
    {
        // Red Star Terminal runs it in a new tab; the system terminals below are the fallback.
        try
        {
            var cmd = new List<string> { file };
            cmd.AddRange(args);
            TerminalLauncher.Run(cmd);
            return;
        }
        catch { }
        foreach (var t in Terminals)
        {
            var path = Which(t);
            if (path == null) continue;
            var all = new List<string>();
            all.Add(t == "gnome-terminal" ? "--" : "-e");
            all.Add(file);
            all.AddRange(args);
            StartDetached(path, all, AppPaths.Home);
            return;
        }
        Notifier.Show("Terminal", "No terminal emulator was found.");
    }
}
