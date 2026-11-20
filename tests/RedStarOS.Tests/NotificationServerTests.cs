// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>Other programs' notifications: the org.freedesktop.Notifications service on a private bus, called with our
/// own client and with GLib's gdbus (the same library most programs' notifications go through).</summary>
public class NotificationServerTests
{
    [Fact]
    public void Markup_is_shown_as_plain_text()
    {
        Assert.Equal("Hello world & friends\nnext line", NotificationServer.Plain("<b>Hello</b> <a href=\"x\">world</a> &amp; friends<br/>next line"));
    }

    [Fact]
    public void A_notification_without_a_summary_is_titled_with_its_program()
    {
        var n = new NotificationServer.Incoming(1, "Discord", "", "", "New message", Array.Empty<(string, string)>(), 1, null, false, 0);
        Assert.Equal(("Discord", "New message"), OtherAppsNotifications.Describe(n));
    }

    [Fact]
    public async Task Programs_can_notify_and_hear_about_their_buttons()
    {
        using var tb = SystemTrayTests.TestBus.Start();
        using var serverBus = await DBusConnection.ConnectAsync(tb.Address);
        var server = new NotificationServer(serverBus);
        var got = new TaskCompletionSource<NotificationServer.Incoming>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Received += n => got.TrySetResult(n);
        Assert.True(await server.StartAsync());

        using var app = await DBusConnection.ConnectAsync(tb.Address);
        var signals = new List<string>();
        app.Signal += m => { lock (signals) signals.Add($"{m.Member} {string.Join(" ", m.Body)}"); };
        await app.AddMatchAsync($"type='signal',interface='{NotificationServer.Iface}'");

        var caps = await app.CallAsync(NotificationServer.Name, NotificationServer.Path, NotificationServer.Iface, "GetCapabilities");
        Assert.Contains("actions", ((List<object>)caps.Body[0]).OfType<string>());

        var hints = new Dictionary<object, object> { ["urgency"] = new Variant("y", (byte)2), ["desktop-entry"] = new Variant("s", "steam") };
        var reply = await app.CallAsync(NotificationServer.Name, NotificationServer.Path, NotificationServer.Iface, "Notify", "susssasa{sv}i",
            new object[] { "Steam", 0u, "", "Download finished", "<i>Game</i> is ready", new List<object> { "default", "", "play", "Play" }, hints, -1 });
        uint id = (uint)reply.Body[0];
        Assert.True(id > 0);
        var n = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Download finished", n.Summary);
        Assert.Equal("Game is ready", n.Body);
        Assert.Equal((byte)2, n.Urgency);
        Assert.Equal("steam", n.DesktopEntry);
        Assert.Contains(("play", "Play"), n.Actions);

        server.Invoke(id, "play");
        for (int i = 0; i < 50; i++) { lock (signals) if (signals.Count >= 2) break; await Task.Delay(50); }
        lock (signals)
        {
            Assert.Contains($"ActionInvoked {id} play", signals);
            Assert.Contains($"NotificationClosed {id} {NotificationServer.Dismissed}", signals);
        }
    }

    [Fact]
    public async Task GLib_programs_reach_it_too()
    {
        var gdbus = ProcessUtil.Which("gdbus");
        if (gdbus == null) Assert.Skip("gdbus isn't installed");
        using var tb = SystemTrayTests.TestBus.Start();
        using var serverBus = await DBusConnection.ConnectAsync(tb.Address);
        var server = new NotificationServer(serverBus);
        var got = new TaskCompletionSource<NotificationServer.Incoming>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Received += n => got.TrySetResult(n);
        Assert.True(await server.StartAsync());

        var psi = new ProcessStartInfo(gdbus!) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "call", "--address", tb.Address, "--dest", NotificationServer.Name, "--object-path", NotificationServer.Path,
                                  "--method", "org.freedesktop.Notifications.Notify", "notify-send", "0", "", "Hello", "from GLib", "[]", "{}", "5000" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, await p.StandardError.ReadToEndAsync());
        Assert.StartsWith("(uint32 ", output.Trim());
        var n = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("Hello", "from GLib"), (n.Summary, n.Body));
    }
}
