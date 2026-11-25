# Roadmap

Where Crimson Banner OS stands, and what comes next, in order. Update it as items are done: strike them through
with the date, as `PRINCIPLES.md` does.

## Where it stands (25 Nov 2026, release 26.11.3)

- **Pace:** 148 commits in the first 8 days (about 18 a day) and four releases, about one every two days.
- **Size:** about 48,000 lines of C# in the shell (47 windows), 4,100 lines of C in redstar-comp, 1,350 in
  redstar-wm and 3,600 in Crimson Online; 186 automated tests.
- **A distribution now:** an installer ISO, signed updates for installed systems (`[crimson-banner]`), its own
  login screen, its own branding, and one look across the shell, in light and dark.
- **Self-reliance:** 13 of 17 layers ours or community commons (76%), against a floor of 75%: one layer of margin.
- **Tested on:** a VMware virtual machine and CI's emulated machines (QEMU). Not yet on real hardware.

## 1. Trustworthy to install and update

Now that people can install it, a bad release reaches every installed system at once.

- [x] ~~**Install test in CI:** boot the ISO, install with Calamares to a virtual disk, boot the installed system and
      check its login screen and a signed-in session~~ (25 Nov 2026: `.github/scripts/iso-install-test.sh`, on every ISO build)
- [x] ~~**A testing channel:** builds from main published to an `os-testing` repository; `[crimson-banner]` for releases only~~
      (25 Nov 2026: `tools/publish-os.sh --testing`, `crimson-repo channel`, Settings → Software Updates)
- [ ] **Rollback:** Btrfs by default in the installer, with a snapshot before each update (snapper) that the boot
      menu can start.
- [ ] **ISO size:** 1.76 GiB of GitHub's 2 GiB per file. Trim before it's a blocker (optional programs, docs, locales).

## 2. Real hardware

- [ ] **The hardware checklist** (README: TBD): Intel, AMD and NVIDIA graphics; a laptop's Wi-Fi, suspend and
      brightness; two monitors; a HiDPI screen.
- [ ] **NVIDIA** with redstar-comp: the likeliest to fail (wlroots and the proprietary driver).
- [ ] **Wayland by default**, once the hardware checklist passes on it with no fallbacks to X11.

## 3. Finish the new look

- [x] ~~Software and Settings in the deck look; the shared layer for every window; branding and wallpapers~~ (25 Nov 2026)
- [ ] **Per-app passes:** File Manager, Notes, Calendar, Clock, Task Manager.
- [ ] **Split the largest files** while restyling them: DocView (1,600 lines), File Manager (1,400), Settings' pages (1,400).

## 4. Reach more people

- [ ] **Translations:** every string is in English in the code. Add the plumbing now, while it's 50,000 lines and not 100,000.
- [ ] **Accessibility:** what works with Orca (screen-reader support for Avalonia on Linux is limited), and
      everything reachable from the keyboard.
- [ ] **Docs:** a user guide; a contributor guide (CONTRIBUTING, issue templates). One person carries the project
      now, and that's its largest risk.

## 5. Self-reliance margin

- [ ] **Mirror to Codeberg** (Forgejo, run by a non-profit) with `check.sh` on Forgejo Actions: source hosting and CI
      move from dependent to commons, 14 of 17 (82%), and there's a way out of GitHub.
- [ ] **.NET and Avalonia** stay dependent with no realistic replacement: say so in `PRINCIPLES.md`.

## 6. Security housekeeping

- [ ] **The signing key** (`19E7 1BAC DC32 A511 D657 F1FA 9EC9 146D 98BE ECA0`): an offline backup, a revocation
      certificate, and signing with a subkey so the main key stays offline.
- [ ] **Signed release tags** (`tools/release.sh` signs when the repository's key is set).
- [ ] **Secure Boot:** document that it must be off for now; shim signing later.

## 7. Put aside, still open

- [ ] **Upgrade test in CI:** install the last release, update to the new one through `[crimson-banner]`, and sign in
      (put aside 25 Nov 2026; the install test covers installing, and updates are tried by hand for now).
- [ ] The libre and Intel Mac ISOs (their recipes are in `iso/`).
- [ ] CD Player: lyrics, the CD changer, mini mode.

## Trajectory

Extrapolated from the first eight days; it holds only if the pace does.

- **Next two weeks:** the install test and a testing channel (both done), the remaining app passes, a first round of
  hardware: enough for a 26.12.0 to call a beta.
- **One to two months:** Wayland by default, translations' plumbing, the Codeberg mirror (82%), the libre ISO (its
  kernel recipe already builds).
- **Three months and on:** the Mac ISO, accessibility, other contributors: these wait on people and hardware more
  than on pace.

**Writing code isn't the bottleneck; checking it in the real world is.** VMware alone took about two of the eight
days, and that was one virtual machine. Every kind of hardware and every installed system adds that kind of work,
so section 1 comes first: tests like the install test are what let the pace continue safely.
