#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Tests for the login screen's system side (packaging/greeter): crimson-greeter-setup switching from another login
# screen and back, and crimson-greeter's fallback to the text login. Everything happens in a scratch folder with
# stand-ins for systemctl, redstar-comp and agreety. Run by ./check.sh.
set -euo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
fail=0
eq() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: got '$2', expected '$3'"; fail=1; fi; }

# ── crimson-greeter-setup ──
ROOT="$T/root"
mkdir -p "$ROOT/etc/greetd" "$ROOT/etc/systemd/system" "$T/bin"
printf '[terminal]\nvt = 1\n[default_session]\ncommand = "agreety --cmd /bin/sh"\nuser = "greeter"\n' > "$ROOT/etc/greetd/config.toml"
ln -s /usr/lib/systemd/system/sddm.service "$ROOT/etc/systemd/system/display-manager.service"
# systemctl's part in this: enable makes the display-manager alias, disable removes it
cat > "$T/bin/systemctl" <<SH
#!/bin/sh
echo "\$@" >> "$T/systemctl.log"
a="$ROOT/etc/systemd/system/display-manager.service"
case "\$1" in
  enable) [ -e "\$a" ] && [ "\$(basename "\$(readlink "\$a")")" != "\$2" ] && { echo "File exists" >&2; exit 1; }; ln -sf "/usr/lib/systemd/system/\$2" "\$a" ;;
  disable) [ "\$(basename "\$(readlink "\$a" 2>/dev/null)")" = "\$2" ] && rm -f "\$a"; true ;;
esac
SH
chmod +x "$T/bin/systemctl"
setup() { CRIMSON_ROOT="$ROOT" CRIMSON_SYSTEMCTL="$T/bin/systemctl" CRIMSON_GREETER_TEMPLATE="$SRC/packaging/greeter/greetd-config.toml" \
          "$SRC/packaging/greeter/crimson-greeter-setup" "$@"; }

eq "status before" "$(setup status)" "sddm"
setup enable >/dev/null
eq "greetd is the login screen" "$(basename "$(readlink "$ROOT/etc/systemd/system/display-manager.service")")" "greetd.service"
eq "status after" "$(setup status)" "Crimson Banner OS's login screen (greetd)"
eq "the config starts crimson-greeter" "$(grep -c '/usr/bin/crimson-greeter' "$ROOT/etc/greetd/config.toml")" "1"
eq "the old config is kept" "$(grep -c 'agreety' "$ROOT/etc/greetd/config.toml.crimson-banner.bak")" "1"
eq "the old login screen is remembered" "$(cat "$ROOT/etc/greetd/crimson-banner.previous-dm")" "sddm.service"
setup enable >/dev/null
eq "enabling twice keeps the first backup" "$(grep -c 'agreety' "$ROOT/etc/greetd/config.toml.crimson-banner.bak")" "1"
setup disable >/dev/null
eq "back to SDDM" "$(basename "$(readlink "$ROOT/etc/systemd/system/display-manager.service")")" "sddm.service"
eq "greetd's own config is back" "$(grep -c 'agreety' "$ROOT/etc/greetd/config.toml")" "1"
eq "nothing left behind" "$(ls "$ROOT/etc/greetd")" "config.toml"
# a computer with no login screen at all
rm "$ROOT/etc/systemd/system/display-manager.service" "$ROOT/etc/greetd/config.toml"
setup enable >/dev/null
eq "enabled with nothing before" "$(setup status)" "Crimson Banner OS's login screen (greetd)"
setup disable >/dev/null
eq "disabled again: no config of ours left" "$([ -e "$ROOT/etc/greetd/config.toml" ] && echo left || echo gone)" "gone"

# ── crimson-greeter's fallback ──
export CRIMSON_GREETER_RUN="$T/run" CRIMSON_GREETER_CACHE="$T/cache"
mkdir -p "$T/run" "$T/cache"
printf '#!/bin/sh\necho wayland >> "%s/tried"\necho "$*" > "%s/args"\nexit "${STUB_CODE:-0}"\n' "$T" "$T" > "$T/bin/redstar-comp"
printf '#!/bin/sh\necho x11 >> "%s/tried"\necho "$*" > "%s/args"\nexit "${STUB_X11:-${STUB_CODE:-0}}"\n' "$T" "$T" > "$T/bin/xinit"
printf '#!/bin/sh\n' > "$T/bin/Xorg"
printf '#!/bin/sh\necho agreety "$@"\n' > "$T/bin/agreety"
chmod +x "$T/bin/redstar-comp" "$T/bin/xinit" "$T/bin/Xorg" "$T/bin/agreety"
export CRIMSON_GREETER_XORG="$T/bin/Xorg"
mkdir -p "$T/dri" && touch "$T/dri/card0"
export CRIMSON_GREETER_DRI="$T/dri"
greeter() { PATH="$T/bin:$PATH" sh "$SRC/packaging/greeter/crimson-greeter"; }
STUB_CODE=1 greeter >/dev/null || true
STUB_CODE=1 greeter >/dev/null || true
eq "two quick failures are counted" "$(cat "$T/run/failures")" "2"
eq "both on Wayland" "$(tr '\n' ' ' < "$T/tried")" "wayland wayland "
STUB_CODE=1 greeter >/dev/null || true
STUB_CODE=1 greeter >/dev/null || true
eq "then twice on X11" "$(tr '\n' ' ' < "$T/tried")" "wayland wayland x11 x11 "
eq "then the text login" "$(greeter | tail -n 1)" "agreety --cmd /bin/sh -l"
echo 2 > "$T/run/failures"
STUB_X11=0 greeter >/dev/null
eq "the X11 login screen working resets the count" "$(cat "$T/run/failures")" "0"
echo 0 > "$T/run/failures"
STUB_CODE=1 greeter >/dev/null || true
STUB_CODE=0 greeter >/dev/null
eq "someone signing in resets the count" "$(cat "$T/run/failures")" "0"
eq "and it's the graphical one again" "$(STUB_CODE=0 greeter; echo done)" "done"
# the login screen it starts: crimson-login
export CRIMSON_GREETER_LOGIN="$T/bin/crimson-login"
printf '#!/bin/sh\n' > "$T/bin/crimson-login"; chmod +x "$T/bin/crimson-login"
echo 0 > "$T/run/failures"
greeter >/dev/null
eq "it starts crimson-login" "$(cat "$T/args")" "-- $T/bin/crimson-login"
# no KMS display device at all (an old frame buffer only): X11 straight away
rm -f "$T/tried" "$T/dri/card0"; echo 0 > "$T/run/failures"
STUB_CODE=0 greeter >/dev/null
eq "no display device: X11 first" "$(tr '\n' ' ' < "$T/tried")" "x11 "
eq "and crimson-login on X11 too" "$(cat "$T/args")" "$T/bin/crimson-login -- $T/bin/Xorg :7 vt1 -nolisten tcp -keeptty"

exit "$fail"
