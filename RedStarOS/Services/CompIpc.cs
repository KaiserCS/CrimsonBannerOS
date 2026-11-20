using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services;

/// <summary>
/// The connection to redstar-comp (compositor/redstar-comp.c, "the Crimson Banner shell's connection"): native Wayland
/// windows for the taskbar and Alt+Tab, the active window and stacking order, and global shortcuts that work
/// whichever window has the keyboard. One line per message, fields separated by tabs. Only used inside redstar-comp
/// (REDSTAR_COMP_SOCKET); <see cref="X11Desktop"/> merges what arrives here with the X11 windows it reads itself.
/// </summary>
public static class CompIpc
{
    /// <summary>Native window ids start here, so they never collide with X11 window ids.</summary>
    public const long NativeBase = 1L << 32;

    public sealed record NativeWindow(long Id, string AppId, string Title, int Pid, bool Minimized, bool Maximized, bool Fullscreen);

    public static bool IsNative(long id) => id >= NativeBase;

    /// <summary>Connected to redstar-comp.</summary>
    public static bool Connected { get; private set; }

    /// <summary>Native windows, by id (snapshot-safe: replaced, never changed in place).</summary>
    public static IReadOnlyDictionary<long, NativeWindow> Windows => _windows;

    /// <summary>The active window as redstar-comp sees it: a native id, an X11 window id, or 0.</summary>
    public static long Active { get; private set; }

    /// <summary>Taskbar windows (native and X11), most recently used first.</summary>
    public static IReadOnlyList<long> Stack { get; private set; } = Array.Empty<long>();

    /// <summary>Raised on the reader thread when windows, the active window or the stacking order change.</summary>
    public static event Action? Changed;

    /// <summary>Raised on the reader thread for a global shortcut (same names as <see cref="X11Desktop.Shortcut"/>).</summary>
    public static event Action<string>? Key;

    private static volatile Dictionary<long, NativeWindow> _windows = new();
    private static volatile Dictionary<string, long> _tokens = new();

    /// <summary>The shell's own windows by the token it named them with (see <see cref="WaylandShell.Name"/>).</summary>
    public static IReadOnlyDictionary<string, long> Tokens => _tokens;
    private static Socket? _sock;
    private static string? _path;
    private static readonly object WriteLock = new();
    private static Thread? _thread;

    public static string? SocketPath => Environment.GetEnvironmentVariable("REDSTAR_COMP_SOCKET") is { Length: > 0 } p ? p : null;

    /// <summary>Connects (once) when running inside redstar-comp. Returns whether it's connected.</summary>
    public static bool Start()
    {
        if (SocketPath is not { } path || !File.Exists(path)) return false;
        if (Connected && path == _path) return true;
        try { _sock?.Dispose(); } catch { }          // another compositor (only happens in the tests)
        try
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(path));
            _sock = s;
            _path = path;
            Connected = true;
        }
        catch (Exception ex) { Console.Error.WriteLine("comp-ipc: " + ex.Message); return false; }
        _thread = new Thread(Read) { IsBackground = true, Name = "comp-ipc" };
        _thread.Start();
        Send("hello");
        return true;
    }

    /// <summary>Sends one command line (see the protocol in redstar-comp.c).</summary>
    public static void Send(string line)
    {
        var s = _sock;
        if (s == null) return;
        try { lock (WriteLock) s.Send(Encoding.UTF8.GetBytes(line + "\n")); }
        catch { Connected = false; }
    }

    // ─────────── questions with an answer (one at a time) ───────────
    private sealed class Pending
    {
        public required Func<string, bool> Accept, IsEnd;
        public List<string> Lines = new();
        public TaskCompletionSource<List<string>> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static volatile Pending? _pending;
    private static readonly SemaphoreSlim QueryLock = new(1, 1);

    /// <summary>Sends a command and collects the answer lines (null without a connection or an answer in 5 s).</summary>
    public static async Task<List<string>?> Query(string command, Func<string, bool> accept, Func<string, bool> isEnd)
    {
        if (!Connected) return null;
        await QueryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var p = new Pending { Accept = accept, IsEnd = isEnd };
            _pending = p;
            Send(command);
            var t = await Task.WhenAny(p.Done.Task, Task.Delay(5000)).ConfigureAwait(false);
            return t == p.Done.Task ? p.Done.Task.Result : null;
        }
        finally { _pending = null; QueryLock.Release(); }
    }

    // ─────────── displays ───────────
    public sealed record OutputMode(int Width, int Height, int RefreshMhz, bool Preferred)
    {
        public string Size => $"{Width}x{Height}";
        public override string ToString() => $"{Width} × {Height}" + (RefreshMhz > 0 ? $" @ {RefreshMhz / 1000.0:0.##} Hz" : "") + (Preferred ? " (recommended)" : "");
    }

    /// <summary>A screen as redstar-comp reports it (Transform: 0 normal, 1 = 90°, 2 = 180°, 3 = 270°, 4–7 flipped).</summary>
    public sealed record OutputInfo(string Name, string Description, bool Enabled, int Width, int Height, int RefreshMhz,
        double Scale, int X, int Y, int Transform, bool Nested, IReadOnlyList<OutputMode> Modes)
    {
        /// <summary>The size it takes in the layout (rotated and scaled).</summary>
        public (int W, int H) Logical
        {
            get
            {
                int w = Width, h = Height;
                if (Transform % 2 == 1) (w, h) = (h, w);
                return ((int)Math.Round(w / Math.Max(0.5, Scale)), (int)Math.Round(h / Math.Max(0.5, Scale)));
            }
        }
    }

    /// <summary>Reads one "output" line (public for the tests).</summary>
    public static OutputInfo? ParseOutput(string line)
    {
        var f = line.Split('\t');
        // output, name, description, on, WxH@mHz, scale, x, y, transform, nested, modes (WxH@mHz[*],…)
        if (f.Length < 11 || f[0] != "output") return null;
        var cur = ParseMode(f[4]);
        var modes = f[10].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ParseMode).Where(m => m != null).Select(m => m!).ToList();
        int I(string x) => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new OutputInfo(f[1], f[2].Trim(), f[3] == "1", cur?.Width ?? 0, cur?.Height ?? 0, cur?.RefreshMhz ?? 0,
            double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 1, I(f[6]), I(f[7]), I(f[8]), f[9] == "1", modes);
    }

    private static OutputMode? ParseMode(string s)
    {
        bool pref = s.EndsWith('*');
        var m = System.Text.RegularExpressions.Regex.Match(s.TrimEnd('*'), @"^(\d+)x(\d+)(?:@(\d+))?$");
        if (!m.Success) return null;
        return new OutputMode(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0, pref);
    }

    /// <summary>Every screen (null when redstar-comp doesn't answer).</summary>
    public static async Task<List<OutputInfo>?> Outputs()
    {
        var lines = await Query("outputs", l => l.StartsWith("output\t") || l == "outputs-end", l => l == "outputs-end").ConfigureAwait(false);
        return lines?.Select(ParseOutput).Where(o => o != null).Select(o => o!).ToList();
    }

    /// <summary>Applies one screen's settings; returns null when it worked, else why not.</summary>
    public static async Task<string?> SetOutput(string name, bool enabled, int w, int h, int mhz, int x, int y, double scale, int transform)
    {
        var cmd = string.Join('\t', "output-set", name, enabled ? "1" : "0", $"{w}x{h}@{mhz}", x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture), scale.ToString("0.###", CultureInfo.InvariantCulture), transform.ToString(CultureInfo.InvariantCulture));
        var lines = await Query(cmd, l => l.StartsWith("output-ok\t") || l.StartsWith("output-failed\t"),
            l => l.StartsWith("output-ok\t") || l.StartsWith("output-failed\t")).ConfigureAwait(false);
        if (lines == null || lines.Count == 0) return "redstar-comp didn't answer";
        var last = lines[^1].Split('\t');
        return last[0] == "output-ok" ? null : (last.Length > 2 ? last[2] : "the screen refused it");
    }

    /// <summary>Keeps the current layout for the next start.</summary>
    public static void SaveOutputs() => Send("outputs-save");

    /// <summary>The line that gives one of the shell's native Wayland windows a role (public for the tests).</summary>
    public static string RoleLine(string role, int pid, string title, int left, int right, int top, int bottom) =>
        string.Join('\t', "role", role, pid.ToString(CultureInfo.InvariantCulture), title.Replace('\t', ' ').Replace('\n', ' '),
            left.ToString(CultureInfo.InvariantCulture), right.ToString(CultureInfo.InvariantCulture),
            top.ToString(CultureInfo.InvariantCulture), bottom.ToString(CultureInfo.InvariantCulture));

    /// <summary>Running natively on Wayland: "this window (by title) is the desktop / a dock", with the space it reserves.</summary>
    public static void Role(string role, string title, int left = 0, int right = 0, int top = 0, int bottom = 0) =>
        Send(RoleLine(role, Environment.ProcessId, title, left, right, top, bottom));

    public static void Activate(long id) => Send($"activate\t{id}");
    public static void Close(long id) => Send($"close\t{id}");

    /// <summary>"left", "right", "maximize", "restore", "minimize" — as <see cref="X11Desktop.SnapWindow"/>.</summary>
    public static void Snap(long id, string how) => Send($"{how}\t{id}");

    private static void Read()
    {
        var buf = new byte[8192];
        var pending = new StringBuilder();
        var mine = _sock;
        try
        {
            var s = _sock;
            while (s != null && ReferenceEquals(s, _sock))
            {
                int n = s.Receive(buf);
                if (n <= 0) break;
                pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                int nl;
                while ((nl = IndexOf(pending, '\n')) >= 0)
                {
                    var line = pending.ToString(0, nl);
                    pending.Remove(0, nl + 1);
                    try { Handle(line); } catch (Exception ex) { Console.Error.WriteLine("comp-ipc: " + ex.Message); }
                }
            }
        }
        catch { }
        if (!ReferenceEquals(mine, _sock)) return;     // replaced by a newer connection
        Connected = false;
        _windows = new();
        Changed?.Invoke();
    }

    private static int IndexOf(StringBuilder sb, char c)
    {
        for (int i = 0; i < sb.Length; i++) if (sb[i] == c) return i;
        return -1;
    }

    /// <summary>One message from redstar-comp (public for the tests).</summary>
    public static void Handle(string line)
    {
        if (_pending is { } p && p.Accept(line))
        {
            p.Lines.Add(line);
            if (p.IsEnd(line)) p.Done.TrySetResult(p.Lines);
            return;
        }
        var f = line.Split('\t');
        switch (f[0])
        {
            case "window" when f.Length >= 6 && long.TryParse(f[1], out var id):
            {
                var flags = f[2];
                var w = new NativeWindow(id, f[4], string.Join('\t', f.Skip(5)), int.TryParse(f[3], out var pid) ? pid : 0,
                    flags.Contains('m'), flags.Contains('M'), flags.Contains('f'));
                var next = new Dictionary<long, NativeWindow>(_windows) { [id] = w };
                _windows = next;
                Changed?.Invoke();
                break;
            }
            case "gone" when f.Length >= 2 && long.TryParse(f[1], out var gid):
            {
                var next = new Dictionary<long, NativeWindow>(_windows);
                if (next.Remove(gid)) { _windows = next; Changed?.Invoke(); }
                break;
            }
            case "active" when f.Length >= 2 && long.TryParse(f[1], out var aid):
                Active = aid;
                Changed?.Invoke();
                break;
            case "stack":
                Stack = f.Skip(1).Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v != 0).ToArray();
                Changed?.Invoke();
                break;
            case "token" when f.Length >= 3 && long.TryParse(f[2], out var tid):
                _tokens = new Dictionary<string, long>(_tokens) { [f[1]] = tid };
                Changed?.Invoke();
                break;
            case "key" when f.Length >= 2:
                Key?.Invoke(f.Length > 2 ? f[1] + "\t" + f[2] : f[1]);     // e.g. "screenshot-window\tx,y wxh"
                break;
        }
    }

    /// <summary>For the tests: forget everything.</summary>
    public static void Reset()
    {
        _windows = new();
        _tokens = new();
        Active = 0;
        Stack = Array.Empty<long>();
    }
}
