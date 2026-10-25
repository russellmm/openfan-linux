# Installed OpenFan on Ubuntu

The Debian package installs the self-contained app on the Linux system drive. Running it does
not require the source checkout, an external development drive, or a .NET SDK/runtime installation.
Native Linux dependencies are declared in the package; NVIDIA features also need the installed
NVIDIA driver/NVML. Hardware-specific PWM permissions, DDR5 binding and optional CPU PPT boot
setup remain separate; see [packaging setup](../packaging/README.md).

## Build and install

For a ready-built installer, use [GitHub Releases](https://github.com/russellmm/openfan-linux/releases).
Download `openfan_0.4.1_amd64.deb` and `SHA256SUMS` into the same directory, then verify and install:

```sh
sha256sum -c SHA256SUMS
sudo apt install ./openfan_0.4.1_amd64.deb
sudo usermod -aG openfan "$USER"
```

Log out and back in if this added a new group membership. The release is tested on Ubuntu 26.10
amd64. Other Debian/Ubuntu versions must satisfy the declared dependencies and are not yet verified.

From the source checkout, as a normal user:

```sh
bash packaging/build-deb.sh "$HOME/Downloads/OpenFan"
```

The builder publishes both app and helper, includes the native graphics libraries and icon,
and creates an amd64 `.deb`. An optional second argument sets the Debian package version.
Builds happen in a temporary directory and do not replace a running app. Close OpenFan, then
install the resulting package using Ubuntu's installer or:

```sh
sudo apt install ./openfan_VERSION_amd64.deb
```

For an older manually installed helper, first inspect `/etc/systemd/system/openfan-helper.service`.
An old full unit there overrides the package's unit. Back up and remove the old stock unit before
installation, retaining any intentional local customizations as a reviewed service override.
The package's helper path is `/usr/lib/openfan/openfan-helper`; it starts and enables the service
on installation. Existing user membership in `openfan` is preserved. On a new machine add the user
to that group and log in again, following the hardware setup instructions.

## Installed files

| Location | Purpose |
| --- | --- |
| `/opt/openfan/app/` | Published app and native graphics libraries |
| `/usr/bin/openfan` | Stable launcher, independent of the checkout |
| `/usr/lib/openfan/openfan-helper` | Privileged helper |
| `/usr/lib/systemd/system/openfan-helper.service` | Packaged system service |
| `/usr/share/applications/openfan.desktop` | System application-menu entry |
| `/usr/share/icons/hicolor/512x512/apps/openfan.png` | Application icon |
| `/usr/share/doc/openfan/` | Guides and source/build identification |

OpenFan continues using the current user profile and `~/.config/openfan/active`; installing or
upgrading does not replace profiles, calibration, curves or sensor preferences. Autostart is a
per-user choice. The app preserves the configured menu command when writing its autostart entry,
including a custom RTX GPU routing wrapper.

On this workstation, the RTXinVM `openfan` app registration targets `/opt/openfan/app/openfan`.
The existing `rtxgpu --app openfan` menu/autostart route stays in place. The personal terminal
launcher and desktop shortcut use that route too. This keeps the established GPU access rules
while eliminating development-directory paths. A system `/usr/bin/openfan` launch is also available.

`openfan --launcher-path` reports the installed executable without starting a second instance.
`OPENFAN_BIN` remains an explicit development override for the system launcher; normal launches
use the installed build. Do not rerun the old development-only `install-launcher.sh` after migrating.

## Updates, verification and removal

Close the app before installing a replacement package; the helper restarts during installation.
Reopen using the normal menu or autostart command. Verify the installed package and helper:

```sh
dpkg-query -W openfan
openfan --launcher-path
systemctl status openfan-helper.service
```

Use `sudo apt remove openfan` to remove the packaged app/helper. User profiles and separate
hardware-binding/PPT services are retained. No CPU frequency/power policy or fan calibration is
changed by packaging. The documented CPU boost-clamp fix remains a separate pending task.
