# SPDX-License-Identifier: GPL-3.0-or-later
# Shared by tools/publish-app.sh and tools/publish-os.sh (sourced, not run): GitHub releases kept up to date in
# place, and the project's public key in the tree. Needs $SLUG (owner/repository) and gh, signed in.

# the release, made the first time (never marked as the latest OS release; "prerelease" marks it as one)
ensure_release() {   # tag title notes [prerelease]
  gh release view "$1" -R "$SLUG" >/dev/null 2>&1 && return 0
  local pre=(); [ "${4:-}" = prerelease ] && pre=(--prerelease)
  gh release create "$1" -R "$SLUG" --title "$2" --notes "$3" --latest=false "${pre[@]}" >/dev/null
}

# local files → the release: new or changed ones uploaded, ones no longer here deleted
sync_release() {     # tag dir
  local tag="$1" dir="$2" stage f b n
  stage="$(mktemp -d)"
  # pacman's .db/.files are links to the real files: GitHub needs them as files
  for f in "$dir"/*; do [ -e "$f" ] && cp -L "$f" "$stage/"; done
  declare -A remote=()
  while IFS=$'\t' read -r n s; do [ -n "$n" ] && remote["$n"]="$s"; done \
    < <(gh release view "$tag" -R "$SLUG" --json assets --jq '.assets[] | "\(.name)\t\(.size)"')
  local up=()
  for f in "$stage"/*; do
    b="$(basename "$f")"
    case "$b" in
      *.db|*.db.*|*.files|*.files.*|catalog.json|catalog.json.sig) up+=("$f") ;;   # always: same size can mean new contents
      *) [ "${remote[$b]:-}" = "$(stat -c %s "$f")" ] || up+=("$f") ;;
    esac
  done
  [ "${#up[@]}" -eq 0 ] || gh release upload "$tag" -R "$SLUG" --clobber "${up[@]}"
  for n in "${!remote[@]}"; do
    [ -e "$stage/$n" ] || gh release delete-asset "$tag" "$n" -R "$SLUG" -y
  done
  echo "$tag: ${#up[@]} file(s) uploaded"
  rm -rf "$stage"
}

# software/crimson-banner.gpg: the public half of the project's key, which the shell checks the apps catalogue with
# and installed systems trust the update and apps repositories by (crimson-repo). Written when it's new or changed.
export_public_key() {   # key
  local tmp; tmp="$(mktemp)"
  gpg --export "$1" > "$tmp"
  if ! cmp -s "$tmp" software/crimson-banner.gpg 2>/dev/null; then
    mkdir -p software
    mv "$tmp" software/crimson-banner.gpg
    echo "Wrote software/crimson-banner.gpg (the project's public key): commit it, so the shell and the ISOs carry it."
  else rm -f "$tmp"; fi
}
