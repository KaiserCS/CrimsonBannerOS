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
        // GTK 4.16 and later colour their own pieces from these variables (GTK's blue, otherwise)
        sb.append (":root { --accent-bg-color: %s; --accent-fg-color: #FFFFFF; --accent-color: %s; }\n".printf (
            a, mode == Mode.DARK ? "mix(%s, #FFFFFF, 0.45)".printf (a) : a));
        sb.append (resource ("base.css"));
        return sb.str;
    }

    /// The same for GTK 3 programs: the tokens, the accent, and the GTK 3 components (no CSS variables there).
    public static string css_for_gtk3 (Mode mode, string accent) {
        var a = is_colour (accent) ? accent : DEFAULT_ACCENT;
        var sb = new StringBuilder ();
        sb.append (resource (mode == Mode.DARK ? "dark.css" : "light.css"));
        sb.append ("\n@define-color accent %s;\n".printf (a));
        sb.append ("@define-color accent_soft alpha(@accent, 0.23);\n");
        sb.append ("@define-color brand %s;\n@define-color danger %s;\n".printf (BRAND, DANGER));
        sb.append (resource ("base-gtk3.css"));
        return sb.str;
    }

    /// The colour tokens of a theme file (dark.css or light.css), by name.
    private static HashTable<string, string> tokens (Mode mode) {
        var map = new HashTable<string, string> (str_hash, str_equal);
        try {
            var re = new Regex ("@define-color\\s+([a-z_]+)\\s+([^;]+);");
            MatchInfo m;
            if (re.match (resource (mode == Mode.DARK ? "dark.css" : "light.css"), 0, out m))
                do { map.insert (m.fetch (1), m.fetch (2).strip ()); } while (m.next ());
        } catch (RegexError e) { critical ("crimson-gtk: %s", e.message); }
        return map;
    }

    /// The accent and what's made from it, as plain colours: the soft wash, and small text (lightened on dark).
    public static void accent_colours (Mode mode, string accent, out string soft, out string text) {
        var c = Gdk.RGBA ();
        if (!c.parse (is_colour (accent) ? accent : DEFAULT_ACCENT)) c.parse (DEFAULT_ACCENT);
        // (rounded by hand: Math.round would need libm in every program that uses the theme)
        int r = (int) (c.red * 255 + 0.5), g = (int) (c.green * 255 + 0.5), b = (int) (c.blue * 255 + 0.5);
        soft = "rgba(%d, %d, %d, 0.23)".printf (r, g, b);
        if (mode == Mode.LIGHT) { text = "#%02X%02X%02X".printf (r, g, b); return; }
        // mixed 45% with white, as the shell's ShellAccentTextBrush
        text = "#%02X%02X%02X".printf ((int) (r * 0.55 + 255 * 0.45 + 0.5), (int) (g * 0.55 + 255 * 0.45 + 0.5),
                                       (int) (b * 0.55 + 255 * 0.45 + 0.5));
    }

    /// A theme's tokens and accent as CSS variables (--cb-fg, --cb-panel…), and GTK's own accent variables.
    private static string variables (Mode mode, string accent) {
        var sb = new StringBuilder ();
        var t = tokens (mode);
        foreach (var name in sorted_keys (t)) sb.append ("  --cb-%s: %s;\n".printf (name.replace ("_", "-"), t.get (name)));
        string soft, text;
        accent_colours (mode, accent, out soft, out text);
        var a = is_colour (accent) ? accent : DEFAULT_ACCENT;
        sb.append ("  --cb-accent: %s;\n  --cb-accent-soft: %s;\n  --cb-accent-text: %s;\n".printf (a, soft, text));
        sb.append ("  --cb-brand: %s;\n  --cb-danger: %s;\n".printf (BRAND, DANGER));
        sb.append ("  --accent-bg-color: %s;\n  --accent-fg-color: #FFFFFF;\n  --accent-color: %s;\n".printf (a, text));
        return sb.str;
    }

    private static string[] sorted_keys (HashTable<string, string> t) {
        var list = new GenericArray<string> ();
        foreach (var k in t.get_keys ()) list.add (k);
        list.sort (strcmp);
        string[] keys = {};
        for (int i = 0; i < list.length; i++) keys += list[i];
        return keys;
    }

    /// base.css with its named colours (@panel) as CSS variables (var(--cb-panel)), and its @define-color lines gone
    /// (they're the variables now), so one sheet can be light or dark by media query.
    public static string with_variables (string css) {
        var known = new GenericSet<string> (str_hash, str_equal);
        var dark = tokens (Mode.DARK);      // kept: get_keys () points into the table
        foreach (var k in dark.get_keys ()) known.add (k);
        foreach (var k in new string[] { "accent", "accent_soft", "accent_text", "brand", "danger" }) known.add (k);
        var sb = new StringBuilder ();
        foreach (var line in css.split ("\n")) {
            if (line.strip ().has_prefix ("@define-color")) continue;
            sb.append (line);
            sb.append_c ('\n');
        }
        try {
            var re = new Regex ("@([a-z_]+)\\b");
            return re.replace_eval (sb.str, -1, 0, 0, (m, res) => {
                var name = m.fetch (1);
                res.append (known.contains (name) ? "var(--cb-%s)".printf (name.replace ("_", "-")) : m.fetch (0));
                return false;
            });
        } catch (RegexError e) {
            critical ("crimson-gtk: %s", e.message);
            return sb.str;
        }
    }

    private const string ROOTS = ":root";

    /// A sheet's @names (@menu, @accent…) as the plain colours of one look: for popovers, where GTK doesn't
    /// resolve the theme's variables.
    public static string with_colours (string css, Mode mode, string accent) {
        var values = tokens (mode);
        string soft, text;
        accent_colours (mode, accent, out soft, out text);
        values.insert ("accent", is_colour (accent) ? accent : DEFAULT_ACCENT);
        values.insert ("accent_soft", soft);
        values.insert ("accent_text", text);
        values.insert ("brand", BRAND);
        values.insert ("danger", DANGER);
        try {
            var re = new Regex ("@([a-z_]+)\\b");
            return re.replace_eval (css, -1, 0, 0, (m, res) => {
                var v = values.get (m.fetch (1));
                res.append (v ?? m.fetch (0));
                return false;
            });
        } catch (RegexError e) {
            critical ("crimson-gtk: %s", e.message);
            return css;
        }
    }

    /// The GTK 4 theme's gtk.css: light by default, dark by GTK's colour scheme (GTK 4.20 and later choose this way).
    public static string gtk4_sheet (string accent) {
        var sb = new StringBuilder ();
        sb.append ("@import url(\"%s\");\n".printf (GTK4_LIGHT));
        sb.append (ROOTS + " {\n" + variables (Mode.LIGHT, accent) + "}\n");
        sb.append ("@media (prefers-color-scheme: dark) {\n" + ROOTS + " {\n" + variables (Mode.DARK, accent) + "}\n");
        sb.append (resource ("dark-surfaces.css"));
        sb.append ("}\n");
        sb.append (with_variables (resource ("base.css")));
        // popovers last, in plain colours: light, then dark under the media query (they don't see the variables)
        sb.append (with_colours (resource ("popovers.css"), Mode.LIGHT, accent));
        sb.append ("@media (prefers-color-scheme: dark) {\n");
        sb.append (with_colours (resource ("popovers.css"), Mode.DARK, accent));
        sb.append ("}\n");
        return sb.str;
    }

    /// GTK's own built-in theme each of ours starts from, so anything ours doesn't restyle still looks right.
    public const string GTK4_LIGHT = "resource:///org/gtk/libgtk/theme/Default/Default-light.css";
    public const string GTK4_DARK = "resource:///org/gtk/libgtk/theme/Default/Default-dark.css";
    public const string GTK3_LIGHT = "resource:///org/gtk/libgtk/theme/Adwaita/gtk-contained.css";
    public const string GTK3_DARK = "resource:///org/gtk/libgtk/theme/Adwaita/gtk-contained-dark.css";

    /// The "Crimson Banner" GTK theme for every other program: a theme folder (/usr/share/themes/CrimsonBanner, or a
    /// person's own copy in their accent) with GTK 4's and GTK 3's style sheets, light (gtk.css) and dark
    /// (gtk-dark.css, which GTK picks when dark is preferred).
    public static void write_theme_folder (string dir, string accent = DEFAULT_ACCENT) throws Error {
        DirUtils.create_with_parents (Path.build_filename (dir, "gtk-4.0"), 0755);
        DirUtils.create_with_parents (Path.build_filename (dir, "gtk-3.0"), 0755);
        var head = "/* Crimson Banner OS's GTK theme, made by crimson-gtk-theme (crimson-gtk/). Accent %s. */\n".printf (accent);
        FileUtils.set_contents (Path.build_filename (dir, "index.theme"),
            "[Desktop Entry]\nType=X-GNOME-Metatheme\nName=Crimson Banner\nComment=Crimson Banner OS: graphite, with one accent\nEncoding=UTF-8\n\n"
            + "[X-GNOME-Metatheme]\nGtkTheme=CrimsonBanner\nIconTheme=CrimsonBanner\n# accent: %s\n".printf (accent));
        // gtk.css answers both: GTK 4.20 and later pick dark by media query; gtk-dark.css is for older GTK, and for
        // GTK_THEME=CrimsonBanner:dark
        FileUtils.set_contents (Path.build_filename (dir, "gtk-4.0", "gtk.css"), head + gtk4_sheet (accent));
        FileUtils.set_contents (Path.build_filename (dir, "gtk-4.0", "gtk-dark.css"),
            head + "@import url(\"%s\");\n".printf (GTK4_DARK) + css_for (Mode.DARK, accent));
        FileUtils.set_contents (Path.build_filename (dir, "gtk-3.0", "gtk.css"),
            head + "@import url(\"%s\");\n".printf (GTK3_LIGHT) + css_for_gtk3 (Mode.LIGHT, accent));
        FileUtils.set_contents (Path.build_filename (dir, "gtk-3.0", "gtk-dark.css"),
            head + "@import url(\"%s\");\n".printf (GTK3_DARK) + css_for_gtk3 (Mode.DARK, accent));
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
    return save_drawing (win, file);
}

/// Any surface as it's drawn (a window, or a popover, which is a surface of its own), to a PNG.
public bool save_drawing (Gtk.Widget win, string file) {
    int w = win.get_width (), h = win.get_height ();
    var paintable = new Gtk.WidgetPaintable (win);
    var snap = new Gtk.Snapshot ();
    paintable.snapshot (snap, w, h);
    var node = snap.to_node ();
    if (node == null) { printerr ("crimson-gtk: nothing was drawn\n"); return false; }
    var texture = win.get_native ().get_renderer ().render_texture (node, null);
    if (!texture.save_to_png (file)) { printerr ("crimson-gtk: couldn't write %s\n", file); return false; }
    print ("drew %dx%d to %s\n", w, h, file);
    return true;
}

}
