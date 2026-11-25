#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Publishes a release's packages as Crimson Banner OS's update channel: the [crimson-banner] pacman repository
# (crimson-banner, the shell, the login screen, redstar-comp, redstar-wm, Crimson Online), built from the release's
# commit, signed with the project's key and uploaded to the "os-ARCH" GitHub release, which is kept up to date in
# place. Installed systems have the repository turned on (crimson-repo enable os), so Settings → Software Updates
# brings them the release. The signing key never leaves this computer.
#
#   tools/publish-os.sh           after tools/release.sh and pushing the tag, from the release's commit
#   tools/publish-os.sh --local   into the folder only (/srv/crimson-banner-os): nothing uploaded, e.g. to serve it
#                                 from your own server or to try it; any commit
#
# Needs: makepkg and repo-add (Arch), gpg, and for uploading gh (GitHub's CLI, signed in: gh auth login).
# $REDSTAR_OS_DIR changes the folder, $REDSTAR_REPO_KEY the key, $REDSTAR_GITHUB_REPO the repository.
set -euo pipefail
cd "$(dirname "$0")/.."
SLUG="${REDSTAR_GITHUB_REPO:-KaiserCS/CrimsonBannerOS}"
OUT="${REDSTAR_OS_DIR:-/srv/crimson-banner-os}"
ARCH="$(uname -m)"
TAG="os-$ARCH"

LOCAL=0
for a in "$@"; do
  case "$a" in
    --local) LOCAL=1 ;;
    -h|--help) sed -n '3,/^set -e/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $a" >&2; exit 2 ;;
  esac
done

# shellcheck source=tools/lib/gh-release.sh
. tools/lib/gh-release.sh
step() { printf '\n\033[1;31m==>\033[0m \033[1m%s\033[0m\n' "$*"; }
die() { echo "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is missing: $2"; }
need gpg "pacman -S gnupg"
need makepkg "build on an Arch-based system (pacman -S base-devel)"

# ── the release: only a release's own commit goes to everyone ──
VER="$(tools/version.sh --pkgver)"
if [ "$LOCAL" = 0 ]; then
  git rev-parse -q --verify "refs/tags/v$VER" >/dev/null && [ "$(git rev-parse "v$VER^{commit}")" = "$(git rev-parse HEAD)" ] \
    || die "HEAD isn't a release (v$VER): check out the release's tag first (git checkout vX), or use --local."
  git diff --quiet HEAD -- && [ -z "$(git ls-files --others --exclude-standard)" ] \
    || die "The tree has changes: published packages are exactly the release's commit."
fi

# ── the key (tools/repo.sh key, ./install-arch.sh) ──
KEY="${REDSTAR_REPO_KEY:-}"
for f in /srv/crimson-banner/signing-key "${XDG_DATA_HOME:-$HOME/.local/share}/redstaros/repo/signing-key"; do
  [ -n "$KEY" ] || { [ -f "$f" ] && KEY="$(cat "$f")"; } || true
done
[ -n "$KEY" ] || die "No signing key: run tools/repo.sh key (or ./install-arch.sh) first."
gpg --list-secret-keys "$KEY" >/dev/null 2>&1 || die "The signing key $KEY isn't in this computer's GnuPG keyring."

# ── the folder ──
if [ ! -d "$OUT" ]; then
  if mkdir -p "$OUT" 2>/dev/null; then :; else sudo install -d -o "$(id -un)" -g "$(id -gn)" "$OUT"; fi
fi

step "Building and signing Crimson Banner OS $VER ($ARCH)"
REDSTAR_REPO="$OUT" REDSTAR_REPO_KEY="$KEY" REDSTAR_REPO_WORK="${XDG_CACHE_HOME:-$HOME/.cache}/redstaros/publish-os" \
  tools/repo.sh build
REDSTAR_REPO="$OUT" REDSTAR_REPO_KEY="$KEY" tools/repo.sh check
export_public_key "$KEY"

if [ "$LOCAL" = 1 ]; then
  echo
  echo "Published into $OUT/$ARCH (nothing uploaded). Serve that folder, then on a machine:"
  echo "  sudo CRIMSON_OS_SERVER='https://your.server/path/\$arch' crimson-repo enable os"
  exit 0
fi

step "Uploading to GitHub ($SLUG, release $TAG)"
need gh "pacman -S github-cli, then gh auth login"
gh auth status >/dev/null 2>&1 || die "Sign in to GitHub first: gh auth login"
ensure_release "$TAG" "Crimson Banner OS updates ($ARCH)" "The [crimson-banner] pacman repository for $ARCH: the latest release's packages, signed with the project's key. Installed systems update from it (Settings → Software Updates). Kept up to date by tools/publish-os.sh; not a release in itself."
sync_release "$TAG" "$OUT/$ARCH"
# the shell and the ISOs built from now on carry this: installers turn the repository on only once it exists
if [ ! -f software/os-repo ]; then
  printf '# [crimson-banner] is published (tools/publish-os.sh): installed systems update from it.\n' > software/os-repo
  echo "Wrote software/os-repo: commit it with the key, so new installs turn updates on."
fi
echo
echo "Published $VER. Installed systems get it at their next update."
git diff --quiet -- software/ && [ -z "$(git ls-files --others --exclude-standard software/)" ] \
  || echo "software/ changed (the key, os-repo): commit it, so the next shell and ISO carry them."
