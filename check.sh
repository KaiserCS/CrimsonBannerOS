#!/usr/bin/env bash
# Builds Red Star OS and runs every automated check. Run this before ./build-appimage.sh.
#   ./check.sh          build + C# tests (every window opens, terminal emulator, pty, browser data), and the window
#                       manager, compositor and Red Star Online tests when their libraries are installed
#   ./check.sh --screenshots   also saves a picture of every window in ./screenshots (open screenshots/index.html)
#   ./check.sh --packages      also builds the Arch packages into Crimson Banner's pacman repository (tools/repo.sh;
#                              signed once tools/repo.sh key has made the key, else unsigned)
#   ./check.sh --offline       packages only from Red Star's own feed (tools/packages.sh); that's also the default
#                              once the feed is filled. --online uses nuget.org instead.
# The same checks run on GitHub for every push (.github/workflows/check.yml).
set -euo pipefail
# no telemetry from the .NET tools (PRINCIPLES.md, 5)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
SHOTS=0; OFFLINE=auto; PACKAGES=0
for a in "$@"; do case "$a" in --screenshots) SHOTS=1 ;; --offline) OFFLINE=1 ;; --online) OFFLINE=0 ;; --packages) PACKAGES=1 ;; esac; done
cd "$(dirname "$0")"
ok() { printf '\033[32m✔ %s\033[0m\n' "$1"; }

FEED="$(tools/packages.sh where)"
if [ "$OFFLINE" = auto ]; then
  OFFLINE=0
  [ -n "$(ls -A "$FEED" 2>/dev/null)" ] && [ -f RedStarOS/packages.lock.json ] && OFFLINE=1
fi
NORESTORE=()
if [ "$OFFLINE" = 1 ]; then
  echo "==> Packages from Crimson Banner's own feed ($FEED)"
  tools/packages.sh restore >/dev/null
  NORESTORE=(--no-restore)
  ok "packages (offline, versions and hashes from packages.lock.json)"
fi

echo "==> Version ($(tools/version.sh))"
tools/test-version.sh >/dev/null || { tools/test-version.sh; exit 1; }
[ "$(sed -n 's/^pkgver=//p' packaging/arch/PKGBUILD)" = "$(tools/version.sh --base)" ] \
  || { echo "packaging/arch/PKGBUILD's pkgver isn't VERSION ($(tools/version.sh --base)): tools/release.sh sets both" >&2; exit 1; }
ok "tools/version.sh, tools/release.sh and the PKGBUILD agree"

echo "==> Login screen scripts (packaging/greeter)"
sh -n packaging/greeter/crimson-greeter && bash -n packaging/greeter/crimson-greeter-setup
tools/test-greeter.sh >/dev/null || { tools/test-greeter.sh; exit 1; }
ok "crimson-greeter and crimson-greeter-setup"

echo "==> Publishing apps, updates and ISOs (tools/publish-app.sh, tools/publish-os.sh, packaging/crimson-repo, tools/split-iso.sh)"
bash -n tools/publish-app.sh && bash -n tools/publish-os.sh && bash -n tools/lib/gh-release.sh && bash -n packaging/crimson-repo && sh -n packaging/crimson-apps-repo
tools/test-publish-app.sh >/dev/null 2>&1 || { tools/test-publish-app.sh; exit 1; }
bash -n tools/split-iso.sh && sh -n tools/merge-iso.sh
for f in apps/*/PKGBUILD; do bash -n "$f" || exit 1; done
tools/test-split-iso.sh >/dev/null 2>&1 || { tools/test-split-iso.sh; exit 1; }
ok "the catalogue, its signature, the apps repository, and ISOs split and joined"

echo "==> Principles (PRINCIPLES.md)"
tools/test-principles.sh || exit 1
ok "PRINCIPLES.md's scores match its tables, and its exceptions have review dates"

echo "==> Icon theme (icons/CrimsonBanner)"
tools/test-icon-theme.sh >/dev/null || { tools/test-icon-theme.sh; exit 1; }
ok "the Crimson Banner icon theme is whole"

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
  echo "==> Crimson Online (GTK 4 + WebKitGTK)"
  if ! build_out="$(browser/build.sh 2>&1)"; then
    printf '%s\n' "$build_out" | grep -E "error|Error|FAILED" | head -n 30
    echo "Crimson Online didn't build"
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
  echo "(Crimson Online skipped: it needs WebKitGTK 6.0, GTK 4, json-glib, libsoup 3 and meson — Arch/CachyOS: pacman -S webkitgtk-6.0 gtk4 json-glib libsoup3 meson)"
fi
if pkg-config --exists gtk4 json-glib-1.0 2>/dev/null && command -v valac >/dev/null 2>&1 && command -v meson >/dev/null 2>&1; then
  echo "==> GTK theme (crimson-gtk) and login screen (crimson-login: Vala + GTK 4)"
  if ! build_out="$(crimson-gtk/build.sh 2>&1)"; then
    printf '%s\n' "$build_out" | grep -E "error|Error|FAILED" | head -n 30
    echo "crimson-gtk didn't build"
    exit 1
  fi
  if ! out="$(meson test -C crimson-gtk/build --print-errorlogs 2>&1)"; then
    printf '%s\n' "$out" | tail -n 40
    exit 1
  fi
  if ! build_out="$(greeter/build.sh 2>&1)"; then
    printf '%s\n' "$build_out" | grep -E "error|Error|FAILED" | head -n 30
    echo "crimson-login didn't build"
    exit 1
  fi
  if ! out="$(meson test -C greeter/build --print-errorlogs 2>&1)"; then
    printf '%s\n' "$out" | tail -n 40
    exit 1
  fi
  printf '%s\n' "$out" | grep -E "^Ok:|^Fail:"
  # its preview drawn headless inside redstar-comp (when that's built)
  if [ -x compositor/build/redstar-comp ]; then
    shot="$(mktemp --suffix=.png)"
    greeter/test/run-screenshot.sh "$shot" >/dev/null || { greeter/test/run-screenshot.sh "$shot"; rm -f "$shot"; exit 1; }
    rm -f "$shot"
  fi
  ok "crimson-gtk and crimson-login"
else
  echo "(crimson-login skipped: it needs Vala, GTK 4, json-glib and meson — Arch/CachyOS: pacman -S vala gtk4 json-glib meson)"
fi
echo "==> Arch packaging"
bash -n packaging/arch/PKGBUILD && bash -n tools/repo.sh && bash -n install-arch.sh && bash -n tools/iso.sh \
  && for f in iso/pkgs/*/PKGBUILD; do bash -n "$f" || exit 1; done \
  && sh -n iso/profile/airootfs/usr/local/bin/crimson-live-cleanup && sh -n iso/profile/airootfs/usr/local/bin/crimson-install && sh -n iso/profile/airootfs/usr/local/bin/crimson-os-release \
  && sh -n iso/profile/airootfs/usr/local/bin/crimson-live-test && for f in .github/scripts/iso-*.sh; do bash -n "$f" || exit 1; done
if [ "$PACKAGES" = 1 ]; then
  if command -v makepkg >/dev/null 2>&1; then
    if [ -f "$(tools/repo.sh where)/signing-key" ] || [ -n "${REDSTAR_REPO_KEY:-}" ]; then
      tools/repo.sh build && tools/repo.sh check
    else
      tools/repo.sh build --unsigned
    fi
    ok "Arch packages ($(tools/repo.sh where))"
  else
    echo "(--packages skipped: building the Arch packages needs makepkg, on an Arch-based system)"
  fi
else
  ok "PKGBUILD and tools/repo.sh (./check.sh --packages builds the packages)"
fi
echo "All checks passed."
