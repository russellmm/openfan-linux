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
