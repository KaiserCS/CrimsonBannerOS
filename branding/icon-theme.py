#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""The Crimson Banner icon theme: Linux programs' app icons drawn like ours (branding/README.md, "The icon theme").

    python3 branding/icon-theme.py      writes icons/CrimsonBanner (committed; run again after changing a drawing)

Every picture is on the graphite tile in light greys with one crimson detail, as the shell's own app icons are.
Programs with a general job (a file manager, a terminal, a PDF viewer...) get a drawing for that job; where the shell
has its own app for the job, its picture. Programs with a brand of their own (Firefox, Steam, LibreOffice...) aren't
redrawn: the shell sets their own icon on the tile instead (RedStarOS/Services/AdaptiveIcons.cs), so they keep the
mark people know them by and still match.

Needs rsvg-convert (librsvg). Aliases are symbolic links, as in other icon themes.
"""
import os
import re
import shutil
import subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APPS = os.path.join(ROOT, "branding", "src", "apps")
OUT = os.path.join(ROOT, "icons", "CrimsonBanner")
SIZES = [32, 48, 64, 128]          # PNGs for programs (and the shell) that don't read SVG; GTK and Qt draw the SVGs

# the house colours (branding/README.md, "The look")
L1, L2, L3, L4, INK, RED = "#F2F2F5", "#DCDCE3", "#B4B4BC", "#6E6E78", "#0C0C0E", "#C8102E"
HOLE = "#1B1B1F"                   # the tile showing through a shape

TILE = """<defs><linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2A2A2F"/><stop offset="1" stop-color="#0C0C0E"/></linearGradient></defs>
  <rect x="8" y="8" width="240" height="240" rx="54" fill="url(#tile)"/>
  <rect x="8.5" y="8.5" width="239" height="239" rx="53.5" fill="none" stroke="#FFFFFF" stroke-opacity="0.12"/>"""


def page(body, what):
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!-- SPDX-License-Identifier: GPL-3.0-or-later  Crimson Banner icon theme: {what}, on the graphite tile. branding/icon-theme.py -->
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  {TILE}
  {body.strip()}
</svg>
"""


def lines(x, ys, widths, h=10, fill=L3):
    return "".join(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{h / 2}" fill="{fill}"/>' for y, w in zip(ys, widths))


# ───────────── drawings for jobs the shell has no app of its own for ─────────────
DRAWN = {
    "text-editor": ("a page of text with a crimson caret", f"""
  <rect x="62" y="40" width="132" height="176" rx="14" fill="{L1}"/>
  {lines(84, [72, 98, 124, 150], [88, 72, 88, 52])}
  <rect x="142" y="142" width="9" height="26" rx="4.5" fill="{RED}"/>"""),

    "document-viewer": ("a page with a folded corner, a crimson heading and a magnifier", f"""
  <path d="M 76 40 L 156 40 L 194 78 L 194 202 A 14 14 0 0 1 180 216 L 76 216 A 14 14 0 0 1 62 202 L 62 54 A 14 14 0 0 1 76 40 Z" fill="{L1}"/>
  <path d="M 156 40 L 156 66 A 12 12 0 0 0 168 78 L 194 78 Z" fill="{L3}"/>
  <rect x="84" y="70" width="56" height="12" rx="6" fill="{RED}"/>
  {lines(84, [98, 120], [88, 64])}
  <circle cx="148" cy="164" r="22" fill="none" stroke="{L4}" stroke-width="10"/>
  <path d="M 164 180 L 182 198" stroke="{L4}" stroke-width="12" stroke-linecap="round"/>"""),

    "archive": ("a box with a crimson zip", f"""
  <rect x="48" y="96" width="160" height="112" rx="12" fill="{L2}"/>
  <rect x="40" y="62" width="176" height="44" rx="10" fill="{L1}"/>
  <rect x="118" y="62" width="20" height="90" rx="4" fill="{RED}"/>
  <rect x="112" y="140" width="32" height="26" rx="6" fill="{L4}"/>"""),

    "music": ("two beamed notes, the beam crimson", f"""
  <circle cx="90" cy="178" r="26" fill="{L1}"/>
  <circle cx="170" cy="162" r="26" fill="{L1}"/>
  <rect x="104" y="72" width="12" height="108" rx="3" fill="{L1}"/>
  <rect x="184" y="56" width="12" height="108" rx="3" fill="{L1}"/>
  <path d="M 104 70 L 196 52 L 196 82 L 104 100 Z" fill="{RED}"/>"""),

    "mail": ("an envelope sealed in crimson", f"""
  <rect x="36" y="64" width="184" height="128" rx="14" fill="{L1}"/>
  <path d="M 42 70 L 128 134 L 214 70" fill="none" stroke="{L3}" stroke-width="10" stroke-linejoin="round"/>
  <circle cx="128" cy="134" r="16" fill="{RED}"/>"""),

    "contacts": ("a card with a person and a crimson name", f"""
  <rect x="36" y="56" width="184" height="144" rx="16" fill="{L1}"/>
  <circle cx="90" cy="112" r="22" fill="{L4}"/>
  <path d="M 56 172 A 34 30 0 0 1 124 172 Z" fill="{L4}"/>
  <rect x="140" y="98" width="56" height="12" rx="6" fill="{RED}"/>
  {lines(140, [124, 146], [48, 36])}"""),

    "maps": ("a folded map with a crimson pin", f"""
  <path d="M 40 72 L 96 56 L 160 72 L 216 56 L 216 184 L 160 200 L 96 184 L 40 200 Z" fill="{L1}"/>
  <path d="M 96 56 L 160 72 L 160 200 L 96 184 Z" fill="{L2}"/>
  <path d="M 128 172 C 128 172 100 140 100 118 A 28 28 0 0 1 156 118 C 156 140 128 172 128 172 Z" fill="{RED}"/>
  <circle cx="128" cy="118" r="9" fill="{L1}"/>"""),

    "weather": ("a crimson sun behind a cloud", f"""
  <circle cx="100" cy="100" r="38" fill="{RED}"/>
  <circle cx="118" cy="158" r="32" fill="{L1}"/>
  <circle cx="160" cy="142" r="40" fill="{L1}"/>
  <rect x="92" y="156" width="120" height="44" rx="22" fill="{L1}"/>"""),

    "help": ("a lifebuoy with crimson bands", f"""
  <circle cx="128" cy="128" r="62" fill="none" stroke="{L1}" stroke-width="44"/>
  <circle cx="128" cy="128" r="62" fill="none" stroke="{RED}" stroke-width="44" stroke-dasharray="48.7 48.7" transform="rotate(-24 128 128)"/>"""),

    "fonts": ("a letter A with a crimson a", f"""
  <path fill-rule="evenodd" fill="{L1}" d="M 50 200 L 98 56 L 130 56 L 178 200 L 148 200 L 137 166 L 91 166 L 80 200 Z M 99 140 L 129 140 L 114 92 Z"/>
  <circle cx="190" cy="178" r="18" fill="none" stroke="{RED}" stroke-width="12"/>
  <rect x="202" y="152" width="12" height="48" rx="6" fill="{RED}"/>"""),

    "characters": ("a grid of characters, one crimson", "".join(
        f'<rect x="{50 + 56 * c}" y="{50 + 56 * r}" width="44" height="44" rx="10" fill="{RED if (r, c) == (1, 1) else (L1 if (r + c) % 2 == 0 else L3)}"/>'
        for r in range(3) for c in range(3))),

    "backup": ("a clock inside a turning arrow, one hand crimson", f"""
  <path d="M 75 82 A 70 70 0 1 1 64 156" fill="none" stroke="{L1}" stroke-width="18" stroke-linecap="round"/>
  <path d="M 44 66 L 92 66 L 76 112 Z" fill="{L1}" stroke="{L1}" stroke-width="6" stroke-linejoin="round"/>
  <path d="M 132 128 L 132 90" stroke="{L1}" stroke-width="13" stroke-linecap="round"/>
  <path d="M 132 128 L 160 146" stroke="{RED}" stroke-width="13" stroke-linecap="round"/>"""),

    "passwords": ("a key with a crimson bow", f"""
  <circle cx="86" cy="128" r="42" fill="{RED}"/>
  <circle cx="86" cy="128" r="15" fill="{HOLE}"/>
  <rect x="118" y="117" width="102" height="22" rx="11" fill="{L1}"/>
  <rect x="186" y="128" width="16" height="36" rx="5" fill="{L1}"/>
  <rect x="156" y="128" width="14" height="26" rx="5" fill="{L1}"/>"""),

    "remote-desktop": ("a screen reaching another, a crimson pointer", f"""
  <rect x="34" y="48" width="124" height="88" rx="12" fill="{L3}"/>
  <rect x="94" y="100" width="128" height="90" rx="12" fill="{L1}"/>
  <rect x="140" y="190" width="36" height="14" fill="{L2}"/>
  <rect x="120" y="202" width="76" height="10" rx="5" fill="{L2}"/>
  <path d="M 140 122 L 140 168 L 152 156 L 162 176 L 172 171 L 162 152 L 178 150 Z" fill="{RED}"/>"""),

    "virtual-machines": ("a screen with a second computer inside it", f"""
  <rect x="34" y="44" width="188" height="132" rx="14" fill="{L1}"/>
  <rect x="58" y="68" width="140" height="84" rx="8" fill="{HOLE}"/>
  <rect x="58" y="68" width="140" height="16" rx="8" fill="{RED}"/>
  <rect x="58" y="76" width="140" height="8" fill="{RED}"/>
  <rect x="110" y="176" width="36" height="16" fill="{L2}"/>
  <rect x="84" y="190" width="88" height="12" rx="6" fill="{L2}"/>"""),

    "logs": ("a list of entries, one marked crimson", f"""
  <rect x="48" y="40" width="160" height="176" rx="14" fill="{L1}"/>
  {"".join(f'<circle cx="76" cy="{y + 5}" r="8" fill="{RED if y == 118 else L4}"/>' for y in [66, 92, 118, 144, 170])}
  {lines(94, [66, 92, 118, 144, 170], [88, 64, 92, 72, 56])}"""),

    "disk-usage": ("a ring chart with a crimson share", f"""
  <circle cx="128" cy="128" r="66" fill="none" stroke="{L4}" stroke-width="40"/>
  <circle cx="128" cy="128" r="66" fill="none" stroke="{L1}" stroke-width="40" stroke-dasharray="186 300" transform="rotate(-90 128 128)"/>
  <circle cx="128" cy="128" r="66" fill="none" stroke="{RED}" stroke-width="40" stroke-dasharray="104 400" transform="rotate(72 128 128)"/>"""),

    "scanner": ("a flatbed scanner with a crimson light", f"""
  <rect x="36" y="112" width="184" height="80" rx="16" fill="{L2}"/>
  <path d="M 52 108 L 204 76 A 8 8 0 0 1 214 84 L 214 100 L 52 112 Z" fill="{L1}"/>
  <rect x="52" y="140" width="152" height="12" rx="6" fill="{RED}"/>
  <circle cx="190" cy="174" r="7" fill="{L4}"/>"""),

    "printer": ("a printer with a page and a crimson light", f"""
  <rect x="72" y="44" width="112" height="56" rx="6" fill="{L1}"/>
  <rect x="36" y="92" width="184" height="88" rx="16" fill="{L2}"/>
  <rect x="72" y="150" width="112" height="64" rx="6" fill="{L1}"/>
  {lines(88, [168, 188], [80, 56], h=8)}
  <circle cx="194" cy="118" r="8" fill="{RED}"/>"""),

    "bluetooth": ("the Bluetooth rune on a crimson disc", f"""
  <circle cx="128" cy="128" r="80" fill="{RED}"/>
  <path d="M 96 96 L 160 152 L 128 182 L 128 74 L 160 104 L 96 160" fill="none" stroke="{L1}" stroke-width="14" stroke-linecap="round" stroke-linejoin="round"/>"""),

    "network": ("signal bars, the first crimson", "".join(
        f'<rect x="{48 + 42 * i}" y="{200 - h}" width="30" height="{h}" rx="8" fill="{RED if i == 0 else L1}"/>'
        for i, h in enumerate([44, 80, 116, 152]))),

    "sound": ("a loudspeaker with waves, the outer crimson", f"""
  <path d="M 48 104 L 82 104 L 128 66 L 128 190 L 82 152 L 48 152 Z" fill="{L1}" stroke="{L1}" stroke-width="10" stroke-linejoin="round"/>
  <path d="M 156 100 A 40 40 0 0 1 156 156" fill="none" stroke="{L2}" stroke-width="14" stroke-linecap="round"/>
  <path d="M 178 76 A 72 72 0 0 1 178 180" fill="none" stroke="{RED}" stroke-width="14" stroke-linecap="round"/>"""),

    "power": ("a battery filling in crimson", f"""
  <rect x="44" y="80" width="152" height="96" rx="18" fill="none" stroke="{L1}" stroke-width="12"/>
  <rect x="200" y="108" width="16" height="40" rx="5" fill="{L1}"/>
  <rect x="64" y="100" width="78" height="56" rx="7" fill="{RED}"/>"""),

    "code": ("angle brackets around a crimson slash", f"""
  <path d="M 96 82 L 52 128 L 96 174" fill="none" stroke="{L1}" stroke-width="18" stroke-linecap="round" stroke-linejoin="round"/>
  <path d="M 160 82 L 204 128 L 160 174" fill="none" stroke="{L1}" stroke-width="18" stroke-linecap="round" stroke-linejoin="round"/>
  <path d="M 142 70 L 114 186" stroke="{RED}" stroke-width="18" stroke-linecap="round"/>"""),

    "firewall": ("a shield with a crimson tick", f"""
  <path d="M 128 38 L 204 66 L 204 124 C 204 170 172 202 128 218 C 84 202 52 170 52 124 L 52 66 Z" fill="{L1}"/>
  <path d="M 94 128 L 120 154 L 166 104" fill="none" stroke="{RED}" stroke-width="18" stroke-linecap="round" stroke-linejoin="round"/>"""),

    "downloads": ("a crimson arrow into a tray", f"""
  <path d="M 52 152 L 52 196 L 204 196 L 204 152" fill="none" stroke="{L1}" stroke-width="18" stroke-linecap="round" stroke-linejoin="round"/>
  <path d="M 128 52 L 128 156" stroke="{RED}" stroke-width="20" stroke-linecap="round"/>
  <path d="M 86 116 L 128 158 L 170 116" fill="none" stroke="{RED}" stroke-width="20" stroke-linecap="round" stroke-linejoin="round"/>"""),

    "books": ("a book with a crimson bookmark", f"""
  <rect x="66" y="40" width="128" height="176" rx="12" fill="{L1}"/>
  <rect x="66" y="40" width="26" height="176" rx="12" fill="{L3}"/>
  <rect x="80" y="40" width="12" height="176" fill="{L3}"/>
  <path d="M 148 40 L 170 40 L 170 104 L 159 94 L 148 104 Z" fill="{RED}"/>
  {lines(108, [128, 150], [64, 48], fill=L3)}"""),

    "system-info": ("an i in a circle, its dot crimson", f"""
  <circle cx="128" cy="128" r="82" fill="{L1}"/>
  <circle cx="128" cy="84" r="14" fill="{RED}"/>
  <rect x="116" y="112" width="24" height="78" rx="12" fill="{INK}"/>"""),

    "tweaks": ("three sliders, one crimson", f"""
  {lines(48, [72, 122, 172], [160, 160, 160], h=12, fill=L4)}
  <circle cx="92" cy="78" r="20" fill="{L1}"/>
  <circle cx="168" cy="128" r="20" fill="{RED}"/>
  <circle cx="116" cy="178" r="20" fill="{L1}"/>"""),

    "extensions": ("a puzzle piece with a crimson knob", f"""
  <rect x="60" y="84" width="112" height="112" rx="16" fill="{L1}"/>
  <circle cx="116" cy="80" r="24" fill="{L1}"/>
  <circle cx="178" cy="140" r="24" fill="{RED}"/>"""),

    "tasks": ("a checklist, the first ticked in crimson", f"""
  <rect x="48" y="40" width="160" height="176" rx="14" fill="{L1}"/>
  <path d="M 70 78 L 82 90 L 104 66" fill="none" stroke="{RED}" stroke-width="10" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="70" y="114" width="28" height="28" rx="7" fill="none" stroke="{L4}" stroke-width="7"/>
  <rect x="70" y="164" width="28" height="28" rx="7" fill="none" stroke="{L4}" stroke-width="7"/>
  {lines(118, [74, 124, 174], [70, 62, 54])}"""),

    "dictionary": ("a book with a crimson A", f"""
  <rect x="66" y="40" width="128" height="176" rx="12" fill="{L1}"/>
  <rect x="66" y="40" width="26" height="176" rx="12" fill="{L3}"/>
  <rect x="80" y="40" width="12" height="176" fill="{L3}"/>
  <path fill-rule="evenodd" fill="{RED}" d="M 110 170 L 134 92 L 152 92 L 176 170 L 158 170 L 153 152 L 133 152 L 128 170 Z M 137 138 L 149 138 L 143 116 Z"/>"""),

    "games": ("a game controller with crimson buttons", f"""
  <path d="M 84 84 L 172 84 C 204 84 222 116 226 156 C 230 190 204 200 186 178 L 170 158 L 86 158 L 70 178 C 52 200 26 190 30 156 C 34 116 52 84 84 84 Z" fill="{L1}"/>
  <rect x="68" y="110" width="40" height="12" rx="6" fill="{L4}"/>
  <rect x="82" y="96" width="12" height="40" rx="6" fill="{L4}"/>
  <circle cx="168" cy="106" r="9" fill="{RED}"/>
  <circle cx="186" cy="126" r="9" fill="{RED}"/>"""),
}

# ───────────── the shell's own pictures (branding/src/apps), for programs that do the same job ─────────────
OURS = ["files", "terminal", "calculator", "calendar", "camera", "clock", "disks", "imageviewer", "notes",
        "screenshot", "recorder", "settings", "software", "taskmgr", "video", "voicememos", "paint", "messenger",
        "online", "cdplayer"]

# ───────────── icon names (as programs' .desktop Icon= give them) → picture ─────────────
# Only programs whose own icon is a picture of their job, not a brand: those keep their mark, on the tile (adaptive).
NAMES = {
    "files": "org.gnome.Nautilus nautilus system-file-manager file-manager org.kde.dolphin dolphin thunar org.xfce.thunar "
             "nemo pcmanfm pcmanfm-qt caja org.gnome.Files",
    "terminal": "org.gnome.Terminal gnome-terminal utilities-terminal org.gnome.Console kgx org.gnome.Ptyxis konsole "
                "org.kde.konsole kitty Alacritty xterm foot xfce4-terminal org.xfce.terminal terminator tilix "
                "com.gexperts.Tilix lxterminal qterminal terminal mate-terminal",
    "calculator": "org.gnome.Calculator gnome-calculator accessories-calculator kcalc org.kde.kcalc galculator "
                  "org.kde.kalk qalculate qalculate-gtk mate-calc",
    "calendar": "org.gnome.Calendar gnome-calendar x-office-calendar office-calendar korganizer org.kde.korganizer "
                "org.kde.merkuro.calendar",
    "camera": "org.gnome.Snapshot org.gnome.Cheese cheese kamoso org.kde.kamoso",
    "clock": "org.gnome.clocks gnome-clocks preferences-system-time org.kde.kclock",
    "disks": "org.gnome.DiskUtility gnome-disks gparted org.kde.partitionmanager partitionmanager",
    "imageviewer": "org.gnome.Loupe org.gnome.eog eog org.kde.gwenview gwenview ristretto org.xfce.ristretto gthumb "
                   "org.gnome.gThumb image-viewer viewnior gpicview lximage-qt shotwell org.gnome.Shotwell "
                   "org.gnome.Photos gnome-photos eom",
    "notes": "org.gnome.Notes bijiben gnote org.gnome.Gnote xpad knotes org.kde.knotes",
    "screenshot": "org.gnome.Screenshot gnome-screenshot applets-screenshooter spectacle org.kde.spectacle "
                  "xfce4-screenshooter org.xfce.screenshooter",
    "recorder": "io.github.seadve.Kooha kooha",
    "settings": "org.gnome.Settings gnome-control-center preferences-system preferences-desktop systemsettings "
                "org.kde.systemsettings xfce4-settings-manager org.xfce.settings.manager lxqt-config "
                "cinnamon-settings mate-control-center",
    "software": "org.gnome.Software gnome-software plasmadiscover org.kde.discover system-software-install "
                "system-software-update pamac pamac-manager octopi",
    "taskmgr": "org.gnome.SystemMonitor gnome-system-monitor utilities-system-monitor ksysguard "
               "org.kde.plasma-systemmonitor plasma-systemmonitor xfce4-taskmanager org.xfce.taskmanager htop btop "
               "lxtask qps org.gnome.Usage",
    "video": "org.gnome.Totem totem org.gnome.Showtime mpv io.mpv.Mpv celluloid io.github.celluloid_player.Celluloid "
             "haruna org.kde.haruna parole org.xfce.parole dragonplayer org.kde.dragonplayer multimedia-video-player",
    "voicememos": "org.gnome.SoundRecorder gnome-sound-recorder audio-recorder",
    "paint": "drawing com.github.maoschanz.drawing kolourpaint org.kde.kolourpaint mtpaint",
    "messenger": "org.gnome.Polari polari empathy konversation org.kde.konversation internet-chat",
    "online": "web-browser org.gnome.Epiphany epiphany falkon org.kde.falkon konqueror org.kde.konqueror",
    "cdplayer": "org.gnome.SoundJuicer sound-juicer brasero org.gnome.Brasero",
    "text-editor": "org.gnome.TextEditor gnome-text-editor org.gnome.gedit gedit accessories-text-editor kate "
                   "org.kde.kate kwrite org.kde.kwrite mousepad org.xfce.mousepad pluma xed featherpad leafpad "
                   "l3afpad text-editor",
    "document-viewer": "org.gnome.Evince evince org.gnome.Papers okular org.kde.okular atril zathura xreader "
                       "qpdfview document-viewer epdfview",
    "archive": "org.gnome.FileRoller file-roller ark org.kde.ark xarchiver engrampa utilities-file-archiver "
               "lxqt-archiver",
    "music": "org.gnome.Music gnome-music rhythmbox org.gnome.Rhythmbox3 lollypop org.gnome.Lollypop elisa "
             "org.kde.elisa juk org.kde.juk multimedia-audio-player audio-player",
    "mail": "org.gnome.Geary geary kmail org.kde.kmail2 kmail2 mail-client internet-mail",
    "contacts": "org.gnome.Contacts gnome-contacts x-office-address-book kaddressbook org.kde.kaddressbook",
    "maps": "org.gnome.Maps gnome-maps marble org.kde.marble",
    "weather": "org.gnome.Weather gnome-weather org.kde.kweather",
    "help": "org.gnome.Yelp yelp help-browser khelpcenter org.kde.khelpcenter system-help",
    "fonts": "org.gnome.font-viewer gnome-font-viewer font-manager org.gnome.FontManager preferences-desktop-font "
             "kfontview",
    "characters": "org.gnome.Characters gnome-characters accessories-character-map gucharmap kcharselect "
                  "org.kde.kcharselect",
    "backup": "org.gnome.DejaDup deja-dup timeshift kbackup org.kde.kbackup backintime",
    "passwords": "org.gnome.seahorse.Application seahorse kwalletmanager org.kde.kwalletmanager5 "
                 "org.kde.kwalletmanager org.gnome.World.Secrets",
    "remote-desktop": "org.gnome.Connections gnome-connections krdc org.kde.krdc vinagre "
                      "preferences-desktop-remote-desktop",
    "virtual-machines": "org.gnome.Boxes gnome-boxes virt-manager",
    "logs": "org.gnome.Logs gnome-logs ksystemlog org.kde.ksystemlog",
    "disk-usage": "org.gnome.baobab baobab filelight org.kde.filelight",
    "scanner": "org.gnome.SimpleScan simple-scan skanlite org.kde.skanlite skanpage org.kde.skanpage xsane",
    "printer": "system-config-printer printer-settings",
    "bluetooth": "blueman blueman-manager preferences-system-bluetooth",
    "network": "nm-connection-editor preferences-system-network",
    "sound": "pavucontrol org.pulseaudio.pavucontrol pavucontrol-qt multimedia-volume-control "
             "org.pipewire.Helvum helvum",
    "power": "org.gnome.PowerStats gnome-power-statistics xfce4-power-manager org.xfce.powermanager "
             "preferences-system-power",
    "code": "org.gnome.Builder gnome-builder kdevelop org.kde.kdevelop",
    "firewall": "firewall-config gufw",
    "downloads": "ktorrent org.kde.ktorrent de.haeckerfelix.Fragments fragments",
    "books": "com.github.johnfactotum.Foliate foliate org.gnome.Books gnome-books",
    "system-info": "kinfocenter org.kde.kinfocenter hardinfo hardinfo2",
    "tweaks": "org.gnome.tweaks gnome-tweaks",
    "extensions": "org.gnome.Extensions org.gnome.Shell.Extensions",
    "tasks": "org.gnome.Todo gnome-todo",
    "dictionary": "org.gnome.Dictionary gnome-dictionary accessories-dictionary",
    "games": "applications-games",
}

INDEX = """[Icon Theme]
Name=Crimson Banner
Comment=Crimson Banner OS's app icons: every program on the same graphite tile
Inherits=Adwaita,breeze,hicolor
Example=org.gnome.Nautilus
Directories={dirs}

[scalable/apps]
Size=256
MinSize=16
MaxSize=512
Type=Scalable
Context=Applications
{fixed}"""


def render(svg_path, size, png_path):
    subprocess.run(["rsvg-convert", "-w", str(size), "-h", str(size), "-o", png_path, svg_path], check=True)


def main():
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    sizes = [f"{s}x{s}/apps" for s in SIZES]
    os.makedirs(os.path.join(OUT, "scalable", "apps"))
    for d in sizes:
        os.makedirs(os.path.join(OUT, d))
    fixed = "".join(f"\n[{s}x{s}/apps]\nSize={s}\nType=Fixed\nContext=Applications\n" for s in SIZES)
    with open(os.path.join(OUT, "index.theme"), "w") as f:
        f.write(INDEX.format(dirs=",".join(["scalable/apps"] + sizes), fixed=fixed))

    pictures = {}
    for name, (what, body) in DRAWN.items():
        pictures[name] = page(body, what)
    for app in OURS:
        with open(os.path.join(APPS, app + ".svg")) as f:
            src = f.read()
        pictures[app] = re.sub(r"branding/README\.md -->", "branding/README.md; in the icon theme by branding/icon-theme.py -->", src, count=1)

    seen = {}
    for picture, names in NAMES.items():
        assert picture in pictures, picture
        for n in names.split():
            assert n not in seen, f"{n} is under both {seen[n]} and {picture}"
            seen[n] = picture

    # one file per picture ("crimson-<picture>"), every program's name a link to it
    for picture, svg in pictures.items():
        base = f"crimson-{picture}"
        svg_path = os.path.join(OUT, "scalable", "apps", base + ".svg")
        with open(svg_path, "w") as f:
            f.write(svg)
        for s in SIZES:
            render(svg_path, s, os.path.join(OUT, f"{s}x{s}", "apps", base + ".png"))
    for n, picture in sorted(seen.items()):
        os.symlink(f"crimson-{picture}.svg", os.path.join(OUT, "scalable", "apps", n + ".svg"))
        for s in SIZES:
            os.symlink(f"crimson-{picture}.png", os.path.join(OUT, f"{s}x{s}", "apps", n + ".png"))
    with open(os.path.join(OUT, "names.txt"), "w") as f:      # for the shell: which names the theme draws
        f.write("".join(f"{n}\n" for n in sorted(seen)))
    unused = sorted(set(pictures) - set(NAMES))
    print(f"icons/CrimsonBanner: {len(pictures)} pictures, {len(seen)} program names, {len(SIZES)} PNG sizes"
          + (f"; not used yet: {', '.join(unused)}" if unused else ""))


if __name__ == "__main__":
    main()
