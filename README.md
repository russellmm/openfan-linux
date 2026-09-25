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

- **CPU page** — live package temp, socket power draw, PPT cap, TDP, TjMax, load % and boost frequency for the
  whole socket, plus a **PPT limit editor** (100–300 W) with a *keep after reboot* option. Sourced from `amd_hsmp`,
  the BIOS CBS variable, board hwmon, `/proc/stat` and cpufreq. TDP and TjMax are shown read-only: this firmware
  will not let the OS write them.

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
- **Desktop overlay (HUD)** — an always-on-top, borderless strip of live sensor tiles, HWiNFO64-style. Configured on
  the **Tray** page: pick any temperature sensor plus CPU socket power / cap and each GPU's board power, give every
  tile its own background colour (eight curated swatches or a full RGB picker with hex paste), **click any tile's name
  to rename it** (the tooltip names the sensor and its PCI bus — "NVIDIA RTX PRO 6000 Blackwell Workstation Edition
  E1:00.0 (GPU 3) — board power" — with the raw id underneath, so two identical cards are tellable apart), NVMe entries named by drive model and PCI address
  (`a9:00.0 Composite · WD_BLACK SN850X 8000GB`, same source as the Sensors page headings), reorder them, choose tiles per row and overall size
  (XSmall / Small / Normal / Large / Huge), and decide
  whether it stays above other windows. Text colour is derived from the tile colour's luminance so it stays readable whatever you pick; drag it
  anywhere and it remembers where you left it (and if that position stops being reachable — a monitor goes away — it
  comes back on screen, or use right-click ▸ **Reset position**). Right-click also hides it or changes the layout.
  Read-only — the overlay never writes anything.
- Quiet by default: monitor-only until you tick **Apply curves**; exact pre-takeover PWM enable mode restored on exit.

## Architecture

```
src/OpenFan.Core/         portable engine: curves, control loop, settings store (no Linux deps)
src/OpenFan.Linux.Hw/     hwmon sysfs backend, NVML backend (+telemetry), helper socket client
src/OpenFan.Linux.Helper/ privileged daemon: fan writes + GPU/CPU power limits over /run/openfan/helper.sock
src/OpenFan.Linux.App/    Avalonia UI (pages, calibration window, desktop overlay, tray)
```

Privilege model: PWM write access via a udev rule (`openfan` group); NVML GPU writes go through
`openfan-helper.service` running as root over a `0660` socket (newline protocol:
`ping | set <id> <pct> | default <id> | power <uuid> <watts> | cpupower <W>|default | cpuboot <W>|clear | quit`).
The helper restores any fan it took over if the app disconnects — power limits intentionally persist.
See [`packaging/README.md`](packaging/README.md) for the install chain (udev rule, modules-load, helper service).

## Build & run

```bash
dotnet build && dotnet test          # 136 tests
./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan
```

Without the helper/udev setup installed, the app still runs read-mostly (monitor + UI) and reports write failures
on the affected cards. CLI smoke tools: `openfan-linux --dump`, `--procs`, `--cpu-power`.

### Desktop launcher

```bash
./packaging/install-launcher.sh        # no root; safe to re-run
```

That writes **`~/.local/bin/openfan`**, a wrapper which picks the most recently built apphost (Release preferred on
ties) so rebuilds never require touching the shortcut, then points both the menu entry
(`~/.local/share/applications/openfan.desktop`) and a desktop icon (`~/Desktop/openfan.desktop`, marked trusted —
GNOME refuses to launch an untrusted `.desktop`) at it. `OPENFAN_BIN=/path/to/openfan` overrides the choice;
`openfan --launcher-path` prints what would run without starting the GUI.

`packaging/install-icons.sh` on its own renders the icon set and writes just the menu entry; it now prefers the
wrapper when one exists, but takes an explicit Exec path as `$1` — pass `~/.local/bin/openfan` rather than letting it
default to a build-specific binary.

### Threadripper socket power: PPT is settable, TDP/TjMax are read-only

The CPU tab has a **Socket power limit (PPT)** box (100–300 W) with a *keep after reboot* checkbox —
the same interaction as the GPU watts control. Writes route through `openfan-helper`, so the app never needs to be
started as root (a root session may also write `power1_cap` directly, which is handy for debugging); each write is
verified by reading `power1_cap` back, and a refused or clamped value is reported in place rather than shown as applied. *keep after reboot* is the single switch for
persistence: ticked, it writes `/etc/hsmp-control/ppt_mw` for `hsmp-control-apply.service` (vendored here under
[`tools/hsmp-control`](tools/hsmp-control/README.md)) to re-assert early at boot, and openfan re-asserts it on start too. Unticked,
**nothing** brings the limit back after a boot — not the service, and not openfan itself when it
launches — so firmware's own value stands; HSMP state is volatile and firmware re-programs it from
BIOS CBS every boot. TDP and TjMax are deliberately **not** editable:
they exist only in the BIOS variable, which this firmware refuses to let the OS write while Secure
Boot is enabled (`EFI_SECURITY_VIOLATION`, verified), so they appear read-only beside the box. This
control has nothing to do with fan behaviour — it is a power knob, not a thermal one.

The companion CLI lives in [`tools/hsmp-control`](tools/hsmp-control/README.md): same firmware state,
usable from scripts and by the boot service that provides persistence. `sudo
tools/hsmp-control/packaging/install-persistence.sh <watts>` installs that unit; openfan detects
it: without the unit, Apply reports an **amber** note ("…is not installed — this reverts to the BIOS value on next
boot") instead of claiming persistence that cannot happen.

On the 9970X, Linux exposes the AMD HSMP hardware monitor at `/sys/class/hwmon/hwmon*/name = amd_hsmp_hwmon`.
Its `power1_input` reports live socket power and `power1_cap` reports the socket power cap, both in microwatts.
The CPU page already displays these values. For scripts or integrations, run:

```bash
dotnet src/OpenFan.Linux.Cli/bin/Debug/net8.0/openfan-linux.dll --cpu-power
{"source":"amd_hsmp_hwmon","socketPowerW":95.847,"socketPowerCapW":270,
 "pptDesiredMw":null,"bootPersistenceInstalled":true,
 "tdpMw":245000,"pptBiosMw":295000,"tjmaxC":80,
 "tdpControl":"manual","pptBiosControl":"manual","tjmaxControl":"manual",
 "cbsOk":true,"cbsReason":null}
```

Three power numbers, deliberately kept separate: `socketPowerCapW` is the **live** HSMP limit and is
volatile — firmware re-programs it from BIOS at every boot, so it is not the persistent setting.
`pptBiosMw` is the **BIOS default held in flash**, i.e. what the SMU receives at next boot.
`pptDesiredMw` is what the `hsmp-control apply` boot helper is configured to re-assert from
`/etc/hsmp-control/ppt_mw` (`null` = no override configured), and `bootPersistenceInstalled` says whether
`hsmp-control-apply.service` is present and unmasked at all — comparing desired / live / BIOS-default with
that flag is how drift and wishful configuration become visible. `tdpMw` and `tjmaxC` are **read-only** — the firmware refuses OS-runtime
writes to that variable while Secure Boot is enabled, so OpenFan reports them and never attempts a
write.

Values are `null`, never zero-guesses, in two distinct cases: the field is set to Auto in BIOS (the
effective limit then comes from CPU fuses and is not exposed to the OS — see the matching
`*Control` string), or the CBS variable could not be read/validated (`cbsOk:false` with `cbsReason`,
e.g. after a BIOS update moved a field). Treat `null` as "unknown" and fall back to hwmon-only logic,
not as an unlimited ceiling.

This command still requires no sudo and never opens `/dev/hsmp`, writes `power1_cap`, or sends an SMU
request; the BIOS fields come from the world-readable (`0644`) EFI variable copy, which is cached at
boot — matching how often those values can change. A missing driver/sensor yields exit code 2 instead
of a guessed value. TDP/PPT/TjMax offsets were decoded from the AMI BIOS Setting Mapping Table in
BIOS 0617 for the TRX50-SAGE WIFI A and verified against the live variable; they are **not**
validated on other boards or BIOS versions, hence the magic-and-range checks that fail closed. The
offsets themselves are listed in [`tools/hsmp-control/README.md`](tools/hsmp-control/README.md).
RyzenAdj/ryzen_smu family/model mappings
are not validated for this CPU, and their PM-table setup invokes SMU commands, so they are
intentionally not used. If `amd_hsmp_hwmon` is absent, check whether the kernel's `amd_hsmp` driver is
available; never force a different CPU model mapping.


## Known limitations

- **GPU fan RPM is unavailable on driver 595.x** (`nvmlDeviceGetFanSpeedRPM` fails; no nvidia hwmon chip). Duty %
  is shown instead; if a future driver makes the RPM call succeed, RPM columns light up automatically.
- **CPU limits below ~200 W are untested on this board.** The accepted window is 100–300 W and firmware reports a
  maximum of 2000 W, so a low value could be clamped; the readback check turns that into a visible error rather
  than a wrong number, but nobody has confirmed SMU behaviour down there.
- **TDP and TjMax cannot be changed from Linux at all** on this firmware while Secure Boot is enabled — see the
  section above. They are displayed, never written.
- The overlay shows values at whole-number precision only — it is a glanceable strip, not a logger; nothing is
  recorded or graphed.
- Theme page remains light. Settings covers general/hidden/system options and autostart, and sleep/resume restore is
  implemented via logind (see STATUS.md §4).

## Status

Working daily-driver on the target machine; see [`STATUS.md`](STATUS.md) for the current handover state,
verified-hardware notes, and the gotchas catalog. MIT-style do-what-you-want for now; licensing TBD.
