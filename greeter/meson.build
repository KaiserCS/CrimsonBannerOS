# SPDX-License-Identifier: GPL-3.0-or-later
# Crimson Banner OS's login screen in Vala and GTK 4 (crimson-login), started by greetd through crimson-greeter
# (packaging/greeter). The first part of the shell to leave .NET and Avalonia (ROADMAP, 5).
# Arch / CachyOS: sudo pacman -S --needed vala gtk4 json-glib meson ninja
project('crimson-login', ['vala', 'c'], version: files('../VERSION'), license: 'GPL-3.0-or-later',
  default_options: ['warning_level=1', 'buildtype=debugoptimized'], meson_version: '>= 0.60')

glib = dependency('glib-2.0', version: '>= 2.74')
gobject = dependency('gobject-2.0')
gio = dependency('gio-2.0')
giounix = dependency('gio-unix-2.0')
json = dependency('json-glib-1.0', version: '>= 1.6')
gtk = dependency('gtk4', version: '>= 4.12')

gnome = import('gnome')
# Crimson Banner's GTK theme (crimson-gtk/), compiled in with the login screen's own sources
theme = files('../crimson-gtk/theme.vala')
theme_resources = gnome.compile_resources('crimson-gtk-resources', '../crimson-gtk/data/crimson-gtk.gresource.xml',
  source_dir: '../crimson-gtk/data', c_name: 'crimson_gtk')
# the login screen's picture, the shell's own (RedStarOS/Assets/login-bg.jpg), inside the program
resources = gnome.compile_resources('login-resources', 'data/login.gresource.xml',
  source_dir: ['data', '../RedStarOS/Assets'], c_name: 'login')

core = files('src/greetd.vala', 'src/flow.vala', 'src/data.vala')

executable('crimson-login', core, theme, 'src/window.vala', 'src/main.vala', resources, theme_resources,
  dependencies: [glib, gobject, gio, giounix, json, gtk],
  install: true)

unit = executable('login-unit', core, 'test/unit.vala', dependencies: [glib, gobject, gio, giounix, json])
test('greetd, signing in, people and desktops', unit)
