#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Splits an installer ISO into parts small enough for a GitHub release file (each must be under 2 GiB), when it's too
# big for one; a smaller ISO is left as it is. The release workflow runs it (iso.yml); people join the parts again with
# merge-iso.sh (Linux, macOS) or merge-iso.ps1 (Windows), which are uploaded beside them.
#
#   tools/split-iso.sh ISO     prints the files to upload: the ISO itself, or its parts, their checksums and the two
#                              merge scripts (copied next to the ISO)
#
# The parts are ISO.part01, ISO.part02… and ISO.parts.sha256 has a checksum for each, so a broken or missing part is
# named before anything is joined. The whole ISO's own ISO.sha256 checks the result. $CRIMSON_ISO_PART_SIZE sets the
# part size in bytes (1900 MiB; the tests use a small one).
set -euo pipefail
ISO="${1:?usage: tools/split-iso.sh ISO}"
LIMIT=$((2 * 1024 * 1024 * 1024))
PART="${CRIMSON_ISO_PART_SIZE:-$((1900 * 1024 * 1024))}"
TOOLS="$(cd "$(dirname "$0")" && pwd)"
dir="$(dirname "$ISO")"; name="$(basename "$ISO")"
size=$(stat -c %s "$ISO")
limit="${CRIMSON_ISO_LIMIT:-$LIMIT}"

if [ "$size" -lt "$limit" ]; then
  echo "$ISO"
  exit 0
fi
[ "$PART" -lt "$limit" ] || { echo "The part size ($PART) has to be under the limit ($limit)." >&2; exit 1; }
echo "$name is $((size / 1024 / 1024)) MiB, over GitHub's 2 GiB per file: splitting it into parts of $((PART / 1024 / 1024)) MiB" >&2
rm -f "$dir/$name".part[0-9][0-9]
(cd "$dir" && split -b "$PART" -d -a 2 --numeric-suffixes=1 "$name" "$name.part")
(cd "$dir" && sha256sum "$name".part[0-9][0-9] > "$name.parts.sha256")
cp "$TOOLS/merge-iso.sh" "$TOOLS/merge-iso.ps1" "$dir/"
ls -1 "$dir/$name".part[0-9][0-9]
echo "$dir/$name.parts.sha256"
echo "$dir/merge-iso.sh"
echo "$dir/merge-iso.ps1"
