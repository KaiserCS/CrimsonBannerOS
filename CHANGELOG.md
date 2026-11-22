# Changes in Crimson Banner OS

Versions are YY.MM.N (README, "Versions"). `tools/release.sh` adds each release's section here.

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

2026-11-21)

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
