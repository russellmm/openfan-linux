#!/usr/bin/env bash
# Build a self-contained, root-owned Debian package without installing anything.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
output=${1:-"$repo/publish/packages"}
app_version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$repo/src/OpenFan.Linux.App/OpenFan.Linux.App.csproj" | head -n 1)
version=${2:-"${app_version:?Missing app version}+$(date -u +%Y%m%d).git$(git -C "$repo" rev-parse --short HEAD)"}
dpkg --validate-version "$version"
mkdir -p "$output"
output=$(cd "$output" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/openfan-package.XXXXXXXX")
trap 'rm -rf -- "$work"' EXIT
stage="$work/root"
mkdir -p "$stage/DEBIAN" "$stage/opt/openfan/app" "$stage/usr/lib/openfan" \
    "$stage/usr/bin" "$stage/usr/lib/systemd/system" "$stage/usr/share/applications" \
    "$stage/usr/share/icons/hicolor/512x512/apps" "$stage/usr/share/doc/openfan"

dotnet publish "$repo/src/OpenFan.Linux.App" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$work/app"
dotnet publish "$repo/src/OpenFan.Linux.Helper" -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "$work/helper"
for file in "$work/app/"*; do
    [ -f "$file" ] || continue
    install -m 644 "$file" "$stage/opt/openfan/app/"
done
chmod 755 "$stage/opt/openfan/app/openfan"
install -m 755 "$work/helper/openfan-helper" "$stage/usr/lib/openfan/openfan-helper"
install -m 755 "$repo/packaging/openfan-launcher" "$stage/usr/bin/openfan"
install -m 644 "$repo/packaging/openfan.desktop" "$stage/usr/share/applications/openfan.desktop"
install -m 644 "$repo/src/OpenFan.Linux.App/Assets/openfan.png" "$stage/usr/share/icons/hicolor/512x512/apps/openfan.png"
install -m 644 "$repo/LICENSE" "$stage/usr/share/doc/openfan/copyright"
sed -e 's|https://github.com/russellmm/OpenFan|https://github.com/russellmm/openfan-linux|' \
    -e 's|After=multi-user.target|After=systemd-udevd.service|' \
    -e 's|/usr/local/lib/openfan/openfan-helper|/usr/lib/openfan/openfan-helper|' \
    "$repo/packaging/openfan-helper.service" > "$stage/usr/lib/systemd/system/openfan-helper.service"
for file in README.md docs/*.md packaging/README.md packaging/dram-temps/README.md CPU-BOOST-CLAMP-README.md DRAM-TEMPS-README.md; do
    install -D -m 644 "$repo/$file" "$stage/usr/share/doc/openfan/$file"
done
# Preserve bundled dependencies' license files and NuGet license metadata.
python3 - "$repo/src/OpenFan.Linux.App/obj/project.assets.json" "$stage/usr/share/doc/openfan/third-party" <<'PY'
import json, pathlib, shutil, sys
assets = json.loads(pathlib.Path(sys.argv[1]).read_text())
output = pathlib.Path(sys.argv[2])
libraries = dict(assets['libraries'])
for framework in assets.get('project', {}).get('frameworks', {}).values():
    for dependency in framework.get('downloadDependencies', []):
        version = dependency['version'].strip('[]').split(',')[0]
        name = dependency['name'] + '/' + version
        libraries[name] = {'type': 'package', 'path': name.lower()}
for name, library in libraries.items():
    if library.get('type') != 'package':
        continue
    for folder in assets['packageFolders']:
        package = pathlib.Path(folder) / library['path']
        if not package.is_dir():
            continue
        for item in package.iterdir():
            title = item.name.lower()
            if item.is_file() and (title.endswith('.nuspec') or title.startswith(('license', 'licence', 'copyright', 'third-party-notice'))):
                target = output / name.replace('/', '-') / item.name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(item, target)
                target.chmod(0o644)
        break
PY
{
    printf 'Package version: %s\nSource commit: %s\n' "$version" "$(git -C "$repo" rev-parse HEAD)"
    if [ -n "$(git -C "$repo" status --porcelain)" ]; then
        printf 'Source contains local changes at build time.\n'
    fi
} > "$stage/usr/share/doc/openfan/build-info.txt"
chmod 644 "$stage/usr/lib/systemd/system/openfan-helper.service" "$stage/usr/share/doc/openfan/build-info.txt"
size=$(du -sk "$stage" | cut -f1)
cat > "$stage/DEBIAN/control" <<EOF
Package: openfan
Version: $version
Architecture: amd64
Maintainer: OpenFan Linux <russellmm@users.noreply.github.com>
Section: utils
Priority: optional
Installed-Size: $size
Depends: libc6 (>= 2.35), libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libfreetype6, libx11-6, libice6, libsm6, libxext6, libxrender1, libxrandr2, libxi6, libxcursor1, libxfixes3, libsensors5, libicu78 | libicu77 | libicu76 | libicu74 | libicu72, libssl3t64 | libssl3, passwd
Recommends: lm-sensors
Homepage: https://github.com/russellmm/openfan-linux
Description: Fan control and hardware monitoring for Linux workstations
 Self-contained OpenFan desktop app and privileged GPU/CPU/NVMe helper.
 User settings and hardware-specific setup are preserved separately.
EOF
cat > "$stage/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = configure ]; then
    getent group openfan >/dev/null || groupadd --system openfan
    if [ -d /run/systemd/system ]; then
        systemctl daemon-reload
        # A migrated manual install can leave a dangling link to its former /etc unit.
        wants=/etc/systemd/system/multi-user.target.wants/openfan-helper.service
        if [ -L "$wants" ] && [ ! -e "$wants" ]; then
            systemctl reenable openfan-helper.service
        else
            systemctl enable openfan-helper.service
        fi
        systemctl restart openfan-helper.service
    fi
    if command -v update-desktop-database >/dev/null; then update-desktop-database -q || true; fi
    if command -v gtk-update-icon-cache >/dev/null; then gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true; fi
fi
EOF
cat > "$stage/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = remove ] || [ "$1" = deconfigure ]; then
    if [ -d /run/systemd/system ]; then
        systemctl stop openfan-helper.service
        systemctl disable openfan-helper.service
    fi
fi
EOF
cat > "$stage/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if [ -d /run/systemd/system ]; then systemctl daemon-reload; fi
if command -v update-desktop-database >/dev/null; then update-desktop-database -q || true; fi
EOF
chmod 755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm" "$stage/DEBIAN/postrm"
find "$stage" -type d -exec chmod 755 {} +
dpkg-deb --root-owner-group --build "$stage" "$output/openfan_${version}_amd64.deb"
printf 'Package: %s/openfan_%s_amd64.deb\n' "$output" "$version"
