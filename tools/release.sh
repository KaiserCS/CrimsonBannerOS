#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Makes a Crimson Banner OS release (README, "Versions"): checks the tree, writes VERSION and the PKGBUILD's
# pkgver, adds the release's section to CHANGELOG.md (from the commits since the last release, for you to edit),
# commits "Crimson Banner OS X" and tags it vX, signed with the repository's key when there is one. Nothing is
# pushed: look it over, then git push origin main vX. The tag makes CI publish the AppImage and the ISOs as a
# release, and tools/publish-os.sh (from the tag) publishes the packages as installed systems' update.
#
#   tools/release.sh            the next version (tools/version.sh --next): YY.MM.0, or the next fix this month
#   tools/release.sh 26.12.0    a version you choose (it must be newer than the last one)
#   tools/release.sh --no-check skip ./check.sh (it runs first otherwise)
set -euo pipefail
cd "$(dirname "$0")/.."

CHECK=1; VER=""
for a in "$@"; do
  case "$a" in
    --no-check) CHECK=0 ;;
    -h|--help) sed -n '3,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) VER="$a" ;;
  esac
done
VER="${VER:-$(tools/version.sh --next)}"
VER="${VER#v}"
[[ "$VER" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "A version is three numbers, like 26.12.0 (got '$VER')" >&2; exit 1; }

git diff --quiet HEAD -- && [ -z "$(git ls-files --others --exclude-standard)" ] \
  || { echo "Commit or stash your changes first: a release is exactly one commit." >&2; exit 1; }
git rev-parse -q --verify "refs/tags/v$VER" >/dev/null && { echo "v$VER already exists." >&2; exit 1; }

PREV="$(tools/version.sh --base)"
PREV_TAG=""
git rev-parse -q --verify "refs/tags/v$PREV" >/dev/null && PREV_TAG="v$PREV"
if [ -n "$PREV_TAG" ] || [ "$VER" != "$PREV" ]; then
  # newer than the last release, by the same rules pacman uses
  newest="$(printf '%s\n%s\n' "$PREV" "$VER" | sort -V | tail -n 1)"
  [ "$newest" = "$VER" ] && [ "$VER" != "$PREV" ] || { echo "$VER isn't newer than $PREV." >&2; exit 1; }
fi

if [ "$CHECK" = 1 ]; then ./check.sh; fi

echo "$VER" > VERSION
sed -i "s/^pkgver=.*/pkgver=$VER/" packaging/arch/PKGBUILD

# CHANGELOG.md: the new section goes under the title, above the last release
range="${PREV_TAG:+$PREV_TAG..}HEAD"
notes="$(git log --no-merges --format='- %s' "$range" | grep -v '^- Crimson Banner OS [0-9]' || true)"
[ -n "$notes" ] || notes="- (no changes recorded)"
section="## $VER ($(date +%Y-%m-%d))"$'\n\n'"$notes"$'\n'
if grep -q "^## $VER " CHANGELOG.md 2>/dev/null; then
  # written by hand beforehand ("## 26.11.0 (not released yet)"): kept, with today's date
  sed -i "s/^## $VER (not released yet)/## $VER ($(date +%Y-%m-%d))/" CHANGELOG.md
  echo "CHANGELOG.md already has a section for $VER: keeping it."
else
  python3 - "$section" <<'PY'
import sys, re
section = sys.argv[1]
s = open('CHANGELOG.md').read()
m = re.search(r'^## ', s, re.M)
s = (s[:m.start()] + section + '\n' + s[m.start():]) if m else (s.rstrip('\n') + '\n\n' + section)
open('CHANGELOG.md', 'w').write(s)
PY
fi
"${EDITOR:-true}" CHANGELOG.md || true

git add VERSION packaging/arch/PKGBUILD CHANGELOG.md
git commit -q -m "Crimson Banner OS $VER"

# signed with the repository's key when there is one, so the tag and the packages vouch for each other
KEY="${REDSTAR_REPO_KEY:-}"
REPO="${REDSTAR_REPO:-${XDG_DATA_HOME:-$HOME/.local/share}/redstaros/repo}"
[ -z "$KEY" ] && [ -f "$REPO/signing-key" ] && KEY="$(cat "$REPO/signing-key")"
if [ -n "$KEY" ]; then
  git tag -s -u "$KEY" -m "Crimson Banner OS $VER" "v$VER"
else
  git tag -a -m "Crimson Banner OS $VER" "v$VER"
fi

echo
echo "Crimson Banner OS $VER: committed and tagged v$VER$( [ -n "$KEY" ] && echo " (signed)")."
echo "Look it over (git show v$VER), then:  git push origin main v$VER"
echo "then publish it as an update for installed systems:  git checkout v$VER && tools/publish-os.sh && git checkout main"
