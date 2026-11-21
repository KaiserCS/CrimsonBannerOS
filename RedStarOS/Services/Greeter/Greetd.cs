// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services.Greeter;

/// <summary>What greetd answered: a question to ask, success, or an error.</summary>
public sealed record GreetdReply(string Type, string? AuthMessageType = null, string? AuthMessage = null, string? ErrorType = null, string? Description = null)
{
    public bool IsSuccess => Type == "success";
    public bool IsError => Type == "error";
    public bool IsQuestion => Type == "auth_message";
    /// <summary>A wrong password (or another refusal from PAM), as opposed to something broken.</summary>
    public bool IsAuthError => IsError && ErrorType == "auth_error";
}

/// <summary>What the login screen needs from greetd (or, for a preview, from <see cref="DemoGreetd"/>).</summary>
public interface IGreetd : IDisposable
{
    Task<GreetdReply> CreateSessionAsync(string user, CancellationToken ct = default);
    Task<GreetdReply> RespondAsync(string? response, CancellationToken ct = default);
    Task<GreetdReply> StartSessionAsync(IReadOnlyList<string> cmd, IReadOnlyList<string> env, CancellationToken ct = default);
    Task<GreetdReply> CancelSessionAsync(CancellationToken ct = default);
}

/// <summary>
/// greetd's IPC (man 7 greetd-ipc): over the socket in $GREETD_SOCK, each message is a 32-bit length in the
/// machine's byte order followed by that much JSON. The login screen creates a session for a user, answers the
/// questions PAM asks through it (the password, usually), then asks greetd to start the chosen desktop.
/// </summary>
public sealed class GreetdClient : IGreetd
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private GreetdClient(Socket s) { _socket = s; _stream = new NetworkStream(s, ownsSocket: false); }

    public static string? SocketPath => Environment.GetEnvironmentVariable("GREETD_SOCK") is { Length: > 0 } p ? p : null;

    public static async Task<GreetdClient> ConnectAsync(string path, CancellationToken ct = default)
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await s.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); }
        catch { s.Dispose(); throw; }
        return new GreetdClient(s);
    }

    public Task<GreetdReply> CreateSessionAsync(string user, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "create_session", ["username"] = user }, ct);

    /// <summary>The answer to the last question; null for an informational message, which only needs acknowledging.</summary>
    public Task<GreetdReply> RespondAsync(string? response, CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "post_auth_message_response", ["response"] = response }, ct);

    public Task<GreetdReply> StartSessionAsync(IReadOnlyList<string> cmd, IReadOnlyList<string> env, CancellationToken ct = default)
    {
        var c = new JsonArray(); foreach (var a in cmd) c.Add(a);
        var e = new JsonArray(); foreach (var a in env) e.Add(a);
        return SendAsync(new JsonObject { ["type"] = "start_session", ["cmd"] = c, ["env"] = e }, ct);
    }

    public Task<GreetdReply> CancelSessionAsync(CancellationToken ct = default) =>
        SendAsync(new JsonObject { ["type"] = "cancel_session" }, ct);

    private async Task<GreetdReply> SendAsync(JsonObject request, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await WriteFrame(_stream, Encoding.UTF8.GetBytes(request.ToJsonString()), ct);
            return Parse(await ReadFrame(_stream, ct));
        }
        finally { _lock.Release(); }
    }

    public static async Task WriteFrame(Stream s, byte[] json, CancellationToken ct)
    {
        var len = new byte[4];
        if (BitConverter.IsLittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)json.Length);
        else BinaryPrimitives.WriteUInt32BigEndian(len, (uint)json.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(json, ct);
        await s.FlushAsync(ct);
    }

    public static async Task<byte[]> ReadFrame(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        uint n = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(len) : BinaryPrimitives.ReadUInt32BigEndian(len);
        if (n > 1 << 20) throw new InvalidDataException("greetd sent a message that's far too long");
        var body = new byte[n];
        await s.ReadExactlyAsync(body, ct);
        return body;
    }

    public static GreetdReply Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new GreetdReply(S("type") ?? "error", S("auth_message_type"), S("auth_message"), S("error_type"), S("description"));
    }

    public void Dispose()
    {
        _stream.Dispose();
        _socket.Dispose();
        _lock.Dispose();
    }
}

/// <summary>
/// greetd's stand-in for previewing the login screen on a running desktop (RedStarOS --greeter-demo): any password
/// but an empty one is accepted, and nothing is started.
/// </summary>
public sealed class DemoGreetd : IGreetd
{
    private bool _asked;
    public List<(IReadOnlyList<string> Cmd, IReadOnlyList<string> Env)> Started { get; } = new();

    public Task<GreetdReply> CreateSessionAsync(string user, CancellationToken ct = default)
    {
        _asked = true;
        return Task.FromResult(new GreetdReply("auth_message", "secret", "Password: "));
    }

    public async Task<GreetdReply> RespondAsync(string? response, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);   // about as long as PAM takes
        if (!_asked) return new GreetdReply("error", ErrorType: "error", Description: "no session");
        _asked = false;
        return string.IsNullOrEmpty(response)
            ? new GreetdReply("error", ErrorType: "auth_error", Description: "Authentication failure")
            : new GreetdReply("success");
    }

    public Task<GreetdReply> StartSessionAsync(IReadOnlyList<string> cmd, IReadOnlyList<string> env, CancellationToken ct = default)
    {
        Started.Add((cmd, env));
        return Task.FromResult(new GreetdReply("success"));
    }

    public Task<GreetdReply> CancelSessionAsync(CancellationToken ct = default) { _asked = false; return Task.FromResult(new GreetdReply("success")); }

    public void Dispose() { }
}
