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

## Environment overrides

`OPENFAN_HELPER_SOCKET` relocates the socket for **both** ends — the daemon and `NvmlHelperClient` — so tests (and
parallel instances) can use a private socket instead of `/run/openfan/helper.sock`.

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
never needs root. It starts even with no NVIDIA driver present, as long as `amd_hsmp_hwmon` exists — and if
**neither** NVML nor `amd_hsmp_hwmon` is available it exits with status 2 ("helper has no job") rather than serving a
socket that can do nothing.

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
  `hsmp-control-apply.service` re-asserts at boot — the unit, the CLI it runs and a verifying
  installer all live in this repo under [`tools/hsmp-control/`](../tools/hsmp-control/README.md):

  ```sh
  # Applies the limit IMMEDIATELY as well as at every later boot — pass your current value
  # (`./tools/hsmp-control/hsmp-control limits`) if you want persistence without changing today's behaviour.
  sudo tools/hsmp-control/packaging/install-persistence.sh 285   # installs + enables + verifies
  ```

  Without that unit, a CPU limit set here is live until reboot only, because the HSMP setting is
  volatile and firmware re-programs it from BIOS CBS at every boot. The app detects whether the unit
  is installed: if it is missing, *keep after reboot* reports an amber warning rather than claiming
  persistence that cannot happen.
- **Restore-on-disconnect:** each connection owns the fans it sets; if the GUI crashes or is
  killed, the helper restores those fans to driver default immediately. Power limits are
  intentionally *not* restored — neither GPU nor CPU — matching how the driver treats them.
- Helper restart / SIGTERM drains sessions with restore. Cancellation of an in-flight read while a
  client is connected is treated as normal shutdown, not a fault (see `ServeClientAsync`), so
  `systemctl stop openfan-helper` with the GUI open completes and removes the socket file.

## After changing the helper's code — reinstall it

The running daemon is a **published binary**, not the repo tree. Editing `HelperProtocol.cs`,
`CpuPowerControl.cs` or anything else in `OpenFan.Linux.Hw` changes the app's expectations but not
the installed daemon, and the failure mode is confusing: the GUI reports `err unknown command` for
commands the source clearly implements. This actually happened during development — a new CPU power
command was refused because the daemon predated it. Re-run the install block above and restart:

```sh
sudo systemctl restart openfan-helper
# confirm the daemon knows the commands, without changing any limit (out-of-window value is refused):
python3 - <<'PY'
import socket
s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM); s.connect("/run/openfan/helper.sock")
f = s.makefile("rw", buffering=1)
f.write("cpupower 999\n"); print(f.readline().strip())   # expect: err watts must be 100-300
f.write("quit\n"); f.readline()
PY
```

`openfan-helper` logs its capabilities at startup (`journalctl -u openfan-helper -n 3`), including
whether CPU power control is available.

## Uninstall

```sh
sudo systemctl disable --now openfan-helper
sudo rm /etc/systemd/system/openfan-helper.service /usr/local/lib/openfan/openfan-helper
sudo systemctl daemon-reload

# and, if CPU boot persistence was installed:
sudo tools/hsmp-control/packaging/install-persistence.sh --remove
```
