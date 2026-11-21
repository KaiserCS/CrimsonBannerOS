#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Crimson Banner's own pacman repository (PRINCIPLES.md, "Distribution base"): the packages from packaging/arch,
# built from this tree, signed with the project's key and listed in a signed repository database. It's a plain
# folder, so any web server, a USB stick or file:// can serve it; no hosted service is needed.
#
#   tools/repo.sh key                  make the signing key (once, on the machine that signs releases)
#   tools/repo.sh build                build every package from this tree and add it to the repository
#   tools/repo.sh check                verify every signature in the repository
#   tools/repo.sh pacman-conf          print how a machine adds the repository (pacman.conf and the key)
#   tools/repo.sh export FILE          the repository as one .tar file, to carry to another machine
#   tools/repo.sh source               only make the source tarball (packaging/arch/crimson-banner-VERSION.tar.gz)
#   tools/repo.sh where                print the repository's folder
#
# The repository is $REDSTAR_REPO, else ~/.local/share/redstaros/repo, with one folder per architecture.
# The key is $REDSTAR_REPO_KEY (a GnuPG fingerprint), else the one 'key' made. build --unsigned skips signing.
# Needs an Arch-based system: makepkg, repo-add (pacman) and gpg.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
REPO="${REDSTAR_REPO:-${XDG_DATA_HOME:-$HOME/.local/share}/redstaros/repo}"
NAME=crimson-banner
CARCH="${CARCH:-$(uname -m)}"
DIR="$REPO/$CARCH"
WORK="${REDSTAR_REPO_WORK:-${XDG_CACHE_HOME:-$HOME/.cache}/redstaros/pkgbuild}"
KEYFILE="$REPO/signing-key"

need() { command -v "$1" >/dev/null 2>&1 || { echo "$1 is missing: $2" >&2; exit 1; }; }

key_id() {
  if [ -n "${REDSTAR_REPO_KEY:-}" ]; then echo "$REDSTAR_REPO_KEY"; return; fi
  [ -f "$KEYFILE" ] && cat "$KEYFILE"
}

# the version: the shell's <Version>, plus how many commits and which one when this is a git checkout
version() {
  local base
  base="$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' RedStarOS/RedStarOS.csproj | head -n 1)"
  if git rev-parse --git-dir >/dev/null 2>&1; then
    local n h dirty=""
    n="$(git rev-list --count HEAD)"; h="$(git rev-parse --short=7 HEAD)"
    git diff --quiet HEAD -- 2>/dev/null || dirty=".local"
    echo "$base.r$n.g$h$dirty"
  else
    echo "$base"
  fi
}

# the tree as it is now (committed or not), without build output
make_source() {
  local ver="$1" out="$2" list
  list="$(mktemp)"
  if git rev-parse --git-dir >/dev/null 2>&1; then
    git ls-files -z --cached --others --exclude-standard | while IFS= read -r -d '' f; do
      # the prebuilt window manager is rebuilt by the package
      [ -e "$f" ] && [ "$f" != wm/redstar-wm ] && printf '%s\0' "$f"
    done > "$list"
  else
    find . \( -path ./.git -o -name bin -o -name obj -o -name build -o -name node_modules \) -prune -o -type f \
      ! -path ./wm/redstar-wm -print0 | sed -z 's#^\./##' > "$list"
  fi
  tar --sort=name --owner=0 --group=0 --transform "s#^#$NAME-$ver/#" -czf "$out" --null -T "$list"
  rm -f "$list"
}

case "${1:-}" in
  key)
    need gpg "install gnupg"
    if [ -n "$(key_id)" ]; then echo "The repository already has a signing key: $(key_id)"; exit 0; fi
    mkdir -p "$REPO"
    uid="Crimson Banner OS packages <packages@crimson-banner.invalid>"
    # no expiry date: an expired key would stop every machine's updates at once. GnuPG keeps a revocation
    # certificate in ~/.gnupg/openpgp-revocs.d for the day the key has to be withdrawn.
    gpg --batch --passphrase '' --quick-gen-key "$uid" ed25519 sign never
    fpr="$(gpg --with-colons --list-keys "$uid" | awk -F: '/^fpr:/ {print $10; exit}')"
    echo "$fpr" > "$KEYFILE"
    gpg --armor --export "$fpr" > "$REPO/$NAME.asc"
    echo "Signing key $fpr"
    echo "Public key: $REPO/$NAME.asc (give it out with the repository). Back up ~/.gnupg: the private key stays there."
    ;;

  source)
    ver="$(version)"
    make_source "$ver" "packaging/arch/$NAME-$ver.tar.gz"
    echo "packaging/arch/$NAME-$ver.tar.gz"
    ;;

  build)
    need makepkg "build on an Arch-based system (pacman -S base-devel)"
    need repo-add "pacman -S pacman"
    [ "$(id -u)" != 0 ] || { echo "makepkg won't run as root: run this as your own user" >&2; exit 1; }
    SIGN=1; [ "${2:-}" = "--unsigned" ] && SIGN=0
    KEY="$(key_id || true)"
    if [ "$SIGN" = 1 ] && [ -z "$KEY" ]; then
      echo "No signing key yet: run tools/repo.sh key first (or build --unsigned for a local test)." >&2
      exit 1
    fi
    ver="$(version)"
    rm -rf "$WORK"; mkdir -p "$WORK" "$DIR"
    sed "s/^pkgver=.*/pkgver=$ver/" packaging/arch/PKGBUILD > "$WORK/PKGBUILD"
    make_source "$ver" "$WORK/$NAME-$ver.tar.gz"
    echo "==> Building $NAME $ver for $CARCH"
    SIGNARGS=(); [ "$SIGN" = 1 ] && SIGNARGS=(--sign --key "$KEY")
    (cd "$WORK" && PKGDEST="$WORK/out" makepkg --syncdeps --noconfirm --needed --cleanbuild "${SIGNARGS[@]}")
    cp "$WORK"/out/*.pkg.tar.* "$DIR/"
    REPOARGS=(); [ "$SIGN" = 1 ] && REPOARGS=(--sign --key "$KEY" --verify)
    # --remove drops the previous version of each package (and its signature) from the folder
    (cd "$DIR" && repo-add --remove "${REPOARGS[@]}" "$NAME.db.tar.zst" \
      $(cd "$WORK/out" && ls -1 ./*.pkg.tar.zst | sed 's#^\./##'))
    [ "$SIGN" = 1 ] && gpg --armor --export "$KEY" > "$REPO/$NAME.asc"
    echo "Repository: $DIR"
    ls -1 "$DIR"
    ;;

  check)
    need gpg "install gnupg"
    [ -d "$DIR" ] || { echo "No repository at $DIR yet: run tools/repo.sh build" >&2; exit 1; }
    bad=0; n=0
    for f in "$DIR"/*.pkg.tar.zst "$DIR/$NAME.db.tar.zst"; do
      [ -e "$f" ] || continue
      n=$((n + 1))
      if [ ! -f "$f.sig" ]; then echo "unsigned: ${f##*/}"; bad=1; continue; fi
      if gpg --batch --verify "$f.sig" "$f" 2>/dev/null; then echo "signed: ${f##*/}"
      else echo "BAD SIGNATURE: ${f##*/}"; bad=1; fi
    done
    [ "$n" -gt 0 ] || { echo "The repository is empty" >&2; exit 1; }
    exit "$bad"
    ;;

  pacman-conf)
    KEY="$(key_id || true)"
    case "$REPO/" in
      "$HOME"/*)
        cat <<NOTE
# Note: pacman fetches as its own unprivileged user (alpm), which can't read inside your home folder. For this
# machine, keep the repository outside it once:
#   sudo install -d -o "\$USER" /srv/crimson-banner && cp -a "$REPO/." /srv/crimson-banner/
#   export REDSTAR_REPO=/srv/crimson-banner     (in ~/.profile, so tools/repo.sh builds there from now on)
# then run tools/repo.sh pacman-conf again.

NOTE
        ;;
    esac
    cat <<EOF
# 1. Trust the project's key (the file $REPO/$NAME.asc, shipped with the repository):
sudo pacman-key --add $REPO/$NAME.asc
sudo pacman-key --lsign-key ${KEY:-<fingerprint>}

# 2. Add to the end of /etc/pacman.conf (Server can be a file:// folder, a USB stick or any web server that
#    serves the folder):
[$NAME]
SigLevel = Required DatabaseRequired
Server = file://$REPO/\$arch

# 3. Install:
sudo pacman -Sy $NAME
EOF
    ;;

  export)
    [ -n "${2:-}" ] || { echo "usage: tools/repo.sh export FILE.tar" >&2; exit 1; }
    tar -C "$REPO" -cf "$2" .
    echo "Wrote $2"
    ;;

  where) echo "$REPO" ;;
  *) sed -n '3,18p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
