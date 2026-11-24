#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Crimson Banner OS's installer ISOs (iso/, PRINCIPLES.md "Installer images"): live systems with the whole desktop
# and Calamares, installing offline. Three of them, one per kernel:
#   linux   Linux, with the firmware most PCs need        out/crimson-banner-VERSION-x86_64.iso
#   libre   Linux-libre, nothing non-free on it            out/crimson-banner-libre-VERSION-x86_64.iso
#   mac     t2linux's Linux for Intel Macs (T2 and older)  out/crimson-banner-mac-VERSION-x86_64.iso
#
#   tools/iso.sh packages VARIANT…   (as yourself) build what the ISOs need that Arch doesn't ship: Crimson Banner
#                                    OS's own packages, Calamares, and the variant's kernel (an hour or two each),
#                                    into a local pacman repository. Already-built versions are kept.
#   sudo tools/iso.sh image VARIANT… make the ISOs from that repository and Arch's (mkarchiso)
#   tools/iso.sh all [VARIANT…]      both, asking for the password for the second (all three when none is named)
#
# Needs an Arch-based system: archiso, base-devel, and for the kernels a few GB of disk. The repository is
# $REDSTAR_ISO_REPO (default ~/.cache/redstaros/iso-repo), the ISOs go to $REDSTAR_ISO_OUT (default ./out), and the
# work folder is $REDSTAR_ISO_WORK (default /var/tmp/crimson-iso). Packages are signed with $REDSTAR_REPO_KEY when
# it's set.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$PWD"
CARCH="${CARCH:-x86_64}"
REPO="${REDSTAR_ISO_REPO:-${XDG_CACHE_HOME:-$HOME/.cache}/redstaros/iso-repo}"
OUT="${REDSTAR_ISO_OUT:-$ROOT/out}"
WORK="${REDSTAR_ISO_WORK:-/var/tmp/crimson-iso}"
DB="$REPO/crimson-banner.db.tar.zst"
ALL=(linux libre mac)

die() { echo "iso.sh: $*" >&2; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 is missing: $2"; }
variants() { if [ "$#" -eq 0 ]; then echo "${ALL[@]}"; else for v; do [ -f "iso/variants/$v/variant.conf" ] || die "no variant '$v' (linux, libre, mac)"; echo "$v"; done; fi; }

# ── packages ───────────────────────────────────────────────────────────────────────────────────────────────────────
# builds iso/pkgs/NAME unless the repository already has that version; makedepends and depends from Arch's
# repositories are installed first (our own, such as linux-t2 for tiny-dfr, are left out: makepkg runs --nodeps)
build_recipe() {
  local name="$1" dir="$ROOT/iso/pkgs/$1" want have=1
  [ -f "$dir/PKGBUILD" ] || die "no recipe iso/pkgs/$name"
  mapfile -t want < <(cd "$dir" && PKGDEST="$REPO" makepkg --packagelist 2>/dev/null | grep -v -- '-debug-')
  for f in "${want[@]}"; do [ -f "$f" ] || have=0; done
  if [ "$have" = 1 ] && [ "${#want[@]}" -gt 0 ]; then echo "==> $name: already built ($(basename "${want[0]}"))"; return; fi
  echo "==> Building $name"
  local deps=() d
  # shellcheck disable=SC1091
  mapfile -t deps < <(cd "$dir" && bash -c 'source ./PKGBUILD >/dev/null 2>&1; printf "%s\n" "${makedepends[@]}" "${depends[@]}" "${checkdepends[@]}"' | sed 's/[<>=].*//' | sort -u)
  local installable=()
  for d in "${deps[@]}"; do [ -n "$d" ] && pacman -Si "$d" >/dev/null 2>&1 && installable+=("$d"); done
  [ "${#installable[@]}" -eq 0 ] || sudo pacman -S --needed --noconfirm --asdeps "${installable[@]}"
  import_keys "$dir"
  local sign=(); [ -n "${REDSTAR_REPO_KEY:-}" ] && sign=(--sign --key "$REDSTAR_REPO_KEY")
  (cd "$dir" && PKGDEST="$REPO" BUILDDIR="$WORK/build" SRCDEST="$WORK/sources" \
     makepkg --nodeps --noconfirm --cleanbuild --force "${sign[@]}")
  # the libre kernel's tarball has no pinned checksum yet: show it, to pin (iso/pkgs/linux-libre/PKGBUILD)
  if [ "$name" = linux-libre ]; then sha256sum "$WORK"/sources/linux-libre-*-gnu.tar.xz 2>/dev/null || true; fi
}

# the keys the recipe's signed sources are checked against: from its keys/pgp folder (kept in the repository, so no
# key server is needed), else fetched once from a key server and saved there for next time
import_keys() {
  local dir="$1" fpr
  mapfile -t fprs < <(cd "$dir" && bash -c 'source ./PKGBUILD >/dev/null 2>&1; printf "%s\n" "${validpgpkeys[@]}"')
  for fpr in "${fprs[@]}"; do
    [ -n "$fpr" ] || continue
    if [ -f "$dir/keys/pgp/$fpr.asc" ]; then gpg --batch --quiet --import "$dir/keys/pgp/$fpr.asc"; continue; fi
    gpg --batch --list-keys "$fpr" >/dev/null 2>&1 && continue
    gpg --batch --keyserver hkps://keyserver.ubuntu.com --recv-keys "$fpr" \
      || gpg --batch --keyserver hkps://keys.openpgp.org --recv-keys "$fpr" || true
    gpg --batch --list-keys "$fpr" >/dev/null 2>&1 || { echo "(no key $fpr: its signatures can't be checked)"; continue; }
    mkdir -p "$dir/keys/pgp"
    gpg --batch --armor --export "$fpr" > "$dir/keys/pgp/$fpr.asc"
    echo "Saved the key $fpr to ${dir#"$ROOT"/}/keys/pgp: commit it."
    echo "NEWKEY ${dir#"$ROOT"/}/keys/pgp/$fpr.asc $(base64 -w0 "$dir/keys/pgp/$fpr.asc")"
  done
}

add_to_repo() {
  local pkgs=() sign=()
  mapfile -t pkgs < <(find "$REPO" -maxdepth 1 -name '*.pkg.tar.zst' ! -name '*-debug-*' | sort)
  [ "${#pkgs[@]}" -gt 0 ] || die "nothing was built into $REPO"
  [ -n "${REDSTAR_REPO_KEY:-}" ] && sign=(--sign --key "$REDSTAR_REPO_KEY")
  rm -f "$REPO"/crimson-banner.db* "$REPO"/crimson-banner.files*
  repo-add -q "${sign[@]}" "$DB" "${pkgs[@]}"
}

cmd_packages() {
  need makepkg "pacman -S base-devel"
  [ "$(id -u)" != 0 ] || die "makepkg won't run as root: run 'packages' as yourself"
  mkdir -p "$REPO" "$WORK/build" "$WORK/sources"
  local v recipes=(ckbcomp calamares)
  for v in $(variants "$@"); do
    # shellcheck disable=SC1090
    BUILD=""; source "iso/variants/$v/variant.conf"
    for r in $BUILD; do [[ " ${recipes[*]} " == *" $r "* ]] || recipes+=("$r"); done
  done
  # Crimson Banner OS itself, from this tree (tools/repo.sh builds it into its own repository: copied over)
  echo "==> Building Crimson Banner OS"
  local own="$WORK/own-repo"
  REDSTAR_REPO="$own" tools/repo.sh build $( [ -n "${REDSTAR_REPO_KEY:-}" ] || echo --unsigned )
  find "$REPO" -maxdepth 1 \( -name 'crimson-banner*.pkg.tar.zst*' -o -name 'crimson-online-*.pkg.tar.zst*' \
       -o -name 'redstar-comp-*.pkg.tar.zst*' -o -name 'redstar-wm-*.pkg.tar.zst*' \) -delete
  cp "$own/$CARCH"/*.pkg.tar.zst* "$REPO/"
  for r in "${recipes[@]}"; do build_recipe "$r"; done
  add_to_repo
  echo "Repository: $REPO"
  ls -1 "$REPO"
}

# ── image ──────────────────────────────────────────────────────────────────────────────────────────────────────────
fill() {   # fill FILE… : the @…@ values of the variant being built
  local f
  for f; do
    sed -i -e "s|@KERNEL@|$KERNEL|g" -e "s|@PARAMS@|$PARAMS|g" -e "s|@VARIANT_TITLE@|$TITLE|g" \
           -e "s|@ISO_NAME@|$ISO_NAME|g" -e "s|@VERSION@|$VERSION|g" -e "s|@LABEL@|$LABEL|g" \
           -e "s|@REPO@|$REPO|g" -e "s|@REPO_SIGLEVEL@|$SIGLEVEL|g" -e "s|@KERNEL_PARAMS@|$KPARAMS|g" "$f"
    python3 - "$f" "$SERVICE_UNITS" <<'PY'
import sys
p, units = sys.argv[1], sys.argv[2]
s = open(p, encoding="utf-8").read()
if "@SERVICE_UNITS@" in s:
    open(p, "w", encoding="utf-8").write(s.replace("@SERVICE_UNITS@\n", units))
PY
  done
}

cmd_image() {
  need mkarchiso "pacman -S archiso"
  [ "$(id -u)" = 0 ] || die "mkarchiso needs root: sudo tools/iso.sh image …"
  [ -f "$DB" ] || die "no repository at $REPO yet: run tools/iso.sh packages first (as yourself)"
  mkdir -p "$OUT" "$WORK"
  VERSION="$(tools/version.sh)"; VERSION="${VERSION%%+*}"     # 26.11.2 (a development build: the release it follows)
  LABEL="$(date +%Y%m)"
  SIGLEVEL="Optional TrustAll"
  [ -n "${REDSTAR_REPO_KEY:-}" ] && SIGLEVEL="Required DatabaseOptional"
  local v
  for v in $(variants "$@"); do
    # shellcheck disable=SC1090
    TITLE=""; ISO_NAME=""; KERNEL=""; PARAMS=""; SERVICES=""; NONFREE=""; source "iso/variants/$v/variant.conf"
    KPARAMS=""; for p in $PARAMS; do KPARAMS+=", \"$p\""; done
    SERVICE_UNITS=""; for s in $SERVICES; do SERVICE_UNITS+="    - name: $s"$'\n'"      action: enable"$'\n'; done
    echo "==> $ISO_NAME $VERSION ($TITLE, kernel $KERNEL)"
    local P="$WORK/profile-$v"
    rm -rf "$P" "$WORK/work-$v"
    cp -a iso/profile "$P"
    { cat iso/packages.common; echo; cat "iso/variants/$v/packages"; } > "$P/packages.x86_64"
    [ ! -d "iso/variants/$v/airootfs" ] || cp -a "iso/variants/$v/airootfs/." "$P/airootfs/"
    mkdir -p "$P/airootfs/etc/calamares"
    cp -a iso/calamares/. "$P/airootfs/etc/calamares/"
    mapfile -t files < <(find "$P" -type f \( -name '*.cfg' -o -name '*.conf' -o -name '*.desc' -o -name 'profiledef.sh' -o -name os-release \))
    fill "${files[@]}"
    if grep -rl '@[A-Z_]*@' "$P" --include='*.conf' --include='*.cfg' --include='*.desc' --include=profiledef.sh --include=os-release; then
      die "values left unfilled in the files above"
    fi
    mkarchiso -v -r -w "$WORK/work-$v" -o "$OUT" "$P"
    local iso; iso="$(ls -t "$OUT/$ISO_NAME-$VERSION"-*.iso | head -n1)"
    check_iso "$iso" "$v"
    (cd "$OUT" && sha256sum "$(basename "$iso")" > "$(basename "$iso").sha256")
  done
  ls -lh "$OUT"
}

# what is on the ISO: the libre one may hold nothing non-free; every one must fit a GitHub release file (2 GiB)
check_iso() {
  local iso="$1" v="$2" list
  list="$(mktemp)"
  bsdtar -xOf "$iso" crimson/pkglist.x86_64.txt > "$list" 2>/dev/null || osirrox -indev "$iso" -extract /crimson/pkglist.x86_64.txt "$list" >/dev/null 2>&1 || true
  echo "$(wc -l < "$list") packages on $(basename "$iso")"
  if [ -n "$NONFREE" ]; then
    local bad="" pat
    for pat in $NONFREE; do
      bad+="$(awk '{print $1}' "$list" | grep -x -- "$(echo "$pat" | sed 's/\*/.*/g')" || true)"$'\n'
    done
    bad="$(echo "$bad" | sed '/^$/d')"
    [ -z "$bad" ] || die "$v must hold nothing non-free, but has: $(echo "$bad" | tr '\n' ' ')"
    echo "Nothing non-free on it (checked: $NONFREE)."
  fi
  rm -f "$list"
  local size; size=$(stat -c %s "$iso")
  echo "Size: $((size / 1024 / 1024)) MiB"
  [ "$size" -lt $((2 * 1024 * 1024 * 1024)) ] || echo "::warning::$(basename "$iso") is over 2 GiB, too big for one GitHub release file"
}

case "${1:-}" in
  packages) shift; cmd_packages "$@" ;;
  recipe)   # one of iso/pkgs into the repository (CI builds the kernels on their own, to keep them between runs)
    shift; [ "$(id -u)" != 0 ] || die "run 'recipe' as yourself"
    mkdir -p "$REPO" "$WORK/build" "$WORK/sources"; for r; do build_recipe "$r"; done ;;
  image) shift; cmd_image "$@" ;;
  all) shift; cmd_packages "$@"; sudo --preserve-env=REDSTAR_ISO_REPO,REDSTAR_ISO_OUT,REDSTAR_ISO_WORK,REDSTAR_REPO_KEY \
         REDSTAR_ISO_REPO="$REPO" "$0" image "$@" ;;
  *) sed -n '3,20p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
