// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>
/// Other programs' notifications (Discord, Steam, Crimson Online, Thunderbird, notify-send…): the freedesktop.org
/// Desktop Notifications service, org.freedesktop.Notifications on the session bus, spoken with our own D-Bus client.
/// What arrives is shown through <see cref="Notifier"/> like Crimson Banner's own messages: a toast (unless Do Not
/// Disturb is on) and the notification centre. Buttons the program offers become the toast's buttons. When another
/// desktop already provides the service (Crimson Banner on top of KDE), that desktop keeps showing them.
/// </summary>
public sealed class NotificationServer
{
    public const string Name = "org.freedesktop.Notifications", Path = "/org/freedesktop/Notifications", Iface = "org.freedesktop.Notifications";

    // why a notification went away (NotificationClosed)
    public const uint Expired = 1, Dismissed = 2, ClosedByCall = 3;

    /// <summary>One notification as a program sent it.</summary>
    public sealed record Incoming(uint Id, string App, string Icon, string Summary, string Body, IReadOnlyList<(string Key, string Label)> Actions,
        byte Urgency, string? DesktopEntry, bool Transient, uint Replaces);

    private readonly DBusConnection _bus;
    private uint _next;
    private readonly Dictionary<uint, string> _owners = new();          // id → sender, for signals
    public bool Owner { get; private set; }

    /// <summary>A notification to show (raised on the bus's thread).</summary>
    public event Action<Incoming>? Received;
    /// <summary>A program asked for its notification to go (CloseNotification).</summary>
    public event Action<uint>? CloseRequested;

    public NotificationServer(DBusConnection bus) { _bus = bus; }

    /// <summary>Takes the name; false when another server already has it.</summary>
    public async Task<bool> StartAsync()
    {
        _bus.MethodCall = OnMethodCall;
        uint got = await _bus.RequestNameAsync(Name);
        Owner = got is 1 or 4;
        return Owner;
    }

    /// <summary>The user pressed one of its buttons ("default" for the notification itself).</summary>
    public void Invoke(uint id, string key)
    {
        _bus.EmitSignal(Path, Iface, "ActionInvoked", "us", id, key);
        Close(id, Dismissed);
    }

    /// <summary>It went: tells the program why.</summary>
    public void Close(uint id, uint reason)
    {
        lock (_owners) if (!_owners.Remove(id)) return;
        _bus.EmitSignal(Path, Iface, "NotificationClosed", "uu", id, reason);
    }

    /// <summary>Body text without markup: tags dropped, entities decoded, links kept as their text.</summary>
    public static string Plain(string body)
    {
        var s = Regex.Replace(body, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        return WebUtility.HtmlDecode(s).Trim();
    }

    private bool OnMethodCall(Message m)
    {
        if (m.Path != Path) return false;
        switch (m.Member)
        {
            case "Notify" when m.Interface is Iface or null:
            {
                var b = m.Body;
                string app = b.ElementAtOrDefault(0) as string ?? "";
                uint replaces = b.ElementAtOrDefault(1) is uint r ? r : 0;
                string icon = b.ElementAtOrDefault(2) as string ?? "";
                string summary = b.ElementAtOrDefault(3) as string ?? "";
                string body = Plain(b.ElementAtOrDefault(4) as string ?? "");
                var actionList = (b.ElementAtOrDefault(5) as List<object>)?.OfType<string>().ToList() ?? new List<string>();
                var actions = new List<(string, string)>();
                for (int i = 0; i + 1 < actionList.Count; i += 2) actions.Add((actionList[i], actionList[i + 1]));
                var hints = b.ElementAtOrDefault(6) as Dictionary<object, object> ?? new Dictionary<object, object>();
                object? Hint(string k) => hints.TryGetValue(k, out var v) ? (v is Variant vv ? vv.Value : v) : null;
                byte urgency = Hint("urgency") is byte u ? u : (byte)1;
                bool transient = Hint("transient") is bool t && t;
                string? entry = Hint("desktop-entry") as string;
                uint id = replaces != 0 ? replaces : (uint)Interlocked.Increment(ref _next);
                lock (_owners) _owners[id] = m.Sender ?? "";
                _bus.Reply(m, "u", id);
                Received?.Invoke(new Incoming(id, app, icon, summary, body, actions, urgency, entry, transient, replaces));
                return true;
            }
            case "CloseNotification" when m.Interface is Iface or null:
            {
                uint id = m.Body.ElementAtOrDefault(0) is uint i ? i : 0;
                _bus.Reply(m);
                CloseRequested?.Invoke(id);
                Close(id, ClosedByCall);
                return true;
            }
            case "GetCapabilities" when m.Interface is Iface or null:
                // no "body-markup": programs then send plain text, which is what we show
                _bus.Reply(m, "as", new List<object> { "actions", "body", "persistence" });
                return true;
            case "GetServerInformation" when m.Interface is Iface or null:
                _bus.Reply(m, "ssss", "Crimson Banner", "Crimson Banner OS", UpdateChecker.Current.ToString(3), "1.2");
                return true;
            case "Introspect" when m.Interface is "org.freedesktop.DBus.Introspectable" or null:
                _bus.Reply(m, "s", Introspection);
                return true;
            case "Ping" when m.Interface == "org.freedesktop.DBus.Peer":
                _bus.Reply(m);
                return true;
        }
        return false;
    }

    private const string Introspection =
        "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\" \"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">\n" +
        "<node>\n" +
        "  <interface name=\"org.freedesktop.Notifications\">\n" +
        "    <method name=\"Notify\"><arg type=\"s\" direction=\"in\"/><arg type=\"u\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/>" +
        "<arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"as\" direction=\"in\"/><arg type=\"a{sv}\" direction=\"in\"/>" +
        "<arg type=\"i\" direction=\"in\"/><arg type=\"u\" direction=\"out\"/></method>\n" +
        "    <method name=\"CloseNotification\"><arg type=\"u\" direction=\"in\"/></method>\n" +
        "    <method name=\"GetCapabilities\"><arg type=\"as\" direction=\"out\"/></method>\n" +
        "    <method name=\"GetServerInformation\"><arg type=\"s\" direction=\"out\"/><arg type=\"s\" direction=\"out\"/><arg type=\"s\" direction=\"out\"/><arg type=\"s\" direction=\"out\"/></method>\n" +
        "    <signal name=\"NotificationClosed\"><arg type=\"u\"/><arg type=\"u\"/></signal>\n" +
        "    <signal name=\"ActionInvoked\"><arg type=\"u\"/><arg type=\"s\"/></signal>\n" +
        "  </interface>\n" +
        "  <interface name=\"org.freedesktop.DBus.Introspectable\"><method name=\"Introspect\"><arg type=\"s\" direction=\"out\"/></method></interface>\n" +
        "</node>\n";
}

/// <summary>Starts the notifications service and shows what arrives.</summary>
public static class OtherAppsNotifications
{
    private static NotificationServer? _server;
    private static readonly Dictionary<uint, DateTime> _shown = new();

    public static bool Running => _server?.Owner == true;

    public static async void Start()
    {
        if (_server != null || DBusConnection.SessionAddress() == null) return;
        try
        {
            var bus = await DBusConnection.ConnectAsync();
            var server = new NotificationServer(bus);
            server.Received += Show;
            bus.Closed += () => _server = null;
            _server = server;
            if (await server.StartAsync()) Console.Error.WriteLine("notifications: showing other programs' notifications");
            else Console.Error.WriteLine("notifications: another desktop shows other programs' notifications");
        }
        catch (Exception ex)
        {
            _server = null;
            Console.Error.WriteLine("notifications: unavailable: " + ex.Message);
        }
    }

    /// <summary>Title and text for the toast: the summary, and the body (the program's name when there's no summary).</summary>
    public static (string Title, string Text) Describe(NotificationServer.Incoming n)
    {
        var app = n.App.Length > 0 ? n.App : n.DesktopEntry ?? "";
        if (n.Summary.Length == 0) return (app.Length > 0 ? app : "Notification", n.Body);
        return (n.Summary, n.Body.Length > 0 ? n.Body : app);
    }

    private static void Show(NotificationServer.Incoming n)
    {
        var server = _server;
        if (server == null) return;
        // a program updating its notification (a download's progress…) quickly: no new toast each time
        lock (_shown)
        {
            if (n.Replaces != 0 && _shown.TryGetValue(n.Replaces, out var when) && (DateTime.Now - when).TotalSeconds < 5) return;
            _shown[n.Id] = DateTime.Now;
            if (_shown.Count > 200) foreach (var k in _shown.OrderBy(k => k.Value).Take(100).Select(k => k.Key).ToList()) _shown.Remove(k);
        }
        var (title, text) = Describe(n);
        var buttons = n.Actions.Where(a => a.Key != "default" && a.Label.Length > 0).Take(3)
            .Select(a => new NotificationAction(a.Label, () => server.Invoke(n.Id, a.Key))).ToList();
        if (n.Actions.Any(a => a.Key == "default") && buttons.Count == 0)
            buttons.Add(new NotificationAction("Open", () => server.Invoke(n.Id, "default")));
        Notifier.Show(title, text, null, buttons.ToArray());
    }
}
