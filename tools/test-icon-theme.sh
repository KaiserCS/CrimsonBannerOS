#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Checks the Crimson Banner icon theme (icons/CrimsonBanner, made by branding/icon-theme.py) is whole: every folder
# index.theme lists is there, every program name in names.txt has its SVG and every PNG size, and no link is broken.
set -euo pipefail
cd "$(dirname "$0")/.."
T=icons/CrimsonBanner
fail() { echo "icon theme: $*" >&2; exit 1; }
[ -f "$T/index.theme" ] || fail "no index.theme"
grep -q '^Name=Crimson Banner$' "$T/index.theme" || fail "index.theme has no Name"
dirs="$(sed -n 's/^Directories=//p' "$T/index.theme" | tr ',' ' ')"
[ -n "$dirs" ] || fail "index.theme lists no directories"
for d in $dirs; do
  [ -d "$T/$d" ] || fail "$d is listed but missing"
  grep -q "^\[$d\]$" "$T/index.theme" || fail "$d has no section in index.theme"
done
broken="$(find "$T" -xtype l)"
[ -z "$broken" ] || fail "broken links: $broken"
n=0
while read -r name; do
  [ -n "$name" ] || continue
  for d in $dirs; do
    ext=png; [ "${d%%/*}" = scalable ] && ext=svg
    [ -e "$T/$d/$name.$ext" ] || fail "$name has no $d/$name.$ext"
  done
  n=$((n + 1))
done < "$T/names.txt"
[ "$n" -ge 100 ] || fail "only $n program names"
# the pictures are the house's: the graphite tile and one crimson detail, under the project's licence
for f in "$T"/scalable/apps/crimson-*.svg; do
  grep -q 'SPDX-License-Identifier: GPL-3.0-or-later' "$f" || fail "$f has no licence line"
  grep -q '#2A2A2F' "$f" || fail "$f isn't on the graphite tile"
  grep -qi '#C8102E' "$f" || fail "$f has no crimson detail"
done
echo "icon theme: $n program names, $(ls "$T"/scalable/apps/crimson-*.svg | wc -l) pictures"
