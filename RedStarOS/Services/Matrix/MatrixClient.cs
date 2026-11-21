// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services.Matrix;

/// <summary>A Matrix server said no: its error code (M_FORBIDDEN…) and message.</summary>
public sealed class MatrixException : Exception
{
    public string Code { get; }
    public HttpStatusCode Status { get; }
    public MatrixException(HttpStatusCode status, string code, string message) : base(message) { Status = status; Code = code; }
}

/// <summary>Who is signed in where: what's saved between sessions.</summary>
public sealed record MatrixLogin(string Homeserver, string UserId, string DeviceId, string AccessToken);

/// <summary>
/// The Matrix client-server API (spec.matrix.org, the Matrix.org Foundation's open standard) over plain HTTPS, written
/// here rather than taken from an SDK: sign-in, /sync, sending, rooms, receipts, typing and media. Nothing is sent
/// anywhere but the homeserver the user chose.
/// </summary>
public sealed class MatrixClient : IDisposable
{
    private readonly HttpClient _http;
    public string Homeserver { get; private set; }
    public MatrixLogin? Login { get; private set; }

    public MatrixClient(string homeserver, HttpMessageHandler? handler = null)
    {
        Homeserver = homeserver.TrimEnd('/');
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(100);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CrimsonBannerMessenger/1.0");
    }

    public MatrixClient(MatrixLogin login, HttpMessageHandler? handler = null) : this(login.Homeserver, handler) => Login = login;

    public void Dispose() => _http.Dispose();

    /// <summary>"matrix.org", "@ada:example.org" or "https://matrix.example.org" → the server's name or URL to look up.</summary>
    public static string ServerOf(string input)
    {
        input = input.Trim();
        if (input.StartsWith('@') && input.IndexOf(':') is var i and > 0) input = input[(i + 1)..];
        return input;
    }

    /// <summary>Finds the client API's address: .well-known/matrix/client, else https://name itself.</summary>
    public static async Task<string> DiscoverAsync(string server, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        server = ServerOf(server);
        var baseUrl = server.StartsWith("http://") || server.StartsWith("https://") ? server.TrimEnd('/') : "https://" + server.TrimEnd('/');
        using var http = handler != null ? new HttpClient(handler) : new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        try
        {
            var text = await http.GetStringAsync(baseUrl + "/.well-known/matrix/client", ct);
            var url = JsonNode.Parse(text)?["m.homeserver"]?["base_url"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out _)) return url.TrimEnd('/');
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException) { }
        return baseUrl;
    }

    // ── requests ──

    private async Task<JsonNode?> Send(HttpMethod method, string path, JsonNode? body = null, CancellationToken ct = default, bool auth = true)
    {
        using var req = new HttpRequestMessage(method, Homeserver + path);
        if (auth && Login != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Login.AccessToken);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { }
        if (!res.IsSuccessStatusCode)
        {
            var code = json?["errcode"]?.GetValue<string>() ?? "M_UNKNOWN";
            var msg = json?["error"]?.GetValue<string>() ?? $"{(int)res.StatusCode} {res.ReasonPhrase}";
            throw new MatrixException(res.StatusCode, code, msg);
        }
        return json;
    }

    private static string E(string s) => Uri.EscapeDataString(s);

    /// <summary>Signs in with a user name (or full @user:server id) and password.</summary>
    public async Task<MatrixLogin> PasswordLoginAsync(string user, string password, string deviceName, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["type"] = "m.login.password",
            ["identifier"] = new JsonObject { ["type"] = "m.id.user", ["user"] = user.Trim() },
            ["password"] = password,
            ["initial_device_display_name"] = deviceName,
        };
        var r = await Send(HttpMethod.Post, "/_matrix/client/v3/login", body, ct, auth: false);
        Login = new MatrixLogin(Homeserver, r?["user_id"]?.GetValue<string>() ?? "", r?["device_id"]?.GetValue<string>() ?? "",
            r?["access_token"]?.GetValue<string>() ?? throw new MatrixException(HttpStatusCode.OK, "M_UNKNOWN", "the server sent no access token"));
        return Login;
    }

    public async Task LogoutAsync()
    {
        try { await Send(HttpMethod.Post, "/_matrix/client/v3/logout", new JsonObject()); } catch (MatrixException) { }
        Login = null;
    }

    /// <summary>The filter for /sync: a screenful of each room's recent messages, members only as needed.</summary>
    public const string SyncFilter = "{\"room\":{\"timeline\":{\"limit\":40},\"state\":{\"lazy_load_members\":true}}}";

    public Task<JsonNode?> SyncAsync(string? since, int timeoutMs, CancellationToken ct)
    {
        var q = $"/_matrix/client/v3/sync?timeout={timeoutMs}&filter={E(SyncFilter)}" + (since != null ? "&since=" + E(since) : "");
        return Send(HttpMethod.Get, q, null, ct);
    }

    public Task<JsonNode?> MessagesAsync(string roomId, string from, int limit = 40, CancellationToken ct = default) =>
        Send(HttpMethod.Get, $"/_matrix/client/v3/rooms/{E(roomId)}/messages?dir=b&limit={limit}&from={E(from)}&filter={E("{\"lazy_load_members\":true}")}", null, ct);

    private static long _txn = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string NewTxnId() => "cb" + Interlocked.Increment(ref _txn);

    /// <summary>Sends an event; returns its id.</summary>
    public async Task<string> SendEventAsync(string roomId, string type, JsonObject content, string? txnId = null)
    {
        var r = await Send(HttpMethod.Put, $"/_matrix/client/v3/rooms/{E(roomId)}/send/{E(type)}/{E(txnId ?? NewTxnId())}", content);
        return r?["event_id"]?.GetValue<string>() ?? "";
    }

    public Task<string> SendTextAsync(string roomId, string text, string? txnId = null) =>
        SendEventAsync(roomId, "m.room.message", new JsonObject { ["msgtype"] = "m.text", ["body"] = text }, txnId);

    public Task ReadReceiptAsync(string roomId, string eventId) =>
        Send(HttpMethod.Post, $"/_matrix/client/v3/rooms/{E(roomId)}/receipt/m.read/{E(eventId)}", new JsonObject());

    public Task TypingAsync(string roomId, bool typing) =>
        Send(HttpMethod.Put, $"/_matrix/client/v3/rooms/{E(roomId)}/typing/{E(Login?.UserId ?? "")}",
            typing ? new JsonObject { ["typing"] = true, ["timeout"] = 15000 } : new JsonObject { ["typing"] = false });

    public async Task<string> JoinAsync(string roomIdOrAlias)
    {
        var r = await Send(HttpMethod.Post, $"/_matrix/client/v3/join/{E(roomIdOrAlias.Trim())}", new JsonObject());
        return r?["room_id"]?.GetValue<string>() ?? roomIdOrAlias;
    }

    public Task LeaveAsync(string roomId) => Send(HttpMethod.Post, $"/_matrix/client/v3/rooms/{E(roomId)}/leave", new JsonObject());

    /// <summary>A private conversation with one person.</summary>
    public async Task<string> CreateDirectAsync(string userId)
    {
        var r = await Send(HttpMethod.Post, "/_matrix/client/v3/createRoom", new JsonObject
        {
            ["is_direct"] = true, ["preset"] = "trusted_private_chat", ["invite"] = new JsonArray(userId.Trim()),
        });
        return r?["room_id"]?.GetValue<string>() ?? "";
    }

    public async Task<string> CreateRoomAsync(string name, bool isPublic, string? alias = null)
    {
        var body = new JsonObject { ["name"] = name, ["preset"] = isPublic ? "public_chat" : "private_chat" };
        if (isPublic && !string.IsNullOrWhiteSpace(alias)) body["room_alias_name"] = alias.Trim().TrimStart('#').Split(':')[0];
        var r = await Send(HttpMethod.Post, "/_matrix/client/v3/createRoom", body);
        return r?["room_id"]?.GetValue<string>() ?? "";
    }

    /// <summary>Marks a room as a direct chat with someone (account data m.direct), as other clients expect.</summary>
    public Task SetDirectAsync(JsonObject direct) =>
        Send(HttpMethod.Put, $"/_matrix/client/v3/user/{E(Login?.UserId ?? "")}/account_data/m.direct", direct);

    // ── media ──

    /// <summary>Uploads a file; returns its mxc:// address.</summary>
    public async Task<string> UploadAsync(byte[] data, string fileName, string contentType, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Homeserver}/_matrix/media/v3/upload?filename={E(fileName)}");
        if (Login != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Login.AccessToken);
        req.Content = new ByteArrayContent(data);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new MatrixException(res.StatusCode, "M_UNKNOWN", $"upload refused ({(int)res.StatusCode})");
        return JsonNode.Parse(text)?["content_uri"]?.GetValue<string>() ?? throw new MatrixException(res.StatusCode, "M_UNKNOWN", "no content_uri");
    }

    /// <summary>"mxc://server/id" → (server, id).</summary>
    public static (string Server, string Id)? Mxc(string? uri)
    {
        if (uri == null || !uri.StartsWith("mxc://")) return null;
        var rest = uri[6..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? (rest[..slash], rest[(slash + 1)..]) : null;
    }

    /// <summary>Downloads media (a thumbnail when a size is given): the authenticated endpoints first (Matrix 1.11),
    /// then the older public ones for servers that don't have them yet.</summary>
    public async Task<byte[]?> DownloadAsync(string mxc, int thumbWidth = 0, int thumbHeight = 0, CancellationToken ct = default)
    {
        if (Mxc(mxc) is not { } m) return null;
        string tail = thumbWidth > 0 ? $"thumbnail/{E(m.Server)}/{E(m.Id)}?width={thumbWidth}&height={thumbHeight}&method=scale" : $"download/{E(m.Server)}/{E(m.Id)}";
        foreach (var (prefix, auth) in new[] { ("/_matrix/client/v1/media/", true), ("/_matrix/media/v3/", false) })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Homeserver + prefix + tail);
            if (auth && Login != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Login.AccessToken);
            try
            {
                using var res = await _http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return await res.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException) { }
        }
        return null;
    }
}
