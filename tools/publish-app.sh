#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Publishes Crimson Banner OS's own apps (apps/*, see apps/README.md) to Software's Crimson Banner tab, without an OS
# release: builds each app's pacman package, signs it into the [crimson-banner-apps] repository, writes and signs the
# catalogue (catalog.json) and uploads everything to two GitHub releases kept up to date in place ("apps" and
# "apps-ARCH"). The signing key never leaves this computer.
#
#   tools/publish-app.sh                 every app in apps/
#   tools/publish-app.sh apps/planner    just these (the catalogue still lists every app already published)
#   tools/publish-app.sh --local         into the folder only (/srv/crimson-banner-apps): nothing is uploaded, e.g.
#                                        to serve it from your own server or to test
#   tools/publish-app.sh --catalog-only  don't build: write, sign and upload the catalogue (after editing an app.json)
#
# Needs: makepkg and repo-add (Arch), gpg, python3, and for uploading gh (GitHub's CLI, signed in: gh auth login).
# $REDSTAR_APPS_DIR changes the folder, $REDSTAR_REPO_KEY the key, $REDSTAR_GITHUB_REPO the repository.
set -euo pipefail
cd "$(dirname "$0")/.."
PWD_TREE="$PWD"
ROOT="$PWD"
SLUG="${REDSTAR_GITHUB_REPO:-KaiserCS/CrimsonBannerOS}"
OUT="${REDSTAR_APPS_DIR:-/srv/crimson-banner-apps}"
ARCH="$(uname -m)"
NAME=crimson-banner-apps
CAT_TAG=apps
PKG_TAG="apps-$ARCH"
BASE_URL="https://github.com/$SLUG/releases/download/$CAT_TAG/"

LOCAL=0; CATALOG_ONLY=0; UNSIGNED=0; APPS=()
for a in "$@"; do
  case "$a" in
    --local) LOCAL=1 ;;
    --catalog-only) CATALOG_ONLY=1 ;;
    --unsigned) UNSIGNED=1 ;;          # tests only, and only with --local
    -h|--help) sed -n '3,/^set -e/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "Unknown option: $a" >&2; exit 2 ;;
    *) APPS+=("${a%/}") ;;
  esac
done
[ "$UNSIGNED" = 0 ] || [ "$LOCAL" = 1 ] || { echo "--unsigned only goes with --local: published apps are always signed." >&2; exit 2; }

# shellcheck source=tools/lib/gh-release.sh
. tools/lib/gh-release.sh
step() { printf '\n\033[1;31m==>\033[0m \033[1m%s\033[0m\n' "$*"; }
die() { echo "$*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is missing: $2"; }
need python3 "pacman -S python"
need gpg "pacman -S gnupg"

# ── the key (tools/repo.sh key, ./install-arch.sh) ──
KEY="${REDSTAR_REPO_KEY:-}"
for f in /srv/crimson-banner/signing-key "${XDG_DATA_HOME:-$HOME/.local/share}/redstaros/repo/signing-key"; do
  [ -n "$KEY" ] || { [ -f "$f" ] && KEY="$(cat "$f")"; } || true
done
if [ "$UNSIGNED" = 0 ]; then
  [ -n "$KEY" ] || die "No signing key: run tools/repo.sh key (or ./install-arch.sh) first."
  gpg --list-secret-keys "$KEY" >/dev/null 2>&1 || die "The signing key $KEY isn't in this computer's GnuPG keyring."
fi

# ── the folder ──
if [ ! -d "$OUT" ]; then
  if mkdir -p "$OUT" 2>/dev/null; then :; else sudo install -d -o "$(id -un)" -g "$(id -gn)" "$OUT"; fi
fi
mkdir -p "$OUT/$ARCH" "$OUT/catalog"
[ "${#APPS[@]}" -gt 0 ] || for d in apps/*/; do [ -f "$d/app.json" ] && APPS+=("${d%/}"); done

# app.json's fields, read with python (no jq needed)
field() { python3 -c 'import json,sys; v=json.load(open(sys.argv[1])).get(sys.argv[2]); print("" if v is None else v)' "$1/app.json" "$2"; }

# ── 1. build and sign each app ──
if [ "$CATALOG_ONLY" = 0 ]; then
  for app in "${APPS[@]}"; do
    [ -f "$app/app.json" ] || die "$app has no app.json (apps/README.md)."
    id="$(field "$app" id)"
    [[ "$id" =~ ^[a-z0-9][a-z0-9-]*$ ]] || die "$app: the id must be lower case letters, digits and '-' (got '$id')."
    if [ -f "$app/PKGBUILD" ]; then
      need makepkg "build on an Arch-based system (pacman -S base-devel)"
      need repo-add "pacman -S pacman"
      step "Building $id"
      work="$(mktemp -d)"
      cp -a "$app/." "$work/"
      SIGN=(); [ "$UNSIGNED" = 1 ] || SIGN=(--sign --key "$KEY")
      # CRIMSON_TREE: apps that share the shell's parts (Planner, its styles) are built from the tree
      (cd "$work" && CRIMSON_TREE="$PWD_TREE" PKGDEST="$work/out" makepkg --syncdeps --noconfirm --needed --cleanbuild "${SIGN[@]}")
      cp "$work"/out/*.pkg.tar.zst* "$OUT/$ARCH/"
      REPOARGS=(); [ "$UNSIGNED" = 1 ] || REPOARGS=(--sign --key "$KEY" --verify)
      (cd "$OUT/$ARCH" && repo-add --remove "${REPOARGS[@]}" "$NAME.db.tar.zst" $(cd "$work/out" && ls -1 ./*.pkg.tar.zst | sed 's#^\./##'))
      rm -rf "$work"
    fi
    ai="$(field "$app" appimage)"
    if [ -n "$ai" ]; then
      [ -f "$app/$ai" ] || die "$app: its AppImage $ai isn't there (build it first)."
      head -c 11 "$app/$ai" | od -An -c | tr -d ' \n' | grep -q '177ELF' || die "$app/$ai isn't an AppImage."
      ver="$(field "$app" version)"
      [ -n "$ver" ] || ver="$(cd "$app" && [ -f PKGBUILD ] && makepkg --printsrcinfo | sed -n 's/^\tpkgver = //p' | head -n1)"
      [ -n "$ver" ] || die "$app: set \"version\" in app.json (there's no PKGBUILD to take it from)."
      rm -f "$OUT/catalog/$id"-*.AppImage
      cp "$app/$ai" "$OUT/catalog/$id-$ver-$ARCH.AppImage"
    fi
    # pictures: the icon and screenshots, named after the app
    icon="$(field "$app" icon)"
    if [ -n "$icon" ]; then cp "$app/$icon" "$OUT/catalog/$id-icon.${icon##*.}"; fi
    rm -f "$OUT/catalog/$id"-screenshot-*
    python3 -c 'import json,sys; [print(s) for s in json.load(open(sys.argv[1])).get("screenshots") or []]' "$app/app.json" |
      { n=0; while IFS= read -r s; do n=$((n + 1)); cp "$app/$s" "$OUT/catalog/$id-screenshot-$n.${s##*.}"; done; }
  done
fi

# ── 2. the catalogue: every app that has something published ──
step "Writing the catalogue"
python3 - "$ROOT/apps" "$OUT" "$ARCH" "$BASE_URL" <<'PY'
import hashlib, json, os, re, sys
apps_dir, out, arch, base = sys.argv[1:5]
repo, cat = os.path.join(out, arch), os.path.join(out, "catalog")
pkg_re = re.compile(r"^(?P<name>.+)-(?P<ver>[^-]+)-(?P<rel>[^-]+)-(?:%s|any)\.pkg\.tar\.zst$" % re.escape(arch))

def newest_package(name):
    found = []
    for f in os.listdir(repo) if os.path.isdir(repo) else []:
        m = pkg_re.match(f)
        if m and m.group("name") == name: found.append((os.path.getmtime(os.path.join(repo, f)), m.group("ver")))
    return max(found)[1] if found else None

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""): h.update(block)
    return h.hexdigest()

software = []
for d in sorted(os.listdir(apps_dir)):
    meta_path = os.path.join(apps_dir, d, "app.json")
    if not os.path.isfile(meta_path): continue
    meta = json.load(open(meta_path))
    app_id = meta["id"]
    install, version = [], meta.get("version")
    pkg = meta.get("package") or app_id
    pkg_ver = newest_package(pkg)
    if pkg_ver:
        install.append({"kind": "pacman", "package": pkg})
        version = version or pkg_ver
    appimages = sorted(f for f in os.listdir(cat) if f.startswith(app_id + "-") and f.endswith(".AppImage"))
    if appimages:
        install.append({"kind": "appimage", "url": base + appimages[-1], "sha256": sha256(os.path.join(cat, appimages[-1]))})
    if not install: continue      # nothing published for it yet
    pictures = sorted(os.listdir(cat))
    icon = next((f for f in pictures if f.startswith(app_id + "-icon.")), None)
    shots = sorted((f for f in pictures if f.startswith(app_id + "-screenshot-")), key=lambda f: int(re.search(r"-(\d+)\.", f).group(1)))
    entry = {k: meta[k] for k in ("id", "name", "summary", "description", "license", "developer", "homepage", "categories") if k in meta}
    entry["version"] = version
    if icon: entry["icon"] = base + icon
    if shots: entry["screenshots"] = [base + s for s in shots]
    entry["install"] = install
    software.append(entry)

doc = {"format": 1,
       "about": "Crimson Banner OS's own software, shown in Software's Crimson Banner tab (apps/README.md). Signed: catalog.json.sig.",
       "software": software}
with open(os.path.join(cat, "catalog.json"), "w") as f: json.dump(doc, f, indent=2, ensure_ascii=False); f.write("\n")
print(f"{len(software)} app(s) in the catalogue")
PY
# the copy the shell is built with, for when it can't fetch the list
cp "$OUT/catalog/catalog.json" software/catalog.json

if [ "$UNSIGNED" = 0 ]; then
  gpg --batch --yes -u "$KEY" --detach-sign -o "$OUT/catalog/catalog.json.sig" "$OUT/catalog/catalog.json"
  export_public_key "$KEY"     # the public key the shell checks the catalogue with
fi

if [ "$LOCAL" = 1 ]; then
  echo
  echo "Published into $OUT (nothing uploaded)."
  exit 0
fi

# ── 3. upload: the two releases, kept up to date in place ──
need gh "pacman -S github-cli, then gh auth login"
gh auth status >/dev/null 2>&1 || die "Sign in to GitHub first: gh auth login"

step "Uploading to GitHub ($SLUG)"
ensure_release "$CAT_TAG" "Crimson Banner OS apps" "Software's Crimson Banner tab: the signed catalogue, icons, screenshots and AppImages. Kept up to date by tools/publish-app.sh; not an OS release."
ensure_release "$PKG_TAG" "Crimson Banner OS apps ($ARCH packages)" "The [crimson-banner-apps] pacman repository for $ARCH, signed with the project's key. Kept up to date by tools/publish-app.sh; not an OS release."
sync_release "$CAT_TAG" "$OUT/catalog"
[ -z "$(ls -A "$OUT/$ARCH" 2>/dev/null)" ] || sync_release "$PKG_TAG" "$OUT/$ARCH"
echo
echo "Published. Software shows it the next time it fetches the list (opening it, or Check for new programs)."
git diff --quiet -- software/ || echo "software/ changed (the catalogue copy, perhaps the key): commit it with the app."
