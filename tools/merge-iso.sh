#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Joins a Crimson Banner OS installer ISO that was published in parts (GitHub takes files up to 2 GiB), and checks it.
# Download every part (NAME.iso.part01, .part02…), NAME.iso.parts.sha256 and NAME.iso.sha256 into one folder with
# this script, then, in that folder:
#
#   sh merge-iso.sh                 joins every ISO whose parts are here
#   sh merge-iso.sh NAME.iso        joins that one
#
# Each part is checked first, so a broken or missing download is named before anything is joined; the joined ISO is
# then checked against NAME.iso.sha256. The parts are left as they are: delete them once the ISO checks out.
# Works on Linux and macOS (sha256sum or shasum). On Windows: merge-iso.ps1.
set -eu

if command -v sha256sum >/dev/null 2>&1; then sha() { sha256sum "$@"; }
elif command -v shasum >/dev/null 2>&1; then sha() { shasum -a 256 "$@"; }
else echo "Neither sha256sum nor shasum is installed, so the download can't be checked." >&2; exit 1; fi
hash_of() { sha "$1" | awk '{ print $1 }'; }
expected() {   # checksum file, file name: the checksum it lists for the name
  awk -v n="$2" '{ f = $2; sub(/^\*/, "", f) } f == n { print $1; exit }' "$1"
}

merge() {
  iso="$1"
  list="$iso.parts.sha256"
  [ -f "$list" ] || { echo "$list is missing: download it with the parts." >&2; return 1; }
  echo "==> $iso"
  # every part the list names, there and unbroken
  bad=0; parts=""
  for p in $(awk '{ f = $2; sub(/^\*/, "", f); print f }' "$list" | sort); do
    if [ ! -f "$p" ]; then echo "  missing: $p (download it again)" >&2; bad=1; continue; fi
    if [ "$(hash_of "$p")" != "$(expected "$list" "$p")" ]; then echo "  broken: $p (download it again)" >&2; bad=1; continue; fi
    echo "  ok: $p"
    parts="$parts $p"
  done
  [ "$bad" = 0 ] || return 1
  # shellcheck disable=SC2086
  cat $parts > "$iso.joining"
  mv "$iso.joining" "$iso"
  if [ -f "$iso.sha256" ]; then
    if [ "$(hash_of "$iso")" = "$(expected "$iso.sha256" "$iso")" ]; then
      echo "  $iso is whole and matches $iso.sha256. The parts can go."
    else
      echo "  $iso doesn't match $iso.sha256: something is wrong. Download it again." >&2
      return 1
    fi
  else
    echo "  $iso joined (no $iso.sha256 here to check it against)."
  fi
}

if [ $# -gt 0 ]; then
  for iso in "$@"; do merge "$iso"; done
  exit 0
fi
found=0
for list in *.iso.parts.sha256; do
  [ -f "$list" ] || continue
  found=1
  merge "${list%.parts.sha256}"
done
[ "$found" = 1 ] || { echo "No ISO parts here (NAME.iso.parts.sha256 and NAME.iso.part01…): run this in the folder you downloaded them to." >&2; exit 1; }
