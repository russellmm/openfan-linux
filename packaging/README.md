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

# openfan-helper — privileged GPU fan + CPU socket power daemon (one-time setup)

The Linux NVIDIA driver gates NVML fan **writes** on root euid (reads are free), and so does the
HSMP socket power limit. `openfan-helper` runs as a small root systemd service and exposes exactly
two capabilities for members of the `openfan` group: setting/restoring NVIDIA fans, and setting the
CPU socket power limit (PPT). hwmon PWM does **not** go through it (udev ACL covers that). The GUI
never needs root. It starts even with no NVIDIA driver present, as long as `amd_hsmp_hwmon` exists.

## Install (after building the repo)

```sh
cd /mnt/8TB/deepseek-linux/projects/openfan-linux
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

Then start the OpenFan GUI — GPU cards and the CPU tab's power-limit control route through the
helper automatically (the status line stops complaining about the helper).

## Safety model

- Socket `/run/openfan/helper.sock` is `0660 root:openfan` → only group members can talk to it.
- Protocol accepts **only** `nvml:<uuid>:fan:<n>` ids with percent 0–100, `power <gpu-uuid> <W>`,
  and the CPU pair `cpupower <W>|default` / `cpuboot <W>|clear`. Nothing else — no shell, no
  arbitrary paths. CPU watts are validated to 100–300 W twice (in the protocol parser and again in
  `CpuPowerControl`, which also refuses anything above `power1_cap_max`) and every live write is
  verified by reading the attribute back, so a firmware clamp is reported instead of displayed as
  success.
- `cpuboot` writes exactly one file, `/etc/hsmp-control/ppt_mw` (or `$HSMP_LIMIT_CONFIG`), as an
  integer in milliwatts via temp-file + rename; `clear` removes it. That file is what
  `hsmp-control-apply.service` re-asserts at boot — see `tr9970x-hsmp/packaging/`. Without that
  unit, a CPU limit set here is live until reboot only, because the HSMP setting is volatile and
  firmware re-programs it from BIOS CBS at every boot.
- **Restore-on-disconnect:** each connection owns the fans it sets; if the GUI crashes or is
  killed, the helper restores those fans to driver default immediately. Power limits are
  intentionally *not* restored — neither GPU nor CPU — matching how the driver treats them.
- Helper restart / SIGTERM also drains sessions with restore.

## Uninstall

```sh
sudo systemctl disable --now openfan-helper
sudo rm /etc/systemd/system/openfan-helper.service /usr/local/lib/openfan/openfan-helper
sudo systemctl daemon-reload
```
