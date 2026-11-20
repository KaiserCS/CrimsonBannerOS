#!/bin/sh
# Runs Red Star Online's self-test: the real browser, headless inside redstar-comp (wlroots' virtual screen), on local
# pages — the blocker, address bar, back, bookmarks, history, find, new tabs, closing and reopening, downloads.
#   browser/test/run-selftest.sh [path/to/redstar-online] [path/to/redstar-comp]
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
ONLINE="${1:-$HERE/../build/redstar-online}"
COMP="${2:-$HERE/../../compositor/build/redstar-comp}"
[ -x "$ONLINE" ] || { echo "build Red Star Online first (browser/build.sh)" >&2; exit 1; }
[ -x "$COMP" ] || { echo "build redstar-comp first (compositor/build.sh)" >&2; exit 1; }
WORK="$(mktemp -d)"
chmod 700 "$WORK"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/runtime" "$WORK/home"
chmod 700 "$WORK/runtime"
unset DISPLAY WAYLAND_DISPLAY DBUS_SESSION_BUS_ADDRESS
export XDG_RUNTIME_DIR="$WORK/runtime" HOME="$WORK/home"
export XDG_CONFIG_HOME="$WORK/home/.config" XDG_DATA_HOME="$WORK/home/.local/share" XDG_CACHE_HOME="$WORK/home/.cache"
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1
# no GPU on a virtual screen: software drawing; WebKit's sandbox needs user namespaces, which test containers lack
export GSK_RENDERER=cairo LIBGL_ALWAYS_SOFTWARE=1 WEBKIT_DISABLE_DMABUF_RENDERER=1 WEBKIT_DISABLE_COMPOSITING_MODE=1
export WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS=1 GDK_BACKEND=wayland NO_AT_BRIDGE=1
status=0
RUN="$ONLINE --self-test"
command -v dbus-run-session >/dev/null 2>&1 && RUN="dbus-run-session -- $RUN"
timeout 180 "$COMP" -- sh -c "$RUN" 2>"$WORK/err.log" | tee "$WORK/out.log" || status=$?
grep -q "^0 failed" "$WORK/out.log" || status=1
if [ "$status" != 0 ]; then
	echo "---- log ----"
	grep -v "^\s*$" "$WORK/err.log" | tail -n 60
fi
exit "$status"
