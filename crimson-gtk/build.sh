#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Builds the GTK theme's gallery and tests into crimson-gtk/build. Arch / CachyOS: pacman -S vala gtk4 json-glib meson
set -e
cd "$(dirname "$0")"
[ -d build ] || meson setup build
meson compile -C build
