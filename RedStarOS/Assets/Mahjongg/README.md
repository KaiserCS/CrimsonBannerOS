# Mahjongg tile sets

These tile sets come from **GNOME Mahjongg** (https://gitlab.gnome.org/GNOME/gnome-mahjongg, its GitHub mirror at
commit 888ea7dfbc4a4764c063fe319dfbe7c05647556c, 18 Nov 2026), and are used under the same licence as the rest of
Crimson Banner OS: **GPL-3.0-or-later** (see LICENSE in the repository root).

| File | From | Copyright |
|---|---|---|
| `smooth.png` | `data/themes/smooth.png`, based on the Smooth tileset (https://github.com/j-evins/smooth-tileset) | 2004–2025 Mahjongg Contributors |
| `postmodern.png` | `data/themes/postmodern.svg`, drawn to a PNG with rsvg-convert at 5504×352 | 2004–2025 Mahjongg Contributors |
| `educational.png` | `data/themes/educational.png` | 2016 Mahjongg Contributors |

Each sheet holds 43 tiles in a row (dots, the four winds, red and green dragons, characters, bamboo, flowers, the white
dragon, seasons, a blank tile), and below them the same tiles highlighted. Each picture is the tile plus its depth:
1/7 of the tile's width on the left and 1/10 of its height at the bottom. `MahjonggView.SheetIndex` maps Crimson
Banner's faces onto them.
