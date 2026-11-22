#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Tests for tools/publish-app.sh --local, in a scratch copy of the tree with a throwaway signing key: an
# AppImage-only app is published into a folder, and the catalogue's entries, checksums and signature are checked
# (the signature with gpgv and the exported key, as Software checks it). Building pacman packages and uploading are
# checked by CI's Arch job. Run by ./check.sh.
set -euo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"
T="$(mktemp -d)"; trap 'gpgconf --homedir "$T/gnupg" --kill all >/dev/null 2>&1 || true; rm -rf "$T"' EXIT
fail=0
eq() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: got '$2', expected '$3'"; fail=1; fi; }
command -v gpg >/dev/null && command -v gpgv >/dev/null && command -v python3 >/dev/null || { echo "(gpg, gpgv and python3 are needed: skipped)"; exit 0; }

tree="$T/tree"; mkdir -p "$tree/tools" "$tree/software" "$tree/apps/planner"
cp "$SRC/tools/publish-app.sh" "$tree/tools/"
export GNUPGHOME="$T/gnupg"; mkdir -m 700 "$GNUPGHOME"
gpg --batch --passphrase '' --quick-gen-key "Test <test@example.invalid>" ed25519 sign never 2>/dev/null
export REDSTAR_REPO_KEY="$(gpg --with-colons --list-keys test@example.invalid | awk -F: '/^fpr:/ {print $10; exit}')"
export REDSTAR_APPS_DIR="$T/out" REDSTAR_GITHUB_REPO="example/repo"

app="$tree/apps/planner"
cat > "$app/app.json" <<'J'
{ "id": "planner", "name": "Planner", "summary": "Economic computation for central planning",
  "description": "Plans.\n\nMore plans.", "license": "GPL-3.0-or-later", "developer": "Crimson Banner OS",
  "categories": ["Science"], "icon": "icon.png", "screenshots": ["one.png", "two.png"],
  "appimage": "Planner.AppImage", "version": "1.0.0" }
J
printf '\x7fELF\x02\x01\x01\x00AI\x02rest-of-the-file' > "$app/Planner.AppImage"
printf '\x89PNG icon' > "$app/icon.png"; printf '\x89PNG one' > "$app/one.png"; printf '\x89PNG two' > "$app/two.png"
mkdir -p "$tree/apps/unpublished"; echo '{ "id": "unpublished", "name": "Not yet", "license": "MIT" }' > "$tree/apps/unpublished/app.json"
# nothing to publish for it: no PKGBUILD, no AppImage

"$tree/tools/publish-app.sh" --local apps/planner >/dev/null
cat="$T/out/catalog/catalog.json"
j() { python3 -c "import json,sys; d=json.load(open('$cat')); print($1)"; }
eq "one app in the catalogue" "$(j 'len(d["software"])')" "1"
eq "its id" "$(j 'd["software"][0]["id"]')" "planner"
eq "its version" "$(j 'd["software"][0]["version"]')" "1.0.0"
eq "the AppImage's address" "$(j 'd["software"][0]["install"][0]["url"]')" "https://github.com/example/repo/releases/download/apps/planner-1.0.0-$(uname -m).AppImage"
eq "its checksum" "$(j 'd["software"][0]["install"][0]["sha256"]')" "$(sha256sum "$app/Planner.AppImage" | cut -d' ' -f1)"
eq "the icon" "$(j 'd["software"][0]["icon"]')" "https://github.com/example/repo/releases/download/apps/planner-icon.png"
eq "the screenshots, in order" "$(j '" ".join(s.rsplit("/",1)[1] for s in d["software"][0]["screenshots"])')" "planner-screenshot-1.png planner-screenshot-2.png"
eq "the files are there" "$(cd "$T/out/catalog" && ls | tr '\n' ' ')" "catalog.json catalog.json.sig planner-1.0.0-$(uname -m).AppImage planner-icon.png planner-screenshot-1.png planner-screenshot-2.png "
mkdir -p "$T/gpgv"; gpgv --homedir "$T/gpgv" --keyring "$tree/software/crimson-banner.gpg" "$cat.sig" "$cat" 2>/dev/null && v=good || v=bad
eq "the signature checks out with the exported key" "$v" "good"
cp "$cat" "$T/changed.json"; sed -i 's/Planner/Plannr/' "$T/changed.json"
gpgv --homedir "$T/gpgv" --keyring "$tree/software/crimson-banner.gpg" "$cat.sig" "$T/changed.json" 2>/dev/null && v=good || v=bad
eq "a changed catalogue doesn't" "$v" "bad"
eq "the shell's copy matches" "$(cmp -s "$cat" "$tree/software/catalog.json" && echo same)" "same"

# a new version replaces the old AppImage
sed -i 's/"1.0.0"/"1.1.0"/' "$app/app.json"
"$tree/tools/publish-app.sh" --local apps/planner >/dev/null
eq "a new version replaces the old file" "$(cd "$T/out/catalog" && ls *.AppImage)" "planner-1.1.0-$(uname -m).AppImage"
eq "and the catalogue has it" "$(j 'd["software"][0]["version"]')" "1.1.0"

# --unsigned is for --local only
"$tree/tools/publish-app.sh" --unsigned >/dev/null 2>&1 && r=allowed || r=refused
eq "--unsigned without --local is refused" "$r" "refused"
# something that isn't an AppImage is refused
printf 'not an elf' > "$app/Planner.AppImage"
"$tree/tools/publish-app.sh" --local apps/planner >/dev/null 2>&1 && r=allowed || r=refused
eq "a file that isn't an AppImage is refused" "$r" "refused"

# ── crimson-apps-repo: the repository in pacman.conf, and the key trusted ──
printf '[options]\nA=1\n\n[extra]\nInclude = x\n' > "$T/pacman.conf"
printf '#!/bin/sh\necho "$@" >> "%s/pk.log"\ncase "$1" in --list-keys) exit 1;; esac\n' "$T" > "$T/pacman-key"; chmod +x "$T/pacman-key"
repo() { CRIMSON_PACMAN_CONF="$T/pacman.conf" CRIMSON_APPS_KEYRING="$tree/software/crimson-banner.gpg" CRIMSON_PACMAN_KEY="$T/pacman-key" \
         "$SRC/packaging/crimson-apps-repo" "$@"; }
eq "off to begin with" "$(repo status)" "disabled"
repo enable >/dev/null
eq "enabled" "$(repo status)" "enabled"
eq "the key is added and trusted" "$(cut -d' ' -f1 "$T/pk.log" | tr '\n' ' ')" "--list-keys --add --lsign-key "
eq "signatures are required" "$(sed -n '/^\[crimson-banner-apps\]/,/^\[/p' "$T/pacman.conf" | grep -c 'SigLevel = Required DatabaseRequired')" "1"
repo enable >/dev/null
eq "enabling twice adds it once" "$(grep -c '^\[crimson-banner-apps\]' "$T/pacman.conf")" "1"
repo disable >/dev/null
eq "disabled leaves pacman.conf as it was" "$(cat "$T/pacman.conf")" "$(printf '[options]\nA=1\n\n[extra]\nInclude = x')"

exit "$fail"
