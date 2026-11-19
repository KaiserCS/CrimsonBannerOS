#!/bin/sh
# Builds redstar-wm and its behaviour tests, runs them on a virtual screen (Xvfb).
# Needs: gcc, libX11 headers, libXtst, Xvfb.   Optional: SHOT=file.png (needs ImageMagick) for a screenshot.
set -e
cd "$(dirname "$0")/.."
gcc -O2 -Wall -o redstar-wm redstar-wm.c -lX11 -ldl -lm
gcc -O0 -Wall -o test/wmtest test/wmtest.c -lX11 -l:libXtst.so.6
gcc -O0 -Wall -o test/clicktest test/clicktest.c -lX11 -l:libXi.so.6 -l:libXtst.so.6
DISP=:${WMTEST_DISPLAY:-63}
Xvfb $DISP -screen 0 1600x900x24 >/dev/null 2>&1 &
XVFB=$!
sleep 1
DISPLAY=$DISP ./redstar-wm >/dev/null 2>/tmp/redstar-wm-test.log &
WM=$!
sleep 0.5
STATUS=0
if [ -n "$SHOT" ]; then
    rm -f /tmp/wmtest-holding
    WMTEST_HOLD=4 DISPLAY=$DISP ./test/wmtest &
    T=$!
    while [ ! -f /tmp/wmtest-holding ] && kill -0 $T 2>/dev/null; do sleep 0.2; done
    DISPLAY=$DISP import -window root "$SHOT" || true
    wait $T || STATUS=$?
else
    DISPLAY=$DISP ./test/wmtest || STATUS=$?
fi
# click delivery, the way Avalonia gets input (XInput 2): every click arrives once, quickly
DISPLAY=$DISP ./test/clicktest client desk desktop >/dev/null 2>&1 &
C1=$!
DISPLAY=$DISP ./test/clicktest client app-a >/dev/null 2>&1 &
C2=$!
DISPLAY=$DISP ./test/clicktest client app-b >/dev/null 2>&1 &
C3=$!
sleep 0.5
DISPLAY=$DISP ./test/clicktest drive || STATUS=$?
kill $C1 $C2 $C3 2>/dev/null || true
kill $WM 2>/dev/null || true
kill $XVFB 2>/dev/null || true
cat /tmp/redstar-wm-test.log
rm -f test/wmtest test/clicktest
exit $STATUS
