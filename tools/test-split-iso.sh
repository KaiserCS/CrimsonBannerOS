#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# check.sh: tools/split-iso.sh and merge-iso.sh on a small stand-in ISO, split into small parts.
set -uo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
fail=0
eq() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: got '$2', expected '$3'"; fail=1; fi; }
split_() { CRIMSON_ISO_LIMIT=4000000 CRIMSON_ISO_PART_SIZE=1500000 "$SRC/tools/split-iso.sh" "$@" 2>/dev/null; }

mkdir -p "$T/out" "$T/dl"
head -c 1000000 /dev/urandom > "$T/out/small.iso"
eq "a small ISO is uploaded as it is" "$(split_ "$T/out/small.iso")" "$T/out/small.iso"
head -c 5000000 /dev/urandom > "$T/out/crimson-banner-1-x86_64.iso"
(cd "$T/out" && sha256sum crimson-banner-1-x86_64.iso > crimson-banner-1-x86_64.iso.sha256)
files="$(split_ "$T/out/crimson-banner-1-x86_64.iso" | xargs -n1 basename | tr '\n' ' ')"
eq "a big one: its parts, their checksums and the merge scripts" "$files" \
   "crimson-banner-1-x86_64.iso.part01 crimson-banner-1-x86_64.iso.part02 crimson-banner-1-x86_64.iso.part03 crimson-banner-1-x86_64.iso.part04 crimson-banner-1-x86_64.iso.parts.sha256 merge-iso.sh merge-iso.ps1 "
eq "every part under the limit" "$(find "$T/out" -name '*.part*' -size +4000000c | wc -l)" "0"

# what a person downloads: the parts, both checksum files and the script, into another folder
cp "$T"/out/*.part0* "$T"/out/*.parts.sha256 "$T"/out/*.iso.sha256 "$T/out/merge-iso.sh" "$T/dl/"
rm -f "$T/dl/small.iso.sha256"
out="$(cd "$T/dl" && sh merge-iso.sh 2>&1)"; code=$?
eq "merge-iso.sh joins it" "$code" "0"
eq "into the very same ISO" "$(cmp -s "$T/dl/crimson-banner-1-x86_64.iso" "$T/out/crimson-banner-1-x86_64.iso" && echo same)" "same"
eq "and says it checks out" "$(grep -c "is whole and matches" <<<"$out")" "1"

rm "$T/dl/crimson-banner-1-x86_64.iso"
printf 'x' | dd of="$T/dl/crimson-banner-1-x86_64.iso.part02" bs=1 seek=100 conv=notrunc 2>/dev/null
out="$(cd "$T/dl" && sh merge-iso.sh crimson-banner-1-x86_64.iso 2>&1)"; code=$?
eq "a broken part stops it" "$code" "1"
eq "and is named" "$(grep -c "broken: crimson-banner-1-x86_64.iso.part02" <<<"$out")" "1"
eq "with nothing joined" "$([ -e "$T/dl/crimson-banner-1-x86_64.iso" ] && echo joined || echo none)" "none"

cp "$T/out/crimson-banner-1-x86_64.iso.part02" "$T/dl/"; rm "$T/dl/crimson-banner-1-x86_64.iso.part04"
out="$(cd "$T/dl" && sh merge-iso.sh 2>&1)"; code=$?
eq "a missing part stops it" "$code" "1"
eq "and is named" "$(grep -c "missing: crimson-banner-1-x86_64.iso.part04" <<<"$out")" "1"

rm -f "$T"/dl/*
out="$(cd "$T/dl" && cp "$T/out/merge-iso.sh" . && sh merge-iso.sh 2>&1)"; code=$?
eq "no parts here: says so" "$code $(grep -c "No ISO parts here" <<<"$out")" "1 1"

# merge-iso.ps1, the same, where PowerShell is (GitHub's runners have it)
if command -v pwsh >/dev/null 2>&1; then
  rm -rf "$T/win"; mkdir -p "$T/win"
  cp "$T"/out/crimson-banner-1-x86_64.iso.part0* "$T"/out/*.parts.sha256 "$T/out/crimson-banner-1-x86_64.iso.sha256" "$T/out/merge-iso.ps1" "$T/win/"
  out="$(cd "$T/win" && pwsh -NoProfile -NonInteractive -File merge-iso.ps1 crimson-banner-1-x86_64.iso 2>&1)"; code=$?
  eq "merge-iso.ps1 joins it" "$code" "0"
  eq "into the very same ISO (PowerShell)" "$(cmp -s "$T/win/crimson-banner-1-x86_64.iso" "$T/out/crimson-banner-1-x86_64.iso" && echo same)" "same"
  eq "and says it checks out (PowerShell)" "$(grep -c "is whole and matches" <<<"$out")" "1"
  rm "$T/win/crimson-banner-1-x86_64.iso"
  printf 'x' | dd of="$T/win/crimson-banner-1-x86_64.iso.part03" bs=1 seek=7 conv=notrunc 2>/dev/null
  out="$(cd "$T/win" && pwsh -NoProfile -NonInteractive -File merge-iso.ps1 crimson-banner-1-x86_64.iso 2>&1)"; code=$?
  eq "a broken part stops it (PowerShell)" "$code $(grep -c "broken: crimson-banner-1-x86_64.iso.part03" <<<"$out")" "1 1"
  eq "with nothing joined (PowerShell)" "$([ -e "$T/win/crimson-banner-1-x86_64.iso" ] && echo joined || echo none)" "none"
else
  echo "(PowerShell isn't installed: merge-iso.ps1 not tried)"
fi
exit "$fail"
