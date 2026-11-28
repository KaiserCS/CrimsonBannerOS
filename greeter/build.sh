#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Builds crimson-login into greeter/build. Arch / CachyOS: sudo pacman -S --needed vala gtk4 json-glib meson ninja
set -e
cd "$(dirname "$0")"
[ -d build ] || meson setup build
meson compile -C build
