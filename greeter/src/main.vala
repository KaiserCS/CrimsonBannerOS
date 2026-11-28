// SPDX-License-Identifier: GPL-3.0-or-later
// crimson-login: Crimson Banner OS's login screen. greetd starts it (through crimson-greeter, packaging/greeter);
//   crimson-login --demo                 a preview on a running desktop: any password works, nothing is started
//   crimson-login --demo --screenshot F  the preview drawn to F (PNG) once it's on screen, then closed (CI's check)
using CrimsonLogin;

int main (string[] args) {
    bool demo = false;
    string? shot = null;
    for (int i = 1; i < args.length; i++) {
        if (args[i] == "--demo") demo = true;
        else if (args[i] == "--screenshot" && i + 1 < args.length) { shot = args[++i]; demo = true; }
        else if (args[i] == "--help" || args[i] == "-h") {
            print ("crimson-login: Crimson Banner OS's login screen, started by greetd.\n  --demo  a preview: any password works, nothing is started\n  --screenshot FILE  the preview, drawn to FILE\n");
            return 0;
        }
    }
    Greetd greetd;
    if (demo) {
        greetd = new DemoGreetd ();
    } else {
        var sock = GreetdClient.socket_path ();
        if (sock == null) {
            printerr ("crimson-login is started by greetd (GREETD_SOCK isn't set). To look at it: crimson-login --demo\n");
            return 2;
        }
        try {
            greetd = GreetdClient.connect_to (sock);
        } catch (Error e) {
            printerr ("crimson-login: can't reach greetd at %s: %s\n", sock, e.message);
            return 1;
        }
    }

    var app = new Gtk.Application ("org.redstaros.Login", ApplicationFlags.NON_UNIQUE);
    app.activate.connect (() => {
        Gtk.Settings.get_default ().gtk_application_prefer_dark_theme = true;
        var css = new Gtk.CssProvider ();
        css.load_from_string (LoginWindow.CSS);
        Gtk.StyleContext.add_provider_for_display (Gdk.Display.get_default (), css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION);
        var win = new LoginWindow (app, greetd, demo);
        win.present ();
        if (shot != null) Timeout.add (2500, () => { save_screenshot (win, shot); app.quit (); return Source.REMOVE; });
    });
    return app.run (new string[] { args[0] });
}

/// The window as it's drawn, to a PNG (for CI, which has no one to look at the screen).
void save_screenshot (Gtk.Window win, string file) {
    int w = win.get_width (), h = win.get_height ();
    var paintable = new Gtk.WidgetPaintable (win);
    var snap = new Gtk.Snapshot ();
    paintable.snapshot (snap, w, h);
    var node = snap.to_node ();
    if (node == null) { printerr ("crimson-login: nothing was drawn\n"); return; }
    var texture = win.get_renderer ().render_texture (node, null);
    if (!texture.save_to_png (file)) printerr ("crimson-login: couldn't write %s\n", file);
    else print ("crimson-login: drew %dx%d to %s\n", w, h, file);
}
