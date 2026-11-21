#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Crimson Banner OS's version, worked out the same way for every part of the system (the shell, redstar-comp,
# redstar-wm, Crimson Online, the Arch packages and the AppImage).
#
# The scheme (README, "Versions"): releases are YY.MM.N, the year and month they came out and a fix number from 0.
# VERSION holds the latest release. A build of a later commit is a development build of it: 26.11.0+5.g1a2b3c4 is
# 5 commits after 26.11.0, at commit 1a2b3c4, and ".local" is added when the tree has changes not committed yet.
# These sort after 26.11.0 and before the next release, for pacman as well as for the updater.
#
#   tools/version.sh            26.11.0, or 26.11.0+5.g1a2b3c4 for a development build
#   tools/version.sh --base     26.11.0 (the release it starts from)
#   tools/version.sh --pkgver   26.11.0, or 26.11.0.r5.g1a2b3c4 (pacman's form: no '+' or '-')
#   tools/version.sh --next     the version the next release gets (tools/release.sh)
#
# Without git (a source tarball), the version made when the tarball was (.build-version) is used, else VERSION.
set -eu
cd "$(dirname "$0")/.."
base="$(sed -n '1{s/[[:space:]]//g;p;}' VERSION)"
mode="${1:-}"

case "$mode" in
  --base) echo "$base"; exit 0 ;;
  --next)
    # VERSION itself while it hasn't been released (no v-tag yet); else this month's first release, or the next fix
    # number when this month already has one
    if git rev-parse --git-dir >/dev/null 2>&1 && ! git rev-parse -q --verify "refs/tags/v$base" >/dev/null; then
      echo "$base"; exit 0
    fi
    now="$(date +%y.%m | sed 's/\.0/./')"
    case "$base" in
      "$now".*) echo "$now.$(( ${base##*.} + 1 ))" ;;
      *) echo "$now.0" ;;
    esac
    exit 0 ;;
  ""|--pkgver) ;;
  *) echo "usage: tools/version.sh [--base|--pkgver|--next]" >&2; exit 2 ;;
esac

commits=""; hash=""; local_changes=""
# a source tarball (unpacked, so no .git of its own, even inside someone else's checkout): its stamp
if [ -f .build-version ] && [ ! -e .git ]; then
  full="$(sed -n 1p .build-version)"
  case "$mode" in
    --pkgver) echo "$full" | sed -e 's/+\([0-9][0-9]*\)\./.r\1./' -e 's/+g/.r0.g/' ;;
    *) echo "$full" ;;
  esac
  exit 0
fi
if git rev-parse --git-dir >/dev/null 2>&1 && [ -n "$(git rev-parse -q --verify HEAD 2>/dev/null)" ]; then
  hash="$(git rev-parse --short=7 HEAD)"
  # counted from the release's tag, else from the commit that last set VERSION
  if git rev-parse -q --verify "refs/tags/v$base" >/dev/null; then
    from="v$base"
  else
    from="$(git log -1 --format=%H -- VERSION 2>/dev/null || true)"
  fi
  if [ "$(git rev-parse --is-shallow-repository 2>/dev/null)" = true ] && ! git rev-parse -q --verify "refs/tags/v$base" >/dev/null; then
    commits="?"   # a shallow clone can't say how far the release is
  elif [ -n "$from" ]; then
    commits="$(git rev-list --count "$from..HEAD")"
  else
    commits="$(git rev-list --count HEAD)"
  fi
  # (the window manager's binary is rebuilt by every build of the shell, so it doesn't count)
  git diff --quiet HEAD -- . ':(exclude)wm/redstar-wm' 2>/dev/null || local_changes=1
fi

if [ -z "$hash" ] || { [ "$commits" = 0 ] && [ -z "$local_changes" ]; }; then
  echo "$base"; exit 0
fi
n="$commits"; [ "$n" = "?" ] && n=""
suffix="g$hash${local_changes:+.local}"
case "$mode" in
  --pkgver) echo "$base.r${n:-0}.$suffix" ;;
  *) echo "$base+${n:+$n.}$suffix" ;;
esac
