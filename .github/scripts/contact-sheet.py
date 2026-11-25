#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
# CI: every window's screenshot, tiled into a few contact sheets and printed in the log as base64 between
# "---- sheet NAME ----" lines, so a change to the look can be checked window by window without fetching artifacts.
#   contact-sheet.py FOLDER LABEL
import base64
import io
import os
import sys

from PIL import Image, ImageDraw

folder, label = sys.argv[1], sys.argv[2]
shots = sorted(f for f in os.listdir(folder) if f.endswith(".png")) if os.path.isdir(folder) else []
COLS, ROWS, W, H = 4, 3, 470, 300
for n in range(0, len(shots), COLS * ROWS):
    sheet = Image.new("RGB", (COLS * W, ROWS * (H + 18)), (60, 60, 64))
    d = ImageDraw.Draw(sheet)
    for i, name in enumerate(shots[n:n + COLS * ROWS]):
        im = Image.open(os.path.join(folder, name)).convert("RGB")
        im.thumbnail((W - 8, H - 8))
        x, y = (i % COLS) * W, (i // COLS) * (H + 18)
        sheet.paste(im, (x + (W - im.width) // 2, y + (H - im.height) // 2))
        d.text((x + 6, y + H + 3), name[:-4], fill=(235, 235, 235))
    buf = io.BytesIO()
    sheet.save(buf, "JPEG", quality=72, optimize=True)
    print(f"---- sheet {label}-{n // (COLS * ROWS) + 1:02d} ----")
    print(base64.b64encode(buf.getvalue()).decode())
    print("---- end ----")
print(f"{len(shots)} screenshots in {label}")
