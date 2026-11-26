# Branding

Crimson Banner OS's mark, icon, wallpapers and boot pictures. Every picture is drawn from the sources in `src/`, so
they can be changed and made again. Nothing is a photograph or someone else's artwork. They're under the project's
licence, GPL-3.0-or-later.

| Source | What it makes |
|---|---|
| `src/mark.svg` | **The mark:** a crimson swallowtail banner on a rod, with the star. The installer's logo, the boot menu, the Start button, the app icon |
| `src/tile.svg` | the app icon's graphite tile (the mark goes on it) |
| `src/apps/*.svg` | **The shell's app icons**, one per app (`RedStarOS/Assets/apps/{id}.png`, shown wherever the app appears: Start menu, taskbar, switcher, notifications, Settings); Writer, Sheets and Slides also make Studio's three pictures |
| `src/app-planner.svg` | Planner (`apps/planner`): a plan's columns rising to a crimson target line |
| `src/app-archive.svg` | People's Archive (`apps/archive`): an open book with a crimson ribbon |
| `src/app-atlas.svg` | Atlas (`apps/atlas`): a folded map with a road, a river and a crimson pin |
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
- **App icons:** the graphite tile (`#2A2A2F` to `#0C0C0E`, a 12% white rim, corners of 54 on 240), a simple drawn
  picture in light greys (`#F2F2F5`, `#DCDCE3`, `#B4B4BC`, `#6E6E78`), and one crimson detail. They must still read at
  20 px, so shapes are bold and few. An app without a picture gets its line icon or initial, white on the same plate
  (`AppIcons.Tile`).
- **Line icons** (`RedStarOS/Services/Icons.cs`): a 24 grid, 2-unit strokes with round caps and joins, corners rounded,
  drawn inside 2.5 to 21.5; they take the colour of the text around them.
- **The mark** stands on its own; keep clear space of half its width around it, and don't recolour the banner.

The earlier wallpapers (the red-star collection and the galaxy picture) stay in Settings → Wallpaper.
