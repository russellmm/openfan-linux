#!/usr/bin/env bash
# Installs the OpenFan application icon into the user's hicolor theme and registers
# a menu/dock entry. Sizes are rendered from Assets/openfan.png (512px master).
# Usage: install-icons.sh [path-to-openfan-executable]
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
MASTER="$REPO/src/OpenFan.Linux.App/Assets/openfan.png"
ICO="$REPO/src/OpenFan.Linux.App/Assets/OpenFan.ico"
# Pass the Exec target explicitly when a stable launcher exists (see install-launcher.sh);
# without one, prefer ~/.local/bin/openfan if present so re-running this never downgrades the
# menu entry to a build-specific path.
EXEC_PATH="${1:-$( [ -x "$HOME/.local/bin/openfan" ] && echo "$HOME/.local/bin/openfan" || echo "$REPO/src/OpenFan.Linux.App/bin/Debug/net8.0/openfan" )}"

[ -f "$MASTER" ] || { echo "missing master icon: $MASTER" >&2; exit 1; }

command -v convert >/dev/null && RENDER="convert" || RENDER="magick"

for size in 16 24 32 48 64 128 256 512; do
  dir="$HOME/.local/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$dir"
  $RENDER "$MASTER" -resize "${size}x${size}" "$dir/openfan.png"
done

# Full-color .ico alongside, for themes that prefer it.
if [ -f "$ICO" ]; then
  mkdir -p "$HOME/.local/share/icons/hicolor/256x256/apps"
  cp "$ICO" "$HOME/.local/share/icons/hicolor/256x256/apps/openfan.ico"
fi

mkdir -p "$HOME/.local/share/applications"
cat > "$HOME/.local/share/applications/openfan.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=OpenFan
Comment=Fan control and sensor monitoring (Threadripper PPT, GPU fans)
Exec=$EXEC_PATH
Terminal=false
Icon=openfan
StartupWMClass=openfan
Categories=System;Monitor;
Keywords=fan;cooling;thermal;ppt;tjmax;cpu;gpu;
EOF

# Autostart entry (if present) may predate the Icon field — refresh it in place.
AUTOSTART="$HOME/.config/autostart/openfan.desktop"
if [ -f "$AUTOSTART" ] && ! grep -q '^Icon=' "$AUTOSTART"; then
  sed -i '/^Terminal=/a Icon=openfan\nStartupWMClass=openfan' "$AUTOSTART"
fi

gtk-update-icon-cache -q "$HOME/.local/share/icons/hicolor" 2>/dev/null || true
echo "icons installed (hicolor 16-512 + openfan.desktop, Exec=$EXEC_PATH)"
