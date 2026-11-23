#!/bin/sh
# Runs redstar-comp on wlroots' virtual screen (no monitor or GPU needed) with two test clients:
#   comptest    an X11 client (through Xwayland, like the Red Star shell): frames, placement, work area, snapping…
#   nativetest  a native Wayland window, checked through the shell's connection (taskbar list, commands, positions)
#   inputtest   a virtual keyboard and mouse: Alt+Tab and the other shortcuts, menus closing on outside clicks,
#               title-bar double-click and buttons, menus that reach past their window
#   compositor/test/run-tests.sh [path/to/redstar-comp]
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
COMP="${1:-$HERE/../build/redstar-comp}"
[ -x "$COMP" ] || { echo "build redstar-comp first (compositor/build.sh)" >&2; exit 1; }
WORK="$(mktemp -d)"
chmod 700 "$WORK"
trap 'rm -rf "$WORK"' EXIT

gcc -O2 -Wall -Wextra -Werror -o "$WORK/comptest" "$HERE/comptest.c" -lX11
WP="$(pkg-config --variable=pkgdatadir wayland-protocols)"
for x in "$WP/stable/xdg-shell/xdg-shell.xml" "$WP/unstable/xdg-decoration/xdg-decoration-unstable-v1.xml"; do
	n="$(basename "$x" .xml)"
	wayland-scanner client-header "$x" "$WORK/$n-client-protocol.h"
	wayland-scanner private-code "$x" "$WORK/$n-protocol.c"
done
gcc -O2 -Wall -Wextra -Werror -I"$WORK" -o "$WORK/nativetest" "$HERE/nativetest.c" "$WORK"/*-protocol.c $(pkg-config --cflags --libs wayland-client)
gcc -O2 -Wall -Wextra -Werror -I"$WORK" -o "$WORK/inputtest" "$HERE/inputtest.c" "$WORK"/*-protocol.c $(pkg-config --cflags --libs wayland-client xkbcommon)
gcc -O2 -Wall -Wextra -Werror -o "$WORK/refreshprobe" "$HERE/refreshprobe.c" $(pkg-config --cflags --libs wayland-client)
# a second redstar-comp as a window inside the first (like the AppImage on KDE), with the probe inside it
cat > "$WORK/nested.sh" <<NESTED
#!/bin/sh
unset DISPLAY REDSTAR_COMP_SOCKET
exec "$COMP" -- "$WORK/refreshprobe" 2>"$WORK/nested.log"
NESTED
chmod +x "$WORK/nested.sh"

mkdir -p "$WORK/runtime" "$WORK/config"
chmod 700 "$WORK/runtime"
unset DISPLAY WAYLAND_DISPLAY
export XDG_RUNTIME_DIR="$WORK/runtime" XDG_CONFIG_HOME="$WORK/config"
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1 REDSTAR_SHADOWS=1
status=0
timeout 150 "$COMP" -- sh -c '"$1" && echo "--- native Wayland window ---" && "$2" && echo "--- keyboard and mouse ---" && "$3" && echo "--- nested (a window on another desktop) ---" && "$4"' \
	sh "$WORK/comptest" "$WORK/nativetest" "$WORK/inputtest" "$WORK/nested.sh" \
	2>"$WORK/comp.log" || status=$?
# a renderer wlroots can't make (like GL on a VM without 3D graphics): redstar-comp falls back to drawing in software
if [ "$status" = 0 ]; then
	WLR_RENDERER=unavailable timeout 30 "$COMP" -- true 2>"$WORK/fallback.log" && grep -q "drawing in software" "$WORK/fallback.log" \
		&& echo "ok   software drawing when GPU drawing isn't available" \
		|| { echo "FAIL no software fallback for the renderer"; tail -n 20 "$WORK/fallback.log"; status=1; }
fi
if [ "$status" != 0 ]; then
	echo "---- redstar-comp log ----"
	tail -n 60 "$WORK/comp.log"
	[ -s "$WORK/nested.log" ] && { echo "---- nested redstar-comp log ----"; tail -n 30 "$WORK/nested.log"; }
fi
exit "$status"
