// SPDX-License-Identifier: GPL-3.0-or-later
// crimson-gtk-theme: writes the "Crimson Banner" GTK theme folder for other programs (GTK 3 and GTK 4).
//   crimson-gtk-theme DIR [ACCENT]     the theme in DIR, in an accent (crimson if none); the shell writes a person's
//                                      own copy this way (~/.local/share/themes/CrimsonBanner) when they pick another
//   crimson-gtk-theme --meson-install   the system's copy, as meson install puts it (share/themes/CrimsonBanner)
int main (string[] args) {
    string? dir = null;
    string accent = Crimson.Theme.DEFAULT_ACCENT;
    if (args.length >= 2 && args[1] == "--meson-install") {
        var prefix = Environment.get_variable ("MESON_INSTALL_DESTDIR_PREFIX");
        if (prefix == null) { printerr ("crimson-gtk-theme: --meson-install is for meson install\n"); return 2; }
        dir = Path.build_filename (prefix, "share", "themes", "CrimsonBanner");
    } else if (args.length >= 2) {
        dir = args[1];
        if (args.length >= 3) accent = args[2];
    }
    if (dir == null) { printerr ("usage: crimson-gtk-theme DIR [ACCENT]\n"); return 2; }
    if (!Crimson.Theme.is_colour (accent)) { printerr ("crimson-gtk-theme: %s isn't a colour\n", accent); return 2; }
    try {
        Crimson.Theme.write_theme_folder (dir, accent);
    } catch (Error e) {
        printerr ("crimson-gtk-theme: %s\n", e.message);
        return 1;
    }
    return 0;
}
