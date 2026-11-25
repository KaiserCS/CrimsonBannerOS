# Principles: self-reliance

Crimson Banner OS (formerly Red Star OS) takes its line from Marxist-Leninist thought, specifically the principle of **self-reliance**. A
desktop is a means of production: the people who use it do their work through it, so who owns it and who controls
the layers beneath it are not side questions. Whoever controls the base layers can set the terms for everything built
on them. The project therefore aims to hold its own base, to keep what it produces as collective property, and to know
exactly where it still depends on others and how it would stop.

These principles govern decisions about the code, its dependencies, its tools and its infrastructure. They don't
replace engineering judgement. They decide between options when judgement leaves more than one open.

## 1. The work is collective property

- All original code is **GPL-3.0-or-later**. Copyleft keeps the collective work from being enclosed: anyone may
  use, study, change and share it, and no one may take it private.
- No contributor licence agreements and no proprietary or "enterprise" editions. Contributions stay in the commons on
  the same terms as everything else.

## 2. Control the base

- Own the layers that govern the rest. The project already holds its window manager (redstar-wm) and its compositor
  (redstar-comp), and its shell no longer needs X11. Further work goes downward where that buys independence: the
  layers other parts can't do without come first.
- Between dependencies that do the same job, prefer the one governed by a community, a public body or a non-profit
  foundation over one a single corporation controls. Prefer copyleft over permissive licences, and permissive over
  proprietary. Proprietary dependencies aren't accepted.

## 3. Every dependency is accounted for

Each dependency is recorded below with who controls it and the way out. A new dependency goes into this table in the
same change that adds it, or it doesn't go in.

| Dependency | Used for | Controlled by | Licence | Status and way out |
|---|---|---|---|---|
| Linux, Mesa, libdrm | the base system | community (Linux Foundation, freedesktop.org) | GPL-2.0, MIT | foundation; accepted |
| Arch Linux (its packages, pacman, makepkg, repo-add, archiso) | the distribution base: the system Crimson Banner installs on, its package manager and the tools that build and publish our packages | the Arch Linux team, a volunteer community with its own governance; an associated project of Software in the Public Interest (non-profit) since 2011 | GPL-2.0-or-later (pacman, archiso); each package under its own licence | **commons, chosen 20 Nov 2026** over Debian (too far behind for wlroots 0.20 and .NET 8), Guix System (needs Linux-libre; .NET isn't packaged) and the company-run bases (Fedora, Ubuntu, openSUSE). Our packages are plain PKGBUILDs (`packaging/arch`) in our own signed repository (`tools/repo.sh`), so they aren't held by Arch's servers. Way out: Parabola (FSF-endorsed) and every other pacman-based system read the same PKGBUILDs. |
| GnuPG | signing our packages and repository | GNU project | GPL-3.0-or-later | accepted (commons) |
| archiso | building the installer ISOs (`tools/iso.sh`, `iso/profile`) | Arch Linux community | GPL-3.0-or-later | **commons** (22 Nov 2026). Our profile is a plain folder in the repository. Way out: any live-image builder (Parabola's is a fork of it) |
| Calamares, ckbcomp | the installer on the ISOs, with our own configuration and branding (`iso/calamares`); ckbcomp turns keyboard layouts into console keymaps for it | the Calamares community (on Codeberg); Debian's installer team (ckbcomp) | GPL-3.0-or-later; GPL-2.0-or-later | **commons** (22 Nov 2026), built by us from release tarballs (`iso/pkgs`). Neither is in Arch's repositories. Its GeoIP lookup, network check and log upload are off, so installing sends nothing anywhere. Way out: archinstall (Arch, GPL-3.0), or an installer of our own |
| Linux-libre | the kernel of the libre ISO | FSFLA (Free Software Foundation Latin America) | GPL-2.0-only | **commons** (22 Nov 2026), built by us from the FSFLA's signed release with Arch's patch and configuration (`iso/pkgs/linux-libre`), so all three ISOs run the same kernel version. Way out: Parabola's build |
| t2linux: linux-t2 and its patches, t2fanrd, apple-t2-audio-config, apple-bcm-firmware-fetcher; tiny-dfr (Asahi Linux) | the Mac ISO: keyboard, trackpad, audio, fans and Touch Bar on T2 Macs, and copying their Wi-Fi/Bluetooth firmware from macOS | the t2linux community; the Asahi Linux community | GPL-2.0, GPL-3.0, MIT | **commons** (22 Nov 2026), built by us from their recipes kept in `iso/pkgs`, with every git source pinned to a commit. Their own package repository is unsigned, so we don't use it. Way out: mainline Linux, as more of the T2 patches go upstream |
| Non-free firmware and drivers: linux-firmware, AMD and Intel microcode, sof-firmware, Broadcom wl | the Linux and Mac ISOs only: Wi-Fi, graphics, sound and CPU fixes on most hardware; wl for the Wi-Fi of older Macs | the hardware makers, packaged by Arch | proprietary (redistributable) | **an exception, by decision (22 Nov 2026).** The owner chose it so the hardware works; it is the one place Crimson Banner ships non-free code. The libre ISO carries none of it (`tools/iso.sh` checks every build), and T2 Macs' Wi-Fi firmware is copied from the Mac's own macOS, never shipped. Way out: free firmware as it appears, and the libre ISO |
| greetd (and agreety) | the login screen's base: it starts on the console, signs people in through PAM and starts their desktop; Crimson Banner's own login screen runs on it, and agreety is its text fallback | community project (kennylevinsen and contributors) | GPL-3.0-or-later | **commons** (21 Nov 2026), replacing SDDM (KDE community, GPL-2.0). Way out: any display manager; `crimson-greeter-setup disable` returns to the previous one |
| Flatpak | Software's Flatpak tab: installing, updating and running Flatpaks (its own `flatpak` command) | freedesktop.org community | LGPL-2.1-or-later | **commons, optional** (21 Nov 2026): nothing needs it to build, test or run; Software offers to install it. Way out: the AppImage tab and our own catalogue don't use it |
| Flathub | where most Flatpaks come from; Software reads its AppStream catalogue from disk, not a web API | the Flathub team, a GNOME Foundation program (non-profit) with KDE e.V. | the catalogue is CC0; each program has its own licence | **accepted, optional, filtered** (21 Nov 2026): Software lists only free software (`FreeLicences`), because Flathub also carries non-free programs. Hosted, but nothing needs it to build, test or run. Way out: any other Flatpak remote, or our own |
| AppImageHub and AppImage | Software's AppImage tab: the community's list of AppImages (appimage.github.io), and each project's own GitHub release for the download | the AppImage community (AppImageHub is served from GitHub Pages) | MIT (AppImage's tools); each program has its own licence | **accepted, optional, filtered** (21 Nov 2026): not-free programs aren't listed; a download must match its release's checksum and be a real AppImage. Way out: adding an AppImage file you have, and our own catalogue |
| Our own software catalogue and apps repository | Software's Crimson Banner tab: `apps/`, published by `tools/publish-app.sh` as a signed catalogue (`catalog.json` + `.sig`) and the signed `[crimson-banner-apps]` pacman repository, on two GitHub releases kept up to date in place ("apps", "apps-x86_64") | us | GPL-3.0-or-later; programs must be free software | **ours** (21 Nov 2026). Signed with the project's key, which stays on the owner's computer; the shell checks the catalogue with gpgv against the public key it ships with (`software/crimson-banner.gpg`), keeps the last good one and has the one it was built with, so it works offline. Way out: `--local` writes the same files to a folder any web server can serve; Settings' SoftwareCatalogUrl and `CRIMSON_APPS_SERVER` point at it |
| Our own update repository | Installed systems' updates: `tools/publish-os.sh` builds each release's packages from its tagged commit, signs them with the project's key and publishes the `[crimson-banner]` pacman repository on a GitHub release kept up to date in place ("os-x86_64"); `crimson-repo enable os` (run by the installer) trusts the key and adds it to pacman.conf | us | GPL-3.0-or-later | **ours** (25 Nov 2026). The key stays on the owner's computer (owner's decision, 25 Nov 2026: no signing in CI). Way out: `--local` writes the same signed folder for any web server, a USB stick or file://; `CRIMSON_OS_SERVER` points installed systems at it |
| GitHub CLI (`gh`) | `tools/publish-app.sh` and `tools/publish-os.sh` uploading to GitHub Releases (only on the publishing computer) | GitHub (Microsoft) | MIT | **accepted, tool for a kept service**: it only talks to GitHub, which is kept by decision. Way out: `--local` and any web server |
| wlroots, Wayland, wayland-protocols, xkbcommon, pixman | redstar-comp | community (freedesktop.org) | MIT | accepted; the compositor is our own |
| Xwayland, xcb, cairo | X11 apps, title bars | community (freedesktop.org) | MIT, LGPL/MPL | accepted; Xwayland only while X11 apps exist |
| D-Bus (the session bus) | system tray, portals | community (freedesktop.org) | GPL-2.0/AFL | accepted; we talk to it with our own client (`Services/DBus.cs`), no D-Bus library |
| .NET runtime and SDK | the shell | .NET Foundation, steered by Microsoft | MIT | **dependent.** Kept by decision (18 Nov 2026): a rewrite would cost more than it frees. Rules: no Windows-only APIs, telemetry off, nothing that ties us to Microsoft services. Way out: the shell's logic stays portable; the X11 and Wayland parts are already plain C. |
| Avalonia (incl. Avalonia.Wayland) | the shell's UI | AvaloniaUI OÜ (company) | MIT | **dependent.** Pinned versions; the native Wayland path reduces reliance on X11. Way out: none cheap. Watch the licence and governance at every upgrade. |
| WebKitGTK | Crimson Online's web engine (since 20 Nov 2026; it replaced Chromium via Electron, removed the same day with npm) | WebKit project (upstream led largely by Apple); the GTK port and its releases by Igalia (a worker-owned cooperative) and the GNOME community | LGPL-2.1, BSD | **commons.** Free software under licences on GNU's free-licence list; LGPL keeps it open. If upstream ever turns against the port, the port's maintainers and its users are the way out. |
| GTK 4, json-glib, libsoup | Crimson Online's window, data file, filter-list downloads | GNOME Foundation (non-profit) | LGPL-2.1 | accepted (commons) |
| GStreamer, FFmpeg (gst-libav) | sound and video in Crimson Online's pages | community (freedesktop.org; FFmpeg project) | LGPL-2.1 | accepted (commons); system packages, not bundled |
| libVLC | Video, CD Player (files and audio CDs) | VideoLAN (non-profit) | LGPL-2.1 | accepted |
| CDPlayer's design (Kizarov3, github.com/Kizarov3/CDPlayer) | the look of CD Player and Video: palette, pills, transport keys, deck display, disc and shelf (`Styles/Deck.axaml`, `Views/HiFi/`, `CdPlayerWindow`) | an independent developer | MIT (Expat) | **ours, after theirs** (22 Nov 2026): rebuilt in C# for the shell, so nothing of the Electron app is shipped or run. Its streaming services (Spotify, YouTube Music, SoundCloud) are left out, so there are no accounts and no hosted services. GNU lists the Expat licence as free and GPL-compatible, and our version is GPL-3.0-or-later with CDPlayer's notice kept (`Views/HiFi/NOTICE`). The FSF would prefer copyleft, but the rule is about what we depend on, and nothing here runs their code. Way out: none needed. |
| systemd (logind, timedated, localed), polkit, Linux-PAM, libxcrypt | sleep, time zone, language, administrator prompts, the lock screen's password check, password hashing | community (systemd project, freedesktop.org, Linux-PAM and libxcrypt maintainers) | LGPL-2.1+, BSD/GPL | accepted (commons); the system's own services, reached through their command-line tools or our D-Bus client. Without them the matching setting says so. |
| UDisks2, AccountsService, PackageKit, power-profiles-daemon | drives and Disks, Users, system updates, power mode | community (freedesktop.org) | GPL-2.0+/LGPL, GPL-3.0+ (AccountsService, power-profiles-daemon) | accepted (commons); spoken over the system bus with our own client (`Services/DBus.cs`); each is optional and the page says what to install. On Arch, system updates and Software's pacman installs go to pacman directly instead of PackageKit (22 Nov 2026), whose pacman backend parses pacman.conf with its own, older parser. |
| pacman-contrib (`checkupdates`) | the list of system updates on Arch, from a copy of pacman's database (no root, the system's own lists untouched) | Arch Linux community | GPL-2.0-or-later | accepted (commons). Way out: `pacman -Qu` after a sync, which Software falls back to |
| Matrix (the client-server standard) and the user's homeserver | Messenger | the Matrix.org Foundation (non-profit) for the standard; the server is whichever the user signs in to (matrix.org, a community's, their own) | Apache-2.0 (the specification) | accepted (commons); Messenger is our own client, written to the specification with no SDK. Federated, so no single server is required; self-hosting (Synapse, Conduit and others) is the way out of any one operator. |
| Orca, Onboard | screen reader, on-screen keyboard (X11) | GNOME project; Onboard's community | LGPL-2.1+, GPL-3.0 | accepted (commons); optional, started when the user switches them on |
| grim, wf-recorder, wl-clipboard; scrot, maim, ImageMagick, xclip | Screenshot, Screen Recorder, copying pictures (optional programs, run when installed) | community projects (independent maintainers; ImageMagick Studio LLC for ImageMagick) | MIT, Apache-2.0 (ImageMagick), GPL-2.0+ (xclip) | accepted (commons); Crimson Banner says what to install when they're missing. FFmpeg (above) records on X11, GStreamer (above) records Voice Memos. |
| xUnit | tests | .NET Foundation community | Apache-2.0 | tests only; the window manager's session test uses GTK 3 and PyGObject (GNOME) since 20 Nov 2026, replacing Playwright's Chromium (Microsoft) |
| GitHub and GitHub Actions | hosting, CI | Microsoft | proprietary service | **dependent, kept by decision (19 Nov 2026).** Codeberg's terms (July 2026) bar projects mostly written by generative AI and limit private repositories, and self-hosting Forgejo costs upkeep; independence is cheaper to win in other layers. GitHub still only repeats `./check.sh` (see 4). Way out if that changes: self-hosted Forgejo (GPL-3.0-or-later) with its own runner, GitHub as a push mirror. |
| NuGet registry (nuget.org) | filling our package feed, once | Microsoft | proprietary service | **reduced to a one-time download** (20 Nov 2026). Exact versions and content hashes are in the repository (`packages.lock.json`); `tools/packages.sh` fills our own feed once, and from then on `./check.sh` and `./build-appimage.sh` restore from it alone, offline (CI proves it with nuget.org cut off). A filled feed can be passed on as a tar file, so a member never has to reach nuget.org. npm is gone with Electron. |
| AI-assisted development | writing and reviewing code | Anthropic (company) | proprietary service | **dependent.** Its output is GPL like all our code; the tool itself is not ours. Nothing in the build or the running system depends on it. |

## The goal: self-sufficiency to a point, 75% or more

Self-reliance isn't isolation. Free software built and governed by its community, such as Linux, the freedesktop.org
stack and other GPL projects, is a commons we share in, not a dependency to escape. Copyleft is preferred, since it
keeps that commons open. What the project aims to escape is control by a single company.

The target: **at least 75% of the layers below are either our own or the community's commons.** Each layer is scored
as *ours*, *commons* (community-governed free software) or *dependent* (controlled by one company). Update the scores
whenever a layer changes.

| Layer | What fills it | Score |
|---|---|---|
| Kernel, graphics drivers | Linux, Mesa | commons |
| Compositor (Wayland) | redstar-comp, on wlroots | ours |
| Window manager (X11) | redstar-wm | ours |
| Shell: desktop, taskbar, apps | Crimson Banner OS | ours |
| UI toolkit | Avalonia | dependent |
| Runtime | .NET | dependent |
| Web engine | Crimson Online on WebKitGTK | commons |
| Media | libVLC | commons |
| Tests | xUnit, our own harnesses | commons |
| Build and release | check.sh, build-appimage.sh | ours |
| Source hosting and CI | GitHub | dependent |
| Package sources | our own feed (`tools/packages.sh`), locked versions and hashes | ours |
| Distribution base | Arch Linux (pacman) | commons |
| Packages and their repository | `packaging/arch`, `tools/repo.sh`, `tools/publish-os.sh`: our PKGBUILDs, signed with our own key, in our own pacman repository, which installed systems update from | ours |
| Installing more software | Software: our own catalogue, Flatpak and AppImage in one app of our own, free software only | ours |
| Login screen | Crimson Banner's own login screen (`RedStarOS --greeter` in redstar-comp), on greetd | ours |
| Installer images | `iso/` and `tools/iso.sh` on archiso; Calamares as the installer; the libre and Mac kernels built by us | commons |

**Now: 13 of 17 (76%).** The installer images were added on 22 Nov 2026 as a commons layer: the ISOs and their
build are ours, but the installer program is Calamares. Before that, 13 of 16 (81%): the login screen, our own on greetd in place of SDDM, was added on 21 Nov 2026, as was
Software, our own way to install more programs (12 of 15). Before that,
11 of 14 (79%): the two distribution layers were added on 20 Nov 2026, when Arch Linux was chosen as the
base and our own signed pacman repository was set up. Before that: **9 of 12 (75%): the target was reached, and from
there it is the floor.** The web engine moved to WebKitGTK
(Crimson Online's rewrite, 20 Nov 2026), Electron and npm were removed, and builds restore from our own package feed
(20 Nov 2026). Source hosting stays on GitHub by decision (19 Nov 2026, see the dependency table).

1. ~~Move the web engine to WebKitGTK~~: done, 8/12 (67%).
2. ~~Keep a local package feed in the repository's tooling so builds run offline from cached packages~~: done,
   **9/12 (75%).**

.NET and Avalonia are the last two. Replacing them is the largest piece of work and isn't needed for the target.

**Once reached, 75% is a floor.** No change may take the score below 75%. A change that would turn an *ours* or
*commons* layer into *dependent* must win the points back elsewhere in the same piece of work, or it doesn't go in.
Every change that touches a layer updates its score here.

## Advice from the Free Software Foundation

For the critical calls (a new dependency, a licence, a hosting or distribution question) the project checks what the
Free Software Foundation and GNU recommend, and records it with the decision. Their advice informs the decision; it
doesn't make it.

- [Free Software Directory](https://directory.fsf.org): is a program free software, and under which licence.
- [GNU licence list](https://www.gnu.org/licenses/license-list.html): which licences are free and which can be
  combined with GPL-3.0-or-later.
- [Free System Distribution Guidelines](https://www.gnu.org/distros/free-system-distribution-guidelines) (FSDG):
  the bar for when Crimson Banner becomes a distribution. Among other things: only free software and free firmware in what
  it ships, a kernel without non-free blobs (Linux-libre), and no steering users towards non-free programs.
- [Ethical repository criteria](https://www.gnu.org/software/repo-criteria.html): how GNU grades code hosts.

- **Package feed (20 Nov 2026).** Every package in the lock files is under a licence on GNU's free-licence list (MIT,
  Apache-2.0, BSD). nuget.org is a proprietary service, but it is only a download point: our feed takes it out of
  every build, which is what GNU's advice on depending on services points towards.

- **Distribution base (20 Nov 2026).** The FSF endorses only distributions that meet the FSDG: Guix System,
  Parabola (from Arch), Trisquel (from Ubuntu), PureOS (from Debian), Hyperbola, Dragora and Dyne:bolic. Arch
  itself isn't endorsed, because its kernel and repositories include non-free firmware and programs. Its tools
  (pacman, makepkg) are free software under the GPL. Crimson Banner is based on Arch; of the bases that can carry the
  desktop, it keeps the shortest route to Parabola, which reads the same PKGBUILDs. Our own repository carries only
  free software, as the FSDG asks of what a distribution ships.

- **Software (21 Nov 2026).** The FSDG asks a distribution not to steer people to non-free software, and the FSF
  doesn't endorse Flathub or AppImageHub, since both carry non-free programs as well. Software lists only programs
  whose licence is free (the FSF's list of free licences, by SPDX id; `FreeLicences`), and our own catalogue accepts
  only free software.

- **Installer images (22 Nov 2026).** The FSDG asks that a distribution ship only free software and free firmware,
  with Linux-libre as its kernel. The libre ISO follows that for everything on it: Linux-libre, no firmware blobs, no
  microcode, no Broadcom wl, checked by `tools/iso.sh` on every build. Calamares, archiso and Linux-libre are under
  the GPL, on GNU's list of free licences. The installed system still updates from Arch's repositories, which also
  carry non-free packages; the FSDG asks for repositories without them (Parabola's `your-freedom` list does this for
  Arch's).

Known differences with that advice, recorded openly:
- **The Linux and Mac ISOs ship non-free firmware (decided 22 Nov 2026).** linux-firmware, the AMD and Intel
  microcode, sof-firmware and, on the Mac ISO, Broadcom's wl driver. The FSDG doesn't allow any of them. They are there
  so the hardware people own works, and the README says which ISO carries what. What would change
  it: free firmware for that hardware; until then, the libre ISO is the FSDG-minded choice.
- **The libre ISO's installed system updates from Arch.** Nothing non-free is installed from the ISO, but Arch's
  repositories offer non-free packages afterwards. What would change it: a blacklist like Parabola's `your-freedom`
  on the libre edition, or Parabola's repositories underneath it.
- **AppImages that don't state a licence.** Many of AppImageHub's entries name no licence. Software hides them unless
  you tick "Include AppImages that don't state a licence", and then marks each one as not stated. That box can show a
  program that turns out not to be free, which the FSDG wouldn't allow. What would change it: dropping the box, if
  enough of the list states its licence.
- **The kernel: mainline Linux, not Linux-libre (decided 20 Nov 2026).** The FSDG asks a distribution for a kernel
  without non-free firmware blobs, and the FSF's endorsed distributions (Parabola, Trisquel, Guix System and others)
  ship Linux-libre. Crimson Banner keeps the mainline kernel so that hardware works (Wi-Fi cards, GPUs and other
  devices whose firmware isn't free). This means that, as a distribution, it wouldn't meet the FSDG. Mainline Linux is
  still community-governed free software, so the kernel layer stays *commons*. What would change this: free firmware
  for the hardware people use. Since 22 Nov 2026 there is a Linux-libre edition next to the main one (the libre ISO).
- **Arch Linux as the base** isn't an FSF-endorsed distribution (see above). Together with the mainline kernel, this
  is the main reason Crimson Banner wouldn't meet the FSDG. What would change it: a Parabola-based edition, with
  Linux-libre and Parabola's repositories underneath our own.
- **GitHub** grades poorly on GNU's repository criteria. It's kept by decision (19 Nov 2026, see the dependency
  table).
- **Proprietary programs** like Discord and Steam work with Crimson Banner through open standards, such as the system
  tray. Crimson Banner doesn't ship them or advertise them. To meet the FSDG as a distribution, the defaults, store and
  recommendations stay free software only.
- **Chromium (Electron)** is free software, but the FSF prefers browsers without Google's services built in. It's gone:
  Crimson Online runs on WebKitGTK, Video on libVLC, and Electron was removed (20 Nov 2026).

## 4. Build and verify without the platforms

- `./check.sh` is the authority on whether the project works. CI on a hosted platform is a convenience that runs the
  same checks, never a step that only exists there.
- The project must build and pass its checks on a member's own machine. The goal is to do this offline, from cached
  packages. That's done: once the feed is filled (or imported), nothing needs the network.
- Releases are built by our own scripts (`build-appimage.sh`), which are reproducible from the repository alone.
- Packages come from our own feed (`tools/packages.sh`): `fill` once, then everything restores offline from it. A
  change of package version updates the lock files in the same change.

## 5. Nothing reports home

- No telemetry, analytics or crash reporting that leaves the machine. Tools that collect it by default are switched
  off in our scripts (`DOTNET_CLI_TELEMETRY_OPTOUT`).
- No accounts and no cloud services required to use Crimson Banner OS. User data stays local unless the user sends it
  somewhere.

## 6. Decisions are open and written down

Significant choices, especially accepting or keeping a dependency, are recorded with their reasons and what would
change them: in this file, the Wayland plan and the commit history. A decision can be revisited when its conditions
change.

## Applying it: checks for a change

- Does it add a dependency? Then it adds a row to the table, and the change says why nothing more self-reliant works.
- Does it need a hosted service to build, test or run? Then it doesn't go in.
- Does it send anything off the machine without the user asking? Then it doesn't go in.
- Is the new code GPL-3.0-or-later, with no added restrictions?
- Does `./check.sh` cover it?
- Does the layer score stay at or above 75%?
- For a dependency, licence or distribution question: what do the FSF's Free Software Directory, licence list and
  FSDG say?
