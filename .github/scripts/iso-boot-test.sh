#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# CI (iso.yml): starts an ISO in QEMU, once per case, and checks what comes up on the screen.
#   iso-boot-test.sh ISO KERNEL CASE…        a case is WHAT:GRAPHICS, or WHAT:GRAPHICS:FIRMWARE
#     WHAT      x11 (the live session, X11), wayland (the Wayland session, in redstar-comp), greeter (the login screen
#               of an installed system); the ISO's crimson-live-test sets this up from crimson.test= on the command line
#     GRAPHICS  std (QEMU's standard VGA, bochs-drm), vmware (QEMU's VMware SVGA, which Linux's vmwgfx doesn't drive:
#               only an old frame buffer, no KMS device, so Wayland has to give way to X11) or virtio (virtio-gpu: a
#               KMS device without 3D, so redstar-comp draws in software, as on VMware's own vmwgfx)
#     FIRMWARE  left out: QEMU starts the ISO's kernel itself, with the test's command line. bios or uefi: the ISO
#               starts as a person's would, through its own boot menu (SYSLINUX, or GRUB on OVMF), its default
#               entry; the test's mode comes through QEMU instead (crimson-live-test reads it)
# Each case must reach the graphical target, keep its session (or login screen) up for two minutes without a crash,
# and show something other than a black screen. A picture of each screen is printed as base64 between
# "---- screen.png (CASE) ----" lines, since CI's files can't always be fetched.
set -uo pipefail
ISO="$1"; KERNEL="$2"; shift 2
CASES=("$@"); [ "${#CASES[@]}" -gt 0 ] || CASES=(x11:std)
T="$(mktemp -d)"
sudo apt-get -qq update >/dev/null && sudo apt-get -qq install -y qemu-system-x86 libarchive-tools socat python3-pil ovmf >/dev/null
bsdtar -xf "$ISO" -C "$T" "crimson/boot/x86_64/vmlinuz-$KERNEL" "crimson/boot/x86_64/initramfs-$KERNEL.img"
LABEL="$(blkid -o value -s LABEL "$ISO")"
ACCEL=(-accel tcg); WAIT=1500
if [ -e /dev/kvm ]; then sudo chmod 666 /dev/kvm; ACCEL=(-accel kvm -cpu host); WAIT=600; fi
failed=()

run_case() {
  local what vga fw ok=0 why=""
  IFS=: read -r what vga fw <<<"$1"
  local d="$T/$what-$vga${fw:+-$fw}"     # no ":" in the path: QEMU reads it as an option separator
  mkdir -p "$d"
  local start=()
  case "$fw" in
    "")   start=(-kernel "$T/crimson/boot/x86_64/vmlinuz-$KERNEL" -initrd "$T/crimson/boot/x86_64/initramfs-$KERNEL.img"
                 -append "archisobasedir=crimson archisolabel=$LABEL console=ttyS0,115200 cow_spacesize=50% systemd.show_status=1 systemd.journald.forward_to_console=1 crimson.test=$what") ;;
    bios) start=(-boot d -fw_cfg "name=opt/org.crimson-banner/test,string=$what") ;;
    uefi) local code="" vars=""
          for c in /usr/share/OVMF/OVMF_CODE_4M.fd /usr/share/OVMF/OVMF_CODE.fd; do [ -f "$c" ] && { code="$c"; break; }; done
          for v in /usr/share/OVMF/OVMF_VARS_4M.fd /usr/share/OVMF/OVMF_VARS.fd; do [ -f "$v" ] && { vars="$v"; break; }; done
          if [ -n "$code" ] && [ -n "$vars" ]; then
            cp "$vars" "$d/vars.fd"
            start=(-drive "if=pflash,format=raw,readonly=on,file=$code" -drive "if=pflash,format=raw,file=$d/vars.fd")
          else
            start=(-bios /usr/share/ovmf/OVMF.fd)
          fi
          start+=(-boot d -fw_cfg "name=opt/org.crimson-banner/test,string=$what") ;;
    *) echo "FAIL $1: no firmware called $fw"; failed+=("$1"); return ;;
  esac
  echo "======== $what on $vga graphics${fw:+, started through its own boot menu ($fw)}, $(date -u +%H:%M:%S) ($(basename "$ISO"), ${ACCEL[1]}) ========"
  qemu-system-x86_64 "${ACCEL[@]}" -m 4096 -smp 2 -cdrom "$ISO" "${start[@]}" \
    -vga "$vga" -display none -serial "file:$d/serial.log" -monitor "unix:$d/mon.sock,server,nowait" \
    -nic user,model=virtio-net-pci &
  local qemu=$!
  for _ in $(seq "$WAIT"); do
    if grep -qE "Reached target .*Graphical Interface" "$d/serial.log" 2>/dev/null; then ok=1; break; fi
    kill -0 "$qemu" 2>/dev/null || break
    sleep 1
  done
  [ "$ok" = 1 ] || why="didn't reach the graphical target"
  # the session (or login screen) starting, and staying; crimson-live-logs prints their logs at 100 s
  [ "$ok" = 1 ] && sleep 120
  echo "screendump $d/screen.ppm" | socat - "UNIX-CONNECT:$d/mon.sock" >/dev/null 2>&1 || true
  sleep 2
  echo "quit" | socat - "UNIX-CONNECT:$d/mon.sock" >/dev/null 2>&1 || true
  for _ in $(seq 15); do kill -0 "$qemu" 2>/dev/null || break; sleep 1; done
  kill -9 "$qemu" 2>/dev/null || true       # never wait on a QEMU that didn't take "quit"
  wait "$qemu" 2>/dev/null || true
  [ -f "$d/serial.log" ] || touch "$d/serial.log"
  local log; log="$(sed 's/\x1b\[[0-9;]*[a-zA-Z]//g' "$d/serial.log")"

  if [ "$ok" = 1 ]; then
    case "$what" in
      x11|wayland)
        # (with no display device the Wayland session hands over to X11 in the same session: still session 1)
        if ! grep -q "Started Session 1 of User liveuser" <<<"$log" || grep -qE "Removed session 1\.|session-1.scope: Deactivated" <<<"$log"; then
          why="the live user's session didn't stay up"
        fi ;;
      greeter)
        if grep -qE "login screen didn't start|Removed session" <<<"$log"; then why="the login screen didn't stay up"; fi ;;
    esac
    if grep -aqE "terminated abnormally|malloc\(\)|double free|corrupted|free\(\): invalid|crash report|no renderer|importing the supplied dmabufs failed|Restarting Xwayland|redstar-comp did not start" <<<"$log"; then
      why="${why:+$why; }something crashed or failed"
    fi
  fi
  echo "---- what tripped the crash check ($1) ----"
  grep -aE "terminated abnormally|malloc\(\)|double free|corrupted|free\(\): invalid|crash report|no renderer|importing the supplied dmabufs failed|Restarting Xwayland|redstar-comp did not start|exited without creating" <<<"$log" | head -n 20 || true
  echo "---- the session's own logs ($1) ----"
  grep -a "^LOG| " <<<"$log" || true
  echo "---- serial console, filtered ($1) ----"
  grep -aE "Reached target|Failed|greetd|Greeter|crimson|redstar|RedStarOS|Xwayland|XWAYLAND|renderer|pixman|dmabuf|Xorg|picom|abnormally|Stack trace|#[0-9]+ +0x|malloc|session" <<<"$log" | grep -av "^LOG| \|xdg-desktop-portal\|Portal service\|RealtimeKit" | tail -n 120 || true
  if [ -f "$d/screen.ppm" ]; then
    local bright
    bright="$(python3 -c "from PIL import Image, ImageStat; im=Image.open('$d/screen.ppm').convert('L'); print(int(ImageStat.Stat(im).mean[0]))")"
    echo "screen brightness: $bright/255"
    [ "$ok" = 1 ] && [ "$bright" -lt 6 ] && why="${why:+$why; }the screen is black"
    python3 -c "from PIL import Image; im=Image.open('$d/screen.ppm'); im.thumbnail((960,720)); im.save('$d/screen.png', optimize=True)"
    echo "---- screen.png ($1) ----"; base64 -w0 "$d/screen.png"; echo; echo "---- end screen.png ----"
  else
    why="${why:+$why; }no picture of the screen"
  fi
  if [ -n "$why" ]; then echo "FAIL $1: $why"; failed+=("$1"); else echo "ok   $1"; fi
}

for c in "${CASES[@]}"; do run_case "$c"; done
[ "${#failed[@]}" -eq 0 ] || { echo "Failed: ${failed[*]}"; exit 1; }
echo "Every case came up."
