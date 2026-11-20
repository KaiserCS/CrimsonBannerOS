// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>
/// The administrator password prompt: a polkit authentication agent. When a program asks polkit to do something that
/// needs an administrator (NetworkManager changing a system connection, a disk tool, pkexec, Software Updates…), polkit
/// asks the session's agent; Crimson Banner shows its own dialog and hands the password to polkit's own setuid helper
/// (polkit-agent-helper-1), which checks it with PAM and tells polkit. Crimson Banner never decides anything itself.
/// When Crimson Banner runs on top of another desktop, that desktop's agent is already registered and keeps the job.
/// </summary>
public sealed class PolkitAgent
{
    public const string AuthorityName = "org.freedesktop.PolicyKit1", AuthorityPath = "/org/freedesktop/PolicyKit1/Authority",
        AuthorityIface = "org.freedesktop.PolicyKit1.Authority", AgentIface = "org.freedesktop.PolicyKit1.AuthenticationAgent",
        AgentPath = "/org/crimsonbanner/PolicyKit1/AuthenticationAgent";

    /// <summary>What polkit asks: the action, its message, and who may answer (unix user names).</summary>
    public sealed record Request(string ActionId, string Message, string Icon, IReadOnlyDictionary<string, string> Details, string Cookie, IReadOnlyList<string> Users);

    /// <summary>Asks the user. Returns the chosen user and a function that tries one password (true when accepted,
    /// with a message on failure), or null when the user cancelled. Runs on the UI's side; set by the shell.</summary>
    public Func<Request, Func<string, string, Task<(bool Ok, string? Error)>>, CancellationToken, Task<bool>>? Prompt { get; set; }

    private readonly DBusConnection _bus;
    private readonly Dictionary<string, CancellationTokenSource> _running = new();

    public PolkitAgent(DBusConnection bus) { _bus = bus; }

    /// <summary>"unix-process" for this process: polkit works out the session from it.</summary>
    public static object[] Subject()
    {
        int pid = Environment.ProcessId;
        ulong start = 0;
        try
        {
            // field 22 of /proc/self/stat, after the ")" that ends the program's name
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            start = ulong.Parse(fields[19]);
        }
        catch { }
        return new object[] { "unix-process", new Dictionary<object, object>
        {
            ["pid"] = new Variant("u", (uint)pid), ["start-time"] = new Variant("t", start), ["uid"] = new Variant("i", (int)getuid()),
        } };
    }

    [DllImport("libc")] private static extern uint getuid();

    /// <summary>Registers with polkit. False (and why) when another agent already serves this session.</summary>
    public async Task<(bool Ok, string? Why)> RegisterAsync()
    {
        _bus.MethodCall = OnMethodCall;
        try
        {
            var locale = (Environment.GetEnvironmentVariable("LANG") ?? "en_US.UTF-8");
            await _bus.CallAsync(AuthorityName, AuthorityPath, AuthorityIface, "RegisterAuthenticationAgent", "(sa{sv})ss",
                new object[] { Subject(), locale, AgentPath });
            return (true, null);
        }
        catch (DBusException ex) { return (false, ex.Message); }
    }

    /// <summary>Users from polkit's identities ("unix-user" with a uid); the signed-in user first, root last.</summary>
    public static List<string> Users(object? identities)
    {
        var users = new List<string>();
        if (identities is not List<object> list) return users;
        foreach (var id in list.OfType<object[]>())
        {
            if (id.Length < 2 || id[0] as string != "unix-user" || id[1] is not Dictionary<object, object> d) continue;
            if (!d.TryGetValue("uid", out var v)) continue;
            var uid = Convert.ToUInt32(v is Variant vv ? vv.Value : v);
            if (NameOf(uid) is { } n) users.Add(n);
        }
        var me = Environment.UserName;
        return users.Distinct().OrderBy(u => u == me ? 0 : u == "root" ? 2 : 1).ToList();
    }

    /// <summary>A user's name from /etc/passwd.</summary>
    public static string? NameOf(uint uid)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var f = line.Split(':');
                if (f.Length > 2 && uint.TryParse(f[2], out var u) && u == uid) return f[0];
            }
        }
        catch { }
        return uid == 0 ? "root" : null;
    }

    private bool OnMethodCall(Message m)
    {
        if (m.Path != AgentPath) return false;
        switch (m.Member)
        {
            case "BeginAuthentication" when m.Interface is AgentIface or null:
            {
                var b = m.Body;
                var details = (b.ElementAtOrDefault(3) as Dictionary<object, object> ?? new())
                    .ToDictionary(k => k.Key.ToString() ?? "", k => k.Value?.ToString() ?? "");
                var req = new Request(b.ElementAtOrDefault(0) as string ?? "", b.ElementAtOrDefault(1) as string ?? "",
                    b.ElementAtOrDefault(2) as string ?? "", details, b.ElementAtOrDefault(4) as string ?? "", Users(b.ElementAtOrDefault(5)));
                _ = Begin(m, req);                // answered when the user is done
                return true;
            }
            case "CancelAuthentication" when m.Interface is AgentIface or null:
            {
                var cookie = m.FirstString ?? "";
                lock (_running) if (_running.TryGetValue(cookie, out var cts)) cts.Cancel();
                _bus.Reply(m);
                return true;
            }
            case "Introspect" when m.Interface is "org.freedesktop.DBus.Introspectable" or null:
                _bus.Reply(m, "s", Introspection);
                return true;
        }
        return false;
    }

    private async Task Begin(Message call, Request req)
    {
        var cts = new CancellationTokenSource();
        lock (_running) _running[req.Cookie] = cts;
        try
        {
            if (req.Users.Count == 0 || Prompt == null)
            {
                _bus.ReplyError(call, "org.freedesktop.PolicyKit1.Error.Failed", "nobody here can answer for this");
                return;
            }
            bool done = await Prompt(req, (user, password) => RunHelper(user, req.Cookie, password, cts.Token), cts.Token);
            if (done) _bus.Reply(call);
            else _bus.ReplyError(call, "org.freedesktop.PolicyKit1.Error.Cancelled", "the user cancelled");
        }
        catch (Exception ex)
        {
            _bus.ReplyError(call, "org.freedesktop.PolicyKit1.Error.Failed", ex.Message);
        }
        finally { lock (_running) _running.Remove(req.Cookie); }
    }

    /// <summary>polkit's helper, wherever the distribution put it.</summary>
    public static string? HelperPath() =>
        new[] { "/usr/lib/polkit-1/polkit-agent-helper-1", "/usr/libexec/polkit-agent-helper-1", "/usr/lib/policykit-1/polkit-agent-helper-1",
                "/usr/libexec/polkit-1/polkit-agent-helper-1" }.FirstOrDefault(File.Exists);

    /// <summary>
    /// Runs polkit-agent-helper-1 for one try: it reads the cookie, asks through PAM ("PAM_PROMPT_ECHO_OFF …" lines),
    /// and ends with SUCCESS or FAILURE, having told polkit itself.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> RunHelper(string user, string cookie, string password, CancellationToken ct)
    {
        var helper = HelperPath();
        if (helper == null) return (false, "polkit's helper (polkit-agent-helper-1) wasn't found");
        var psi = new ProcessStartInfo(helper) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(user);
        using var p = Process.Start(psi);
        if (p == null) return (false, "polkit's helper didn't start");
        using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
        string? info = null;
        try
        {
            await p.StandardInput.WriteLineAsync(cookie);
            await p.StandardInput.FlushAsync();
            bool answered = false;
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("PAM_PROMPT_ECHO_OFF") || line.StartsWith("PAM_PROMPT_ECHO_ON"))
                {
                    // one password per try: a second question means PAM wants more than we have (it'll fail)
                    await p.StandardInput.WriteLineAsync(answered ? "" : password);
                    await p.StandardInput.FlushAsync();
                    answered = true;
                }
                else if (line.StartsWith("PAM_ERROR_MSG") || line.StartsWith("PAM_TEXT_INFO")) info = line[(line.IndexOf(' ') + 1)..].Trim();
                else if (line.StartsWith("SUCCESS")) { await p.WaitForExitAsync(ct); return (true, null); }
                else if (line.StartsWith("FAILURE")) break;
            }
        }
        catch (OperationCanceledException) { return (false, "cancelled"); }
        catch (IOException) { }
        try { await p.WaitForExitAsync(ct); } catch { }
        return (false, info ?? "That password didn't work.");
    }

    private const string Introspection =
        "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\" \"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">\n" +
        "<node>\n" +
        "  <interface name=\"org.freedesktop.PolicyKit1.AuthenticationAgent\">\n" +
        "    <method name=\"BeginAuthentication\"><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/>" +
        "<arg type=\"a{ss}\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"a(sa{sv})\" direction=\"in\"/></method>\n" +
        "    <method name=\"CancelAuthentication\"><arg type=\"s\" direction=\"in\"/></method>\n" +
        "  </interface>\n" +
        "</node>\n";

    // ── start-up ──

    private static PolkitAgent? _agent;

    /// <summary>Registers Crimson Banner's agent on the system bus (in a Crimson Banner session); the shell's dialog
    /// answers.</summary>
    public static async void Start(Func<Request, Func<string, string, Task<(bool Ok, string? Error)>>, CancellationToken, Task<bool>> prompt)
    {
        if (_agent != null || !File.Exists("/run/dbus/system_bus_socket")) return;
        try
        {
            var bus = await DBusConnection.ConnectAsync("unix:path=/run/dbus/system_bus_socket");
            var agent = new PolkitAgent(bus) { Prompt = prompt };
            var (ok, why) = await agent.RegisterAsync();
            if (!ok) { Console.Error.WriteLine("polkit: not the authentication agent: " + why); bus.Dispose(); return; }
            _agent = agent;
            bus.Closed += () => _agent = null;
            Console.Error.WriteLine("polkit: Crimson Banner answers administrator password prompts");
        }
        catch (Exception ex) { Console.Error.WriteLine("polkit: unavailable: " + ex.Message); }
    }
}
