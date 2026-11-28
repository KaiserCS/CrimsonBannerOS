// SPDX-License-Identifier: GPL-3.0-or-later
namespace CrimsonLogin {

/// A picture cut to a circle, exactly `size` across whatever the picture's own size, and covering the circle as a
/// photo would (a Gtk.Picture grows to its picture's size, which made a large photo fill the card).
public class RoundPicture : Gtk.Widget {
    private Gdk.Paintable paintable;
    private int size;

    public RoundPicture (Gdk.Paintable paintable, int size) {
        this.paintable = paintable;
        this.size = size;
        halign = Gtk.Align.CENTER;
        valign = Gtk.Align.CENTER;
    }

    public override Gtk.SizeRequestMode get_request_mode () {
        return Gtk.SizeRequestMode.CONSTANT_SIZE;
    }

    public override void measure (Gtk.Orientation orientation, int for_size, out int minimum, out int natural,
                                  out int minimum_baseline, out int natural_baseline) {
        minimum = size;
        natural = size;
        minimum_baseline = -1;
        natural_baseline = -1;
    }

    public override void snapshot (Gtk.Snapshot s) {
        float w = get_width (), h = get_height ();
        double iw = paintable.get_intrinsic_width (), ih = paintable.get_intrinsic_height ();
        if (iw <= 0 || ih <= 0) { iw = w; ih = h; }
        double scale = double.max (w / iw, h / ih);
        double dw = iw * scale, dh = ih * scale;
        var bounds = Graphene.Rect ();
        bounds.init (0, 0, w, h);
        var clip = Gsk.RoundedRect ();
        clip.init_from_rect (bounds, float.min (w, h) / 2);
        s.push_rounded_clip (clip);
        s.save ();
        var at = Graphene.Point ();
        at.init ((float) ((w - dw) / 2), (float) ((h - dh) / 2));
        s.translate (at);
        paintable.snapshot (s, dw, dh);
        s.restore ();
        s.pop ();
    }
}

/// Crimson Banner OS's login screen (crimson-login, started by greetd through crimson-greeter; packaging/greeter).
/// It looks like the lock screen: the clock, then a card with the person, their password and the desktop to start.
/// Several people: their pictures above the card, to pick from. Shut down, restart and sleep sit at the bottom.
public class LoginWindow : Gtk.ApplicationWindow {
    private Greetd greetd;
    private Flow flow;
    private bool demo;
    private GenericArray<LoginUser> users;
    private GenericArray<LoginSession> sessions;
    private LoginState state;
    private string state_path;

    private LoginUser? user;
    private bool busy;
    private bool question_open;

    private Gtk.Label clock = new Gtk.Label ("");
    private Gtk.Label date = new Gtk.Label ("");
    private Gtk.Box people = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 14);
    private Gtk.Box avatar = new Gtk.Box (Gtk.Orientation.VERTICAL, 0);
    private Gtk.Label name_label = new Gtk.Label ("");
    private Gtk.Entry user_box = new Gtk.Entry ();
    private Gtk.Entry answer = new Gtk.Entry ();
    private Gtk.Label message = new Gtk.Label ("");
    private Gtk.Label notes = new Gtk.Label ("");
    private Gtk.Button sign_in = new Gtk.Button.with_label ("Sign in");
    private Gtk.DropDown session;
    private Gtk.Box card = new Gtk.Box (Gtk.Orientation.VERTICAL, 12);

    public LoginWindow (Gtk.Application app, Greetd greetd, bool demo) {
        Object (application: app, title: "Crimson Banner OS");
        this.greetd = greetd;
        this.flow = new Flow (greetd);
        this.demo = demo;
        state_path = demo ? Path.build_filename (Environment.get_tmp_dir (), "crimson-login-demo.json") : LoginState.default_path ();
        state = LoginState.load (state_path);
        users = Data.system_users ();
        if (demo && users.length == 0)
            users.add (new LoginUser (Environment.get_user_name (), Environment.get_real_name () == "Unknown" ? "" : Environment.get_real_name (), 1000, Environment.get_home_dir ()));
        sessions = Data.sessions (Data.WAYLAND_DIRS, Data.X11_DIRS, (p) => Environment.find_program_in_path (p));

        add_css_class ("login");
        decorated = false;
        if (demo) set_default_size (1100, 720);
        else fullscreen ();

        // the picture, darkened; everything else floats over it
        var root = new Gtk.Overlay ();
        var picture = new Gtk.Picture.for_resource ("/org/redstaros/login/login-bg.jpg");
        picture.content_fit = Gtk.ContentFit.COVER;
        picture.can_shrink = true;
        root.child = picture;
        var scrim = new Gtk.Box (Gtk.Orientation.VERTICAL, 0);
        scrim.add_css_class ("scrim");
        scrim.hexpand = true;
        scrim.vexpand = true;
        root.add_overlay (scrim);

        var brand = new Gtk.Label ("Crimson Banner OS" + (demo ? " · preview: any password works, nothing is started" : "") + "\n" + Data.host_name ());
        brand.add_css_class ("host");
        brand.halign = Gtk.Align.START;
        brand.valign = Gtk.Align.START;
        brand.xalign = 0;
        brand.margin_start = 28;
        brand.margin_top = 28;
        root.add_overlay (brand);

        // the card
        avatar.add_css_class ("avatar");
        avatar.halign = Gtk.Align.CENTER;
        avatar.overflow = Gtk.Overflow.HIDDEN;
        name_label.add_css_class ("name");
        user_box.placeholder_text = "User name";
        user_box.visible = false;
        answer.placeholder_text = "Password";
        answer.visibility = false;
        message.add_css_class ("error");
        notes.add_css_class ("notes");
        foreach (var l in new Gtk.Label[] { message, notes }) {
            l.wrap = true;
            l.justify = Gtk.Justification.CENTER;
            l.max_width_chars = 40;
            l.visible = false;
        }
        sign_in.add_css_class ("primary");
        sign_in.add_css_class ("signin");
        sign_in.clicked.connect (() => submit.begin ());
        answer.activate.connect (() => submit.begin ());
        user_box.activate.connect (() => answer.grab_focus ());

        card.add_css_class ("login-card");
        card.width_request = 360;
        card.halign = Gtk.Align.CENTER;
        card.append (avatar);
        card.append (name_label);
        card.append (user_box);
        card.append (answer);
        card.append (message);
        card.append (notes);
        card.append (sign_in);

        clock.add_css_class ("clock");
        date.add_css_class ("date");
        people.halign = Gtk.Align.CENTER;
        var time = new Gtk.Box (Gtk.Orientation.VERTICAL, 0);
        time.append (clock);
        time.append (date);
        var middle = new Gtk.Box (Gtk.Orientation.VERTICAL, 26);
        middle.halign = Gtk.Align.CENTER;
        middle.valign = Gtk.Align.CENTER;
        middle.append (time);
        middle.append (people);
        middle.append (card);
        root.add_overlay (middle);

        // the desktop to start, bottom left; power, bottom right
        string[] names = {};
        for (int i = 0; i < sessions.length; i++) names += sessions[i].shown;
        session = new Gtk.DropDown.from_strings (names);
        var desktop = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 10);
        desktop.add_css_class ("bottom");
        desktop.halign = Gtk.Align.START;
        desktop.valign = Gtk.Align.END;
        desktop.margin_start = 28;
        desktop.margin_bottom = 28;
        var dl = new Gtk.Label ("Desktop");
        dl.add_css_class ("dim");
        desktop.append (dl);
        if (sessions.length > 0) desktop.append (session);
        else desktop.append (new Gtk.Label ("No desktops are installed."));
        root.add_overlay (desktop);

        var power = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 6);
        power.add_css_class ("bottom");
        power.halign = Gtk.Align.END;
        power.valign = Gtk.Align.END;
        power.margin_end = 28;
        power.margin_bottom = 28;
        power.append (power_button ("Sleep", "suspend"));
        power.append (power_button ("Restart", "reboot"));
        power.append (power_button ("Shut down", "poweroff"));
        root.add_overlay (power);

        child = root;

        build_people ();
        LoginUser? first = null;
        for (int i = 0; i < users.length; i++) if (users[i].name == state.last_user) first = users[i];
        if (first == null && users.length > 0) first = users[0];
        choose (first);

        update_clock ();
        Timeout.add_seconds (1, () => { update_clock (); return Source.CONTINUE; });

        var keys = new Gtk.EventControllerKey ();
        keys.key_pressed.connect ((keyval, keycode, mods) => {
            if (keyval != Gdk.Key.Escape) return false;
            answer.text = "";
            flow.cancel.begin ();
            question_open = false;
            ready ();
            return true;
        });
        ((Gtk.Widget) this).add_controller (keys);
    }

    private void update_clock () {
        var now = new DateTime.now_local ();
        clock.label = now.format ("%H:%M");
        date.label = now.format ("%A, %-d %B");
    }

    // ── people ──

    private void build_people () {
        Gtk.Widget? c;
        while ((c = people.get_first_child ()) != null) people.remove (c);
        people.visible = users.length != 1;
        if (users.length == 1) return;
        for (int i = 0; i < users.length && i < 8; i++) {
            var u = users[i];
            people.append (person_tile (u.shown, picture_for (u, 56), () => choose (u)));
        }
        people.append (person_tile ("Someone else", letter ("+", 56), () => choose (null)));
    }

    private delegate void Pick ();

    private Gtk.Button person_tile (string label, Gtk.Widget picture, owned Pick pick) {
        var b = new Gtk.Button ();
        b.add_css_class ("flat");
        b.add_css_class ("person");
        var box = new Gtk.Box (Gtk.Orientation.VERTICAL, 6);
        box.width_request = 92;
        picture.halign = Gtk.Align.CENTER;
        box.append (picture);
        var l = new Gtk.Label (label);
        l.ellipsize = Pango.EllipsizeMode.END;
        l.max_width_chars = 12;
        box.append (l);
        b.child = box;
        b.clicked.connect (() => pick ());
        return b;
    }

    private void choose (LoginUser? u) {
        flow.cancel.begin ();
        question_open = false;
        user = u;
        Gtk.Widget? c;
        while ((c = avatar.get_first_child ()) != null) avatar.remove (c);
        avatar.append (u == null ? letter ("?", 96) : picture_for (u, 96));
        name_label.label = u != null ? u.shown : "";
        name_label.visible = u != null;
        user_box.visible = u == null;
        user_box.text = "";
        answer.text = "";
        message.visible = false;
        notes.visible = false;
        ready ();
        string? last = u != null ? state.sessions.get (u.name) : null;
        var pick = Data.preferred (sessions, last);
        if (pick != null)
            for (int i = 0; i < sessions.length; i++) if (sessions[i] == pick) session.selected = (uint) i;
        Idle.add (() => { (u == null ? (Gtk.Widget) user_box : (Gtk.Widget) answer).grab_focus (); return Source.REMOVE; });
    }

    private Gtk.Widget picture_for (LoginUser u, int size) {
        var path = u.avatar ();
        if (path != null) {
            try {
                return new RoundPicture (Gdk.Texture.from_filename (path), size);
            } catch (Error e) { /* the letter instead */ }
        }
        return letter (u.shown.length > 0 ? u.shown.get_char (0).toupper ().to_string () : "?", size);
    }

    private static Gtk.Widget letter (string text, int size) {
        var l = new Gtk.Label (text);
        l.add_css_class ("letter");
        l.add_css_class (size >= 80 ? "big" : "small");
        l.set_size_request (size, size);
        l.halign = Gtk.Align.CENTER;
        return l;
    }

    // ── signing in ──

    private async void submit () {
        if (busy) return;
        var name = user != null ? user.name : user_box.text.strip ();
        if (name.length == 0) { user_box.grab_focus (); return; }
        if (sessions.length == 0 || session.selected >= (uint) sessions.length) { fail ("Choose a desktop to start first."); return; }
        busy = true;
        set_busy (true);
        try {
            FlowStep step;
            if (!question_open) {
                step = yield flow.sign_in (name);
                // the first question is nearly always the password, which has just been typed
                if (step.kind == StepKind.QUESTION) step = yield flow.answer (answer.text);
            } else {
                step = yield flow.answer (answer.text);
            }
            yield handle (step, name);
        } catch (Error e) {
            question_open = false;
            fail ("The login service didn't answer: " + e.message);
        }
        busy = false;
        set_busy (false);
    }

    private async void handle (FlowStep step, string name) throws Error {
        notes.label = string.joinv ("\n", step.notes);
        notes.visible = notes.label.length > 0;
        switch (step.kind) {
        case StepKind.QUESTION:
            // a further question (a second factor, a new password): asked in the same box
            question_open = true;
            answer.text = "";
            answer.visibility = !step.secret;
            answer.placeholder_text = step.message;
            message.visible = false;
            answer.grab_focus ();
            break;
        case StepKind.AUTHENTICATED:
            var chosen = sessions[(int) session.selected];
            string[] cmd, env;
            Data.command (chosen, out cmd, out env);
            var started = yield flow.start (cmd, env);
            if (started.kind != StepKind.STARTED) { question_open = false; fail (started.message ?? ""); return; }
            state.last_user = name;
            state.sessions.insert (name, chosen.id);
            state.save (state_path);
            if (demo) {
                question_open = false;
                ready ();
                notes.label = "Preview: %s would now get %s (%s).".printf (name, chosen.name, string.joinv (" ", cmd));
                notes.visible = true;
                return;
            }
            // greetd starts the desktop once the login screen has gone
            close ();
            break;
        case StepKind.FAILED:
            question_open = false;
            fail (step.message ?? "Signing in didn't work.");
            break;
        default:
            break;
        }
    }

    private void ready () {
        answer.visibility = false;
        answer.placeholder_text = "Password";
        message.visible = false;
    }

    private void fail (string why) {
        message.label = why;
        message.visible = true;
        answer.text = "";
        ready ();
        message.visible = true;
        answer.grab_focus ();
        // a shake, as the lock screen does
        int[] offsets = { -14, 14, -10, 10, -5, 5, 0 };
        int step = 0;
        Timeout.add (35, () => {
            int o = offsets[step++];
            card.margin_start = o > 0 ? o * 2 : 0;
            card.margin_end = o < 0 ? -o * 2 : 0;
            return step < offsets.length;
        });
    }

    private void set_busy (bool on) {
        answer.sensitive = !on;
        user_box.sensitive = !on;
        sign_in.sensitive = !on;
        sign_in.label = on ? "Signing in…" : "Sign in";
    }

    // ── power ──

    private Gtk.Button power_button (string label, string verb) {
        var b = new Gtk.Button.with_label (label);
        b.add_css_class ("flat");
        b.add_css_class ("power");
        b.clicked.connect (() => power.begin (label, verb));
        return b;
    }

    private async void power (string label, string verb) {
        if (demo) {
            notes.label = "Preview: this would %s the computer.".printf (label.down ());
            notes.visible = true;
            return;
        }
        try {
            var p = new Subprocess.newv ({ "systemctl", verb }, SubprocessFlags.STDOUT_PIPE | SubprocessFlags.STDERR_MERGE);
            string output, errors;
            yield p.communicate_utf8_async (null, null, out output, out errors);
            if (p.get_exit_status () != 0) {
                var lines = (output ?? "").strip ().split ("\n");
                fail ("Couldn't %s: %s".printf (label.down (), lines.length > 0 ? lines[lines.length - 1] : ""));
            }
        } catch (Error e) {
            fail ("Couldn't %s: %s".printf (label.down (), e.message));
        }
    }

    /// What only the login screen has; everything else is Crimson Banner's GTK theme (crimson-gtk).
    public const string CSS = """
window.login { background-image: none; background-color: #1A0A0C; color: #FFFFFF; }
.scrim { background-color: alpha(black, 0.44); }
.host { opacity: 0.75; font-size: 13px; }
.clock { font-size: 88px; font-weight: 300; }
.date { opacity: 0.85; font-size: 18px; }
.login-card { background-color: rgba(20, 20, 24, 0.78); border: 1px solid rgba(255, 255, 255, 0.18); border-radius: 20px; padding: 26px 28px; }
.login-card .name { font-size: 20px; font-weight: 600; }
.login-card entry { min-height: 40px; border-radius: 10px; }
.login-card .error { color: #FF8A8A; font-size: 13px; }
.login-card .notes { opacity: 0.8; font-size: 12px; }
.avatar { border-radius: 9999px; border: 2px solid rgba(255, 255, 255, 0.25); }
.letter { background-color: #C8102E; color: #FFFFFF; border-radius: 9999px; font-weight: 600; }
.letter.big { font-size: 40px; }
.letter.small { font-size: 24px; }
button.signin { min-height: 26px; border-radius: 10px; }
button.person, button.power { color: #FFFFFF; }
button.person { padding: 8px; border-radius: 14px; font-size: 12px; }
.bottom .dim { opacity: 0.8; }
""";
}

}
