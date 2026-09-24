# hsmp-control — Threadripper socket power CLI

Command-line control and reporting for AMD HSMP socket power limits on TRX50 + Ryzen
Threadripper 9970X. This is the tool `openfan-helper` and the boot-persistence unit are built
around; openfan-linux's CPU tab and this CLI read and write exactly the same firmware state, so
values shown in either place always agree.

It exists because the two control surfaces on this platform behave differently, and only one of
them is writable from Linux:

| Value | Where it lives | OS-runtime access | Survives reboot |
|---|---|---|---|
| **PPT** (socket power limit) | HSMP SMU state, `power1_cap` in hwmon | **writable** (`SET_SOCKET_POWER_LIMIT`, message 0x05) | no — volatile SMU state |
| **PPT** as programmed by firmware | UEFI var `AmdSetupSHP` (CBS), GUID `3a997502-647a-4c82-998e-52ef9486a247` | readable; writes refused with `EFI_SECURITY_VIOLATION` while Secure Boot is on | yes, but only from Setup |
| **TDP** | same CBS variable | read-only (same refusal) | change in Setup only |
| **TjMax** | same CBS variable | read-only (same refusal) | change in Setup only |

That asymmetry is the whole design: PPT can be set at runtime but is forgotten at boot, while the
BIOS values persist but cannot be written from a running system. Persistence therefore means
*re-applying*, not *saving* — which is what `hsmp-control-apply.service` does. No write path to the
firmware variable is offered here; disabling Secure Boot to force one risks leaving the machine
unable to boot, and Setup already writes those fields during boot services anyway.

## Build

```sh
cc -std=c11 -Wall -Wextra -Werror -O2 -o hsmp-control hsmp_control.c
```

Requires the `amd_hsmp` driver (`modprobe amd_hsmp`, then `/dev/hsmp` exists) and a CPU this tool
recognises — it checks family/model and HSMP protocol 7 before touching anything, and refuses on
other parts rather than guessing.

## Commands

```
hsmp-control info                 HSMP/CBS identity, live socket power, BIOS limits
hsmp-control status               current limit plus this boot's journal
hsmp-control limits               PPT (live + BIOS default), TDP, TjMax
hsmp-control limits --json        same values as one JSON object, for other programs
hsmp-control set <mW> --confirm   change the live limit            (needs root)
hsmp-control restore --confirm    back to this boot's baseline     (needs root)
hsmp-control apply                re-assert /etc/hsmp-control/ppt_mw at boot
```

Reads need no privileges; writes do. Every write is verified by reading the value back, so a limit
the firmware clamps is reported as clamped instead of being shown as applied.

```
$ ./hsmp-control limits
PPT   (live socket power limit): 285000 mW   volatile; writable: set <mW> --confirm
PPT   (BIOS default, flash) : 295000 mW   what the SMU gets at next boot
TDP   (BIOS CBS, read-only) : 245000 mW
TjMax (BIOS CBS, read-only) : 80 C
```

Accepted range is **100000–300000 mW**, deliberately the same window openfan-linux offers. Values
below roughly 200 W are untested on this board; if firmware clamps one, you get an error rather than
a silently different number.

## Boot persistence

One command installs the binary, the config file and the systemd unit, then verifies itself:

```sh
sudo ./packaging/install-persistence.sh 285      # pass the watts you actually want
```

**Enabling applies the limit immediately**, not only at boot — so pass your current value (check
`./hsmp-control limits`) to add persistence without changing behaviour today. The script validates
the window before it will accept anything, backs up an existing config, rebuilds the binary if the
source is newer, enables the unit, and finally compares the live limit against what was configured,
exiting non-zero with a `journalctl` pointer on mismatch. Undo with `sudo ./packaging/install-persistence.sh --remove`.

Config is a single milliwatt value in `/etc/hsmp-control/ppt_mw` (see `packaging/ppt_mw.example`).
The unit skips itself entirely when that file is absent (`ConditionPathExists`), so an unconfigured
system runs nothing and the firmware default stands.

How this interacts with openfan's **keep after reboot** checkbox:

| Checkbox | Boot behaviour |
|---|---|
| ticked | openfan writes `/etc/hsmp-control/ppt_mw`; the unit re-asserts it before login |
| unticked | openfan removes that file and does **not** re-apply on its own start — firmware's value stands |

openfan detects whether this unit is installed; without it, ticking the box reports an amber warning
instead of claiming persistence that cannot happen.

## Reading values from another program

```sh
$ ./hsmp-control limits --json
{"ppt_limit_mw":285000,"tdp_mw":245000,"ppt_bios_mw":295000,"tjmax_c":80,
 "tdp_control":"manual","ppt_bios_control":"manual","tjmax_control":"manual",
 "cbs_ok":true,"cbs_reason":"","writable":{"ppt":true,"tdp":false,"tjmax":false}}
```

`null` means *Auto* — no value is programmed in flash — never `0`, so a caller can't mistake an
unset field for a real zero. CBS parsing fails closed: short variable, magic mismatch
(`0xE5AF127C`), unknown control byte or out-of-range value all yield `cbs_ok: false` with a reason,
and no plausible-looking numbers are invented.

Offsets are compiled in from the AMI BIOS setting mapping table for **BIOS 0617** on the Pro WS
TRX50-SAGE WIFI A (TDP control/value at data offsets 1043/1044, PPT 1048/1049, TjMax 1053/1054). On
a different BIOS revision they can move; `CBS_SETUP_VAR=/path/to/variable` points the reader at
another file, which is also how the degradation paths are tested.

## Tests

```sh
./test-cli.sh
```

No root and no limit changes: it builds the tool, asserts the JSON contract (including Auto → null),
exercises CBS degradation against a synthetic variable, and checks that `apply` refuses malformed or
out-of-window config *before* issuing any write. It declines to run the apply checks if
`/etc/hsmp-control/ppt_mw` exists, so it can't interfere with a configured machine.

## Cautions

- HSMP limits are volatile SMU state; `restore` can only return to the baseline captured by this tool **during the current boot**, and says so rather than guessing a BIOS value.
- Don't drive the same limit from other writers (RyzenAdj, another script) concurrently — they don't share this tool's lock.
- PPT is a power knob, unrelated to fan control; nothing here touches fan curves.
