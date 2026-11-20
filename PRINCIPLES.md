# Principles: self-reliance

Red Star OS takes its line from Marxist-Leninist thought, specifically the principle of **self-reliance**. A
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
| wlroots, Wayland, wayland-protocols, xkbcommon, pixman | redstar-comp | community (freedesktop.org) | MIT | accepted; the compositor is our own |
| Xwayland, xcb, cairo | X11 apps, title bars | community (freedesktop.org) | MIT, LGPL/MPL | accepted; Xwayland only while X11 apps exist |
| D-Bus (the session bus) | system tray, portals | community (freedesktop.org) | GPL-2.0/AFL | accepted; we talk to it with our own client (`Services/DBus.cs`), no D-Bus library |
| .NET runtime and SDK | the shell | .NET Foundation, steered by Microsoft | MIT | **dependent.** Kept by decision (18 Nov 2026): a rewrite would cost more than it frees. Rules: no Windows-only APIs, telemetry off, nothing that ties us to Microsoft services. Way out: the shell's logic stays portable; the X11 and Wayland parts are already plain C. |
| Avalonia (incl. Avalonia.Wayland) | the shell's UI | AvaloniaUI OÜ (company) | MIT | **dependent.** Pinned versions; the native Wayland path reduces reliance on X11. Way out: none cheap. Watch the licence and governance at every upgrade. |
| WebKitGTK | Red Star Online's web engine (since 20 Nov 2026) | WebKit project (upstream led largely by Apple); the GTK port and its releases by Igalia (a worker-owned cooperative) and the GNOME community | LGPL-2.1, BSD | **commons.** Free software under licences on GNU's free-licence list; LGPL keeps it open. If upstream ever turns against the port, the port's maintainers and its users are the way out. |
| GTK 4, json-glib, libsoup | Red Star Online's window, data file, filter-list downloads | GNOME Foundation (non-profit) | LGPL-2.1 | accepted (commons) |
| GStreamer, FFmpeg (gst-libav) | sound and video in Red Star Online's pages | community (freedesktop.org; FFmpeg project) | LGPL-2.1 | accepted (commons); system packages, not bundled |
| Chromium via Electron | the previous Red Star Online, Red Star Video | Google (Chromium), OpenJS Foundation (Electron) | BSD, MIT | **dependent, on its way out.** Red Star Online no longer needs it; it stays as the previous browser (Settings → Default apps) until the new one has reader mode, PDF saving, picture-in-picture and imports, and for Red Star Video. Way out: those features in Red Star Online, Red Star Video on GStreamer or libVLC. |
| libVLC | video | VideoLAN (non-profit) | LGPL-2.1 | accepted |
| xUnit, Playwright | tests | community, Microsoft (Playwright) | Apache-2.0, Apache-2.0 | tests only; Playwright replaceable by the engine's own test harness |
| GitHub and GitHub Actions | hosting, CI | Microsoft | proprietary service | **dependent, kept by decision (19 Nov 2026).** Codeberg's terms (July 2026) bar projects mostly written by generative AI and limit private repositories, and self-hosting Forgejo costs upkeep; independence is cheaper to win in other layers. GitHub still only repeats `./check.sh` (see 4). Way out if that changes: self-hosted Forgejo (GPL-3.0-or-later) with its own runner, GitHub as a push mirror. |
| NuGet, npm registries | package downloads | Microsoft, GitHub (Microsoft) | proprietary services | **dependent.** Way out: a local package cache so builds can run offline. |
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
| Shell: desktop, taskbar, apps | Red Star OS | ours |
| UI toolkit | Avalonia | dependent |
| Runtime | .NET | dependent |
| Web engine | Red Star Online on WebKitGTK | commons |
| Media | libVLC | commons |
| Tests | xUnit, our own harnesses | commons |
| Build and release | check.sh, build-appimage.sh | ours |
| Source hosting and CI | GitHub | dependent |
| Package sources | NuGet, npm registries | dependent |

**Now: 8 of 12 (67%).** The web engine moved to WebKitGTK (Red Star Online's rewrite, 20 Nov 2026). Source hosting
stays on GitHub by decision (19 Nov 2026, see the dependency table), so the rest of the way runs through the other
layers:

1. ~~Move the web engine to WebKitGTK~~: done, 8/12 (67%).
2. Keep a local package feed in the repository's tooling so builds run offline from cached packages. Package
   sources become ours: **9/12 (75%), the target.**

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
  the bar for when Red Star becomes a distribution. Among other things: only free software and free firmware in what
  it ships, a kernel without non-free blobs (Linux-libre), and no steering users towards non-free programs.
- [Ethical repository criteria](https://www.gnu.org/software/repo-criteria.html): how GNU grades code hosts.

Known differences with that advice, recorded openly:
- **GitHub** grades poorly on GNU's repository criteria. It's kept by decision (19 Nov 2026, see the dependency
  table).
- **Proprietary programs** like Discord and Steam work with Red Star through open standards, such as the system
  tray. Red Star doesn't ship them or advertise them. To meet the FSDG as a distribution, the defaults, store and
  recommendations stay free software only.
- **Chromium (Electron)** is free software, but the FSF prefers browsers without Google's services built in. Red Star
  Online now runs on WebKitGTK; Electron remains only for the previous browser and Red Star Video until they're
  replaced.

## 4. Build and verify without the platforms

- `./check.sh` is the authority on whether the project works. CI on a hosted platform is a convenience that runs the
  same checks, never a step that only exists there.
- The project must build and pass its checks on a member's own machine. The goal is to do this offline, from cached
  packages.
- Releases are built by our own scripts (`build-appimage.sh`), which are reproducible from the repository alone.

## 5. Nothing reports home

- No telemetry, analytics or crash reporting that leaves the machine. Tools that collect it by default are switched
  off in our scripts (`DOTNET_CLI_TELEMETRY_OPTOUT`).
- No accounts and no cloud services required to use Red Star OS. User data stays local unless the user sends it
  somewhere.
- Unlike the original Red Star OS, the system doesn't watermark files, watch what users do or report on them.

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
