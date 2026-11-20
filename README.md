# Red Star OS (Project ANDROMEDA RSO 1.2) – Linux / AppImage port, v1.3

Avalonia 12 (12.1.6) / .NET 8 port of the original WinForms project, with a much more functional desktop.

> **Principles:** the project is governed by self-reliance, in the Marxist-Leninist sense: it holds its own base
> layers, keeps its work GPL, accounts for every outside dependency and its way out, and builds and runs without
> hosted services or telemetry. See [PRINCIPLES.md](PRINCIPLES.md).



## Desktop essentials
- **Search everything** (Super, or the Start button): apps, settings pages, recent documents, files in your home
  folder (indexed in the background; hidden and build folders skipped), quick sums ("12*7" → copy the result),
  and "Open site" / "Search the web" in Red Star Online.
- **Red Star Online (new: GTK 4 + WebKitGTK, `browser/`):** a native browser in its own window, so typing,
  scrolling and the clipboard go straight to the page. Tabs, an address bar that opens addresses or searches,
  bookmarks bar, history, downloads (Ctrl+J), find in page (Ctrl+F), zoom per site, print, developer tools (F12),
  permissions asked once per site, private windows (Ctrl+Shift+N), and the ad and tracker blocker: EasyList,
  EasyPrivacy and Peter Lowe's list converted into WebKit's own content blocker, refreshed every four days, plus
  WebKit's tracking prevention and no third-party cookies. It shares bookmarks, history and settings with the shell
  (`~/.config/redstaros/browser-data.json`). Build it with `browser/build.sh` (Arch/CachyOS:
  `pacman -S webkitgtk-6.0 gtk4 json-glib libsoup3`, and for sound and video in pages
  `gst-plugins-good gst-plugins-bad gst-libav`) and `install-session.sh` installs it; Red Star then uses it
  for every link. Not yet in it: reader mode, Save as PDF, picture-in-picture and imports from other browsers —
  Settings → Default apps → "Use the previous Red Star Online" switches back to the Electron one meanwhile.
- **Other programs' tray icons:** Discord, Steam, Qt programs, update notifiers… show their icons in the taskbar's
  tray (freedesktop StatusNotifierItem): left click opens the program (or its menu, for Steam-style icons), right
  click its menu (DBusMenu, with submenus and ticks), middle click its second action, the wheel scrolls. In its own
  session Red Star is the tray "watcher" programs register with; on KDE it joins KDE's. Red Star talks D-Bus with
  its own small client, no library. Old XEmbed-only tray icons (some Wine programs) aren't shown yet.
- **Global shortcuts (X11):** Super = Start/search, Alt+Tab / Alt+Shift+Tab = window switcher (every window,
  also non-Red Star programs), Super+←/→ snap, Super+↑ maximise, Super+↓ minimise, Super+D show desktop,
  Super+E files, Super+T or Ctrl+Alt+T terminal, Super+V clipboard history, Super+L lock, Ctrl+Alt+Delete the
  Lock / Log off / Restart / Shut down / Task Manager menu, and the keyboard's volume keys (scrolling over the tray's
  volume icon does the same; the level shows above the tray). As the login session, Log off goes back to the login
  screen and Shut down / Restart turn off or restart the computer (systemd-logind); as an app on another desktop they
  lock, close or restart Red Star OS.
  Red Star has a single workspace (workspace switching was removed).
- **Other programs on the taskbar**: every window of another program (Firefox, GIMP, LibreOffice, a Flatpak…)
  gets a taskbar button that works like a Red Star app's — click to switch to it, click again to minimise, the
  running/active indicator, and right-click for New window, Restore, Minimize, Snap Left/Right, Fill Screen,
  Pin to taskbar and Close. It shows the program's own icon (from its window, or its icon theme when pinned) and is
  matched to its installed app (StartupWMClass, .desktop file name or program name), so installed programs can be
  pinned from the Start menu or the taskbar and launched from there. Alt+Tab shows the same icons. Updates are
  event-driven (the window manager's client list, titles, state and icons), dialogs and tool windows are left out.
- **Clipboard history** (tray icon or Super+V): last 30 copied texts, pin, remove, pause, clear — memory only.
- **Notification buttons**: "Open" / "Show in folder" on downloads, saved PDFs and camera pictures.
- **Share** menu (Online, File Manager, Image Viewer, camera notifications): Messenger, Writer, e-mail, copy.
- File Manager: "Open Terminal here".

## Hardware settings (Settings)
- **Wi-Fi & Network** (nmcli): Wi-Fi on/off, airplane mode, scan, connect (asks for the password), disconnect,
  saved connections with Forget, network interfaces.
- **Sound** (pactl, PulseAudio or PipeWire): output and input device, volume, mute, test sound.
- **Bluetooth** (bluetoothctl): on/off, search, pair, connect, disconnect, remove.
- **Displays** (xrandr): resolution, rotation, main display, position / mirror, on/off for each screen; every change
  reverts after 15 seconds unless you keep it.
- **Printers** (CUPS): default printer, test page, print queue with cancel, add a printer (system-config-printer or the
  CUPS web page).
- **Default apps**: Red Star app, the system default or any installed program for links, documents, spreadsheets,
  PDFs, pictures and media; optionally applied system-wide with xdg-mime.
Every page says which tool to install when one is missing.
## Keyboard and mouse stay in Red Star OS

When Red Star OS is started on top of another desktop (GNOME, KDE, Xfce…), it now runs in **its own display**,
full screen, like a virtual machine — so Super, Alt+Tab, hot corners, panels and every other key and click go to
Red Star and not to the desktop underneath.

| Host | How | Needs |
|---|---|---|
| Wayland | rootful Xwayland with `-host-grab` (the compositor's shortcuts are inhibited, the pointer is confined) | Xwayland 23.1+ (GNOME asks once to allow it) |
| X11 | Xephyr; its keyboard/pointer grab is switched on automatically | Xephyr (`xserver-xephyr` / `xorg-x11-server-Xephyr` / `xorg-server-xephyr`) |

* Windows get **Red Star title bars** from Red Star's own window manager, `redstar-wm` (C, Xlib — `wm/`): minimise,
  maximise, close, double-click to maximise, drag to move (a maximised window restores under the pointer), edges and
  corners to resize, Alt+drag to move, Alt+right-drag to resize, Alt+F4 to close; full screen, snapping,
  dialogs centred on their window, the desktop always at the bottom and the taskbar's height kept free.
  It's also used in the Red Star session, so no other window manager is needed (set `REDSTAR_WM=openbox` to use
  openbox instead). The build compiles it when gcc and the X11 headers are present; a prebuilt x86_64 binary is
  included. Its behaviour tests run on Xvfb: `wm/test/run-tests.sh` — 47 window-management checks plus 10
  click-delivery checks made the way Avalonia receives input (XInput 2): every click and wheel step arrives exactly
  once, the first click on an unfocused window both focuses it and works, and the WM adds about 1 ms.
  Only unfocused windows have their clicks routed through the WM (click-to-focus); the focused window gets them
  straight from the X server. `REDSTAR_WM_DEBUG=1` logs clicks, focus changes and window requests to `~/.config/redstaros/session.log`.
  `wm/test/session_test.py` runs a real Chromium under redstar-wm with input injected through the X server (26 checks:
  clicks, first click on an unfocused window, hover without flicker, tooltips, drop-downs, typing, new windows, full
  screen, maximise and dragging through the app's own title bar). New windows that would land exactly on another
  are cascaded, and windows shown "without activation" (like the Alt+Tab panel) don't steal focus.
* **GPU acceleration** (Settings → Diagnostics → Graphics acceleration: Automatic / Always GPU / Software only).
  A display offers the GPU when its X server has DRI3 and the kernel a render node: Xwayland and the Red Star
  session do, Xephyr doesn't.
  - **Red Star Online** renders pages and decodes video on the GPU (VA-API — Intel, AMD, NVIDIA with
    nvidia-vaapi-driver) whenever the display allows it; after three GPU-process crashes in a day it uses the CPU.
  - **Red Star's windows** use the GPU when a compositor runs too; otherwise (Xephyr, or no picom) they use the
    software renderer, because GPU-drawn popups flicker without a compositor. `REDSTAR_RENDER=gpu|software` overrides.
  - The **compositor** (picom, else xcompmgr) runs on the GPU (`--backend glx`) when it can, otherwise `xrender`.
    Without any compositor, popups, menus, tooltips and the Alt+Tab panel are drawn square and opaque.
    picom uses your own `picom.conf` when you have one (your transparency, fades and other effects), otherwise Red
    Star's (`~/.config/redstaros/picom.conf`: fades, translucent windows allowed, soft shadows). **Window shadows**
    are on (picom `-c`, even if your own picom.conf turns them off; Settings → Desktop & Taskbar → "Soft shadows under
    windows" switches them off at the next start). redstar-wm marks the frames that should have one with
    `_REDSTAR_SHADOW` — framed windows that aren't maximised or full screen — and picom shadows only those, so
    tooltips, menus, popups, the desktop, the taskbar and see-through undecorated windows never get a shadow box.
    With another window manager the shadows go by window type instead. xcompmgr draws no shadows (it can't leave out
    tooltips). Accessibility → Reduce motion turns fades off. With a compositor running, popups, menus and the Alt+Tab panel are rounded and translucent again.
  - Diagnostics shows what is in use: GPU access, compositor, renderer of Red Star's windows, and the browser's
    rendering / video-decoding status with the GPU's name. `webengine/test/gpu.test.js` checks the engine's switches.
* **Smooth video in Red Star Online**: the web engine writes only the rows of the page that changed (a playing
  video is a band, not the whole page) into shared memory; Red Star copies just those rows from a memory mapping on
  the engine's connection thread — not the UI thread — and acknowledges right away, so the next frame is painted
  while this one is drawn.
* **Ctrl+Shift** releases the keyboard and mouse (to reach the other desktop), and captures them again.
* **Shut down** Red Star OS to return to the desktop underneath.
* Turn it off in **Settings → Desktop & Taskbar** (it shows whether isolation is active), or start with
  `--no-isolate` / `REDSTAR_NO_ISOLATE=1`. As a full login session (`redstaros-session`) nothing is nested —
  Red Star already owns the keyboard there.
* If neither server is installed, Red Star starts as before and a notification says what to install.
  Details are logged to `~/.config/redstaros/session.log`.
* The clipboard of the desktop underneath is separate from Red Star's while isolated.

## Wayland compositor: redstar-comp (experimental, phase 2)

`compositor/redstar-comp.c` is Red Star's own Wayland compositor, built on wlroots 0.20. It takes the place of
redstar-wm **and** picom: the same title bars (red star, title, minimise / maximise / close, double-click to
maximise), 3 px borders, resizing from the edges, soft shadows, Alt+drag / Alt+right-drag, drag to the top edge to
maximise and to the sides to snap, Alt+F4. Red Star itself is unchanged: it still talks X11, through the Xwayland
redstar-comp runs, and redstar-comp keeps what the shell reads — `_NET_WORKAREA` minus the taskbar,
`_NET_FRAME_EXTENTS`, `_NET_MOVERESIZE_WINDOW` (Super+arrow snapping), `_NET_CLOSE_WINDOW`, the client list and the
compositing-manager selection. Native Wayland apps also work: they get a Red Star title bar unless they draw
their own (Chrome's tab strip, apps that ask for client-side decorations), so there's never a second set of buttons.
`REDSTAR_FORCE_FRAMES=1` frames every app anyway.

* **Build:** `sudo pacman -S --needed wlroots0.20 meson ninja xorg-xwayland xcb-util-wm` then `compositor/build.sh`.
  `dotnet build` also builds it when wlroots 0.20 and meson are there, and copies `redstar-comp` next to RedStarOS.
* **Use it:** Settings → Desktop & Taskbar → *Run Red Star OS in its own Wayland compositor*, or start with
  `REDSTAR_WAYLAND=1`. On another desktop it opens full screen, like keyboard isolation: on Wayland it asks the
  desktop to pass Super, Alt+Tab and its other shortcuts through (keyboard-shortcuts-inhibit; KDE and GNOME may ask
  once), on X11 it asks the window manager for full screen. `REDSTAR_COMP_WINDOWED=1` (or `redstar-comp -w`) gives an
  ordinary window instead. For the full session, build redstar-comp (`compositor/build.sh`) and run
  `sudo ./install-session.sh`: it adds **Red Star OS (Wayland)** to the login screen (on KDE, SDDM's session menu at
  the bottom left) next to the X11 session. Without redstar-comp built it leaves the Wayland entry out.
* Ctrl+Alt+Backspace quits it; Ctrl+Alt+F1…F12 switch consoles on the real hardware. Messages go to
  `~/.config/redstaros/session.log`.
* **Shell integration (phase 3):** redstar-comp and the shell talk over a Unix socket (`$REDSTAR_COMP_SOCKET`; the
  line protocol is described in `redstar-comp.c`). Native Wayland windows get taskbar buttons (icon from their
  app id's desktop entry) and take part in Alt+Tab, minimise / maximise / snap / close from the taskbar and Super+arrows
  work on them, and Alt+Tab follows the compositor's most-recently-used order for every window. Super, Alt+Tab,
  Super+key and Ctrl+Alt+T are taken by the compositor and handed to the shell, so they work whichever window has
  the keyboard (X11 or Wayland). Other programs' windows reopen where they were closed
  (`~/.config/redstaros/window-positions`), and new windows fade in (off with Reduce motion).
* **Displays, screenshots, screen sharing (phase 4, first part):** inside redstar-comp, Settings → Displays sets
  resolution, refresh rate, scale (100–300%), rotation and arrangement through the compositor (15-second undo as
  before), and what you keep is used again at the next start (`~/.config/redstaros/outputs`). Standard Wayland tools
  work too: wlr-randr and kanshi (output management), grim (screencopy), wl-clipboard (data control).
  **Print** saves a screenshot of every screen and **Shift+Print** of the active window to `~/Pictures/Screenshots`
  (grim inside redstar-comp; scrot, maim or ImageMagick on X11). Other apps' screenshots and screen sharing (browsers,
  OBS, video calls) go through xdg-desktop-portal-wlr: Red Star writes `~/.config/xdg-desktop-portal/redstar-portals.conf`
  for it, and in the Red Star (Wayland) session points the session's services at the compositor. Install them with
  `sudo pacman -S --needed grim xdg-desktop-portal-wlr` (Debian/Ubuntu: `grim xdg-desktop-portal-wlr`).
  Programs inside redstar-comp run natively on Wayland (System Settings → "Run other programs natively on Wayland",
  on by default), since OBS's "Screen Capture (PipeWire)" and Discord's screen share only work that way. Red Star
  also writes `~/.config/xdg-desktop-portal-wlr/config` (when you have none) with `chooser_type=none`, because the
  usual screen pickers (slurp, wofi) need layer-shell, which redstar-comp doesn't have yet.
  Apps can now also hand GPU buffers to the compositor (linux-dmabuf), so Xwayland draws Red Star's windows on the GPU.
* **Red Star natively on Wayland (phase 4, second part, experimental):** Settings → Desktop & Taskbar → *Draw Red
  Star's own windows natively on Wayland* (or `REDSTAR_NATIVE_WAYLAND=1`; `0` forces Xwayland). Inside redstar-comp
  the shell then uses Avalonia's Wayland backend (Avalonia.Wayland) instead of Xwayland. Plain Wayland has no window
  positions or desktop/dock window types, so the shell tells redstar-comp over its connection which window is the
  desktop and how tall the taskbar is (`role` lines); redstar-comp keeps the desktop at the bottom and full screen,
  reserves the taskbar's space, and draws the desktop's menus (Start menu, tray panels) above other windows. Apps
  are brought to the front through xdg-activation. Each of Red Star's windows is named to redstar-comp once with a token (an
  invisible marker title for a moment, never shown), so the taskbar, Alt+Tab and Super+arrows tell its windows apart
  even when titles repeat. Avalonia doesn't fall back to X11 by itself, so Red Star does: a
  start that fails is redone through Xwayland at once, and a crash before the desktop has been up for 8 seconds makes
  the next start use Xwayland; a notification says which. Known differences from Xwayland: Red Star's own windows open
  centred instead of where they were last (Wayland apps can't place themselves), and other programs are unaffected
  (X11 apps still go through Xwayland).
* **Tests:** `compositor/test/run-tests.sh` runs it on wlroots' virtual screen (no monitor or GPU) with two test
  clients: an X11 one that checks frames, placement, the work area, snapping, maximise/restore, minimise, close and
  the desktop window, and a native Wayland one checked through the shell's connection (listed with its app id and
  title, the taskbar's commands, leaving the list, remembered position, display settings and the capture, display and
  clipboard protocols other tools need), and a third that types and clicks with a virtual keyboard and mouse
  (the protocols wtype and ydotool-style tools use, which redstar-comp now offers): Alt+Tab, Super, Super+arrows,
  Ctrl+Alt+T, Print and Shift+Print, shortcuts left alone until the shell asks for them, menus closing on a click
  outside and staying open on a click inside, and the title bar's double-click, maximise and close buttons.
  The C# tests cover the shell's side.
  The same client passes 16 of its 18 checks against redstar-wm (it has no WM_S0 owner or compositor), so the two
  behave the same where it matters.

## First run, diagnostics and updates

* **Welcome** (first sign-in; Settings → Appearance → “Show the welcome again”): theme and accent with live previews,
  wallpaper, text size / high contrast / reduced motion / system sounds, importing from other browsers, and a page of
  the shortcuts worth knowing.
* **Import from other browsers** (welcome, or Red Star Online → Settings): bookmarks and history from Firefox
  (incl. Snap/Flatpak, LibreWolf) and Chrome, Chromium, Brave, Edge, Vivaldi, Opera. Bookmarks work everywhere
  (Firefox's `.jsonlz4` backups are decoded natively); history needs the `sqlite3` program. Databases are copied
  first, so the other browser can stay open. Nothing is duplicated.
* **Diagnostics** (Settings → Diagnostics): system facts, the last lines of `crash.log` and `browser.log`, “Export
  diagnostics…” (a zip on the Desktop to attach to a bug report), copy facts, open the logs folder, clear logs.
  Unexpected errors anywhere in the shell are now caught, written to `~/.config/redstaros/crash.log`, and shown as
  a notification with a “Details” button — the session keeps running instead of closing.
* **Software Updates** is a real page now: check automatically once a day (on by default), “Check now”, last-checked
  time, un-skip a skipped version. A new release shows a notification with “Update…” and “Skip this version”.

## Files (redesigned)

* **Toolbar**: back / forward / up, a clickable **breadcrumb** path (click an empty spot or Ctrl+L to type a path,
  `~` works), **search** (filters as you type; Enter also searches the subfolders, up to 500 hits, with "Show in its
  folder"), **New** (folder, text document), **list / grid** view, and a ⋯ menu (show hidden files, sort by, pin this
  folder, Open Terminal here, Properties).
* **Sidebar**: Places (Home, Desktop, Documents, Downloads, Pictures, Music, Videos), **Pinned** folders (right-click a
  folder → Pin to sidebar), **Devices** (mounted drives with a free-space bar) and **Trash** with its item count.
  Drop files on any place or breadcrumb to move them there (Ctrl: copy).
* **List view** with sortable Name / Kind / Size / Modified columns (friendly dates: "Today, 09:14") and coloured
  vector icons per kind; **grid view** with large icons and **picture thumbnails** (made in the background, cached).
* **Trash**: a banner with **Restore selected** (back to where each item was; a free name if it's taken) and **Empty
  Trash**; Properties shows where an item was deleted from and when.
* **Properties** (Alt+Enter): kind, size (folders counted in the background), location, dates, permissions.
* **Right-click**: Open, Open in new window, Open Terminal here, Share, Show in its folder (search results), Cut, Copy,
  Paste into folder, Duplicate, Rename, Pin to sidebar, Copy path, Move to Trash, Properties; on empty space: New
  folder, New text document, Paste, Select all.
* **Tabs** (Ctrl+T new, Ctrl+W close, Ctrl+Tab / Ctrl+PageUp/PageDown switch; middle-click a folder, Ctrl+Enter or
  right-click → Open in new tab): the same tab strip as Red Star Online and Terminal, shown from two tabs on; every tab
  keeps its own back/forward history.
* **Copying and moving** run in the background with a progress panel (name, bytes, item n of m) and **Cancel** — a
  cancelled copy leaves no half-copied files. Moves on the same drive are instant renames. When names already exist
  you're asked once: **Replace** (the old item goes to the Trash, so it can come back), **Keep both** ("name (2)") or
  **Skip**.
* **Undo** (Ctrl+Z, or the Undo button in the status bar): moves move back, copies and new items go to the Trash,
  trashed items are restored, renames are reverted — the last 30 steps.
* **Space: preview** without opening an app — pictures (full size, with their dimensions), text and source files,
  folders (item count, size, first names); ↑ ↓ moves to the next item and the preview follows, Enter opens, Space or
  Esc closes.
* The open folder is **watched**: files added, removed or changed elsewhere appear without refreshing. Copying and
  moving run in the background. The status bar shows the selection's size and the drive's free space.
* Keys: Ctrl+C/X/V/A, Delete (Shift+Delete: permanently), F2, F5, Enter, Alt+Enter, Backspace / Alt+↑ (up),
  Alt+←/→, Ctrl+L, Ctrl+F, Ctrl+H (hidden files), Ctrl+1 / Ctrl+2 (list / grid), Ctrl+Shift+N, Esc, and typing a
  few letters jumps to a name. F9: details panel.
* **Archives** (.zip, .tar, .tar.gz/.xz/.bz2/.zst, .7z, .rar, .jar, .cbz): double-click opens one **like a folder**
  (unpacked once into `~/.cache/redstaros/archives`, reused until the archive changes; a banner offers **Extract
  all** / **Extract to…**). Right-click → **Extract here** (one top-level item lands directly; several get a folder
  named after the archive), **Extract to…**, **Compress → name.zip / name.tar.gz**. All of it shows progress, can be
  cancelled and undone. zip is built in; tar archives use `tar`; 7z needs p7zip or bsdtar; rar needs unrar, 7-Zip or
  bsdtar (a message says what to install).
* **Open with** (right-click a file): its Red Star app, the installed programs that say they open that kind of file
  (from their `.desktop` MimeType and the system's shared MIME database), and **Other application…** — a searchable
  list of every program with **Always use this app for …** (the same choice as Settings → Default apps).
* **Drag and drop**: drag items to another folder, tab, breadcrumb, sidebar place, Red Star window (Writer, Image
  Viewer, Video…), the Trash, or **Starred**; dropping on a folder in the list puts them inside it. Like other file
  managers it moves on the same drive and copies to another one (Ctrl: copy, Shift: move); files dropped from other
  programs are accepted. **Rubber-band selection**: drag on empty space (Ctrl adds to the selection; the list scrolls
  at the edges). Copy / Cut also put the files on the system clipboard in the GNOME / KDE format, and files copied in
  another file manager can be pasted here.
* **Network and cloud**: ⋯ → **Connect to server…** (smb://, sftp://, ftp://, dav(s)://; GVfs's `gio mount`, the
  password goes to it on stdin and isn't stored; without GVfs, sftp:// uses sshfs) — connected servers are listed under
  **Network** with a disconnect button. **Cloud** lists your **rclone** remotes (set up once with `rclone config`):
  click to mount one in `~/Cloud/<name>` (`--vfs-cache-mode writes`). Removable drives under Devices have an **eject**
  button (unmounts and powers off a USB stick).
* **Recent** (files opened in Red Star and in other apps, from `recently-used.xbel`) and **Starred** (right-click →
  Star, or drop on Starred in the sidebar; stars follow renames and moves; starred items show a ★).
* **Details panel** (F9 or the panel button): a larger preview, kind, size (folders counted), picture dimensions,
  location, dates, MIME type, which program opens it, a Star toggle; for several items the total size and kinds; with
  nothing selected, the folder itself.
* **Open / Save dialogs in every app** (Writer, Sheets, Slides, Paint, Image Viewer, Video, Studio, the browser,
  wallpaper and account pictures) are Red Star's own, built like Files: the same sidebar (Home, Recent, Starred,
  the standard folders, your pinned folders, drives, connected servers and cloud drives), path bar (click it or
  Ctrl+L to type a location, `~` works), icons, sorting, search, hidden-file setting (Ctrl+H) and New folder. They
  open in the folder that was last open in Files or in another dialog, so Files and the apps always agree on where
  you are; a Save dialog adds the right extension for the chosen type and asks before replacing a file.
* **Batch rename** (select several → F2, or right-click → Rename N items…): find and replace (plain or regular
  expression, match case), add text at the start or end, numbering (`Holiday ###` → Holiday 001…, `*` = old name,
  start and step), or change the case; extensions are kept unless you tick otherwise. A live preview shows every new
  name and marks clashes in red; swapping names works, and it's one Undo step.

## Accessibility and identity

* **Settings → Accessibility**: size of text and controls (100–200 %, used from the next sign-in, Electron apps
  included), high contrast (black-on-white / white-on-black text and borders, thick yellow or blue focus outline;
  follows Light/Dark), large mouse pointer, reduce motion (no splash fade, no blinking terminal cursor, web pages get
  `prefers-reduced-motion`), system sounds with previews, keyboard notes and a link to all shortcuts.
* **Keyboard focus** is visible everywhere: whatever Tab reaches gets an outline in the accent colour.
* **Names for assistive technology**: icon-only buttons take their tooltip (or icon) as their accessible name.
  On Linux, how much Orca can read depends on Avalonia's accessibility support, which is still limited.
* **Wallpapers → Find online**: searches the Wallhaven and Openverse (Creative Commons) indexes — no account
  needed, safe-for-work results only. Filters: at least your screen's resolution (or Full HD / QHD / 4K / any),
  sort (relevance, top, latest, random, most viewed), categories and a dominant colour. Results show their real
  resolution and whether they're sharp on your screen; scrolling loads more. “Set as wallpaper” downloads the
  original at full size to `~/Pictures/Wallpapers` (Openverse credits go to `credits.txt` there), so it works
  offline and appears under **My wallpapers**, which also browses any folder and runs the slideshow.
  Desktop right-click → “Find Wallpapers Online…”.
* **Red Star collection** (My wallpapers → “Red Star collection (4K)”): five wallpapers — Dawn, Night, Rays, Snow,
  Ribbon — at 3840 × 2160, rendered with supersampled edges and no grain. Large wallpapers are scaled down once, in
  high quality, to the screen's size, so they're sharp on 1080p and 1440p screens too.
* **Start-up splash** over the desktop while it loads, and **system sounds** (start-up, sign-in, notification,
  error — synthesised, off until you turn them on in the welcome or in Accessibility).

## Checking a build
`./check.sh` builds the shell and runs the automated checks: every window is opened and closed headlessly
(catches XAML that compiles but crashes at runtime), the terminal emulator and a real pty, browser data,
and the ad blocker. `./check.sh --full` adds the web-engine tests (needs Playwright); `./check.sh --screenshots`
also draws every window (Skia) into `screenshots/` — open `screenshots/index.html` to see them all side by side.

**On GitHub** every push and pull request runs `.github/workflows/check.yml`, four jobs in parallel:
* **Build and C# tests** — the build, every C# test and a screenshot of every window (including the Open / Save
  dialogs, batch rename and Files with a folder open). The run's summary page lists the result and any failures
  with their messages; the **screenshots** artifact holds the pictures (open `index.html`).
* **Window manager** — redstar-wm must compile with no warnings (`-Wall -Wextra -Werror`), then its behaviour and
  click tests run on Xvfb, then a real Chromium session under it (**session-screenshots** artifact).
* **Web engine** — ad blocker, GPU switches and the engine tests with Playwright's Chromium.
* **Wayland compositor** — in an Arch Linux container (for wlroots 0.20): redstar-comp must compile with
  warnings as errors, then its behaviour tests run headless through Xwayland.

A red mark means a change broke the build, a test or a window; the failing step's log says which.

## Build

```bash
sudo pacman -S dotnet-sdk-8.0 nodejs npm   # or: sudo apt install dotnet-sdk-8.0 nodejs npm
./build-appimage.sh                  # -> dist/RedStarOS-x86_64.AppImage
./build-appimage.sh arm64            # on an arm64 host
SKIP_ELECTRON=1 ./build-appimage.sh  # smaller build without the Electron browser
(cd browser && npm run setup)        # once, downloads Electron for the browser when using the line below
dotnet run --project RedStarOS       # development run, no AppImage
```

Optional self-update support: `GH_REPO=user/repo ./build-appimage.sh` embeds update information
(also install `zsync` so the `.zsync` file is produced). `.github/workflows/appimage.yml` does this
in CI and attaches both files to a GitHub release when you push a `v*` tag.

Login-screen sessions: `sudo ./install-session.sh` (it finds what you built: the newest AppImage in `dist/`, else the
`dotnet build` output; or give it an AppImage, the RedStarOS program or its folder). It adds **Red Star OS** (X11,
with redstar-wm) and, when redstar-comp is built, **Red Star OS (Wayland)**. `sudo ./uninstall-session.sh` takes them
off the login screen again (`--dry-run` lists what it would remove; your settings and the program itself stay).

## Red Star Online – native browser
Tabs, address bar and menus are native Avalonia; pages are rendered by a hidden Electron/Chromium "web engine" (`webengine/`) and drawn inline, so it works on X11 and Wayland and behaves like the other apps.
- **Core:** bookmarks (☆ / Ctrl+D, bookmarks bar, manager with rename and HTML export), history page (search, delete, clear by time range), new-tab page (search + bookmarks and most-visited tiles), session restore, address-bar suggestions from history and bookmarks, padlock / site-info panel, search-engine choice (DuckDuckGo, Google, Bing, Brave, Startpage, Wikipedia), tab favicons, per-site zoom, remembered per-site permissions.
- **Privacy and safety:** private windows (Ctrl+Shift+N; separate in-memory session, no history, wiped when the window closes), ad and tracker blocker using EasyList, EasyPrivacy and Peter Lowe's list (downloaded on first start, refreshed every 4 days, built-in starter list offline; blocks requests and hides leftover ad boxes; strips YouTube video ads) with a per-site switch and blocked count, Clear browsing data dialog (Ctrl+Shift+Del), certificate-error page with "Proceed anyway", your own blocked-sites list (Settings).
- **Shell integration:** tray icon with menu (show, new tab, private window, downloads, close) and active-download badge, "Open page in Writer" (article text and images become a Writer document), "Send link to Messenger" (copies the link and opens Messenger), downloads panel with Show in folder in the native File Manager, dark/light follows the shell theme (`prefers-color-scheme`).
- **Extras:** reader mode, Save as PDF / Print (Ctrl+S / Ctrl+P, uses `lp` when present), picture-in-picture, mute-tab button and audio indicator, developer tools (F12), keyboard-shortcuts page, Chromium's built-in PDF viewer.
- Internal pages: `redstar://newtab`, `history`, `bookmarks`, `settings`, `shortcuts`. Data lives in `browser-data.json` in the config folder.
- Picture-in-picture is native: the video fills its tab and a small always-on-top window shows it (play/pause, back to tab), so it works on Wayland too. HTTP sign-in prompts, pop-up windows that keep `window.opener` (sign-in with Google/Microsoft), and page fullscreen are supported.
- "Send link to Messenger" posts the page into the Messenger chat (double-click a message with a link to open it). "Open page in Writer" picks the article, keeps its pictures and adds a source line.
- The hidden engine runs on X11 (Xwayland on a Wayland desktop) whenever an X server is there: on Wayland Chromium
  puts its hidden page windows on screen. `REDSTAR_WEB_OZONE=wayland|x11` overrides.
- Diagnostics: `~/.config/redstaros/browser.log`. Engine tests: `webengine/test/` (see its README).
- Human verification (Cloudflare, hCaptcha, reCAPTCHA, GeeTest, Arkose, DataDome): those checks are never blocked by
  the ad blocker, and when one appears Red Star offers **Open in a window** (also in the page's right-click menu): the
  page opens in a real Chromium window sharing the tab's cookies, where sliders and puzzles get input directly;
  closing that window reloads the tab, already verified.
- Known gaps: no extensions, no password manager, `<select>` drop-downs and IME input are limited in offscreen mode, no GPU compositing (heavy WebGL / 4K video is slower), DRM video is unsupported. If the engine can't start, the classic Electron browser opens instead.

## Fit and finish round (apps and polish)
- **New apps:** *Task Manager* (CPU / memory / uptime, filter, sort, End task), *Clock* (stopwatch with laps, countdown timer that notifies when done; keep its window open), *Red Star Paint* (pen, eraser, colours, size, undo, save PNG).
- **Settings** is one resizable window with a sidebar. Appearance, Desktop & Taskbar (labels, 12/24 h clock, Do Not Disturb, pinned apps) and Games are pages; Network, Account, Wallpapers and Updates open their existing windows.
- **Login** has a frosted card, big clock, shake on a wrong password, Enter to sign in (also when no password is set), Restart / Shut down, and hides Electron windows while locked.
- **Notes**: search, pinning, previews and dates, "Saving... / Saved", word count, Ctrl+N, delete with Undo.
- **Camera**: self-timer (3 / 10 s), recording timer badge, red recording icon in the app tray (stop from there), strip of recent photos that open in Image Viewer.
- **Ping Pong**: resizable playfield, Solo and Versus-computer (first to 7) modes, keyboard + mouse, pause (P/Space, auto-pause on focus loss), saved scores.
- **Red Star Terminal** (native, `Views/TerminalWindow`, `Views/TerminalView`, `Services/TermEmulator`, `Services/Pty`): the shell runs on a real pseudo-terminal opened through libc and started with `setsid -c` (python3 as a fallback), so job control, Ctrl+C/Z and resizing work; no Electron, xterm.js or native module. The emulator is xterm-compatible: 16/256/true colour, bold/italic/underline/strike, alternate screen (vim, htop, less, mc), mouse reporting (incl. SGR), bracketed paste, focus events, cursor shapes, OSC titles, OSC 7 folders, OSC 8 links, OSC 52 copy, wide characters and emoji, 10 000 lines of scrollback that re-wrap when the window is resized. Tabs (Ctrl+Shift+T / W, Ctrl+PgUp/PgDn, Alt+1-9), copy / paste (Ctrl+Shift+C / V, Ctrl/Shift+Insert, middle-click), word / line selection, find (Ctrl+Shift+F), Ctrl+click opens links in Red Star Online, clear scrollback (Ctrl+Shift+K), zoom (Ctrl+= / - / 0, remembered), full screen (F11), follows the theme and accent. New tabs open in the current tab's folder; tab titles show the folder or the running program; closing a tab or the window asks first when a program is still running. "Open Terminal" on a folder and apps with `Terminal=true` open in a new tab. Split panes: Ctrl+Shift+E (right) / Ctrl+Shift+O (down), drag the divider to resize, Alt+Shift+arrows to move between panes, Ctrl+Shift+Z maximises / restores a pane, Ctrl+Shift+W closes a pane (and the tab with its last pane). Profiles (menu → Profiles and colours…, or ▾ next to ＋ to open a tab with one): font and size, colour scheme (Red Star — follows the desktop theme — plus Red Star Dark / Light, Solarized Dark / Light, Tango, Dracula, Gruvbox, Nord, Green on Black), cursor shape and blinking, a command instead of the shell, start folder, scrollback length, visual bell, bold-as-bright; edits apply to open terminals at once (saved in `terminal.json`). Input methods (fcitx / IBus) show the text being composed at the cursor and place their candidate window there. The old Electron version in `terminal/` is no longer used.

## Shell redesign: one design system, one taskbar, two trays

**Taskbar** (`Views/Taskbar.axaml`) – a single dark-glass strip:
`[Start] [pinned + running apps] [app tray] | [system tray] [clock]`
- Pinned launchers (default: File Manager, Online, Video) and running windows share one row. A pinned app that is running *is* the same button, with a running dot (a longer bar when focused). Right-click: Restore / Minimize / Snap / **Pin or Unpin** / Close. Start menu apps can be pinned with right-click too.
- **Electron apps are on the taskbar.** Red Star Online and Red Star Video get a button the moment they are launched ("Red Star Video…" while starting), show their window title, focus/minimise on click, and support Snap Left/Right/Fill and Close from the right-click menu, exactly like native windows. Launching an app that is already running just focuses it.
- With more than 5 windows the buttons collapse to icons (titles move to tooltips).
- The old fixed Folder / Power / Wallpaper / Calendar buttons are gone: Folder is a pinned app, Power is in Start and Quick Settings, wallpaper commands are in the desktop right-click menu and Quick Settings, Calendar opens from the clock.

**App tray** (new, left of the divider) – the notification area for background things. It always holds the **notification centre** bell (unread dot, history, Do Not Disturb, Clear all). Apps add their own icons: Red Star Video shows a ▶/⏸ icon with Play/Pause, Previous, Next, Show and Close while media is loaded. More than 4 icons spill into a ⌃ overflow flyout.

**System tray** – network / volume / battery in one button that opens **Quick Settings**: profile, network tile, dark-mode tile, Do Not Disturb tile, next wallpaper, volume slider with mute, battery, Log off / Restart / Shut down. The **clock** (HH:mm + date) opens a calendar panel with today's agenda.

**Design system** (`Styles/Shell.axaml`) – shared tokens and classes used by every window: `h1 / h2 / caption / eyebrow / brand` text, `card`, `primary / flat / tile / hubtile` buttons, theme-aware borders/hover colours, accent brushes driven by Settings → Appearance. `Services/AppIcons.cs` gives each app one glyph + colour used by the taskbar, Start menu, notifications, toasts and Settings. Hard-coded colours/sizes in the app windows were replaced, window titles now match the app names (e.g. "RSMessanger" → "Red Star Messenger").

**Electron apps follow the theme.** The shell passes the dark/light mode and accent colour to Online and Video and pushes changes live; Online was restyled (rounded address bar, load progress bar, light/dark), Video uses the accent colour.

### How the Electron integration works (`Services/ShellBridge.cs`, `shared/shell-bridge.js`)
The shell listens on a Unix socket (`$XDG_RUNTIME_DIR/redstaros-shell-<pid>.sock`, mode 0600) and starts Electron apps with `REDSTAR_SHELL_SOCKET` set. The app connects and speaks newline-delimited JSON: it reports its window (title, focused, minimised), tray icons and notifications; the shell sends focus / minimise / restore / close / bounds (snap) / theme / tray-menu messages. It works the same on X11 and Wayland because no window-manager tricks are used. Without the variable (plain `npm start`) the apps run stand-alone. To integrate another Electron app: copy the `loadBridge()` / `attach(win)` lines from `video/main.js`, add an entry to `AppRegistry` and a launcher that calls `ElectronLauncher.Launch("<id>", ...)`. On shutdown the shell asks all Electron apps to quit.

Notes: the bridge covers Red Star's own Electron apps. Third-party apps' tray icons (StatusNotifierItem / D-Bus) are **not** hosted. Electron windows are raised above the full-screen shell with an always-on-top flip; if your window manager still keeps them behind, turn off Settings → Appearance → Full-screen desktop. Alt+arrow snapping is for native windows; Electron windows snap from the taskbar menu.

## Start menu (redesigned)
Opens with the Start button, **Win** or **Ctrl+Esc**. Search box on top, category rail on the left (All apps, Recent, Office, Media, Games, Online, Utilities, System, then your installed Linux apps grouped by type), app list with coloured icon tiles on the right, and a footer with your avatar, Settings and a Power menu (Log Off / Shutdown / Restart).
- Type to search every app; **Enter** launches the first result, **↓** moves into the list, **Enter** launches the highlighted app, **Esc** closes. Clicking an app launches it (arrow keys no longer launch apps by accident).
- *Recent* remembers your last 8 launches.
- Fixes: the old menu was wider than the flyout's built-in 456 px limit (clipped), showed double scrollbars, and had cramped, unaligned rows.

## Red Star Video (native, LibVLC)
A native Avalonia player built on **LibVLCSharp** (Start menu → **RSMedia → Red Star Video**; also opens for video/audio files). It plays everything VLC does (MKV/AVI/WMV/FLV/HEVC included), so the old "can't decode" gaps are gone.

- **Needs libvlc on the machine**: `sudo apt install vlc libvlc-dev` (Debian/Ubuntu) or your distro's equivalent. If it is missing the shell falls back to the classic Electron player in `video/` and shows a hint.
- Playlist (add/remove/clear, click to play, no duplicates, drag and drop), repeat off/all/one, auto-advance, seek bar, volume + mute, speed 0.25x–3x, subtitles (`.srt .vtt .ass .ssa .sub` auto-loaded beside the file; **S** loads another, **V** toggles), resume position per file, error message with *Open with system player*.
- Remembers volume, mute, speed, repeat, subtitles and playlist visibility; tray icon with play/pause, previous, next, show, close.
- Opening another file while it runs adds it to the playlist.
- Shortcuts: **Space/K** · **←/→** ±5 s (Shift ±30 s) · **↑/↓** volume · **M** · **F / double-click** full screen · **, / .** speed · **PgUp/PgDn** · **O** · **S/V** · **L** · **R** · **0–9**.
- Video frames are decoded by libvlc into memory and drawn by Avalonia itself (no native child window), so the picture is inline on both X11 and Wayland. Large videos are scaled down to at most 1920 px wide for display.

## Changes in this revision
- **Start menu has two sections**, switched with two icon tabs above the category rail — the red star for Red Star
  apps, your distribution's own logo (os-release `LOGO=`, else a terminal glyph) for Linux apps (the last one
  used is remembered): *Red Star* — All Red Star apps, Recent, Office, Media, Games, Online, Utilities, System;
  *Linux* — All Linux apps, Recent, and the installed programs grouped as Internet / Multimedia / Graphics / Office /
  Development / Games / … (Other last), shown with their own icons. Each section keeps its own Recent list. Search
  covers both and groups the results under RED STAR APPS, LINUX APPS (the open section first) and FILES, SETTINGS AND
  THE WEB. Without installed Linux programs (or for a child account) the tabs are hidden.
- **Messenger** no longer ships sample messages or the fake "typing..." line (old sample messages are purged from saved history). The placeholder contact list is removed too.
- **Full-screen desktop by default** (Settings → Appearance can switch it back to a maximised window).
- **Ping Pong has a normal title bar** and a *Restart the Level* menu entry (also F1); Esc still exits.
- **Red Star Online now runs on Electron** (`browser/`, home page https://duckduckgo.com): address bar with search, back/forward/reload/home, links that open new windows load in the same tab, single instance. It needs these host libraries, which any GTK desktop already has: gtk3, nss, alsa-lib, libxss. Adds roughly 250 MB to the AppImage.

## What's new in 1.3

**Foundation**
- Taskbar lists every open window: click to focus/minimise, right-click for Snap Left/Right, Fill Screen, Close.
- App registry (`Services/AppRegistry.cs`) drives the Start menu – add an app with one line.
- Settings persist in `~/.config/redstaros/` (wallpaper, theme, accent, avatar, password, window sizes/positions, recent files, notes, events, chat history).
- Shortcuts: **Win** or **Ctrl+Esc** Start menu · **Alt+←/→/↑/↓** snap/fill/restore a window · Writer: Ctrl+N/O/S/Shift+S/W/F, F3 · File Manager: Ctrl+C/X/V/A, Delete, F2, F5, Backspace, Enter.

**Apps**
- **File Manager** – places sidebar, sortable columns, right-click menu, cut/copy/paste, rename, new folder, move to Trash (freedesktop trash), opens images in Image Viewer, text in Writer, CSV in Sheets, anything else with the system default.
- **Writer** – see "Writer, Sheets and Studio (round 3)" below (the native plain-text Writer remains as a fallback).
- **Notes** – many notes, autosaved.
- **Sheets** – see "Writer, Sheets and Studio (round 3)" below.
- **Settings** – real network interfaces, Wi-Fi scan/connect via `nmcli`, real user name, lock-screen password, profile photo, account type (Child hides online apps and installed Linux apps), update checker (GitHub releases API URL), Appearance (light/dark/system + accent), Wallpaper picker with thumbnails and slideshow.
- **Camera** – ffmpeg + `/dev/video*`: take picture (to `~/Pictures`), live preview (snapshot polling), record video (to `~/Videos`).
- **Calendar** – saved events with reminders (toast notification + next event shown in the tray).
- **Messenger** – history saved locally.

**Shell**
- Start menu with search across Red Star apps **and every installed Linux app** (`.desktop` files).
- Desktop icons from `~/Desktop`, right-click menu (wallpaper, new folder, terminal, settings).
- Tray: see *Shell redesign* above (app tray + system tray + quick settings); in-app toast notifications with history.

## Not done (and why)
- **Messenger over a real network (Matrix/IRC):** needs a decision on which service and credentials; history is local only.
- **Minimise/maximise animations:** Avalonia windows can't be animated portably across X11/Wayland window managers.
- **Drag-to-edge snapping:** snapping is by taskbar menu and Alt+arrows. A window's own maximise button covers the taskbar (use *Fill Screen* instead).
- **MVVM refactor:** the shared state lives in small static services instead; a full MVVM rewrite would add risk without changing behaviour.
- **Wayland note:** run under XWayland; window position restore and snapping depend on the compositor honouring client positions (GNOME/Wayland may ignore them).
- Power menu: Log Off shows the lock screen; Shutdown/Restart affect only Red Star OS, not the computer.

## Red Star Paint (rebuilt)

Paint is now a real raster editor (SkiaSharp, already bundled with Avalonia) instead of a stroke list:

- **Tools:** pencil, fill, text, eraser, colour picker, magnifier, brushes (round, square, calligraphy, airbrush, marker, crayon), rectangle and free-form select, 20 shapes (line, curve, polygon, oval, rectangle, rounded rectangle, triangles, diamond, pentagon, hexagon, arrows, stars, heart, lightning) with outline / outline + fill / solid fill.
- **Canvas:** drag the right/bottom/corner grips or use *Image → Canvas size…*; zoom 12.5 %–800 % (Ctrl+wheel), gridlines, status bar with cursor position and sizes.
- **Selections:** move, Ctrl-drag to copy, stretch with the eight grips, transparent selection, cut/copy/paste (inside Paint), paste from file, crop, rotate/flip, resize and skew, invert colours.
- **Files:** open/save PNG, JPEG, BMP; 60-step undo/redo (Ctrl+Z / Ctrl+Y); unsaved-changes prompt.
- Shift constrains lines to 45° and shapes to squares/circles. Right-drag uses colour 2 as the main colour.

Building requires `AllowUnsafeBlocks` (already set in `RedStarOS.csproj`).

## Image Viewer (rebuilt) and Red Star Slides (new)

**Image Viewer:** browses the whole folder (←/→, Home/End, thumbnail strip), fit / 100% / wheel zoom anchored on the cursor, drag to pan, double-click to toggle fit and 100%, rotate and flip (R, Shift+R, H, V — EXIF orientation is honoured), slideshow (S), full screen (F11), rename (F2), move to Trash (Del), set as wallpaper, copy path, *Edit in Paint*, and "Save rotated/flipped image". Drag a picture onto the window to open it.

**Red Star Slides** (the missing third Studio app): slide list with thumbnails, five layouts, seven themes, text boxes, 18 shapes, pictures (embedded), move / resize / rotate with smart guides, in-place text editing, fonts, size, bold / italic / underline, bullets, alignment, fill / outline colours, z-order, speaker notes, per-slide transitions (None, Fade, Push), undo / redo, and a full-screen slide show (F5, Shift+F5). Files are saved as `.rsslides` (JSON with embedded pictures); export to PDF or PNG images from the File menu.

## Writer, Sheets and Studio (round 3)

**Red Star Writer** is now a fully native Avalonia word processor (no Electron). Its own rich-text engine lives in `Views/DocView.cs` (document model in `Services/RichDoc.cs`).

- Formatting: paragraph styles (Normal, Heading 1–3, Quote, Code), font, size, bold/italic/underline/strike, super/subscript, text colour, highlight, clear formatting, alignment (left/centre/right/justify), bullets, numbering, indent/outdent, line spacing, links (Ctrl+click opens), pictures (insert, drop, paste), tables (Tab moves between cells, Tab in the last cell adds a row, row/column menu), horizontal line, page break.
- Documents: tabs with an unsaved dot, caret and scroll restored per tab, autosave crash recovery every 4 s, undo/redo, find/replace (highlighted matches, match case, replace all), word/character count, zoom 50–250 % (Ctrl+wheel), page view (A4/Letter with page guides and page numbers) or web view.
- Files: `.docx` (own reader/writer: styles, lists, tables, images, links, footers with page numbers), `.html`, `.md`, `.txt`; PDF export is a real vector PDF with selectable text; Print sends that PDF to the default printer (`lp`) or opens it in your viewer.
- Opening another file while Writer is running adds a tab. The old Electron `writer/` folder is no longer packaged or launched.
- Differences from the old Electron Writer: no spell-check underlines (Avalonia has no spell-checker; install nothing, it is simply not there), table cells hold one paragraph each, pictures are their own block (not inline with text), and column widths in tables are equal.
- Rich text ribbon: paragraph styles, font, size, bold / italic / underline / strike, super/subscript, text colour, highlight, alignment, bullets, numbering, indent, line spacing, links, pictures, tables, rules, page breaks.
- Files: **.docx** (own reader/writer: styles, lists, tables, pictures, links, page numbers), .html, .txt and .md. PDF export and printing use Chromium's print engine, with A4 or Letter.
- Page view with page-break guides, web view, zoom (Ctrl+wheel), find & replace with highlighting, word / character count, spell-check underlines (Chromium's built-in checker), tabs with a dot for unsaved changes, each tab remembers its scroll position and caret, crash recovery (autosaved every 4 s, offered on next start), `--file=` arguments and file-manager opening go to the running instance.
- Not included: per-tab undo history (undo restarts when you switch tabs), headers / footers other than page numbers, comments and tracked changes.

**Red Star Sheets** was rebuilt around a custom-drawn grid, so only the visible cells are ever created.
- 100,000 rows × 702 columns (A–ZZ), resizable columns and rows (double-click a column border to fit), freeze panes, Ctrl+arrow jumps, Shift-selection, header selection, fill handle (copies, number series, "Item 1, Item 2" text series, formulas shift their references), Ctrl+D / Ctrl+R.
- Formatting: bold / italic / underline, text and fill colours, borders, alignment, number formats (number, thousands, currency, percent, date, text) with decimal buttons. Typing `12%`, `$5` or `2025-01-31` sets the format for you.
- Clipboard as tab-separated text (works with other programs), internal copy keeps formatting and moves relative references; cut moves cells. Undo / redo (200 steps).
- About 80 functions: SUM, AVERAGE, COUNT, MIN, MAX, MEDIAN, IF, IFERROR, AND, OR, COUNTIF(S), SUMIF(S), AVERAGEIF, VLOOKUP, HLOOKUP, INDEX, MATCH, text functions, TEXT, dates, rounding and more (Help → Functions). Operators `+ - * / ^ % & = <> < > <= >=`, `$A$1` references, text and TRUE/FALSE.
- Sort A→Z / Z→A (heading row detected), filter by column, status-bar sum / average / count, **Chart** window (column, bar, line, pie) with PNG export.
- Files: **.xlsx** open and save (values, formulas, formats, widths, heights, frozen panes; first sheet only), .csv / .tsv.
- Not included: several sheets in one workbook, merged cells, inserting or deleting rows / columns, charts embedded in the grid, formulas that refer to other sheets, row insertion adjusting references.

**Studio** now shows recent documents from Writer, Sheets and Slides, plus **Open file…** and three "New" buttons. The File Manager and any "open" action route `.docx .txt .md .html` to Writer, `.xlsx .csv .tsv` to Sheets and `.rsslides` to Slides.

Build note: `build-appimage.sh` copies `writer/` into the AppImage (no `npm install` is needed for it).

## Licence
Copyright (C) 2026 The Red Star OS contributors.

Red Star OS is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
version (SPDX: `GPL-3.0-or-later`). It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
see [LICENSE](LICENSE) for the full text.

It uses Avalonia (MIT), LibVLC (LGPL-2.1-or-later, linked dynamically) and other libraries under GPL-compatible
licences; their own terms still apply to them.
