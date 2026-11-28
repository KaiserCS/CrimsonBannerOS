#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Draws a GTK program to a picture, headless inside redstar-comp (wlroots' virtual screen): it runs
# PROGRAM ARGS... --screenshot OUT and checks the picture came out. For crimson-login and the theme's gallery.
#   crimson-gtk/test/headless.sh OUT PROGRAM [ARGS...]     (REDSTAR_COMP: redstar-comp, else the tree's build)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:?where to write the picture}"; shift
PROGRAM="${1:?which program}"; shift
COMP="${REDSTAR_COMP:-$HERE/../../compositor/build/redstar-comp}"
[ -x "$PROGRAM" ] || { echo "$PROGRAM isn't built" >&2; exit 1; }
[ -x "$COMP" ] || { echo "build redstar-comp first (compositor/build.sh)" >&2; exit 1; }
WORK="$(mktemp -d)"
chmod 700 "$WORK"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/runtime" "$WORK/home"
chmod 700 "$WORK/runtime"
unset DISPLAY WAYLAND_DISPLAY DBUS_SESSION_BUS_ADDRESS
export XDG_RUNTIME_DIR="$WORK/runtime" HOME="$WORK/home" XDG_CONFIG_HOME="$WORK/home/.config"
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1
export GSK_RENDERER=cairo LIBGL_ALWAYS_SOFTWARE=1 GDK_BACKEND=wayland NO_AT_BRIDGE=1
case "$OUT" in /*) ;; *) OUT="$PWD/$OUT" ;; esac
rm -f "$OUT"
cmd="\"$PROGRAM\""
for a in "$@"; do cmd="$cmd \"$a\""; done
timeout 60 "$COMP" -- sh -c "$cmd --screenshot \"$OUT\"" >"$WORK/out.log" 2>"$WORK/err.log" || true
if [ ! -s "$OUT" ]; then
  echo "$(basename "$PROGRAM") drew nothing"
  grep -v "^\s*$" "$WORK/err.log" | tail -n 60
  exit 1
fi
cat "$WORK/out.log"
