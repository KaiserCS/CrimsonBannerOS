#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# CI (iso.yml): installs Crimson Banner OS from the ISO onto an empty virtual disk, as a person would, then checks
# what was installed and starts it.
#   iso-install-test.sh ISO KERNEL
#  1. The ISO starts with crimson.test=install: the live session comes up and crimson-live-test drives the installer
#     from the keyboard (user "tester", password "crimson", the whole disk), then powers off. A picture of the
#     screen is taken at each of its steps.
#  2. The installed disk is looked at: the system's name, the kernel and GRUB, the user, the login screen, the update
#     repository, and nothing of the live session left behind.
#  3. The disk starts through its own GRUB (BIOS), twice: to its login screen, and signed straight in as tester.
#     Each must stay up two minutes with no crash and no black screen.
# Pictures are printed as base64 between "---- screen.png (NAME) ----" lines, as iso-boot-test.sh does.
set -uo pipefail
ISO="$1"; KERNEL="$2"
T="$(mktemp -d)"
sudo apt-get -qq update >/dev/null && sudo apt-get -qq install -y qemu-system-x86 libarchive-tools socat python3-pil >/dev/null
bsdtar -xf "$ISO" -C "$T" "crimson/boot/x86_64/vmlinuz-$KERNEL" "crimson/boot/x86_64/initramfs-$KERNEL.img"
LABEL="$(blkid -o value -s LABEL "$ISO")"
ACCEL=(-accel tcg); SLOW=3
if [ -e /dev/kvm ]; then sudo chmod 666 /dev/kvm; ACCEL=(-accel kvm -cpu host); SLOW=1; fi
DISK="$T/disk.img"
truncate -s 24G "$DISK"
MNT="$T/root"; mkdir -p "$MNT"
failed=()
fail() { echo "FAIL $*"; failed+=("$1"); }

picture() {   # socket name: the screen, printed as base64
  echo "screendump $T/$2.ppm" | socat - "UNIX-CONNECT:$1" >/dev/null 2>&1 || return 0
  sleep 1
  [ -f "$T/$2.ppm" ] || return 0
  python3 -c "from PIL import Image; im=Image.open('$T/$2.ppm'); im.thumbnail((960,720)); im.save('$T/$2.png', optimize=True)" 2>/dev/null || return 0
  echo "---- screen.png ($2) ----"; base64 -w0 "$T/$2.png"; echo; echo "---- end screen.png ----"
}
brightness() { python3 -c "from PIL import Image, ImageStat; print(int(ImageStat.Stat(Image.open('$1').convert('L')).mean[0]))" 2>/dev/null || echo 0; }
clean() { sed 's/\x1b\[[0-9;]*[a-zA-Z]//g' "$1"; }

# ── 1. installing ──
echo "======== installing ($(basename "$ISO"), ${ACCEL[1]}, $(date -u +%H:%M:%S)) ========"
qemu-system-x86_64 "${ACCEL[@]}" -m 4096 -smp 2 -cdrom "$ISO" -no-reboot \
  -drive "file=$DISK,format=raw,if=virtio" \
  -kernel "$T/crimson/boot/x86_64/vmlinuz-$KERNEL" -initrd "$T/crimson/boot/x86_64/initramfs-$KERNEL.img" \
  -append "archisobasedir=crimson archisolabel=$LABEL console=ttyS0,115200 cow_spacesize=50% systemd.show_status=1 systemd.journald.forward_to_console=1 crimson.test=install" \
  -vga std -display none -serial "file:$T/install.log" -monitor "unix:$T/install.sock,server,nowait" \
  -nic user,model=virtio-net-pci &
qemu=$!
seen=0
for _ in $(seq $((3600 * SLOW))); do
  kill -0 "$qemu" 2>/dev/null || break
  # a picture at each of the driver's steps
  n="$(grep -a "crimson-install-test\[[0-9]*\]:" "$T/install.log" 2>/dev/null | grep -av "log: " | grep -ac "step:")"; n="${n:-0}"
  while [ "$seen" -lt "$n" ]; do seen=$((seen + 1)); sleep 2; picture "$T/install.sock" "install-step-$seen"; done
  grep -a "crimson-install-test\[[0-9]*\]:" "$T/install.log" 2>/dev/null | grep -av "log: " | grep -q "FAILED:" && { sleep 15; picture "$T/install.sock" "install-failed"; break; }
  sleep 1
done
if kill -0 "$qemu" 2>/dev/null; then
  picture "$T/install.sock" "install-end"
  echo "quit" | socat - "UNIX-CONNECT:$T/install.sock" >/dev/null 2>&1 || true
  for _ in $(seq 15); do kill -0 "$qemu" 2>/dev/null || break; sleep 1; done
  kill -9 "$qemu" 2>/dev/null || true
fi
wait "$qemu" 2>/dev/null || true
echo "---- the installer's steps ----"
clean "$T/install.log" | grep -a "crimson-install-test" | sed 's/.*crimson-install-test\[[0-9]*\]: //' | tail -n 200
echo "---- serial console, filtered (installing) ----"
clean "$T/install.log" | grep -aE "Reached target|Failed|calamares|Calamares|crimson|abnormally|session" | grep -av "crimson-install-test" | tail -n 60
if ! clean "$T/install.log" | grep -aq "crimson-install-test\[[0-9]*\]: installed"; then
  echo "---- serial console, the end (installing) ----"; clean "$T/install.log" | tail -n 40
  fail install "the installer didn't finish"
  echo "Failed: ${failed[*]}"; exit 1
fi
echo "ok   install"

# ── 2. what was installed ──
LOOP="$(sudo losetup -fP --show "$DISK")"
sleep 2
lsblk -o NAME,SIZE,FSTYPE,LABEL "$LOOP"
ROOTPART=""; ROOTNUM=""
for p in "$LOOP"p*; do
  [ "$(sudo blkid -o value -s TYPE "$p" 2>/dev/null)" = ext4 ] || continue
  sudo mount "$p" "$MNT" 2>/dev/null || continue
  if [ -f "$MNT/etc/os-release" ]; then ROOTPART="$p"; ROOTNUM="${p##*p}"; break; fi
  sudo umount "$MNT"
done
check() {   # name, then a test run against the installed root
  local name="$1"; shift
  if sudo bash -c "cd '$MNT' && $*" >/dev/null 2>&1; then echo "ok   installed: $name"; else fail "installed:$name" "($*)"; fi
}
if [ -z "$ROOTPART" ]; then
  fail installed "no installed root file system on the disk"
else
  check "named Crimson Banner OS"            "grep -q '^NAME=\"Crimson Banner OS\"' etc/os-release"
  check "a kernel and its initramfs in /boot" "ls boot/vmlinuz-* && ls boot/initramfs-*.img"
  check "GRUB, with a menu"                   "grep -q 'menuentry' boot/grub/grub.cfg"
  check "GRUB names Crimson Banner OS"        "grep -q 'Crimson Banner OS' boot/grub/grub.cfg"
  check "the user tester, in wheel"           "grep -q '^tester:' etc/passwd && grep -q '^wheel:.*tester' etc/group"
  check "no live user"                        "! grep -q '^liveuser:' etc/passwd"
  check "no live sign-in or sudo"             "! grep -q initial_session etc/greetd/config.toml && [ ! -e etc/sudoers.d/liveuser ]"
  check "the login screen is greetd's"        "grep -q crimson-greeter etc/greetd/config.toml && [ -L etc/systemd/system/display-manager.service ]"
  check "the update repository is on"         "grep -q '^\[crimson-banner\]' etc/pacman.conf"
  check "the project's key is in pacman's"    "gpg --batch --homedir etc/pacman.d/gnupg --list-keys 19E71BACDC32A511D657F1FA9EC9146D98BEECA0"
  check "no installer left"                   "[ ! -e usr/bin/calamares ] && [ ! -e usr/bin/xdotool ]"
  check "no live test service"                "[ ! -e etc/systemd/system/crimson-live-test.service ]"
  # the boots below show the journal on the serial port (GRUB's own menu is otherwise untouched)
  sudo sed -i -E '/^[[:space:]]*linux[[:space:]]/ s/$/ console=ttyS0,115200 systemd.show_status=1 systemd.journald.forward_to_console=1/; s/ quiet( |$)/\1/' "$MNT/boot/grub/grub.cfg"
  sudo umount "$MNT"
fi
sudo losetup -d "$LOOP"

# ── 3. starting it ──
boot() {   # name: start the disk, check it stays up
  local name="$1" ok=0 why=""
  echo "======== the installed system: $name ($(date -u +%H:%M:%S)) ========"
  qemu-system-x86_64 "${ACCEL[@]}" -m 4096 -smp 2 -drive "file=$DISK,format=raw,if=virtio" \
    -vga std -display none -serial "file:$T/$name.log" -monitor "unix:$T/$name.sock,server,nowait" \
    -nic user,model=virtio-net-pci &
  local q=$!
  for _ in $(seq $((600 * SLOW))); do
    grep -qE "Reached target .*Graphical Interface" "$T/$name.log" 2>/dev/null && { ok=1; break; }
    kill -0 "$q" 2>/dev/null || break
    sleep 1
  done
  [ "$ok" = 1 ] || why="didn't reach the graphical target"
  [ "$ok" = 1 ] && sleep 120
  picture "$T/$name.sock" "installed-$name"
  echo "quit" | socat - "UNIX-CONNECT:$T/$name.sock" >/dev/null 2>&1 || true
  for _ in $(seq 15); do kill -0 "$q" 2>/dev/null || break; sleep 1; done
  kill -9 "$q" 2>/dev/null || true
  wait "$q" 2>/dev/null || true
  local log; log="$(clean "$T/$name.log")"
  if [ "$ok" = 1 ]; then
    case "$name" in
      greeter) grep -qE "login screen didn't start" <<<"$log" && why="the login screen didn't stay up" ;;
      session) grep -qE "Started Session [0-9]+ of User tester" <<<"$log" || why="tester's session didn't start"
               grep -qE "Removed session [0-9]+\.|session-[0-9]+\.scope: Deactivated" <<<"$log" && why="${why:+$why; }tester's session didn't stay up" ;;
    esac
    grep -aqE "terminated abnormally|malloc\(\)|double free|corrupted|crash report|no renderer|redstar-comp did not start" <<<"$log" \
      && why="${why:+$why; }something crashed or failed"
    [ -f "$T/installed-$name.ppm" ] && [ "$(brightness "$T/installed-$name.ppm")" -lt 6 ] && why="${why:+$why; }the screen is black"
  fi
  echo "---- serial console, filtered (installed $name) ----"
  grep -aE "Reached target|Failed|greetd|crimson|redstar|RedStarOS|Session|session|abnormally" <<<"$log" | tail -n 60 || true
  if [ -n "$why" ]; then fail "installed-$name" "$why"; else echo "ok   installed system: $name"; fi
}
if [ -n "$ROOTPART" ]; then
  boot greeter
  # signed straight in as tester, as if from the login screen
  LOOP="$(sudo losetup -fP --show "$DISK")"; sleep 2
  sudo mount "${LOOP}p$ROOTNUM" "$MNT"
  sudo python3 - "$MNT/etc/greetd/config.toml" <<'PY'
import sys
p = sys.argv[1]
s = open(p).read()
s += '\n[initial_session]\ncommand = "startx /usr/bin/env /usr/bin/redstaros-session"\nuser = "tester"\n'
open(p, "w").write(s)
PY
  sudo umount "$MNT"; sudo losetup -d "$LOOP"
  boot session
fi

[ "${#failed[@]}" -eq 0 ] || { echo "Failed: ${failed[*]}"; exit 1; }
echo "Installed, checked and started."
