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
css_test = executable('crimson-gtk-test', 'theme.vala', 'test/css.vala', resources, dependencies: deps)
test('the style sheet, in both themes and every accent', css_test)
