# Reading Memory (RAM) Temperatures on This Linux PC

## November 14 update — eight DIMMs on WRX90

The current board is **ASUS Pro WS WRX90E-SAGE SE**, with eight 32 GiB DDR5 modules.
All **eight direct SPD5118 temperature sensors** are now readable in OpenFan's
**Sensors → Memory temperatures** group. The old four-module binding script only checked
addresses 0x50 and 0x52, so it missed responding hubs at 0x51 and 0x53 on both memory buses.
The kernel driver successfully validated those four additional hubs and exposed real readings.

The updated, installed `dram-temps-bind.sh` checks 0x50–0x53 only on the two verified WRX90
memory controllers, AMDI0010:01 and AMDI0010:02. It recognizes this board before probing,
skips existing devices, and preserves the previous 0x50/0x52 setup for the known TRX50 board.
The enabled `dram-temps.service` was restarted successfully; another run leaves eight devices.
This change is startup setup, separate from the app package. The script never scans GPU
or unrelated adapters or reads the SPD EEPROM. The temperature driver is documented in the
[Linux SPD5118 guide](https://docs.kernel.org/hwmon/spd5118.html).

The original four controller/address identities and their saved aliases/curve choices are
unchanged. New sensors start unchecked for **Use in curves**. Physical slot letters have
not been mapped to the direct controller/address identities.

OpenFan 0.4.6 also adds a **BMC** sensor group with slot-labeled **DIMMA1_Temp–DIMMH1_Temp**,
CPU/LAN/PCIe temperatures, and fan telemetry. BMC temperatures are reported independently
and differ from direct SPD readings; they are not substituted or merged. See
[BMC details](docs/wrx90-bmc-fans.md). After a reboot, verify **eight** spd5118 devices,
not the four mentioned in the historical TRX50 notes below.

## Historical TRX50 setup (October 2026)

## The short version

Windows programs like HWiNFO64 can show a temperature sensor on each stick of RAM. All four DDR5 SPD hub sensors now work on this Linux machine and are integrated into OpenFan.

## October 19 update — OpenFan and startup verified

Open **Sensors → Memory temperatures** in OpenFan. Each module has one row with its current,
minimum, maximum and average temperature. A separate line shows its configured alarm limits:

| Limit | Reported value on all four modules |
| --- | ---: |
| Low | 0°C |
| High | 55°C |
| Critical low | 0°C |
| Critical high | 85°C |

These are read-only alarm settings, not a manufacturer-certified safe operating range. They are
separate from the measured Min/Max columns. OpenFan does not change the limits.

Controller/address labels distinguish the four sensors; the physical DIMM slot letters are not yet
mapped. Names, fan curves and overlay tiles use stable identities that survive Linux device
renumbering. Hiding or collapsing the memory group pauses optional reads, including the limits.
Required fan-curve or overlay temperature readings continue. Hide/collapse choices save in the
active profile and restore on the next program launch.

The already-enabled `dram-temps.service` ran successfully on the October 19 boot, and all four
sensors were readable afterward. Startup binding is therefore verified on this machine. The GUI
uses the existing libsensors reader without running bus scans or privileged binding scripts.

See [working setup files](packaging/dram-temps/README.md), the
[sensor coverage audit](docs/sensor-data-audit.md), and the
[current handover](HANDOVER-2026-10-18.md) for setup details and verification. The original
October 18 notes remain in `/mnt/SOLIDIGM/Claude/dramtemps/DRAM-TEMPS-README.md`.

To see the temperatures any time, open a terminal and type:

```
sensors | grep -A2 spd5118
```

You should see four entries, one per RAM stick.

---

## What we found out about your computer

- **CPU:** AMD Ryzen Threadripper 9970X
- **RAM:** Kingston Fury Renegade Pro DDR5 (32 GB sticks, with error correction)
- **Each RAM stick has a tiny built-in thermometer chip** (called an "SPD hub"). Linux can read it, but only if it knows where to look and has the right driver turned on.

## Original October 18 setup, step by step

1. **Checked what Linux already had.** The right driver (`spd5118`) was available but switched off, and nothing was telling Linux where your RAM sensors were.
2. **Installed a small tool (`i2c-tools`).** It lets us look at the little "wires" inside the motherboard that connect to the sensors.
3. **First search: nothing found.** We searched the motherboard's main sensor connections and found no RAM thermometers. We even suspected they used a newer type of connection Linux doesn't support on this CPU.
4. **Looked up other people with similar hardware.** Someone with a similar ASUS Threadripper board had found their RAM sensors on a *different* set of connections (the "Synopsys DesignWare" ones) than we had searched.
5. **Searched those and found them.** Four sensors showed up: two on connection #0 and two on connection #1.
6. **Told Linux to use them.** We attached the `spd5118` driver to each of the four sensors. Temperatures appeared immediately.
7. **Made it survive restarts.** Without this step, the setup would disappear every time you reboot. We created a small automatic startup task that redoes it each boot.

## Files we created

Originally in `/mnt/SOLIDIGM/Claude/dramtemps/`:

| File | What it is |
|---|---|
| `dram-temps.sh` | The unsuccessful first search script; not used by OpenFan or copied into its packaging. |
| `dram-temps-bind.sh` | The script that connects Linux to the four RAM sensors. |
| `dram-temps.service` | The startup task that runs the script above every time the PC boots. |
| `DRAM-TEMPS-README.md` | This document. |

The bind script and service were installed into `/usr/local/sbin/` and `/etc/systemd/system/`
so they run automatically at startup. Copies are now included in `packaging/dram-temps/`.

## Things installed or changed on your PC

- Installed the `i2c-tools` package (a small utility).
- Turned on the `spd5118` driver.
- Added and enabled one startup task called `dram-temps.service`.

Nothing else on the system was altered.

## Checking after a future reboot

The October 19 boot succeeded. To check a later restart, run:

```
sensors | grep -A2 spd5118
```

- **Four entries shown** → everything works.
- **Missing or nothing** → run `systemctl status dram-temps.service` and share the output.

## Good to know

- **Unexpected readings:** Compare OpenFan with the kernel's `sensors` output and the hub alarm flags when investigating a suspicious sample.
- **Live view:** For a continuously updating display, run `watch -n1 "sensors | grep -A1 spd5118"`. Press Ctrl+C to stop.
- **To undo everything:** run these commands:

```
sudo systemctl disable --now dram-temps.service
sudo rm /etc/systemd/system/dram-temps.service /usr/local/sbin/dram-temps-bind.sh
```
