# OpenFan Linux

Native Ubuntu fan-control app for TRX50-class workstations — motherboard/case/AIO fans via **hwmon** and NVIDIA
GPU fans + power limits via **NVML**, with a curve library (Flat / Graph / Mix) modeled on the Windows
[OpenFan](https://github.com/ScriptHamster/OpenFan) program. Design references in [`screenshots/`](screenshots/)
(Windows OpenFan captures); full build spec in [`openfan-ubuntu.md`](openfan-ubuntu.md).

Built with Avalonia 11 / .NET 8, FluentTheme dark. Currently developed and daily-driven on Ubuntu 26.10 +
Threadripper 9970X + RTX 5060 Ti / 2× RTX PRO 6000 Blackwell (driver 615.x).

## Requirements

- **libsensors** (recommended): `sudo apt install libsensors5 lm-sensors`. OpenFan calls the library directly
  for hwmon measurements, honoring its labels, conversion rules and ignored inputs. Appropriate kernel
  drivers must also be loaded (for example `nct6775`, `asus_ec_sensors`, `k10temp`). Driver discovery is a
  one-time setup task; OpenFan never runs `sensors-detect` or scans hardware buses during normal polling.
  Without the library, a visible fallback provides the original temperature/fan readings from sysfs.
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
  NVIDIA curve targets of 0% use the GPU's automatic policy to allow firmware fan-stop;
  positive targets resume manual control. The card shows **0% (auto)** with measured RPM,
  since the GPU can still run its fan when its own thermal rules require it. Nonzero hardware
  floors, including the RTX PRO 6000's 30%, remain enforced. Update the helper as well as the app.
  **Calibrate** becomes available when a real paired RPM reading arrives, including NVIDIA fans.
  Point % follows the slider unless manually overridden for a point; moving the slider follows it again.
  **OK** saves points and avoid flags directly into the active configuration JSON; Cancel discards
  table edits. Save configuration also includes the accepted points. See [fan calibration](docs/fan-calibration.md).
- **Curve library** — curves are first-class named objects; assign one curve to many fans.
  - *Flat*: fixed % · *Graph*: temperature→% points with hysteresis + live mini-preview · *Mix*: max/min/average of other curves
  - Save/load whole setups as portable JSON profiles.
- **GPUs page** — per card: utilization + VRAM bars, temp / power draw-vs-limit / P-state / fan % / RPM / clocks,
  PCIe generation × width with live RX/TX throughput, and a live process table (name · PID · compute/graphics · VRAM).
  Power-limit editor per GPU (applied via the root helper, persisted, reapplied at startup).
- **Sensors page** — compact Current / Min / Max / Average / Unit tables for temperatures, fan RPM/duty,
  voltages, current, power/energy and humidity where available, NVIDIA load/clocks/VRAM, and memory ECC
  counters. Bound DDR5 SPD5118 hubs appear together under **Memory temperatures**, with stable
  controller/address identities for names, curves and overlay tiles. Configured low/high/critical
  temperature limits appear beneath each memory row, separate from measured Min/Max. Status/alarm flags are optional.
  Statistics share one hardware sample and continue while other
  tabs are visible. **Sensor groups** lets you hide categories such as Memory (ECC). Hidden or collapsed
  groups pause optional hardware reads; active fan curves, the overlay and visible pages keep required
  readings. Visibility and collapse choices persist in the profile. Names become edit boxes only when
  clicked; table painting pauses during editing, while sampling/fan control continue. NVMe/GPU headings
  include PCI identity, and names retain hardware details in tooltips. Regular read-only polling shares one background batch; NVMe SMART uses an independent batch. Discovery runs every five
  seconds or on Refresh. See [sensor coverage and limitations](docs/sensor-data-audit.md), including the
  [RAM temperature investigation](docs/memory-temperature-investigation.md).
- **NVMe drive data** — drive composite temperatures and read-only high/critical limits; internal
  probes explicitly identify unverified physical locations. Separate **Drive activity (NVMe)** and
  **Drive health (NVMe)** groups add read/write rates, activity and since-start totals, plus SMART wear,
  spare capacity, lifetime transfers, power-on time, shutdown/error counts and thermal history.
  SMART reads run independently every 30 seconds through a fixed read-only helper endpoint.
  Both groups support saved hide/collapse and paused polling. See [NVMe details](docs/nvme-sensors.md)
  and [motherboard research references](docs/motherboard-sensor-map.md).
- **Desktop overlay (HUD)** — an always-on-top, borderless strip of live sensor tiles, HWiNFO64-style. Configured on
  the **Tray** page: pick any temperature sensor plus CPU socket power / cap and each GPU's board power, give every
  tile its own background colour (eight curated swatches or a full RGB picker with hex paste), **click any tile's name
  to rename it** (the tooltip names the sensor and its PCI bus — "NVIDIA RTX PRO 6000 Blackwell Workstation Edition
  E1:00.0 (GPU 3) — board power" — with the raw id underneath, so two identical cards are tellable apart), NVMe entries named by drive model and PCI address
  (`a9:00.0 Drive temperature (composite) · WD_BLACK SN850X 8000GB`, same source as the Sensors page headings), reorder them, choose tiles per row and overall size
  (XSmall / Small / Normal / Large / Huge), and decide
  whether it stays above other windows. Text colour is derived from the tile colour's luminance so it stays readable whatever you pick; drag it
  anywhere and it remembers where you left it (and if that position stops being reachable — a monitor goes away — it
  comes back on screen, or use right-click ▸ **Reset position**). Right-click also hides it or changes the layout.
  Read-only — the overlay never writes anything.
- Quiet by default: monitor-only until you tick **Apply curves**; exact pre-takeover PWM enable mode restored on exit.

## Architecture

```
src/OpenFan.Core/         portable engine: curves, control loop, settings store (no Linux deps)
src/OpenFan.Linux.Hw/     libsensors reader, hwmon PWM/fallback, EDAC, NVML, helper socket client
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
dotnet build && dotnet test
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

### Window size and display scaling

On a Wayland desktop OpenFan runs as an X11 client through XWayland, where its size comes from the session's
`Xft.dpi` hint. Two things go wrong there in practice, and both are handled:

- **Login autostart can beat the desktop.** GNOME writes `Xft.dpi` a moment after starting autostart apps, so an
  app that reads it immediately gets nothing and draws at scale 1 — a postage-stamp window on a HiDPI panel, but
  only at login; launching it by hand later looks fine. OpenFan now waits up to 3 s for the hint **only when it is
  missing**, so a normal launch pays nothing. It deliberately does not hand the toolkit a scale factor of its own in
  automatic mode: on GNOME Wayland that override has produced a window which mapped at the correct size, painted
  nothing, and still swallowed clicks over other windows. Waiting changes only timing.
- **The toolkit's override needs connector names.** `AVALONIA_SCREEN_SCALE_FACTORS="*=2"` is silently ignored;
  `DP-2=2;DP-3=2` works. Pins therefore enumerate the displays (mutter, falling back to `xrandr`).

If a window is still the wrong size, **Settings ▸ Window scale** pins it for every display — it shows the factor in
use right now, applies on next launch, and survives reboots. A pin never depends on hint timing, so it is the
deterministic choice when sizing has to be right at login. To see what happened at login:

```bash
journalctl --user -u app-openfan@autostart.service -b | grep -E "window scale|scaling hint"
```

### CPU core clocks and frequency ceilings

The CPU tab includes a compact, live table with one row per **physical core** (32 rows on the 9970X), grouping its SMT threads by kernel socket/core topology. Current, minimum, maximum, average and the active ceiling are shown in MHz. Sampling runs once per second in the background while the page is visible; Reset statistics starts a new minimum/maximum/average window. Hover over a current reading for each sibling thread's clock and source.

Current is the highest available sibling reading, preferring `cpuinfo_cur_freq`, then `scaling_cur_freq`, then `/proc/cpuinfo`. Idle threads can retain an earlier reading. These are sampled kernel-reported clocks, **not effective clocks** or a continuous hardware trace. Missing readings show a dash; offline threads are excluded from the current clock. Unknown topology is kept as separate CPU rows rather than guessed.

Use **All cores** to apply one maximum clock to every cpufreq policy. Check **Set individual cores** to disable the all-core controls and enable the **Set MHz** box beside every core. The boxes populate with current caps, preserve unfinished edits across refreshes, and apply only edited cores through **Apply individual caps**. Each edit targets both sibling policies; conflicting edits to a shared policy are rejected. The GUI stays unprivileged: writes go through the updated `openfan-helper`. It validates the entire selection against current minimum and hardware maximum limits before writing, waits up to 500 ms for asynchronous kernel readback, and attempts to restore previous ceilings if a write or verification fails. **Hardware maximum** restores each selected policy's currently advertised `cpuinfo_max_freq`; it does not enable boost or change governors, EPP, minimum clocks, PPT, or BIOS settings.

Frequency caps are live until reboot or another power tool changes them. They are not saved in OpenFan configurations or automatically re-applied. No extra monitoring package is required. The 9970X currently exposes `amd-pstate-epp` policies for all 64 threads; with per-policy boost off, the available ceiling is 4,000 MHz. Real-machine verification of a 3,000 MHz request measured about 3,013 MHz under load in `turbostat`, so hardware frequency steps and clock measurements can differ slightly from the requested policy ceiling.

The proposed reboot-persistence design is documented in [the persistence plan](docs/cpu-frequency-persistence-plan.md); persistence is not enabled yet.

Interface reference: [Linux CPUFreq documentation](https://docs.kernel.org/admin-guide/pm/cpufreq.html).

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
