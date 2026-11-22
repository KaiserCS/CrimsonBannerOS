#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Reads the logs hover-probe.sh collects and says whether a hover highlight blinked: a control left and entered
again with no other control in between, while the pointer moved steadily across it. Exits 1 when one did."""
import re
import sys

shell_log, comp_log = sys.argv[1], sys.argv[2]
events = []
for line in open(shell_log, encoding="utf-8", errors="replace"):
    parts = line.rstrip("\n").split("\t")
    if len(parts) >= 5 and parts[1] in ("enter", "exit"):
        events.append((int(parts[0]), parts[1], parts[2], parts[3], parts[4]))

print(f"---- hover: {len(events)} enter/exit events from the shell ----")
blinks = {}
for i in range(1, len(events) - 0):
    if i + 1 > len(events) - 1:
        break
    t0, w0, c0, _, p0 = events[i]
    t1, w1, c1, _, p1 = events[i + 1]
    # exit of C immediately followed by enter of C again (nothing else entered between): the highlight blinked
    if w0 == "exit" and w1 == "enter" and c0 == c1 and t1 - t0 < 400:
        blinks.setdefault(c0, []).append((t0, t1 - t0, p0, p1))

controls = sorted({e[2] for e in events})
print(f"controls hovered: {len(controls)}")
for c in controls[:40]:
    n = sum(1 for e in events if e[2] == c and e[1] == "enter")
    print(f"  {n:3} enters  {c}")

focus = [l.rstrip() for l in open(comp_log, encoding="utf-8", errors="replace") if l.startswith("pointer\t")]
print(f"---- redstar-comp: {len(focus)} changes of the surface under the pointer ----")
for l in focus[:60]:
    print("  " + l)

if blinks:
    print(f"---- BLINKS: {sum(len(v) for v in blinks.values())} in {len(blinks)} controls ----")
    for c, v in blinks.items():
        for t, gap, p0, p1 in v[:5]:
            print(f"  {c}: left at {p0}, back {gap} ms later at {p1} (t={t})")
    sys.exit(1)
print("no blinks")
