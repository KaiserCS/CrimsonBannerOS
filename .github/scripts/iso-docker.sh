#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# CI (iso.yml): runs tools/iso.sh in an Arch Linux container, with the big work folder on the runner's /mnt.
#   iso-docker.sh recipe NAME…   build iso/pkgs recipes into ./iso-repo (the kernels: kept in CI's cache)
#   iso-docker.sh iso VARIANT    build the rest and the ISO, into ./out
set -euo pipefail
WORKDIR="${ISO_HOST_WORK:-/mnt/crimson-iso}"
sudo mkdir -p "$WORKDIR" iso-repo out
sudo chmod 777 "$WORKDIR" iso-repo out
docker run --rm --privileged \
  -v "$PWD:/src" -v "$WORKDIR:/work" -v "$PWD/iso-repo:/repo" -v "$PWD/out:/out" \
  -e REDSTAR_ISO_REPO=/repo -e REDSTAR_ISO_WORK=/work -e REDSTAR_ISO_OUT=/out \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  archlinux:latest bash -euo pipefail -c '
    pacman-key --init >/dev/null 2>&1; pacman-key --populate archlinux >/dev/null 2>&1
    pacman -Syu --noconfirm --needed base-devel git sudo gnupg python archiso libisoburn \
      dotnet-sdk dotnet-runtime dotnet-targeting-pack-8.0 dotnet-runtime-8.0 >/dev/null
    useradd -m builder
    echo "builder ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/builder
    git config --global --add safe.directory /src
    sudo -u builder git config --global --add safe.directory /src
    chown -R builder /src /repo /work /out
    cd /src
    as_builder() { sudo -u builder -H --preserve-env=REDSTAR_ISO_REPO,REDSTAR_ISO_WORK,REDSTAR_ISO_OUT,DOTNET_CLI_TELEMETRY_OPTOUT,DOTNET_NOLOGO "$@"; }
    case "$0" in
      recipe) as_builder tools/iso.sh recipe "$@" ;;
      iso)
        as_builder tools/packages.sh fill >/dev/null
        as_builder tools/iso.sh packages "$1"
        tools/iso.sh image "$1" ;;
    esac
    # keys fetched from a key server are printed, to be committed under iso/pkgs/*/keys/pgp
    for k in iso/pkgs/*/keys/pgp/*.asc; do
      [ -f "$k" ] || continue
      git ls-files --error-unmatch "$k" >/dev/null 2>&1 && continue
      echo "NEWKEY $k $(base64 -w0 "$k")"
    done
  ' "$@"
