#!/usr/bin/env bash
# Builds RedStarOS-<arch>.AppImage from source.
#
# Requirements (build machine): .NET 8 SDK, curl (or an existing appimagetool), a Linux host; for Red Star Online,
# webkitgtk-6.0, gtk4, json-glib, libsoup3 and meson.
# Usage:   ./build-appimage.sh            # x86_64
#          ./build-appimage.sh arm64      # aarch64
set -euo pipefail
# no telemetry from the .NET tools (PRINCIPLES.md, 5)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

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

# packages from Red Star's own feed when it's filled (tools/packages.sh), else from nuget.org
SOURCE=()
FEED="$(tools/packages.sh where)"
if [ -n "$(ls -A "$FEED" 2>/dev/null)" ] && [ -f RedStarOS/packages.lock.json ] && [ "${REDSTAR_ONLINE_PACKAGES:-0}" != 1 ]; then
  echo "==> Packages from Crimson Banner's own feed ($FEED)"
  SOURCE=(--source "$FEED" -p:NuGetAudit=false -p:REDSTAR_LOCKED=1)
fi

echo "==> Publishing self-contained build ($RID)"
dotnet publish RedStarOS/RedStarOS.csproj "${SOURCE[@]}" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false \
  -o "$APPDIR/usr/bin"

echo "==> Assembling AppDir"
cp packaging/AppRun "$APPDIR/AppRun"
cp packaging/redstaros.desktop "$APPDIR/redstaros.desktop"
cp packaging/redstaros.png "$APPDIR/redstaros.png"
cp packaging/redstaros.png "$APPDIR/.DirIcon"
chmod +x "$APPDIR/AppRun" "$APPDIR/usr/bin/RedStarOS"

# Red Star Online (browser/, GTK 4 + WebKitGTK) is built and copied in by the publish step above when its libraries
# are installed. WebKitGTK, GTK and GStreamer come from the system, not the AppImage.
if [ -x "$APPDIR/usr/bin/redstar-online" ]; then
  echo "==> Crimson Online included"
else
  echo "==> Crimson Online not included (install webkitgtk-6.0 gtk4 json-glib libsoup3 meson to build it)"
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
