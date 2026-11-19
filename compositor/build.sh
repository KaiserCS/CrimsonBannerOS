#!/bin/sh
# Builds redstar-comp into compositor/build/redstar-comp (and copies it next to the Red Star app when that's built).
# Arch / CachyOS: sudo pacman -S --needed wlroots0.20 wayland-protocols meson ninja xorg-xwayland xcb-util-wm cairo
set -e
cd "$(dirname "$0")"
if ! pkg-config --exists wlroots-0.20; then
	echo "redstar-comp needs wlroots 0.20 (Arch/CachyOS: sudo pacman -S wlroots0.20)" >&2
	exit 1
fi
[ -d build ] || meson setup build
meson compile -C build
for d in ../RedStarOS/bin/*/net8.0 ../RedStarOS/bin/*/net8.0/linux-x64; do
	[ -d "$d" ] && cp build/redstar-comp "$d/" && echo "copied to $d"
done
echo "built compositor/build/redstar-comp"
