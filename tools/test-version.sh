#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Tests for tools/version.sh and the version scheme (README, "Versions"), in throwaway git repositories. Run by
# ./check.sh. Where pacman's vercmp is installed, also checks that pacman orders the versions the same way.
set -euo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
fail=0
eq() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: got '$2', expected '$3'"; fail=1; fi; }

repo="$T/repo"; mkdir -p "$repo/tools"
cp "$SRC/tools/version.sh" "$repo/tools/"
cd "$repo"
git init -q; git config user.email t@example.invalid; git config user.name test; git config commit.gpgsign false
git config tag.gpgsign false
echo 26.11.0 > VERSION; echo a > file; git add -A; git commit -qm "set 26.11.0"

eq "the commit that sets VERSION is the release" "$(tools/version.sh)" "26.11.0"
eq "--base" "$(tools/version.sh --base)" "26.11.0"
eq "--pkgver of a release" "$(tools/version.sh --pkgver)" "26.11.0"
eq "--next before it is tagged is VERSION itself" "$(tools/version.sh --next)" "26.11.0"

echo b >> file; git commit -qam two; echo c >> file; git commit -qam three
h="$(git rev-parse --short=7 HEAD)"
eq "two commits later" "$(tools/version.sh)" "26.11.0+2.g$h"
eq "pacman's form" "$(tools/version.sh --pkgver)" "26.11.0.r2.g$h"

echo d >> file
eq "changes not committed" "$(tools/version.sh)" "26.11.0+2.g$h.local"
git checkout -q file

mkdir -p wm; echo bin > wm/redstar-wm; git add wm; git commit -qm "prebuilt wm"; echo rebuilt > wm/redstar-wm
h="$(git rev-parse --short=7 HEAD)"
eq "a rebuilt redstar-wm isn't a local change" "$(tools/version.sh)" "26.11.0+3.g$h"
git checkout -q wm/redstar-wm

git tag -a -m r v26.11.0 HEAD~1
eq "counted from the tag once there is one" "$(tools/version.sh)" "26.11.0+1.g$h"
next="$(date +%y.%m | sed 's/\.0/./')"
case "26.11" in "$next") want="26.11.1" ;; *) want="$next.0" ;; esac
eq "--next after a release" "$(tools/version.sh --next)" "$want"

# a source tarball: no .git, the stamp tools/repo.sh writes
mkdir -p "$T/tarball/tools"; cp tools/version.sh "$T/tarball/tools/"; cp VERSION "$T/tarball/"
echo "26.11.0+7.gabcdef1" > "$T/tarball/.build-version"
eq "a tarball's stamp" "$("$T/tarball/tools/version.sh")" "26.11.0+7.gabcdef1"
eq "a tarball's stamp, pacman's form" "$("$T/tarball/tools/version.sh" --pkgver)" "26.11.0.r7.gabcdef1"
echo "26.11.0+gabcdef1" > "$T/tarball/.build-version"
eq "a shallow build's stamp, pacman's form" "$("$T/tarball/tools/version.sh" --pkgver)" "26.11.0.r0.gabcdef1"
# ...also when the tarball was unpacked inside another checkout (makepkg in a folder under git)
mkdir -p "$repo/inner/tools"; cp tools/version.sh "$repo/inner/tools/"; cp VERSION "$repo/inner/"
echo "26.11.0+7.gabcdef1" > "$repo/inner/.build-version"
eq "a tarball inside a checkout uses its stamp" "$("$repo/inner/tools/version.sh")" "26.11.0+7.gabcdef1"
rm -rf "$repo/inner"

# a shallow clone that can't see the release: a development build, distance unknown
git clone -q --depth 1 "file://$repo" "$T/shallow" 2>/dev/null
h="$(git -C "$T/shallow" rev-parse --short=7 HEAD)"
eq "a shallow clone" "$("$T/shallow/tools/version.sh")" "26.11.0+g$h"

# tools/release.sh: VERSION, the PKGBUILD's pkgver, CHANGELOG.md, one commit and its tag
rel="$T/rel"; mkdir -p "$rel/tools" "$rel/packaging/arch"
cp "$SRC/tools/version.sh" "$SRC/tools/release.sh" "$rel/tools/"
cd "$rel"
git init -q; git config user.email t@example.invalid; git config user.name test; git config commit.gpgsign false
echo 26.11.0 > VERSION; printf 'pkgname=x\npkgver=26.11.0\n' > packaging/arch/PKGBUILD
printf '# Changes\n\n## 26.11.0 (not released yet)\n\n- written by hand\n' > CHANGELOG.md
git add -A; git commit -qm "first"; echo x > f; git add f; git commit -qm "Add f"
EDITOR=true REDSTAR_REPO="$T/norepo" tools/release.sh --no-check >/dev/null
eq "the first release is VERSION as it was" "$(git describe --tags)" "v26.11.0"
eq "its section keeps the hand-written notes" "$(grep -c 'written by hand' CHANGELOG.md)" "1"
eq "...with the date" "$(grep -c "^## 26.11.0 ($(date +%Y-%m-%d))" CHANGELOG.md)" "1"
eq "a build of it" "$(tools/version.sh)" "26.11.0"
echo y >> f; git commit -qam "Change f"
EDITOR=true REDSTAR_REPO="$T/norepo" tools/release.sh --no-check 26.12.0 >/dev/null
eq "a chosen version" "$(git describe --tags)" "v26.12.0"
eq "VERSION" "$(cat VERSION)" "26.12.0"
eq "the PKGBUILD's pkgver" "$(sed -n 's/^pkgver=//p' packaging/arch/PKGBUILD)" "26.12.0"
eq "its notes are the commits since the last release" "$(sed -n '/^## 26.12.0/,/^## 26.11.0/p' CHANGELOG.md | grep -c '^- ')" "1"
eq "the newest release comes first" "$(grep -m1 '^## ' CHANGELOG.md | cut -d' ' -f2)" "26.12.0"
if EDITOR=true REDSTAR_REPO="$T/norepo" tools/release.sh --no-check 26.11.5 >/dev/null 2>&1; then
  eq "an older version is refused" "accepted" "refused"
else
  eq "an older version is refused" "refused" "refused"
fi
cd "$repo"

# the order: releases, and development builds between them, the same for pacman as for sort -V
order=(1.3.0 26.11.0 26.11.0.r2.gaaaaaaa 26.11.0.r10.gbbbbbbb 26.11.1 26.12.0 27.1.0)
sorted="$(printf '%s\n' "${order[@]}" | sort -V | tr '\n' ' ')"
eq "sort -V order" "$sorted" "$(printf '%s ' "${order[@]}")"
if command -v vercmp >/dev/null 2>&1; then
  for ((i = 0; i + 1 < ${#order[@]}; i++)); do
    eq "pacman: ${order[i]} < ${order[i+1]}" "$(vercmp "${order[i]}" "${order[i+1]}")" "-1"
  done
else
  echo "(pacman's vercmp isn't installed: its order is checked on Arch)"
fi

exit "$fail"
