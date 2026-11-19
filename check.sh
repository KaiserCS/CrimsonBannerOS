#!/usr/bin/env bash
# Builds Red Star OS and runs every automated check. Run this before ./build-appimage.sh.
#   ./check.sh          build + C# tests (every window opens, terminal emulator, pty, browser data) + ad-blocker tests
#   ./check.sh --full   also the web-engine tests (needs: npm i -g playwright && npx playwright install chromium)
#   ./check.sh --screenshots   also saves a picture of every window in ./screenshots (open screenshots/index.html)
# The same checks run on GitHub for every push (.github/workflows/check.yml).
set -euo pipefail
# no telemetry from the .NET tools (PRINCIPLES.md, 5)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
FULL=0; SHOTS=0
for a in "$@"; do case "$a" in --full) FULL=1 ;; --screenshots) SHOTS=1 ;; esac; done
cd "$(dirname "$0")"
ok() { printf '\033[32m✔ %s\033[0m\n' "$1"; }

echo "==> Building"
dotnet build RedStarOS/RedStarOS.csproj -c Release -nologo -v quiet
ok "build"

echo "==> C# tests"
if [ "$SHOTS" = 1 ]; then
  rm -rf screenshots
  REDSTAR_SCREENSHOTS="$PWD/screenshots" dotnet test tests/RedStarOS.Tests/RedStarOS.Tests.csproj -c Release -nologo --logger "console;verbosity=minimal"
  python3 .github/scripts/summary.py shots screenshots | head -1
  ok "C# tests (screenshots in screenshots/index.html)"
else
  dotnet test tests/RedStarOS.Tests/RedStarOS.Tests.csproj -c Release -nologo --logger "console;verbosity=minimal"
  ok "C# tests"
fi

if command -v node >/dev/null 2>&1; then
  echo "==> Ad-blocker tests"
  node webengine/test/adblock.test.js | tail -1
  ok "ad blocker"
  node webengine/test/gpu.test.js | tail -1
  ok "web engine GPU switches"
  if [ "$FULL" = 1 ]; then
    echo "==> Web engine tests"
    node webengine/test/run-tests.js | tail -1
    ok "web engine"
  fi
fi
if command -v Xvfb >/dev/null 2>&1 && command -v gcc >/dev/null 2>&1 && [ -f /usr/include/X11/Xlib.h ]; then
  echo "==> Window manager tests (Xvfb)"
  wm/test/run-tests.sh | grep "passed"
  ok "window manager"
  if python3 -c "import playwright" 2>/dev/null; then
    echo "==> Isolated session with a real app (Chromium under redstar-wm)"
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
echo "All checks passed."
