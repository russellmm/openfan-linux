# NVMe temperatures, activity and health

October 19, 2026. Five local PCIe controllers: Samsung 970 EVO Plus 2 TB, Solidigm P41 Plus
(`SSDPFKNU020TZ`), WD_BLACK SN850X 4 TB, and two WD_BLACK SN850X 8 TB drives. Model and PCI
address distinguish devices; firmware/controller metadata appears below each device heading.

## Temperature labels and limits

The kernel's Composite temperature is labeled **Drive temperature (composite)**. This is the
drive's overall thermal-management value, potentially calculated from several inputs; it is
not necessarily the controller or NAND thermometer. Numbered probes are labeled **Internal
temperature 1/2/3 (unmapped)**, with explanatory tooltips. Native custom labels and OpenFan
aliases take precedence. Existing hwmon temperature ids stay compatible with saved curves,
aliases and overlay tiles; curve/overlay source menus distinguish drives by model/PCI address.

NVMe explicitly makes the physical point and accuracy of numbered probes implementation-defined.
No reliable manufacturer mapping for these exact model/firmware combinations was found. A hotter
probe, an ordinal number or another model's behavior is insufficient to label it controller/NAND.
The supplied HWiNFO screenshot shows activity rather than temperature probe locations.
[NVMe data definitions](https://github.com/linux-nvme/libnvme/blob/master/doc/rst/types.rst).

Composite high and critical limits are displayed separately beneath the measured temperature.
Only these valid composite thresholds are included. Internal-probe placeholder limits such as
65,261.85 °C and Kelvin-zero minimums are excluded. Readings retain the kernel/libsensors Celsius
conversion, including fractional .85 values; no invented rounding to whole Kelvin-based degrees.
[Linux NVMe hwmon implementation](https://github.com/torvalds/linux/blob/master/drivers/nvme/host/hwmon.c).

## Drive activity (NVMe)

Seven metrics per controller: read/write rates (decimal MB/s), read/write activity, total activity,
and read/write totals since device start (decimal GB). These are unprivileged reads of the whole
namespace's kernel `stat` file, with partitions excluded to avoid double-counting. Multiple namespaces
are aggregated. Totals reset at reboot/device reinitialization and are separate from lifetime SMART
TB counters. Rates use a monotonic clock; first samples, resets and missing files give a dash.

Read/write activity is summed request time divided by elapsed time and can exceed 100% for
parallel NVMe I/O. Total activity uses busy time, capped at 100%; neither measures bandwidth
saturation. These are Linux activity estimates, not identical Windows performance counters.
Hiding/collapsing pauses reads and clears rate baselines, so resuming does not average through
the hidden interval. [Kernel I/O counter definitions](https://docs.kernel.org/admin-guide/iostats.html).

## Drive health (NVMe)

SMART supplies endurance used, available spare/threshold, lifetime read/write TB, host commands,
power cycles/hours, unsafe shutdowns, media/data-integrity errors, error-log entry counts, warning/
critical temperature minutes, thermal-management transitions/time and the current warning bitmap.
Endurance used can exceed 100%. Error-log entries include unsupported commands and are not
equivalent to media failure; time above critical is historical, not a current alarm. Some zero
thermal fields can mean unsupported or never triggered. Data units are 512,000 bytes, not 512 bytes.
Unsigned 128-bit counters are decoded explicitly; the shared numeric display uses floating point,
so counts beyond 2^53 may be rounded.
Large counts use M/B/T abbreviations to fit the table; hover the current value for its unabridged
numeric reading. Each drive also shows a decoded current warning summary and read timestamp.

Health uses one independent background batch every 30 seconds. Slow storage cannot hold the fan
client's socket lock or delay the regular hardware/fan sample. Each drive shows its last update or
error. Failed/missing data becomes unavailable rather than perfect zero health; stale cached values
expire. Hidden groups/devices stop optional future requests; one in-flight read may finish.

The existing root helper adds only `nvmesmart <controller> <identity-hash>`. It checks a discovered
PCIe controller and identity (PCI/model/serial hash), opens a fixed `/dev/nvmeN` node read-only with
no symlink following, and issues exactly a 512-byte SMART Get Log request with a bounded timeout.
Clients cannot select paths, opcodes, namespaces, firmware operations or writes. Positive NVMe
status returns are failures, not zero-filled success. RAE=0 matches the kernel; this Samsung
firmware rejected an initial diagnostic RAE=1 request. No nvme-cli/smartctl process or new runtime
package is required. Existing helper installations must be updated for SMART; ordinary temperatures
and kernel activity remain usable if SMART is unavailable. Read-only telemetry connections never
own GPU fans. Completed transient helper sessions are pruned.
Shutdown waits for the accept loop before draining registered sessions, preventing an accepted
client from escaping cleanup while the service stops.

## Research references

The supplied HWiNFO screenshot and ASUS block diagram are retained locally under ignored
`publish/nvme-audit/references/`. See [motherboard sensor map](motherboard-sensor-map.md) for the diagram's routes and limits.
Raw health dumps and test/build/GUI evidence stay under ignored `publish/nvme-audit/`.

## Verification and deployment

The October 19 update passed all 270 tests and Release builds for both the app and helper.
Read-only probes returned SMART data from all five installed controllers. Headless UI checks
covered table sizing, alias editing, saved visibility/collapse choices across restarts, and paused
activity/SMART polling. Existing fan, CPU and DRAM settings were preserved during deployment.
The running app and installed helper were verified against the published build files; launchers
select `publish/nvme-app/openfan`. Keep that local directory while those launchers depend on it.
Build outputs, reference screenshots, machine profiles and diagnostic evidence are not committed.
