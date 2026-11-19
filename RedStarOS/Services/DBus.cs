// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RedStarOS.Services.DBus;

// Red Star's own D-Bus client: the wire format, a bus connection, method calls, replies and signals. Just what the
// system tray (StatusNotifierItem, DBusMenu) needs, no third-party library (PRINCIPLES.md). Spec:
// https://dbus.freedesktop.org/doc/dbus-specification.html
//
// Values: y byte, b bool, n short, q ushort, i int, u uint, x long, t ulong, d double, s string, o ObjectPath,
// g Signature, v Variant, a… any IEnumerable (read back as List<object>; ay as byte[]), a{…} IDictionary (read back as
// Dictionary<object, object>), (…) object[].

public readonly record struct ObjectPath(string Value) { public override string ToString() => Value; }
public readonly record struct Signature(string Value) { public override string ToString() => Value; }

/// <summary>A value together with its type (D-Bus "v").</summary>
public sealed record Variant(string Signature, object Value)
{
    public static Variant Of(object value) => value switch
    {
        Variant v => v,
        string s => new("s", s),
        bool b => new("b", b),
        int i => new("i", i),
        uint u => new("u", u),
        long x => new("x", x),
        ulong t => new("t", t),
        double d => new("d", d),
        byte y => new("y", y),
        ObjectPath o => new("o", o),
        string[] a => new("as", a),
        byte[] a => new("ay", a),
        _ => throw new ArgumentException("no D-Bus type for " + value.GetType().Name),
    };
}

public sealed class DBusException : Exception
{
    public string ErrorName { get; }
    public DBusException(string name, string message) : base(name + ": " + message) { ErrorName = name; }
}

public enum MessageType : byte { MethodCall = 1, MethodReturn = 2, Error = 3, Signal = 4 }

public sealed class Message
{
    public const byte NoReplyExpected = 1;
    public MessageType Type { get; set; }
    public byte Flags { get; set; }
    public uint Serial { get; set; }
    public string? Path { get; set; }
    public string? Interface { get; set; }
    public string? Member { get; set; }
    public string? ErrorName { get; set; }
    public uint ReplySerial { get; set; }
    public string? Destination { get; set; }
    public string? Sender { get; set; }
    public string Signature { get; set; } = "";
    public object[] Body { get; set; } = Array.Empty<object>();

    public string? FirstString => Body.Length > 0 ? Body[0] as string : null;
}

/// <summary>Type signatures: splitting into single complete types, alignment.</summary>
public static class Sig
{
    public static int End(string sig, int i)
    {
        if (i >= sig.Length) throw new FormatException("signature ends early: " + sig);
        switch (sig[i])
        {
            case 'a': return End(sig, i + 1);
            case '(': case '{':
            {
                char open = sig[i], close = open == '(' ? ')' : '}';
                int depth = 0;
                for (int j = i; j < sig.Length; j++)
                {
                    if (sig[j] == open) depth++;
                    else if (sig[j] == close && --depth == 0) return j + 1;
                }
                throw new FormatException("unbalanced signature: " + sig);
            }
            default: return i + 1;
        }
    }

    public static List<string> Split(string sig)
    {
        var list = new List<string>();
        for (int i = 0; i < sig.Length;) { int e = End(sig, i); list.Add(sig[i..e]); i = e; }
        return list;
    }

    public static int Align(char c) => c switch
    {
        'y' or 'g' or 'v' => 1,
        'n' or 'q' => 2,
        'b' or 'i' or 'u' or 's' or 'o' or 'a' or 'h' => 4,
        'x' or 't' or 'd' or '(' or '{' => 8,
        _ => throw new FormatException("unknown type code " + c),
    };
}

public sealed class WireWriter
{
    private byte[] _buf = new byte[256];
    public int Length { get; private set; }
    public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();

    private Span<byte> Take(int n)
    {
        if (Length + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        var s = _buf.AsSpan(Length, n);
        Length += n;
        return s;
    }

    public void Pad(int align) { while (Length % align != 0) Take(1)[0] = 0; }
    public void Byte(byte v) => Take(1)[0] = v;
    public void UInt32(uint v) { Pad(4); BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v); }
    private void Bytes(ReadOnlySpan<byte> b) => b.CopyTo(Take(b.Length));

    public void Write(string sig, object value)
    {
        switch (sig[0])
        {
            case 'y': Byte(Convert.ToByte(value)); break;
            case 'b': UInt32(value is true ? 1u : 0u); break;
            case 'n': Pad(2); BinaryPrimitives.WriteInt16LittleEndian(Take(2), Convert.ToInt16(value)); break;
            case 'q': Pad(2); BinaryPrimitives.WriteUInt16LittleEndian(Take(2), Convert.ToUInt16(value)); break;
            case 'i': Pad(4); BinaryPrimitives.WriteInt32LittleEndian(Take(4), Convert.ToInt32(value)); break;
            case 'u': UInt32(Convert.ToUInt32(value)); break;
            case 'x': Pad(8); BinaryPrimitives.WriteInt64LittleEndian(Take(8), Convert.ToInt64(value)); break;
            case 't': Pad(8); BinaryPrimitives.WriteUInt64LittleEndian(Take(8), Convert.ToUInt64(value)); break;
            case 'd': Pad(8); BinaryPrimitives.WriteDoubleLittleEndian(Take(8), Convert.ToDouble(value)); break;
            case 's': case 'o':
            {
                var b = Encoding.UTF8.GetBytes(value is ObjectPath p ? p.Value : (string)value);
                UInt32((uint)b.Length); Bytes(b); Byte(0);
                break;
            }
            case 'g':
            {
                var b = Encoding.ASCII.GetBytes(value is Signature g ? g.Value : (string)value);
                Byte((byte)b.Length); Bytes(b); Byte(0);
                break;
            }
            case 'v':
            {
                var v = Variant.Of(value);
                Write("g", v.Signature);
                Write(v.Signature, v.Value);
                break;
            }
            case '(':
            {
                Pad(8);
                var parts = Sig.Split(sig[1..^1]);
                var vals = (object[])value;
                if (vals.Length != parts.Count) throw new ArgumentException($"struct {sig} needs {parts.Count} values");
                for (int i = 0; i < parts.Count; i++) Write(parts[i], vals[i]);
                break;
            }
            case 'a':
            {
                var elem = sig[1..];
                UInt32(0);
                int lenAt = Length - 4;
                Pad(Sig.Align(elem[0]));
                int start = Length;
                if (elem == "y" && value is byte[] raw) Bytes(raw);
                else if (elem[0] == '{')
                {
                    var kv = Sig.Split(elem[1..^1]);
                    foreach (DictionaryEntry e in (IDictionary)value)
                    {
                        Pad(8);
                        Write(kv[0], e.Key);
                        Write(kv[1], e.Value!);
                    }
                }
                else foreach (var item in (IEnumerable)value) Write(elem, item!);
                BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(lenAt, 4), (uint)(Length - start));
                break;
            }
            default: throw new ArgumentException("can't write D-Bus type " + sig);
        }
    }
}

public sealed class WireReader
{
    private readonly byte[] _b;
    private readonly bool _big;
    public int Pos { get; set; }

    public WireReader(byte[] bytes, bool bigEndian, int pos = 0) { _b = bytes; _big = bigEndian; Pos = pos; }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (Pos + n > _b.Length || n < 0) throw new FormatException("D-Bus message ends early");
        var s = _b.AsSpan(Pos, n);
        Pos += n;
        return s;
    }

    public void Align(int a) => Pos = (Pos + a - 1) / a * a;
    public uint UInt32() { Align(4); var s = Take(4); return _big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s); }

    public object Read(string sig)
    {
        switch (sig[0])
        {
            case 'y': return Take(1)[0];
            case 'b': return UInt32() != 0;
            case 'n': { Align(2); var s = Take(2); return _big ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s); }
            case 'q': { Align(2); var s = Take(2); return _big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s); }
            case 'i': return unchecked((int)UInt32());
            case 'u': case 'h': return UInt32();
            case 'x': { Align(8); var s = Take(8); return _big ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s); }
            case 't': { Align(8); var s = Take(8); return _big ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s); }
            case 'd': { Align(8); var s = Take(8); return _big ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s); }
            case 's': case 'o':
            {
                int n = (int)UInt32();
                var str = Encoding.UTF8.GetString(Take(n));
                Take(1);
                return sig[0] == 'o' ? new ObjectPath(str) : str;
            }
            case 'g': return new Signature(ReadSignature());
            case 'v':
            {
                var g = ReadSignature();
                return new Variant(g, Read(g));
            }
            case '(':
            {
                Align(8);
                return Sig.Split(sig[1..^1]).Select(Read).ToArray();
            }
            case 'a':
            {
                int n = (int)UInt32();
                var elem = sig[1..];
                Align(Sig.Align(elem[0]));
                int end = Pos + n;
                if (end > _b.Length || n < 0) throw new FormatException("D-Bus array runs past the message");
                if (elem == "y") return Take(n).ToArray();
                if (elem[0] == '{')
                {
                    var kv = Sig.Split(elem[1..^1]);
                    var d = new Dictionary<object, object>();
                    while (Pos < end) { Align(8); var k = Read(kv[0]); d[k] = Read(kv[1]); }
                    return d;
                }
                var list = new List<object>();
                while (Pos < end) list.Add(Read(elem));
                return list;
            }
            default: throw new FormatException("can't read D-Bus type " + sig);
        }
    }

    private string ReadSignature()
    {
        int n = Take(1)[0];
        var s = Encoding.ASCII.GetString(Take(n));
        Take(1);
        return s;
    }
}

/// <summary>Turns messages into bytes and back.</summary>
public static class Wire
{
    public static byte[] Encode(Message m)
    {
        var body = new WireWriter();
        var types = Sig.Split(m.Signature);
        if (types.Count != m.Body.Length) throw new ArgumentException($"signature {m.Signature} needs {types.Count} values, got {m.Body.Length}");
        for (int i = 0; i < types.Count; i++) body.Write(types[i], m.Body[i]);

        var fields = new List<object[]>();
        void Field(byte code, string sig, object? v) { if (v != null) fields.Add(new object[] { code, new Variant(sig, v) }); }
        Field(1, "o", m.Path == null ? null : new ObjectPath(m.Path));
        Field(2, "s", m.Interface);
        Field(3, "s", m.Member);
        Field(4, "s", m.ErrorName);
        if (m.ReplySerial != 0) Field(5, "u", m.ReplySerial);
        Field(6, "s", m.Destination);
        Field(7, "s", m.Sender);
        if (m.Signature.Length > 0) Field(8, "g", new Signature(m.Signature));

        var h = new WireWriter();
        h.Byte((byte)'l'); h.Byte((byte)m.Type); h.Byte(m.Flags); h.Byte(1);
        h.UInt32((uint)body.Length);
        h.UInt32(m.Serial);
        h.Write("a(yv)", fields);
        h.Pad(8);
        var head = h.ToArray();
        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.ToArray().CopyTo(all, head.Length);
        return all;
    }

    /// <summary>Bytes still needed after the 16-byte fixed header (header fields, padding and body).</summary>
    public static int RemainingLength(ReadOnlySpan<byte> fixedHeader)
    {
        bool big = fixedHeader[0] == (byte)'B';
        uint bodyLen = big ? BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[4..]);
        uint fieldsLen = big ? BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[12..]) : BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[12..]);
        long headerEnd = (16 + fieldsLen + 7) / 8 * 8;
        long total = headerEnd + bodyLen;
        if (total > 128L * 1024 * 1024) throw new FormatException("D-Bus message too large");
        return (int)(total - 16);
    }

    public static Message Decode(byte[] b)
    {
        bool big = b[0] == (byte)'B';
        if (!big && b[0] != (byte)'l') throw new FormatException("not a D-Bus message");
        var r = new WireReader(b, big, 4);
        var m = new Message { Type = (MessageType)b[1], Flags = b[2] };
        r.UInt32();                           // body length (RemainingLength checked it)
        m.Serial = r.UInt32();
        var fields = (List<object>)r.Read("a(yv)");
        foreach (object[] f in fields.Cast<object[]>())
        {
            var v = ((Variant)f[1]).Value;
            switch ((byte)f[0])
            {
                case 1: m.Path = v.ToString(); break;
                case 2: m.Interface = v as string; break;
                case 3: m.Member = v as string; break;
                case 4: m.ErrorName = v as string; break;
                case 5: m.ReplySerial = Convert.ToUInt32(v); break;
                case 6: m.Destination = v as string; break;
                case 7: m.Sender = v as string; break;
                case 8: m.Signature = v.ToString() ?? ""; break;
            }
        }
        r.Align(8);
        m.Body = Sig.Split(m.Signature).Select(r.Read).ToArray();
        return m;
    }
}

/// <summary>A connection to the session bus.</summary>
public sealed class DBusConnection : IDisposable
{
    private readonly Socket _sock;
    private readonly object _sendLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Message>> _pending = new();
    private int _serial;
    private volatile bool _closed;

    public string UniqueName { get; private set; } = "";

    /// <summary>Signals, raised on the connection's reader thread: don't block it (no waiting on calls there).</summary>
    public event Action<Message>? Signal;

    /// <summary>Method calls to us, on the reader thread. Return true when handled (replied to); anything else gets
    /// an UnknownMethod error.</summary>
    public Func<Message, bool>? MethodCall { get; set; }

    public event Action? Closed;

    private DBusConnection(Socket s) { _sock = s; }

    [DllImport("libc")] private static extern uint getuid();

    /// <summary>The session bus address: $DBUS_SESSION_BUS_ADDRESS, else $XDG_RUNTIME_DIR/bus.</summary>
    public static string? SessionAddress()
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 } a) return a;
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run && System.IO.File.Exists(System.IO.Path.Combine(run, "bus")))
            return "unix:path=" + System.IO.Path.Combine(run, "bus");
        return null;
    }

    /// <summary>The Unix socket of a bus address ("unix:path=…" or "unix:abstract=…"; the first usable entry), with
    /// a leading NUL for an abstract one; null if there is none.</summary>
    public static string? SocketPath(string address)
    {
        foreach (var entry in address.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!entry.StartsWith("unix:")) continue;
            foreach (var kv in entry[5..].Split(','))
            {
                var eq = kv.IndexOf('=');
                if (eq < 0) continue;
                var key = kv[..eq];
                var val = Uri.UnescapeDataString(kv[(eq + 1)..]);
                if (key == "path") return val;
                if (key == "abstract") return "\0" + val;
            }
        }
        return null;
    }

    public static async Task<DBusConnection> ConnectAsync(string? address = null)
    {
        address ??= SessionAddress() ?? throw new InvalidOperationException("no session bus");
        var path = SocketPath(address) ?? throw new InvalidOperationException("unsupported bus address " + address);
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(path));
            // authenticate as ourselves (EXTERNAL: the bus checks our uid on the socket)
            var uid = Convert.ToHexString(Encoding.ASCII.GetBytes(getuid().ToString())).ToLowerInvariant();
            SendRaw(sock, new byte[] { 0 });
            SendRaw(sock, Encoding.ASCII.GetBytes("AUTH EXTERNAL " + uid + "\r\n"));
            var line = ReadLine(sock);
            if (!line.StartsWith("OK ")) throw new InvalidOperationException("D-Bus refused us: " + line);
            SendRaw(sock, Encoding.ASCII.GetBytes("BEGIN\r\n"));
        }
        catch { sock.Dispose(); throw; }

        var c = new DBusConnection(sock);
        new Thread(c.ReadLoop) { IsBackground = true, Name = "dbus" }.Start();
        var hello = await c.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "Hello");
        c.UniqueName = hello.FirstString ?? "";
        return c;
    }

    private static void SendRaw(Socket s, byte[] data)
    {
        int sent = 0;
        while (sent < data.Length) sent += s.Send(data, sent, data.Length - sent, SocketFlags.None);
    }

    private static string ReadLine(Socket s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 4096)
        {
            if (s.Receive(one) != 1) throw new InvalidOperationException("D-Bus closed during sign-in");
            sb.Append((char)one[0]);
            if (sb.Length >= 2 && sb[^2] == '\r' && sb[^1] == '\n') return sb.ToString(0, sb.Length - 2);
        }
        throw new InvalidOperationException("D-Bus sign-in line too long");
    }

    private void ReceiveExact(byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            int n = _sock.Receive(buf, offset, count, SocketFlags.None);
            if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
            offset += n; count -= n;
        }
    }

    private void ReadLoop()
    {
        try
        {
            var head = new byte[16];
            while (!_closed)
            {
                ReceiveExact(head, 0, 16);
                int rest = Wire.RemainingLength(head);
                var all = new byte[16 + rest];
                head.CopyTo(all, 0);
                ReceiveExact(all, 16, rest);
                Message m;
                try { m = Wire.Decode(all); }
                catch (Exception ex) { Console.Error.WriteLine("dbus: skipped a message: " + ex.Message); continue; }
                Dispatch(m);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or FormatException) { }
        finally
        {
            _closed = true;
            foreach (var p in _pending) p.Value.TrySetException(new DBusException("org.freedesktop.DBus.Error.Disconnected", "the bus connection closed"));
            _pending.Clear();
            try { Closed?.Invoke(); } catch { }
        }
    }

    private void Dispatch(Message m)
    {
        switch (m.Type)
        {
            case MessageType.MethodReturn:
            case MessageType.Error:
                if (_pending.TryRemove(m.ReplySerial, out var tcs)) tcs.TrySetResult(m);
                break;
            case MessageType.Signal:
                try { Signal?.Invoke(m); } catch (Exception ex) { Console.Error.WriteLine("dbus: signal handler: " + ex.Message); }
                break;
            case MessageType.MethodCall:
                bool handled = false;
                try { handled = MethodCall?.Invoke(m) ?? false; }
                catch (Exception ex) { ReplyError(m, "org.freedesktop.DBus.Error.Failed", ex.Message); handled = true; }
                if (!handled && m.Interface == "org.freedesktop.DBus.Peer" && m.Member == "Ping") { Reply(m); handled = true; }
                if (!handled) ReplyError(m, "org.freedesktop.DBus.Error.UnknownMethod", $"no {m.Interface}.{m.Member} at {m.Path}");
                break;
        }
    }

    private void Send(Message m)
    {
        if (_closed) throw new DBusException("org.freedesktop.DBus.Error.Disconnected", "the bus connection is closed");
        lock (_sendLock)
        {
            m.Serial = (uint)Interlocked.Increment(ref _serial);
            SendRaw(_sock, Wire.Encode(m));
        }
    }

    /// <summary>Calls a method and waits for its reply; an error reply throws <see cref="DBusException"/>.</summary>
    public async Task<Message> CallAsync(string destination, string path, string iface, string member,
        string signature = "", object[]? body = null, int timeoutMs = 5000)
    {
        var m = new Message
        {
            Type = MessageType.MethodCall, Destination = destination, Path = path, Interface = iface, Member = member,
            Signature = signature, Body = body ?? Array.Empty<object>(),
        };
        var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sendLock)
        {
            m.Serial = (uint)Interlocked.Increment(ref _serial);
            _pending[m.Serial] = tcs;
            try { SendRaw(_sock, Wire.Encode(m)); }
            catch { _pending.TryRemove(m.Serial, out _); throw; }
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (done != tcs.Task)
        {
            _pending.TryRemove(m.Serial, out _);
            throw new DBusException("org.freedesktop.DBus.Error.NoReply", $"{destination} didn't answer {iface}.{member}");
        }
        var reply = await tcs.Task;
        if (reply.Type == MessageType.Error) throw new DBusException(reply.ErrorName ?? "error", reply.FirstString ?? "");
        return reply;
    }

    public void Reply(Message call, string signature = "", params object[] body)
    {
        if ((call.Flags & Message.NoReplyExpected) != 0) return;
        Send(new Message { Type = MessageType.MethodReturn, ReplySerial = call.Serial, Destination = call.Sender, Signature = signature, Body = body });
    }

    public void ReplyError(Message call, string name, string text)
    {
        if ((call.Flags & Message.NoReplyExpected) != 0) return;
        Send(new Message { Type = MessageType.Error, ReplySerial = call.Serial, Destination = call.Sender, ErrorName = name, Signature = "s", Body = new object[] { text } });
    }

    public void EmitSignal(string path, string iface, string member, string signature = "", params object[] body) =>
        Send(new Message { Type = MessageType.Signal, Path = path, Interface = iface, Member = member, Signature = signature, Body = body });

    // ── the bus itself ──
    private const string Bus = "org.freedesktop.DBus", BusPath = "/org/freedesktop/DBus";

    /// <summary>RequestName; 1 = we own it now, 2 = queued, 3 = someone else has it, 4 = we already had it.</summary>
    public async Task<uint> RequestNameAsync(string name, uint flags = 4 /* do not queue */) =>
        (uint)(await CallAsync(Bus, BusPath, Bus, "RequestName", "su", new object[] { name, flags })).Body[0];

    public Task AddMatchAsync(string rule) => CallAsync(Bus, BusPath, Bus, "AddMatch", "s", new object[] { rule });

    /// <summary>The unique name (":1.42") that owns a name, or null.</summary>
    public async Task<string?> GetNameOwnerAsync(string name)
    {
        try { return (await CallAsync(Bus, BusPath, Bus, "GetNameOwner", "s", new object[] { name })).FirstString; }
        catch (DBusException) { return null; }
    }

    public async Task<Dictionary<string, object>> GetAllPropertiesAsync(string dest, string path, string iface)
    {
        var r = await CallAsync(dest, path, "org.freedesktop.DBus.Properties", "GetAll", "s", new object[] { iface });
        var d = new Dictionary<string, object>();
        if (r.Body.Length > 0 && r.Body[0] is Dictionary<object, object> props)
            foreach (var (k, v) in props) d[(string)k] = v is Variant var ? var.Value : v;
        return d;
    }

    public async Task<object?> GetPropertyAsync(string dest, string path, string iface, string name)
    {
        var r = await CallAsync(dest, path, "org.freedesktop.DBus.Properties", "Get", "ss", new object[] { iface, name });
        return r.Body.Length > 0 && r.Body[0] is Variant v ? v.Value : null;
    }

    public void Dispose()
    {
        _closed = true;
        try { _sock.Shutdown(SocketShutdown.Both); } catch { }
        _sock.Dispose();
    }
}
