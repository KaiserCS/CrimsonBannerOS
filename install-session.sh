#!/usr/bin/env bash
# Adds "Red Star OS" (X11) and "Red Star OS (Wayland)" (experimental, needs redstar-comp) to your login screen.
# Usage: sudo ./install-session.sh /full/path/to/RedStarOS-x86_64.AppImage
# Also install a lightweight window manager, e.g.:  sudo pacman -S openbox   |   sudo apt install openbox
set -euo pipefail
APPIMAGE_PATH="${1:-}"
if [ -z "$APPIMAGE_PATH" ] || [ ! -f "$APPIMAGE_PATH" ]; then
  echo "Usage: sudo $0 /full/path/to/RedStarOS-x86_64.AppImage"; exit 1
fi
APPIMAGE_PATH="$(readlink -f "$APPIMAGE_PATH")"
chmod +x "$APPIMAGE_PATH"
cd "$(dirname "$0")/packaging"
sed "s#@APPIMAGE@#$APPIMAGE_PATH#" redstaros-session > /usr/local/bin/redstaros-session
chmod 755 /usr/local/bin/redstaros-session
install -Dm644 redstaros-xsession.desktop /usr/share/xsessions/redstaros.desktop
install -Dm644 redstaros-wayland.desktop /usr/share/wayland-sessions/redstaros-wayland.desktop
# the compositor, when it was built (compositor/build.sh); the AppImage looks next to itself and on PATH
if [ -x ../compositor/build/redstar-comp ]; then
  install -Dm755 ../compositor/build/redstar-comp /usr/local/bin/redstar-comp
fi
echo "Installed. Log out and choose 'Red Star OS' from the session menu."
