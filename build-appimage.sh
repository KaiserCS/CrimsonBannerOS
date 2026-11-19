#!/usr/bin/env bash
# Builds RedStarOS-<arch>.AppImage from source.
#
# Requirements (build machine): .NET 8 SDK, curl (or an existing appimagetool), a Linux host.
# Usage:   ./build-appimage.sh            # x86_64
#          ./build-appimage.sh arm64      # aarch64
set -euo pipefail

cd "$(dirname "$0")"
ARCH_ARG="${1:-x64}"
case "$ARCH_ARG" in
  x64|x86_64|amd64)  RID=linux-x64;   AI_ARCH=x86_64 ;;
  arm64|aarch64)     RID=linux-arm64; AI_ARCH=aarch64 ;;
  *) echo "Unknown architecture '$ARCH_ARG' (use x64 or arm64)"; exit 1 ;;
esac

OUT="$PWD/dist"
APPDIR="$OUT/RedStarOS.AppDir"
rm -rf "$OUT"
mkdir -p "$APPDIR/usr/bin"

echo "==> Publishing self-contained build ($RID)"
dotnet publish RedStarOS/RedStarOS.csproj \
  -c Release -r "$RID" --self-contained true \
  -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false \
  -o "$APPDIR/usr/bin"

echo "==> Assembling AppDir"
cp packaging/AppRun "$APPDIR/AppRun"
cp packaging/redstaros.desktop "$APPDIR/redstaros.desktop"
cp packaging/redstaros.png "$APPDIR/redstaros.png"
cp packaging/redstaros.png "$APPDIR/.DirIcon"
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/RedStarOS"

# Red Star Online runs on Electron. The Electron binary (~200 MB) is downloaded by npm.
# Set SKIP_ELECTRON=1 to build a smaller AppImage without the browser.
if [ "${SKIP_ELECTRON:-0}" != "1" ]; then
  if ! command -v npm >/dev/null 2>&1; then
    echo "npm was not found. Install Node.js (e.g. 'sudo pacman -S nodejs npm') or run with SKIP_ELECTRON=1."
    exit 1
  fi
  echo "==> Installing Electron browser"
  BROWSER_DST="$APPDIR/usr/lib/redstar-browser"
  mkdir -p "$BROWSER_DST"
  cp browser/package.json browser/main.js browser/preload.js browser/index.html browser/renderer.js browser/icon.png browser/setup.sh "$BROWSER_DST/"
  # Shared shell bridge: lets the Electron apps appear in the taskbar / app tray and follow the theme.
  SHARED_DST="$APPDIR/usr/lib/redstar-shared"
  mkdir -p "$SHARED_DST"
  cp shared/shell-bridge.js "$SHARED_DST/"
  # setup.sh runs npm install and, if npm blocks or skips Electron's installer, downloads the binary itself.
  bash "$BROWSER_DST/setup.sh"
  if [ ! -x "$BROWSER_DST/node_modules/electron/dist/electron" ]; then
    echo "Electron binary was not downloaded. Check your network connection, or build with SKIP_ELECTRON=1."
    exit 1
  fi

  # Red Star Video is native (LibVLC); its Electron version is packaged only as a fallback for machines without libvlc.
  echo "==> Adding Red Star Video"
  VIDEO_DST="$APPDIR/usr/lib/redstar-video"
  mkdir -p "$VIDEO_DST"
  cp video/package.json video/main.js video/preload.js video/helpers.js video/index.html \
     video/renderer.js video/style.css video/icon.png "$VIDEO_DST/"

  # Native browser engine: headless Chromium page renderer (reuses the Electron binary installed above).
  echo "==> Adding the web engine"
  ENGINE_DST="$APPDIR/usr/lib/redstar-webengine"
  mkdir -p "$ENGINE_DST"
  cp webengine/package.json webengine/main.js webengine/adblock.js webengine/filters-builtin.txt "$ENGINE_DST/"

  # Red Star Terminal is native now (RedStarOS/Views/TerminalWindow): nothing to package here.
fi

TOOL="${APPIMAGETOOL:-}"
if [ -z "$TOOL" ]; then
  if command -v appimagetool >/dev/null 2>&1; then
    TOOL="$(command -v appimagetool)"
  else
    TOOL="$OUT/appimagetool-$AI_ARCH.AppImage"
    echo "==> Downloading appimagetool"
    curl -fsSL -o "$TOOL" \
      "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$(uname -m).AppImage"
    chmod +x "$TOOL"
  fi
fi

echo "==> Creating AppImage"
# Optional self-update support: GH_REPO=user/repo ./build-appimage.sh
# embeds update information so AppImageUpdate can fetch deltas from GitHub releases
# (needs 'zsyncmake' installed to also produce the .zsync file).
UPDATE_ARGS=()
if [ -n "${GH_REPO:-}" ]; then
  UPDATE_ARGS=(-u "gh-releases-zsync|${GH_REPO%/*}|${GH_REPO#*/}|latest|RedStarOS-*${AI_ARCH}.AppImage.zsync")
fi

# --appimage-extract-and-run lets appimagetool work on machines/CI without FUSE.
(
  cd "$OUT"
  ARCH="$AI_ARCH" "$TOOL" --appimage-extract-and-run "${UPDATE_ARGS[@]}" RedStarOS.AppDir "RedStarOS-$AI_ARCH.AppImage" \
    || ARCH="$AI_ARCH" "$TOOL" "${UPDATE_ARGS[@]}" RedStarOS.AppDir "RedStarOS-$AI_ARCH.AppImage"
)

echo
echo "Done: $OUT/RedStarOS-$AI_ARCH.AppImage"
echo "Run:  chmod +x \"$OUT/RedStarOS-$AI_ARCH.AppImage\" && \"$OUT/RedStarOS-$AI_ARCH.AppImage\""
