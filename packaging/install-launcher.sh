#!/usr/bin/env bash
# Creates a rebuild-proof launcher for openfan:
#   ~/.local/bin/openfan          wrapper that finds the newest built apphost
#   menu entry + optional desktop icon, both Exec= that wrapper
#
# Rebuilds never need the shortcut touched. Safe to re-run. No root.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP_BIN="$REPO/src/OpenFan.Linux.App/bin"
WRAPPER="$HOME/.local/bin/openfan"

mkdir -p "$HOME/.local/bin"
cat > "$WRAPPER" <<EOF
#!/bin/sh
# Stable launcher for openfan-linux, so desktop/menu shortcuts survive rebuilds.
# Chooses the most recently built apphost (Release preferred on ties).
# Override explicitly:  OPENFAN_BIN=/path/to/openfan openfan
set -e

APP_BIN="$APP_BIN"
REPO="$REPO"

if [ -n "\${OPENFAN_BIN:-}" ]; then
    [ -x "\$OPENFAN_BIN" ] || { echo "openfan: OPENFAN_BIN is not executable: \$OPENFAN_BIN" >&2; exit 1; }
    exec "\$OPENFAN_BIN" "\$@"
fi

chosen=""; newest=0
for cand in "\$APP_BIN/Release/net8.0/openfan" "\$APP_BIN/Debug/net8.0/openfan"; do
    [ -x "\$cand" ] || continue
    m=\$(stat -c %Y "\$cand")
    [ "\$m" -gt "\$newest" ] && { newest=\$m; chosen=\$cand; }
done

if [ -z "\$chosen" ]; then
    echo "openfan: no built binary found under \$APP_BIN" >&2
    echo "  build one first:  cd \$REPO && dotnet build src/OpenFan.Linux.App" >&2
    exit 1
fi

# Diagnostics for the shortcut itself, without starting the GUI.
[ "\${1:-}" = "--launcher-path" ] && { echo "\$chosen"; exit 0; }

exec "\$chosen" "\$@"
EOF
chmod 755 "$WRAPPER"

# Icons + menu entry. install-icons.sh takes the Exec target explicitly, so pass the wrapper —
# its own default is a raw Debug path, which would defeat the point of this script.
if [ -x "$REPO/packaging/install-icons.sh" ]; then
    "$REPO/packaging/install-icons.sh" "$WRAPPER" || {
        echo "install-icons.sh failed (ImageMagick missing?) — writing the menu entry directly" >&2
        mkdir -p "$HOME/.local/share/applications"
        cat > "$HOME/.local/share/applications/openfan.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=OpenFan
Comment=Fan control and sensor monitoring (Threadripper PPT, GPU fans)
Exec=$WRAPPER
Terminal=false
Icon=openfan
StartupWMClass=openfan
Categories=System;Monitor;
Keywords=fan;cooling;thermal;ppt;tjmax;cpu;gpu;
EOF
    }
fi

# Desktop icon. GNOME refuses to launch an untrusted .desktop, so mark it.
DESKTOP_DIR="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
if [ -d "$DESKTOP_DIR" ]; then
    cp "$HOME/.local/share/applications/openfan.desktop" "$DESKTOP_DIR/openfan.desktop"
    chmod 755 "$DESKTOP_DIR/openfan.desktop"
    gio set "$DESKTOP_DIR/openfan.desktop" metadata::trusted true \
        || echo "note: could not mark trusted (gio unavailable) — right-click ▸ Allow Launching on the icon"
fi

echo "launcher: $WRAPPER  →  $("$WRAPPER" --launcher-path)"
command -v desktop-file-validate >/dev/null &&
    desktop-file-validate "$HOME/.local/share/applications/openfan.desktop" &&
    echo "menu entry validates"
