// SPDX-License-Identifier: GPL-3.0-or-later
// The theme's style sheet, read by GTK in both themes and with every accent the shell offers: a line GTK can't read
// would be dropped without a word, so here it fails the test.
int main (string[] args) {
    Test.init (ref args);
    string[] accents = { "#C8102E", "#0078D4", "#107C10", "#D83B01", "#744DA9", "#00828A", "#5D5A58", "not a colour" };
    foreach (var mode in new Crimson.Mode[] { Crimson.Mode.DARK, Crimson.Mode.LIGHT }) {
        var m = mode;
        Test.add_data_func ("/theme/%s".printf (m == Crimson.Mode.DARK ? "dark" : "light"), () => {
            foreach (var a in accents) {
                var css = Crimson.Theme.css_for (m, a);
                assert (css.contains ("@define-color accent ") && css.contains (".pill") && css.contains ("@define-color backdrop_top"));
                var problems = new GenericArray<string> ();
                var p = new Gtk.CssProvider ();
                p.parsing_error.connect ((section, error) => {
                    problems.add ("%s: %s".printf (section.to_string (), error.message));
                });
                p.load_from_string (css);
                for (int i = 0; i < problems.length; i++) printerr ("%s (accent %s)\n", problems[i], a);
                assert (problems.length == 0);
            }
        });
    }
    Test.add_func ("/theme/colours", () => {
        assert (Crimson.Theme.is_colour ("#C8102E"));
        assert (!Crimson.Theme.is_colour ("crimsonish"));
        assert (!Crimson.Theme.is_colour (""));
        // a bad accent falls back to crimson
        assert (Crimson.Theme.css_for (Crimson.Mode.DARK, "nope").contains ("@define-color accent #C8102E;"));
    });
    // the theme folder other programs use, read by GTK 4 itself: it needs a display, for GTK's own theme to start from
    var folder = args.length > 1 ? args[1] : null;
    if (folder != null) Test.add_func ("/theme/folder", () => {
        if (!Gtk.init_check ()) {
            if (Environment.get_variable ("CRIMSON_GTK_REQUIRE_DISPLAY") != null) error ("no display");
            Test.skip ("no display");
            return;
        }
        foreach (var name in new string[] { "gtk.css", "gtk-dark.css" }) {
            var path = Path.build_filename (folder, "gtk-4.0", name);
            assert (FileUtils.test (path, FileTest.EXISTS));
            var problems = new GenericArray<string> ();
            var p = new Gtk.CssProvider ();
            p.parsing_error.connect ((section, error) => { problems.add ("%s: %s".printf (section.to_string (), error.message)); });
            p.load_from_path (path);
            for (int i = 0; i < problems.length; i++) printerr ("%s\n", problems[i]);
            assert (problems.length == 0);
        }
        assert (FileUtils.test (Path.build_filename (folder, "index.theme"), FileTest.EXISTS));
        assert (FileUtils.test (Path.build_filename (folder, "gtk-3.0", "gtk-dark.css"), FileTest.EXISTS));
    });
    return Test.run ();
}
