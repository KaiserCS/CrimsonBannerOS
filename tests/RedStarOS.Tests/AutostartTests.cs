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

public class RegionSettingsTests
{
    [Fact]
    public void Chosen_layouts_become_xkb_names()
    {
        var s = new Settings { KeyboardLayouts = new() { "us", "de:nodeadkeys" }, KeyboardSwitch = "grp:alt_shift_toggle", KeyboardCompose = "compose:ralt" };
        Assert.Equal(("us,de", ",nodeadkeys", "grp:alt_shift_toggle,compose:ralt"), RegionSettings.Xkb(s));
        // one layout: no switching key, no variants
        s.KeyboardLayouts = new() { "fr" };
        s.KeyboardCompose = "";
        Assert.Equal(("fr", "", ""), RegionSettings.Xkb(s));
    }

    [Fact]
    public void Layouts_and_variants_are_read_from_xkbs_list()
    {
        var f = Path.Combine(Path.GetTempPath(), "rs-evdev-" + Guid.NewGuid().ToString("N") + ".lst");
        File.WriteAllText(f, "! model\n  pc105  Generic 105-key PC\n\n! layout\n  us   English (US)\n  de   German\n\n! variant\n  nodeadkeys  de: German (no dead keys)\n  intl  us: English (US, intl., with dead keys)\n\n! option\n  grp  Switching\n");
        try
        {
            var l = RegionSettings.Layouts(f);
            Assert.Equal(new[] { "English (US)", "German" }, l.Select(x => x.Name));
            Assert.Equal(("nodeadkeys", "German (no dead keys)"), l[1].Variants.Single());
        }
        finally { File.Delete(f); }
    }
}

public class SystemUpdatesTests
{
    [Fact]
    public void PackageKit_ids_are_split_into_name_and_version()
    {
        var u = SystemUpdates.Parse(8, "openssl;3.4.1-1;x86_64;core", "The Open Source toolkit for SSL");
        Assert.Equal(("openssl", "3.4.1-1", true), (u.Name, u.Version, u.Security));
        Assert.False(SystemUpdates.Parse(2, "zstd;1.5.6-1;x86_64;core", "").Security);
    }

    [Fact]
    public void Without_PackageKit_the_package_manager_runs_in_a_terminal()
    {
        var c = SystemUpdates.TerminalCommand(n => n == "pacman" ? "/usr/bin/pacman" : null);
        Assert.Equal("sudo", c!.Value.Exe);
        Assert.Equal(new[] { "pacman", "-Syu" }, c.Value.Args);
        Assert.Null(SystemUpdates.TerminalCommand(_ => null));
    }
}

public class UserAccountsTests
{
    [Theory]
    [InlineData("ada", true)]
    [InlineData("ada_l-2", true)]
    [InlineData("Ada", false)]
    [InlineData("2ada", false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    public void User_names_follow_useradds_rules(string name, bool ok) => Assert.Equal(ok, UserAccounts.ValidUserName(name));

    [Fact]
    public void A_user_name_is_suggested_from_the_full_name()
    {
        Assert.Equal("ada", UserAccounts.SuggestUserName("Ada Lovelace"));
        Assert.Equal("user", UserAccounts.SuggestUserName("  "));
    }

    [Fact]
    public void Passwords_are_hashed_with_a_fresh_salt()
    {
        string a, b;
        try { a = UserAccounts.Hash("secret"); b = UserAccounts.Hash("secret"); }
        catch (DllNotFoundException) { Assert.Skip("libcrypt isn't installed"); return; }
        Assert.StartsWith("$", a);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("secret", a);
    }
}

public class PolkitHelperTests
{
    [Fact]
    public async Task The_password_answers_the_helpers_question_and_success_is_read()
    {
        var reader = new System.IO.StringReader("PAM_TEXT_INFO hello\nPAM_PROMPT_ECHO_OFF Password: \nSUCCESS\n");
        var writer = new System.IO.StringWriter { NewLine = "\n" };
        var (ok, _) = await PolkitAgent.Converse(reader, writer, "pw", default);
        Assert.True(ok);
        Assert.Equal("pw\n", writer.ToString());
    }

    [Fact]
    public async Task A_refused_password_says_what_pam_said()
    {
        var reader = new System.IO.StringReader("PAM_PROMPT_ECHO_OFF Password: \nPAM_ERROR_MSG Authentication failure\nFAILURE\n");
        var (ok, why) = await PolkitAgent.Converse(reader, new System.IO.StringWriter(), "bad", default);
        Assert.False(ok);
        Assert.Equal("Authentication failure", why);
    }
}

public class PacmanUpdatesTests
{
    [Fact]
    public void Checkupdates_lines_become_updates()
    {
        var list = RedStarOS.Services.SystemUpdates.ParseCheckUpdates(
            "linux 6.17.8.arch1-1 -> 6.17.9.arch1-1\nfirefox 145.0-1 -> 145.0.1-1\n\nnot an update line\n");
        Assert.Equal(new[] { "firefox", "linux" }, list.Select(u => u.Name));
        Assert.Equal("145.0.1-1", list[0].Version);
        Assert.Equal("145.0-1 → 145.0.1-1", list[0].Summary);
        Assert.Empty(RedStarOS.Services.SystemUpdates.ParseCheckUpdates(""));
    }

    [Theory]
    [InlineData("Downloading 45%", 45)]
    [InlineData("( 3/12) upgrading linux", 25)]
    [InlineData("(12/12) checking keys in keyring", 100)]
    [InlineData("resolving dependencies...", -1)]
    [InlineData("1.2.3 is 400% better", -1)]
    public void Progress_from_a_line(string line, int pct) => Assert.Equal(pct, RedStarOS.Services.Software.Runner.ProgressOf(line));
}
