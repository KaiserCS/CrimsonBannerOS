#!/bin/sh
# Builds Red Star Online into browser/build/redstar-online (and copies it next to the Red Star app when that's built).
# Arch / CachyOS: sudo pacman -S --needed webkitgtk-6.0 gtk4 json-glib libsoup3 meson ninja
set -e
cd "$(dirname "$0")"
if ! pkg-config --exists webkitgtk-6.0 gtk4 json-glib-1.0 libsoup-3.0; then
	echo "Crimson Online needs WebKitGTK 6.0, GTK 4, json-glib and libsoup 3 (Arch/CachyOS: sudo pacman -S webkitgtk-6.0 gtk4 json-glib libsoup3)" >&2
	exit 1
fi
[ -d build ] || meson setup build
meson compile -C build
for d in ../RedStarOS/bin/*/net8.0 ../RedStarOS/bin/*/net8.0/linux-x64; do
	[ -d "$d" ] && cp build/redstar-online "$d/" && echo "copied to $d"
done
echo "built browser/build/redstar-online"
if ! command -v gst-inspect-1.0 >/dev/null 2>&1 || ! gst-inspect-1.0 autoaudiosink >/dev/null 2>&1; then
	echo "note: for sound and video in pages install GStreamer's plugins (Arch/CachyOS: sudo pacman -S gst-plugins-good gst-plugins-bad gst-libav)"
fi
