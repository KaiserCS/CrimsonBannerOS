#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Builds Crimson Banner OS's Arch packages from this tree and installs them, in one go (Arch, CachyOS, EndeavourOS
# and other pacman-based systems):
#
#   ./install-arch.sh               build, sign and install (run it again to update to what you have checked out)
#   ./install-arch.sh --pull        git pull first, then the same
#   ./install-arch.sh --extras      also install the optional programs: drives, users, updates, video, screen reader,
#                                   screenshots, recording (the list is in packaging/arch/PKGBUILD)
#   ./install-arch.sh --yes         don't ask before installing (pacman --noconfirm)
#   ./install-arch.sh --rebuild     build even when this exact version is already in the repository
#   ./install-arch.sh --login-screen  also make Crimson Banner's login screen the one that starts (instead of SDDM,
#                                   GDM…; sudo crimson-greeter-setup disable goes back). Without it, you're asked once
#   ./install-arch.sh --uninstall   remove the packages and the repository's entry in /etc/pacman.conf
#
# Run it as yourself, not with sudo: it asks for your password when it needs it. The first run makes the project's
# signing key and the repository (/srv/crimson-banner), trusts the key in pacman, adds [crimson-banner] to
# /etc/pacman.conf (a copy of the old file is kept as pacman.conf.crimson-banner.bak) and replaces an earlier
# install-session.sh install. Afterwards, sign out and pick "Crimson Banner OS" on the login screen.
set -euo pipefail
cd "$(dirname "$0")"
HERE="$PWD"

NAME=crimson-banner
REPO_DIR="${REDSTAR_REPO:-/srv/crimson-banner}"
PACKAGES=(crimson-banner crimson-banner-shell crimson-banner-greeter redstar-comp redstar-wm crimson-online)
PULL=0; EXTRAS=0; YES=0; UNINSTALL=0; REBUILD=0; LOGIN=ask
for a in "$@"; do
  case "$a" in
    --pull) PULL=1 ;;
    --extras) EXTRAS=1 ;;
    --yes|-y) YES=1 ;;
    --uninstall) UNINSTALL=1 ;;
    --rebuild) REBUILD=1 ;;
    --login-screen) LOGIN=yes ;;
    -h|--help) sed -n '3,/^set -e/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $a (see ./install-arch.sh --help)" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1;31m==>\033[0m \033[1m%s\033[0m\n' "$*"; }
die() { printf '\033[31m%s\033[0m\n' "$*" >&2; exit 1; }
CONFIRM=(); [ "$YES" = 1 ] && CONFIRM=(--noconfirm)

[ "$(id -u)" != 0 ] || die "Run it as yourself, not with sudo or as root: makepkg won't build as root. It asks for your password when it needs it."
command -v pacman >/dev/null 2>&1 || die "This is for Arch-based systems (pacman). Elsewhere, see install-session.sh and build-appimage.sh."
command -v sudo >/dev/null 2>&1 || die "sudo is needed (pacman -S sudo, as root)."

# ── removing ──
if [ "$UNINSTALL" = 1 ]; then
  step "Removing Crimson Banner OS's packages"
  # first the login screen, so the computer still starts one after the packages are gone
  if [ "$(crimson-greeter-setup status 2>/dev/null)" = "Crimson Banner OS's login screen (greetd)" ]; then
    sudo crimson-greeter-setup disable
  fi
  installed=()
  for p in "${PACKAGES[@]}"; do pacman -Qq "$p" >/dev/null 2>&1 && installed+=("$p"); done
  if [ "${#installed[@]}" -gt 0 ]; then sudo pacman -Rns "${CONFIRM[@]}" "${installed[@]}"; else echo "None are installed."; fi
  if grep -q "^\[$NAME\]" /etc/pacman.conf; then
    step "Taking [$NAME] out of /etc/pacman.conf"
    sudo cp /etc/pacman.conf /etc/pacman.conf.$NAME.bak
    # the section runs from its header to the next header (or the end)
    sudo sed -i -e "/^# Crimson Banner OS (.*install-arch.sh)\$/d" -e "/^\[$NAME\]/,/^\[/{/^\[$NAME\]/d;/^\[/!d}" /etc/pacman.conf
  fi
  echo
  echo "Done. Your settings (~/.config/redstaros), the repository ($REPO_DIR) and the signing key are kept."
  exit 0
fi

# ── 1. what building needs ──
step "Build tools"
need=()
for p in base-devel git gnupg; do pacman -Qq "$p" >/dev/null 2>&1 || pacman -Qqg "$p" >/dev/null 2>&1 || need+=("$p"); done
if [ "${#need[@]}" -gt 0 ]; then sudo pacman -S --needed "${CONFIRM[@]}" "${need[@]}"; else echo "base-devel, git and gnupg are there."; fi
# (makepkg installs the rest, the packages' build dependencies, as it builds)

if [ "$PULL" = 1 ]; then
  step "Updating this checkout"
  git pull --ff-only
fi

# ── 2. the repository and its key (first run only) ──
step "Repository ($REPO_DIR)"
if [ ! -d "$REPO_DIR" ]; then
  sudo install -d -o "$(id -un)" -g "$(id -gn)" "$REPO_DIR"
  # an earlier repository in your home folder (tools/repo.sh before this script): keep its key and packages
  OLD="${XDG_DATA_HOME:-$HOME/.local/share}/redstaros/repo"
  if [ -d "$OLD" ] && [ "$OLD" != "$REPO_DIR" ]; then cp -a "$OLD/." "$REPO_DIR/"; echo "Moved the repository from $OLD."; fi
fi
[ -w "$REPO_DIR" ] || die "$REPO_DIR isn't writable by you: sudo chown $(id -un) $REPO_DIR"
export REDSTAR_REPO="$REPO_DIR"
tools/repo.sh key

# ── 3. build and sign ──
VERSION="$(tools/version.sh)"
PKGVER="$(tools/version.sh --pkgver)"
CARCH="$(uname -m)"
# a committed version that's already in the repository needs no second build; one with changes not committed
# (".local") always does, since the same version can stand for different contents
if [ "$REBUILD" = 0 ] && [[ "$PKGVER" != *.local ]] && \
   ls "$REPO_DIR/$CARCH/crimson-banner-shell-$PKGVER-"*.pkg.tar.zst >/dev/null 2>&1; then
  step "Crimson Banner OS $VERSION is already built (./install-arch.sh --rebuild builds it again)"
else
  step "Building the packages (the first build takes a while)"
  tools/repo.sh build
  # pacman keeps downloaded packages by name: an earlier build of this same version in its cache would no longer
  # match the repository, so it goes
  for p in "${PACKAGES[@]}"; do sudo rm -f /var/cache/pacman/pkg/"$p-$PKGVER"-*.pkg.tar.zst{,.sig}; done
fi

# ── 4. pacman trusts the key and knows the repository ──
step "Letting pacman trust the project's key"
FPR="$(cat "$REPO_DIR/signing-key")"
# pacman's keyring needs its own local key to vouch for ours (a fresh system or container may not have one yet)
if ! sudo gpg --homedir /etc/pacman.d/gnupg --batch --list-secret-keys 2>/dev/null | grep -q '^sec'; then
  sudo pacman-key --init
fi
sudo pacman-key --list-keys "$FPR" >/dev/null 2>&1 || sudo pacman-key --add "$REPO_DIR/$NAME.asc"
# every time, so a run that stopped halfway can't leave the key added but not trusted
sudo pacman-key --lsign-key "$FPR"

SERVER="file://$REPO_DIR/\$arch"
if grep -q "^\[$NAME\]" /etc/pacman.conf; then
  # there already; make sure it points here
  if ! sed -n "/^\[$NAME\]/,/^\[/p" /etc/pacman.conf | grep -qxF "Server = $SERVER"; then
    sudo cp /etc/pacman.conf /etc/pacman.conf.$NAME.bak
    sudo sed -i "/^\[$NAME\]/,/^\[/{s#^Server *=.*#Server = $SERVER#}" /etc/pacman.conf
    echo "Pointed [$NAME] in /etc/pacman.conf at $REPO_DIR."
  else
    echo "[$NAME] is in /etc/pacman.conf."
  fi
else
  sudo cp /etc/pacman.conf /etc/pacman.conf.$NAME.bak
  printf '\n# Crimson Banner OS (%s/install-arch.sh)\n[%s]\nSigLevel = Required DatabaseRequired\nServer = %s\n' \
    "$HERE" "$NAME" "$SERVER" | sudo tee -a /etc/pacman.conf >/dev/null
  echo "Added [$NAME] to /etc/pacman.conf (the old one is /etc/pacman.conf.$NAME.bak)."
fi

# ── 5. an earlier install-session.sh install would clash with the packages' files ──
if [ -e /usr/local/bin/redstaros-session ] || { [ -e /usr/share/xsessions/redstaros.desktop ] && ! pacman -Qqo /usr/share/xsessions/redstaros.desktop >/dev/null 2>&1; }; then
  step "Replacing the earlier install-session.sh install"
  sudo "$HERE/uninstall-session.sh"
fi

# ── 6. install ──
step "Installing Crimson Banner OS $VERSION"
# naming every package reinstalls a rebuild of the same version too
sudo pacman -Sy "${CONFIRM[@]}" "${PACKAGES[@]}"

if [ "$EXTRAS" = 1 ]; then
  step "Optional programs"
  # the optional dependencies of the crimson-banner package, as listed in the PKGBUILD
  mapfile -t extras < <(pacman -Qi crimson-banner | sed -n '/^Optional Deps/,/^[A-Z][a-z]* [A-Za-z]* *:/p' \
                       | grep -oE '^(Optional Deps *: *| +)[a-z0-9@._+-]+:' | sed -E 's/^(Optional Deps *: *| +)//; s/:$//')
  # only what this system's repositories have (one missing package would stop the whole install)
  have=(); missing=()
  for p in "${extras[@]}"; do if pacman -Si "$p" >/dev/null 2>&1; then have+=("$p"); else missing+=("$p"); fi; done
  if [ "${#have[@]}" -gt 0 ]; then sudo pacman -S --needed "${CONFIRM[@]}" "${have[@]}"; fi
  [ "${#missing[@]}" -eq 0 ] || echo "Not in your repositories, so skipped: ${missing[*]} (the AUR may have them)."
fi

# ── 7. the login screen ──
if [ "$(crimson-greeter-setup status 2>/dev/null)" != "Crimson Banner OS's login screen (greetd)" ]; then
  if [ "$LOGIN" = ask ] && [ "$YES" = 0 ] && [ -t 0 ]; then
    now="$(crimson-greeter-setup status 2>/dev/null || true)"
    printf '\nUse Crimson Banner OS'"'"'s own login screen from the next start, instead of %s? [y/N] ' "${now:-the current one}"
    read -r answer || answer=""
    case "$answer" in [yY]*) LOGIN=yes ;; esac
  fi
  if [ "$LOGIN" = yes ]; then
    step "Login screen"
    sudo crimson-greeter-setup enable
  fi
fi

echo
printf '\033[1;32mCrimson Banner OS %s is installed.\033[0m\n' "$VERSION"
echo "Sign out and choose \"Crimson Banner OS\" (or \"Crimson Banner OS (Wayland)\") on the login screen."
echo "To update later: ./install-arch.sh --pull"
[ "$EXTRAS" = 1 ] || echo "Optional programs (drives, users, video, screen reader…): ./install-arch.sh --extras"
