// SPDX-License-Identifier: GPL-3.0-or-later
// Crimson Banner's look for GTK 4 programs: the shell's colour tokens and components (RedStarOS/Styles/Shell.axaml,
// DESIGN.md) as GTK CSS, and the common parts built in code under the same names as the shell's Deck helpers. Every
// program that leaves .NET and Avalonia uses it, so the system stays one piece as it moves (ROADMAP, 5).
//
//   Crimson.Theme.apply ();                  the person's theme and accent, as the shell's settings say
//   Crimson.Theme.apply_mode (Crimson.Mode.DARK, "#C8102E");   a fixed one (the login screen)
//   Crimson.kicker ("SECTION"), Crimson.pill ("Save", true), Crimson.panel (), Crimson.lcd (…)
namespace Crimson {

public enum Mode { DARK, LIGHT }

public class Theme : Object {
    public const string DEFAULT_ACCENT = "#C8102E";
    /// The brand mark's colour, whatever the accent.
    public const string BRAND = "#C8102E";
    public const string DANGER = "#E81123";

    private static Gtk.CssProvider? provider;

    /// The shell's own choice (~/.config/redstaros/settings.json): Light, or Dark (also for System, as the shell's
    /// default is dark), and the accent. Anything missing or unreadable: dark and crimson.
    public static void from_settings (out Mode mode, out string accent) {
        mode = Mode.DARK;
        accent = DEFAULT_ACCENT;
        var path = Path.build_filename (Environment.get_user_config_dir (), "redstaros", "settings.json");
        try {
            var p = new Json.Parser ();
            p.load_from_file (path);
            var root = p.get_root ();
            if (root == null || root.get_node_type () != Json.NodeType.OBJECT) return;
            var o = root.get_object ();
            if (o.get_string_member_with_default ("Theme", "") == "Light") mode = Mode.LIGHT;
            var a = o.get_string_member_with_default ("Accent", "");
            if (is_colour (a)) accent = a;
        } catch (Error e) { /* the defaults */ }
    }

    public static bool is_colour (string text) {
        var c = Gdk.RGBA ();
        return text.length > 0 && c.parse (text);
    }

    /// The whole style sheet for a theme and an accent: the tokens, the accent and what's made from it, then the
    /// components. Public for the tests, which check GTK reads every line of it.
    public static string css_for (Mode mode, string accent) {
        var a = is_colour (accent) ? accent : DEFAULT_ACCENT;
        var sb = new StringBuilder ();
        sb.append (resource (mode == Mode.DARK ? "dark.css" : "light.css"));
        sb.append ("\n@define-color accent %s;\n".printf (a));
        sb.append ("@define-color accent_soft alpha(@accent, 0.23);\n");
        // small text in the accent: lightened on dark, where the accent itself is under 3:1 (DESIGN.md)
        sb.append (mode == Mode.DARK ? "@define-color accent_text mix(@accent, #FFFFFF, 0.45);\n" : "@define-color accent_text @accent;\n");
        sb.append ("@define-color brand %s;\n@define-color danger %s;\n".printf (BRAND, DANGER));
        sb.append (resource ("base.css"));
        return sb.str;
    }

    private static string resource (string name) {
        try {
            var bytes = resources_lookup_data ("/org/redstaros/crimson-gtk/" + name, ResourceLookupFlags.NONE);
            var sb = new StringBuilder.sized (bytes.get_size () + 1);
            sb.append_len ((string) bytes.get_data (), (ssize_t) bytes.get_size ());
            return sb.str;
        } catch (Error e) {
            critical ("crimson-gtk: %s is missing: %s", name, e.message);
            return "";
        }
    }

    /// The person's theme and accent, on every window of this program.
    public static void apply () {
        Mode mode;
        string accent;
        from_settings (out mode, out accent);
        apply_mode (mode, accent);
    }

    public static void apply_mode (Mode mode, string accent = DEFAULT_ACCENT) {
        var display = Gdk.Display.get_default ();
        if (display == null) return;
        if (provider != null) Gtk.StyleContext.remove_provider_for_display (display, provider);
        provider = new Gtk.CssProvider ();
        provider.load_from_string (css_for (mode, accent));
        Gtk.StyleContext.add_provider_for_display (display, provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION);
        // GTK's own pieces in the same light: GTK 4.20's colour scheme where it's there, else the older switch
        var settings = Gtk.Settings.get_default ();
        if (settings == null) return;
        if (settings.get_class ().find_property ("gtk-interface-color-scheme") != null)
            settings.set ("gtk-interface-color-scheme", mode == Mode.DARK ? 2 : 3);   // DARK, LIGHT
        else
            settings.gtk_application_prefer_dark_theme = mode == Mode.DARK;
    }
}

// ── the common parts, as the shell's Deck builds them ──

/// A section's label: small spaced capitals (written in capitals here, as the shell does).
public Gtk.Label kicker (string text, bool accent = false) {
    var l = new Gtk.Label (text.up ());
    l.add_css_class ("kicker");
    if (accent) l.add_css_class ("accent");
    l.xalign = 0;
    l.wrap = true;
    return l;
}

/// Secondary text: muted, wrapped, left-aligned.
public Gtk.Label muted (string text) {
    var l = new Gtk.Label (text);
    l.add_css_class ("muted");
    l.xalign = 0;
    l.wrap = true;
    l.max_width_chars = 80;
    return l;
}

/// A page's title.
public Gtk.Label display (string text) {
    var l = new Gtk.Label (text);
    l.add_css_class ("display");
    l.xalign = 0;
    l.wrap = true;
    return l;
}

/// A panel lifted off the backdrop, holding what's appended to it.
public Gtk.Box panel (Gtk.Orientation orientation = Gtk.Orientation.VERTICAL, int spacing = 10) {
    var b = new Gtk.Box (orientation, spacing);
    b.add_css_class ("panel");
    return b;
}

/// A pill button; accent: the one filled with the accent. Its label in capitals, as the shell's.
public Gtk.Button pill (string text, bool accent = false) {
    var b = new Gtk.Button.with_label (text.up ());
    b.add_css_class ("pill");
    if (accent) b.add_css_class ("accent");
    return b;
}

/// A state word in a small pill.
public Gtk.Label badge (string text, bool accent = false) {
    var l = new Gtk.Label (text.up ());
    l.add_css_class ("badge");
    if (accent) l.add_css_class ("accent");
    l.halign = Gtk.Align.START;
    l.valign = Gtk.Align.CENTER;
    return l;
}

/// Figures that matter, in the display's red digits: labels[i] above values[i].
public Gtk.Box lcd (string[] labels, string[] values) {
    var row = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 28);
    row.add_css_class ("lcd");
    row.halign = Gtk.Align.START;
    for (int i = 0; i < labels.length && i < values.length; i++) {
        var col = new Gtk.Box (Gtk.Orientation.VERTICAL, 1);
        var l = new Gtk.Label (labels[i].up ());
        l.add_css_class ("lcd-label");
        l.xalign = 0;
        var v = new Gtk.Label (values[i]);
        v.add_css_class ("lcd-digits");
        v.xalign = 0;
        col.append (l);
        col.append (v);
        row.append (col);
    }
    return row;
}

/// A sidebar's place; add the class "on" to the chosen one.
public Gtk.Button nav (string text) {
    var b = new Gtk.Button ();
    b.add_css_class ("nav");
    var l = new Gtk.Label (text);
    l.xalign = 0;
    b.child = l;
    return b;
}

/// The top of a small app: its picture at 30, its name, a line under it; the app's own actions go in `actions`.
public Gtk.Box app_header (Gdk.Paintable? picture, string name, string? subtitle, out Gtk.Box actions) {
    var row = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 12);
    row.add_css_class ("app-header");
    if (picture != null) {
        var img = new Gtk.Image.from_paintable (picture);
        img.pixel_size = 30;
        img.valign = Gtk.Align.CENTER;
        row.append (img);
    }
    var words = new Gtk.Box (Gtk.Orientation.VERTICAL, 0);
    words.valign = Gtk.Align.CENTER;
    words.hexpand = true;
    var n = new Gtk.Label (name);
    n.add_css_class ("app-name");
    n.xalign = 0;
    words.append (n);
    if (subtitle != null) {
        var s = new Gtk.Label (subtitle);
        s.add_css_class ("caption");
        s.xalign = 0;
        words.append (s);
    }
    row.append (words);
    actions = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 8);
    actions.valign = Gtk.Align.CENTER;
    row.append (actions);
    return row;
}

/// An empty list or preview: what goes here, and how to put it there.
public Gtk.Box empty_state (string icon_name, string title, string body) {
    var b = new Gtk.Box (Gtk.Orientation.VERTICAL, 6);
    b.add_css_class ("empty-state");
    b.halign = Gtk.Align.CENTER;
    b.valign = Gtk.Align.CENTER;
    var img = new Gtk.Image.from_icon_name (icon_name);
    img.pixel_size = 26;
    b.append (img);
    var t = new Gtk.Label (title);
    t.add_css_class ("title");
    b.append (t);
    var c = new Gtk.Label (body);
    c.add_css_class ("caption");
    c.wrap = true;
    c.justify = Gtk.Justification.CENTER;
    c.max_width_chars = 44;
    b.append (c);
    return b;
}

/// The window as it's drawn, to a PNG (CI has no one to look at the screen).
public bool save_screenshot (Gtk.Window win, string file) {
    int w = win.get_width (), h = win.get_height ();
    var paintable = new Gtk.WidgetPaintable (win);
    var snap = new Gtk.Snapshot ();
    paintable.snapshot (snap, w, h);
    var node = snap.to_node ();
    if (node == null) { printerr ("crimson-gtk: nothing was drawn\n"); return false; }
    var texture = win.get_renderer ().render_texture (node, null);
    if (!texture.save_to_png (file)) { printerr ("crimson-gtk: couldn't write %s\n", file); return false; }
    print ("drew %dx%d to %s\n", w, h, file);
    return true;
}

}
