// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonLogin {

/// What greetd answered: a question to ask, success, or an error.
public class GreetdReply : Object {
    public string kind { get; construct; }
    public string? auth_message_type { get; construct; }
    public string? auth_message { get; construct; }
    public string? error_type { get; construct; }
    public string? description { get; construct; }

    public GreetdReply (string kind, string? auth_message_type = null, string? auth_message = null,
                        string? error_type = null, string? description = null) {
        Object (kind: kind, auth_message_type: auth_message_type, auth_message: auth_message,
                error_type: error_type, description: description);
    }

    public bool is_success { get { return kind == "success"; } }
    public bool is_error { get { return kind == "error"; } }
    public bool is_question { get { return kind == "auth_message"; } }
    /// A wrong password (or another refusal from PAM), as opposed to something broken.
    public bool is_auth_error { get { return is_error && error_type == "auth_error"; } }

    public static GreetdReply parse (string json) throws Error {
        var p = new Json.Parser ();
        p.load_from_data (json, -1);
        var root = p.get_root ();
        if (root == null || root.get_node_type () != Json.NodeType.OBJECT)
            throw new IOError.INVALID_DATA ("greetd sent something that isn't a JSON object");
        var o = root.get_object ();
        return new GreetdReply (text (o, "type") ?? "error", text (o, "auth_message_type"), text (o, "auth_message"),
                                text (o, "error_type"), text (o, "description"));
    }

    private static string? text (Json.Object o, string name) {
        if (!o.has_member (name)) return null;
        var n = o.get_member (name);
        if (n.get_node_type () != Json.NodeType.VALUE || n.get_value_type () != typeof (string)) return null;
        return n.get_string ();
    }
}

/// What the login screen needs from greetd (or, for a preview, from DemoGreetd).
public interface Greetd : Object {
    public abstract async GreetdReply create_session (string user) throws Error;
    /// The answer to the last question; null for an informational message, which only needs acknowledging.
    public abstract async GreetdReply respond (string? response) throws Error;
    public abstract async GreetdReply start_session (string[] cmd, string[] env) throws Error;
    public abstract async GreetdReply cancel_session () throws Error;
}

/// greetd's IPC (man 7 greetd-ipc): over the socket in $GREETD_SOCK, each message is a 32-bit length in the
/// machine's byte order followed by that much JSON. The login screen creates a session for a user, answers the
/// questions PAM asks through it (the password, usually), then asks greetd to start the chosen desktop.
public class GreetdClient : Object, Greetd {
    private SocketConnection conn;

    public GreetdClient (SocketConnection conn) {
        this.conn = conn;
    }

    public static string? socket_path () {
        var p = Environment.get_variable ("GREETD_SOCK");
        return p != null && p.length > 0 ? p : null;
    }

    public static GreetdClient connect_to (string path) throws Error {
        var client = new SocketClient ();
        return new GreetdClient (client.connect (new UnixSocketAddress (path)));
    }

    public async GreetdReply create_session (string user) throws Error {
        var b = request ("create_session");
        b.set_member_name ("username");
        b.add_string_value (user);
        return yield send (b);
    }

    public async GreetdReply respond (string? response) throws Error {
        var b = request ("post_auth_message_response");
        b.set_member_name ("response");
        if (response == null) b.add_null_value (); else b.add_string_value (response);
        return yield send (b);
    }

    public async GreetdReply start_session (string[] cmd, string[] env) throws Error {
        var b = request ("start_session");
        b.set_member_name ("cmd");
        b.begin_array ();
        foreach (var a in cmd) b.add_string_value (a);
        b.end_array ();
        b.set_member_name ("env");
        b.begin_array ();
        foreach (var a in env) b.add_string_value (a);
        b.end_array ();
        return yield send (b);
    }

    public async GreetdReply cancel_session () throws Error {
        return yield send (request ("cancel_session"));
    }

    private static Json.Builder request (string type) {
        var b = new Json.Builder ();
        b.begin_object ();
        b.set_member_name ("type");
        b.add_string_value (type);
        return b;
    }

    private async GreetdReply send (Json.Builder b) throws Error {
        b.end_object ();
        var g = new Json.Generator ();
        g.set_root (b.get_root ());
        yield write_frame (conn.output_stream, g.to_data (null));
        return GreetdReply.parse (yield read_frame (conn.input_stream));
    }

    public static async void write_frame (OutputStream s, string json) throws Error {
        uint32 n = (uint32) json.length;
        var len = new uint8[4];
        Memory.copy (len, &n, 4);      // the machine's own byte order, as greetd reads it
        size_t written;
        yield s.write_all_async (len, Priority.DEFAULT, null, out written);
        yield s.write_all_async (json.data, Priority.DEFAULT, null, out written);
        yield s.flush_async (Priority.DEFAULT, null);
    }

    public static async string read_frame (InputStream s) throws Error {
        var len = new uint8[4];
        size_t got;
        yield s.read_all_async (len, Priority.DEFAULT, null, out got);
        if (got != 4) throw new IOError.CLOSED ("greetd closed the connection");
        uint32 n = 0;
        Memory.copy (&n, len, 4);
        if (n > (1 << 20)) throw new IOError.INVALID_DATA ("greetd sent a message that's far too long");
        var body = new uint8[n];
        yield s.read_all_async (body, Priority.DEFAULT, null, out got);
        if (got != n) throw new IOError.CLOSED ("greetd closed the connection mid-message");
        var sb = new StringBuilder.sized (n + 1);
        sb.append_len ((string) body, n);
        return sb.str;
    }
}

/// greetd's stand-in for previewing the login screen on a running desktop (crimson-login --demo): any password but
/// an empty one is accepted, and nothing is started.
public class DemoGreetd : Object, Greetd {
    private bool asked;
    public string[] started = {};

    public async GreetdReply create_session (string user) throws Error {
        asked = true;
        return new GreetdReply ("auth_message", "secret", "Password: ");
    }

    public async GreetdReply respond (string? response) throws Error {
        Timeout.add (400, respond.callback);   // about as long as PAM takes
        yield;
        if (!asked) return new GreetdReply ("error", null, null, "error", "no session");
        asked = false;
        return response == null || response == ""
            ? new GreetdReply ("error", null, null, "auth_error", "Authentication failure")
            : new GreetdReply ("success");
    }

    public async GreetdReply start_session (string[] cmd, string[] env) throws Error {
        started += string.joinv (" ", cmd);
        return new GreetdReply ("success");
    }

    public async GreetdReply cancel_session () throws Error {
        asked = false;
        return new GreetdReply ("success");
    }
}

}
