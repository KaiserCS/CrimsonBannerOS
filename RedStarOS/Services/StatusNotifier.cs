// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RedStarOS.Services.DBus;

namespace RedStarOS.Services;

/// <summary>An icon another program put in the tray (StatusNotifierItem).</summary>
public sealed class SniItem
{
    /// <summary>As listed by the watcher: bus name and object path, "org.kde.StatusNotifierItem-12-1/StatusNotifierItem".</summary>
    public string Key { get; init; } = "";
    public string Service { get; init; } = "";
    public string Path { get; init; } = "/StatusNotifierItem";
    /// <summary>The unique bus name (":1.42") of the program.</summary>
    public string Owner { get; set; } = "";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Passive (hidden), Active or NeedsAttention.</summary>
    public string Status { get; set; } = "Active";
    public string IconName { get; set; } = "";
    public string AttentionIconName { get; set; } = "";
    public string IconThemePath { get; set; } = "";
    public SniPixmap? Pixmap { get; set; }
    public SniPixmap? AttentionPixmap { get; set; }
    public string ToolTipTitle { get; set; } = "";
    public string ToolTipText { get; set; } = "";
    public string MenuPath { get; set; } = "";
    /// <summary>The program only has a menu: a left click opens it too (libappindicator programs such as Steam).</summary>
    public bool ItemIsMenu { get; set; }

    public bool NeedsAttention => Status == "NeedsAttention";
    public bool Hidden => Status == "Passive";

    /// <summary>What the tooltip says: the program's tooltip, else its title, else its id.</summary>
    public string Tooltip
    {
        get
        {
            var t = ToolTipTitle.Length > 0 ? ToolTipTitle : Title.Length > 0 ? Title : Id;
            return ToolTipText.Length > 0 ? t + "\n" + ToolTipText : t;
        }
    }
}

/// <summary>An icon as pixels: ARGB32, network byte order (A, R, G, B per pixel), as StatusNotifierItem sends it.</summary>
public sealed record SniPixmap(int Width, int Height, byte[] Argb);

/// <summary>One entry of a program's tray menu (DBusMenu).</summary>
public sealed class SniMenuItem
{
    public int Id { get; init; }
    public string Label { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool Separator { get; init; }
    /// <summary>"checkmark" or "radio" with <see cref="Checked"/>, else "".</summary>
    public string Toggle { get; init; } = "";
    public bool Checked { get; init; }
    public List<SniMenuItem> Children { get; init; } = new();
}

/// <summary>
/// The system tray's D-Bus side (freedesktop StatusNotifierItem, as KDE defined it, plus DBusMenu for the menus):
/// Discord, Steam, Qt programs, update notifiers… register their icons with a "watcher" and the desktop's tray
/// ("host") shows them. Red Star is the watcher when no one else is (its own session), and otherwise registers as one
/// more host with the watcher that's running (on KDE). Specs: freedesktop.org StatusNotifierItem, KDE's
/// org.kde.StatusNotifierWatcher, Canonical's com.canonical.dbusmenu.
/// </summary>
public sealed class StatusNotifierHost
{
    public const string WatcherName = "org.kde.StatusNotifierWatcher", WatcherPath = "/StatusNotifierWatcher";
    public const string WatcherIface = "org.kde.StatusNotifierWatcher", ItemIface = "org.kde.StatusNotifierItem";
    public const string MenuIface = "com.canonical.dbusmenu";
    private const string Props = "org.freedesktop.DBus.Properties";

    private readonly DBusConnection _bus;
    private readonly object _lock = new();
    private readonly Dictionary<string, SniItem> _items = new();
    private readonly List<string> _registered = new();       // watcher: what RegisteredStatusNotifierItems lists
    private string _hostName = "";
    private static int _hosts;

    /// <summary>True when Red Star is the watcher; false when it joined another desktop's.</summary>
    public bool IsWatcher { get; private set; }

    /// <summary>Raised (on a background thread) when an icon appears, changes or goes; null key: everything.</summary>
    public event Action<SniItem?>? Changed;
    public event Action<string>? Removed;

    public StatusNotifierHost(DBusConnection bus) { _bus = bus; }

    public IReadOnlyList<SniItem> Items { get { lock (_lock) return _items.Values.ToList(); } }

    public async Task StartAsync()
    {
        _bus.MethodCall = OnMethodCall;
        _bus.Signal += OnSignal;
        await _bus.AddMatchAsync("type='signal',sender='org.freedesktop.DBus',interface='org.freedesktop.DBus',member='NameOwnerChanged'");
        await _bus.AddMatchAsync($"type='signal',interface='{ItemIface}'");

        _hostName = $"org.kde.StatusNotifierHost-{Environment.ProcessId}-{System.Threading.Interlocked.Increment(ref _hosts)}";
        await _bus.RequestNameAsync(_hostName);

        uint got = await _bus.RequestNameAsync(WatcherName);
        IsWatcher = got is 1 or 4;
        if (IsWatcher)
        {
            _bus.EmitSignal(WatcherPath, WatcherIface, "StatusNotifierHostRegistered");
            return;
        }
        // another desktop's watcher (KDE): join it as a host and show what it already has
        await _bus.AddMatchAsync($"type='signal',sender='{WatcherName}',interface='{WatcherIface}'");
        try
        {
            await _bus.CallAsync(WatcherName, WatcherPath, WatcherIface, "RegisterStatusNotifierHost", "s", new object[] { _hostName });
            if (await _bus.GetPropertyAsync(WatcherName, WatcherPath, WatcherIface, "RegisteredStatusNotifierItems") is List<object> list)
                foreach (var k in list.OfType<string>()) _ = AddAsync(k, null);
        }
        catch (DBusException ex) { Console.Error.WriteLine("tray: the running watcher refused us: " + ex.Message); }
    }

    /// <summary>"service/path" as the watcher lists it, from what a program registered: a bus name ("…-12-1"),
    /// or only an object path ("/org/ayatana/NotificationItem/steam"), which belongs to whoever sent it.</summary>
    public static (string Service, string Path) Split(string registered, string? sender)
    {
        if (registered.StartsWith('/')) return (sender ?? "", registered);
        int slash = registered.IndexOf('/');
        return slash < 0 ? (registered, "/StatusNotifierItem") : (registered[..slash], registered[slash..]);
    }

    // ── the watcher (when Red Star is it) ──

    private bool OnMethodCall(Message m)
    {
        if (!IsWatcher || m.Path != WatcherPath) return false;
        bool watcherIface = m.Interface is WatcherIface or "org.freedesktop.StatusNotifierWatcher" or null;
        switch (m.Member)
        {
            case "RegisterStatusNotifierItem" when watcherIface:
            {
                var registered = m.FirstString ?? "";
                var (service, path) = Split(registered, m.Sender);
                if (service.Length == 0) { _bus.ReplyError(m, "org.freedesktop.DBus.Error.InvalidArgs", "no service"); return true; }
                _bus.Reply(m);
                // by object path: the icon belongs to whoever registered it; by name: to whoever owns that name
                _ = AddAsync(service + path, registered.StartsWith('/') ? m.Sender : null);
                return true;
            }
            case "RegisterStatusNotifierHost" when watcherIface:
                _bus.Reply(m);
                _bus.EmitSignal(WatcherPath, WatcherIface, "StatusNotifierHostRegistered");
                return true;
            case "Get" when m.Interface == Props:
            {
                var props = WatcherProperties();
                var name = m.Body.Length > 1 ? m.Body[1] as string : null;
                if (name != null && props.TryGetValue(name, out var v)) _bus.Reply(m, "v", v);
                else _bus.ReplyError(m, "org.freedesktop.DBus.Error.UnknownProperty", "no property " + name);
                return true;
            }
            case "GetAll" when m.Interface == Props:
                _bus.Reply(m, "a{sv}", WatcherProperties().ToDictionary(p => p.Key, p => (object)p.Value));
                return true;
            case "Set" when m.Interface == Props:
                _bus.ReplyError(m, "org.freedesktop.DBus.Error.PropertyReadOnly", "read-only");
                return true;
            case "Introspect" when m.Interface is "org.freedesktop.DBus.Introspectable" or null:
                _bus.Reply(m, "s", WatcherIntrospection);
                return true;
        }
        return false;
    }

    private Dictionary<string, Variant> WatcherProperties()
    {
        string[] items;
        lock (_lock) items = _registered.ToArray();
        return new()
        {
            ["RegisteredStatusNotifierItems"] = new Variant("as", items),
            ["IsStatusNotifierHostRegistered"] = new Variant("b", true),
            ["ProtocolVersion"] = new Variant("i", 0),
        };
    }

    public const string WatcherIntrospection =
        "<!DOCTYPE node PUBLIC \"-//freedesktop//DTD D-BUS Object Introspection 1.0//EN\" \"http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd\">\n" +
        "<node>\n" +
        "  <interface name=\"org.kde.StatusNotifierWatcher\">\n" +
        "    <method name=\"RegisterStatusNotifierItem\"><arg name=\"service\" type=\"s\" direction=\"in\"/></method>\n" +
        "    <method name=\"RegisterStatusNotifierHost\"><arg name=\"service\" type=\"s\" direction=\"in\"/></method>\n" +
        "    <property name=\"RegisteredStatusNotifierItems\" type=\"as\" access=\"read\"/>\n" +
        "    <property name=\"IsStatusNotifierHostRegistered\" type=\"b\" access=\"read\"/>\n" +
        "    <property name=\"ProtocolVersion\" type=\"i\" access=\"read\"/>\n" +
        "    <signal name=\"StatusNotifierItemRegistered\"><arg type=\"s\"/></signal>\n" +
        "    <signal name=\"StatusNotifierItemUnregistered\"><arg type=\"s\"/></signal>\n" +
        "    <signal name=\"StatusNotifierHostRegistered\"/>\n" +
        "    <signal name=\"StatusNotifierHostUnregistered\"/>\n" +
        "  </interface>\n" +
        "  <interface name=\"org.freedesktop.DBus.Properties\">\n" +
        "    <method name=\"Get\"><arg type=\"s\" direction=\"in\"/><arg type=\"s\" direction=\"in\"/><arg type=\"v\" direction=\"out\"/></method>\n" +
        "    <method name=\"GetAll\"><arg type=\"s\" direction=\"in\"/><arg type=\"a{sv}\" direction=\"out\"/></method>\n" +
        "  </interface>\n" +
        "  <interface name=\"org.freedesktop.DBus.Introspectable\">\n" +
        "    <method name=\"Introspect\"><arg type=\"s\" direction=\"out\"/></method>\n" +
        "  </interface>\n" +
        "</node>\n";

    // ── items ──

    private async Task AddAsync(string key, string? owner)
    {
        try { await AddCoreAsync(key, owner); }
        catch (Exception ex) { Console.Error.WriteLine($"tray: {key}: {ex.Message}"); }
    }

    private async Task AddCoreAsync(string key, string? owner)
    {
        var (service, path) = Split(key, owner);
        owner ??= service.StartsWith(':') ? service : await _bus.GetNameOwnerAsync(service);
        if (string.IsNullOrEmpty(owner)) return;
        SniItem item;
        lock (_lock)
        {
            if (_items.ContainsKey(key)) return;
            item = new SniItem { Key = key, Service = service, Path = path, Owner = owner };
            _items[key] = item;
            if (IsWatcher && !_registered.Contains(key)) _registered.Add(key);
        }
        if (IsWatcher) _bus.EmitSignal(WatcherPath, WatcherIface, "StatusNotifierItemRegistered", "s", key);
        await RefreshAsync(item);
    }

    private void Remove(string key)
    {
        lock (_lock)
        {
            if (!_items.Remove(key)) return;
            _registered.Remove(key);
        }
        if (IsWatcher) _bus.EmitSignal(WatcherPath, WatcherIface, "StatusNotifierItemUnregistered", "s", key);
        Removed?.Invoke(key);
    }

    /// <summary>Reads an icon's properties again (after it said it changed).</summary>
    public async Task RefreshAsync(SniItem item)
    {
        Dictionary<string, object> p;
        try { p = await _bus.GetAllPropertiesAsync(item.Owner, item.Path, ItemIface); }
        catch (DBusException ex)
        {
            Console.Error.WriteLine($"tray: {item.Key}: {ex.Message}");
            if (ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.UnknownObject") Remove(item.Key);
            return;
        }
        try { Apply(item, p); }
        catch (Exception ex) { Console.Error.WriteLine($"tray: {item.Key}: odd properties: {ex.Message}"); return; }
        Changed?.Invoke(item);
    }

    /// <summary>Copies StatusNotifierItem properties onto an item.</summary>
    public static void Apply(SniItem item, IReadOnlyDictionary<string, object> p)
    {
        string S(string k) => p.TryGetValue(k, out var v) ? v switch { string s => s, ObjectPath o => o.Value, _ => "" } : "";
        item.Id = S("Id");
        item.Title = S("Title");
        item.Status = S("Status") is { Length: > 0 } st ? st : "Active";
        item.IconName = S("IconName");
        item.AttentionIconName = S("AttentionIconName");
        item.IconThemePath = S("IconThemePath");
        item.MenuPath = S("Menu") is "/NO_DBUSMENU" ? "" : S("Menu");
        item.ItemIsMenu = p.TryGetValue("ItemIsMenu", out var im) && im is true;
        item.Pixmap = p.TryGetValue("IconPixmap", out var px) ? BestPixmap(px) : null;
        item.AttentionPixmap = p.TryGetValue("AttentionIconPixmap", out var apx) ? BestPixmap(apx) : null;
        item.ToolTipTitle = item.ToolTipText = "";
        // ToolTip (sa(iiay)ss): icon name, icon pixmaps, title, text (which may have a little HTML)
        if (p.TryGetValue("ToolTip", out var tt) && tt is object[] { Length: 4 } t)
        {
            item.ToolTipTitle = PlainText(t[2] as string ?? "");
            item.ToolTipText = PlainText(t[3] as string ?? "");
        }
    }

    /// <summary>The pixmap closest to tray size (prefers the smallest one at least 22 px).</summary>
    public static SniPixmap? BestPixmap(object value, int want = 22)
    {
        if (value is not List<object> list) return null;
        var all = list.OfType<object[]>()
            .Where(a => a.Length == 3 && a[0] is int && a[1] is int && a[2] is byte[])
            .Select(a => new SniPixmap((int)a[0], (int)a[1], (byte[])a[2]))
            .Where(x => x.Width > 0 && x.Height > 0 && x.Argb.Length >= x.Width * x.Height * 4)
            .ToList();
        if (all.Count == 0) return null;
        return all.Where(x => x.Width >= want).OrderBy(x => x.Width).FirstOrDefault() ?? all.OrderByDescending(x => x.Width).First();
    }

    public static string PlainText(string s) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(s.Replace("<br>", "\n").Replace("<br/>", "\n"), "<[^>]+>", "")).Trim();

    private void OnSignal(Message m)
    {
        if (m.Interface == "org.freedesktop.DBus" && m.Member == "NameOwnerChanged" && m.Body.Length == 3)
        {
            // a program left the bus: its icons go
            var name = m.Body[0] as string ?? ""; var now = m.Body[2] as string ?? "";
            if (now.Length > 0) return;
            List<string> gone;
            lock (_lock) gone = _items.Values.Where(i => i.Owner == name || i.Service == name).Select(i => i.Key).ToList();
            foreach (var k in gone) Remove(k);
            return;
        }
        if (m.Interface == ItemIface && m.Member is "NewIcon" or "NewAttentionIcon" or "NewOverlayIcon" or "NewTitle"
            or "NewToolTip" or "NewStatus" or "NewMenu" or "NewIconThemePath")
        {
            List<SniItem> hit;
            lock (_lock) hit = _items.Values.Where(i => i.Owner == m.Sender && (m.Path == null || i.Path == m.Path)).ToList();
            foreach (var i in hit) _ = RefreshAsync(i);
            return;
        }
        if (!IsWatcher && m.Interface == WatcherIface)
        {
            if (m.Member == "StatusNotifierItemRegistered" && m.FirstString is { } k) _ = AddAsync(k, null);
            else if (m.Member == "StatusNotifierItemUnregistered" && m.FirstString is { } k2) Remove(k2);
        }
    }

    // ── what clicks do ──

    /// <summary>Left click (Activate). False when the program has no such action (it then wants its menu).</summary>
    public async Task<bool> ActivateAsync(SniItem item, int x = 0, int y = 0)
    {
        try { await _bus.CallAsync(item.Owner, item.Path, ItemIface, "Activate", "ii", new object[] { x, y }); return true; }
        catch (DBusException ex) { return ex.ErrorName != "org.freedesktop.DBus.Error.UnknownMethod"; }
    }

    public Task SecondaryActivateAsync(SniItem item, int x = 0, int y = 0) =>
        Quiet(_bus.CallAsync(item.Owner, item.Path, ItemIface, "SecondaryActivate", "ii", new object[] { x, y }));

    /// <summary>Right click on a program without a DBusMenu: it shows its own menu.</summary>
    public Task ContextMenuAsync(SniItem item, int x = 0, int y = 0) =>
        Quiet(_bus.CallAsync(item.Owner, item.Path, ItemIface, "ContextMenu", "ii", new object[] { x, y }));

    public Task ScrollAsync(SniItem item, int delta, bool horizontal = false) =>
        Quiet(_bus.CallAsync(item.Owner, item.Path, ItemIface, "Scroll", "is", new object[] { delta, horizontal ? "horizontal" : "vertical" }));

    private static async Task Quiet(Task t) { try { await t; } catch (DBusException) { } }

    // ── menus (DBusMenu) ──

    /// <summary>The program's menu as it is now, or null without one.</summary>
    public async Task<List<SniMenuItem>?> GetMenuAsync(SniItem item)
    {
        if (item.MenuPath.Length == 0) return null;
        try { await _bus.CallAsync(item.Owner, item.MenuPath, MenuIface, "AboutToShow", "i", new object[] { 0 }, 1000); }
        catch (DBusException) { }                              // optional
        try
        {
            var r = await _bus.CallAsync(item.Owner, item.MenuPath, MenuIface, "GetLayout", "iias", new object[] { 0, -1, Array.Empty<string>() });
            return r.Body.Length > 1 && r.Body[1] is object[] root ? ParseLayout(root).Children : null;
        }
        catch (DBusException ex) { Console.Error.WriteLine($"tray: {item.Key} menu: {ex.Message}"); return null; }
    }

    /// <summary>A GetLayout node, (ia{sv}av): id, properties, children (each a variant holding a node).</summary>
    public static SniMenuItem ParseLayout(object[] node)
    {
        var props = node.Length > 1 && node[1] is Dictionary<object, object> d
            ? d.ToDictionary(kv => (string)kv.Key, kv => kv.Value is Variant v ? v.Value : kv.Value)
            : new Dictionary<string, object>();
        var children = new List<SniMenuItem>();
        if (node.Length > 2 && node[2] is List<object> kids)
            foreach (var k in kids)
                if ((k is Variant v ? v.Value : k) is object[] child)
                {
                    var c = ParseLayout(child);
                    if (c.Label.Length > 0 || c.Separator || c.Children.Count > 0) children.Add(c);
                }
        bool visible = !props.TryGetValue("visible", out var vis) || vis is not false;
        return new SniMenuItem
        {
            Id = node.Length > 0 && node[0] is int id ? id : 0,
            Label = visible ? Mnemonic(props.TryGetValue("label", out var l) ? l as string ?? "" : "") : "",
            Enabled = !props.TryGetValue("enabled", out var en) || en is not false,
            Separator = visible && props.TryGetValue("type", out var ty) && ty as string == "separator",
            Toggle = props.TryGetValue("toggle-type", out var tg) ? tg as string ?? "" : "",
            Checked = props.TryGetValue("toggle-state", out var ts) && ts is 1,
            Children = visible ? children : new(),
        };
    }

    /// <summary>"_Quit" → "Quit", "Save && Exit" style doubles kept: "__" is a literal underscore.</summary>
    public static string Mnemonic(string label) => label.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_");

    /// <summary>A menu entry was clicked.</summary>
    public Task MenuClickedAsync(SniItem item, int id) =>
        Quiet(_bus.CallAsync(item.Owner, item.MenuPath, MenuIface, "Event", "isvu",
            new object[] { id, "clicked", new Variant("i", 0), (uint)Environment.TickCount }));
}
