# Crimson Banner OS apps

Programs published to the Crimson Banner tab of Software. The first is [Planner](planner/README.md). They don't belong in every install (large or specialised
tools), so they're installed on demand. Each app is a folder here; `tools/publish-app.sh` builds it, signs it and
publishes it, without an OS release.

```
apps/planner/
  app.json        what Software shows (below)
  PKGBUILD        builds the pacman package (optional: or an AppImage)
  icon.png        its icon (PNG, 128×128 or larger)
  screenshot-1.png …
```

`app.json`:

```json
{
  "id": "planner",
  "name": "Planner",
  "summary": "Economic computation for central planning",
  "description": "Several paragraphs. Blank lines separate them.",
  "license": "GPL-3.0-or-later",
  "developer": "Crimson Banner OS",
  "homepage": "https://github.com/KaiserCS/CrimsonBannerOS",
  "categories": ["Science", "Office"],
  "icon": "icon.png",
  "screenshots": ["screenshot-1.png"],
  "package": "crimson-planner",
  "appimage": "build/Planner-x86_64.AppImage"
}
```

- `id`: lower case letters, digits and `-`; it names the app's files on the release page.
- `license`: an SPDX licence; it must be free software, or Software won't show the app.
- `package`: the pacman package the PKGBUILD makes (default: the id). On Arch, Software installs it from the
  `[crimson-banner-apps]` repository, so it updates with the system.
- `appimage`: an AppImage to offer as well, or instead (a path in the app's folder). It's how people on other
  systems get it.
- The version is the PKGBUILD's `pkgver` (or `"version"` in app.json for an AppImage-only app).

## Publishing

On the machine with the project's signing key (`./install-arch.sh` or `tools/repo.sh key` made it), signed in to
GitHub (`gh auth login`):

```bash
tools/publish-app.sh apps/planner     # one app (all of them: no argument)
tools/publish-app.sh --local          # everything, into /srv/crimson-banner-apps only (nothing uploaded)
```

It builds the package, signs it and adds it to the `[crimson-banner-apps]` repository. It writes the catalogue
(`catalog.json`, with every app's details and each download's checksum) and signs that too. Then it uploads the lot
to two GitHub releases that are kept up to date in place:

- `apps`: the catalogue, its signature, icons, screenshots and AppImages;
- `apps-x86_64`: the pacman repository.

Neither is ever marked as the latest OS release. Software checks the catalogue's signature against the project's
key, which is shipped with the shell (`software/crimson-banner.gpg`), before it shows anything. Your private key
stays on your computer.

The first time, the script writes `software/crimson-banner.gpg`, the public half of your key: commit it, so the
next shell build checks the catalogue.
