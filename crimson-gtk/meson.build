# SPDX-License-Identifier: GPL-3.0-or-later
# crimson-gtk: Crimson Banner's look for GTK 4 programs (theme.vala, data/*.css). The programs that use it compile
# theme.vala and data/crimson-gtk.gresource.xml themselves (greeter/ does); this builds the gallery and the tests.
# Arch / CachyOS: sudo pacman -S --needed vala gtk4 json-glib meson ninja
project('crimson-gtk', ['vala', 'c'], version: files('../VERSION'), license: 'GPL-3.0-or-later',
  default_options: ['warning_level=1', 'buildtype=debugoptimized'], meson_version: '>= 0.60')

deps = [dependency('glib-2.0', version: '>= 2.74'), dependency('gobject-2.0'), dependency('gio-2.0'),
        dependency('json-glib-1.0'), dependency('gtk4', version: '>= 4.12')]
gnome = import('gnome')
resources = gnome.compile_resources('crimson-gtk-resources', 'data/crimson-gtk.gresource.xml',
  source_dir: 'data', c_name: 'crimson_gtk')

executable('crimson-gtk-gallery', 'theme.vala', 'gallery.vala', resources, dependencies: deps)

# the "Crimson Banner" GTK theme for every other program: crimson-gtk-theme writes it, here into
# share/themes/CrimsonBanner, and the shell into a person's own copy when they pick another accent
theme_tool = executable('crimson-gtk-theme', 'theme.vala', 'make-theme.vala', resources, dependencies: deps,
  install: true)
meson.add_install_script(theme_tool, '--meson-install')

# the tests: the style sheet as GTK reads it, and the theme folder read by GTK 4 and GTK 3 themselves
theme_folder = custom_target('theme-for-tests', output: 'theme-for-tests', command: [theme_tool, '@OUTPUT@'],
  build_by_default: true)
css_test = executable('crimson-gtk-test', 'theme.vala', 'test/css.vala', resources, dependencies: deps)
test('the style sheet, in both themes and every accent', css_test, args: [theme_folder.full_path()], depends: theme_folder)
gtk3 = dependency('gtk+-3.0', required: false)
if gtk3.found()
  gtk3_check = executable('crimson-gtk3-check', 'test/gtk3-check.c', dependencies: gtk3)
  test('the theme folder, read by GTK 3', gtk3_check, args: [theme_folder.full_path()], depends: theme_folder)
endif
