#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Runs a command headless inside redstar-comp (wlroots' virtual screen), so GTK programs and tests have a display,
# and gives back its exit code. CRIMSON_GTK_REQUIRE_DISPLAY is set, so tests that need a display don't skip.
#   crimson-gtk/test/in-compositor.sh COMMAND [ARGS...]      (REDSTAR_COMP: redstar-comp, else the tree's build)
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
COMP="${REDSTAR_COMP:-$HERE/../../compositor/build/redstar-comp}"
[ -x "$COMP" ] || { echo "build redstar-comp first (compositor/build.sh)" >&2; exit 1; }
WORK="$(mktemp -d)"
chmod 700 "$WORK"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/runtime" "$WORK/home"
chmod 700 "$WORK/runtime"
unset DISPLAY WAYLAND_DISPLAY DBUS_SESSION_BUS_ADDRESS
export XDG_RUNTIME_DIR="$WORK/runtime" HOME="$WORK/home" XDG_CONFIG_HOME="$WORK/home/.config"
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1
export GSK_RENDERER=cairo LIBGL_ALWAYS_SOFTWARE=1 GDK_BACKEND=wayland NO_AT_BRIDGE=1 CRIMSON_GTK_REQUIRE_DISPLAY=1
cmd=""
for a in "$@"; do cmd="$cmd \"$a\""; done
timeout 120 "$COMP" -- sh -c "$cmd; echo \$? > \"$WORK/code\"" 2>"$WORK/err.log" || true
code="$(cat "$WORK/code" 2>/dev/null || echo 1)"
[ "$code" = 0 ] || grep -v "^\s*$" "$WORK/err.log" | tail -n 40
exit "$code"
