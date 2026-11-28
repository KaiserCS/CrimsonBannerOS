#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Draws crimson-login's preview to a picture, headless inside redstar-comp (wlroots' virtual screen), and checks it
# came out: the window opens, draws, and isn't blank. CI prints the picture in its log.
#   greeter/test/run-screenshot.sh OUT.png [path/to/crimson-login] [path/to/redstar-comp]
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:?where to write the picture}"
LOGIN="${2:-$HERE/../build/crimson-login}"
COMP="${3:-$HERE/../../compositor/build/redstar-comp}"
[ -x "$LOGIN" ] || { echo "build crimson-login first (greeter/build.sh)" >&2; exit 1; }
[ -x "$COMP" ] || { echo "build redstar-comp first (compositor/build.sh)" >&2; exit 1; }
WORK="$(mktemp -d)"
chmod 700 "$WORK"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/runtime" "$WORK/home"
chmod 700 "$WORK/runtime"
unset DISPLAY WAYLAND_DISPLAY DBUS_SESSION_BUS_ADDRESS
export XDG_RUNTIME_DIR="$WORK/runtime" HOME="$WORK/home"
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1
export GSK_RENDERER=cairo LIBGL_ALWAYS_SOFTWARE=1 GDK_BACKEND=wayland NO_AT_BRIDGE=1
# a large photo as this person's picture: it must still come out the avatar's size
mkdir -p "$WORK/avatars"
cp "$HERE/../../RedStarOS/Assets/login-bg.jpg" "$WORK/avatars/$(id -un)"
export CRIMSON_GREETER_AVATARS="$WORK/avatars"
case "$OUT" in /*) ;; *) OUT="$PWD/$OUT" ;; esac
rm -f "$OUT"
timeout 60 "$COMP" -- sh -c "\"$LOGIN\" --screenshot \"$OUT\"" >"$WORK/out.log" 2>"$WORK/err.log" || true
if [ ! -s "$OUT" ]; then
  echo "crimson-login drew nothing"
  grep -v "^\s*$" "$WORK/err.log" | tail -n 60
  exit 1
fi
cat "$WORK/out.log"
