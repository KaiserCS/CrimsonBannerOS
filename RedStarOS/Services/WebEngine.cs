using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RedStarOS.Services;

/// <summary>
/// Connection to the Electron "web engine" (webengine/main.js): a headless Chromium that renders every
/// tab offscreen. We own a unix socket; the engine connects to it and speaks JSON lines.
/// </summary>
public static class WebEngine
{
    private static Socket? _listener, _client;
    private static NetworkStream? _stream;
    private static readonly object WriteLock = new();
    /// <summary>What the engine reports about GPU use (rendering, video decoding), for Diagnostics.</summary>
    public static string? GpuStatus { get; private set; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, WebTab> Tabs = new();   // frames arrive on the reader thread
    private static int _next = 1;
    private static Task<bool>? _starting;
    private static TaskCompletionSource<bool>? _hello;

    public static string? Error { get; private set; }
    public static string? ChromeVersion { get; private set; }
    public static bool Connected => _client is { Connected: true } && _stream != null;

    /// <summary>Messages that don't belong to a tab (downloads, cleared data...).</summary>
    public static event Action<JsonElement>? GlobalMessage;
    /// <summary>The engine process went away; open tabs are dead until reloaded.</summary>
    public static event Action? Disconnected;

    public static int Register(WebTab t) { var id = _next++; Tabs[id] = t; return id; }

    public static void Unregister(int id)
    {
        Tabs.TryRemove(id, out _);
        if (Tabs.IsEmpty) _ = IdleShutdown();
    }

    private static async Task IdleShutdown()
    {
        await Task.Delay(30000);
        if (Tabs.IsEmpty) Shutdown();
    }

    public static void Shutdown()
    {
        try { Send(new { c = "quit" }); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _stream = null; _client = null; _listener = null;
    }

    /// <summary>
    /// Where the hidden engine runs: X11 (Xwayland on a Wayland desktop) whenever an X server is there. Chromium's
    /// hidden offscreen page windows stay hidden on X11, but on Wayland they appear as real windows on top of the
    /// browser (and its pictures never reach Red Star: white pages). Only without any X server does Chromium pick.
    /// REDSTAR_WEB_OZONE=wayland|x11 forces one (public for the tests).
    /// </summary>
    public static string OzoneFlag(string? overrideValue = null, bool? hasX = null)
    {
        overrideValue ??= Environment.GetEnvironmentVariable("REDSTAR_WEB_OZONE");
        if (overrideValue is "wayland" or "x11") return "--ozone-platform=" + overrideValue;
        bool x = hasX ?? Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };
        return x ? "--ozone-platform=x11" : "--ozone-platform-hint=auto";
    }

    public static Task<bool> EnsureStartedAsync()
    {
        if (Connected) return Task.FromResult(true);
        return _starting ??= RunStart();
    }

    private static async Task<bool> RunStart()
    {
        try { return await StartAsync(); }
        finally { _starting = null; }
    }

    private static async Task<bool> StartAsync()
    {
        try
        {
            Error = null;
            var electron = ElectronLauncher.FindElectron();
            if (electron == null) { Error = "Electron is not installed (run 'npm run setup' inside the browser folder)."; return false; }
            var dir = ElectronLauncher.FindAppDir("redstar-webengine", "webengine");
            if (dir == null) { Error = "The webengine folder was not found."; return false; }

            var rt = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(rt) || !Directory.Exists(rt)) rt = Path.GetTempPath();
            var path = Path.Combine(rt, $"redstar-web-{Environment.ProcessId}.sock");
            try { File.Delete(path); } catch { }

            try { _listener?.Dispose(); } catch { }
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            _listener.Listen(1);

            var ozone = OzoneFlag();
            BrowserLog.Write("starting the web engine: " + electron + " " + ozone + " (WAYLAND_DISPLAY=" +
                (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "-") + ", DISPLAY=" + (Environment.GetEnvironmentVariable("DISPLAY") ?? "-") + ")");
            var ok = ProcessUtil.StartDetached(electron,
                new[] { "--no-sandbox", ozone, dir }, dir,
                new Dictionary<string, string>
                {
                    ["REDSTAR_WEB_LOG"] = BrowserLog.FilePath,                                    // the engine writes its own lines there
                    ["REDSTAR_WEB_PLATFORM"] = ozone,
                    ["REDSTAR_WEB_SOCKET"] = path,
                    ["REDSTAR_REDUCE_MOTION"] = SettingsStore.Current.ReduceMotion ? "1" : "0",   // pages see prefers-reduced-motion
                    ["REDSTAR_WEB_GPU"] = Graphics.UseGpuForWeb() ? "1" : "0",                   // GPU rendering + video decoding
                });
            if (!ok) { Error = "The web engine could not be started."; return false; }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            _client = await _listener.AcceptAsync(cts.Token);
            _stream = new NetworkStream(_client, true);
            var hello = new TaskCompletionSource<bool>();
            _hello = hello;
            _ = Task.Run(ReadLoop);
            var done = await Task.WhenAny(hello.Task, Task.Delay(15000));
            try { File.Delete(path); } catch { }
            if (done != hello.Task) { Error = "The web engine did not respond."; return false; }
            return true;
        }
        catch (Exception e)
        {
            Error = e.Message;
            return false;
        }
    }

    private static async Task ReadLoop()
    {
        try
        {
            using var sr = new StreamReader(_stream!, new UTF8Encoding(false));
            string? line;
            while ((line = await sr.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                JsonElement el;
                try { using var doc = JsonDocument.Parse(line); el = doc.RootElement.Clone(); }
                catch { continue; }
                var type = el.TryGetProperty("t", out var tp) ? tp.GetString() : null;
                if (type == "gpu")
                {
                    GpuStatus = el.TryGetProperty("summary", out var gs) ? gs.GetString() : null;
                    continue;
                }
                if (type == "hello")
                {
                    if (el.TryGetProperty("version", out var v)) ChromeVersion = v.GetString();
                    _hello?.TrySetResult(true);
                    Dispatcher.UIThread.Post(() => GlobalMessage?.Invoke(el));   // windows re-send their settings
                    continue;
                }
                // Video frames are copied right here, off the UI thread, so the next one can be painted meanwhile.
                if (type == "frame" && el.TryGetProperty("tab", out var ft) && ft.TryGetInt32(out var fid) && Tabs.TryGetValue(fid, out var ftab))
                {
                    ftab.ConsumeFrame(el);
                    continue;
                }
                Dispatcher.UIThread.Post(() => Dispatch(el));
            }
        }
        catch { }
        finally
        {
            _stream = null; _client = null;
            Dispatcher.UIThread.Post(() => Disconnected?.Invoke());
        }
    }

    private static void Dispatch(JsonElement el)
    {
        if (el.TryGetProperty("tab", out var tp) && tp.TryGetInt32(out var id) && Tabs.TryGetValue(id, out var tab))
        {
            tab.Handle(el);
            return;
        }
        GlobalMessage?.Invoke(el);
    }

    public static void Send(object msg)
    {
        var s = _stream;
        if (s == null) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg) + "\n");
        try { lock (WriteLock) s.Write(bytes, 0, bytes.Length); }
        catch { }
    }
}
