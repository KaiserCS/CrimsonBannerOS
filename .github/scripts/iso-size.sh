#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# CI (iso.yml): what takes the room in an ISO, read from the ISO itself (mkarchiso -r has deleted its work folder).
#   iso-size.sh ISO
# GitHub takes files up to 2 GiB, so this is printed with every build (ROADMAP.md, "ISO size").
set -uo pipefail
ISO="$1"
T="$(mktemp -d)"
sudo apt-get -qq update >/dev/null && sudo apt-get -qq install -y libarchive-tools squashfs-tools initramfs-tools-core >/dev/null
mib() { awk '{ printf "%8.1f MiB  %s\n", $1 / 1048576, substr($0, index($0, $2)) }'; }

total=$(stat -c %s "$ISO")
echo "======== $(basename "$ISO"): $(awk -v b="$total" 'BEGIN { printf "%.1f MiB (%.2f GiB of the 2 GiB GitHub takes)", b/1048576, b/1073741824 }') ========"

echo "---- the ISO's files (the EFI boot image is a partition appended to them, not a file) ----"
bsdtar -tvf "$ISO" | awk '$1 !~ /^d/ { print $5, $9 }' | sort -rn | head -n 12 | mib
files=$(bsdtar -tvf "$ISO" | awk '$1 !~ /^d/ { s += $5 } END { print s }')
echo "$((total - files))" "the rest: the EFI boot image (FAT) and the ISO's own structures" | mib

echo "---- the boot files ----"
bsdtar -xf "$ISO" -C "$T" --include '*/boot/*' --include '*/x86_64/airootfs.sfs'
find "$T" -path '*/boot/*' -type f -printf '%s %P\n' | sort -rn | head -n 8 | mib
init="$(find "$T" -name 'initramfs-*.img' | head -n 1)"
if [ -n "$init" ]; then
  mkdir -p "$T/init"
  if unmkinitramfs "$init" "$T/init" 2>/dev/null; then
    echo "-- inside the initramfs, unpacked (it's xz-compressed on the ISO) --"
    du -sb "$T/init" | awk '{ print $1, "all of it" }' | mib
    du -sb "$T"/init/main/usr/lib/firmware/* "$T"/init/usr/lib/firmware/* 2>/dev/null | sed "s#$T/init/\(main/\)\{0,1\}##" | sort -rn | head -n 12 | mib
    du -sb "$T"/init/main/usr/lib/modules "$T"/init/usr/lib/modules 2>/dev/null | sed "s#$T/init/\(main/\)\{0,1\}##" | mib
  fi
fi

SFS="$(find "$T" -name airootfs.sfs | head -n 1)"
echo "---- the live system (airootfs.sfs, $(stat -c %s "$SFS" | awk '{ printf "%.1f MiB", $1/1048576 }') compressed); sizes below are uncompressed ----"
# every file's size from the listing, summed by folder two and three levels down
unsquashfs -lls "$SFS" 2>/dev/null | awk '$1 ~ /^-/ { sub(/^squashfs-root/, "", $6); print $3, $6 }' > "$T/files"
awk '{ s += $1 } END { print s, "all of it" }' "$T/files" | mib
echo "-- by folder --"
for depth in 4 5; do
  awk -v d="$depth" '{ n = split($2, p, "/"); k = ""; for (i = 2; i <= d && i <= n; i++) k = k "/" p[i]; s[k] += $1 } END { for (k in s) print s[k], k }' "$T/files"
done | sort -rn | awk '!seen[$2]++' | head -n 40 | mib

echo "-- by package (pacman's installed size) --"
unsquashfs -q -d "$T/db" "$SFS" var/lib/pacman/local >/dev/null 2>&1
for d in "$T"/db/var/lib/pacman/local/*/; do
  [ -f "$d/desc" ] || continue
  awk '/^%NAME%/ { getline; n = $0 } /^%SIZE%/ { getline; s = $0 } END { print s, n }' "$d/desc"
done | sort -rn > "$T/pkgs"
echo "$(wc -l < "$T/pkgs") packages"
head -n 45 "$T/pkgs" | mib
echo "-- by kind --"
awk '{ k = "other"
       if ($2 ~ /firmware|ucode/) k = "firmware (linux-firmware-*, sof, microcode)"
       else if ($2 ~ /^(linux|linux-lts|linux-zen|linux-libre)$/) k = "the kernel and its modules"
       else if ($2 ~ /font|noto|ttf|otf/) k = "fonts"
       else if ($2 ~ /^(llvm|clang|gcc|binutils|gdb|python|perl|ruby|nodejs|dotnet)/) k = "compilers and interpreters (llvm, gcc, python, perl, gdb…)"
       else if ($2 ~ /^(qt|kf6|plasma|kirigami)/) k = "Qt and KDE (for Calamares, the installer)"
       else if ($2 ~ /^(gtk|webkit|gst|libadwaita|glib|gnome)/) k = "GTK, WebKitGTK and GStreamer (Crimson Online, media)"
       else if ($2 ~ /^(crimson|redstar)/) k = "Crimson Banner OS itself"
       else if ($2 ~ /^(mesa|vulkan|libva|intel-media|nvidia|xf86-|xorg|wayland|wlroots|libdrm)/) k = "graphics (Mesa, Xorg, Vulkan)"
       else if ($2 ~ /^(libreoffice|onlyoffice|vlc|mpv|ffmpeg|firefox|chromium)/) k = "big programs (office, media)"
       s[k] += $1 } END { for (k in s) print s[k], k }' "$T/pkgs" | sort -rn | mib
echo "-- documentation and translations (uncompressed) --"
for p in usr/share/doc usr/share/man usr/share/info usr/share/help usr/share/locale usr/share/gtk-doc usr/include; do
  awk -v p="/$p/" 'index($2, p) == 1 { s += $1 } END { print s + 0 }' "$T/files" | awk -v p="/$p" '{ print $1, p }'
done | sort -rn | mib
rm -rf "$T"
