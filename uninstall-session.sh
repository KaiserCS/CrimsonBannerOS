#!/usr/bin/env bash
# Takes Red Star OS off the login screen again: undoes install-session.sh.
#   sudo ./uninstall-session.sh            removes the two sessions, the session launcher, redstar-comp and Red Star Online
#   sudo ./uninstall-session.sh --dry-run  only lists what it would remove
#
# Your Red Star OS settings and files (~/.config/redstaros, ~/Documents…) are not touched, nor is the program
# itself (the AppImage or your build). Safe to run more than once.
set -euo pipefail

DRY=0
[ "${1:-}" = "--dry-run" ] && DRY=1
if [ "$DRY" = 0 ] && [ "$(id -u)" != 0 ]; then
  echo "Run it with sudo: sudo $0 $*"
  exit 1
fi

FILES=(
  /usr/share/xsessions/redstaros.desktop                 # "Red Star OS" (X11)
  /usr/share/wayland-sessions/redstaros-wayland.desktop  # "Red Star OS (Wayland)"
  /usr/local/bin/redstaros-session                       # what both sessions start
  /usr/local/bin/redstar-comp                            # the compositor copy install-session.sh put there
  /usr/local/bin/redstar-online                          # Red Star Online (GTK + WebKitGTK)
  /usr/local/share/applications/org.redstaros.Online.desktop
)

# signed in to a Red Star session right now? removing it is fine, but say so
if [ "${XDG_CURRENT_DESKTOP:-}" = "RedStar" ] || [ "${REDSTAR_SESSION:-}" = "1" ]; then
  echo "Note: you're in a Crimson Banner OS session now. It keeps running; it just won't be offered at the next login."
fi

removed=0
for f in "${FILES[@]}"; do
  [ -e "$f" ] || continue
  if [ "$DRY" = 1 ]; then echo "would remove $f"
  else rm -f "$f" && echo "removed $f"
  fi
  removed=$((removed + 1))
done

if [ "$removed" = 0 ]; then
  echo "Nothing to remove: Crimson Banner OS isn't on the login screen."
elif [ "$DRY" = 1 ]; then
  echo "(dry run: nothing was removed)"
else
  echo "Crimson Banner OS is off the login screen. Your settings in ~/.config/redstaros are kept."
fi
