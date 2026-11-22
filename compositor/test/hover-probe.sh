#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# The installed desktop, drawn natively on Wayland inside a headless redstar-comp, with a virtual mouse moved slowly
# across the taskbar and the Start menu. The shell logs every hover enter/exit (REDSTAR_TRACE_POINTER), redstar-comp
# every change of the surface under the pointer (REDSTAR_COMP_TRACE_POINTER), and hover-report.py says whether any
# highlight dropped while the pointer was still on its control. Run as a normal user, after the packages are installed.
#   compositor/test/hover-probe.sh [native|x11]
set -u
MODE="${1:-native}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
chmod 700 "$WORK"
mkdir -p "$WORK/runtime" "$WORK/config/redstaros" "$WORK/data"
chmod 700 "$WORK/runtime"
gcc -O2 -Wall -Wextra -o "$WORK/hoverprobe" "$HERE/hoverprobe.c" $(pkg-config --cflags --libs wayland-client) -lm || exit 1

unset DISPLAY WAYLAND_DISPLAY
export XDG_RUNTIME_DIR="$WORK/runtime" XDG_CONFIG_HOME="$WORK/config" XDG_DATA_HOME="$WORK/data"
export WLR_BACKENDS=headless WLR_RENDERER=pixman WLR_LIBINPUT_NO_DEVICES=1 WLR_HEADLESS_OUTPUTS=1
export REDSTAR_SESSION=1 REDSTAR_TRACE_POINTER="$WORK/shell-pointer.log" REDSTAR_COMP_TRACE_POINTER=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
[ "$MODE" = native ] && export REDSTAR_NATIVE_WAYLAND=1 || export REDSTAR_NATIVE_WAYLAND=0
: > "$WORK/shell-pointer.log"
echo '{ "WelcomeDone": true }' > "$WORK/config/redstaros/settings.json"     # no welcome window over the desktop

# the taskbar runs along the bottom of the 1280×720 screen; the Start button is at its left end
timeout 150 redstar-comp -- sh -c '
	RedStarOS >"$1/shell.log" 2>&1 &
	sleep 35                       # the shell starting up and drawing its desktop and taskbar
	"$2" 1280 720 \
		0,700:640,700 640,700:0,700 0,706:640,706 640,694:0,694 \
		300,712:300,712:click 24,700:24,700:click \
		60,660:60,300 200,660:200,300 40,400:400,400 400,500:40,500 \
		> "$1/probe.log" 2>&1
	command -v grim >/dev/null && grim -s 0.5 "$1/screen.png"
	sleep 1
' sh "$WORK" "$WORK/hoverprobe" 2>"$WORK/comp.log"

echo "---- probe ----"; cat "$WORK/probe.log" 2>/dev/null
echo "---- shell log (last lines) ----"; tail -n 25 "$WORK/shell.log" 2>/dev/null
echo "---- redstar-comp log (last lines, without the pointer trace) ----"; grep -v "^pointer	" "$WORK/comp.log" | tail -n 25
echo "---- shell pointer log (first lines) ----"; head -n 30 "$WORK/shell-pointer.log"
# a small picture of the screen, as text (CI's files can't always be fetched): base64 of a PNG
[ -s "$WORK/screen.png" ] && { echo "---- screen.png (base64) ----"; base64 -w0 "$WORK/screen.png"; echo; echo "---- end screen.png ----"; }
python3 "$HERE/hover-report.py" "$WORK/shell-pointer.log" "$WORK/comp.log"
status=$?
[ -n "${HOVER_PROBE_KEEP:-}" ] && cp "$WORK"/*.log "$HOVER_PROBE_KEEP"/ 2>/dev/null
rm -rf "$WORK"
exit "$status"
