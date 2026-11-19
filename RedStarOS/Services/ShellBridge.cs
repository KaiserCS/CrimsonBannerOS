using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>One connected Electron app.</summary>
public sealed class BridgeConnection
{
    private readonly object _writeLock = new();

    internal NetworkStream Stream { get; }
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public ExternalTask? Task { get; set; }
    public HashSet<string> TrayKeys { get; } = new();

    internal BridgeConnection(Socket socket)
    {
        socket.SendTimeout = 1000;
        Stream = new NetworkStream(socket, ownsSocket: true);
    }

    public bool Send(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        lock (_writeLock)
        {
            try { Stream.Write(bytes, 0, bytes.Length); return true; }
            catch { return false; }                                       // the app went away; the read loop cleans up
        }
    }

    public void SendCommand(string cmd) => Send(new JsonObject { ["t"] = "cmd", ["cmd"] = cmd });

    public void SendBounds(int x, int y, int w, int h) =>
        Send(new JsonObject { ["t"] = "bounds", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h });
}

/// <summary>
/// Local channel between the shell and the Electron apps (see shared/shell-bridge.js for the protocol).
/// The shell listens on a Unix socket ($XDG_RUNTIME_DIR/redstaros-shell-PID.sock, mode 0600, so only this
/// user can connect). Apps report their window and tray icons; the shell shows them in the taskbar
/// and app tray, and sends focus / minimise / snap / close commands back.
/// </summary>
public static class ShellBridge
{
    private const int StartupTimeoutSeconds = 25;

    private static Socket? _listener;
    private static readonly List<BridgeConnection> Clients = new();
    private static readonly List<ExternalTask> Externals = new();            // UI thread only

    public static string? SocketPath { get; private set; }

    // ------------------------------------------------------------ lifecycle

    public static void Start()
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.GetTempPath();
            SocketPath = Path.Combine(dir, $"redstaros-shell-{Environment.ProcessId}.sock");
            if (File.Exists(SocketPath)) File.Delete(SocketPath);

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            try { File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            _listener.Listen(16);
            _ = Task.Run(AcceptLoop);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Shell bridge disabled: " + ex.Message);
            SocketPath = null;
            _listener?.Dispose();
            _listener = null;
        }
    }

    /// <summary>Asks every connected app to quit and removes the socket (called when the session ends).</summary>
    public static void Shutdown()
    {
        foreach (var c in Snapshot()) c.SendCommand("quit");
        try { _listener?.Dispose(); } catch { }
        _listener = null;
        try { if (SocketPath != null && File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
    }

    /// <summary>Process ids of connected Electron apps (their windows already have taskbar entries).</summary>
    public static HashSet<int> ConnectedPids() => new(Snapshot().Select(c => c.Pid).Where(p => p > 0));

    private static List<BridgeConnection> Snapshot()
    {
        lock (Clients) return Clients.ToList();
    }

    private static async Task AcceptLoop()
    {
        while (_listener != null)
        {
            try
            {
                var socket = await _listener.AcceptAsync();
                _ = Task.Run(() => ClientLoop(socket));
            }
            catch (ObjectDisposedException) { break; }
            catch { await Task.Delay(200); }
        }
    }

    private static async Task ClientLoop(Socket socket)
    {
        var conn = new BridgeConnection(socket);
        lock (Clients) Clients.Add(conn);
        try
        {
            using var reader = new StreamReader(conn.Stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch { continue; }
                if (msg == null) continue;
                Dispatcher.UIThread.Post(() =>
                {
                    try { Handle(conn, msg); }
                    catch (Exception ex) { Console.Error.WriteLine("Shell bridge message ignored: " + ex.Message); }
                });
            }
        }
        catch { /* connection dropped */ }
        finally
        {
            lock (Clients) Clients.Remove(conn);
            Dispatcher.UIThread.Post(() => Disconnected(conn));
        }
    }

    // ------------------------------------------------------------ launching (taskbar placeholders)

    /// <summary>Runs an Electron app's taskbar button before the app has connected ("starting").</summary>
    public static void ExpectLaunch(string appId, string name)
    {
        if (Externals.Any(t => t.AppId == appId)) return;                      // already running / starting
        var task = new ExternalTask(appId, name);
        Externals.Add(task);
        WindowManager.AddExternal(task);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(StartupTimeoutSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (task.Starting) CancelLaunch(task);                             // never connected: launch failed
        };
        timer.Start();
    }

    public static void CancelLaunch(ExternalTask task)
    {
        Externals.Remove(task);
        WindowManager.RemoveExternal(task);
    }

    public static void CancelLaunch(string appId)
    {
        var task = Externals.FirstOrDefault(t => t.AppId == appId && t.Starting);
        if (task != null) CancelLaunch(task);
    }

    /// <summary>If the app is already running, brings its window to the front and returns true.</summary>
    public static bool FocusIfRunning(string appId)
    {
        var task = Externals.FirstOrDefault(t => t.AppId == appId && !t.Starting);
        if (task == null) return false;
        task.Activate();
        return true;
    }

    // ------------------------------------------------------------ messages from apps (UI thread)

    private static void Handle(BridgeConnection conn, JsonObject msg)
    {
        switch ((string?)msg["t"])
        {
            case "hello": OnHello(conn, msg); break;
            case "window": OnWindow(conn, msg); break;
            case "tray": OnTray(conn, msg); break;
            case "notify":
                Notifier.Show((string?)msg["title"] ?? conn.Name, (string?)msg["body"] ?? "",
                    conn.AppId.Length > 0 ? conn.AppId : null);
                break;
            case "bye": break;                                                 // the socket closing does the cleanup
        }
    }

    private static void OnHello(BridgeConnection conn, JsonObject msg)
    {
        conn.AppId = (string?)msg["app"] ?? "";
        conn.Name = (string?)msg["name"] ?? conn.AppId;
        try { conn.Pid = (int?)msg["pid"] ?? 0; } catch { }

        var task = Externals.FirstOrDefault(t => t.AppId == conn.AppId && t.Starting);
        if (task == null)
        {
            task = new ExternalTask(conn.AppId, conn.Name);
            Externals.Add(task);
            WindowManager.AddExternal(task);
        }
        task.Connection = conn;
        task.SetTitle(conn.Name);
        conn.Task = task;

        conn.Send(ThemeMessage("welcome"));
        WindowManager.RaiseChanged();
        X11Desktop.RefreshWindows();          // its window may already be listed as a foreign window: drop it
    }

    private static void OnWindow(BridgeConnection conn, JsonObject msg)
    {
        var task = conn.Task;
        if (task == null) return;

        var focused = (bool?)msg["focused"] ?? false;
        var minimized = (bool?)msg["minimized"] ?? false;
        var visible = (bool?)msg["visible"] ?? true;

        var now = Environment.TickCount64;
        if (focused && !task.Focused) task.LastFocusAt = now;
        if (!focused && task.Focused) task.LastBlurAt = now;

        task.Focused = focused;
        task.Minimized = minimized || !visible;
        task.SetTitle((string?)msg["title"]);
        WindowManager.RaiseChanged();
    }

    private static void OnTray(BridgeConnection conn, JsonObject msg)
    {
        var id = (string?)msg["id"] ?? "main";
        var key = conn.AppId + ":" + id;

        if ((bool?)msg["remove"] == true)
        {
            conn.TrayKeys.Remove(key);
            TrayService.Remove(key);
            return;
        }

        var entry = new TrayEntry
        {
            Key = key,
            Tooltip = (string?)msg["tooltip"] ?? conn.Name,
            Glyph = (string?)msg["glyph"],
            Icon = DecodeIcon((string?)msg["icon"]),
            Click = () => conn.Send(new JsonObject { ["t"] = "trayclick", ["tray"] = id }),
            MenuSelected = item => conn.Send(new JsonObject { ["t"] = "traymenu", ["tray"] = id, ["item"] = item })
        };
        if (entry.Glyph == null && entry.Icon == null) entry.Glyph = AppIcons.GlyphFor(conn.AppId, conn.Name);

        if (msg["menu"] is JsonArray menu)
        {
            foreach (var node in menu.OfType<JsonObject>())
            {
                if ((bool?)node["sep"] == true) { entry.Menu.Add(new TrayMenuEntry { Separator = true }); continue; }
                entry.Menu.Add(new TrayMenuEntry { Id = (string?)node["id"] ?? "", Label = (string?)node["label"] ?? "" });
            }
        }

        conn.TrayKeys.Add(key);
        TrayService.Upsert(entry);
    }

    private static Bitmap? DecodeIcon(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl)) return null;
        try
        {
            var comma = dataUrl.IndexOf(',');
            var bytes = Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch { return null; }
    }

    private static void Disconnected(BridgeConnection conn)
    {
        foreach (var key in conn.TrayKeys.ToList()) TrayService.Remove(key);
        conn.TrayKeys.Clear();
        if (conn.Task != null && conn.Task.Connection == conn)
        {
            Externals.Remove(conn.Task);
            WindowManager.RemoveExternal(conn.Task);
        }
    }

    // ------------------------------------------------------------ lock screen

    private static readonly List<ExternalTask> HiddenForLock = new();

    /// <summary>Minimises Electron windows while the lock screen is up and restores them afterwards.</summary>
    public static void SetLocked(bool locked)
    {
        if (locked)
        {
            foreach (var t in Externals.Where(t => !t.Starting && !t.Minimized).ToList())
            {
                HiddenForLock.Add(t);
                t.Minimize();
            }
        }
        else
        {
            foreach (var t in HiddenForLock)
                if (Externals.Contains(t)) t.Restore();
            HiddenForLock.Clear();
        }
    }

    // ------------------------------------------------------------ theme

    private static JsonObject ThemeMessage(string type) => new()
    {
        ["t"] = type,
        ["dark"] = ThemeService.IsDark,
        ["accent"] = SettingsStore.Current.Accent
    };

    /// <summary>Tells every Electron app about a new theme / accent colour.</summary>
    public static void BroadcastTheme()
    {
        var msg = ThemeMessage("theme");
        foreach (var c in Snapshot()) c.Send(msg);
    }
}
