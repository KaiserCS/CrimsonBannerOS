# Working on Crimson Banner OS (formerly Red Star OS)

**The project's governing principle is self-reliance, in the Marxist-Leninist sense the owner has set. It is in
`PRINCIPLES.md`, and it decides between technical options.** In practice:

- Before adding any dependency, tool or service, prefer one governed by a community or non-profit over one a single
  company controls, copyleft over permissive, and never proprietary. Add it to the dependency table in
  `PRINCIPLES.md` in the same change, with who controls it and the way out. Say so to the owner when the
  self-reliant option costs more.
- Nothing may require a hosted service to build, test or run. `./check.sh` is the authority; CI only repeats it.
- No telemetry, analytics or phoning home. Opt out of tools' telemetry in scripts.
- New code is GPL-3.0-or-later.
- When work reduces a recorded dependency, update its row and the layer scores ("The goal" in `PRINCIPLES.md`).
  The aim is at least 75% of layers ours or community commons, and once reached 75% is a floor: no change may drop
  below it. Self-sufficiency to a point, not isolation: community GPL projects are welcome.
- The layer table also carries weights, and PRINCIPLES.md states the count and the weighted figure: keep both in step
  with the table (`tools/test-principles.sh`, run by `./check.sh`, fails when they differ). Exceptions have review
  dates (section 7); a passed date is a warning, so raise it with the owner. Section 8 says who decides: the owner.
  Don't invent governance rules or sign anything on the owner's behalf; put open questions to the owner.
- For critical calls (new dependencies, licences, hosting, distribution) check the FSF's advice (Free Software
  Directory, GNU licence list, FSDG; links in `PRINCIPLES.md`), say what it recommends, and note any difference.

## How the owner works

- Builds and tests on CachyOS (wlroots 0.20, gcc 16, .NET 8): `./check.sh`. The sandbox here usually lacks wlroots,
  Wayland headers and dotnet, so say plainly what couldn't be compiled or run.
- Versions are YY.MM.N from `VERSION` (README, "Versions"); `tools/version.sh` gives every part its version, and
  development builds are `26.11.0+5.g1a2b3c4`. Releases only with `tools/release.sh` when the owner asks (it tags;
  pushing a `v*` tag publishes a release). Don't hand-edit versions elsewhere.
- After a change: commit to `main` on KaiserCS/CrimsonBannerOS (formerly KaiserCS/RedStarOS-Linux; GitHub redirects the old
  name), and send a zip of the tree as `CrimsonBannerOS.zip`
  (without `.git`, `bin`, `obj`, `node_modules`, `__pycache__`, screenshots, `compositor/build`).
- Layout: `RedStarOS/` the Avalonia shell (C#), `compositor/` redstar-comp (C, wlroots), `wm/` redstar-wm (X11),
  `browser/` Crimson Online (C, GTK 4 + WebKitGTK),
  `tests/` C# tests, `compositor/test/` headless compositor tests, `browser/test/` the browser's unit tests and self-test,
  `tools/packages.sh` the local NuGet feed, `apps/` our own apps for Software (`tools/publish-app.sh`).
- The visible name is Crimson Banner OS (apps without a prefix, the browser Crimson Online); internal names
  (`redstar-*`, `RedStarOS`, `~/.config/redstaros`, app ids) stay as they are.
- Package versions are exact and locked (`packages.lock.json`, locked mode on CI). Changing one means updating the lock
  files in the same change (the CI "packages" job prints new ones in its log when they're missing).
