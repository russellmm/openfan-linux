# Sensor collection and coverage audit

Reviewed October 18–19, 2026 on Ubuntu 26.10, kernel 7.3.0-9, ASUS Pro WS TRX50-SAGE WIFI A,
Threadripper 9970X, four 32 GB Kingston DDR5 ECC modules and three NVIDIA GPUs.

## Collection design

OpenFan now uses **libsensors** as its default read-only hwmon reader. This is the standard library
behind the `sensors` command. The kernel recommends it for conversions, configuration labels and
ignored inputs. OpenFan loads the default sensors configuration, including `/etc/sensors.d`, and
consumes library values in engineering units, without applying sysfs scaling a second time.
[Kernel hwmon guidance](https://docs.kernel.org/hwmon/sysfs-interface.html).

The original direct sysfs implementation was appropriate for basic temperatures and fans, but
repeated directory discovery, chip-name reads and label reads every poll. It also omitted other
measurement types already exported by the kernel. The revised architecture is:

| Source | Reads | Access |
| --- | --- | --- |
| libsensors | Board/CPU/ASUS EC/NVMe/network measurements and status flags | Normal user, direct library calls |
| NVML | NVIDIA temperatures, fan duty/RPM, clocks, utilization, power/limit and VRAM | Normal user, direct library calls |
| EDAC | Corrected/uncorrected memory error counters, controller and kernel rank groups | Normal user, kernel files |
| Linux block statistics | NVMe read/write rates, activity and totals since device start | Normal user, selected whole-namespace stat files |
| NVMe SMART | Endurance/spare, lifetime transfers, power/error counts and thermal history | Fixed read-only helper endpoint; independent 30-second background batch |
| hwmon PWM | Duty readback; existing validated fan writer remains separate | Read normally; writes use existing udev ACL |

No `sensors`, `nvidia-smi`, shell or bus-scanning process is launched for routine measurements.
Missing libraries/configuration errors visibly select the original sysfs temperature/fan fallback.
No new generic sensor-write capability was added.

Regular read-only sampling runs as one background batch, with at most one outstanding poll.
NVMe SMART uses a separate background batch every 30 seconds and its own helper connection,
so a slow drive cannot hold the fan client's socket lock or delay the regular sample. Results are
published on the UI thread; the fan controller, table, cards and applicable HUD readings share that
sample. Profile changes during a read defer inventory refresh and discard that old sample. Shutdown
drains the read batch before disposing native libraries. Inventory discovery runs every five seconds
or on Refresh; discovery itself and fan writes still run on the UI thread. This removes most repeated
metadata work but is not a guarantee against all UI stalls. Device/driver latency and other page-specific
readers remain relevant.

Native libsensors access is serialized because configuration and chip pointers are process-global.
Discovery reloads its configuration and rebuilds pointers; a generation check prevents dereferencing
pointers invalidated by another reader. Configured labels/ignores/conversion rules are handled by the
library. Existing temperature/tach ids are retained for saved curves and aliases, except newly
integrated SPD5118 hubs use physical controller/address identities instead of boot-specific hwmon indices.

## Actual coverage on this machine

The former Sensors page had **60 rows**. With the October 19 DDR5 and NVMe updates, the page has
**136 ordinary hardware rows**, **35 drive-activity rows** and **100 SMART health rows**, plus
**52 optional status/alarm rows**: **323 table rows** in total across the available groups.
Counts are inventory entries, including fan duty readbacks, configured
power limits, fixed VRAM capacity, raw/unverified motherboard channels and counters; they are not
independently calibrated physical sensors. A native read-only inventory sample returned a value
for every discovered library/EDAC/NVML channel. Raw PWM readback is provided separately.

Another 16 DDR5 alarm-threshold readbacks (four per module) appear beneath the four memory
temperature rows rather than as extra measurement rows. They use a distinct TemperatureLimit
kind, stay out of temperature source pickers, and pause with the memory group. These configured
Low/High/Critical low/Critical high values are independent of the sampled Min/Max columns.
Ten additional NVMe composite high/critical threshold readbacks appear beneath the five composite
temperature rows. Generic internal probes are explicitly unmapped; unused sentinel limits are excluded.
See [NVMe details](nvme-sensors.md) for identity, cadence, units, failure handling and Linux/Windows counter differences.

| Category | Rows | Notes |
| --- | ---: | --- |
| Temperature | 45 | CPU package/CCDs, board, ASUS EC, five NVMe drives, network, GPU core and four DDR5 SPD hubs |
| Fan RPM | 14 | Six SuperIO, three ASUS EC and five NVIDIA fans |
| Fan duty | 12 | Seven motherboard PWM and five GPU fan readbacks |
| Voltage | 18 | NCT6799 inputs, labeled by libsensors/config; unverified rails keep generic names |
| Power / configured limits | 8 | CPU package draw/cap and GPU board draw/cap |
| ECC error counters | 18 | Controller totals and eight kernel rank groups; four physical DIMMs |
| GPU load | 6 | GPU and memory-controller utilization per card |
| GPU clocks | 9 | Graphics, SM and memory MHz per card |
| GPU memory | 6 | Used/total GiB per card |
| NVMe drive activity | 35 | Seven metrics per drive: read/write rates, activity and since-start totals |
| NVMe SMART health | 100 | Twenty metrics per drive: endurance/spare, lifetime counters, warnings and thermal history |
| Optional status/alarm flags | 52 | Kernel-reported states, including 16 DDR5 flags; thresholds may be unconfigured |

The reader also understands current, energy, humidity and generic hwmon frequency measurements when
other hardware exports them. This machine does not expose those through ordinary hwmon at present.

GPU fan RPM was previously missing because the native fan structure used an eight-byte speed field.
NVIDIA defines three four-byte unsigned fields (12 bytes total), and its structure size is encoded in
the version argument. Correcting this now returns RPM on all five fans, including stopped-fan zero.
The utilization structure was also two bytes instead of two unsigned integers. The PCI structure had
an extra nonexistent function field, shifting the full bus-id string. These three layouts now match
[NVIDIA's published header](https://github.com/NVIDIA/nvidia-settings/blob/main/src/nvml.h), with
native-layout regression tests and live read-only verification.

## Responsiveness and optional sampling update

The initial expanded table still did unnecessary UI work. Hidden Home cards/curve dropdowns updated
on every tick, and each GPU power overlay tile called the full multi-GPU snapshot reader (including
process enumeration). This also caused extra CPU/hwmon scans on the UI thread. The overlay now reads
the shared background sample only; hidden Home views do not repaint. Names are lightweight labels,
with one temporary edit box for the clicked row. While typing, the sensor table pauses painting and
automatic discovery defers; the sampler, fan controller and overlay continue. Finishing the edit saves
once, and Home curve-dropdown rebuilds defer until Home is opened. Only visible rows update their
formatted text, unchanged strings are skipped, and sample tooltips are no longer rebuilt every tick.

**Sensor groups** chooses which categories to show. Hidden groups and collapsed groups/devices are
excluded from the read request passed to each backend, rather than read and discarded afterward.
Unchecked status flags also stop being read. ECC counter reads consequently stop when Memory (ECC)
is hidden or collapsed. Inventory metadata remains discoverable, so groups can be shown again.
A read already in progress can finish; the next poll uses the new choices.

The request planner retains dependencies for active fan curves (including nested mixes), configured
visible overlay tiles, and visible Home/GPU views. Stale/disconnected controls do not create demand.
Hiding a group never removes its controls or disables a fan curve. These exceptions are explained in
the group picker. Normal offscreen rows still collect history; deliberately hidden/collapsed readings
pause history unless another consumer requires them. Existing minima/maxima remain while paused,
current values become missing, and fresh readings resume when enabled again. Reset still resets all
history explicitly.

All category headings now collapse consistently, including single-device groups. Group/device collapse
and group visibility are saved in profiles and restored on restart. Manual Refresh rescans hardware
without rebuilding unchanged tables; profile changes still invalidate views. If hardware actually
changes and a rebuild is needed, the scroll offset is retained.

Validation: 254 tests pass, including selective native-reader call counts, hidden/status/collapse
requests, mix-cycle dependencies, overlay and visible-page requirements, missing controls, legacy CPU
power fallback and settings round trips. Headless checks typed a long name across multiple ticks,
compared unchanged numeric-cell pixels during typing, verified Enter/Escape/clear, repeated eight
collapse/expand cycles with Refresh, and restored collapsed groups/devices after restart.

Seven-second syscall traces recorded zero ECC counter opens while hidden and 36 corrected plus
36 uncorrected counter opens after showing ECC again. The tested new Sensors/overlay session had
zero `/proc/cpuinfo`, cpufreq-frequency and process-name opens from the old per-tile snapshot paths;
an earlier live-build trace had 5, 330 and 225 respectively. These are observations from separate
sessions, not a controlled CPU-utilization or input-latency benchmark. Traces, screenshots and results
are under ignored `publish/lab-lag/`; unit/build logs use `publish/*sensor-groups*.log`.

## Table behavior and data quality

Tables use aligned Current / Min / Max / Average / Unit columns, compact rows, natural numeric ordering,
editable aliases and hardware-detail tooltips. Reset starts a new observation window. History for enabled readings continues
while another tab is visible; repainting does not add samples. Missing/non-finite values show a dash
and do not enter averages. Real zeros remain valid. Counter/status averages are omitted.

Table minima/maxima are observed values since launch/reset, never hardware critical/min/max thresholds.
Power caps are explicitly named as limits. GPU NVML fan RPM is driver-reported operating RPM, not an
independent proof that a mechanically blocked fan is turning; see the
[NVIDIA query documentation](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html).
Some device alarms clear when read, so displayed flags are sampled state rather than a durable event log.

Raw motherboard channels are not automatically trustworthy: this board has a floating -9 °C input,
zero-valued channels and a roughly 97 °C AUX channel whose physical mapping is unconfirmed. The table
shows the library's data faithfully. The existing 0–150 °C eligibility check is retained separately for
fan curves, including curve previews, so a formerly invalid negative input does not suddenly control fans.
Generic voltage inputs are not guessed to be CPU Vcore, DIMM VDD, +5 V or +12 V; board-specific labels
and resistor-divider scaling require verified information.

## Remaining gaps and next work

| Desired data | Findings / next step |
| --- | --- |
| Physical DIMM slot mapping | Four SPD hub temperatures now work through the DesignWare adapters. Controller/address identities are verified; mapping them to physical slot letters remains open. See the updated RAM investigation. |
| Exact board rail names/scaling | Verify against board documentation, firmware or a Windows sensor export, then add a reviewed libsensors configuration. |
| Vendor-specific SSD data / physical probe mapping | Standard NVMe SMART health and Linux activity now work. Controller/NAND probe mapping, vendor NAND writes and physical slot names still require verified model/firmware documentation. |
| Package/core energy and derived watts | AMD RAPL counters exist but are root-readable here. Add a fixed read-only helper endpoint with wrap/reset handling; do not loosen all sysfs permissions. |
| GPU memory-junction/hotspot | `nvidia-smi` memory temperature reports N/A on these cards/driver. Investigate documented NVML thermal fields before concluding they are inaccessible; do not relabel core temperature as hotspot. |
| Detailed AMD per-core temperature/power/voltage, effective clocks | Standard k10temp/cpufreq do not provide Windows-equivalent per-core telemetry. Assess model/protocol-specific documented HSMP/SMU interfaces and validate units/support before adding them. |
| Other devices (PSU/AIO/USB controllers) | Inventory actual connected hardware before adding vendor-specific read-only backends; do not assume arbitrary motherboard readings belong to these devices. |

A Windows HWiNFO sensor export from this same system would provide a precise gap list and verified
names, but is not required for the channels already added. Exact Windows parity is not established.
HWiNFO includes device/firmware-specific access that may have no equivalent Linux kernel driver.

## Efficiency evidence and limitations

The initial 12-sample read-only benchmark of the old inventory measured median discovery 3.20 ms,
hwmon read 13.67 ms and NVML read 17.05 ms. Metadata caching alone changed these to 2.88 ms, 13.42 ms
and 11.58 ms respectively; the NVML variation was not attributed to the hwmon optimization. These
rapid samples were not a controlled CPU-utilization benchmark and did not wait for device update intervals.

The expanded native inventory read batch was about 61 ms in a one-shot probe. It does substantially
more work, so comparing it to the old batch is not a performance regression/optimization claim.
The practical improvement is shared samples, less discovery and background measurement reads, while
preserving driver update intervals rather than busy-polling.

Further engineering work: migrate hwmon-index ids to persistent device identities with saved-profile
compatibility; re-enumerate NVML handles after driver reset/hotplug; share remaining CPU/GPU detail-page
reads; assess slow/stalled driver behavior and serialized fan writes before claiming all polling is
fully optimized. The current five-second reload still discovers metadata synchronously.

Evidence (ignored local files): `publish/libsensors-probe/inventory.json`, `results-final.log`,
`publish/probe-nvml-layout.py`, `publish/lab/sensors-*.png`, `publish/tests-sensors-final.log`.
