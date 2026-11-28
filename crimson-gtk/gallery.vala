// SPDX-License-Identifier: GPL-3.0-or-later
// crimson-gtk-gallery: every part of Crimson Banner's GTK 4 look in one window, to check it against the shell's.
//   crimson-gtk-gallery [--light] [--accent #RRGGBB] [--screenshot FILE]
//   crimson-gtk-gallery --menu FILE      also a program's main menu (a popover menu, as Crimson Online's), drawn
//                                        to FILE with its size printed
//   crimson-gtk-gallery --as-other-program [--light]    without our own styling: GTK's installed "Crimson Banner"
//                                                       theme instead, as any other GTK 4 program gets it
int main (string[] args) {
    var mode = Crimson.Mode.DARK;
    string accent = Crimson.Theme.DEFAULT_ACCENT;
    string? shot = null;
    bool other = false;
    string? menu_shot = null;
    for (int i = 1; i < args.length; i++) {
        if (args[i] == "--light") mode = Crimson.Mode.LIGHT;
        else if (args[i] == "--as-other-program") other = true;
        else if (args[i] == "--menu" && i + 1 < args.length) menu_shot = args[++i];
        else if (args[i] == "--accent" && i + 1 < args.length) accent = args[++i];
        else if (args[i] == "--screenshot" && i + 1 < args.length) shot = args[++i];
    }
    var app = new Gtk.Application ("org.redstaros.CrimsonGtkGallery", ApplicationFlags.NON_UNIQUE);
    app.activate.connect (() => {
        if (other) {
            var settings = Gtk.Settings.get_default ();
            settings.gtk_theme_name = "CrimsonBanner";
            settings.gtk_application_prefer_dark_theme = mode == Crimson.Mode.DARK;
            // GTK 4.20 picks the theme's dark sheet from its colour scheme, not the older switch
            if (settings.get_class ().find_property ("gtk-interface-color-scheme") != null)
                settings.set ("gtk-interface-color-scheme", mode == Crimson.Mode.DARK ? 2 : 3);
        } else {
            Crimson.Theme.apply_mode (mode, accent);
        }
        Gtk.MenuButton menu_button;
        var win = gallery (app, out menu_button);
        win.present ();
        if (menu_shot != null) Timeout.add (1200, () => {
            menu_button.popup ();
            Timeout.add (1200, () => {
                var pop = menu_button.popover;
                print ("menu: %d x %d\n", pop.get_width (), pop.get_height ());
                Crimson.save_drawing (pop, menu_shot);
                return Source.REMOVE;
            });
            return Source.REMOVE;
        });
        if (shot != null) Timeout.add (2000, () => { Crimson.save_screenshot (win, shot); app.quit (); return Source.REMOVE; });
    });
    return app.run (new string[] { args[0] });
}

Gtk.Window gallery (Gtk.Application app, out Gtk.MenuButton menu_button) {
    var win = new Gtk.ApplicationWindow (app);
    win.title = "Crimson Banner GTK";
    win.set_default_size (1180, 820);

    // the sidebar, as Planner's
    var side = new Gtk.Box (Gtk.Orientation.VERTICAL, 2);
    side.width_request = 200;
    side.margin_start = 14;
    side.margin_top = 16;
    side.margin_end = 6;
    var brand = Crimson.kicker ("Crimson Banner OS", true);
    brand.margin_start = 10;
    side.append (brand);
    var name = new Gtk.Label ("Gallery");
    name.add_css_class ("h1");
    name.xalign = 0;
    name.margin_start = 10;
    name.margin_bottom = 16;
    side.append (name);
    string[] pages = { "Components", "Fields", "Readouts" };
    for (int i = 0; i < pages.length; i++) {
        var b = Crimson.nav (pages[i]);
        if (i == 0) b.add_css_class ("on");
        side.append (b);
    }

    // a main menu, as a program's (Crimson Online's): sections, a check, a submenu
    var model = new Menu ();
    var s1 = new Menu ();
    s1.append ("New Tab", "app.none");
    s1.append ("New Window", "app.none");
    model.append_section (null, s1);
    var s2 = new Menu ();
    s2.append ("Find in Page…", "app.none");
    s2.append ("Block Ads and Trackers", "app.check");
    var sub = new Menu ();
    sub.append ("Bookmarks", "app.none");
    s2.append_submenu ("History", sub);
    model.append_section (null, s2);
    var none = new SimpleAction ("none", null);
    app.add_action (none);
    var check = new SimpleAction.stateful ("check", null, new Variant.boolean (true));
    app.add_action (check);
    menu_button = new Gtk.MenuButton ();
    menu_button.icon_name = "open-menu-symbolic";
    menu_button.menu_model = model;
    menu_button.halign = Gtk.Align.START;
    side.append (menu_button);

    var col = new Gtk.Box (Gtk.Orientation.VERTICAL, 16);
    col.margin_start = 22;
    col.margin_top = 18;
    col.margin_end = 28;
    col.margin_bottom = 28;
    col.append (Crimson.kicker ("The parts, in GTK 4", true));
    col.append (Crimson.display ("Everything in one place"));
    col.append (Crimson.muted ("The shell's design system for GTK programs: the same tokens, the same panels, pills, badges and readouts, so a program that moves off Avalonia keeps its look."));

    // an app header
    Gtk.Box actions;
    var header = Crimson.app_header (null, "Calculator", "An app's header: its name, a line, its own actions", out actions);
    actions.append (Crimson.pill ("History"));
    var hp = Crimson.panel ();
    hp.append (header);
    col.append (hp);

    var grid = new Gtk.Grid ();
    grid.column_spacing = 12;
    grid.row_spacing = 12;
    grid.column_homogeneous = true;

    // buttons
    var buttons = Crimson.panel ();
    buttons.append (Crimson.kicker ("Buttons"));
    var r1 = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 8);
    r1.append (Crimson.pill ("Install", true));
    var on = Crimson.pill ("On this computer");
    on.add_css_class ("on");
    r1.append (on);
    r1.append (Crimson.pill ("Details"));
    buttons.append (r1);
    var r2 = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 8);
    var primary = new Gtk.Button.with_label ("Save");
    primary.add_css_class ("primary");
    r2.append (primary);
    r2.append (new Gtk.Button.with_label ("Cancel"));
    var flat = new Gtk.Button.with_label ("More");
    flat.add_css_class ("flat");
    r2.append (flat);
    var danger = new Gtk.Button.with_label ("Erase");
    danger.add_css_class ("danger");
    r2.append (danger);
    buttons.append (r2);
    var big = Crimson.pill ("Start the period", true);
    big.add_css_class ("large");
    big.halign = Gtk.Align.START;
    buttons.append (big);
    grid.attach (buttons, 0, 0);

    // badges and the readout
    var readout = Crimson.panel ();
    readout.append (Crimson.kicker ("Badges and readout"));
    var badges = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 6);
    badges.append (Crimson.badge ("Normal"));
    badges.append (Crimson.badge ("Running short", true));
    var status = new Gtk.Label ("READY");
    status.add_css_class ("status");
    badges.append (status);
    readout.append (badges);
    readout.append (Crimson.lcd ({ "Period", "Products", "Recounts" }, { "04", "05", "01" }));
    grid.attach (readout, 1, 0);

    // fields
    var fields = Crimson.panel ();
    fields.append (Crimson.kicker ("Fields"));
    var entry = new Gtk.Entry ();
    entry.placeholder_text = "A text field";
    fields.append (entry);
    var search = new Gtk.Entry ();
    search.placeholder_text = "Search";
    search.add_css_class ("pill-field");
    fields.append (search);
    var dd = new Gtk.DropDown.from_strings ({ "Crimson Banner OS (Wayland)", "Crimson Banner OS (X11)" });
    dd.halign = Gtk.Align.START;
    fields.append (dd);
    grid.attach (fields, 0, 1);

    // switches and sliders
    var toggles = Crimson.panel ();
    toggles.append (Crimson.kicker ("Switches and sliders"));
    var srow = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 10);
    var sw = new Gtk.Switch ();
    sw.active = true;
    srow.append (sw);
    srow.append (new Gtk.Label ("Night light"));
    toggles.append (srow);
    var check = new Gtk.CheckButton.with_label ("Remember this desktop");
    check.active = true;
    toggles.append (check);
    var scale = new Gtk.Scale.with_range (Gtk.Orientation.HORIZONTAL, 0, 100, 1);
    scale.set_value (62);
    scale.draw_value = false;
    toggles.append (scale);
    var progress = new Gtk.ProgressBar ();
    progress.fraction = 0.4;
    toggles.append (progress);
    grid.attach (toggles, 1, 1);

    col.append (grid);

    var empty = Crimson.panel ();
    empty.height_request = 140;
    empty.append (Crimson.empty_state ("document-open-symbolic", "No reports yet", "Drop a council's report here, or choose one."));
    col.append (empty);

    var scroll = new Gtk.ScrolledWindow ();
    scroll.hscrollbar_policy = Gtk.PolicyType.NEVER;
    scroll.child = col;
    scroll.hexpand = true;
    var root = new Gtk.Box (Gtk.Orientation.HORIZONTAL, 0);
    root.append (side);
    root.append (scroll);
    win.child = root;
    return win;
}
