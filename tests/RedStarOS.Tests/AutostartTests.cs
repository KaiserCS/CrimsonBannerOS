// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

public class AutostartTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rs-autostart-").FullName;
    private string User => Path.Combine(_root, "user");
    private string Sys => Path.Combine(_root, "etc");
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string dir, string name, string body)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), "[Desktop Entry]\nType=Application\n" + body);
    }

    [Fact]
    public void The_users_file_replaces_the_systems_and_hidden_ones_are_off()
    {
        Write(Sys, "a.desktop", "Name=A\nExec=a --tray\n");
        Write(Sys, "b.desktop", "Name=B\nExec=b\n");
        Write(User, "b.desktop", "Name=B mine\nExec=b --mine\n");
        Write(User, "c.desktop", "Name=C\nExec=c\nX-GNOME-Autostart-enabled=false\n");
        var all = Autostart.All(User, new[] { Sys });
        Assert.Equal(new[] { "A", "B mine", "C" }, all.Select(e => e.Name));
        Assert.Equal("b --mine", all[1].Exec);
        Assert.True(all[2].Hidden);
    }

    [Fact]
    public void Entries_for_other_desktops_are_left_out()
    {
        Write(Sys, "g.desktop", "Name=G\nExec=g\nOnlyShowIn=GNOME;\n");
        Write(Sys, "k.desktop", "Name=K\nExec=k\nNotShowIn=CrimsonBanner;\n");
        Write(Sys, "x.desktop", "Name=X\nExec=x\n");
        var desktops = new[] { "CrimsonBanner" };
        var all = Autostart.All(User, new[] { Sys });
        Assert.Equal(new[] { "X" }, all.Where(e => e.RunsHere(desktops)).Select(e => e.Name));
    }

    [Fact]
    public void Switching_a_system_entry_off_and_on_leaves_the_system_file_alone()
    {
        Write(Sys, "s.desktop", "Name=Steam\nExec=steam -silent %U\n");
        var before = File.ReadAllText(Path.Combine(Sys, "s.desktop"));
        var e = Autostart.All(User, new[] { Sys }).Single();
        Autostart.SetEnabled(e, false, User);
        e = Autostart.All(User, new[] { Sys }).Single();
        Assert.True(e.Hidden);
        Assert.Equal("Steam", e.Name);
        Autostart.SetEnabled(e, true, User);
        e = Autostart.All(User, new[] { Sys }).Single();
        Assert.False(e.Hidden);
        Assert.False(File.Exists(Path.Combine(User, "s.desktop")));         // the override went again
        Assert.Equal(before, File.ReadAllText(Path.Combine(Sys, "s.desktop")));
    }
}

public class SystemAuthTests
{
    [Fact]
    public async Task An_unknown_user_is_never_let_in()
    {
        if (!SystemAuth.Available) Assert.Skip("libpam isn't installed");
        var (outcome, _) = await SystemAuth.CheckAsync("not the password", "no-such-user-crimson-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.NotEqual(SystemAuth.Outcome.Accepted, outcome);
    }
}

public class PolkitAgentTests
{
    [Fact]
    public void The_agent_names_this_process_for_polkit()
    {
        var s = PolkitAgent.Subject();
        Assert.Equal("unix-process", s[0]);
        var d = (System.Collections.Generic.Dictionary<object, object>)s[1];
        Assert.Equal((uint)Environment.ProcessId, ((RedStarOS.Services.DBus.Variant)d["pid"]).Value);
        Assert.True((ulong)((RedStarOS.Services.DBus.Variant)d["start-time"]).Value > 0);
    }

    [Fact]
    public void Identities_become_user_names_with_the_signed_in_user_first()
    {
        var ids = new System.Collections.Generic.List<object>
        {
            new object[] { "unix-user", new System.Collections.Generic.Dictionary<object, object> { ["uid"] = new RedStarOS.Services.DBus.Variant("u", 0u) } },
            new object[] { "unix-group", new System.Collections.Generic.Dictionary<object, object> { ["gid"] = new RedStarOS.Services.DBus.Variant("u", 10u) } },
        };
        Assert.Equal(new[] { "root" }, PolkitAgent.Users(ids));
    }
}

public class IdleManagerTests
{
    [Theory]
    [InlineData(10, 0, 600)]     // screen off only
    [InlineData(0, 30, 1800)]    // sleep only
    [InlineData(10, 30, 600)]    // screen off first, sleep later
    [InlineData(0, 0, 0)]        // never
    public void The_first_step_comes_after_the_shortest_time(int screenOff, int sleep, int seconds) =>
        Assert.Equal(seconds, IdleManager.FirstStepSeconds(screenOff, sleep));
}
