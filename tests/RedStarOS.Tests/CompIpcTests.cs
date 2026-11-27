using System.Net.Sockets;
using System.Text;
using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>
/// The shell's side of the redstar-comp connection: parsing its messages, the Alt+Tab order it reports, and the
/// commands sent back (against a fake compositor on a Unix socket).
/// </summary>
[Collection("environment")]     // sets REDSTAR_COMP_SOCKET
public class CompIpcTests
{
    private const long N1 = CompIpc.NativeBase + 1, N2 = CompIpc.NativeBase + 2;

    [Fact]
    public void Window_messages_build_the_native_window_list()
    {
        CompIpc.Reset();
        CompIpc.Handle($"window\t{N1}\t-\t4242\torg.gnome.TextEditor\tNotes — Text Editor");
        CompIpc.Handle($"window\t{N2}\tmM\t17\tfoot\ttitle\twith a tab");
        Assert.Equal(2, CompIpc.Windows.Count);
        var a = CompIpc.Windows[N1];
        Assert.Equal("org.gnome.TextEditor", a.AppId);
        Assert.Equal("Notes — Text Editor", a.Title);
        Assert.Equal(4242, a.Pid);
        Assert.False(a.Minimized);
        var b = CompIpc.Windows[N2];
        Assert.True(b.Minimized);
        Assert.True(b.Maximized);
        Assert.Equal("title\twith a tab", b.Title);           // the title is the rest of the line

        CompIpc.Handle($"window\t{N1}\tf\t4242\torg.gnome.TextEditor\tRenamed");   // changes replace the entry
        Assert.Equal("Renamed", CompIpc.Windows[N1].Title);
        Assert.True(CompIpc.Windows[N1].Fullscreen);

        CompIpc.Handle($"gone\t{N2}");
        Assert.False(CompIpc.Windows.ContainsKey(N2));
        CompIpc.Handle("gone\tnot-a-number");                   // nonsense is ignored
        CompIpc.Handle("something-new\t1\t2");
        Assert.Single(CompIpc.Windows);
        CompIpc.Reset();
    }

    [Fact]
    public void Active_window_stack_and_keys()
    {
        CompIpc.Reset();
        string? key = null;
        void OnKey(string k) => key = k;
        CompIpc.Key += OnKey;
        try
        {
            CompIpc.Handle($"active\t{N1}");
            Assert.Equal(N1, CompIpc.Active);
            Assert.True(CompIpc.IsNative(CompIpc.Active));
            CompIpc.Handle("active\t4194305");                 // an X11 window
            Assert.False(CompIpc.IsNative(CompIpc.Active));
            CompIpc.Handle($"stack\t{N2}\t4194305\t{N1}");
            Assert.Equal(new[] { N2, 4194305L, N1 }, CompIpc.Stack);
            CompIpc.Handle("key\tswitcher-open");
            Assert.Equal("switcher-open", key);
        }
        finally { CompIpc.Key -= OnKey; CompIpc.Reset(); }
    }

    [Fact]
    public void Alt_tab_follows_the_compositor_order()
    {
        var list = new List<X11Desktop.XWin>
        {
            new(4194305, "Terminal", "term", 0, false),
            new(N1, "Editor", "editor", 0, false),
            new(4194400, "Not in the stack", "x", 0, false),
            new(N2, "Player", "player", 0, true),
        };
        var ordered = X11Desktop.OrderByStack(list, new[] { N2, 4194305L, N1 });
        Assert.Equal(new[] { N2, 4194305L, N1, 4194400L }, ordered.Select(w => w.Id));
        Assert.Equal(list.Select(w => w.Id), X11Desktop.OrderByStack(list, Array.Empty<long>()).Select(w => w.Id));
    }

    [Fact]
    public async Task Talks_to_the_compositor_over_its_socket()
    {
        // a stand-in for redstar-comp: accepts the shell, sends a window, reads the commands that come back
        var path = Path.Combine(Path.GetTempPath(), "rs-comp-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var old = Environment.GetEnvironmentVariable("REDSTAR_COMP_SOCKET");
        Environment.SetEnvironmentVariable("REDSTAR_COMP_SOCKET", path);
        try
        {
            CompIpc.Reset();
            var changed = new TaskCompletionSource();
            void OnChanged() { if (CompIpc.Windows.ContainsKey(N1)) changed.TrySetResult(); }
            CompIpc.Changed += OnChanged;
            Assert.True(CompIpc.Start());
            using var comp = await listener.AcceptAsync(TestContext.Current.CancellationToken);
            comp.Send(Encoding.UTF8.GetBytes($"window\t{N1}\t-\t99\tfoot\tfoot\nactive\t{N1}\n"));
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            CompIpc.Changed -= OnChanged;
            Assert.Equal("foot", CompIpc.Windows[N1].AppId);

            X11Desktop.Activate(N1);              // native ids go to the compositor, not to X
            X11Desktop.SnapWindow(N1, "left");
            X11Desktop.CloseWindow(N1);
            var got = new StringBuilder();
            var buf = new byte[1024];
            var deadline = DateTime.Now.AddSeconds(5);
            while (!got.ToString().Contains($"close\t{N1}") && DateTime.Now < deadline)
            {
                comp.ReceiveTimeout = 1000;
                int n = comp.Receive(buf);
                if (n <= 0) break;
                got.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            // the accent goes to the compositor whenever it changes, which another test running alongside may do at any time
            var lines = got.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("accent\t")).ToArray();
            Assert.Equal("hello", lines[0]);
            Assert.Contains($"activate\t{N1}", lines);
            Assert.Contains($"left\t{N1}", lines);
            Assert.Contains($"close\t{N1}", lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDSTAR_COMP_SOCKET", old);
            try { File.Delete(path); } catch { }
        }
    }
}

/// <summary>Displays, screenshots and screen sharing inside redstar-comp (phase 4).</summary>
[Collection("environment")]     // sets REDSTAR_COMP_SOCKET
public class CompDisplayTests
{
    [Fact]
    public void Output_lines_are_read()
    {
        var o = CompIpc.ParseOutput("output\tDP-1\tDell Inc. U2720Q\t1\t3840x2160@59997\t1.500\t0\t0\t1\t0\t3840x2160@59997*,2560x1440@59951,1920x1080@60000");
        Assert.NotNull(o);
        Assert.Equal("DP-1", o!.Name);
        Assert.Equal("Dell Inc. U2720Q", o.Description);
        Assert.True(o.Enabled);
        Assert.Equal((3840, 2160, 59997), (o.Width, o.Height, o.RefreshMhz));
        Assert.Equal(1.5, o.Scale, 3);
        Assert.Equal(1, o.Transform);
        Assert.False(o.Nested);
        Assert.Equal(3, o.Modes.Count);
        Assert.True(o.Modes[0].Preferred);
        Assert.Equal("1920 × 1080 @ 60 Hz", o.Modes[2].ToString());
        Assert.Equal((1440, 2560), o.Logical);           // rotated 90° and scaled 150%

        var nested = CompIpc.ParseOutput("output\tWL-1\t \t1\t1280x720@0\t1.000\t0\t0\t0\t1\t");
        Assert.NotNull(nested);
        Assert.True(nested!.Nested);
        Assert.Empty(nested.Modes);
        Assert.Null(CompIpc.ParseOutput("output\ttoo\tshort"));
        Assert.Null(CompIpc.ParseOutput("window\t1\t2\t3\t4\t5\t6\t7\t8\t9\t10\t11"));
    }

    [Fact]
    public void Screens_are_placed_next_to_the_main_one()
    {
        var main = CompIpc.ParseOutput("output\teDP-1\tlaptop\t1\t2880x1800@60000\t2.000\t0\t0\t0\t0\t")!;
        Assert.Equal((1440, 0), Views.SettingsPages.PlaceNextTo(main, (1920, 1080), "right"));
        Assert.Equal((-1920, 0), Views.SettingsPages.PlaceNextTo(main, (1920, 1080), "left"));
        Assert.Equal((0, -1080), Views.SettingsPages.PlaceNextTo(main, (1920, 1080), "above"));
        Assert.Equal((0, 900), Views.SettingsPages.PlaceNextTo(main, (1920, 1080), "below"));
    }

    [Fact]
    public void Screenshot_tools()
    {
        string? Has(string name, params string[] installed) => installed.Contains(name) ? "/usr/bin/" + name : null;
        var file = "/tmp/shot.png";
        var full = Screenshots.Command(file, false, null, wayland: true, n => Has(n, "grim"));
        Assert.Equal("/usr/bin/grim", full!.Value.Exe);
        Assert.Equal(new[] { file }, full.Value.Args);
        var win = Screenshots.Command(file, true, "100,80 640x480", wayland: true, n => Has(n, "grim"));
        Assert.Equal(new[] { "-g", "100,80 640x480", file }, win!.Value.Args);
        Assert.Null(Screenshots.Command(file, false, null, wayland: true, n => Has(n, "scrot")));     // Wayland needs grim
        Assert.Equal(new[] { "-u", "-o", file }, Screenshots.Command(file, true, null, false, n => Has(n, "scrot", "maim"))!.Value.Args);
        Assert.Equal("/usr/bin/import", Screenshots.Command(file, false, null, false, n => Has(n, "import"))!.Value.Exe);
        Assert.Null(Screenshots.Command(file, false, null, false, _ => null));
        Assert.EndsWith("Screenshot 2026-11-18 21-05-09.png", Screenshots.NewPath(new DateTime(2026, 11, 18, 21, 5, 9)));
    }

    [Fact]
    public void Portal_config_picks_the_wlroots_portal()
    {
        Assert.Contains("org.freedesktop.impl.portal.ScreenCast=wlr", PortalSetup.Config);
        Assert.Contains("org.freedesktop.impl.portal.Screenshot=wlr", PortalSetup.Config);
        Assert.Contains("redstar-portals.conf", PortalSetup.FileNames);
    }

    [Fact]
    public async Task Display_questions_get_their_answers()
    {
        // a fake compositor answering "outputs" and "output-set"
        var path = Path.Combine(Path.GetTempPath(), "rs-comp-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var old = Environment.GetEnvironmentVariable("REDSTAR_COMP_SOCKET");
        Environment.SetEnvironmentVariable("REDSTAR_COMP_SOCKET", path);
        try
        {
            Assert.True(CompIpc.Start());
            using var comp = await listener.AcceptAsync(TestContext.Current.CancellationToken);
            var server = Task.Run(() =>
            {
                var buf = new byte[4096];
                var got = "";
                while (true)
                {
                    int n;
                    try { n = comp.Receive(buf); } catch { return; }
                    if (n <= 0) return;
                    got += Encoding.UTF8.GetString(buf, 0, n);
                    int nl;
                    while ((nl = got.IndexOf('\n')) >= 0)
                    {
                        var line = got[..nl];
                        got = got[(nl + 1)..];
                        string reply = line == "outputs"
                            ? "active\t0\noutput\tDP-1\tScreen\t1\t1920x1080@60000\t1.000\t0\t0\t0\t0\t1920x1080@60000*\noutputs-end\n"
                            : line.StartsWith("output-set\tDP-1\t1\t") ? "output-ok\tDP-1\n"
                            : line.StartsWith("output-set\t") ? "output-failed\tDP-9\tunknown screen\n" : "";
                        if (reply.Length > 0) comp.Send(Encoding.UTF8.GetBytes(reply));
                    }
                }
            }, TestContext.Current.CancellationToken);
            var outs = await CompIpc.Outputs();
            Assert.NotNull(outs);
            Assert.Single(outs!);
            Assert.Equal("DP-1", outs![0].Name);
            Assert.Null(await CompIpc.SetOutput("DP-1", true, 1920, 1080, 60000, 0, 0, 1.25, 0));
            Assert.Equal("unknown screen", await CompIpc.SetOutput("DP-9", false, 0, 0, 0, 0, 0, 1, 0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("REDSTAR_COMP_SOCKET", old);
            try { File.Delete(path); } catch { }
        }
    }
}

/// <summary>Red Star's own windows natively on Wayland (phase 4, second part).</summary>
public class NativeWaylandShellTests
{
    [Fact]
    public void Native_drawing_is_only_used_inside_redstar_comp_when_asked_for()
    {
        Assert.True(WaylandShell.Decide(null, setting: true, inComp: true, waylandDisplay: true, crashedLastTime: false));
        Assert.False(WaylandShell.Decide(null, setting: false, inComp: true, waylandDisplay: true, crashedLastTime: false));
        Assert.False(WaylandShell.Decide(null, setting: true, inComp: false, waylandDisplay: true, crashedLastTime: false));   // another compositor
        Assert.False(WaylandShell.Decide(null, setting: true, inComp: true, waylandDisplay: false, crashedLastTime: false));
        Assert.False(WaylandShell.Decide(null, setting: true, inComp: true, waylandDisplay: true, crashedLastTime: true));    // fell back
        Assert.True(WaylandShell.Decide("1", setting: false, inComp: true, waylandDisplay: true, crashedLastTime: true));     // forced on
        Assert.False(WaylandShell.Decide("0", setting: true, inComp: true, waylandDisplay: true, crashedLastTime: false));    // forced off
        Assert.False(WaylandShell.Decide("1", setting: true, inComp: false, waylandDisplay: true, crashedLastTime: false));
    }

    [Fact]
    public void Role_lines()
    {
        Assert.Equal("role\tdesktop\t4242\tRed Star OS\t0\t0\t0\t48", CompIpc.RoleLine("desktop", 4242, "Red Star OS", 0, 0, 0, 48));
        Assert.Equal("role\tdock\t1\ta b\t0\t0\t0\t0", CompIpc.RoleLine("dock", 1, "a\tb", 0, 0, 0, 0));   // a tab can't break the line
    }
}

[Collection("environment")]     // shares CompIpc's state with the other connection tests
public class WindowTokenTests
{
    [Fact]
    public void Token_lines_map_the_shells_windows_to_compositor_ids()
    {
        CompIpc.Reset();
        CompIpc.Handle($"token\tab12cd34ef56ab78\t{CompIpc.NativeBase + 7}");
        CompIpc.Handle($"token\tff00ff00ff00ff00\t{CompIpc.NativeBase + 8}");
        Assert.Equal(CompIpc.NativeBase + 7, CompIpc.Tokens["ab12cd34ef56ab78"]);
        Assert.Equal(CompIpc.NativeBase + 8, CompIpc.Tokens["ff00ff00ff00ff00"]);
        CompIpc.Handle("token\tbroken");                 // ignored
        Assert.Equal(2, CompIpc.Tokens.Count);
        Assert.StartsWith("⁣", WaylandShell.TokenMark);   // invisible if it were ever shown
        CompIpc.Reset();
        Assert.Empty(CompIpc.Tokens);
    }
}
