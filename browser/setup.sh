#!/usr/bin/env bash
# Installs the Electron binary used by Red Star Online.
# Works even when npm blocks Electron's postinstall script or its installer exits silently.
set -euo pipefail
cd "$(dirname "$0")"

npm install --omit=dev --no-audit --no-fund

EL=node_modules/electron
if [ -x "$EL/dist/electron" ]; then
  echo "Electron is already installed."
  exit 0
fi

# Electron's own installer (may do nothing on very new Node versions).
node "$EL/install.js" || true
if [ -x "$EL/dist/electron" ]; then
  echo "Electron installed."
  exit 0
fi

echo "Electron's installer did not produce the binary; downloading it directly..."
VERSION="$(node -p "require('./$EL/package.json').version")"
case "$(uname -m)" in
  x86_64)        ARCH=x64 ;;
  aarch64|arm64) ARCH=arm64 ;;
  armv7l)        ARCH=armv7l ;;
  *) echo "Unsupported CPU architecture: $(uname -m)"; exit 1 ;;
esac

ZIP="electron-v${VERSION}-linux-${ARCH}.zip"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
curl -fL --retry 3 -o "$TMP/$ZIP" "https://github.com/electron/electron/releases/download/v${VERSION}/${ZIP}"

mkdir -p "$EL/dist"
if command -v unzip >/dev/null 2>&1; then
  unzip -q -o "$TMP/$ZIP" -d "$EL/dist"
elif command -v bsdtar >/dev/null 2>&1; then
  bsdtar -xf "$TMP/$ZIP" -C "$EL/dist"
else
  echo "Please install 'unzip' and run this script again."; exit 1
fi

printf 'electron' > "$EL/path.txt"
chmod +x "$EL/dist/electron"
if [ -x "$EL/dist/electron" ]; then
  echo "Electron $VERSION installed."
else
  echo "Electron download failed."; exit 1
fi
