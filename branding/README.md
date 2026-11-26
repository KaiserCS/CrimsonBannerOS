# Branding

Crimson Banner OS's mark, icon, wallpapers and boot pictures. Every picture is drawn from the sources in `src/`, so
they can be changed and made again. Nothing is a photograph or someone else's artwork. They're under the project's
licence, GPL-3.0-or-later.

| Source | What it makes |
|---|---|
| `src/mark.svg` | **The mark:** a crimson swallowtail banner on a rod, with the star. The installer's logo, the boot menu, the Start button, the app icon |
| `src/tile.svg` | the app icon's graphite tile (the mark goes on it) |
| `src/app-writer.svg`, `app-sheets.svg`, `app-slides.svg` | Studio's three programs, on the same graphite tile |
| `src/app-planner.svg` | Planner (`apps/planner`): a plan's columns rising to a crimson target line |
| `src/wallpaper-banner.svg` | **Banner**, the default wallpaper (`Assets/wallpaper.jpg`), and, blurred and dimmed, the login screen's backdrop |
| `src/wallpaper-folds.svg` | **Folds**: the banner's cloth up close |
| `src/wallpaper-deck.svg` | **Deck**: plain graphite with one red line of light, like CD Player's panel |
| `src/wallpaper-daylight.svg` | **Daylight**: the banner in a pale room, for the light theme |

```bash
python3 branding/render.py    # needs rsvg-convert (librsvg), Pillow and NumPy
```

It writes the 4K wallpapers to `RedStarOS/Assets/Wallpapers/`, the default wallpaper and the login backdrop,
`packaging/redstaros.png` (the app icon), the Start button's picture, a new account's picture, the BIOS boot menu's
splash and the installer's logo and welcome picture. The results are committed, so a build doesn't need these
tools: run it again only after changing a source.

## The look

- **Colours:** crimson `#C8102E` (the accent; brighter `#E8243A` where it's lit, deeper `#7E0A1C` in shade), the
  display's red `#F02028`, and graphite from `#1C1C1F` to `#09090A`. In the light theme, `#F6F6F8` to `#E8E8EC`.
- **Shapes:** panels with 12 px corners lifted off a backdrop, pill buttons, small spaced capitals for labels, and
  figures in red digits on a dark display (`RedStarOS/Styles/Shell.axaml`, "Panels"; CD Player's own in
  `Styles/Deck.axaml`).
- **The mark** stands on its own; keep clear space of half its width around it, and don't recolour the banner.

The earlier wallpapers (the red-star collection and the galaxy picture) stay in Settings → Wallpaper.
