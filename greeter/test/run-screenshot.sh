#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# Draws crimson-login's preview to a picture, headless inside redstar-comp, with a large photo as the person's
# picture (it must still come out the avatar's size). CI prints the picture in its log.
#   greeter/test/run-screenshot.sh OUT.png [path/to/crimson-login]
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:?where to write the picture}"
LOGIN="${2:-$HERE/../build/crimson-login}"
AVATARS="$(mktemp -d)"
trap 'rm -rf "$AVATARS"' EXIT
cp "$HERE/../../RedStarOS/Assets/login-bg.jpg" "$AVATARS/$(id -un)"
CRIMSON_GREETER_AVATARS="$AVATARS" "$HERE/../../crimson-gtk/test/headless.sh" "$OUT" "$LOGIN" --demo
