using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RedStarOS.Services;

public sealed class ContextInfo
{
    public int X, Y;
    public string Link = "", Src = "", Media = "none", Sel = "", Page = "";
    public bool Editable;
}

/// <summary>One browser tab: state mirrored from the engine plus the latest frame.</summary>
public sealed class WebTab
{
    public static string SearchTemplate = "https://duckduckgo.com/?q={0}";
    public int Id { get; private set; }
    public bool Private { get; }
    public int Group { get; set; }          // private windows share one in-memory session per group
    public int ErrorCode { get; private set; }
    public bool Muted { get; private set; }
    public bool Audible { get; private set; }
    public int Blocked { get; private set; }
    public bool InReader { get; private set; }     // showing the engine's reader view of Url
    public bool InPip { get; set; }
    public string Internal { get; set; } = "";   // "newtab", "history"... : a native page is shown instead of the engine frame
    public string Title { get; private set; } = "New tab";
    public string Url { get; private set; } = "";
    public bool Loading { get; private set; }
    public bool CanBack { get; private set; }
    public bool CanForward { get; private set; }
    public string Favicon { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string CursorName { get; private set; } = "default";
    public string? ErrorText { get; private set; }
    public string? ErrorUrl { get; private set; }
    public double Zoom { get; private set; } = 1.0;
    public int FindActive { get; private set; }
    public int FindCount { get; private set; }
    public bool Alive { get; private set; }
    public WriteableBitmap? Frame { get; private set; }
    public int Width { get; private set; } = 1000;
    public int Height { get; private set; } = 700;

    public event Action? Changed;          // title, url, loading, nav state, status, error
    public event Action? FrameReady;
    public event Action? CursorChanged;
    public event Action<string, string, int>? NewTabRequested;
    /// <summary>A human-verification check (Cloudflare, hCaptcha, reCAPTCHA…) appeared on the page; the host it came from.</summary>
    public event Action<string>? ChallengeSeen;      // url, disposition, engine pop-up key (0: none)
    public event Action? ClosedByPage;                              // window.close() in the page
    public event Action<int, string, string, bool>? AuthRequested;  // request id, host, realm, proxy
    public event Action<bool>? FullscreenRequested;
    public event Action<bool, int, int, bool>? PipChanged;          // on, video width, height, paused
    public event Action<string>? PipFailed;
    public event Action<bool>? PipPlayState;                        // paused
    public event Action<string>? EngineError;
    public event Action<ContextInfo>? ContextMenuRequested;
    public event Action<int, string, string>? PermissionRequested;  // request id, permission, url
    public event Action? FindChanged;
    public event Action<string, string, string, string, string>? PageData;   // kind, title, html, url, error
    public event Action<string, bool>? PdfDone;
    public event Action<string>? CopyRequested;      // text the page wants on the clipboard

    private string _pendingUrl = "";

    public WebTab(bool priv = false) { Private = priv; }

    public void Open(string url, int w, int h)
    {
        Width = Math.Max(100, w); Height = Math.Max(100, h);
        Id = WebEngine.Register(this);
        Alive = true;
        _pendingUrl = url;
        Url = url;
        WebEngine.Send(new { c = "open", tab = Id, url, w = Width, h = Height, priv = Private, group = Group, zoom = Zoom });
    }

    /// <summary>Takes over a window the page opened itself (keeps window.opener, needed by sign-in pop-ups).</summary>
    public void Adopt(int child, string url, int w, int h)
    {
        Width = Math.Max(100, w); Height = Math.Max(100, h);
        Id = WebEngine.Register(this);
        Alive = true;
        _pendingUrl = url; Url = url;
        WebEngine.Send(new { c = "open", tab = Id, adopt = child, url, w = Width, h = Height, priv = Private, group = Group, zoom = Zoom });
    }

    /// <summary>Re-creates the page after the engine restarted.</summary>
    public async System.Threading.Tasks.Task<bool> ReviveAsync()
    {
        if (!await WebEngine.EnsureStartedAsync()) return false;
        var url = string.IsNullOrEmpty(Url) ? _pendingUrl : Url;
        WebEngine.Unregister(Id);
        Open(url, Width, Height);
        ErrorText = null;
        Changed?.Invoke();
        return true;
    }

    public void MarkDead(string why)
    {
        Alive = false; Loading = false;
        ErrorText = why; ErrorUrl = Url;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (Id == 0) return;
        WebEngine.Send(new { c = "close", tab = Id });
        WebEngine.Unregister(Id);
        Id = 0;
        lock (_frameLock) UnmapFrameFile();
    }

    // ───────────────────────── commands ─────────────────────────
    public static string Normalize(string input, string? searchTemplate = null)
    {
        searchTemplate ??= SearchTemplate;
        input = input.Trim();
        if (input.Length == 0) return "about:blank";
        if (Regex.IsMatch(input, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://") || Regex.IsMatch(input, @"^(about|data|view-source|file|mailto|javascript):", RegexOptions.IgnoreCase))
            return input;
        if (!input.Contains(' '))
        {
            var host = input.Split('/', '?', '#')[0];
            bool local = host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}(:\d+)?$");
            if (local) return "http://" + input;
            if (host.Contains('.') && !host.EndsWith(".") && Regex.IsMatch(host, @"^[\w\-\.]+(:\d+)?$")) return "https://" + input;
        }
        return string.Format(searchTemplate, Uri.EscapeDataString(input));
    }

    public void Navigate(string input)
    {
        var url = Normalize(input);
        Url = url; ErrorText = null;
        WebEngine.Send(new { c = "nav", tab = Id, url });
        Changed?.Invoke();
    }

    public void Back() => WebEngine.Send(new { c = "back", tab = Id });
    public void Forward() => WebEngine.Send(new { c = "forward", tab = Id });
    public void Stop() => WebEngine.Send(new { c = "stop", tab = Id });

    public void Reload(bool hard = false)
    {
        if (!Alive) { _ = ReviveAsync(); return; }
        if (ErrorText != null && !string.IsNullOrEmpty(ErrorUrl)) { ErrorText = null; WebEngine.Send(new { c = "nav", tab = Id, url = ErrorUrl }); Changed?.Invoke(); return; }
        WebEngine.Send(new { c = "reload", tab = Id, hard });
    }

    public void Resize(int w, int h)
    {
        w = Math.Max(100, w); h = Math.Max(100, h);
        if (w == Width && h == Height) return;
        Width = w; Height = h;
        if (Alive) WebEngine.Send(new { c = "resize", tab = Id, w, h });
    }

    public void Focus() { if (Alive) WebEngine.Send(new { c = "focus", tab = Id }); }

    public void Mouse(string type, double x, double y, string button, int clicks, string[] mods) =>
        WebEngine.Send(new { c = "mouse", tab = Id, type, x = (int)x, y = (int)y, button, clicks, mods });

    public void Wheel(double x, double y, double dx, double dy, string[] mods) =>
        WebEngine.Send(new { c = "wheel", tab = Id, x = (int)x, y = (int)y, dx, dy, mods });

    public void Key(string type, string key, string[] mods) => WebEngine.Send(new { c = "key", tab = Id, type, key, mods });
    public void Char(string text) => WebEngine.Send(new { c = "char", tab = Id, text });
    public void Insert(string text) => WebEngine.Send(new { c = "insert", tab = Id, text });
    public void Edit(string cmd) => WebEngine.Send(new { c = "edit", tab = Id, cmd });

    public void SetZoom(double z)
    {
        Zoom = Math.Clamp(z, 0.25, 5);
        WebEngine.Send(new { c = "zoom", tab = Id, factor = Zoom });
        Changed?.Invoke();
    }

    public void Find(string text, bool forward = true, bool next = false, bool matchCase = false)
    {
        if (string.IsNullOrEmpty(text)) { StopFind(); return; }
        WebEngine.Send(new { c = "find", tab = Id, text, forward, next, matchCase });
    }

    public void StopFind()
    {
        FindActive = 0; FindCount = 0;
        WebEngine.Send(new { c = "stopfind", tab = Id });
        FindChanged?.Invoke();
    }

    public void SetMuted(bool m) { Muted = m; WebEngine.Send(new { c = "mute", tab = Id, on = m }); Changed?.Invoke(); }
    public void DevTools() => WebEngine.Send(new { c = "devtools", tab = Id });

    /// <summary>Opens the page in a real browser window sharing this tab's cookies (verification checks work there);
    /// closing it reloads the tab.</summary>
    public void PopOut() => WebEngine.Send(new { c = "popout", tab = Id });
    public void Reader(bool dark) => WebEngine.Send(new { c = "reader", tab = Id, dark });
    public void ExtractPage(string kind) => WebEngine.Send(new { c = "extract", tab = Id, kind });
    public void SavePdf(string path) => WebEngine.Send(new { c = "pdf", tab = Id, path });
    public void PictureInPicture() => WebEngine.Send(new { c = "pip", tab = Id });
    public void PipExit() => WebEngine.Send(new { c = "pipexit", tab = Id });
    public void PipPlay() => WebEngine.Send(new { c = "pipplay", tab = Id });
    public void ExitFullscreen() => WebEngine.Send(new { c = "exitfullscreen", tab = Id });
    public void AnswerAuth(int req, string? user, string? pass) =>
        WebEngine.Send(new { c = "authreply", req, user = user ?? "", pass = pass ?? "", cancel = user == null });
    public void AllowCert(string host) => WebEngine.Send(new { c = "allowcert", host });

    public void AnswerPermission(int req, bool allow) => WebEngine.Send(new { c = "perm", req, allow });

    // ───────────────────────── events from the engine ─────────────────────────
    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public void Handle(JsonElement m)
    {
        switch (Str(m, "t"))
        {
            case "frame": ConsumeFrame(m); break;          // normally handled on the reader thread already
            case "title": Title = Str(m, "title"); Changed?.Invoke(); break;
            case "loading": Loading = Bool(m, "on"); if (Loading) ErrorText = null; Changed?.Invoke(); break;
            case "url":
                Url = Str(m, "url"); CanBack = Bool(m, "back"); CanForward = Bool(m, "fwd"); ErrorText = null; InReader = Bool(m, "reader");
                Changed?.Invoke(); break;
            case "favicon": Favicon = Str(m, "url"); Changed?.Invoke(); break;
            case "status": Status = Str(m, "url"); Changed?.Invoke(); break;
            case "cursor": CursorName = Str(m, "type"); CursorChanged?.Invoke(); break;
            case "fail":
                ErrorText = Str(m, "desc"); ErrorUrl = Str(m, "url"); ErrorCode = Int(m, "code"); Loading = false;
                if (string.IsNullOrEmpty(Url)) Url = ErrorUrl ?? "";
                Changed?.Invoke(); break;
            case "challenge": ChallengeSeen?.Invoke(Str(m, "host")); break;
            case "newtab": NewTabRequested?.Invoke(Str(m, "url"), Str(m, "disposition"), Int(m, "child")); break;
            case "closed": Alive = false; ClosedByPage?.Invoke(); break;
            case "auth": AuthRequested?.Invoke(Int(m, "req"), Str(m, "host"), Str(m, "realm"), Bool(m, "proxy")); break;
            case "fullscreen": FullscreenRequested?.Invoke(Bool(m, "on")); break;
            case "pip": InPip = Bool(m, "on"); PipChanged?.Invoke(InPip, Int(m, "w"), Int(m, "h"), Bool(m, "paused")); break;
            case "pipfail": InPip = false; PipFailed?.Invoke(Str(m, "why")); break;
            case "pipstate": PipPlayState?.Invoke(Bool(m, "paused")); break;
            case "error": EngineError?.Invoke(Str(m, "msg")); break;
            case "found": FindActive = Int(m, "active"); FindCount = Int(m, "matches"); FindChanged?.Invoke(); break;
            case "context":
                ContextMenuRequested?.Invoke(new ContextInfo
                {
                    X = Int(m, "x"), Y = Int(m, "y"), Link = Str(m, "link"), Src = Str(m, "src"), Media = Str(m, "media"),
                    Sel = Str(m, "sel"), Page = Str(m, "page"), Editable = Bool(m, "editable")
                });
                break;
            case "audio": Audible = Bool(m, "on"); Changed?.Invoke(); break;
            case "blocked": Blocked = Int(m, "n"); Changed?.Invoke(); break;
            case "siteblocked": ErrorText = "This site is blocked by your browser settings."; ErrorUrl = Str(m, "url"); ErrorCode = -9999; Loading = false; Changed?.Invoke(); break;
            case "pagedata": PageData?.Invoke(Str(m, "kind"), Str(m, "title"), Str(m, "html"), Str(m, "url"), Str(m, "error")); break;
            case "pdfdone": PdfDone?.Invoke(Str(m, "path"), Bool(m, "ok")); break;
            case "clip": CopyRequested?.Invoke(Str(m, "text")); break;
            case "permission": PermissionRequested?.Invoke(Int(m, "req"), Str(m, "permission"), Str(m, "url")); break;
        }
    }

    // ───────────────────────── frames ─────────────────────────
    // The engine keeps the page as BGRA in a shared-memory file and says which rows changed (y0..y1). They are
    // copied from a memory mapping straight into the bitmap on the engine's reader thread, then acknowledged so
    // the engine can paint the next frame while the UI draws this one.
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private string? _mapPath;
    private long _mapLen;
    private readonly object _frameLock = new();

    private unsafe byte* MapFrameFile(string file, long len)
    {
        if (_view == null || _mapPath != file || _mapLen != len)
        {
            UnmapFrameFile();
            _map = MemoryMappedFile.CreateFromFile(file, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _map.CreateViewAccessor(0, len, MemoryMappedFileAccess.Read);
            _mapPath = file; _mapLen = len;
        }
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        return p + _view.PointerOffset;
    }

    private void UnmapFrameFile()
    {
        try { _view?.Dispose(); } catch { }
        try { _map?.Dispose(); } catch { }
        _view = null; _map = null; _mapPath = null; _mapLen = 0;
    }

    /// <summary>Called on the engine connection's reader thread.</summary>
    internal unsafe void ConsumeFrame(JsonElement m)
    {
        int id = Id;
        try
        {
            lock (_frameLock)
            {
                var file = Str(m, "file");
                int w = Int(m, "w"), h = Int(m, "h");
                if (w <= 0 || h <= 0 || !File.Exists(file)) return;
                int y0 = m.TryGetProperty("y0", out _) ? Int(m, "y0") : 0;
                int y1 = m.TryGetProperty("y1", out _) ? Int(m, "y1") : h;
                var frame = Frame;
                if (frame == null || frame.PixelSize.Width != w || frame.PixelSize.Height != h)
                {
                    frame = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    y0 = 0; y1 = h;                                  // a new bitmap needs every row
                }
                y0 = Math.Clamp(y0, 0, h); y1 = Math.Clamp(y1, y0, h);
                long row = (long)w * 4, len = row * h;
                if (new FileInfo(file).Length < len) return;          // the engine is resizing the file
                byte* src = MapFrameFile(file, len);
                try
                {
                    using var fb = frame.Lock();
                    if (fb.RowBytes == row)
                        Buffer.MemoryCopy(src + y0 * row, (byte*)fb.Address + y0 * row, (h - y0) * row, (y1 - y0) * row);
                    else
                        for (int y = y0; y < y1; y++)
                            Buffer.MemoryCopy(src + y * row, (byte*)fb.Address + (long)y * fb.RowBytes, fb.RowBytes, row);
                }
                finally { _view?.SafeMemoryMappedViewHandle.ReleasePointer(); }
                Frame = frame;
            }
        }
        catch { UnmapFrameFile(); }
        finally
        {
            if (id != 0) WebEngine.Send(new { c = "ack", tab = id });
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FrameReady?.Invoke(), Avalonia.Threading.DispatcherPriority.Render);
    }
}
