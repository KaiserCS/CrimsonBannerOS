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
| .NET runtime and SDK | the shell | .NET Foundation, steered by Microsoft | MIT | **dependent.** Kept by decision (18 Nov 2026): a rewrite would cost more than it frees. Rules: no Windows-only APIs, telemetry off, nothing that ties us to Microsoft services. Way out: the shell's logic stays portable; the X11 and Wayland parts are already plain C. |
| Avalonia (incl. Avalonia.Wayland) | the shell's UI | AvaloniaUI OÜ (company) | MIT | **dependent.** Pinned versions; the native Wayland path reduces reliance on X11. Way out: none cheap. Watch the licence and governance at every upgrade. |
| Chromium via Electron | Red Star Online's web engine | Google (Chromium), OpenJS Foundation (Electron) | BSD, MIT | **dependent: the largest one.** Way out to evaluate: WebKitGTK (community-maintained, LGPL). |
| libVLC | video | VideoLAN (non-profit) | LGPL-2.1 | accepted |
| xUnit, Playwright | tests | community, Microsoft (Playwright) | Apache-2.0, Apache-2.0 | tests only; Playwright replaceable by the engine's own test harness |
| GitHub and GitHub Actions | hosting, CI | Microsoft | proprietary service | **dependent.** GitHub is a mirror, not the authority (see 4). Way out: mirror to a community forge such as Codeberg (Forgejo) and run CI there or on our own runner. |
| NuGet, npm registries | package downloads | Microsoft, GitHub (Microsoft) | proprietary services | **dependent.** Way out: a local package cache so builds can run offline. |
| AI-assisted development | writing and reviewing code | Anthropic (company) | proprietary service | **dependent.** Its output is GPL like all our code; the tool itself is not ours. Nothing in the build or the running system depends on it. |

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
