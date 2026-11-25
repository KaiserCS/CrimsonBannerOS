# Crimson Banner OS

A self-reliant Linux desktop and distribution, based on Arch Linux. The main parts are its own, and all of it is free
software under the GPL:

| Part | What it is |
|---|---|
| **The desktop shell** (`RedStarOS/`) | Taskbar, Start menu, desktop, notifications, lock screen, Settings and the apps below. C#, Avalonia 12, .NET 8 |
| **redstar-comp** (`compositor/`) | The Wayland compositor: window frames, snapping, shadows, Xwayland. C, wlroots 0.20 |
| **redstar-wm** (`wm/`) | The window manager for the X11 session. C, Xlib |
| **Crimson Online** (`browser/`) | The web browser: tabs, a content blocker, reader view, Save as PDF. C, GTK 4, WebKitGTK |
| **The login screen** | Replaces SDDM, GDM or LightDM. Runs on greetd, inside redstar-comp |
| **Software** | Installs more programs: our own catalogue, Flatpaks from Flathub, AppImages. Free software only |

Why it's built this way, and which dependencies it accepts, is set out in [PRINCIPLES.md](PRINCIPLES.md).

## The apps

- **Office:** Writer, Sheets, Slides, Studio
- **Media:** CD Player and Video (LibVLC; the deck look after [CDPlayer](https://github.com/Kizarov3/CDPlayer) by Kizarov3, MIT), Paint, Screen Recorder, Voice Memos
- **Online:** Crimson Online, Messenger (Matrix)
- **Games:** Blocks, Gems, Mahjongg, Ping Pong
- **Utilities:** File Manager, Terminal, Calculator, Calendar, Camera, Clock, Notes, Image Viewer, Screenshot, Disks
- **System:** Software, Task Manager, Settings:
  - connections and devices: Wi-Fi & Network, Bluetooth, Displays, Sound, Printers, Power & battery;
  - your account and system: Users, Region & keyboard, Date & time, Accessibility, Default apps, Startup apps;
  - care: Software Updates, Diagnostics.

Programs installed on the system (from pacman, Flatpak or Software) appear in the Start menu next to these.

## Installing

**On a new computer: the installer ISOs** (being built; they'll be on [Releases](https://github.com/KaiserCS/CrimsonBannerOS/releases)).
Each is a live Crimson Banner OS with an installer (Calamares) that needs no network. There are three, one per kernel:

| ISO | Kernel | For | Non-free parts |
|---|---|---|---|
| `crimson-banner-VERSION-x86_64.iso` | Linux | most PCs | linux-firmware, CPU microcode, sound firmware |
| `crimson-banner-libre-VERSION-x86_64.iso` | Linux-libre | hardware that works with free firmware | none |
| `crimson-banner-mac-VERSION-x86_64.iso` | Linux for Intel Macs (t2linux) | T2 Macs (2018–2020) and older Intel Macs | as the first, plus Broadcom's wl Wi-Fi driver; T2 Macs' Wi-Fi firmware is copied from their own macOS |

The Linux ISO comes first; the libre and Mac ones are put aside until later (their recipes are in `iso/`).
Write one to a USB stick (`sudo dd if=crimson-banner-….iso of=/dev/sdX bs=4M status=progress`, or any image
writer), start the computer from it, and choose **Install Crimson Banner OS**. `tools/iso.sh all` builds them on any
Arch machine ([iso/README.md](iso/README.md)).

**On Arch, CachyOS or EndeavourOS** (the supported way):

```bash
git clone https://github.com/KaiserCS/CrimsonBannerOS.git
cd CrimsonBannerOS
./install-arch.sh               # build, sign and install the packages; run it again to update
```

Run it as yourself: it asks for your password when it needs one. It builds the packages from the source, signs them
into Crimson Banner's own pacman repository (`/srv/crimson-banner`) and installs them with pacman. It then offers to
switch the login screen to Crimson Banner's own. When it's done, sign out and choose **Crimson Banner OS**
(X11) or **Crimson Banner OS (Wayland)**.

| | |
|---|---|
| `./install-arch.sh --pull` | `git pull` first, then build and install |
| `./install-arch.sh --extras` | also the optional programs: drives, users, video, screen reader, screenshots, recording |
| `./install-arch.sh --login-screen` | switch to the login screen without asking |
| `./install-arch.sh --uninstall` | remove everything (the previous login screen comes back) |

**Elsewhere:** the AppImage from [Releases](https://github.com/KaiserCS/CrimsonBannerOS/releases) runs the desktop on
top of your current one. `sudo ./install-session.sh` adds it to your login screen as a session.

## The login screen

```bash
crimson-greeter-setup preview        # look at it in a window first (any password works, nothing starts)
sudo crimson-greeter-setup enable    # use it from the next start
sudo crimson-greeter-setup disable   # back to the previous login screen
```

If it ever fails to come up twice in a row, greetd's text login takes over until the next restart, so it can't
lock you out. The reason is logged in `/var/cache/crimson-greeter/greeter.log`.

## Updates

- **The system** (Settings → Software Updates). On Arch this goes straight to pacman: `checkupdates` lists the
  updates, and installing runs `pacman -Syu` behind the password prompt. Other systems go through PackageKit.
- **Crimson Banner OS itself:** on a system installed from the ISO, with the rest of the system: each release's
  packages are published as the signed `[crimson-banner]` pacman repository (`tools/publish-os.sh`), so Software
  Updates brings them. On a machine set up with `./install-arch.sh`: `./install-arch.sh --pull` (it builds your own
  `[crimson-banner]`). The AppImage checks for new releases once a day. `crimson-repo status` shows which you have.
- **The update channel:** releases, or testing, which is builds of main before they're released (Settings → Software
  Updates → Update channel, or `sudo crimson-repo channel testing`; `releases` goes back). Both are the same
  `[crimson-banner]` repository on two servers. Testing gets each release too, so it never falls behind.
- **Programs from Software:** its Installed page, with Update all.

## Publishing our own apps

Programs made for Crimson Banner OS are published to Software without an OS release. Each is a folder in `apps/`,
and one command builds it, signs it and uploads it:

```bash
tools/publish-app.sh apps/planner
```

Software checks the catalogue's signature against the project's key before showing anything. The details are in
[apps/README.md](apps/README.md).

## Building and testing

```bash
sudo pacman -S --needed base-devel dotnet-sdk dotnet-targeting-pack-8.0 meson ninja wlroots0.20 xorg-xwayland xcb-util-wm \
                        webkitgtk-6.0 gtk4 json-glib libsoup3
./check.sh                         # build everything and run every test: the authority on whether a change is good
./check.sh --packages              # also build the Arch packages
dotnet run --project RedStarOS     # run the shell from the source tree
./build-appimage.sh                # dist/RedStarOS-x86_64.AppImage
```

`./check.sh` runs the C# tests (every window is opened headlessly), redstar-wm's tests under Xvfb, redstar-comp's
tests headless, Crimson Online's unit tests and self-test, and the scripts' tests. It skips the parts whose
libraries aren't installed, and says so. CI (`.github/workflows/check.yml`) runs the same checks on every push. It
also builds the Arch packages with the package registry cut off, installs them with pacman, and publishes a test
app end to end.

**No network needed:** NuGet package versions and hashes are locked (`packages.lock.json`). After
`tools/packages.sh fill` has downloaded them once into our own feed, every build restores from it alone.

## Versions

Releases are numbered **YY.MM.N**: the year, the month, then a fix number. `VERSION` holds the latest release, and
`tools/version.sh` gives every part of the system the same version. A build after a release reads like
`26.11.1+5.g1a2b3c4`: 5 commits later, at commit `1a2b3c4`. `tools/release.sh` makes a release: it sets
`VERSION`, writes the [changelog](CHANGELOG.md), commits and tags. Pushing the tag publishes the AppImage and the
installer ISOs on GitHub Releases. Then, on the computer with the project's signing key:

```bash
tools/publish-os.sh                  # installed systems get the newest release as an update
tools/publish-os.sh --tag v26.12.0   # or a particular one
tools/publish-os.sh --testing        # main, as pushed, to the testing channel, whenever it's worth trying
```

It builds the packages from the release's tagged commit (in a temporary copy: nothing is checked out), signs them
with the project's key and uploads the `[crimson-banner]` repository to the `os-x86_64` release, and to
`os-testing-x86_64` too unless testing already has something newer. `--testing` builds `origin/main` the same way
and uploads it to `os-testing-x86_64` only. The project's key
is `19E7 1BAC DC32 A511 D657  F1FA 9EC9 146D 98BE ECA0`; the script won't sign a published release with another. The first time, it writes `software/crimson-banner.gpg` and `software/os-repo`: commit them,
and ISOs built from then on turn updates on when they install (`crimson-repo enable os`; on a system installed
before, run that once by hand).

## Project layout

| | |
|---|---|
| `RedStarOS/` | the desktop shell and its apps |
| `compositor/`, `wm/`, `browser/` | redstar-comp, redstar-wm, Crimson Online, each with its tests |
| `tests/` | the shell's tests |
| `packaging/` | the Arch packages (`arch/PKGBUILD`), sessions, login screen, PAM |
| `iso/` | the installer ISOs: archiso profile, Calamares settings, the three variants, kernel recipes |
| `apps/`, `software/` | our own apps for Software, and its catalogue and key |
| `tools/` | releases, versions, the package repository and feed, publishing apps |

Internal names use the prefix `redstar`: `redstar-comp`, `redstar-wm`, `redstar-online`, the `RedStarOS` project and
`~/.config/redstaros`.

## Licence

Copyright (C) 2026 The Crimson Banner OS contributors.

Crimson Banner OS is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
version (SPDX: `GPL-3.0-or-later`). It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
see [LICENSE](LICENSE) for the full text.

It uses Avalonia (MIT), LibVLC (LGPL-2.1-or-later, linked dynamically), wlroots (MIT), WebKitGTK (LGPL/BSD) and other
libraries under GPL-compatible licences; their own terms still apply to them. CD Player's and Video's look follows CDPlayer by Kizarov3 (MIT); its notice is in
`RedStarOS/Views/HiFi/NOTICE`.
