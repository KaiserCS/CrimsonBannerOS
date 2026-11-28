# crimson-gtk

Crimson Banner's look for GTK 4 programs: the shell's colour tokens and components (`RedStarOS/Styles/Shell.axaml`,
`DESIGN.md`) as GTK CSS, and the common parts built in code. Everything that moves off .NET and Avalonia uses it
(ROADMAP, 5), starting with the login screen (`greeter/`).

- `data/dark.css`, `data/light.css`: the colour tokens, the same values as the shell's dark and light dictionaries.
  Change a token in both places in the same change.
- `data/base.css`: the components under the shell's class names (`.panel`, `.card`, `.kicker`, `.display`,
  `.muted`, `.caption`, `button.pill` with `.accent`/`.on`/`.large`, `button.nav`, `button.primary`, `.flat`,
  `.danger`, `.badge`, `.status`, `.lcd`, `entry.pill-field`, `.app-header`, `.empty-state`), and GTK's own controls
  restyled to match: buttons, fields, drop-downs, popovers, switches, checks, sliders, progress, header bars.
- `theme.vala`: `Crimson.Theme.apply ()` follows the person's theme and accent (the shell's settings file);
  `apply_mode ()` fixes them (the login screen is always dark). `Crimson.kicker`, `pill`, `panel`, `badge`, `lcd`,
  `nav`, `app_header`, `empty_state` build the parts; `save_screenshot` draws a window to a PNG.

A program uses it by compiling `../crimson-gtk/theme.vala` (copied into its build with `configure_file`, as Meson's
Vala won't compile a source from outside the project) and `../crimson-gtk/data/crimson-gtk.gresource.xml` with its
own sources (see `greeter/meson.build`). `crimson-gtk-gallery` shows every part (`--light`, `--accent`,
`--screenshot FILE`); CI prints it in both themes.
