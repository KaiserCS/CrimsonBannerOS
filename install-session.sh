#!/usr/bin/env bash
# Adds Red Star OS to your login screen's session list (SDDM on KDE, GDM, LightDM…):
#   "Red Star OS"            the X11 session (redstar-wm + picom)
#   "Red Star OS (Wayland)"  Red Star inside its own compositor, redstar-comp (only when redstar-comp is built)
#
#   sudo ./install-session.sh                   uses what you built here: the newest AppImage in dist/, else the
#                                               dotnet build (RedStarOS/bin/Release or Debug/net8.0/RedStarOS)
#   sudo ./install-session.sh PATH              an AppImage, the RedStarOS program, or a folder that has it
#   sudo ./install-session.sh --uninstall       removes both sessions again (same as ./uninstall-session.sh)
#
# Build redstar-comp first for the Wayland session: compositor/build.sh (Arch/CachyOS: pacman -S wlroots0.20 meson).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
[ "$(id -u)" = 0 ] || { echo "Run it with sudo: sudo $0 $*"; exit 1; }

BIN=/usr/local/bin
XS=/usr/share/xsessions/redstaros.desktop
WS=/usr/share/wayland-sessions/redstaros-wayland.desktop

if [ "${1:-}" = "--uninstall" ]; then
  exec "$HERE/uninstall-session.sh"
fi

# ── which Red Star to start ──
find_app() {
  local p="$1"
  if [ -d "$p" ]; then
    for c in "$p/RedStarOS" "$p/usr/bin/RedStarOS" "$p/bin/Release/net8.0/RedStarOS" "$p/bin/Debug/net8.0/RedStarOS"; do
      [ -x "$c" ] && { echo "$c"; return; }
    done
    return
  fi
  [ -f "$p" ] && echo "$p"
}
APP=""
if [ -n "${1:-}" ]; then
  APP="$(find_app "$1")"
  [ -n "$APP" ] || { echo "No Crimson Banner OS program in $1 (give an AppImage, the RedStarOS program or its folder)."; exit 1; }
else
  APP="$(ls -t "$HERE"/dist/*.AppImage 2>/dev/null | head -n 1 || true)"
  for c in "$HERE/RedStarOS/bin/Release/net8.0/RedStarOS" "$HERE/RedStarOS/bin/Debug/net8.0/RedStarOS"; do
    [ -z "$APP" ] && [ -x "$c" ] && APP="$c"
  done
  [ -n "$APP" ] || { echo "Nothing built yet: run 'dotnet build RedStarOS/RedStarOS.csproj -c Release' (or ./build-appimage.sh) first."; exit 1; }
fi
APP="$(readlink -f "$APP")"
chmod +x "$APP"
echo "Crimson Banner OS: $APP"

sed "s#@APPIMAGE@#$APP#" "$HERE/packaging/redstaros-session" > "$BIN/redstaros-session"
chmod 755 "$BIN/redstaros-session"
install -Dm644 "$HERE/packaging/redstaros-xsession.desktop" "$XS"
# the lock screen checks the Linux password with the console sign-in rules (PAM service "crimson-banner")
install -Dm644 "$HERE/packaging/crimson-banner.pam" /etc/pam.d/crimson-banner
echo "  added the lock screen's sign-in rules (/etc/pam.d/crimson-banner)"
echo "  added \"Crimson Banner OS\" ($XS)"

# ── the Wayland session needs redstar-comp ──
COMP=""
for c in "$HERE/compositor/build/redstar-comp" "$(dirname "$APP")/redstar-comp"; do
  [ -z "$COMP" ] && [ -x "$c" ] && COMP="$c"
done
if [ -n "$COMP" ]; then
  install -Dm755 "$COMP" "$BIN/redstar-comp"
  install -Dm644 "$HERE/packaging/redstaros-wayland.desktop" "$WS"
  echo "  added \"Crimson Banner OS (Wayland)\" ($WS), compositor $BIN/redstar-comp"
else
  rm -f "$WS"
  echo "  not added: \"Crimson Banner OS (Wayland)\" — build redstar-comp first (compositor/build.sh), then run this again"
fi

# ── Red Star Online, the GTK + WebKitGTK browser (the previous one is used without it) ──
ONLINE=""
for c in "$HERE/browser/build/redstar-online" "$(dirname "$APP")/redstar-online"; do
  [ -z "$ONLINE" ] && [ -x "$c" ] && ONLINE="$c"
done
if [ -n "$ONLINE" ]; then
  install -Dm755 "$ONLINE" "$BIN/redstar-online"
  install -Dm644 "$HERE/browser/data/org.redstaros.Online.desktop" /usr/local/share/applications/org.redstaros.Online.desktop
  echo "  installed Crimson Online (GTK + WebKitGTK): $BIN/redstar-online"
  if ! command -v gst-inspect-1.0 >/dev/null 2>&1 || ! gst-inspect-1.0 autoaudiosink >/dev/null 2>&1; then
    echo "    note: pages will be silent without GStreamer's plugins — sudo pacman -S gst-plugins-good gst-plugins-bad gst-libav"
  fi
else
  echo "  not installed: the new Crimson Online — build it first (browser/build.sh); until then the previous browser is used"
fi

echo
echo "Log out. On the login screen pick the session before signing in — on KDE's login screen (SDDM) it's the"
echo "session menu at the bottom left. If a session returns straight to the login screen, see"
echo "~/.config/redstaros/session.log and ~/.local/share/sddm/wayland-session.log (or xorg-session.log)."
