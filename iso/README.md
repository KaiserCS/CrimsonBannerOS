# The installer ISOs

Three live, installable ISOs of Crimson Banner OS, built with archiso. Each one boots the whole desktop and opens
Calamares, which copies that live system to the disk as it is. Installing needs no network, and nothing is sent
anywhere.

**For now only the `linux` ISO is built** (on every change, by CI). The `libre` and `mac` ones are put aside until
later: their recipes stay here, and `tools/iso.sh all libre` or the workflow's `libre_and_mac` option still builds them.

| Variant | Kernel | Firmware | Built from |
|---|---|---|---|
| `linux` | Arch's `linux` | linux-firmware, AMD/Intel microcode, sof-firmware | Arch's repositories |
| `libre` | `linux-libre` | none (`tools/iso.sh` fails the build if any non-free package gets on it) | `pkgs/linux-libre` (FSFLA's source, Arch's patch and configuration) |
| `mac` | `linux-t2` (t2linux) | as `linux`, plus Broadcom's `wl` (DKMS) for older Macs; T2 Macs' Wi-Fi/Bluetooth firmware is copied from their macOS when the live session starts | `pkgs/linux-t2`, `pkgs/t2fanrd`, `pkgs/tiny-dfr`, `pkgs/apple-*` |

## Building

On any Arch-based machine, with `archiso` and `base-devel` installed:

```bash
tools/iso.sh all                 # all three: builds the packages (as you), then the ISOs (asks for sudo)
tools/iso.sh all libre           # just one
tools/iso.sh packages mac        # only the packages (the kernels take an hour or two each; built once per version)
sudo tools/iso.sh image mac      # only the ISO, from packages already built
```

The ISOs go to `out/`, each with a `.sha256` file. CI does the same (`.github/workflows/iso.yml`): it keeps the two
kernels between runs, starts each ISO in QEMU to check that the live session comes up, and attaches the ISOs to a
release when it's tagged.

## What's where

| | |
|---|---|
| `profile/` | the archiso profile every variant shares: boot menus (`syslinux/`, `efiboot/`, `grub/`), the live system's files (`airootfs/`: the live user, its automatic sign-in through greetd, the installer's launcher, `crimson-live-cleanup`) |
| `packages.common` | the packages every ISO carries (the desktop, its optional programs, the installer, file systems) |
| `variants/*/` | each variant's kernel and firmware (`packages`), its settings (`variant.conf`: kernel, boot parameters, services) and extra files (`airootfs/`) |
| `calamares/` | the installer's settings, its modules' configuration and its branding |
| `pkgs/` | recipes for what Arch doesn't ship: Calamares, ckbcomp, Linux-libre, and t2linux's kernel and tools |

`tools/iso.sh` fills in the `@…@` values (kernel, boot parameters, version) for each variant.

## How installing works

Calamares partitions the disk, then copies the live system's root image (`unpackfs`). Next, `crimson-live-cleanup`
turns the copy into an installed system: it removes the live user's automatic sign-in and passwordless sudo, puts the
kernel back in `/boot`, makes a fresh pacman keyring, switches greetd to Crimson Banner OS's login screen, names
the system (`/etc/os-release`) and, once `tools/publish-os.sh` has published it, turns on the `[crimson-banner]`
update repository, so Software Updates brings each release. After
that, Calamares creates the person's account, removes the installer's packages, builds the initramfs and installs
GRUB (BIOS and UEFI, with the fallback EFI path that Macs need).

## Still to do

- On the libre edition, a blacklist of Arch's non-free packages (like Parabola's `your-freedom`).
- Free firmware for the libre ISO (`linux-libre-firmware`).
