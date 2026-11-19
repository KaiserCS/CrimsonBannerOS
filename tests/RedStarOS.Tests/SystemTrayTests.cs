// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using RedStarOS.Services;
using RedStarOS.Services.DBus;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Red Star's D-Bus client (wire format) and the system tray (StatusNotifierItem watcher and host, DBusMenu),
/// against a private dbus-daemon with a fake tray program, and GLib's gdbus to check we speak the same format.</summary>
public class SystemTrayTests
{
    // ── wire format ──

    [Fact]
    public void A_method_call_encodes_as_the_spec_says()
    {
        var b = Wire.Encode(new Message { Type = MessageType.MethodCall, Path = "/a", Member = "B", Serial = 1 });
        // fixed header: little endian, method call, no flags, version 1, empty body, serial 1
        Assert.Equal(new byte[] { (byte)'l', 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0 }, b[..12]);
        Assert.Equal(26, b[12]);                                  // header fields: 26 bytes
        Assert.Equal(new byte[] { 1, 1, (byte)'o', 0, 2, 0, 0, 0, (byte)'/', (byte)'a', 0 }, b[16..27]);   // PATH, "o", "/a"
        Assert.Equal(new byte[] { 3, 1, (byte)'s', 0, 1, 0, 0, 0, (byte)'B', 0 }, b[32..42]);               // MEMBER, "s", "B"
        Assert.Equal(48, b.Length);                               // padded to 8
    }

    [Fact]
    public void Messages_survive_a_round_trip()
    {
        var layout = new object[]
        {
            0, new Dictionary<string, object> { ["children-display"] = new Variant("s", "submenu") },
            new List<object> { new Variant("(ia{sv}av)", new object[] { 7, new Dictionary<string, object> { ["label"] = new Variant("s", "_Quit") }, new List<object>() }) },
        };
        var m = new Message
        {
            Type = MessageType.MethodReturn, ReplySerial = 9, Destination = ":1.5", Serial = 3,
            Signature = "u(ia{sv}av)aysxdbo",
            Body = new object[] { 12u, layout, new byte[] { 1, 2, 3 }, "héllo", -5L, 2.5, true, new ObjectPath("/Menu") },
        };
        var back = Wire.Decode(Wire.Encode(m));
        Assert.Equal(MessageType.MethodReturn, back.Type);
        Assert.Equal(9u, back.ReplySerial);
        Assert.Equal(":1.5", back.Destination);
        Assert.Equal("u(ia{sv}av)aysxdbo", back.Signature);
        Assert.Equal(12u, back.Body[0]);
        var menu = StatusNotifierHost.ParseLayout((object[])back.Body[1]);
        Assert.Equal("Quit", Assert.Single(menu.Children).Label);
        Assert.Equal(7, menu.Children[0].Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Body[2]);
        Assert.Equal("héllo", back.Body[3]);
        Assert.Equal(-5L, back.Body[4]);
        Assert.Equal(2.5, back.Body[5]);
        Assert.Equal(true, back.Body[6]);
        Assert.Equal(new ObjectPath("/Menu"), back.Body[7]);
    }

    [Theory]
    [InlineData("unix:path=/run/user/1000/bus", "/run/user/1000/bus")]
    [InlineData("unix:abstract=/tmp/dbus-AbC,guid=12ab", "\0/tmp/dbus-AbC")]
    [InlineData("tcp:host=x;unix:path=/tmp/a%20b", "/tmp/a b")]
    public void Bus_addresses(string address, string socket) => Assert.Equal(socket, DBusConnection.SocketPath(address));

    // ── what programs send ──

    [Theory]
    [InlineData("org.kde.StatusNotifierItem-12-1", null, "org.kde.StatusNotifierItem-12-1", "/StatusNotifierItem")]
    [InlineData("/org/ayatana/NotificationItem/steam", ":1.42", ":1.42", "/org/ayatana/NotificationItem/steam")]
    [InlineData(":1.42/org/ayatana/NotificationItem/steam", null, ":1.42", "/org/ayatana/NotificationItem/steam")]
    public void Registrations_by_name_or_by_path(string registered, string? sender, string service, string path) =>
        Assert.Equal((service, path), StatusNotifierHost.Split(registered, sender));

    [Fact]
    public void Item_properties_tooltip_and_pixmaps()
    {
        var item = new SniItem();
        StatusNotifierHost.Apply(item, new Dictionary<string, object>
        {
            ["Id"] = "steam", ["Title"] = "Steam", ["Status"] = "NeedsAttention", ["IconName"] = "steam_tray_mono",
            ["Menu"] = new ObjectPath("/MenuBar"), ["ItemIsMenu"] = true,
            ["ToolTip"] = new object[] { "", new List<object>(), "Steam", "<b>2</b> downloads&amp;updates" },
            ["IconPixmap"] = new List<object> { new object[] { 16, 16, new byte[16 * 16 * 4] }, new object[] { 32, 32, new byte[32 * 32 * 4] }, new object[] { 24, 24, new byte[24 * 24 * 4] } },
        });
        Assert.True(item.NeedsAttention);
        Assert.True(item.ItemIsMenu);
        Assert.Equal("/MenuBar", item.MenuPath);
        Assert.Equal("Steam\n2 downloads&updates", item.Tooltip);
        Assert.Equal(24, item.Pixmap!.Width);                     // the smallest at least tray size
    }

    [Fact]
    public void Pixels_become_premultiplied_BGRA()
    {
        // one half-transparent pure red pixel: A=128 R=255 G=0 B=0
        var bgra = SystemTray.ToBgraPremultiplied(new SniPixmap(1, 1, new byte[] { 128, 255, 0, 0 }));
        Assert.Equal(new byte[] { 0, 0, 128, 128 }, bgra);
    }

    [Fact]
    public void Menus_strip_mnemonics_and_hide_invisible_entries()
    {
        Assert.Equal("Quit", StatusNotifierHost.Mnemonic("_Quit"));
        Assert.Equal("snake_case", StatusNotifierHost.Mnemonic("snake__case"));
        var root = StatusNotifierHost.ParseLayout(new object[]
        {
            0, new Dictionary<object, object>(), new List<object>
            {
                new Variant("(ia{sv}av)", new object[] { 1, new Dictionary<object, object> { ["label"] = new Variant("s", "Hidden"), ["visible"] = new Variant("b", false) }, new List<object>() }),
                new Variant("(ia{sv}av)", new object[] { 2, new Dictionary<object, object> { ["label"] = new Variant("s", "Mute"), ["toggle-type"] = new Variant("s", "checkmark"), ["toggle-state"] = new Variant("i", 1), ["enabled"] = new Variant("b", false) }, new List<object>() }),
            },
        });
        var only = Assert.Single(root.Children);
        Assert.Equal("Mute", only.Label);
        Assert.Equal("checkmark", only.Toggle);
        Assert.True(only.Checked);
        Assert.False(only.Enabled);
    }

    // ── on a real bus ──

    [Fact]
    public async Task Tray_icons_appear_change_open_menus_and_go()
    {
        using var bus = TestBus.Start();
        using var shell = await DBusConnection.ConnectAsync(bus.Address);
        var host = new StatusNotifierHost(shell);
        var removed = new TaskCompletionSource<string>();
        host.Removed += k => removed.TrySetResult(k);
        await host.StartAsync();
        Assert.True(host.IsWatcher);                              // nobody else is: Red Star is the watcher

        var appBus = await DBusConnection.ConnectAsync(bus.Address);
        var app = await FakeTrayProgram.StartAsync(appBus, "org.kde.StatusNotifierItem-7-1");
        await appBus.CallAsync(StatusNotifierHost.WatcherName, StatusNotifierHost.WatcherPath, StatusNotifierHost.WatcherIface,
            "RegisterStatusNotifierItem", "s", new object[] { "org.kde.StatusNotifierItem-7-1" });

        var item = await WaitFor(() => host.Items.FirstOrDefault(i => i.Title == "Chat"));
        Assert.Equal("org.kde.StatusNotifierItem-7-1/StatusNotifierItem", item.Key);
        Assert.Equal("chat", item.IconName);
        Assert.Equal("Chat\n3 unread", item.Tooltip);
        Assert.Equal(2, item.Pixmap!.Width);

        // the watcher lists it for anyone who asks
        var listed = await appBus.GetPropertyAsync(StatusNotifierHost.WatcherName, StatusNotifierHost.WatcherPath,
            StatusNotifierHost.WatcherIface, "RegisteredStatusNotifierItems");
        Assert.Contains(item.Key, Assert.IsType<List<object>>(listed).Cast<string>());

        // clicks
        Assert.True(await host.ActivateAsync(item));
        await host.ScrollAsync(item, -1);
        await WaitFor(() => app.Calls.Contains("Activate") && app.Calls.Contains("Scroll -1 vertical") ? "ok" : null);

        // its menu, as it is when opened; and a click on an entry
        var menu = await host.GetMenuAsync(item);
        Assert.NotNull(menu);
        Assert.Equal(new[] { "Open", "", "Status", "Quit" }, menu!.Select(m => m.Label));
        Assert.True(menu[1].Separator);
        var busy = Assert.Single(menu[2].Children);
        Assert.Equal(("Busy", "radio", true), (busy.Label, busy.Toggle, busy.Checked));
        await host.MenuClickedAsync(item, 3);
        await WaitFor(() => app.Calls.Contains("Event 3 clicked") ? "ok" : null);

        // it changes its title and says so
        app.Title = "Chat (4)";
        appBus.EmitSignal("/StatusNotifierItem", StatusNotifierHost.ItemIface, "NewTitle");
        await WaitFor(() => host.Items.FirstOrDefault(i => i.Title == "Chat (4)"));

        // the program quits: its icon goes
        appBus.Dispose();
        Assert.Equal(item.Key, await removed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Empty(host.Items);
    }

    [Fact]
    public async Task On_KDE_Red_Star_joins_the_running_watcher()
    {
        using var bus = TestBus.Start();
        using var kdeBus = await DBusConnection.ConnectAsync(bus.Address);
        var kde = new StatusNotifierHost(kdeBus);                 // stands in for KDE's watcher
        await kde.StartAsync();
        Assert.True(kde.IsWatcher);

        using var appBus = await DBusConnection.ConnectAsync(bus.Address);
        await FakeTrayProgram.StartAsync(appBus, "org.kde.StatusNotifierItem-8-1");
        await appBus.CallAsync(StatusNotifierHost.WatcherName, StatusNotifierHost.WatcherPath, StatusNotifierHost.WatcherIface,
            "RegisterStatusNotifierItem", "s", new object[] { "org.kde.StatusNotifierItem-8-1" });
        await WaitFor(() => kde.Items.FirstOrDefault());

        using var shellBus = await DBusConnection.ConnectAsync(bus.Address);
        var shell = new StatusNotifierHost(shellBus);
        await shell.StartAsync();
        Assert.False(shell.IsWatcher);
        await WaitFor(() => shell.Items.FirstOrDefault(i => i.Title == "Chat"));   // what was there already

        // a program that starts later: the watcher tells us
        using var app2Bus = await DBusConnection.ConnectAsync(bus.Address);
        await FakeTrayProgram.StartAsync(app2Bus, "org.kde.StatusNotifierItem-9-1", title: "Updates");
        await app2Bus.CallAsync(StatusNotifierHost.WatcherName, StatusNotifierHost.WatcherPath, StatusNotifierHost.WatcherIface,
            "RegisterStatusNotifierItem", "s", new object[] { "org.kde.StatusNotifierItem-9-1" });
        await WaitFor(() => shell.Items.FirstOrDefault(i => i.Title == "Updates"));
    }

    [Fact]
    public async Task GLib_programs_understand_our_watcher()
    {
        using var bus = TestBus.Start();
        var gdbus = ProcessUtil.Which("gdbus");
        if (gdbus == null) Assert.Skip("gdbus (GLib) isn't installed");
        using var shellBus = await DBusConnection.ConnectAsync(bus.Address);
        var host = new StatusNotifierHost(shellBus);
        await host.StartAsync();
        using var appBus = await DBusConnection.ConnectAsync(bus.Address);
        await FakeTrayProgram.StartAsync(appBus, "org.kde.StatusNotifierItem-5-1");

        // GLib registers the item (its marshalling, read by ours) and reads the list back (ours, read by GLib)
        string Gdbus(params string[] args)
        {
            var psi = new ProcessStartInfo(gdbus!) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "call", "--address", bus.Address, "--dest", StatusNotifierHost.WatcherName,
                         "--object-path", StatusNotifierHost.WatcherPath, "--method" }.Concat(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(10000), "gdbus didn't finish");
            return output;
        }
        Gdbus("org.kde.StatusNotifierWatcher.RegisterStatusNotifierItem", "org.kde.StatusNotifierItem-5-1");
        await WaitFor(() => host.Items.FirstOrDefault(i => i.Title == "Chat"));
        var list = Gdbus("org.freedesktop.DBus.Properties.Get", StatusNotifierHost.WatcherIface, "RegisteredStatusNotifierItems");
        Assert.Contains("org.kde.StatusNotifierItem-5-1/StatusNotifierItem", list);
        var registered = Gdbus("org.freedesktop.DBus.Properties.Get", StatusNotifierHost.WatcherIface, "IsStatusNotifierHostRegistered");
        Assert.Contains("true", registered);
        Assert.Contains("RegisterStatusNotifierItem", Gdbus("org.freedesktop.DBus.Introspectable.Introspect"));
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe, int ms = 5000) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (probe() is { } v) return v;
            await Task.Delay(25);
        }
        throw new TimeoutException("waited " + ms + " ms");
    }

    /// <summary>A program with a tray icon and a menu, like Discord or Steam, on its own bus connection.</summary>
    private sealed class FakeTrayProgram
    {
        public string Title { get; set; } = "Chat";
        public List<string> Calls { get; } = new();

        public static async Task<FakeTrayProgram> StartAsync(DBusConnection bus, string name, string title = "Chat")
        {
            var app = new FakeTrayProgram { Title = title };
            bus.MethodCall = m => app.Handle(bus, m);
            Assert.Equal(1u, await bus.RequestNameAsync(name));
            return app;
        }

        private static Variant Node(int id, Dictionary<string, object> props, params Variant[] kids) =>
            new("(ia{sv}av)", new object[] { id, props, kids.Cast<object>().ToList() });

        private bool Handle(DBusConnection bus, Message m)
        {
            lock (Calls)
            {
                if (m.Path == "/StatusNotifierItem" && m.Member == "GetAll")
                {
                    bus.Reply(m, "a{sv}", new Dictionary<string, object>
                    {
                        ["Id"] = new Variant("s", "chat-app"), ["Title"] = new Variant("s", Title), ["Status"] = new Variant("s", "Active"),
                        ["IconName"] = new Variant("s", "chat"), ["Menu"] = new Variant("o", new ObjectPath("/Menu")),
                        ["ItemIsMenu"] = new Variant("b", false),
                        ["ToolTip"] = new Variant("(sa(iiay)ss)", new object[] { "", new List<object>(), "Chat", "<i>3</i> unread" }),
                        ["IconPixmap"] = new Variant("a(iiay)", new List<object> { new object[] { 2, 2, new byte[16] } }),
                    });
                    return true;
                }
                if (m.Path == "/StatusNotifierItem" && m.Member is "Activate" or "SecondaryActivate" or "ContextMenu")
                {
                    Calls.Add(m.Member); bus.Reply(m); return true;
                }
                if (m.Path == "/StatusNotifierItem" && m.Member == "Scroll")
                {
                    Calls.Add($"Scroll {m.Body[0]} {m.Body[1]}"); bus.Reply(m); return true;
                }
                if (m.Path == "/Menu" && m.Member == "AboutToShow") { bus.Reply(m, "b", false); return true; }
                if (m.Path == "/Menu" && m.Member == "GetLayout")
                {
                    var root = Node(0, new() { ["children-display"] = new Variant("s", "submenu") },
                        Node(1, new() { ["label"] = new Variant("s", "_Open") }),
                        Node(2, new() { ["type"] = new Variant("s", "separator") }),
                        Node(4, new() { ["label"] = new Variant("s", "Status"), ["children-display"] = new Variant("s", "submenu") },
                            Node(5, new() { ["label"] = new Variant("s", "Busy"), ["toggle-type"] = new Variant("s", "radio"), ["toggle-state"] = new Variant("i", 1) })),
                        Node(3, new() { ["label"] = new Variant("s", "Quit") }));
                    bus.Reply(m, "u(ia{sv}av)", 1u, root.Value);
                    return true;
                }
                if (m.Path == "/Menu" && m.Member == "Event")
                {
                    Calls.Add($"Event {m.Body[0]} {m.Body[1]}"); bus.Reply(m); return true;
                }
                return false;
            }
        }
    }

    /// <summary>A private session bus (dbus-daemon) for one test.</summary>
    private sealed class TestBus : IDisposable
    {
        private readonly Process _p;
        private readonly string _dir;
        public string Address { get; }

        private TestBus(Process p, string dir, string address) { _p = p; _dir = dir; Address = address; }

        public static TestBus Start()
        {
            var exe = ProcessUtil.Which("dbus-daemon");
            if (exe == null) Assert.Skip("dbus-daemon isn't installed");
            var dir = Directory.CreateTempSubdirectory("redstar-bus-").FullName;
            var cfg = Path.Combine(dir, "bus.conf");
            File.WriteAllText(cfg, $"""
                <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
                <busconfig>
                  <type>session</type>
                  <listen>unix:dir={dir}</listen>
                  <auth>EXTERNAL</auth>
                  <policy context="default">
                    <allow send_destination="*" eavesdrop="true"/>
                    <allow eavesdrop="true"/>
                    <allow own="*"/>
                  </policy>
                </busconfig>
                """);
            var psi = new ProcessStartInfo(exe!) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--config-file=" + cfg, "--nofork", "--print-address" }) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!;
            var read = p.StandardOutput.ReadLineAsync();
            if (!read.Wait(5000) || string.IsNullOrWhiteSpace(read.Result))
            {
                try { p.Kill(); } catch { }
                throw new InvalidOperationException("dbus-daemon didn't start: " + p.StandardError.ReadToEnd());
            }
            return new TestBus(p, dir, read.Result!.Trim());
        }

        public void Dispose()
        {
            try { _p.Kill(); _p.WaitForExit(2000); } catch { }
            try { Directory.Delete(_dir, true); } catch { }
        }
    }
}
