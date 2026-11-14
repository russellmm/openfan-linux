# OpenFan Linux — Project Status & Handover

**Last updated:** 2026-11-14 · **State: v0.4.4 installed and verified; low-speed BMC RPM fixed, calibration defaults to 30%.**
Branch `main` → github.com/russellmm/openfan-linux. Spec / design: [`openfan-ubuntu.md`](openfan-ubuntu.md). Hardware findings journal: [`spike-notes.md`](spike-notes.md). Manual install chain (udev, modules-load, helper service): [`packaging/README.md`](packaging/README.md). CPU power CLI + boot persistence: [`tools/hsmp-control/README.md`](tools/hsmp-control/README.md).
NOTE: history was rewritten once before the first push to scrub a sudo password from old STATUS revisions. Keep credentials out of these docs — that includes paths to local password/token files.

## Version 0.4.4: low-speed RPM and calibration defaults

- Fixed BMC telemetry dropping valid numeric RPM when SDR status reports a threshold
  alarm. Live CHA_FAN2 at 20% and CHA_FAN3 at 26% both reported 360 RPM with `lcr`
  while the old helper returned null. Threshold statuses now preserve numeric RPM;
  unavailable, disabled, nonfinite or invalid values stay missing. No firmware thresholds
  were changed. Both tested zones' original curves were restored exactly.
- Calibration opens at 30% (or a higher hardware minimum). Auto step starts at the
  current slider value and displays its range. Lower manual targets remain available;
  this is a starting default, not a new fan floor. RTX PRO 6000 retains its 30% minimum.
  Existing calibration points are preserved. Native BMC zero-command rejection remains.
- App version **0.4.4**; README, installation, calibration, BMC guide and release notes
  updated. **325 tests pass**, including end-to-end alarmed RPM through the helper client
  and rejection of unavailable/invalid readings without hardware writes.
- Hardware evidence is in ignored `publish/release-v0.4.4/`.
- Installed package built from clean source `fb1f86af0b6048405897ba6b12cf42366b9394a9`; SHA-256
  `6032b5e543d862996e1f4334b0f0dcb50d4d30af9178043814234ac2f0a2d58b`. Both installed binaries match the package, package verification is clean,
  and the helper is active. Profile/calibration hashes, CPU/PPT settings, GPU power limits
  and all seven original fan curves match pre-upgrade snapshots.
- Live post-upgrade tests retain 360 RPM with `lcr` on CHA_FAN2 at 20% and CHA_FAN3 at
  26%; both exact original curves restored. Isolated installed-GUI checks verify 30%
  opening duty, selectable 20%, successful auto-step recording from 20%, and retained
  RTX PRO 6000 30% minimum. GUI write checks used a private simulated helper endpoint;
  live RPM tests used the real helper. Test points were discarded; user profiles untouched.

## Version 0.4.3: full-range PWM correction

- Removed driver-imposed BMC floors: all seven zones, including W_PUMP+, now expose
  minimum 0% and expose 0–100% targets. Manual targets submit the same duty to all five
  BMC curve points. The previous 70°C override is removed; thermal policy belongs to
  the user's OpenFan curves. The original-curve journal and restore/lease behavior remain.
- Calibration no longer adds its own 20% sweep minimum. A zero-floor control sweeps
  0, 10, 20, …, 100%; RTX PRO 6000 controls still start at 30%, with the existing GPU
  writer's minimum preserved. Old calibration points are not changed or fabricated.
- Version bumped to **0.4.3**. [Release notes](docs/releases/v0.4.3.md),
  [calibration guide](docs/fan-calibration.md), [BMC guide](docs/wrx90-bmc-fans.md) and
  installation documentation updated. **307 tests pass**, including zero-duty requests,
  readback and exact restoration on every zone plus helper-client and sweep regressions.
- Live firmware testing found an additional limitation: actual 0% commands return OEM
  0xD9 on CPU_FAN and attached chassis zones; CHA_FAN1 accepts flat 1/5/10/20/70/100%.
  Zero rejection is reported and rolled back without silently substituting another duty.
  The generic documented configuration setter returned 0xC7 even for the original curve.
  Direct NCT zero held its register values but did not change physical RPM. No alternate
  zero-duty control path has been verified. **True fan-stop remains unresolved**, separate
  from the removed software floors and temperature override. Hardware records are under
  ignored `publish/release-v0.4.3/`; all test changes were restored.
- Published [v0.4.3](https://github.com/russellmm/openfan-linux/releases/tag/v0.4.3)
  from clean source `c3fe8a54eb2d0e9c4288412b3b977a3b818d7167`; the annotated release
  tag remains on that exact package source. Installer: `openfan_0.4.3_amd64.deb`,
  55,134,360 bytes; SHA-256 `b0f5d30638e0ae9829074d8749f80c82812d7d708c14540ce48412f3c8b95745`.
  Installer and SHA256SUMS were downloaded from GitHub and matched local files.
- Installed app/helper hashes match the release package; `dpkg --verify openfan` is clean,
  helper active, all seven reported minimums are 0%. Selected profile/calibration hashes,
  captured CPU/PPT settings, GPU power limits and all seven original BMC curves match
  pre-upgrade snapshots. No personal calibration points were changed or test points saved.
- Isolated GUI checks confirmed CPU calibration starts at 0% with Auto step 0–100%,
  while RTX PRO 6000 stays at 30% with Auto step 30–100%. Mocked writes verified the
  requested percentages without moving GPU fans; a separate installed-GUI check against
  the real BMC showed the explicit 0xD9 rejection and disabled sample recording at 0%.
  These captures and deployment/firmware records are in `publish/release-v0.4.3/`.
  No test GUI remains running; no reboot, firmware update or control-source switch was done.

## Version 0.4.2 release (historical)

- OpenFan's assembly/package version is now **0.4.2**. Home and About both obtain the
  displayed version from the assembly; About no longer contains a stale hard-coded version.
  Backend labels include the active BMC reader. The previous local package revision changed
  only Debian metadata, so its GUI still showed 0.4.1; this release corrects that mismatch.
- The release includes the WRX90 BMC source, response fixture, tests and documentation
  described below. [Release notes](docs/releases/v0.4.2.md) and [installation guide](docs/installation.md)
  describe the `openfan_0.4.2_amd64.deb` upgrade and preserved profiles.
- Build the release installer from its clean tagged source with
  `bash packaging/build-deb.sh publish/packages/v0.4.2 0.4.2`.
- Published [OpenFan Linux v0.4.2](https://github.com/russellmm/openfan-linux/releases/tag/v0.4.2)
  on November 14, 2026 (Pacific time). Annotated tag `v0.4.2` points to clean source commit
  `de4916ba447da60d9ee2f2c9c19d5b34e7c876e7`. This deployment record is a later
  documentation-only commit; the release tag remains on its exact package source.
- Release assets: `openfan_0.4.2_amd64.deb` (55,134,080 bytes) and `SHA256SUMS`.
  Installer SHA-256: `854ee8ba319b98ee9292940266906717ab3a462177d4be42f0f60c45bc95652a`.
  Both published files were downloaded again and matched their local SHA-256 hashes.
  Local release files are in ignored `publish/packages/v0.4.2/`.
- The exact release package is installed as **openfan 0.4.2**. App/helper hashes match
  the package, `dpkg --verify openfan` reports no differences, and the active helper
  serves seven BMC zones. Installed build-info identifies the clean release source commit.
  Isolated monitor-only GUI captures verified **v0.4.2** on both Home and About, with
  BMC included in backend labels. No test GUI remains running.
- **297 tests pass**; Release solution/app/helper builds succeeded. Upgrade checks
  confirmed unchanged selected profile, other profile files, RTX routing, captured CPU
  frequency/PPT limits, GPU power limits and all seven BMC curves. Build/install logs,
  GUI captures, deployment snapshots and downloaded-asset verification are in ignored
  `publish/release-v0.4.2/`. No reboot was performed.

## November 14: initial WRX90 BMC implementation and validation

- Hardware is now ASUS Pro WS WRX90E-SAGE SE, BIOS 1405, Threadripper PRO 9975WX.
  Local BMC identifies as ASUS manufacturer 2623 / product 0x1193, firmware 2.01,
  auxiliary revision `22 00 00 00`. NCT6798 and local IPMI coexist; actual control
  via BMC has been verified. See [WRX90 driver, protocol and limitations](docs/wrx90-bmc-fans.md).
- Built and installed local package **0.4.1+20261114.bmc1**. Existing installed app/helper
  paths remain the same; `openfan-helper.service` is active with seven verified BMC zones.
  At this initial validation stage the implementation was local and uncommitted; the 0.4.2
  release above supersedes that package-only revision.
- New root driver: `AsusBmcController`; app backend: `BmcHelperClient`; diagnostic:
  CLI `--bmc-probe`. Uses existing kernel IPMI plus `ipmitool`, with fixed helper commands,
  exact write/readback, exclusive session ownership, a 15-second lease, saved-curve journal
  and startup recovery. No network credentials or new kernel driver needed.
- BMC cards replace NCT6798 PWM cards on this board. Stable IDs are
  `bmc:asus-wrx90:fan:0` through `:6`. CPU_FAN/CPU_OPT share zone 0, CHA_FAN1–5 use
  zones 1–5, W_PUMP+ uses zone 6. Chassis/CPU targets have a 20% floor, preserve full
  speed at the firmware source's 70°C point, and the pump is fixed at 100%.
  Old profile assignments/calibrations are retained but not automatically mapped to new hardware.
- **297 tests pass**, including 16 new BMC cases. Release app/helper package built successfully.
  Packaged GUI checked in an isolated monitor-only Xvfb session: seven named cards and
  correct RPM pairing, pump/disconnected-channel calibration disabled.
- Live CHA_FAN1 70% test: approximately 2,040 → 2,760 RPM. All seven active curves
  matched original bytes afterward. Physical client disconnect, stalled-client timeout
  (observed recovery after 16.4 seconds at two-second observation intervals), and SIGKILL
  helper recovery also restored the original curve. Other zones have not been speed-tested.
- Deployment verification: saved profile hashes, all captured CPU frequency/PPT limits,
  GPU power limits and all seven BMC curves unchanged. Prior installed app/helper backup,
  installer, GUI capture, logs and hardware/deployment verification live in ignored
  `publish/bmc-audit/`; installer is `publish/packages/openfan_0.4.1+20261114.bmc1_amd64.deb`.
  Temporary development helper was stopped before installing the production helper.

## October 24 handoff and deployment (historical)

Start with [October 24 installation handoff](HANDOVER-2026-10-24.md); [October 21 handoff](HANDOVER-2026-10-21.md) and [October 19 handoff](HANDOVER-2026-10-19.md)
and [October 18 history](HANDOVER-2026-10-18.md) cover the earlier sensor and CPU work.
Work in `/mnt/SOLIDIGM/ChatGPT/openfan-linux`.

- Current package: `openfan 0.4.1`, built from clean commit `29bae8e`. App: `/opt/openfan/app/openfan`;
  helper: `/usr/lib/openfan/openfan-helper`, managed by the packaged systemd unit. Existing RTX
  routing is preserved, with the registered app targeting the installed executable. Source/build
  directories on the USB drive are no longer runtime dependencies. [Installation guide](docs/installation.md).
- [GitHub release v0.4.1](https://github.com/russellmm/openfan-linux/releases/tag/v0.4.1)
  includes the amd64 Debian installer, SHA256SUMS and tagged source archives. The tag stays on
  the exact package source commit; subsequent deployment documentation lives on main.
- **281 tests pass**; Release app/helper builds and headless UI checks are recorded in the handoff.
  Latest calibration GUI tests used real read-only RPM with simulated actuator writes; earlier
  fan-stop tests exercised the actual 5060 Ti and verified automatic/manual policy transitions.
- NVIDIA **real fan RPM works**, including stopped-fan zero. GPU Calibrate links update when the
  first real readings arrive. Slider/Point % synchronize, GPU sweeps respect hardware floors, and
  temporary calibration takeover preserves saved Curve flags. OK saves per-fan points and avoid
  flags to the active JSON. [Calibration guide](docs/fan-calibration.md).
- A 0% NVIDIA target uses firmware automatic fan-stop; positive targets use manual control.
  The PRO 6000's existing 30% floor stays enforced. Update both app and helper for the fan-stop fix.
- Earlier published CPU core caps, compact/background sensor sampling, saved visibility and
  collapse with paused optional polling, DDR5 temperatures/limits, and NVMe activity/SMART are
  retained. [Sensor coverage](docs/sensor-data-audit.md), [NVMe data](docs/nvme-sensors.md),
  [DRAM setup](DRAM-TEMPS-README.md), [motherboard research](docs/motherboard-sensor-map.md).
- CPU **frequency-cap reboot persistence remains planned**, distinct from existing PPT power-limit
  persistence. [Frequency persistence plan](docs/cpu-frequency-persistence-plan.md).
- Active user profile: `/home/russellm/Documents/openfanOct23.json`. Package deployment preserved
  the full JSON and 343 captured CPU/PWM/DRAM settings. Start minimized is still enabled.
  Generated builds, local profiles,
  backups, traces and reference images remain ignored rather than being published.

## Historical notes — September 24, 2026

The numbered sections below retain the older implementation journal. Their deployment paths,
test counts, driver/OS observations and GPU-RPM limitations describe that earlier revision;
use the current handoff above for present behavior and verification.

## 1. What this is
Native Ubuntu app owning motherboard/case/AIO fans (hwmon sysfs) + NVIDIA GPU fans and power limits (NVML), with
Flat/Graph/Mix curves, mirroring the Windows **OpenFan** reference (design screenshots in `screenshots/`). Also
controls the **CPU socket power limit (PPT)** on Threadripper via HSMP — a power knob on the CPU tab, unrelated to
fan behaviour (see §4).
Pages: **Home** (fan Controls + Curves library), **CPU** (socket telemetry + PPT editor), **GPUs** (per-card
telemetry + power limits), **Sensors** (full inventory, grouped, friendly renaming), Theme stub, **Tray** (desktop-overlay setup), Settings
(general / hidden / system incl. conflict detector + autostart), About. Avalonia 11.2.3 / .NET 8, FluentTheme Dark.

## 2. Build / run / test (agent workflow — agent rebuilds + restarts, user just looks)

```bash
cd /mnt/8TB/deepseek-linux/projects/openfan-linux
dotnet build && dotnet test          # 136 tests green (tests/OpenFan.Core.Tests)
# GUI smoke: exit 124 after timeout == stable run
timeout 12 ./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan; echo exit=$?
```

**If you changed `OpenFan.Linux.Hw` or `OpenFan.Linux.Helper`, reinstall the daemon too.** The running helper is a
published binary, not the repo tree; a stale daemon answers new commands with `err unknown command` (this cost real
debugging time — the GUI looked broken when only the installed daemon was old). Publish + install + restart per
[`packaging/README.md`](packaging/README.md), then probe the socket with an out-of-window value to confirm it knows
the command without changing any limit.

**Primary UI verification: the headless lab** (`../headless-lab`, read its README first):
```bash
cd ../headless-lab && ./hd start            # idempotent Xvfb :99 + Openbox
./hd run --wait 7 -- dotnet ../openfan-linux/src/OpenFan.Linux.App/bin/Debug/net8.0/openfan.dll
./hd shot label                             # read the printed PNG, decide next click
./hd click X Y                              # screenshot px == display px, 1:1
./hd diff a.png b.png                       # changed-pixel count proves UI reacted
```
Loop: **shot → read image → act → shot**. Monitor-only is safe; ASK before Apply-curves/calibration.
⚠ A lab instance holds the single-instance lock (/run/user/1000/openfan.lock) — `./hd kill` BEFORE relaunching the :0 GUI.
Lab gotchas that bit me (see lab README for the full list):
- Clicks land DURING a card rebuild silently no-op → sleep ~2 s after launch/rebuild-triggering actions.
- Code-built menus: set `btn.Flyout = fly` (auto-opens); manual ShowAttachedFlyout was flaky under automation.
- Flyouts are separate override-redirect windows — they appear in `hd shot` (root capture), not window captures.
- Measure before clicking: crop+zoom the current screenshot for exact widget bounds; never reuse coords from an older layout.
On the real desktop (`:0`) prefer keyboard (`xdotool key --window $W ctrl+N`); clicks there need a FRESH xwininfo
origin per attempt (mutter re-places windows; stale coords once hit Exit and quit the app).
```bash
# Relaunch on user's desktop (kill old instance first):
pkill -x openfan; sleep 1
DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 WAYLAND_DISPLAY=wayland-0 \
  setsid nohup ./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan >/tmp/openfan-gui.log 2>&1 </dev/null &
```
- Crash log for the agent-launched instance: `/tmp/openfan-gui.log` (how the calibrate crash was found).
- **See the user's actual window** (required before claiming a UI change works):
  `W=$(DISPLAY=:0 xdotool search --class openfan | head -1); DISPLAY=:0 import -window "$W" /tmp/x.png` then read_image.
  Caught the invisible-nav-rail bug this way — verify visually, don't reason blind.
- Unit tests + CLI checks are NOT end-to-end proof. Claiming "Apply works" from unit tests alone was wrong once:
  the deployed daemon was stale. For anything crossing the socket, drive the real socket or the real UI.
- `pgrep -f "bin/Debug/net8.0/openfan"` self-matches its own bash line → use `pgrep -x openfan`.
- Git identity is configured **repo-locally** (`russellmm <russellmm@users.noreply.github.com>`); no `-c` flags needed.
  Pushing over HTTPS needs a credential and none is stored in git config — see §8 for the procedure.
- **Never chain `build | grep "0 Error(s)" && … && commit`**: grep exits 0 on match and the chain commits
  even when errors were also printed (happened once; fixed via --amend). Gate on the build explicitly.
- Likewise never trust a negative from the wrong artifact: `.NET` string literals are UTF-16, so plain `grep` on a
  built `.dll` reports strings as absent. Use `strings -el`. And confirm *which* config file an app instance uses
  (`$XDG_CONFIG_HOME/openfan/active` → named profile) before concluding a setting was never saved.

**Launcher (user-facing, survives rebuilds):** both `~/.local/share/applications/openfan.desktop` and
`~/Desktop/openfan.desktop` (trusted via `gio set … metadata::trusted true` for GNOME) exec **`~/.local/bin/openfan`**,
a wrapper that picks the newest built apphost (Release preferred on ties), honours `OPENFAN_BIN=`, and answers
`--launcher-path` without starting the GUI. Icons come from `packaging/install-icons.sh`.

## 3. Target machine facts (verified)
- Ubuntu 26.04.1, TRX50 + Threadripper 9970X, NVIDIA driver **595.91.07**. BIOS **0617**, Secure Boot **on**.
- GPUs: RTX 5060 Ti `GPU-551a52fc…`; PRO 6000 WS `GPU-6d3eab54…` (slot 1); PRO 6000 WS `GPU-d0f36b3b…` (slot 3).
- **GPU fan RPM unavailable on this driver** — verified by direct ctypes probe: `nvmlDeviceGetFanSpeedRPM`
  fails on all 3 cards with every struct-version encoding, and `/sys/class/hwmon` has **no nvidia chip**.
  Windows shows RPM only because its driver is newer. Decision (user-agreed): GPU fan cards show measured duty %;
  GPUs are NOT calibratable — commanded % taken at face value. On a driver update tach readings appear
  automatically: NvmlBackend writes `nvml:{uuid}:tach:{n}` whenever the RPM call succeeds.
- Board sensors: NCT6796D-S binds as **nct6799** (hwmon5, 6 fan tachs) + **asusec** (hwmon8).
  `/etc/modules-load.d/openfan.conf` loads nct6775 at boot. Board boots with `pwmN_enable=5` — app restores
  the exact pre-takeover value on release/exit.
- PWM write access: udev rule `/etc/udev/rules.d/99-openfan-pwm.rules` → group `openfan`; user is a member.
- GPU writes: root helper `/usr/local/lib/openfan/openfan-helper` + **openfan-helper.service** (enabled, active),
  socket `/run/openfan/helper.sock` (root:openfan 0660; relocate both ends with `OPENFAN_HELPER_SOCKET`, which is how
  tests use a private socket). If neither NVML nor `amd_hsmp_hwmon` exists the daemon exits 2 ("helper has no job")
  instead of serving an inert socket. Protocol lines: `ping | set <nvml-fan-id> <pct> |
  default <id> | power <GPU-uuid> <watts> | cpupower <W>|default | cpuboot <W>|clear | quit`. Fan writes restore on
  disconnect; **power limits deliberately not restored** (driver persists them, and a CPU limit is re-applied by
  policy, not restored). App keeps ONE persistent connection — per-call connections would trip
  restore-on-disconnect. GPU reads go direct via NVML in-process.
- HSMP: driver `amd_hsmp`, protocol **7**, SMU firmware 115.46.0. hwmon chip `amd_hsmp_hwmon` exposes
  `power1_input` / `power1_cap` in **microwatts** and `power1_cap_max` = 2000 W (firmware ceiling, *not* a
  recommended target). Live limit at handover: set by the user; BIOS flash default **295 W**, TDP **245 W**,
  TjMax **80 °C**.
- CPU power persistence is installed on this machine: `/usr/local/bin/hsmp-control` + enabled
  `hsmp-control-apply.service` + `/etc/hsmp-control/ppt_mw` (milliwatts, present only when *keep after reboot* is
  ticked). Installed via `tools/hsmp-control/packaging/install-persistence.sh`.
- User's own `nvidia-power-limit.service` sets -pm 1 + 150/250/250 W at boot; OpenFan's saved limits were seeded
  to match (config `gpuPowerLimitsW`, reapplied at app start). Interaction documented: last writer wins, no conflict.
- Privileged steps: this harness has a root-grant mechanism configured on the machine. Use it for one command at a
  time when needed; **never** write the password file path or any secret into docs, commits, or scripts.

## 4. Architecture
**Curve library model (user-mandated, non-negotiable):** curves are first-class named objects; fans reference them.
- `Settings.Curves: List<CurveSettings>` — Id / Type(`flat|graph|mix`) / Name / SensorId / HysteresisC=2 /
  HysteresisS=1 / MinTempC=30 / MaxTempC=100 / MaxSpeedPercent / Function(`max|min|average`) / ChildCurveIds / Points.
  `ControlSettings.CurveId` + `Enabled` reference a curve (many fans → one curve). Mix-in-mix blocked.
- `CurvePointDto(double TempC, double Percent)` — **Percent is DOUBLE**. `GraphCurve.Evaluate(IReadOnlyList<CurvePoint>, …)`
  takes CurvePoint, NOT DTOs; map first.
- Config `$XDG_CONFIG_HOME/openfan/config.json` (camelCase): curves, controls, `SensorAliases` (id→friendly),
  `GpuPowerLimitsW` (uuid→W), `CpuPowerLimitW` (int? watts), `CpuKeepAfterReboot` (bool), WindowX/Y/W/H geometry.
  A named-profile sidecar `$XDG_CONFIG_HOME/openfan/active` holds an absolute path to the real active file — so the
  live config is often NOT `config.json` (the user's is `~/Documents/openfan2.json`). Check the pointer before
  reasoning about what is saved. `CpuLimitShouldReassertOnStart` is derived and `[JsonIgnore]`: never persisted.
- Sensor ids: `hwmon:{chip}:{n}:temp:{label}` / `…:pwm:{n}` / `…:fan:{n}`; GPU `nvml:{uuid}:temp:core`,
  `nvml:{uuid}:fan:{n}` (control + live-% readback), `nvml:{uuid}:tach:{n}`.
- **Sleep / resume (logind):** `LogindMonitor` watches `PrepareForSleep`. Suspend → `Controller.RestoreAll()` (fans
  handed back to firmware before sleep); resume → ~1.5 s settle, then `Controller.ResetApplies()`, because wake often
  resets `pwm_enable` on this EC. A `Suspended` flag suppresses curve writes across the window (`FanApp.cs:54-68`).
  Synthetic test: `sudo busctl --system emit /org/freedesktop/login1 org.freedesktop.login1.Manager PrepareForSleep b true|false`.
- **GPU power limits**: `FanApp.SetGpuPowerLimit(uuid, watts)` → helper `power` cmd if socket exists else direct NVML;
  saved on success only; reapplied at startup. NvmlBackend also exposes `SnapshotAll()` (full per-GPU telemetry record).

### CPU socket power (PPT) — the design that took the most verification
**Two control surfaces, and this is the crux of every persistence question:**
1. **HSMP** `SET_SOCKET_POWER_LIMIT` (message 0x05), exposed as hwmon `power1_cap` → writes **volatile SMU state**.
   Takes effect immediately, forgotten at boot. Root write; world read.
2. **UEFI CBS variable** `AmdSetupSHP` / GUID `3a997502-647a-4c82-998e-52ef9486a247` → holds what firmware programs
   at boot (PPT default, TDP, TjMax). Readable at runtime from the world-readable efivarfs copy; **writes are refused
   with `EFI_SECURITY_VIOLATION`** while Secure Boot is on — verified, including a control test where rewriting an
   unrelated variable no-op also failed, so the block is global, not field-specific. (`S_IMMUTABLE` would give
   `EPERM` from `inode_permission`; `EACCES` only ever comes from firmware status mapping.)

Therefore: **PPT is writable but not persistent; TDP/TjMax are persistent but not writable.** Persistence = re-apply
at boot, never "save". No write path to the CBS variable is offered anywhere in this codebase; changing TDP/TjMax
means Setup. Full write-up: [`tools/hsmp-control/README.md`](tools/hsmp-control/README.md).

- `Hw/CpuPowerControl.cs` — implements `ICpuPowerWriter`. Window **100–300 W**, deliberately identical to the C tool's
  `100000..300000 mW`. W→µW conversion (`power1_cap` is microwatts; a `/1_000` bug here once displayed 250 W as
  250000 W), refuses above `power1_cap_max`, **verifies every write by readback** and reports "firmware settled at X
  instead of Y (clamped)". `SetBootLimitWatts(int?)` writes `/etc/hsmp-control/ppt_mw` (milliwatts, 0644, temp+rename)
  or deletes it. `BootPersistenceWired()` checks the four systemd load paths for `hsmp-control-apply.service`,
  treating a masked unit (symlink to `/dev/null`) as absent — so the UI can never promise persistence that has no
  executor.
- `Hw/CbsSetupReader.cs` — reads + validates the CBS blob: magic `0xE5AF127C`, control bytes (`manual|auto`), value
  ranges; **fails closed** (short file, magic mismatch, unknown control byte, out-of-range → `Ok=false` + reason) and
  maps Auto to `null`, never 0. Offsets from BIOS 0617's AMI mapping table (TDP ctl/val 1043/1044, PPT 1048/1049,
  TjMax 1053/1054); `CBS_SETUP_VAR` overrides the path, which is how degradation paths are tested.
- `Hw/HsmpLimitConfig.cs` — reads the desired boot limit from `/etc/hsmp-control/ppt_mw` (`#` comments skipped,
  null when absent/malformed). Deliberately applies no range policy: it reports intent, `CpuPowerControl` decides.
- **Helper commands:** `cpupower <W>|default` and `cpuboot <W>|clear`, validated in the parser *and* again in
  `CpuPowerControl`. Arity gotcha: `"cpupower 250"` is **two** parts with the value in `parts[1]` (a `Length == 3`
  check made every command answer `err unknown command`). The helper starts even with no NVIDIA driver as long as
  `amd_hsmp_hwmon` exists, and logs `CPU PPT available` at startup.
- **Persistence policy (`AppSettings.CpuLimitShouldReassertOnStart = CpuPowerLimitW is not null && CpuKeepAfterReboot`):**
  *keep after reboot* is the single switch for both mechanisms — the boot file AND whether the app re-asserts on
  launch. Unticked ⇒ nothing restores the limit after a boot, including openfan itself; ticked ⇒ the unit applies it
  before login and the app does not rewrite `/etc` at startup (so an admin-installed config survives launching the
  app). Bug history worth remembering: the constructor used to re-apply any saved limit unconditionally, so an
  **unchecked** box still produced a limit that survived reboot — user-reported, fixed, regression-tested.
- **Failure messaging:** a helper older than these commands answers `err unknown command`; `FanApp` maps that to
  "openfan-helper is out of date … reinstall it" instead of leaking the raw string. A live write that succeeds but
  whose boot-file step fails reports "limit applied, but …" rather than a flat failure; a successful apply with no
  unit installed shows an **amber** note, never green.

**Key files**
- `src/OpenFan.Core/` — portable engine: Curves/, FanController, SettingsStore, CalibrationMap/Rules.
- `src/OpenFan.Linux.Hw/` — HwmonBackend, NvmlBackend (+NvmlNative P/Invokes incl. telemetry + SetPowerManagementLimit),
  CompositeActuator, InventoryMerger, NvmlHelperClient, HelperProtocol/Server/Sessions (`IGpuPowerWriter`,
  `ICpuPowerWriter`), HsmpPowerReader (read-only telemetry), CpuPowerControl, CbsSetupReader, HsmpLimitConfig.
- `src/OpenFan.Linux.App/` — MainWindow.axaml(.cs) (~2700 lines: nav rail; Home cards+curves; CPU page builder;
  GPUs page builder; Sensors page builder), GraphEditorWindow, CalibrationWindow, HudWindow + HudColorDialog, TrayHudPage, FanApp, tray.
- `tools/hsmp-control/` — vendored C CLI (`hsmp_control.c`), systemd unit, `install-persistence.sh`, `test-cli.sh`.

**UI conventions**
- Tokens: accent `#F0A03C`, window `#171B1F`, card `#1E2429`, border `#2C363D`, secondary `#8FA0AA`,
  nav rail `#101416`, FAB bg `#4A5D68`, VRAM bar `#4FC3F7`, ok-green `#7BC97B`, warn-amber `#E5C07B`, bad-red `#EF6B6B`.
- Styles: `RadioButton.nav` (orange bar), `Button.accent`, `Button.fab`, `TextBox.cardname` quiet-affordance rename box.

### Desktop overlay (HUD) — HWiNFO64-style sensor strip (`78bca67` + tray picker)

Always-on-top borderless window of live tiles; configured on the **Tray** page. Pure logic lives in
`OpenFan.Core/Hud/HudFormat.cs` (+`HudTheme`, `HudDefaults`) so it is testable without a display; `HudWindow` is a
renderer, `TrayHudPage.cs` is the picker (partial class of MainWindow), `HudColorDialog.cs` the RGB/hex chooser.

- **Why not top-panel tray items** — settled with evidence, do not re-explore: Avalonia's `TrayIcon` exposes only
  Icon/ToolTipText/Menu/IsVisible/Command (no text at all), and Ubuntu's `ubuntu-appindicators` extension renders panel
  text *only* from the legacy `XAyatanaLabel` SNI property (`appIndicator.js` `get label()` → `_proxy.XAyatanaLabel`,
  drawn by `indicatorStatusIcon.js:_updateLabel`) in a themed colour. So per-sensor background colours are impossible
  in the panel without writing our own StatusNotifierItem over D-Bus or shipping a Shell extension.
- **Source ids** are either real sensor ids from the same inventory the curves use, or synthetic `cpu:power:w`,
  `cpu:pptcap:w`, `cpu:temp:c`, `nvml:<uuid>:power:w`. GPU power is deliberately NOT in the readings dictionary
  (curves never drive off wattage), so those tiles resolve through `NvmlBackend.SnapshotAll()`.
- **Units are inferred from the id, per backend**: hwmon `:fan:` is RPM while NVML `:fan:` is % and publishes RPM as
  `:tach:`. Getting this wrong crossed fan % onto a tach tile — covered by `HudTests`.
- **Text colour is derived**, never chosen: BT.601 luminance of the user's background (`HudTheme.TextColorFor`), so a
  pale tile gets dark text. A test asserts no palette entry yields text matching its own background.
- **Cost discipline**: repaint only when a tile's *formatted* string changed at shown precision; tile visuals rebuild
  only on a settings-signature change; one `CpuMonitor.Read()` shared by all CPU tiles per refresh (three tiles would
  otherwise triple sysfs traffic for the same sample). Refresh is driven by `FanApp.Ticked`, so the overlay and the
  control loop always show the same sample.
- **Two-way sync**: the overlay's own menu (hide, tiles-per-row) raises `App.HudUiSync`, and the Tray page re-reads
  settings behind a `_hudSyncing` guard so handlers don't echo. Without it the page kept a ticked checkbox for an
  overlay the user had just hidden from the strip itself.
- **Verified on the active-profile path, not just a scratch config**: with `…/openfan/active` pointing at
  `/tmp/…/work.json`, the overlay read its tiles from that file, recolouring via the UI wrote back to *that* file, and
  no stray `config.json` appeared. Launching with the overlay enabled leaves focus on the main window (title showed
  `OpenFan — work.json`; `xdotool getactivewindow` never reported the overlay).
- **Idle cost measured**, not assumed: 1.04 s vs 0.91 s CPU per 30 s wall with the overlay on vs off (~0.03 % of one
  core). Closing the main window hides it (`e.Cancel = true` + `Hide()`), so the DispatcherTimer keeps ticking and the
  overlay keeps updating from the tray process.
- **A second top-level window breaks `Close()`-based exit.** The header Exit button used to set `Exiting = true` and
  `Close()` the main window, relying on Avalonia's default `ShutdownMode.OnLastWindowClose`. Once the desktop overlay
  existed, "the last window" was no longer the main one: Exit hid the UI and left a live process with no visible UI.
  Exit now calls `IClassicDesktopStyleApplicationLifetime.Shutdown()` (what the tray Exit always did). Any future
  top-level window — a dialog, a second HUD — has the same consequence. Closing the window with the WM close button
  must keep *hiding* to tray; both paths are verified separately.
- **NVML sensor ids put the kind in segment 2**: `nvml:<uuid>:<kind>:<index|label>`. Code reading `parts[3]` gets
  `"w"` or `"core"` and yields labels like "GPU w". hwmon ids have a different shape, so don't share one splitter.
- **`dotnet test tests/OpenFan.Core.Tests` does not rebuild the Avalonia app.** A correct Core fix looked broken in the
  lab because the launched `openfan.dll` was stale; build `src/OpenFan.Linux.App` before any visual check.

- **Identical GPUs are only distinguishable by PCI bus**, so the bus goes in every GPU-facing label, not just the
  tooltip: `HudDescribe.Of(…, pciBus:, gpuIndex:)` appends it (skipping duplicates when the name already carries one),
  and the picker's GPU submenu puts it **first** (`GPU 3 · E1:00.0 — RTX PRO 6000 Blackwell Workstation Edition`)
  because menu width truncates long names from the right and would cut off exactly the disambiguator. This machine has
  two RTX PRO 6000 cards at `11:00.0` and `E1:00.0`, identical text without it — the same reason `GpuFormat.GpuLabel`
  exists for the fan pages.
- **"Only after a reboot" was a startup race, not a scaling bug.** OpenFan autostarts at login, and GNOME writes
  `Xft.dpi` into the X resource database shortly *after* autostart apps begin. Reading it too early yields nothing, so
  Avalonia scales at 1.0 — on this box (2 × 4K BenQ/Coolermaster, monitor scale 1.25, text scaling 1.21) that is the
  difference between a 3096×2108 window and a 1340×974 one. Manual launches minutes later always look correct, which
  is what made it look unreproducible. Automatic mode now waits up to 2.5 s for the hint, but only on a Wayland
  session and only when it is genuinely absent, so ordinary starts are unaffected.
- **`AVALONIA_SCREEN_SCALE_FACTORS` needs connector names.** Measured here: `*=2.4` changed nothing at all while
  `DP-2=2.4;DP-3=2.4` produced the expected size — a wildcard pin is a silent no-op, which is worse than no pin.
  Pins enumerate connectors via mutter's D-Bus (`GetCurrentState`) with an `xrandr --listmonitors` fallback.
- **Don't derive the factor from mutter's per-monitor scale alone.** Tried and rejected: on GNOME/XWayland an X pixel
  is not a device pixel (X reports 6144 px across a 3840 px panel, so 0.625 device px each), and text scaling is
  separate. `Xft.dpi 232` is exactly `1.25 × 1.21 / 0.625`, while mutter's bare `1.25` reproduces the tiny-window bug.
- **Xvfb cannot validate HiDPI scaling work.** Its DPI baseline differs from a real compositor's: the rescue path and
  the control both rendered identically there, which would have looked like a fix that did nothing. Verify scale
  changes on the actual session (`Settings ▸ Window scale` reports the factor in use).

- **The user's deployed copy is pinned to `ba2aa30`.** On their real GNOME Wayland session, builds from today's
  commits showed as an invisible window: mapped, sized correctly, drawing nothing, and still on top of other windows
  swallowing clicks. Cause not yet isolated. Their autostart and menu entries point at `~/my_programs/openfan-linux`,
  which holds a self-contained build of the last known-good commit; `.desktop.bak-<date>` copies keep the previous
  dev-wrapper wiring.
- **Window geometry proves nothing about rendering.** Every Release build today was "verified" by measuring size with
  xwininfo — a fully transparent window measures exactly like a correct one, so those checks could not have caught this.
  Verification now means capturing pixels and looking at them (`hd shot` on the virtual display).
- **Xvfb cannot expose GL presentation bugs.** There is no GLX there, so Avalonia always falls back to software
  rendering — which is why every lab screenshot looked perfect while the real session drew nothing. Lab checks prove
  startup and layout, never that GL presents correctly on hardware.
- **Never launch-and-kill repeatedly on the user's live desktop.** Roughly ten start/SIGTERM cycles on their session
  while windows were mapped is the leading suspect for the empty-surface state, and it also left compositor damage.
  Use `hd` on :99; touch :0 only with a single launch they have agreed to.

- **NVMe identity comes from the controller class directory, not the hwmon symlink.** `/sys/class/hwmon/hwmonN/device`
  is a *relative* link and the class directories are themselves links, so resolving it to a real path is unreliable; the
  link's **name** (`nvmeN`) is enough, and `/sys/class/nvme/nvmeN/model` + `address` are plain world-readable files.
  `MainWindow.NvmeLabels` (hwmon index → model + PCI) feeds both the Sensors page headings and the HUD picker, so the
  two screens cannot disagree about what a drive is called. PCI leads in HUD labels because this box has two drives of
  the same model (`a9:00.0` / `ad:00.0`) and menus truncate from the right.
- **MenuItem headers eat underscores.** Avalonia parses `_` as an access-key marker, so sensor names arrived mangled —
  "WD_BLACK SN850X" rendered as "WDBLACK SN850X", "PCH_CHIP_CPU_MAX_TEMP" as "PCHCHIPCPU_MAX_TEMP". `MainWindow.MenuText`
  doubles them; verified against sysfs that labels now render exactly as the kernel reports them. TextBlock rows were
  never affected, which is why only menu paths needed it.
- **Temperature entries need qualification too**: five NVMe hwmon chips all report "Composite", so the picker prefixes
  the chip (`nct6799 · AUXIN5`) and, where a label still collides, widens it to `HudDescribe.ChipInstance`
  (`nvme (hwmon2) · Composite`). The hwmon number is kernel assignment order, not stable device identity — shown as an
  instance marker rather than implied to be one. GPU temps need nothing extra: their inventory names already carry the bus.

- **The rebuild signature must hash everything `Rebuild()` reads.** It hashed only the tiles, so changing tiles-per-row
  from the Tray page saved the setting and left the strip laid out the old way — a silent no-op. The overlay's own menu
  masked it because that path nulls `_signature` before refreshing. Columns and scale are in the hash now
  (`{columns}x{scale}|…`).

- **Position restore is screen-aware**: the saved point is resolved against the screens that exist *now*
  (`HudLayout.ClampIntoBounds`, pure + tested). A strip parked on a monitor that has since been unplugged would come
  back at coordinates nobody can see, and an invisible window cannot be dragged back — which reads as a broken overlay
  rather than a changed desktop. At least 35 % stays on screen, and the corrected position is saved so it does not
  re-fight on every boot. Right-click ▸ **Reset position (top-left)** is the manual escape hatch.
  Lab-verified: saved `5000,4000` on a 1600x1000 display → landed at `1495,930` with ~105 px visible; reset → `24,60`.
- **Empty state is a labelled strip, not a blank box**: removing every tile leaves a slim "no sensors" pill so the
  window still has a body to right-click (Hide / Configure) instead of an unexplained floating rectangle.
- **Missing reading renders as `—`**, never 0 — a HUD that invents an idle-looking 0 W CPU is worse than a blank tile.
- Position restore needs **bounded self-correction**: the WM re-places borderless windows *after* `Show()` returns, so
  `OnPositionChanged` nudges back up to 4 times, then accepts the WM's choice and saves that instead of looping forever.

**Gotchas catalog (all personally verified)**
- Avalonia **Grid does NOT auto-place children** — every child needs explicit `Grid.Column` or they stack on top of
  each other in column 0 (caused the GPU-page overlap bug). Code-behind: use `Col(el, n)` helper (MainWindow).
- Bare `<ContentPresenter/>` in a custom ControlTemplate renders NOTHING — must bind
  `Content="{TemplateBinding Content}"` explicitly (invisible nav rail bug).
- NVML struct-array out-params need a **GCHandle-pinned buffer passed as nint** — the default marshaller hands native
  code a copy (all-zero pids bug). Probe with null/0 first for count.
- `Path.GetFullPath` is lexical; sysfs `device` entries are symlinks — read *through* them (`…/device/model`) or use
  `ResolveLinkTarget`. NVMe temp chips live at `/sys/class/hwmon/hwmonN` (name "nvme"), NOT under the drive dir.
- Code-created ComboBoxes DROP pre-attach selection → re-assert on `AttachedToVisualTree`; heal via SelectedIndex=-1.
  NumericUpDown Width≥120; Value/Min/Max are decimal (cast!). `Items` get-only. Focus-away:
  `TopLevel.GetTopLevel(x)?.FocusManager?.ClearFocus()`. Shapes namespace clashes with System.IO.Path — qualify inline.
  Detached MenuFlyout: `FlyoutBase.SetAttachedFlyout` + `ShowAttachedFlyout`. No string→Thickness conversion in C# —
  `new Thickness(...)`. Attached properties in object initializers need post-init setters (`Grid.SetColumn`).
- NVML notes: GetPerformanceState (not GetCurrentPState); GetPcieThroughput counters 0=TX/1=RX KB/s; process structs
  are `{uint pid; uint pad; ulong usedGpuMemory; …}` ×24B; rc 7 = INSUFFICIENT_SIZE on count probe; PowerMax constraint
  can read 0 (unbounded) — clamp UI max to 800 in that case.
- `HelperServer` shutdown: a session's pending `ReadLineAsync(_cts.Token)` throws `OperationCanceledException` when
  the service stops with a client attached. That must be caught (and teardown wrapped in `finally`) or
  `DisposeAsync` rethrows and skips socket removal — fixed, regression test in `CpuPowerControlTests.cs`.
- Avalonia `ToolTip` is attached-only: `ToolTip.SetTip(ctrl, "...")`. The WPF-style initializer
  `ToolTip = { Tip = "..." }` does not compile. Likewise `ColorView`/`ColorPicker` are **not** in Avalonia core —
  they ship in a separate package with theme-resource requirements; the HUD colour dialog is built from three RGB
  sliders + hex box instead, which also avoids offering an alpha channel a solid tile cannot use.
- headless-lab `hd status` reports **+0+0 for borderless windows** even when they are correctly placed — read
  `xwininfo -id <win> | grep Absolute` for the truth. Cost me a false "position restore is broken" conclusion.
- Unit-test flakiness was real, not environmental: it came from the above. If a socket test flakes, suspect the
  product code before adding retries.

## 5. Pages
**Home** — fan cards (click-to-rename, ☑Curve + dropdown, cmd%/measured% + RPM value line, Calibrate link where a
real tach reads, error line) then Curves section (flat ±5/box, graph live-sensor dropdown + mini preview + Edit,
mix function+children; × delete with unassign flyout). Header: Apply curves · Refresh · ⋮ Save/Load setup · Exit · clock.
FABs Flat/Graph/Mix create curves.
**CPU** (`49546a8`) — stats row **Temp · Socket power · PPT · TDP · TjMax · Load · Frequency · RAM used**; load and
socket-draw bars; `Socket power limit (PPT)` editor (100–300 W, ±5 step) with *keep after reboot* checkbox + Apply
(green applied / amber "unit not installed" / red failure with reason); note line shows the **flashed** PPT, which
legitimately differs from the live cap after an apply. TDP/TjMax are display-only with tooltips explaining they come
from BIOS flash. Nothing here affects fan behaviour by design.
**GPUs** (`144e7e6`, fixes `f8825ec`) — per card: header (GPU n · name busId / uuid), Power [W] Apply editor
(green/red note; persists + saves config), GPU util bar + VRAM used/total GiB bar, stats row Temp · Power draw/limit ·
P-State · Fan % · Fan RPM (— on 595) · Clocks G/S/M, PCIe gen×width + RX/TX rates, process table top-12 by VRAM
(name/PID/kind/mem; rebuild only when the pid|name|mem key changes). WDDM/MCDM badges skipped (Windows-only).
**Sensors** (`7c4808d`, `0a3d0eb`) — all temps + tachs + GPU fan-% controls, sections CPU / GPU / Motherboard /
Chipset-ASUS EC / AMD HSMP / Storage (NVMe) / Network / Other via `SensorGroup()` chip mapping; sections with several
devices get collapsible ▾/▸ per-device headers (NVMe labeled with model from sysfs). Click-to-rename → `SensorAliases`;
aliases drive every sensor dropdown app-wide (curve cards rebuild on commit); tooltip = original name + group + id.
**Tray** (`78bca67` + this pass) — desktop-overlay setup: enable checkbox (seeds CPU power/temp + each GPU's
power/temp on first enable), tiles-per-row, overlay size (XSmall 0.56 / Small 0.75 / Normal 1.0 / Large 1.35 /
Huge 1.8 → `HudScale`; the floor in `HudLayout.MinScale` is 0.5 so XSmall stays readable), a *Keep above other windows* checkbox (`HudTopMost`, mirrored by the strip's own right-click menu), and a
tile list with colour swatch (8-swatch menu or full RGB dialog with hex paste), **click-to-rename label** (same
interaction as fan cards: commit on blur or Enter; empty falls back to the sensor-derived name, never blank — its
tooltip leads with `HudDescribe.Of`, which resolves NVML UUIDs to GPU names via `Nvml.SnapshotAll()` and honours
Sensors-page nicknames, with the raw id on a second line), unit,
↑/↓ ordering and ✕ removal. Every edit applies to the live overlay immediately — the overlay is its
own preview, so there is no mock-up to drift. Tray icon itself unchanged: Open / Apply curves / Exit.
**Calibration window** — see §6 of previous revision / commit `f62c927`: live slider, auto-step sweep, avoid flags,
validation trio gating Ok, get-or-create Cfg() rows.

## 6. Recent commits (newest first)
overlay tray picker UI + docs pass (this commit) · `78bca67` desktop overlay (HUD): always-on-top sensor tiles,
user-chosen colours, headless-testable formatting/contrast · `cc8f717` record persistence verification ·
`53fce3f` docs brought in line with implementation · `ba917dc` reproducible launcher installer ·
`b60265f` vendored hsmp-control + persistence installer · `49546a8` CPU socket power control (PPT settable,
TDP/TjMax read-only, checkbox policy fix, helper shutdown fix) · `d3c322e` CPU page labels honour HSMP semantics ·
`e96980c` HsmpPowerReader extraction + `--cpu-power` · `2368667` CPU page RAM · `0a3d0eb` Sensors fan-% + collapsible
subgroups · `e00092d` PageUp/Down scroll · `7c4808d` Sensors page + aliases · `f8825ec` GPU page fixes ·
`144e7e6` GPUs page + power limits via helper.

## 7. Pending / next

**Verified on hardware (reboot tests performed by the user):**
- **Unticked across reboot** → firmware's own value returns. Confirmed again this boot: unit skipped
  (`ConditionPathExists`), flash 295 W, and a later manual Apply of 270 W is visible as live ≠ flash exactly as designed.
- **Ticked across reboot** → the configured limit survives while BIOS still reports 295 W, which is the point: persistence
  is re-application by the boot unit, not a firmware write.
  Caveat for whoever picks this up: that test leaves no trace on disk — a later unticked Apply deletes
  `/etc/hsmp-control/ppt_mw` — so it must be re-run rather than audited after the fact. Recipe: set a non-295 value with
  the box ticked, reboot, then `./tools/hsmp-control/hsmp-control limits --json` should show live == configured while
  `ppt_bios_mw` stays 295000, and `journalctl -b -u hsmp-control-apply.service` should show it ran instead of skipping.

**Verification still open (say so honestly if asked what is proven):**
0. **Overlay on real GNOME (mutter), not just the lab's Openbox**: always-on-top stacking against ordinary windows and
   drag feel are lab-verified only; the user opted to tick *Tray ▸ Show the desktop overlay* themselves rather than let
   an agent edit their live profile, so this is deliberately outstanding, not forgotten.
1. **Sub-200 W behaviour** on this board: the window allows 100 W but nobody has confirmed the SMU accepts it; readback
   would report a clamp, so the worst case is visible rather than silent.
2. Optional: *reset to BIOS default* button on the CPU tab — `cpupower default` exists in the protocol and is tested;
   only the UI control is missing.

**Feature/packaging backlog:**
1. `.deb` packaging (dpkg-deb script); polkit alternative to the helper service. Manual chain in packaging/README.md.
2. Consider an integration test for SnapshotAll against live NVML (skip on CI).
3. Nice-to-haves: calibration-estimated RPM while applying; recent-setups submenu; collapsed-state persistence for
   Sensors subgroups; sensor aliases also in tooltips of dropdown items; card drag-reorder (⋮ Move up/down shipped instead).

**Config files (menu, Windows parity):** header ⋮ = Create new / Save / Save as… / Load… + Open error log + Exit, with Ctrl+N/S/Shift+S/L. Active-file model: `Save` always writes the *current* config; Save-as/Load switch it; a sidecar `$XDG_CONFIG_HOME/openfan/active` remembers the choice (default = absent; stale pointer → falls back to default). Title shows "OpenFan — myconfig.json" when named. Order matters: write file BEFORE SwitchConfig (it loads from disk). Error log: $XDG_STATE_HOME/openfan/errors.log via AppDomain/TaskScheduler hooks.
**Resolved "lab quirk" (was an app bug):** link-style buttons built with `Background = null` are only hit-testable on the glyph's ink pixels — clicks landing between strokes (dead center of ×, say) fall through. All such buttons now use `Background = Brushes.Transparent` so the whole padding box is clickable. This retroactively explains every "synthetic click ignored" mystery (Edit links, card ×, unhide pills).
⚠ LAB HYGIENE: a lab app inherits the REAL session bus → StorageProvider pops portal dialogs on the user's desktop. Run lab apps as `./hd run -- env -u DBUS_SESSION_BUS_ADDRESS dotnet …` — Avalonia then uses its internal fallback picker (renders inside :99, drivable: click Name field, ctrl+a+Delete via focused-window xdotool, type, Save).
⚠ Xvfb can die between calls (`hd status` → DOWN): just `hd start` again.
KEYBOARD (fixed 2026-09-22): `hd key COMBO [window-name]` now activates the window first — XTEST keys only reach the
FOCUSED window and fresh Openbox maps don't focus. Always pass the name: `./hd key ctrl+s OpenFan`.
Do NOT use `xdotool key --window` (XSendEvent) on Avalonia — unreliable. NB: this port has NO Ctrl+1..7 nav shortcuts
(Windows-only); implemented config keys are Ctrl+N / Ctrl+S / Ctrl+Shift+S / Ctrl+L.

**DONE since last handover (2026-09-24):** CPU tab PPT control end to end (helper commands, µW conversion, readback
verification, 100–300 W window shared with the C tool) · CBS reader for TDP/TjMax/flash-PPT with fail-closed
validation · persistence model + `install-persistence.sh` + enabled boot unit on this machine · checkbox made the
single persistence switch (fixes unchecked-box-survives-reboot bug) · boot-unit detection with amber warning ·
helper shutdown cancellation fix · stale-helper error translated into an actionable message · CPU stats row relayout
(PPT/TDP/TjMax prominent) · vendored `tools/hsmp-control` + docs · rebuild-proof desktop launcher.

## 8. Standing cautions
- Smoke runs load the user's REAL config with real group access — fans may briefly follow curves during test windows.
  GPU auto-restores via helper EOF on disconnect; board resumes next app run. Keep smoke runs short; disclose long ones.
- Anything that writes `power1_cap` changes CPU power behaviour machine-wide and is **not** restored by the helper on
  disconnect (by design). Prefer out-of-window values when probing the socket — they are refused before any write, so
  capability checks have no side effects.
- Rebooting the user's machine needs explicit permission; it is the only way to prove boot-time behaviour.
- **Pushing to GitHub:** no credential lives in git config on this machine. The user supplies a token file; feed it to
  git through a mode-700 `GIT_ASKPASS` helper that reads the file itself (never put the secret in argv, in a remote URL,
  or in git config), run `git push -u origin main`, then delete the helper. Never write the token path or value into
  these docs. If pushing fails with "could not read Username", the repo is simply being read anonymously — pushes need
  that credential.
- Write tool requires a read-tool observation after files are touched by bash/python/sed (python in-place edits fine).
- `grep -c` with 0 matches exits 1 — harmless inside chains, but see §2 commit-chain caution.
- Do not push firmware images, extracted BIOS/IFR blobs, or NVRAM variable dumps. The research tree that produced
  the CBS offsets lives outside this repo and stays there deliberately.
