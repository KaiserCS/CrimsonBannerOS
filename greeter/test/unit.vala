// SPDX-License-Identifier: GPL-3.0-or-later
// crimson-login's tests: greetd's messages and their framing, signing in step by step (with a greetd that answers
// as PAM would), who is shown, which desktops are offered and what greetd is asked to start, and what's remembered.
// The same cases as the shell's GreeterTests, so both login screens behave alike while both exist.
using CrimsonLogin;

const string PASSWD = """root:x:0:0:root:/root:/bin/bash
greeter:x:968:968:greeter:/var/lib/greetd:/bin/bash
nobody:x:65534:65534:Kernel Overflow User:/:/usr/bin/nologin
rosa:x:1000:1000:Rosa Luxemburg,,,:/home/rosa:/bin/bash
karl:x:1001:1001::/home/karl:/usr/bin/zsh
backup:x:1002:1002:Backups:/srv/backup:/usr/bin/nologin
clara:x:1003:1003:Clara Zetkin:/home/clara:/bin/bash
rosa:x:1004:1004:Duplicate:/home/rosa2:/bin/bash
""";

string? which_some (string p) {
    return p == "redstaros-session" || p == "startx" || p == "sway" ? "/usr/bin/" + p : null;
}

string? which_none (string p) {
    return null;
}

MainLoop loop;

/// Runs an async test (started with its own callback) to its end.
void run_loop () {
    loop = new MainLoop ();
    loop.run ();
}

void done (Error? e) {
    if (e != null) error ("%s", e.message);
    loop.quit ();
}

/// greetd as PAM would answer: a password question, then success for "right", a refusal for anything else;
/// "info" first for the user "expiring", and a second factor for "two".
class FakeGreetd : Object, Greetd {
    public string? user;
    public int questions;
    public string[] log = {};

    public async GreetdReply create_session (string user) throws Error {
        this.user = user;
        questions = 0;
        log += "create " + user;
        if (user == "expiring") return new GreetdReply ("auth_message", "info", "Your password expires in 3 days");
        return new GreetdReply ("auth_message", "secret", "Password: ");
    }

    public async GreetdReply respond (string? response) throws Error {
        log += "respond " + (response ?? "(null)");
        if (response == null) return new GreetdReply ("auth_message", "secret", "Password: ");
        if (response != "right" && questions == 0) return new GreetdReply ("error", null, null, "auth_error", "Authentication failure");
        questions++;
        if (user == "two" && questions == 1) return new GreetdReply ("auth_message", "visible", "Verification code: ");
        return new GreetdReply ("success");
    }

    public async GreetdReply start_session (string[] cmd, string[] env) throws Error {
        log += "start " + string.joinv (" ", cmd);
        return new GreetdReply ("success");
    }

    public async GreetdReply cancel_session () throws Error {
        log += "cancel";
        return new GreetdReply ("success");
    }
}

async void frames () throws Error {
    var out = new MemoryOutputStream.resizable ();
    yield GreetdClient.write_frame (out, "{\"type\":\"cancel_session\"}");
    yield GreetdClient.write_frame (out, "{\"é\":1}");
    out.close ();
    var bytes = out.steal_as_bytes ();
    assert (bytes.get_size () == 4 + 25 + 4 + 8);
    uint32 n = 0;
    Memory.copy (&n, bytes.get_data (), 4);
    assert (n == 25);   // the machine's own byte order
    var input = new MemoryInputStream.from_bytes (bytes);
    assert ((yield GreetdClient.read_frame (input)) == "{\"type\":\"cancel_session\"}");
    assert ((yield GreetdClient.read_frame (input)) == "{\"é\":1}");
    try { yield GreetdClient.read_frame (input); assert_not_reached (); } catch (IOError e) { }
}

async void over_socket () throws Error {
    // a greetd of our own on a socket: it checks the request and answers
    var dir = DirUtils.make_tmp ("login-sock-XXXXXX");
    var path = Path.build_filename (dir, "greetd.sock");
    var service = new SocketService ();
    SocketAddress effective;
    service.add_address (new UnixSocketAddress (path), SocketType.STREAM, SocketProtocol.DEFAULT, null, out effective);
    string? asked = null;
    service.incoming.connect ((c, source) => {
        GreetdClient.read_frame.begin (c.input_stream, (o2, r2) => {
            try {
                asked = GreetdClient.read_frame.end (r2);
                GreetdClient.write_frame.begin (c.output_stream, "{\"type\":\"auth_message\",\"auth_message_type\":\"secret\",\"auth_message\":\"Password: \"}");
            } catch (Error e) { error ("%s", e.message); }
        });
        return true;
    });
    service.start ();
    var client = GreetdClient.connect_to (path);
    var reply = yield client.create_session ("rosa");
    assert (reply.is_question && reply.auth_message == "Password: ");
    assert (asked == "{\"type\":\"create_session\",\"username\":\"rosa\"}");
}

async void signing_in () throws Error {
    var g = new FakeGreetd ();
    var flow = new Flow (g);
    var step = yield flow.sign_in ("rosa");
    assert (step.kind == StepKind.QUESTION && step.message == "Password" && step.secret);
    step = yield flow.answer ("wrong");
    assert (step.kind == StepKind.FAILED && step.message == "That password didn't work. Try again.");
    assert (g.log[g.log.length - 1] == "cancel");    // the next try starts cleanly
    step = yield flow.sign_in ("rosa");
    step = yield flow.answer ("right");
    assert (step.kind == StepKind.AUTHENTICATED);
    step = yield flow.start ({ "redstaros-session" }, {});
    assert (step.kind == StepKind.STARTED && g.log[g.log.length - 1] == "start redstaros-session");
}

async void messages () throws Error {
    var g = new FakeGreetd ();
    var flow = new Flow (g);
    var step = yield flow.sign_in ("expiring");
    assert (step.kind == StepKind.QUESTION && step.notes.length == 1 && step.notes[0] == "Your password expires in 3 days");
    assert (g.log[1] == "respond (null)");   // the message acknowledged
    g = new FakeGreetd ();
    flow = new Flow (g);
    yield flow.sign_in ("two");
    step = yield flow.answer ("right");
    assert (step.kind == StepKind.QUESTION && step.message == "Verification code" && !step.secret);
    step = yield flow.answer ("123456");
    assert (step.kind == StepKind.AUTHENTICATED);
}

async void demo_flow () throws Error {
    var d = new DemoGreetd ();
    var flow = new Flow (d);
    yield flow.sign_in ("anyone");
    assert ((yield flow.answer ("")).kind == StepKind.FAILED);
    yield flow.sign_in ("anyone");
    assert ((yield flow.answer ("x")).kind == StepKind.AUTHENTICATED);
    assert ((yield flow.start ({ "a", "b" }, {})).kind == StepKind.STARTED && d.started[0] == "a b");
}

int main (string[] args) {
    Test.init (ref args);

    Test.add_func ("/data/users", () => {
        var u = Data.users (PASSWD);
        assert (u.length == 3);
        assert (u[0].shown == "Clara Zetkin");
        assert (u[1].shown == "karl");          // no real name: the login
        assert (u[2].shown == "Rosa Luxemburg"); // GECOS's first field only
        assert (u[2].uid == 1000);              // the first rosa wins
        var few = Data.users (PASSWD, 1001, 1001);
        assert (few.length == 1 && few[0].name == "karl");
    });

    Test.add_func ("/data/uid-range", () => {
        int min, max;
        Data.uid_range ("# comment\nUID_MIN\t\t 2000\nUID_MAX 3000\n", out min, out max);
        assert (min == 2000 && max == 3000);
        Data.uid_range (null, out min, out max);
        assert (min == 1000 && max == 60000);
    });

    Test.add_func ("/data/split-exec", () => {
        var a = Data.split_exec ("redstaros-session --wayland %U");
        assert (a.length == 2 && a[0] == "redstaros-session" && a[1] == "--wayland");
        var b = Data.split_exec ("sh -c \"echo \\\"hi there\\\"\" 100%%");
        assert (b.length == 4 && b[2] == "echo \"hi there\"" && b[3] == "100%");
        var c = Data.split_exec ("prog \"\"");
        assert (c.length == 2 && c[1] == "");
    });

    Test.add_func ("/data/parse-session", () => {
        var s = Data.parse ("redstaros-wayland", "[Desktop Entry]\nName=Crimson Banner OS\nName[de]=X\nExec=redstaros-session --wayland\nDesktopNames=CrimsonBanner;GNOME;\n[Other]\nName=No\n", true, which_some);
        assert (s != null && s.name == "Crimson Banner OS" && s.exec[1] == "--wayland");
        assert (s.desktop_names.length == 2 && s.desktop_names[1] == "GNOME");
        assert (s.shown == "Crimson Banner OS");
        assert (Data.parse ("x", "[Desktop Entry]\nName=Hidden\nExec=sway\nHidden=true\n", true, which_some) == null);
        assert (Data.parse ("x", "[Desktop Entry]\nName=Missing\nExec=not-installed\n", true, which_some) == null);
        assert (Data.parse ("x", "[Desktop Entry]\nName=TryExec\nExec=sway\nTryExec=not-installed\n", true, which_some) == null);
        var x11 = Data.parse ("i3", "[Desktop Entry]\nName=i3\nExec=sway\n", false, which_some);
        assert (x11.shown == "i3 (X11)");
    });

    Test.add_func ("/data/sessions-from-folders", () => {
        try {
            var dir = DirUtils.make_tmp ("login-test-XXXXXX");
            var w = Path.build_filename (dir, "wayland"); var x = Path.build_filename (dir, "x");
            DirUtils.create (w, 0755); DirUtils.create (x, 0755);
            FileUtils.set_contents (Path.build_filename (w, "sway.desktop"), "[Desktop Entry]\nName=Sway\nExec=sway\n");
            FileUtils.set_contents (Path.build_filename (w, "redstaros-wayland.desktop"), "[Desktop Entry]\nName=Crimson Banner OS\nExec=redstaros-session --wayland\n");
            FileUtils.set_contents (Path.build_filename (x, "redstaros.desktop"), "[Desktop Entry]\nName=Crimson Banner OS\nExec=redstaros-session\n");
            var all = Data.sessions ({ w }, { x }, which_some);
            assert (all.length == 3);
            assert (all[0].id == "redstaros-wayland" && all[1].id == "sway" && !all[2].wayland);
            // without startx, no X11 desktops
            assert (Data.sessions ({ w }, { x }, (p) => p == "startx" ? null : which_some (p)).length == 2);
            assert (Data.sessions ({ w }, { x }, which_none).length == 0);
        } catch (Error e) { error ("%s", e.message); }
    });

    Test.add_func ("/data/command", () => {
        string[] cmd, env;
        var w = new LoginSession ("redstaros-wayland", "Crimson Banner OS", true, { "redstaros-session", "--wayland" }, { "CrimsonBanner" });
        Data.command (w, out cmd, out env);
        assert (string.joinv (" ", cmd) == "redstaros-session --wayland");
        assert ("XDG_SESSION_TYPE=wayland" in env && "XDG_CURRENT_DESKTOP=CrimsonBanner" in env && "DESKTOP_SESSION=redstaros-wayland" in env);
        var x = new LoginSession ("redstaros", "Crimson Banner OS", false, { "redstaros-session" }, {});
        Data.command (x, out cmd, out env);
        assert (string.joinv (" ", cmd) == "startx /usr/bin/env redstaros-session");
        assert ("XDG_SESSION_TYPE=x11" in env && env.length == 3);
    });

    Test.add_func ("/data/preferred", () => {
        var list = new GenericArray<LoginSession> ();
        list.add (new LoginSession ("sway", "Sway", true, { "sway" }, {}));
        list.add (new LoginSession ("redstaros-wayland", "Crimson Banner OS", true, { "redstaros-session" }, {}));
        list.add (new LoginSession ("redstaros", "Crimson Banner OS", false, { "redstaros-session" }, {}));
        assert (Data.preferred (list, "sway").id == "sway");
        assert (Data.preferred (list, null).id == "redstaros");
        assert (Data.preferred (list, "gone").id == "redstaros");
        list.remove_index (2);
        assert (Data.preferred (list, null).id == "redstaros-wayland");
        assert (Data.preferred (new GenericArray<LoginSession> (), null) == null);
    });

    Test.add_func ("/data/state", () => {
        try {
            var path = Path.build_filename (DirUtils.make_tmp ("login-state-XXXXXX"), "sub", "state.json");
            var s = new LoginState ();
            s.last_user = "rosa";
            s.sessions.insert ("rosa", "redstaros-wayland");
            s.save (path);
            var back = LoginState.load (path);
            assert (back.last_user == "rosa" && back.sessions.get ("rosa") == "redstaros-wayland");
            // the shell's login screen writes the same file
            FileUtils.set_contents (path, "{\"LastUser\":\"karl\",\"Sessions\":{\"karl\":\"sway\"}}");
            back = LoginState.load (path);
            assert (back.last_user == "karl" && back.sessions.get ("karl") == "sway");
            FileUtils.set_contents (path, "not json");
            assert (LoginState.load (path).last_user == null);
            assert (LoginState.load (path + ".missing").sessions.size () == 0);
        } catch (Error e) { error ("%s", e.message); }
    });

    Test.add_func ("/greetd/replies", () => {
        try {
            var r = GreetdReply.parse ("{\"type\":\"auth_message\",\"auth_message_type\":\"secret\",\"auth_message\":\"Password: \"}");
            assert (r.is_question && r.auth_message_type == "secret" && r.auth_message == "Password: ");
            r = GreetdReply.parse ("{\"type\":\"error\",\"error_type\":\"auth_error\",\"description\":\"no\"}");
            assert (r.is_error && r.is_auth_error && r.description == "no");
            assert (GreetdReply.parse ("{\"type\":\"success\"}").is_success);
            assert (GreetdReply.parse ("{\"error_type\":null}").is_error);   // no type: an error
        } catch (Error e) { error ("%s", e.message); }
        try { GreetdReply.parse ("[1,2]"); assert_not_reached (); } catch (Error e) { }
    });

    Test.add_func ("/greetd/frames", () => { frames.begin ((o, r) => { try { frames.end (r); done (null); } catch (Error e) { done (e); } }); run_loop (); });

    Test.add_func ("/greetd/over-a-socket", () => { over_socket.begin ((o, r) => { try { over_socket.end (r); done (null); } catch (Error e) { done (e); } }); run_loop (); });

    Test.add_func ("/flow/prompt", () => {
        assert (Flow.prompt ("Password: ", true) == "Password");
        assert (Flow.prompt ("Verification code:", false) == "Verification code");
        assert (Flow.prompt ("", true) == "Password");
        assert (Flow.prompt (null, false) == "Answer");
    });

    Test.add_func ("/flow/signing-in", () => { signing_in.begin ((o, r) => { try { signing_in.end (r); done (null); } catch (Error e) { done (e); } }); run_loop (); });

    Test.add_func ("/flow/messages-and-second-questions", () => { messages.begin ((o, r) => { try { messages.end (r); done (null); } catch (Error e) { done (e); } }); run_loop (); });

    Test.add_func ("/flow/demo", () => { demo_flow.begin ((o, r) => { try { demo_flow.end (r); done (null); } catch (Error e) { done (e); } }); run_loop (); });

    return Test.run ();
}
