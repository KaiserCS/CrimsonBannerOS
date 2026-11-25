#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Publishes Crimson Banner OS's packages to its update channels: the [crimson-banner] pacman repository
# (crimson-banner, the shell, the login screen, redstar-comp, redstar-wm, Crimson Online), signed with the project's
# key and uploaded to a GitHub release that is kept up to date in place. Installed systems have the repository turned
# on (crimson-repo enable os), so Settings → Software Updates brings them what's published. Two channels, one
# repository on two servers:
#   releases  "os-ARCH": each release, built from its tag. Every installed system, unless it chose testing
#   testing   "os-testing-ARCH": builds of main, for those who chose them (Settings → Software Updates, or
#             sudo crimson-repo channel testing). Publishing a release puts it here too, so testing never falls
#             behind the releases
# The signing key never leaves this computer.
#
#   tools/publish-os.sh                the newest release (the newest v* tag), after tools/release.sh and pushing
#                                      its tag; built from the tag in a temporary copy, so nothing is checked out
#   tools/publish-os.sh --tag v26.11.3 a particular release
#   tools/publish-os.sh --testing      main as it is on GitHub (origin/main), to the testing channel
#   tools/publish-os.sh --local        this tree as it is, into the folder only (/srv/crimson-banner-os, or with
#                                      --testing /srv/crimson-banner-os-testing): nothing uploaded, e.g. to serve
#                                      it from your own server or to try it
#
# Needs: makepkg and repo-add (Arch), gpg with the project's key, and for uploading gh (GitHub's CLI, signed in:
# gh auth login). $REDSTAR_OS_DIR / $REDSTAR_OS_TESTING_DIR change the folders, $REDSTAR_REPO_KEY the key,
# $REDSTAR_GITHUB_REPO the repository.
set -euo pipefail
cd "$(dirname "$0")/.."
SLUG="${REDSTAR_GITHUB_REPO:-KaiserCS/CrimsonBannerOS}"
ARCH="$(uname -m)"
STABLE_OUT="${REDSTAR_OS_DIR:-/srv/crimson-banner-os}"
TESTING_OUT="${REDSTAR_OS_TESTING_DIR:-/srv/crimson-banner-os-testing}"
STABLE_TAG="os-$ARCH"
TESTING_TAG="os-testing-$ARCH"
# the project's signing key (its public half: software/crimson-banner.gpg). Signing with another needs
# $REDSTAR_REPO_KEY set on purpose, since installed systems trust only the key their shell carries.
PROJECT_KEY=19E71BACDC32A511D657F1FA9EC9146D98BEECA0

LOCAL=0
TESTING=0
RELEASE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --local) LOCAL=1 ;;
    --testing) TESTING=1 ;;
    --tag) RELEASE="${2:?--tag needs a release tag, such as v26.11.3}"; shift ;;
    -h|--help) sed -n '3,/^set -e/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

[ "$TESTING" = 0 ] || [ -z "$RELEASE" ] || { echo "--testing publishes main, not a release: leave out --tag." >&2; exit 2; }
if [ "$TESTING" = 1 ]; then OUT="$TESTING_OUT"; TAG="$TESTING_TAG"; else OUT="$STABLE_OUT"; TAG="$STABLE_TAG"; fi

# shellcheck source=tools/lib/gh-release.sh
. tools/lib/gh-release.sh
step() { printf '\n\033[1;31m==>\033[0m \033[1m%s\033[0m\n' "$*"; }
die() { echo "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is missing: $2"; }
need gpg "pacman -S gnupg"
need makepkg "build on an Arch-based system (pacman -S base-devel)"
need vercmp "pacman -S pacman"

# ── what to build: a release's own commit (only that goes to everyone), main as pushed (testing), or with --local
#    this tree ──
SRC="$PWD"
cleanup() { [ "$SRC" = "$PWD" ] || git worktree remove --force "$SRC" >/dev/null 2>&1 || true; }
trap cleanup EXIT
FROM=""
if [ "$LOCAL" = 0 ]; then
  git fetch -q --tags origin 2>/dev/null || true
  if [ "$TESTING" = 1 ]; then
    git fetch -q origin main || die "Couldn't fetch main from GitHub."
    FROM="origin/main"
  else
    [ -n "$RELEASE" ] || RELEASE="$(git tag -l 'v[0-9]*' --sort=-v:refname | head -n 1)"
    [ -n "$RELEASE" ] || die "There's no release yet: make one with tools/release.sh and push its tag."
    git rev-parse -q --verify "refs/tags/$RELEASE" >/dev/null || die "There's no release $RELEASE (git tag -l 'v*' lists them)."
    FROM="$RELEASE"
  fi
  SRC="$(mktemp -d "${TMPDIR:-/tmp}/crimson-publish-os.XXXXXX")"
  git worktree add -q --detach "$SRC" "$FROM"
  [ -f "$SRC/packaging/crimson-repo" ] || die "$FROM is older than the update channel (it hasn't crimson-repo): publish a newer release."
fi
VER="$(cd "$SRC" && tools/version.sh --pkgver)"
# the version in a channel's folder (the shell's package), or nothing
published() { ls "$1/$ARCH"/crimson-banner-shell-*.pkg.tar.zst 2>/dev/null | sed -E 's#.*/crimson-banner-shell-(.*)-[0-9]+-[^-]+\.pkg\.tar\.zst$#\1#' | head -n 1; }

# ── the key ──
KEY="${REDSTAR_REPO_KEY:-}"
if [ -z "$KEY" ] && gpg --list-secret-keys "$PROJECT_KEY" >/dev/null 2>&1; then KEY="$PROJECT_KEY"; fi
for f in /srv/crimson-banner/signing-key "${XDG_DATA_HOME:-$HOME/.local/share}/redstaros/repo/signing-key"; do
  [ -n "$KEY" ] || { [ -f "$f" ] && KEY="$(cat "$f")"; } || true
done
[ -n "$KEY" ] || die "No signing key: the project's ($PROJECT_KEY) isn't in this computer's GnuPG keyring."
gpg --list-secret-keys "$KEY" >/dev/null 2>&1 || die "The signing key $KEY isn't in this computer's GnuPG keyring."
FPR="$(gpg --with-colons --fingerprint "$KEY" 2>/dev/null | awk -F: '/^fpr:/ {print $10; exit}')"
if [ "$LOCAL" = 0 ] && [ "$FPR" != "$PROJECT_KEY" ] && [ -z "${REDSTAR_REPO_KEY:-}" ]; then
  die "The key found ($FPR) isn't the project's ($PROJECT_KEY): installed systems wouldn't trust it. Set REDSTAR_REPO_KEY to sign with it anyway."
fi

# ── the folders ──
for d in "$OUT" "$TESTING_OUT"; do
  [ -d "$d" ] && continue
  if mkdir -p "$d" 2>/dev/null; then :; else sudo install -d -o "$(id -un)" -g "$(id -gn)" "$d"; fi
done

if [ "$LOCAL" = 0 ] && [ "$(published "$OUT")" = "$VER" ]; then
  # the same commit (the version names it): building it again would put different files under the same name
  step "Crimson Banner OS $VER is already built for this channel: uploading it as it is"
else
  step "Building and signing Crimson Banner OS $VER ($ARCH)${FROM:+, from $FROM}"
  (cd "$SRC" && REDSTAR_REPO="$OUT" REDSTAR_REPO_KEY="$KEY" REDSTAR_REPO_WORK="${XDG_CACHE_HOME:-$HOME/.cache}/redstaros/publish-os" \
    tools/repo.sh build)
fi
(cd "$SRC" && REDSTAR_REPO="$OUT" REDSTAR_REPO_KEY="$KEY" tools/repo.sh check)
export_public_key "$KEY"

if [ "$LOCAL" = 1 ]; then
  echo
  echo "Published into $OUT/$ARCH (nothing uploaded). Serve that folder, then on a machine:"
  if [ "$TESTING" = 1 ]; then echo "  sudo CRIMSON_OS_TESTING_SERVER='https://your.server/path/\$arch' crimson-repo channel testing"
  else echo "  sudo CRIMSON_OS_SERVER='https://your.server/path/\$arch' crimson-repo enable os"; fi
  exit 0
fi

step "Uploading to GitHub ($SLUG, release $TAG)"
need gh "pacman -S github-cli, then gh auth login"
gh auth status >/dev/null 2>&1 || die "Sign in to GitHub first: gh auth login"
ensure_testing() {
  ensure_release "$TESTING_TAG" "Crimson Banner OS testing builds ($ARCH)" "The testing channel of the [crimson-banner] pacman repository for $ARCH: builds of main, and each release as it comes out, signed with the project's key. Systems that chose testing update from it (Settings → Software Updates → Update channel, or sudo crimson-repo channel testing). Not checked as carefully as a release. Kept up to date by tools/publish-os.sh --testing; not a release in itself." prerelease
}
if [ "$TESTING" = 1 ]; then
  ensure_testing
  sync_release "$TAG" "$OUT/$ARCH"
  echo
  echo "Published $VER (main at $(git rev-parse --short "$FROM")) to the testing channel. Systems that chose testing get it at their next update."
  exit 0
fi
ensure_release "$TAG" "Crimson Banner OS updates ($ARCH)" "The [crimson-banner] pacman repository for $ARCH: the latest release's packages, signed with the project's key. Installed systems update from it (Settings → Software Updates). Kept up to date by tools/publish-os.sh; not a release in itself."
sync_release "$TAG" "$OUT/$ARCH"

# the release goes to testing too, unless testing already has something newer (main after the release)
have="$(published "$TESTING_OUT")"
if [ -n "$have" ] && [ "$(vercmp "$have" "$VER")" -gt 0 ]; then
  echo "The testing channel has $have, newer than $VER: left as it is."
else
  step "The testing channel gets $VER too (release $TESTING_TAG)"
  rm -rf "${TESTING_OUT:?}/$ARCH"; mkdir -p "$TESTING_OUT"
  cp -a "$OUT/$ARCH" "$TESTING_OUT/$ARCH"
  ensure_testing
  sync_release "$TESTING_TAG" "$TESTING_OUT/$ARCH"
fi
# the shell and the ISOs built from now on carry this: installers turn the repository on only once it exists
if [ ! -f software/os-repo ]; then
  printf '# [crimson-banner] is published (tools/publish-os.sh): installed systems update from it.\n' > software/os-repo
  echo "Wrote software/os-repo: commit it with the key, so new installs turn updates on."
fi
echo
echo "Published $VER. Installed systems get it at their next update."
git diff --quiet -- software/ && [ -z "$(git ls-files --others --exclude-standard software/)" ] \
  || echo "software/ changed (the key, os-repo): commit it, so the next shell and ISO carry them."
