#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Crimson Banner OS's pictures, made from the SVG sources in branding/src (branding/README.md).

    python3 branding/render.py          everything, into the places the shell, the packages and the ISOs take them from

Needs rsvg-convert (librsvg) and Python's Pillow and NumPy. The results are committed, so building Crimson Banner OS
doesn't need any of this: run it again only after changing a source.
"""
import io
import os
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "branding", "src")
ASSETS = os.path.join(ROOT, "RedStarOS", "Assets")


def svg(name, width, height=None):
    """An SVG source drawn at a size, as an RGBA picture."""
    args = ["rsvg-convert", "-w", str(width)] + (["-h", str(height)] if height else []) + [os.path.join(SRC, name)]
    return Image.open(io.BytesIO(subprocess.run(args, check=True, capture_output=True).stdout)).convert("RGBA")


def grain(im, amount=2.2, seed=7):
    """A very fine, fixed grain over a picture, so its smooth gradients don't show bands on a screen."""
    a = np.asarray(im.convert("RGB"), dtype=np.float32)
    noise = np.random.default_rng(seed).normal(0, amount, a.shape[:2])[..., None]
    return Image.fromarray(np.clip(a + noise, 0, 255).astype(np.uint8))


def save(im, *parts, **kw):
    path = os.path.join(ROOT, *parts)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if path.endswith(".jpg"):
        im.convert("RGB").save(path, quality=kw.get("quality", 90), optimize=True, progressive=True)
    else:
        im.save(path, optimize=True)
    print(os.path.relpath(path, ROOT), im.size)


def icon(size):
    """The app icon: the mark on its graphite tile."""
    tile = svg("tile.svg", size)
    m = int(size * 0.765)
    mark = svg("mark.svg", m)
    tile.alpha_composite(mark, ((size - m) // 2, (size - m) // 2))
    return tile


def start_button(size):
    """The Start button's picture: the mark on a round graphite disc with a thin rim."""
    scale = 4
    s = size * scale
    disc = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(disc)
    d.ellipse((0, 0, s - 1, s - 1), fill=(14, 14, 16, 255))
    d.ellipse((scale * 2, scale * 2, s - 1 - scale * 2, s - 1 - scale * 2), fill=(36, 36, 41, 255))
    disc = disc.resize((size, size), Image.LANCZOS)
    m = int(size * 0.74)
    disc.alpha_composite(svg("mark.svg", m), ((size - m) // 2, (size - m) // 2 + size // 40))
    return disc


def avatar(size):
    """A new account's picture: a plain figure on a crimson disc."""
    scale = 4
    s = size * scale
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.ellipse((0, 0, s - 1, s - 1), fill=(200, 16, 46, 255))
    shade = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(shade).ellipse((0, s * 0.35, s - 1, s * 1.6), fill=(90, 4, 18, 90))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).ellipse((0, 0, s - 1, s - 1), fill=255)
    im.paste(Image.alpha_composite(im, shade), (0, 0), mask)
    white = (255, 255, 255, 235)
    d = ImageDraw.Draw(im)
    d.ellipse((s * 0.34, s * 0.18, s * 0.66, s * 0.50), fill=white)                       # head
    body = Image.new("L", (s, s), 0)
    ImageDraw.Draw(body).ellipse((s * 0.17, s * 0.56, s * 0.83, s * 1.22), fill=255)       # shoulders
    body = Image.composite(body, Image.new("L", (s, s), 0), mask)
    im.paste(Image.new("RGBA", (s, s), white), (0, 0), body)
    return im.resize((size, size), Image.LANCZOS)


def panel_backdrop(w, h, glow=(0.75, 0.45), line=None):
    """The deck's graphite, lit by a soft crimson glow (and, if asked, its red line of light at a height)."""
    y, x = np.mgrid[0:h, 0:w].astype(np.float32)
    top, bottom = np.array([28, 28, 31], np.float32), np.array([8, 8, 9], np.float32)
    a = top + (bottom - top) * (y / h)[..., None]
    gx, gy = glow
    r = np.sqrt(((x / w - gx) * w / h) ** 2 + (y / h - gy) ** 2)
    a += np.array([196, 20, 28], np.float32) * (0.32 * np.clip(1 - r / 0.75, 0, 1) ** 2)[..., None]
    if line is not None:
        ly = line * h
        fade = np.clip(1 - np.abs(x / w - 0.5) / 0.42, 0, 1) ** 1.5
        core = np.exp(-((y - ly) / (h * 0.004)) ** 2) * fade
        halo = np.exp(-((y - ly) / (h * 0.04)) ** 2) * fade * 0.35
        a += np.array([240, 32, 40], np.float32) * (core + halo)[..., None]
    return Image.fromarray(np.clip(a, 0, 255).astype(np.uint8))


def font(size):
    from PIL import ImageFont
    for f in ("/usr/share/fonts/TTF/DejaVuSans-Bold.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"):
        if os.path.exists(f):
            return ImageFont.truetype(f, size)
    return None


def splash(w, h):
    """The BIOS boot menu's picture: the deck's graphite, the mark and the name above the menu."""
    im = panel_backdrop(w, h, glow=(0.5, 0.18), line=0.40)
    m = int(h * 0.22)
    mark = svg("mark.svg", m)
    im.paste(mark, ((w - m) // 2, int(h * 0.06)), mark)
    d = ImageDraw.Draw(im)
    f = font(int(h * 0.042))
    text = "CRIMSON BANNER OS"
    tw = d.textlength(text, font=f) if f else len(text) * 6
    d.text(((w - tw) / 2, int(h * 0.30)), text, fill=(232, 233, 236), font=f)
    return im


def welcome(w, h):
    """The installer's welcome picture: the name on the left, the banner on the right."""
    im = panel_backdrop(w, h, glow=(0.78, 0.4))
    m = int(h * 0.92)
    mark = svg("mark.svg", m)
    im.paste(mark, (w - m - int(w * 0.04), (h - m) // 2), mark)
    d = ImageDraw.Draw(im)
    d.text((int(w * 0.06), int(h * 0.34)), "Crimson Banner OS", fill=(240, 240, 242), font=font(int(h * 0.13)))
    d.text((int(w * 0.06), int(h * 0.56)), "FREE SOFTWARE · YOURS TO KEEP", fill=(240, 32, 40), font=font(int(h * 0.05)))
    return im


def main():
    if subprocess.run(["which", "rsvg-convert"], capture_output=True).returncode != 0:
        sys.exit("rsvg-convert is missing: install librsvg (pacman -S librsvg)")

    # the wallpapers (Software → Wallpapers lists Assets/Wallpapers; Assets/wallpaper.jpg is the default)
    papers = {}
    for name in ("banner", "folds", "deck", "daylight"):
        papers[name] = grain(svg(f"wallpaper-{name}.svg", 3840, 2160))
        save(papers[name], "RedStarOS", "Assets", "Wallpapers", f"crimson-{name}-4k.jpg")
    save(papers["banner"].resize((2880, 1620), Image.LANCZOS), "RedStarOS", "Assets", "wallpaper.jpg")

    # the login screen's backdrop: the banner, out of focus and dimmed, so the sign-in card stands out
    bg = papers["banner"].resize((1920, 1080), Image.LANCZOS).filter(ImageFilter.GaussianBlur(26))
    bg = Image.blend(bg, Image.new("RGB", bg.size, (6, 6, 7)), 0.30)
    save(grain(bg, 1.8, seed=11), "RedStarOS", "Assets", "login-bg.jpg", quality=88)

    # the mark, the icon, the Start button and a new account's picture
    save(svg("mark.svg", 512), "branding", "mark-512.png")
    save(svg("mark.svg", 256), "RedStarOS", "Assets", "mark.png")          # Brand.Mark: the splash, Welcome
    save(icon(256), "packaging", "redstaros.png")
    save(icon(512), "branding", "icon-512.png")
    save(start_button(128), "RedStarOS", "Assets", "icon-start.png")
    save(avatar(256), "RedStarOS", "Assets", "avatar.png")

    # Studio's three programs (Studio's window, and their entries in the Start menu where they use these)
    for app, asset in (("writer", "studio-writer.png"), ("sheets", "studio-sheet.png"), ("slides", "studio-slides.png")):
        save(svg(f"app-{app}.svg", 128), "RedStarOS", "Assets", asset)

    # the installer ISO: the boot menu and Calamares
    save(splash(640, 480), "iso", "profile", "syslinux", "splash.png")
    save(svg("mark.svg", 256), "iso", "calamares", "branding", "crimson", "logo.png")
    save(welcome(800, 300), "iso", "calamares", "branding", "crimson", "welcome.png")


if __name__ == "__main__":
    main()
