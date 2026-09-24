# OpenFan Linux

Native Ubuntu fan-control app for TRX50-class workstations — motherboard/case/AIO fans via **hwmon** and NVIDIA
GPU fans + power limits via **NVML**, with a curve library (Flat / Graph / Mix) modeled on the Windows
[OpenFan](https://github.com/ScriptHamster/OpenFan) program. Design references in [`screenshots/`](screenshots/)
(Windows OpenFan captures); full build spec in [`openfan-ubuntu.md`](openfan-ubuntu.md).

Built with Avalonia 11 / .NET 8, FluentTheme dark. Currently developed and daily-driven on Ubuntu 26.04 +
Threadripper 9970X + RTX 5060 Ti / 2× RTX PRO 6000 Blackwell (driver 595.x).

## Requirements

- **lm-sensors** (recommended): `sudo apt install lm-sensors && sudo sensors-detect` — loads the board's
  hwmon drivers (nct6772, asucec, …). OpenFan reads sysfs directly like lm-sensors does; the package itself
  is the convenient way to get the kernel modules configured.
- **CPU page**: package power + PPT cap come from the `amd_hsmp` kernel module (automatic on TRX50 /
  Threadripper PRO); temperature, load and frequency work from plain hwmon/cpufreq even without it.
- NVIDIA GPU features need the proprietary driver with NVML (`nvidia-smi` working).

## Features

- **CPU page** — live package temp, power draw (PPT), PPT limit, load % and boost frequency for the whole
  socket, sourced from `amd_hsmp`, board hwmon, `/proc/stat` and cpufreq.

- **Home** — every PWM control as a card: click-to-rename, curve assignment dropdown, live duty %, tach RPM,
  per-fan manual calibration window (auto-step sweep with avoid-band support).
- **Curve library** — curves are first-class named objects; assign one curve to many fans.
  - *Flat*: fixed % · *Graph*: temperature→% points with hysteresis + live mini-preview · *Mix*: max/min/average of other curves
  - Save/load whole setups as portable JSON profiles.
- **GPUs page** — per card: utilization + VRAM bars, temp / power draw-vs-limit / P-state / fan % / RPM / clocks,
  PCIe generation × width with live RX/TX throughput, and a live process table (name · PID · compute/graphics · VRAM).
  Power-limit editor per GPU (applied via the root helper, persisted, reapplied at startup).
- **Sensors page** — full sensor inventory grouped by source (CPU / GPU / Motherboard / Chipset EC / AMD HSMP /
  NVMe / Network), collapsible per-device subgroups (NVMe drives labeled with their model), and friendly renaming:
  aliases flow into every curve dropdown while tooltips keep the original hardware name + id.
- Quiet by default: monitor-only until you tick **Apply curves**; exact pre-takeover PWM enable mode restored on exit.

## Architecture

```
src/OpenFan.Core/         portable engine: curves, control loop, settings store (no Linux deps)
src/OpenFan.Linux.Hw/     hwmon sysfs backend, NVML backend (+telemetry), helper socket client
src/OpenFan.Linux.Helper/ privileged daemon: fan writes + power limits over /run/openfan/helper.sock
src/OpenFan.Linux.App/    Avalonia UI (pages, calibration window, tray)
```

Privilege model: PWM write access via a udev rule (`openfan` group); NVML GPU writes go through
`openfan-helper.service` running as root over a `0660` socket (newline protocol:
`ping | set <id> <pct> | default <id> | power <uuid> <watts> | quit`). The helper restores any fan it took over
if the app disconnects — GPU power limits intentionally persist. See [`packaging/README.md`](packaging/README.md)
for the install chain (udev rule, modules-load, helper service).

## Build & run

```bash
dotnet build && dotnet test          # 91 tests
./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan
```

Without the helper/udev setup installed, the app still runs read-mostly (monitor + UI) and reports write failures
on the affected cards. CLI smoke tools: `openfan-linux --dump`, `--procs`.

## Known limitations

- **GPU fan RPM is unavailable on driver 595.x** (`nvmlDeviceGetFanSpeedRPM` fails; no nvidia hwmon chip). Duty %
  is shown instead; if a future driver makes the RPM call succeed, RPM columns light up automatically.
- Theme/Tray/Settings pages are stubs; autostart and sleep/resume restore are on the roadmap (see STATUS.md §7).

## Status

Working daily-driver on the target machine; see [`STATUS.md`](STATUS.md) for the current handover state,
verified-hardware notes, and the gotchas catalog. MIT-style do-what-you-want for now; licensing TBD.
