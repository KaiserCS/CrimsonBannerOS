// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonLogin {

/// Someone who can sign in.
public class LoginUser : Object {
    public string name;
    public string real_name;
    public int uid;
    public string home;

    public LoginUser (string name, string real_name, int uid, string home) {
        this.name = name;
        this.real_name = real_name;
        this.uid = uid;
        this.home = home;
    }

    public string shown { get { return real_name.length > 0 ? real_name : name; } }

    /// Their picture: AccountsService's copy (readable by the login screen), else none.
    public string? avatar (string dir = "/var/lib/AccountsService/icons") {
        var p = Path.build_filename (dir, name);
        return FileUtils.test (p, FileTest.IS_REGULAR) ? p : null;
    }
}

/// A desktop that can be started (a .desktop file in wayland-sessions or xsessions).
public class LoginSession : Object {
    public string id;
    public string name;
    public bool wayland;
    public string[] exec;
    public string[] desktop_names;

    public LoginSession (string id, string name, bool wayland, string[] exec, string[] desktop_names) {
        this.id = id;
        this.name = name;
        this.wayland = wayland;
        this.exec = exec;
        this.desktop_names = desktop_names;
    }

    public string shown { owned get { return wayland ? name : name + " (X11)"; } }
}

public delegate string? Which (string program);

/// What the login screen remembers between starts: the last person, and each person's last desktop.
public class LoginState : Object {
    public string? last_user;
    public HashTable<string, string> sessions = new HashTable<string, string> (str_hash, str_equal);

    /// /var/cache/crimson-greeter/state.json (the login screen's own folder; packaging/greeter), or
    /// $CRIMSON_GREETER_STATE.
    public static string default_path () {
        var p = Environment.get_variable ("CRIMSON_GREETER_STATE");
        return p != null && p.length > 0 ? p : "/var/cache/crimson-greeter/state.json";
    }

    public static LoginState load (string path) {
        var s = new LoginState ();
        try {
            if (!FileUtils.test (path, FileTest.EXISTS)) return s;
            var p = new Json.Parser ();
            p.load_from_file (path);
            var root = p.get_root ();
            if (root == null || root.get_node_type () != Json.NodeType.OBJECT) return s;
            var o = root.get_object ();
            // the names are the shell's (GreeterData.State), so either login screen reads the other's file
            if (o.has_member ("LastUser") && o.get_member ("LastUser").get_value_type () == typeof (string))
                s.last_user = o.get_string_member ("LastUser");
            if (o.has_member ("Sessions") && o.get_member ("Sessions").get_node_type () == Json.NodeType.OBJECT) {
                var m = o.get_object_member ("Sessions");
                foreach (var k in m.get_members ())
                    if (m.get_member (k).get_value_type () == typeof (string)) s.sessions.insert (k, m.get_string_member (k));
            }
        } catch (Error e) { /* not remembering is fine */ }
        return s;
    }

    public void save (string path) {
        try {
            DirUtils.create_with_parents (Path.get_dirname (path), 0755);
            var b = new Json.Builder ();
            b.begin_object ();
            b.set_member_name ("LastUser");
            if (last_user == null) b.add_null_value (); else b.add_string_value (last_user);
            b.set_member_name ("Sessions");
            b.begin_object ();
            sessions.foreach ((k, v) => { b.set_member_name (k); b.add_string_value (v); });
            b.end_object ();
            b.end_object ();
            var g = new Json.Generator ();
            g.set_root (b.get_root ());
            var tmp = path + ".tmp";
            FileUtils.set_contents (tmp, g.to_data (null));
            FileUtils.rename (tmp, path);
        } catch (Error e) { /* not remembering is fine */ }
    }
}

/// What the login screen shows and starts: the people who can sign in (/etc/passwd), the desktops installed
/// (/usr/share/wayland-sessions and xsessions), and the command and environment greetd starts for a choice.
namespace Data {

    const string[] NO_LOGIN_SHELLS = { "nologin", "false", "sync", "shutdown", "halt", "git-shell" };

    public const string[] WAYLAND_DIRS = { "/usr/share/wayland-sessions", "/usr/local/share/wayland-sessions" };
    public const string[] X11_DIRS = { "/usr/share/xsessions", "/usr/local/share/xsessions" };

    /// /etc/passwd's ordinary accounts (UID_MIN to UID_MAX from login.defs), ordered by the name they go by.
    public GenericArray<LoginUser> users (string passwd, int uid_min = 1000, int uid_max = 60000) {
        var list = new GenericArray<LoginUser> ();
        var seen = new GenericSet<string> (str_hash, str_equal);
        foreach (var line in passwd.split ("\n")) {
            var f = line.split (":");
            if (f.length < 7) continue;
            int uid = 0;
            if (!int.try_parse (f[2], out uid)) continue;
            if (uid < uid_min || uid > uid_max) continue;
            var shell = f[6].strip ();
            if (shell.length == 0 || Path.get_basename (shell) in NO_LOGIN_SHELLS) continue;
            if (f[0] == "greeter" || f[0] == "nobody" || seen.contains (f[0])) continue;
            seen.add (f[0]);
            list.add (new LoginUser (f[0], f[4].split (",")[0].strip (), uid, f[5]));
        }
        list.sort ((a, b) => a.shown.casefold ().collate (b.shown.casefold ()));
        return list;
    }

    public void uid_range (string? login_defs, out int min, out int max) {
        min = 1000;
        max = 60000;
        foreach (var raw in (login_defs ?? "").split ("\n")) {
            var p = Regex.split_simple ("\\s+", raw.strip ());
            if (p.length < 2 || p[0].has_prefix ("#")) continue;
            int v = 0;
            if (p[0] == "UID_MIN" && int.try_parse (p[1], out v)) min = v;
            if (p[0] == "UID_MAX" && int.try_parse (p[1], out v)) max = v;
        }
    }

    public GenericArray<LoginUser> system_users () {
        string? defs = null;
        string passwd;
        try { FileUtils.get_contents ("/etc/login.defs", out defs); } catch (Error e) { defs = null; }
        int min, max;
        uid_range (defs, out min, out max);
        try { FileUtils.get_contents ("/etc/passwd", out passwd); } catch (Error e) { return new GenericArray<LoginUser> (); }
        return users (passwd, min, max);
    }

    /// The installed desktops: Wayland ones, and X11 ones when startx (xorg-xinit) is there to bring up X under greetd.
    public GenericArray<LoginSession> sessions (string[] wayland_dirs, string[] x11_dirs, Which which) {
        var list = new GenericArray<LoginSession> ();
        var seen = new GenericSet<string> (str_hash, str_equal);
        read_dirs (list, seen, wayland_dirs, true, which);
        if (which ("startx") != null) read_dirs (list, seen, x11_dirs, false, which);
        return list;
    }

    private void read_dirs (GenericArray<LoginSession> list, GenericSet<string> seen, string[] dirs, bool wayland, Which which) {
        foreach (var dir in dirs) {
            Dir d;
            try { d = Dir.open (dir); } catch (Error e) { continue; }
            var names = new GenericArray<string> ();
            string? n;
            while ((n = d.read_name ()) != null)
                if (n.has_suffix (".desktop")) names.add (n);
            names.sort (strcmp);
            for (int i = 0; i < names.length; i++) {
                var file = names[i];
                var id = file.substring (0, file.length - ".desktop".length);
                var key = (wayland ? "w:" : "x:") + id;
                if (seen.contains (key)) continue;
                seen.add (key);
                string text;
                try { FileUtils.get_contents (Path.build_filename (dir, file), out text); } catch (Error e) { continue; }
                var s = parse (id, text, wayland, which);
                if (s != null) list.add (s);
            }
        }
    }

    public LoginSession? parse (string id, string desktop_file, bool wayland, Which which) {
        var d = new HashTable<string, string> (str_hash, str_equal);
        bool in_entry = false;
        foreach (var raw in desktop_file.split ("\n")) {
            var l = raw.strip ();
            if (l.has_prefix ("[")) { in_entry = l == "[Desktop Entry]"; continue; }
            if (!in_entry || l.has_prefix ("#")) continue;
            var eq = l.index_of ("=");
            if (eq <= 0) continue;
            var key = l.substring (0, eq).strip ();
            if ("[" in key || d.contains (key)) continue;
            d.insert (key, l.substring (eq + 1).strip ());
        }
        if (d.get ("Hidden") == "true" || d.get ("NoDisplay") == "true") return null;
        var name = d.get ("Name");
        var exec = d.get ("Exec");
        if (name == null || exec == null) return null;
        var argv = split_exec (exec);
        if (argv.length == 0) return null;
        var try_exec = d.get ("TryExec");
        if (try_exec != null && try_exec.length > 0 && !exists (try_exec, which)) return null;
        if (!exists (argv[0], which)) return null;
        string[] names = {};
        foreach (var part in (d.get ("DesktopNames") ?? "").split (";"))
            if (part.strip ().length > 0) names += part.strip ();
        return new LoginSession (id, name, wayland, argv, names);
    }

    private bool exists (string program, Which which) {
        return program.has_prefix ("/") ? FileUtils.test (program, FileTest.EXISTS) : which (program) != null;
    }

    /// A .desktop Exec line as arguments: double quotes group, backslash escapes, field codes (%f, %U and so on) go.
    public string[] split_exec (string exec) {
        string[] args = {};
        var cur = new StringBuilder ();
        bool quoted = false, any = false;
        for (int i = 0; i < exec.length; i++) {
            char c = exec[i];
            if (quoted) {
                if (c == '\\' && i + 1 < exec.length) { cur.append_c (exec[++i]); continue; }
                if (c == '"') { quoted = false; continue; }
                cur.append_c (c);
            } else if (c == '"') {
                quoted = true;
                any = true;
            } else if (c == ' ' || c == '\t') {
                if (cur.len > 0 || any) { args += cur.str; cur.truncate (0); any = false; }
            } else if (c == '%' && i + 1 < exec.length) {
                if (exec[++i] == '%') cur.append_c ('%');
            } else {
                cur.append_c (c);
            }
        }
        if (cur.len > 0 || any) args += cur.str;
        return args;
    }

    /// What greetd starts for a desktop: Wayland ones as they are; X11 ones through startx, which brings up the X
    /// server on this console first. Plus the variables desktops look at to know what they are.
    public void command (LoginSession s, out string[] cmd, out string[] env) {
        env = {
            "XDG_SESSION_TYPE=" + (s.wayland ? "wayland" : "x11"),
            "XDG_SESSION_DESKTOP=" + s.id,
            "DESKTOP_SESSION=" + s.id,
        };
        if (s.desktop_names.length > 0) env += "XDG_CURRENT_DESKTOP=" + string.joinv (":", s.desktop_names);
        string[] c = {};
        if (!s.wayland) { c += "startx"; c += "/usr/bin/env"; }
        foreach (var a in s.exec) c += a;
        cmd = c;
    }

    /// The desktop to offer first: this person's last one, else Crimson Banner OS, else the first.
    public LoginSession? preferred (GenericArray<LoginSession> sessions, string? last) {
        for (int i = 0; i < sessions.length; i++) if (last != null && sessions[i].id == last) return sessions[i];
        for (int i = 0; i < sessions.length; i++) if (sessions[i].id == "redstaros") return sessions[i];
        for (int i = 0; i < sessions.length; i++) if (sessions[i].id.has_prefix ("redstaros")) return sessions[i];
        return sessions.length > 0 ? sessions[0] : null;
    }

    public string host_name () {
        try {
            string h;
            FileUtils.get_contents ("/etc/hostname", out h);
            if (h.strip ().length > 0) return h.strip ();
        } catch (Error e) { }
        return Environment.get_host_name ();
    }
}

}
