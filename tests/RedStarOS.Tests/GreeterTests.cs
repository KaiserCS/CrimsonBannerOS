// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RedStarOS.Services.Greeter;
using Xunit;

namespace RedStarOS.Tests;

/// <summary>A greetd stand-in on a real Unix socket: one person ("steven", password "right"), with a message from PAM
/// before the password question, as some setups have.</summary>
internal sealed class FakeGreetd : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "greetd-test-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
    public List<JsonElement> Received { get; } = new();
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _cts = new();

    public FakeGreetd()
    {
        _listener.Bind(new UnixDomainSocketEndPoint(Path));
        _listener.Listen(1);
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        using var conn = await _listener.AcceptAsync(_cts.Token);
        using var s = new NetworkStream(conn);
        int stage = 0;
        while (!_cts.IsCancellationRequested)
        {
            byte[] body;
            try { body = await GreetdClient.ReadFrame(s, _cts.Token); } catch { return; }
            var req = JsonDocument.Parse(body).RootElement.Clone();
            lock (Received) Received.Add(req);
            var type = req.GetProperty("type").GetString();
            string reply = (type, stage) switch
            {
                ("create_session", _) when req.GetProperty("username").GetString() == "steven" => Next(ref stage, 1, """{"type":"auth_message","auth_message_type":"info","auth_message":"Welcome back"}"""),
                ("create_session", _) => """{"type":"error","error_type":"error","description":"unknown user"}""",
                ("post_auth_message_response", 1) => Next(ref stage, 2, """{"type":"auth_message","auth_message_type":"secret","auth_message":"Password: "}"""),
                ("post_auth_message_response", 2) when req.GetProperty("response").GetString() == "right" => Next(ref stage, 3, """{"type":"success"}"""),
                ("post_auth_message_response", 2) => Next(ref stage, 0, """{"type":"error","error_type":"auth_error","description":"Authentication failure"}"""),
                ("start_session", 3) => """{"type":"success"}""",
                ("cancel_session", _) => Next(ref stage, 0, """{"type":"success"}"""),
                _ => """{"type":"error","error_type":"error","description":"out of order"}""",
            };
            await GreetdClient.WriteFrame(s, Encoding.UTF8.GetBytes(reply), _cts.Token);
        }
    }

    private static string Next(ref int stage, int to, string reply) { stage = to; return reply; }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        try { File.Delete(Path); } catch { }
    }
}

public class GreeterFlowTests
{
    [Fact]
    public async Task Signing_in_answers_the_password_and_starts_the_desktop()
    {
        using var fake = new FakeGreetd();
        using var client = await GreetdClient.ConnectAsync(fake.Path);
        var flow = new GreeterFlow(client);

        var step = await flow.BeginAsync("steven");
        Assert.Equal(FlowStep.Kinds.Question, step.Kind);
        Assert.Equal("Password", step.Message);
        Assert.True(step.Secret);
        Assert.Equal(new[] { "Welcome back" }, step.Notes);     // PAM's message is passed on, and acknowledged

        step = await flow.AnswerAsync("right");
        Assert.Equal(FlowStep.Kinds.Authenticated, step.Kind);

        step = await flow.StartAsync(new[] { "redstaros-session", "wayland" }, new[] { "XDG_SESSION_TYPE=wayland" });
        Assert.Equal(FlowStep.Kinds.Started, step.Kind);

        var start = fake.Received.Last();
        Assert.Equal("start_session", start.GetProperty("type").GetString());
        Assert.Equal(new[] { "redstaros-session", "wayland" }, start.GetProperty("cmd").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("XDG_SESSION_TYPE=wayland", start.GetProperty("env")[0].GetString());
        // the informational message was answered with null, as greetd expects
        var ack = fake.Received[1];
        Assert.Equal("post_auth_message_response", ack.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, ack.GetProperty("response").ValueKind);
    }

    [Fact]
    public async Task A_wrong_password_cancels_and_the_next_try_starts_cleanly()
    {
        using var fake = new FakeGreetd();
        using var client = await GreetdClient.ConnectAsync(fake.Path);
        var flow = new GreeterFlow(client);

        await flow.BeginAsync("steven");
        var step = await flow.AnswerAsync("wrong");
        Assert.Equal(FlowStep.Kinds.Failed, step.Kind);
        Assert.Contains("password didn't work", step.Message);
        Assert.Equal("cancel_session", fake.Received.Last().GetProperty("type").GetString());

        await flow.BeginAsync("steven");
        Assert.Equal(FlowStep.Kinds.Authenticated, (await flow.AnswerAsync("right")).Kind);
    }

    [Fact]
    public async Task An_unknown_person_is_a_failure_with_greetds_reason()
    {
        using var fake = new FakeGreetd();
        using var client = await GreetdClient.ConnectAsync(fake.Path);
        var step = await new GreeterFlow(client).BeginAsync("nobody-here");
        Assert.Equal(FlowStep.Kinds.Failed, step.Kind);
        Assert.Contains("unknown user", step.Message);
    }

    [Fact]
    public async Task The_preview_accepts_any_password_but_an_empty_one()
    {
        var demo = new DemoGreetd();
        var flow = new GreeterFlow(demo);
        await flow.BeginAsync("anyone");
        Assert.Equal(FlowStep.Kinds.Failed, (await flow.AnswerAsync("")).Kind);
        await flow.BeginAsync("anyone");
        Assert.Equal(FlowStep.Kinds.Authenticated, (await flow.AnswerAsync("x")).Kind);
        Assert.Equal(FlowStep.Kinds.Started, (await flow.StartAsync(new[] { "a" }, Array.Empty<string>())).Kind);
        Assert.Single(demo.Started);
    }

    [Theory]
    [InlineData("Password: ", true, "Password")]
    [InlineData("", true, "Password")]
    [InlineData("Verification code:", false, "Verification code")]
    [InlineData(null, false, "Answer")]
    public void PAMs_prompt_as_a_placeholder(string? text, bool secret, string shown) => Assert.Equal(shown, GreeterFlow.Prompt(text, secret));

    [Fact]
    public void Frames_are_a_length_then_JSON()
    {
        using var ms = new MemoryStream();
        GreetdClient.WriteFrame(ms, Encoding.UTF8.GetBytes("{\"type\":\"success\"}"), default).GetAwaiter().GetResult();
        var bytes = ms.ToArray();
        Assert.Equal(4 + 18, bytes.Length);
        Assert.Equal(18, BitConverter.ToInt32(bytes, 0));
        ms.Position = 0;
        Assert.True(GreetdClient.Parse(GreetdClient.ReadFrame(ms, default).GetAwaiter().GetResult()).IsSuccess);
    }
}

public class GreeterDataTests
{
    private const string Passwd = """
        root:x:0:0:root:/root:/bin/bash
        greeter:x:964:964:greetd greeter user:/var/lib/greetd:/bin/bash
        steven:x:1000:1000:Steven K,,,:/home/steven:/usr/bin/fish
        anna:x:1001:1001::/home/anna:/bin/bash
        svc:x:1002:1002::/var/svc:/usr/bin/nologin
        nobody:x:65534:65534:Kernel Overflow User:/:/usr/bin/nologin
        broken line
        """;

    [Fact]
    public void Ordinary_accounts_that_can_log_in()
    {
        var users = GreeterData.Users(Passwd);
        Assert.Equal(new[] { "anna", "steven" }, users.Select(u => u.Name));
        Assert.Equal("Steven K", users[1].RealName);
        Assert.Equal("Steven K", users[1].Shown);
        Assert.Equal("anna", users[0].Shown);
        Assert.Equal("/home/steven", users[1].Home);
    }

    [Fact]
    public void Login_defs_sets_the_range()
    {
        Assert.Equal((500, 59999), GreeterData.UidRange("# comment\nUID_MIN   500\nUID_MAX 59999\n"));
        Assert.Equal((1000, 60000), GreeterData.UidRange(null));
    }

    [Theory]
    [InlineData("redstaros-session wayland", new[] { "redstaros-session", "wayland" })]
    [InlineData("\"/opt/My Desktop/start\" --flag %U", new[] { "/opt/My Desktop/start", "--flag" })]
    [InlineData("sh -c \"echo \\\"hi\\\"\"", new[] { "sh", "-c", "echo \"hi\"" })]
    [InlineData("app 100%%", new[] { "app", "100%" })]
    public void Exec_lines_become_arguments(string exec, string[] args) => Assert.Equal(args, GreeterData.SplitExec(exec));

    [Fact]
    public void Desktops_from_their_files()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var wl = Directory.CreateDirectory(Path.Combine(dir.FullName, "wayland-sessions")).FullName;
            var x = Directory.CreateDirectory(Path.Combine(dir.FullName, "xsessions")).FullName;
            File.WriteAllText(Path.Combine(wl, "redstaros-wayland.desktop"), "[Desktop Entry]\nName=Crimson Banner OS (Wayland)\nName[de]=X\nExec=redstaros-session wayland\nDesktopNames=CrimsonBanner;\n");
            File.WriteAllText(Path.Combine(wl, "gone.desktop"), "[Desktop Entry]\nName=Gone\nExec=not-installed\n");
            File.WriteAllText(Path.Combine(wl, "hidden.desktop"), "[Desktop Entry]\nName=Hidden\nExec=redstaros-session\nHidden=true\n");
            File.WriteAllText(Path.Combine(x, "redstaros.desktop"), "[Desktop Entry]\nName=Crimson Banner OS\nExec=redstaros-session\n");
            string? Which(string p) => p is "redstaros-session" or "startx" ? "/usr/bin/" + p : null;

            var sessions = GreeterData.Sessions(new[] { wl }, new[] { x }, Which);
            Assert.Equal(new[] { "redstaros-wayland", "redstaros" }, sessions.Select(s => s.Id));
            Assert.Equal("Crimson Banner OS (X11)", sessions[1].Shown);
            Assert.Equal("redstaros", GreeterData.Preferred(sessions, null)!.Id);
            Assert.Equal("redstaros-wayland", GreeterData.Preferred(sessions, "redstaros-wayland")!.Id);

            // X11 desktops need startx; without it they aren't offered
            Assert.Single(GreeterData.Sessions(new[] { wl }, new[] { x }, p => p == "redstaros-session" ? "/usr/bin/x" : null));

            var (cmd, env) = GreeterData.Command(sessions[0]);
            Assert.Equal(new[] { "redstaros-session", "wayland" }, cmd);
            Assert.Contains("XDG_SESSION_TYPE=wayland", env);
            Assert.Contains("XDG_CURRENT_DESKTOP=CrimsonBanner", env);
            Assert.Contains("XDG_SESSION_DESKTOP=redstaros-wayland", env);
            (cmd, env) = GreeterData.Command(sessions[1]);
            Assert.Equal(new[] { "startx", "/usr/bin/env", "redstaros-session" }, cmd);
            Assert.Contains("XDG_SESSION_TYPE=x11", env);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void It_remembers_the_last_person_and_their_desktop()
    {
        var path = Path.Combine(Path.GetTempPath(), "greeter-state-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null(GreeterData.Load(path).LastUser);
            var s = new GreeterData.State { LastUser = "steven" };
            s.Sessions["steven"] = "redstaros-wayland";
            GreeterData.Save(path, s);
            var back = GreeterData.Load(path);
            Assert.Equal("steven", back.LastUser);
            Assert.Equal("redstaros-wayland", back.Sessions["steven"]);
            File.WriteAllText(path, "not json");
            Assert.Null(GreeterData.Load(path).LastUser);
        }
        finally { File.Delete(path); }
    }
}
