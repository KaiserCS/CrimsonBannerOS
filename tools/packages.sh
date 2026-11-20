#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Red Star's own package feed (PRINCIPLES.md, "Package sources"): every NuGet package the shell, its tests and the
# AppImage need, kept on this machine, so building and checking need no package registry. The exact versions and
# their content hashes are in the repository (packages.lock.json next to each project); the feed holds just those.
#
#   tools/packages.sh fill            download them once from nuget.org into the feed (needs the network)
#   tools/packages.sh restore         restore from the feed alone: no registry, nothing from the network
#   tools/packages.sh export FILE     the feed as one .tar file, to carry to another machine
#   tools/packages.sh import FILE     unpack such a file into the feed
#   tools/packages.sh where           print the feed's folder
#
# The feed is $REDSTAR_PACKAGES, else ~/.local/share/redstaros/packages. ./check.sh and ./build-appimage.sh use it
# by themselves once it's filled.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1
cd "$(dirname "$0")/.."
FEED="${REDSTAR_PACKAGES:-${XDG_DATA_HOME:-$HOME/.local/share}/redstaros/packages}"
PROJECTS=(RedStarOS/RedStarOS.csproj tests/RedStarOS.Tests/RedStarOS.Tests.csproj)
RIDS=(linux-x64 linux-arm64)

# lock files present: they must match (no silent upgrade); missing: this run writes them
locked() {
  local p
  for p in "${PROJECTS[@]}"; do [ -f "$(dirname "$p")/packages.lock.json" ] || return 1; done
  return 0
}

case "${1:-}" in
  fill)
    mkdir -p "$FEED"
    LOCK=(); locked && LOCK=(--locked-mode)
    # restoring with --packages FEED lays the packages out the way a local NuGet source is read
    for p in "${PROJECTS[@]}"; do
      dotnet restore "$p" --packages "$FEED" "${LOCK[@]}"
    done
    # the self-contained AppImage also needs .NET's runtime and app-host packs for each architecture
    for rid in "${RIDS[@]}"; do
      # (-p:RuntimeIdentifier, not -r: "restore -r" replaces the project's RuntimeIdentifiers, which the lock file records)
      dotnet restore RedStarOS/RedStarOS.csproj --packages "$FEED" -p:RuntimeIdentifier="$rid" -p:SelfContained=true "${LOCK[@]}"
    done
    echo "Feed filled: $FEED ($(du -sh "$FEED" | cut -f1))"
    ;;
  restore)
    if [ ! -d "$FEED" ] || [ -z "$(ls -A "$FEED" 2>/dev/null)" ]; then
      echo "The package feed is empty ($FEED): run tools/packages.sh fill once (with the network), or import one." >&2
      exit 1
    fi
    locked || { echo "packages.lock.json is missing: run tools/packages.sh fill first" >&2; exit 1; }
    # --source replaces every configured source (nuget.org included); audit would ask nuget.org about advisories
    for p in "${PROJECTS[@]}"; do
      REDSTAR_LOCKED=1 dotnet restore "$p" --source "$FEED" --locked-mode -p:NuGetAudit=false
    done
    ;;
  export)
    [ -n "${2:-}" ] || { echo "usage: tools/packages.sh export FILE.tar" >&2; exit 1; }
    tar -C "$FEED" -cf "$2" .
    echo "Wrote $2"
    ;;
  import)
    [ -f "${2:-}" ] || { echo "usage: tools/packages.sh import FILE.tar" >&2; exit 1; }
    mkdir -p "$FEED"
    tar -C "$FEED" -xf "$2"
    echo "Imported into $FEED"
    ;;
  where) echo "$FEED" ;;
  *) sed -n '3,14p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
