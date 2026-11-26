# Atlas

Crimson Banner OS's maps, in Software's Crimson Banner tab (`crimson-atlas`). It keeps maps of the world on your
computer and draws them itself, so once a map is downloaded nothing needs the network.

## The maps

| What | Where it comes from | Run by |
|---|---|---|
| **Maps** | The Mapsforge project's ready-made maps (format version 5), one country or region a file, made from OpenStreetMap and remade every few weeks. Atlas lists and downloads them from the university mirror the project points programs to (`ftp-stud.hs-esslingen.de`), and from the project's own server (`download.mapsforge.org`) when the mirror can't be reached. | The Mapsforge project (community); the mirror, Hochschule Esslingen |
| **The data in them** | OpenStreetMap | Its contributors; the OpenStreetMap Foundation (non-profit). © OpenStreetMap contributors, under the ODbL, credited on the map |

Atlas goes online only when you ask for a list of maps (a continent, then its countries) or download one. Requests
name it (`CrimsonAtlas/1.0`); one map downloads at a time, into a `.part` file that a later download carries on from
(an HTTP Range request) rather than starting again, and the file has to be a Mapsforge map before it takes its name.
Some countries come in regions (Germany, France, the United States…) and some files are several gigabytes.

Atlas reads Mapsforge's file format with its own reader (`src/Model/Mapsforge.cs`, from the format's specification);
it uses none of Mapsforge's code.

## Using it

- **Map:** drag to move, the wheel or + − to zoom (2 to 19), the arrow keys to pan, a double click to look closer.
  Day and Night colours; the scale at the middle; "Whole map" to see all of it.
- **Search:** the places on the open map (cities, towns, villages, suburbs, islands, peaks), found once when a map is
  first opened and kept beside it as `<map>.places.json`. Case and accents don't matter: `lodz` finds Łódź.
- **Maps:** the maps on this computer (open, remove) and the server's, by continent. Any Mapsforge `.map` file opened
  with Atlas is copied in with them.
- An example island (`Example Island.map`, written by Atlas itself) is there before any map is downloaded.
- Maps are kept in `~/.local/share/crimson-atlas/maps`; the map last open and where are in
  `~/.config/crimson-atlas/settings.json`.

## How it draws

`Renderer` draws each 256-point tile with SkiaSharp (which comes with Avalonia) on worker threads, at the screen's
density: land, then the sea polygons the Mapsforge files carry, land cover, water, buildings (from zoom 14), roads
(casing, then fill, by importance, sized for the zoom), railways and borders. `MapView` keeps the tiles near the view,
shows a lower zoom's tile enlarged until a new one is ready, and places the names over the tiles so none overlap.

## Building

The source is in `src/` (C#, Avalonia), sharing the shell's `Styles/Shell.axaml` and `apps/shared`. The engine and
window are tested with the shell's tests (`tests/RedStarOS.Tests/AtlasTests.cs`, and screenshots of every page) on the
example island and a stand-in server, so the tests need no network. `MapWriter` writes the small Mapsforge files they
use.

```bash
dotnet run --project apps/atlas/src            # from the tree
tools/publish-app.sh apps/atlas                # build, sign and publish it to Software
```
