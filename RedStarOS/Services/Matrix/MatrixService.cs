// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace RedStarOS.Services.Matrix;

/// <summary>
/// Messenger's connection to Matrix, for the whole session: the saved sign-in (~/.config/redstaros/matrix.json, readable
/// only by the user), the /sync loop that keeps the rooms current, sending with the message shown at once, and
/// notifications for new messages while Messenger isn't in front. Everything happens on the UI thread except the
/// network waits; <see cref="Changed"/> says when to redraw.
/// </summary>
public static class MatrixService
{
    public const string DeviceName = "Crimson Banner Messenger";
    private static MatrixClient? _client;
    private static CancellationTokenSource? _cts;
    private static HttpMessageHandler? _handler;           // tests

    public static MatrixState? State { get; private set; }
    public static MatrixLogin? Login => _client?.Login;
    public static bool SignedIn => _client?.Login != null;
    /// <summary>"Connecting…", "Offline: …", or null when all is well.</summary>
    public static string? Status { get; private set; }
    /// <summary>The rooms changed (on the UI thread); the room id when it was one room's timeline.</summary>
    public static event Action<string?>? Changed;
    /// <summary>The room Messenger shows while it's the active window: its messages don't notify.</summary>
    public static Func<string?>? Watching { get; set; }

    public static string SessionFile => AppPaths.ConfigFile("matrix.json");

    /// <summary>For the tests: talk to a fake server, keep the session in memory.</summary>
    public static void UseHandler(HttpMessageHandler? h) => _handler = h;

    /// <summary>Signs back in with the saved session (at sign-in to the desktop, or when Messenger opens).</summary>
    public static void Resume()
    {
        if (_client != null) return;
        try
        {
            if (!File.Exists(SessionFile)) return;
            var login = JsonSerializer.Deserialize<MatrixLogin>(File.ReadAllText(SessionFile));
            if (login == null || string.IsNullOrEmpty(login.AccessToken)) return;
            Start(new MatrixClient(login, _handler));
        }
        catch (Exception ex) { Console.Error.WriteLine("messenger: saved session unreadable: " + ex.Message); }
    }

    /// <summary>Signs in; null when it worked, else why not.</summary>
    public static async Task<string?> SignInAsync(string server, string user, string password)
    {
        try
        {
            var hs = await MatrixClient.DiscoverAsync(server.Length > 0 ? server : MatrixClient.ServerOf(user), _handler);
            var client = new MatrixClient(hs, _handler);
            var login = await client.PasswordLoginAsync(user, password, DeviceName);
            Save(login);
            Start(client);
            return null;
        }
        catch (MatrixException ex) { return ex.Code == "M_FORBIDDEN" ? "The user name or password is wrong." : ex.Message; }
        catch (HttpRequestException ex) { return "Couldn't reach the server: " + ex.Message; }
        catch (TaskCanceledException) { return "The server didn't answer."; }
    }

    public static async Task SignOutAsync()
    {
        var c = _client;
        Stop();
        if (c != null) { await c.LogoutAsync(); c.Dispose(); }
        try { File.Delete(SessionFile); } catch { }
        Raise(null);
    }

    private static void Save(MatrixLogin login)
    {
        if (_handler != null) return;
        try
        {
            var path = SessionFile;
            File.WriteAllText(path, "");
            // the access token is a password: only the user may read the file
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(path, JsonSerializer.Serialize(login));
        }
        catch (Exception ex) { Console.Error.WriteLine("messenger: couldn't save the session: " + ex.Message); }
    }

    private static void Start(MatrixClient client)
    {
        Stop();
        _client = client;
        State = new MatrixState(client.Login!.UserId);
        _cts = new CancellationTokenSource();
        _ = SyncLoop(client, State, _cts.Token);
    }

    private static void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _client = null;
        State = null;
    }

    private static void Raise(string? roomId) => Dispatcher.UIThread.Post(() => Changed?.Invoke(roomId));

    private static async Task SyncLoop(MatrixClient client, MatrixState state, CancellationToken ct)
    {
        int failures = 0;
        Status = "Connecting…";
        Raise(null);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var json = await client.SyncAsync(state.Since, state.Since == null ? 0 : 30000, ct);
                if (json == null) continue;
                var tcs = new TaskCompletionSource();
                // apply on the UI thread, where the window reads the state
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (!ct.IsCancellationRequested && ReferenceEquals(state, State))
                        {
                            var arrivals = state.Apply(json);
                            Status = null;
                            Changed?.Invoke(null);
                            Notify(arrivals);
                        }
                    }
                    finally { tcs.SetResult(); }
                });
                await tcs.Task;
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (MatrixException ex) when (ex.Code is "M_UNKNOWN_TOKEN" or "M_MISSING_TOKEN")
            {
                Status = "Signed out by the server. Sign in again.";
                Dispatcher.UIThread.Post(async () => await SignOutAsync());
                return;
            }
            catch (Exception ex)
            {
                failures++;
                Status = "Offline: " + (ex is MatrixException me ? me.Message : "can't reach the server") + ". Trying again…";
                Raise(null);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(failures, 5))), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static void Notify(System.Collections.Generic.List<MatrixState.Arrival> arrivals)
    {
        var watching = Watching?.Invoke();
        foreach (var g in arrivals.GroupBy(a => a.Room.Id))
        {
            if (g.Key == watching) continue;
            var last = g.Last();
            var me = State?.Me ?? "";
            var room = last.Room;
            var who = room.NameOf(last.Message.Sender);
            var title = room.IsDirect || room.DisplayName(me) == who ? who : $"{who} in {room.DisplayName(me)}";
            var id = room.Id;
            Notifier.Show(title, MatrixState.Summary(last.Message), "messenger",
                new NotificationAction("Open", () => Views.MessengerWindow.OpenRoom(id)));
        }
    }

    // ── actions (UI thread) ──

    /// <summary>Sends text: it shows at once, greyed until the server confirms it.</summary>
    public static async Task SendTextAsync(string roomId, string text)
    {
        if (_client is not { } c || State?.Rooms.GetValueOrDefault(roomId) is not { } room) return;
        var txn = MatrixClient.NewTxnId();
        var m = new MatrixMessage { Sender = State.Me, Time = DateTimeOffset.Now, Kind = MessageKind.Text, Body = text, PendingTxn = txn };
        room.Timeline.Add(m);
        Changed?.Invoke(roomId);
        try
        {
            var id = await c.SendTextAsync(roomId, text, txn);
            if (m.PendingTxn != null) { m.EventId = id; m.PendingTxn = null; }
        }
        catch (Exception ex)
        {
            m.Failed = true;
            Console.Error.WriteLine("messenger: send failed: " + ex.Message);
        }
        Changed?.Invoke(roomId);
    }

    /// <summary>Sends a file (a picture is sent as one, everything else as a file).</summary>
    public static async Task<string?> SendFileAsync(string roomId, string path)
    {
        if (_client is not { } c) return "not signed in";
        try
        {
            var data = await File.ReadAllBytesAsync(path);
            var name = Path.GetFileName(path);
            var mime = MimeOf(path);
            var mxc = await c.UploadAsync(data, name, mime);
            var type = mime.StartsWith("image/") ? "m.image" : mime.StartsWith("video/") ? "m.video" : mime.StartsWith("audio/") ? "m.audio" : "m.file";
            await c.SendEventAsync(roomId, "m.room.message", new JsonObject
            {
                ["msgtype"] = type, ["body"] = name, ["filename"] = name, ["url"] = mxc,
                ["info"] = new JsonObject { ["mimetype"] = mime, ["size"] = data.LongLength },
            });
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>The media type Matrix is told about, from the file's extension.</summary>
    public static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml", ".mp4" => "video/mp4", ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg", ".ogg" or ".oga" or ".opus" => "audio/ogg", ".wav" => "audio/wav", ".flac" => "audio/flac", ".m4a" => "audio/mp4",
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".zip" => "application/zip", ".odt" => "application/vnd.oasis.opendocument.text",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };

    public static async Task LoadOlderAsync(string roomId)
    {
        if (_client is not { } c || State?.Rooms.GetValueOrDefault(roomId) is not { PrevBatch: { } from, HasOlder: true }) return;
        try
        {
            var page = await c.MessagesAsync(roomId, from);
            if (page != null) State?.ApplyOlder(roomId, page);
        }
        catch (Exception ex) { Console.Error.WriteLine("messenger: older messages: " + ex.Message); }
        Changed?.Invoke(roomId);
    }

    /// <summary>Marks what's shown as read.</summary>
    public static void MarkRead(string roomId)
    {
        if (_client is not { } c || State?.Rooms.GetValueOrDefault(roomId) is not { } room) return;
        var last = room.Timeline.LastOrDefault(m => m.EventId.StartsWith('$'));
        if (last == null || room.Unread == 0 && room.Highlights == 0) return;
        room.Unread = room.Highlights = 0;
        _ = Quiet(() => c.ReadReceiptAsync(roomId, last.EventId));
        Changed?.Invoke(null);
    }

    private static DateTime _lastTyping;
    public static void Typing(string roomId, bool typing)
    {
        if (_client is not { } c) return;
        if (typing && (DateTime.Now - _lastTyping).TotalSeconds < 8) return;
        _lastTyping = typing ? DateTime.Now : DateTime.MinValue;
        _ = Quiet(() => c.TypingAsync(roomId, typing));
    }

    private static async Task Quiet(Func<Task> f) { try { await f(); } catch { } }

    public static Task<string?> Try(Func<MatrixClient, Task> f) => TryCore(f);

    private static async Task<string?> TryCore(Func<MatrixClient, Task> f)
    {
        if (_client is not { } c) return "not signed in";
        try { await f(c); return null; }
        catch (MatrixException ex) { return ex.Message; }
        catch (HttpRequestException ex) { return ex.Message; }
    }

    /// <summary>Opens or starts a direct chat with someone; returns the room.</summary>
    public static async Task<(string? RoomId, string? Error)> DirectChatAsync(string userId)
    {
        if (_client is not { } c || State is not { } st) return (null, "not signed in");
        userId = userId.Trim();
        if (!userId.StartsWith('@') || !userId.Contains(':')) return (null, "Write the full address, like @ada:example.org.");
        var existing = st.Rooms.Values.FirstOrDefault(r => r.IsDirect && !r.Invite && r.Members.ContainsKey(userId) && r.Members.Count <= 2);
        if (existing != null) return (existing.Id, null);
        try
        {
            var id = await c.CreateDirectAsync(userId);
            // m.direct, as other clients expect, so it shows as a direct chat everywhere
            var direct = new JsonObject();
            foreach (var r in st.Rooms.Values.Where(r => r.IsDirect))
                foreach (var who in r.Members.Keys.Where(k => k != st.Me))
                    (direct[who] as JsonArray ?? (JsonArray)(direct[who] = new JsonArray())).Add(r.Id);
            (direct[userId] as JsonArray ?? (JsonArray)(direct[userId] = new JsonArray())).Add(id);
            _ = Quiet(() => c.SetDirectAsync(direct));
            return (id, null);
        }
        catch (MatrixException ex) { return (null, ex.Message); }
        catch (HttpRequestException ex) { return (null, ex.Message); }
    }

    /// <summary>A picture's thumbnail, cached in memory.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, byte[]?> _thumbs = new();
    public static async Task<byte[]?> ThumbnailAsync(string mxc, int w, int h)
    {
        lock (_thumbs) if (_thumbs.TryGetValue(mxc, out var b)) return b;
        var data = _client != null ? await _client.DownloadAsync(mxc, w, h) : null;
        lock (_thumbs) { if (_thumbs.Count > 300) _thumbs.Clear(); _thumbs[mxc] = data; }
        return data;
    }

    /// <summary>Downloads an attachment into ~/Downloads and returns where.</summary>
    public static async Task<string?> DownloadAsync(MatrixMessage m)
    {
        if (_client is not { } c || m.Mxc == null) return null;
        var data = await c.DownloadAsync(m.Mxc);
        if (data == null) return null;
        var dir = AppPaths.UserFolder("Downloads");
        Directory.CreateDirectory(dir);
        var name = string.Concat((m.FileName ?? "file").Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(dir, name);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
        await File.WriteAllBytesAsync(path, data);
        return path;
    }
}
