# OpenFan Linux — one-time privileged setup (prototype)

Everything here runs once with sudo; the OpenFan app itself never runs as root
(spec §3.4/§8). After this, `openfan-linux` writes PWM as a member of `openfan`.

## Install

```sh
sudo groupadd -f openfan
sudo usermod -aG openfan "$USER"

sudo install -d -m 755 /usr/local/lib/openfan
sudo install -m 755 pwm-acl.sh /usr/local/lib/openfan/
sudo install -m 644 99-openfan-pwm.rules /etc/udev/rules.d/
sudo install -m 644 openfan-modules-load.conf /etc/modules-load.d/openfan.conf

sudo modprobe nct6775            # load now (survives boot via modules-load.d)
sudo udevadm control --reload
sudo udevadm trigger --subsystem-match=hwmon --action=change
```

**Log out and back in** so the new group membership applies (`id` should list `openfan`).

## Verify (as your normal user, after relogin)

```sh
ls -l /sys/class/hwmon/hwmon*/pwm[0-9]      # expect: -rw-rw---- 1 root openfan
test -w /sys/class/hwmon/hwmon10/pwm4 && echo "PWM writable ✓"

# Phase 2 checkpoint: hold a case fan at 40 % for 15 s, then restore auto(5)
openfan-linux --apply-once hwmon:nct6799:10:pwm:4 40 --seconds 15
```

## Scope / safety notes

- `pwm-acl.sh` touches **only** `pwm<N>` and `pwm<N>_enable` — never the auto-point
  tables, temps, or anything else. Group write on duty+enable is what OpenFan's
  tick writer needs; nothing more.
- If a board never shows PWM nodes: the kernel lacks a driver for its SuperIO/EC —
  OpenFan degrades to GPU fans + read-only board temps (documented fork).
- Uninstall: remove the two installed files, `sudo groupdel openfan` (after removing
  members), modes revert on next boot.

---

# openfan-helper — privileged GPU fan daemon (one-time setup)

The Linux NVIDIA driver gates NVML fan **writes** on root euid (reads are free).
`openfan-helper` runs as a small root systemd service and exposes exactly one
capability: setting/restoring NVIDIA fans, for members of the `openfan` group.
hwmon PWM does **not** go through it (udev ACL covers that).

## Install (after building the repo)

```sh
cd /run/media/russellm/8TB/deepseek-linux/projects/openfan-linux
dotnet publish src/OpenFan.Linux.Helper -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o /tmp/openfan-helper-build
sudo install -m 755 /tmp/openfan-helper-build/openfan-helper /usr/local/lib/openfan/
sudo install -m 644 packaging/openfan-helper.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openfan-helper
```

## Verify

```sh
systemctl status openfan-helper --no-pager | head -5
ls -l /run/openfan/helper.sock        # srw-rw---- 1 root openfan
# as your normal user:
python3 -c "print('socket visible:', __import__('os').path.exists('/run/openfan/helper.sock'))"
```

Then start the OpenFan GUI — GPU cards route through the helper automatically
(the status line stops complaining about the helper).

## Safety model

- Socket `/run/openfan/helper.sock` is `0660 root:openfan` → only group members can talk to it.
- Protocol accepts **only** `nvml:<uuid>:fan:<n>` ids with percent 0–100; nothing else —
  no shell, no file paths, no power limits (yet).
- **Restore-on-disconnect:** each connection owns the fans it sets; if the GUI crashes or is
  killed, the helper restores those fans to driver default immediately.
- Helper restart / SIGTERM also drains sessions with restore.

## Uninstall

```sh
sudo systemctl disable --now openfan-helper
sudo rm /etc/systemd/system/openfan-helper.service /usr/local/lib/openfan/openfan-helper
sudo systemctl daemon-reload
```
