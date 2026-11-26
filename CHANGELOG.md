# Changes in Crimson Banner OS

Versions are YY.MM.N (README, "Versions"). `tools/release.sh` adds each release's section here.

## 26.11.5 (2026-11-26)

- Snap Assist: an app's picture when its window's class names a Crimson Banner app
- Desktop icons: tooltip is the file's name
- Desktop icons: the menu through the ContextRequested event (no override on Canvas)
- Desktop and taskbar, the Windows way: real desktop icons, snap previews in the accent, Snap Assist, Show desktop
- Blocks: Space drops the piece instead of pressing New game
- Gems: cut stones instead of flat shapes
- Screenshots: the Start menu at its own width (750), so its app list is pictured
- Icons: the plate's rim as an immutable brush made the usual way
- Icons redrawn for the deck look: every app a drawn picture on the graphite tile, the line icons on one grid
- Atlas: its screenshot for Software
- Atlas: tiles placed on whole pixels (no seams between them), and a map opens whole
- Atlas: ways summed in whole micro-degrees, so a closed ring (the island's coast) reads back closed
- Atlas: maps of the world that work offline, from the Mapsforge project's maps of OpenStreetMap

## 26.11.4 (2026-11-25)

- Start menu: our own apps (Planner, People's Archive…) with the shell's apps, not under Linux Apps
- People's Archive: Project Gutenberg from its catalogue file and its own mirror, as it asks programs to; refusals in plain words
- People's Archive: its screenshot for Software, and the roadmap item done
- People's Archive: its PKGBUILD's error message without an apostrophe (it opened a quote in ${:?}); check.sh checks every app's PKGBUILD
- People's Archive: the reading room's bar in three columns (no more overlap); the shelf's search on the left
- People's Archive: a plain separator between table cells; the line between results follows the theme
- People's Archive: free books from public-domain archives, a shelf and a reading room
- Planner: its screenshot for Software, and the roadmap item done
- Planner: the Plan table puts what to decide first (signal, need, proposal, change, decision), the working after
- Planner: a capacity trim lands exactly on capacity; pages fit the window; only a limited product shows a shortfall
- Planner: the first of our own apps, an advisory Economic Planning Algorithm after The Economic Structure
- ISOs over 2 GiB go up to the release in parts, with scripts that check and join them
- ISO 1.42 GiB (from 1.76): the roadmap item done; the live test's log line reads properly
- ISO: UEFI through GRUB and no kms in the live initramfs, so the boot files are on it once and smaller; boot tests through the ISO's own menus
- Roadmap: firmware and modules are about half of the live system, not two-thirds
- ISO size report: tidy up without failing; the roadmap has what it found
- ISO builds print what takes the room: files, boot files, the initramfs, the live system by folder and by package
- A testing channel: builds of main for those who choose them
- Roadmap: the upgrade test put aside
- Roadmap: the install test is done
- Install test: recognise the driver's verdict through the console's colour codes; Check: the update channel's version taken before publishing
- Install test: done when Calamares's log says so; Check: restore the committed key after the apps test
- Install test: watch for the installer's failure while it runs, print its log, picture every two minutes
- A roadmap, and an install test: the ISO installed onto an empty disk by CI, checked, and started

## 26.11.3 (2026-11-25)

The first release with an installer, updates for installed systems, and a new look throughout.

- **Installer ISO.** A live, offline installer for Crimson Banner OS (archiso and Calamares): it boots the whole
  desktop, opens the installer, and copies the live system to the disk. It's attached to the release. The libre and
  Intel Mac editions are put aside for now; their recipes are in `iso/`.
- **Updates for installed systems.** Each release's packages are published as the signed `[crimson-banner]` pacman
  repository (`tools/publish-os.sh`, signed with the project's key `19E7 1BAC DC32 A511 D657 F1FA 9EC9 146D 98BE
  ECA0` on the owner's computer). Systems installed from this ISO turn it on themselves, so Settings → Software
  Updates brings each release with the rest of the system. `crimson-repo` turns it on or off by hand.
- **A new look for every window**, after CD Player's deck: a graphite backdrop with panels lifted off it, rounder
  controls, small spaced capitals for labels, lists with the chosen row in a soft wash of the accent, and flyouts and
  menus as panels, in the light and dark themes. Software is rebuilt in it (a red-digit display of what's there,
  cards that fill the window, a program's page as panels), and Settings has a sidebar like Software's.
- **Dark with the Crimson accent** is now the default for new accounts, the live session and the login screen.
- **New branding**, all drawn from source in `branding/`: one mark (a crimson swallowtail banner with the star) on the
  app icon, the Start button, the start-up splash, Welcome and the installer; four 4K wallpapers (Banner, the new
  default; Folds; Deck; and Daylight, the default in the light theme); the login screen on a blurred Banner; and
  Studio's own icons. The earlier wallpapers are still in Settings → Wallpaper.
- **Virtual machines.** VMware's display adapter (and VirtualBox's, QXL, Bochs…) now draws in software, as safe
  graphics did: the login screen comes up first time, and the Wayland and X11 sessions no longer crash or go black.
  Where redstar-comp has no display device at all, the login screen and the Wayland session use X11 instead.
- **Installed systems are named Crimson Banner OS**, not Arch Linux, at the text login and in `/etc/os-release`.
- **Fixed: a crash in text shaping** (Skia's and HarfBuzz's native libraries now keep their own symbols).

## 26.11.2 (2026-11-22)

A second fix release, with a new app.

- **CD Player**, a music player built like a hi-fi CD deck, after [CDPlayer](https://github.com/Kizarov3/CDPlayer)
  by Kizarov3 (MIT). The album cover spins on a disc in its jewel case. A deck display shows the track, the time and
  the PLAY / PAUSE / SHUFFLE / REPEAT lights. The shelf holds the albums in your Music folder, and audio CDs play from
  the drive. It runs natively in the shell, played through libVLC, with no Electron and no streaming accounts.
  Music files now open in it, and Default apps has its own Music entry.
- **Video** has the same deck look: a faceplate with the deck display, hi-fi transport keys, and the playlist as a
  booklet.
- **Fixed: system updates on Arch** failed with "url contained $arch, which is not set". Settings → Software Updates
  now lists updates with `checkupdates` and installs them with `pacman -Syu`, behind the password prompt.
- **Fixed: programs installed from Software** didn't appear in the Start menu until the next sign-in.
- **Fixed: Software's pages were empty** in every tab.
- **The repository is KaiserCS/CrimsonBannerOS**, and the README is rewritten: shorter, and up to date.
- **Settings → Diagnostics → Record pointer activity**, for tracking down highlights that blink.

## 26.11.1 (2026-11-21)

The first fix release: a menu bug in the Wayland session, and three additions.

- **Software**, a new app for installing more programs. It covers Crimson Banner OS's own apps, Flatpaks from
  Flathub and AppImages from AppImageHub, plus what's installed and its updates, all in one place. It has an
  Explore page, categories, one search across everything, and a page for each program with screenshots. Only free
  software is shown.
- **Our own apps, published without an OS release**: `tools/publish-app.sh` builds and signs each app in `apps/`
  into the `[crimson-banner-apps]` pacman repository and a signed catalogue on GitHub Releases. Software checks
  that signature against the project's key before showing anything.
- **Crimson Banner OS's own login screen**, on greetd, to use instead of SDDM. It shares the lock screen's look,
  with people to pick from, the desktop to start, and sleep, restart and shut down. `crimson-greeter-setup preview`
  shows it in a window; `sudo crimson-greeter-setup enable` switches to it (`disable` goes back). If it ever fails to
  start, the text login takes over.
- **Fixed (Wayland session)**: a context menu that reached past the bottom of its window had the window's border
  drawn through it, and its lower items couldn't be clicked. This affected every app with native Wayland menus.

## 26.11.0 (2026-11-21)

The first version in the new numbering (it follows 1.3.0), and the first since the change of name.

- **Crimson Banner OS**: the new name everywhere you see it. The apps lost their "Red Star" prefix and the browser
  is Crimson Online; internal names and settings folders stay as they were.
- **A distribution base**: Arch Linux. Five pacman packages, built from this tree and signed into our own pacman
  repository (`packaging/arch`, `tools/repo.sh`). `./install-arch.sh` builds and installs them in one command; run
  it again to update.
- **Versions**: one version for every part, worked out by `tools/version.sh`, with `--version` on every program, a
  release script and this changelog.
- **No Electron**: Crimson Online (GTK 4 + WebKitGTK) is the only browser engine. Video uses LibVLC.
- **Our own package feed**: every NuGet package at a locked version and hash, so the shell builds with no registry
  (`tools/packages.sh`).
- **Messenger** is a Matrix client (rooms, direct chats, pictures and files; encrypted rooms are marked and can't be
  written to yet).
- **New apps**: Blocks, Mahjongg (GNOME Mahjongg's tile sets), Gems, Screenshot, Screen Recorder and Voice Memos.
  Pong has difficulty levels.
- **Desktop plumbing**:
  - a notification server for other apps;
  - an administrator-password prompt (polkit), which works with polkit 126's socket-activated helper;
  - the lock screen can use your system password (PAM);
  - removable drives, and a Disks app;
  - idle, screen off and sleep;
  - startup apps.
- **New Settings pages**: Power, Date & time, Region & keyboard, Users, system updates (PackageKit), and
  Accessibility (screen reader and on-screen keyboard). Fonts, cursor and theme also apply to other apps (GTK and Qt).
- **redstar-comp** (Wayland):
  - cursor themes and keyboard layouts;
  - idle and screen-off, with idle inhibit;
  - Super+Alt+S starts the screen reader;
  - X11 windows map reliably.
- **Smaller things**: the Start menu's category column is wider, so "All Crimson Banner apps" fits; the taskbar's
  hidden-icons arrow is centred.
