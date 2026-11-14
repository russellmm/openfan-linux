# ASUS WRX90 BMC fans

On November 14, 2026 the development system changed from the TRX50-SAGE WIFI A / 9970X
to an **ASUS Pro WS WRX90E-SAGE SE**, BIOS **1405**, and **Threadripper PRO 9975WX**.
The board exposes NCT6798 hwmon, ASUS EC sensors and `/dev/ipmi0`. Presence of hwmon PWM
files does not establish ownership of the headers. A live IPMI test verified fan control
through the BMC and a different channel order from the previous board.

## Supported hardware

The root helper enables writes only after checking the board vendor/name, ASUS manufacturer
ID 2623, product ID 4499 (`0x1193`), firmware revision 2.01, auxiliary bytes `22 00 00 00`,
eight-slot order and seven expected names/table layouts. Other identities or layouts fail
closed. The eighth slot is N/A and never becomes a control.

| OpenFan ID suffix | Firmware label | Paired RPM | Policy |
| --- | --- | --- | --- |
| `fan:0` | CPU_FAN & CPU_OPT | CPU_FAN | Shared zone, 0–100% |
| `fan:1` | CHA_FAN1 | CHA_FAN1 | 0–100% |
| `fan:2` | CHA_FAN2 | CHA_FAN2 | 0–100% |
| `fan:3` | CHA_FAN3 | CHA_FAN3 | 0–100% |
| `fan:4` | CHA_FAN4 | CHA_FAN4 | 0–100%; currently disconnected |
| `fan:5` | CHA_FAN5 | CHA_FAN5 | 0–100% |
| `fan:6` | W_PUMP+ | W_PUMP+ | 0–100% |

IDs start with `bmc:asus-wrx90:`; tach IDs use `tach:N` instead of `fan:N`.
CPU_OPT cannot be controlled independently. USB4, VRM and M.2 fans remain available
as read-only EC measurements, outside these seven control zones.

On this board with local IPMI present, GUI/CLI inventory suppresses NCT6798 PWM controls,
including when the BMC helper is unavailable. This avoids falling back to conflicting
register writes. Other NCT measurements remain available. Old profiles are preserved;
old `hwmon:...:pwm:N` assignments/calibrations are **not** migrated to a different fan.
Assign existing curves to the new BMC cards after updating the app and helper.

## Protocol and restoration

The driver uses `/usr/bin/ipmitool -I open` through Linux's existing IPMI device driver;
no new kernel module, network connection or BMC password is needed. `/dev/ipmi0` remains
root-only. The GUI talks to the existing `0660 root:openfan` helper socket.

Primary reference: [ASUS E26328 IPMI manual, July 2025, appendix A-5–A-7](https://dlcdnets.asus.com/pub/ASUS/mb/Add-on_card/IPMI_EXPANSION_CARD/E26328_IPMI_Card_EM_V3_WEB.pdf),
linked from [this motherboard's ASUS support page](https://www.asus.com/motherboards-components/motherboards/workstation/pro-ws-wrx90e-sage-se/helpdesk_manual?model2Name=Pro-WS-WRX90E-SAGE-SE).

- `30 13 01 00`: PWM count. Observed `08`.
- `30 13 03 00`: PWM order. Observed `ff 00 01 02 03 04 05 06 07`.
- `30 13 00 INDEX`: 54-byte table. Observed 32-byte NUL-padded name, two metadata bytes,
  ten active temperature/duty bytes and ten default temperature/duty bytes.
- `30 0f INDEX`: active mode plus five temperature/duty pairs (11 bytes). Mode 04 is verified;
  other modes are currently unsupported for takeover.
- `30 0e 04 INDEX` followed by ten temperature/duty bytes: update that zone's manual curve.
  Every write and restoration is checked against `30 0f INDEX`.

The count/order/name/layout checks are based on live responses, not just the generic manual.
Out-of-range `30 13 00` getters returned unrelated bytes with success status, so the diagnostic
now confines reads to advertised indices. Firmware rejected the manual's no-payload
temperature-source getter (`30 93`); the driver does not guess a payload for a command
that can also change temperature sources.

Since 0.4.3 a manual target replaces all five duty values in the existing temperature grid,
creating a flat 0–100% PWM command. The driver adds no minimum duty, pump restriction or
70°C/100°C override; OpenFan's assigned curves determine thermal policy.
The card labels the requested duty **target**, and RPM is measured
separately from BMC SDRs, refreshed every two seconds. Target duty is unknown while
the original firmware curve is active; it is not guessed from temperature or RPM.
The temperature source remains whichever source the firmware was already using.

Before the first write to a zone, the helper saves its exact active curve to a private,
atomically replaced `/var/lib/openfan/bmc-restore.json`, with a machine-identity check.
Each session owns its zones exclusively. Release, disconnect, graceful helper shutdown
and a 15-second heartbeat timeout restore the saved curve. The watchdog retries failed
restores; failed restoration keeps the journal for recovery. After a helper crash,
startup restores the journal before accepting fan commands. A process lock prevents
two BMC helpers from controlling the same journal concurrently. Helper restart/lease
expiry re-arms app curve outputs on the next successful poll.

Global fan modes, firmware defaults, temperature sources, thresholds and SDR settings
are not modified. Monitor-only startup remains monitor-only. A pending crash-recovery
journal is the exception: restoring an earlier takeover happens at helper startup.

## Diagnostics and verification

Read-only diagnostic (JSON on stdout; exit 2 if unavailable/unrecognized):

```bash
dotnet build src/OpenFan.Linux.Cli
sudo dotnet src/OpenFan.Linux.Cli/bin/Debug/net8.0/openfan-linux.dll --bmc-probe > /tmp/openfan-bmc-probe.json
```

With the updated helper running, the normal CLI discovers BMC controls and can apply a
temporary target, restoring afterward:

```bash
dotnet src/OpenFan.Linux.Cli/bin/Debug/net8.0/openfan-linux.dll --dump
dotnet src/OpenFan.Linux.Cli/bin/Debug/net8.0/openfan-linux.dll --apply-once bmc:asus-wrx90:fan:1 70 --seconds 8
```

An isolated helper was used for development so the installed GPU/CPU helper was not
interrupted during initial testing. CHA_FAN1 increased from approximately 2,040 to
2,760 RPM during the 70% test. All seven active curves were compared afterward and
matched the pre-test bytes exactly. CPU/pump curves were not changed by this test.
Only CHA_FAN1 has received a physical speed-change test so far; the other zone mappings
come from live firmware labels and SDR records.

The checked-in, credential-free response fixture is `tests/OpenFan.Core.Tests/Fixtures/asus-wrx90-bmc.json`.
Tests exercise read-only probing, identity rejection, single-zone writes/readback,
zero targets on every BMC zone, range validation, journal failures/recovery, competing clients,
lease expiration, socket disconnect and re-arming after control loss. Full suite: **306 passing tests**.
Local verification records are under ignored `publish/bmc-audit/`.
