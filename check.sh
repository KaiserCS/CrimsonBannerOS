#!/usr/bin/env bash
# Builds Red Star OS and runs every automated check. Run this before ./build-appimage.sh.
#   ./check.sh          build + C# tests (every window opens, terminal emulator, pty, browser data), and the window
#                       manager, compositor and Red Star Online tests when their libraries are installed
#   ./check.sh --screenshots   also saves a picture of every window in ./screenshots (open screenshots/index.html)
#   ./check.sh --offline       packages only from Red Star's own feed (tools/packages.sh); that's also the default
#                              once the feed is filled. --online uses nuget.org instead.
# The same checks run on GitHub for every push (.github/workflows/check.yml).
set -euo pipefail
# no telemetry from the .NET tools (PRINCIPLES.md, 5)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
SHOTS=0; OFFLINE=auto
for a in "$@"; do case "$a" in --screenshots) SHOTS=1 ;; --offline) OFFLINE=1 ;; --online) OFFLINE=0 ;; esac; done
cd "$(dirname "$0")"
ok() { printf '\033[32m✔ %s\033[0m\n' "$1"; }

FEED="$(tools/packages.sh where)"
if [ "$OFFLINE" = auto ]; then
  OFFLINE=0
  [ -n "$(ls -A "$FEED" 2>/dev/null)" ] && [ -f RedStarOS/packages.lock.json ] && OFFLINE=1
fi
NORESTORE=()
if [ "$OFFLINE" = 1 ]; then
  echo "==> Packages from Red Star's own feed ($FEED)"
  tools/packages.sh restore >/dev/null
  NORESTORE=(--no-restore)
  ok "packages (offline, versions and hashes from packages.lock.json)"
fi

echo "==> Building"
dotnet build RedStarOS/RedStarOS.csproj -c Release -nologo -v quiet "${NORESTORE[@]}"
ok "build"

echo "==> C# tests"
if [ "$SHOTS" = 1 ]; then
  rm -rf screenshots
  REDSTAR_SCREENSHOTS="$PWD/screenshots" dotnet test tests/RedStarOS.Tests/RedStarOS.Tests.csproj -c Release -nologo "${NORESTORE[@]}" --logger "console;verbosity=minimal"
  python3 .github/scripts/summary.py shots screenshots | head -1
  ok "C# tests (screenshots in screenshots/index.html)"
else
  dotnet test tests/RedStarOS.Tests/RedStarOS.Tests.csproj -c Release -nologo "${NORESTORE[@]}" --logger "console;verbosity=minimal"
  ok "C# tests"
fi

if command -v Xvfb >/dev/null 2>&1 && command -v gcc >/dev/null 2>&1 && [ -f /usr/include/X11/Xlib.h ]; then
  echo "==> Window manager tests (Xvfb)"
  wm/test/run-tests.sh | grep "passed"
  ok "window manager"
  if python3 -c "import gi; gi.require_version('Gtk', '3.0'); from gi.repository import Gtk" 2>/dev/null; then
    echo "==> Isolated session with a real program (GTK under redstar-wm)"
    python3 wm/test/session_test.py | tail -1
    ok "isolated session"
  fi
else
  echo "(window manager tests skipped: they need Xvfb, gcc and the X11 headers)"
fi
if pkg-config --exists wlroots-0.20 2>/dev/null && command -v meson >/dev/null 2>&1 && command -v Xwayland >/dev/null 2>&1; then
  echo "==> Wayland compositor (redstar-comp on wlroots' virtual screen)"
  if ! build_out="$(compositor/build.sh 2>&1)"; then
    printf '%s\n' "$build_out" | grep -E "error|Error|FAILED" | head -n 30
    echo "redstar-comp didn't build"
    exit 1
  fi
  # the three test clients' totals; on a failure, every failed check and redstar-comp's log
  if out="$(compositor/test/run-tests.sh 2>&1)"; then
    printf '%s\n' "$out" | grep -E "passed, [0-9]+ failed"
  else
    printf '%s\n' "$out" | grep -E "^FAIL|passed, [0-9]+ failed|^---- redstar-comp log|^\s|\[ERROR\]" | tail -n 80
    exit 1
  fi
  ok "compositor"
else
  echo "(compositor tests skipped: they need wlroots 0.20, meson and Xwayland — Arch/CachyOS: pacman -S wlroots0.20 meson xorg-xwayland)"
fi
if pkg-config --exists webkitgtk-6.0 gtk4 json-glib-1.0 libsoup-3.0 2>/dev/null && command -v meson >/dev/null 2>&1; then
  echo "==> Red Star Online (GTK 4 + WebKitGTK)"
  if ! build_out="$(browser/build.sh 2>&1)"; then
    printf '%s\n' "$build_out" | grep -E "error|Error|FAILED" | head -n 30
    echo "Red Star Online didn't build"
    exit 1
  fi
  if ! out="$(meson test -C browser/build --print-errorlogs 2>&1)"; then
    printf '%s\n' "$out" | tail -n 40
    exit 1
  fi
  printf '%s\n' "$out" | grep -E "^Ok:|^Fail:"
  # the real browser, headless inside redstar-comp (when that's built)
  if [ -x compositor/build/redstar-comp ]; then
    if out="$(browser/test/run-selftest.sh 2>&1)"; then
      printf '%s\n' "$out" | grep -E "^[0-9]+ failed"
    else
      printf '%s\n' "$out" | grep -E "^FAIL|failed|exited with" | tail -n 40
      exit 1
    fi
  fi
  ok "Red Star Online"
else
  echo "(Red Star Online skipped: it needs WebKitGTK 6.0, GTK 4, json-glib, libsoup 3 and meson — Arch/CachyOS: pacman -S webkitgtk-6.0 gtk4 json-glib libsoup3 meson)"
fi
echo "All checks passed."
