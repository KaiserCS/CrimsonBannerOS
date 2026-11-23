#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# CI (iso.yml): starts an ISO in QEMU and checks that the live session comes up: systemd reaches the graphical
# target and greetd signs the live user in. A picture of the screen is printed as base64 (CI's files can't always be
# fetched), between "---- screen.png ----" lines.
#   iso-boot-test.sh ISO KERNEL
set -euo pipefail
ISO="$1"; KERNEL="$2"
T="$(mktemp -d)"
sudo apt-get -qq update >/dev/null && sudo apt-get -qq install -y qemu-system-x86 libarchive-tools socat python3-pil >/dev/null
bsdtar -xf "$ISO" -C "$T" "crimson/boot/x86_64/vmlinuz-$KERNEL" "crimson/boot/x86_64/initramfs-$KERNEL.img"
LABEL="$(blkid -o value -s LABEL "$ISO")"
ACCEL=(-accel tcg); WAIT=1500
if [ -e /dev/kvm ]; then sudo chmod 666 /dev/kvm; ACCEL=(-accel kvm -cpu host); WAIT=600; fi
echo "Booting $(basename "$ISO") ($LABEL, ${ACCEL[1]})"
qemu-system-x86_64 "${ACCEL[@]}" -m 4096 -smp 2 -cdrom "$ISO" \
  -kernel "$T/crimson/boot/x86_64/vmlinuz-$KERNEL" -initrd "$T/crimson/boot/x86_64/initramfs-$KERNEL.img" \
  -append "archisobasedir=crimson archisolabel=$LABEL console=ttyS0,115200 systemd.show_status=1 systemd.journald.forward_to_console=1" \
  -vga std -display none -serial "file:$T/serial.log" -monitor "unix:$T/mon.sock,server,nowait" \
  -nic user,model=virtio-net-pci &
QEMU=$!
ok=0
for _ in $(seq "$WAIT"); do
  if grep -qE "Reached target .*Graphical Interface" "$T/serial.log" 2>/dev/null; then ok=1; break; fi
  kill -0 "$QEMU" 2>/dev/null || break
  sleep 1
done
# the live user's desktop starting: its session must still be there after a while (if it ends, greetd falls back to
# the login screen)
if [ "$ok" = 1 ]; then
  sleep 120
  if ! grep -q "Started Session 1 of User liveuser" "$T/serial.log" || grep -qE "Removed session 1\.|session-1.scope: Deactivated" "$T/serial.log"; then
    echo "The live user's session didn't stay up"; ok=0
  fi
fi
echo "screendump $T/screen.ppm" | socat - "UNIX-CONNECT:$T/mon.sock" >/dev/null || true
sleep 2
echo "quit" | socat - "UNIX-CONNECT:$T/mon.sock" >/dev/null 2>&1 || true
wait "$QEMU" 2>/dev/null || true
echo "---- serial console (filtered) ----"
sed 's/\x1b\[[0-9;]*[a-zA-Z]//g' "$T/serial.log" | grep -aE "Reached target|Failed|greetd|Greeter|session|Session|Xorg|xinit|startx|redstaros|RedStarOS|crimson|calamares|error|liveuser" | tail -n 150 || true
if [ -f "$T/screen.ppm" ]; then
  python3 -c "from PIL import Image; im=Image.open('$T/screen.ppm'); im.thumbnail((960,720)); im.save('$T/screen.png', optimize=True)"
  echo "---- screen.png ----"; base64 -w0 "$T/screen.png"; echo; echo "---- end screen.png ----"
fi
[ "$ok" = 1 ] || { echo "The live system didn't reach the graphical target"; tail -n 60 "$T/serial.log"; exit 1; }
echo "The live system came up."
