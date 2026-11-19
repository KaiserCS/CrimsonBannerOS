# Working on Red Star OS

**The project's governing principle is self-reliance, in the Marxist-Leninist sense the owner has set. It is in
`PRINCIPLES.md`, and it decides between technical options.** In practice:

- Before adding any dependency, tool or service, prefer one governed by a community or non-profit over one a single
  company controls, copyleft over permissive, and never proprietary. Add it to the dependency table in
  `PRINCIPLES.md` in the same change, with who controls it and the way out. Say so to the owner when the
  self-reliant option costs more.
- Nothing may require a hosted service to build, test or run. `./check.sh` is the authority; CI only repeats it.
- No telemetry, analytics or phoning home. Opt out of tools' telemetry in scripts.
- New code is GPL-3.0-or-later.
- When work reduces a recorded dependency, update its row.

## How the owner works

- Builds and tests on CachyOS (wlroots 0.20, gcc 16, .NET 8): `./check.sh`. The sandbox here usually lacks wlroots,
  Wayland headers and dotnet, so say plainly what couldn't be compiled or run.
- After a change: commit to `main` on KaiserCS/RedStarOS-Linux, and send a zip of the tree as `RedStarOS-Linux.zip`
  (without `.git`, `bin`, `obj`, `node_modules`, `__pycache__`, screenshots, `compositor/build`).
- Layout: `RedStarOS/` the Avalonia shell (C#), `compositor/` redstar-comp (C, wlroots), `wm/` redstar-wm (X11),
  `webengine/` the browser engine (Electron), `tests/` C# tests, `compositor/test/` headless compositor tests.
