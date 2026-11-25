#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Crimson Banner OS's live, installable ISO (archiso). tools/iso.sh fills in the @…@ values for each variant:
#   linux   Linux with the firmware most PCs need      crimson-banner-VERSION-x86_64.iso
#   libre   Linux-libre, nothing non-free on it         crimson-banner-libre-VERSION-x86_64.iso
#   mac     Linux for Intel Macs (T2 and older)         crimson-banner-mac-VERSION-x86_64.iso
# shellcheck disable=SC2034

iso_name="@ISO_NAME@"
iso_label="CRIMSON_@LABEL@"
iso_publisher="Crimson Banner OS <https://github.com/KaiserCS/CrimsonBannerOS>"
iso_application="Crimson Banner OS live and installer (@VARIANT_TITLE@)"
iso_version="@VERSION@"
install_dir="crimson"
buildmodes=('iso')
# UEFI through GRUB, which reads the kernel and initramfs from the ISO itself, as SYSLINUX does for BIOS; systemd-boot
# needed its own copy of them in the EFI boot image, about 230 MiB more (ROADMAP.md, "ISO size")
bootmodes=('bios.syslinux' 'uefi.grub')
pacman_conf="pacman.conf"
airootfs_image_type="squashfs"
# xz with the x86 filter: the smallest image, so each ISO stays under GitHub's 2 GiB per release file
airootfs_image_tool_options=('-comp' 'xz' '-Xbcj' 'x86' '-b' '1M' '-Xdict-size' '1M')
file_permissions=(
  ["/etc/shadow"]="0:0:400"
  ["/etc/gshadow"]="0:0:400"
  ["/etc/sudoers.d/liveuser"]="0:0:440"
  ["/root"]="0:0:750"
  ["/root/.gnupg"]="0:0:700"
  ["/home/liveuser"]="1000:1000:750"
  ["/home/liveuser/Desktop"]="1000:1000:755"
  ["/home/liveuser/.config"]="1000:1000:755"
  ["/home/liveuser/.config/redstaros"]="1000:1000:755"
  ["/home/liveuser/.config/redstaros/settings.json"]="1000:1000:644"
  ["/usr/local/bin/crimson-install"]="0:0:755"
  ["/usr/local/bin/crimson-live-cleanup"]="0:0:755"
  ["/usr/local/bin/crimson-live-session"]="0:0:755"
  ["/usr/local/bin/crimson-live-test"]="0:0:755"
  ["/usr/local/bin/crimson-os-release"]="0:0:755"
)
