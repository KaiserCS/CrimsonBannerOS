using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RedStarOS.Services;

/// <summary>
/// A shell running on a Linux pseudo-terminal. The pty is opened here through libc (/dev/ptmx); the shell is
/// started through <c>setsid -c</c> (or a tiny python3 equivalent) so it becomes a session leader with the pty as
/// its controlling terminal — job control, Ctrl+C, resizing (SIGWINCH) all work, no native module needed.
/// </summary>
public sealed class PtySession : IDisposable
{
    // ── libc ──
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ptsname_r(int fd, byte[] buf, nuint len);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, ref WinSize ws);
    [DllImport("libc", SetLastError = true)] private static extern int poll([In, Out] PollFd[] fds, nuint nfds, int timeout);
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);

    [StructLayout(LayoutKind.Sequential)] private struct WinSize { public ushort Row, Col, XPixel, YPixel; }
    [StructLayout(LayoutKind.Sequential)] private struct PollFd { public int Fd; public short Events; public short REvents; }

    private const int O_RDWR = 2, O_NOCTTY = 0x100, O_CLOEXEC = 0x80000;
    private const nuint TIOCSWINSZ = 0x5414;
    private const short POLLIN = 1;
    private const int SIGHUP = 1, SIGKILL = 9;
    private const int EINTR = 4, EAGAIN = 11;

    private int _fd = -1;
    private Process? _proc;
    private Thread? _reader;
    private volatile bool _closing;
    private readonly BlockingCollection<byte[]> _writes = new();
    private Thread? _writer;

    /// <summary>Raised on the reader thread with a chunk of terminal output.</summary>
    public event Action<byte[], int>? Output;
    /// <summary>Raised once when the shell has ended (exit code, or -1 when unknown).</summary>
    public event Action<int>? Exited;

    public int Pid => _proc?.Id ?? 0;
    public string Shell { get; private set; } = "";
    public bool JobControl { get; private set; } = true;

    public static string DefaultShell()
    {
        var sh = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(sh) && File.Exists(sh)) return sh;
        return File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            try { var p = Path.Combine(dir, name); if (File.Exists(p)) return p; } catch { }
        }
        foreach (var p in new[] { "/usr/bin/" + name, "/bin/" + name }) if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Starts <paramref name="command"/> (default: the user's shell) on a new pty.</summary>
    public void Start(int cols, int rows, string? cwd, IList<string>? command = null)
    {
        _fd = open("/dev/ptmx", O_RDWR | O_NOCTTY | O_CLOEXEC);
        if (_fd < 0) throw new IOException("Couldn't open a pseudo-terminal (/dev/ptmx), error " + Marshal.GetLastWin32Error());
        if (grantpt(_fd) != 0 || unlockpt(_fd) != 0) throw new IOException("Couldn't unlock the pseudo-terminal, error " + Marshal.GetLastWin32Error());
        var nameBuf = new byte[128];
        if (ptsname_r(_fd, nameBuf, (nuint)nameBuf.Length) != 0) throw new IOException("Couldn't name the pseudo-terminal");
        int nameLen = Array.IndexOf(nameBuf, (byte)0);
        if (nameLen < 0) nameLen = nameBuf.Length;
        var slave = System.Text.Encoding.ASCII.GetString(nameBuf, 0, nameLen);
        Resize(cols, rows);

        var dir = !string.IsNullOrEmpty(cwd) && Directory.Exists(cwd) ? cwd : AppPaths.Home;
        var prog = command is { Count: > 0 } ? new List<string>(command) : new List<string> { DefaultShell() };
        Shell = prog[0];

        // sh opens the pty as fd 0-2, then hands over to setsid, which makes it the controlling terminal.
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = dir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec 0<>\"$1\" 1>&0 2>&0; shift; exec \"$@\"");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(slave);
        string? setsid = Which("setsid"), python = Which("python3");
        if (setsid != null)
        {
            psi.ArgumentList.Add(setsid); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("-w");
        }
        else if (python != null)
        {
            psi.ArgumentList.Add(python); psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import os,sys,fcntl,termios\nos.setsid()\nfcntl.ioctl(0, termios.TIOCSCTTY, 0)\nos.execvp(sys.argv[1], sys.argv[1:])");
        }
        else JobControl = false;
        foreach (var a in prog) psi.ArgumentList.Add(a);

        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["COLORTERM"] = "truecolor";
        psi.Environment["TERM_PROGRAM"] = "RedStarTerminal";
        psi.Environment.Remove("REDSTAR_SHELL_SOCKET");
        psi.Environment.Remove("ELECTRON_RUN_AS_NODE");
        psi.Environment.Remove("COLUMNS");
        psi.Environment.Remove("LINES");

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        int exitedOnce = 0;
        _proc.Exited += (_, _) =>
        {
            int code = -1;
            try { code = _proc.ExitCode; } catch { }
            // let the last output drain before reporting
            new Thread(() => { Thread.Sleep(150); if (Interlocked.Exchange(ref exitedOnce, 1) == 0) Exited?.Invoke(code); }) { IsBackground = true }.Start();
        };
        _proc.Start();

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-reader" };
        _reader.Start();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "pty-writer" };
        _writer.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[65536];
        var pfd = new PollFd[1];
        while (!_closing)
        {
            pfd[0].Fd = _fd; pfd[0].Events = POLLIN; pfd[0].REvents = 0;
            int r = poll(pfd, 1, 250);
            if (r == 0) continue;
            if (r < 0) { if (Marshal.GetLastWin32Error() == EINTR) continue; break; }
            nint n = read(_fd, buf, buf.Length);
            if (n > 0)
            {
                var chunk = new byte[(int)n];
                Buffer.BlockCopy(buf, 0, chunk, 0, (int)n);
                Output?.Invoke(chunk, (int)n);
                continue;
            }
            if (n < 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == EINTR || err == EAGAIN) continue;
            }
            break;   // EIO: every process using the terminal has gone
        }
    }

    /// <summary>Queues input for the program. Writing happens on a background thread, so a program that stops
    /// reading (or a huge paste) never freezes the window.</summary>
    public void Write(byte[] data)
    {
        if (_fd < 0 || data.Length == 0 || _writes.IsAddingCompleted) return;
        try { _writes.Add(data); } catch (InvalidOperationException) { }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var data in _writes.GetConsumingEnumerable())
            {
                var rest = data;
                while (rest.Length > 0 && !_closing)
                {
                    int fd = _fd;
                    if (fd < 0) return;
                    nint n = write(fd, rest, rest.Length);
                    if (n < 0)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err == EINTR) continue;
                        if (err == EAGAIN) { Thread.Sleep(5); continue; }
                        return;
                    }
                    if (n >= rest.Length) break;
                    var next = new byte[rest.Length - (int)n];
                    Buffer.BlockCopy(rest, (int)n, next, 0, next.Length);
                    rest = next;
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Write(string s) => Write(System.Text.Encoding.UTF8.GetBytes(s));

    public void Resize(int cols, int rows)
    {
        if (_fd < 0) return;
        var ws = new WinSize { Col = (ushort)Math.Clamp(cols, 2, 1000), Row = (ushort)Math.Clamp(rows, 2, 1000) };
        ioctl(_fd, TIOCSWINSZ, ref ws);
    }

    /// <summary>The shell's current folder (Linux /proc), or null.</summary>
    public string? CurrentDirectory()
    {
        try
        {
            if (Pid <= 0) return null;
            var target = new DirectoryInfo($"/proc/{Pid}/cwd").ResolveLinkTarget(false);
            return target?.FullName;
        }
        catch { return null; }
    }

    /// <summary>True when a program other than the shell itself is running in the foreground (e.g. vim, a build).</summary>
    public bool HasRunningJob()
    {
        try
        {
            if (Pid <= 0 || _proc == null || _proc.HasExited) return false;
            var stat = File.ReadAllText($"/proc/{Pid}/stat");
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            // fields after "comm": state ppid pgrp session tty_nr tpgid ...
            int pgrp = int.Parse(rest[2]), tpgid = int.Parse(rest[5]);
            return tpgid > 0 && tpgid != pgrp;
        }
        catch { return false; }
    }

    /// <summary>Name of the foreground program (for tab titles / the close prompt).</summary>
    public string? ForegroundName()
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{Pid}/stat");
            var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            int tpgid = int.Parse(rest[5]);
            if (tpgid <= 0) return null;
            var comm = File.ReadAllText($"/proc/{tpgid}/comm").Trim();
            return comm.Length > 0 ? comm : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_closing) return;
        _closing = true;
        _writes.CompleteAdding();
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                // hang up the whole session (shell and its jobs), as closing a terminal window does
                if (JobControl) kill(-_proc.Id, SIGHUP);
                kill(_proc.Id, SIGHUP);
                var p = _proc;
                new Thread(() => { if (!p.WaitForExit(1500)) { try { kill(-p.Id, SIGKILL); p.Kill(true); } catch { } } }) { IsBackground = true }.Start();
            }
        }
        catch { }
        var fd = _fd;
        _fd = -1;
        var reader = _reader;
        new Thread(() => { reader?.Join(1000); if (fd >= 0) close(fd); }) { IsBackground = true }.Start();
    }
}
