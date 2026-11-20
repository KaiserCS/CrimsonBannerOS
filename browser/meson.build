# Red Star Online: GTK 4 + WebKitGTK web browser (GPL-3.0-or-later)
# Arch / CachyOS: sudo pacman -S --needed webkitgtk-6.0 gtk4 json-glib libsoup3 meson ninja
project('redstar-online', 'c', version: '1.0', license: 'GPL-3.0-or-later',
  default_options: ['c_std=gnu11', 'warning_level=2', 'buildtype=debugoptimized'])

glib = dependency('glib-2.0', version: '>= 2.74')
gio = dependency('gio-2.0')
gtk = dependency('gtk4', version: '>= 4.12')
webkit = dependency('webkitgtk-6.0', version: '>= 2.42')
json = dependency('json-glib-1.0', version: '>= 1.6')
soup = dependency('libsoup-3.0')

gnome = import('gnome')
# the built-in starter filter list, inside the program (also used by the old Electron engine until it goes)
resources = gnome.compile_resources('online-resources', 'data/online.gresource.xml',
  source_dir: '../webengine', c_name: 'online')

executable('redstar-online',
  'src/browser.c', 'src/store.c', 'src/filters.c', 'src/adblock.c', 'src/util.c', resources,
  dependencies: [glib, gio, gtk, webkit, json, soup],
  install: true)

unit = executable('online-unit', 'test/unit.c', 'src/adblock.c', 'src/util.c', dependencies: [glib])
store_test = executable('online-store-test', 'test/store-test.c', 'src/store.c', dependencies: [glib, gio, json])
test('converter and address bar', unit)
test('bookmarks and history file', store_test)

install_data('data/org.redstaros.Online.desktop', install_dir: get_option('datadir') / 'applications')
