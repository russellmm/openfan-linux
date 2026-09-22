# Design & implementation plan: OpenFan on Ubuntu

**Status:** Approved — implementation underway (Phase 2); hardware spike answers in `spike-notes.md`  
**Date:** 2026-09-16  
**Companion:** Windows OpenFan at `E:\hermes_working\OpenFan\` (WPF + LHM + NVML)  
**Intent:** Same product job as Windows OpenFan — own motherboard/case/AIO fans and NVIDIA GPU fans with Flat / Graph / Mix curves — on Ubuntu, without LibreHardwareMonitor, WinRing0, HWiNFO, or WPF.

This is not a pixel port of the WPF app. It is a Linux-native OpenFan that **reuses curve math and config ideas**, and **replaces every hardware and OS integration**.

---

## 1. Outcome and success

### What

**OpenFan Linux** is a logged-in Ubuntu app (plus optional systemd user service later) that:

- Discovers PWM fan **controls** and temp/tach **sensors** from Linux `hwmon` (SuperIO / nct67xx / it87 / CPU k10temp / NVMe / etc.).
- Discovers NVIDIA GPUs via **NVML** (`libnvidia-ml.so`) — same per-fan `SetFanSpeed_v2` model as Windows, including PRO 6000 independent fans and a **30% floor**.
- Binds controls to **Flat / Graph / Mix** (Max / Min / Average).
- Calibrates PWM↔RPM, pairs tachs, hides unused controls, drag-reorders cards.
- **Apply curves** is off until checked. Exit restores kernel/BIOS automatic (hwmon `pwmN_enable=2` or chip default) and NVML default fans.
- Dark Fan Control–style cards; accent color; named configs.

### Why Ubuntu is a different product underneath

| Windows OpenFan | Ubuntu OpenFan |
|-----------------|----------------|
| LibreHardwareMonitor + WinRing0 | `/sys/class/hwmon` + `lm-sensors` |
| HWiNFO Gadget registry | hwmon labels, `sensors`, optional `liquidctl` / HID |
| `requireAdministrator` + scheduled task | udev + `pwm` group / CAP, optional polkit |
| WPF + WinForms tray | GTK4 or Avalonia + StatusNotifierItem |
| NCT6701D via LHM SuperIO | `nct6775` / `nct6683` kernel driver (board-dependent) |
| Sleep: reopen LHM + re-SetSoftware | Resume: re-write `pwmN` every tick (same EC fight) |

### Success (testable)

1. On a supported Ubuntu box with `hwmon` PWM and NVIDIA driver: discover at least one board fan + GPU temp/fans.
2. Graph on CPU Tctl (k10temp) can drive a case fan PWM.
3. Mix(CPU Graph + GPU Graph) can drive a motherboard fan.
4. Each PRO 6000 fan is its own control, 30% floor, PCI bus in the name. *(verified via `--dump`; live per-fan write needs root — see §3.4 note)*
5. Apply off = monitor-only. Apply on writes PWM every ~1 s. Exit restores auto.
6. Uninstalling `fancontrol` / CoolerControl is possible without losing curves (user smoke, not a v1 gate).

### Non-goals (v1 Linux)

- Pixel-perfect clone of Windows OpenFan or rem0o Fan Control.
- Windows Service equivalent as a **system** daemon that runs at boot before login (phase 2).
- HWiNFO, LHM, WinRing0.
- Sync / Auto / Trigger / Offset / Time-average curves.
- Auto-installing kernel modules or DKMS.
- Controlling Dell/HP/Lenovo EC fans that have no hwmon PWM (document as unsupported).
- Shipping a `.deb` to Ubuntu universe in v1 (local install / tarball is enough).

---

## 2. Target user and hardware assumptions

Primary user is still **russell**. Two plausible Ubuntu targets:

**A. Same TRX50 + 9970X + PRO 6000s (dual-boot or future Linux box)**  
- SuperIO: Nuvoton NCT6701D — Linux support is **not** as mature as Windows LHM. Need a spike: does `nct6775` bind? Are `pwm1`…`pwm7` writable?  
- CPU: `k10temp` — Tctl/Tdie; CCD temps on Zen 5 HEDT may need kernel ≥ 6.x with the same k10temp map we patched into LHM (0x599F0). If the running kernel lacks Turin CCD, Graph sources stay Tctl until a kernel bump.  
- NVIDIA: proprietary driver + NVML. MCDM/TCC are Windows; on Linux the cards are normal `nvidia` devices. Fan set still needs a driver that implements NVML fan APIs (same 30% floor).

**B. A generic Ubuntu workstation**  
- hwmon PWM from whatever `sensors-detect` loaded.  
- NVIDIA consumer GPU with 0% floor.

v1 design must work on **B** and treat **A** as a first-class spike, not a hard blocker of the whole port.

---

## 3. Architecture

### 3.1 Do not share the Windows WPF project as-is

`OpenFan.Core` is `net8.0-windows` because it references **LibreHardwareMonitorLib**. Curve math (`GraphCurve`, `MixCurve`, `HysteresisGate`, `CalibrationMap`, `FanController`) is otherwise portable.

**Decision (recommended): split the Windows tree later, but Linux v1 is a sibling project.**

```
OpenFan/                 # existing Windows app (untouched until a later extract)
OpenFan.Linux/           # new tree  E:\hermes_working\OpenFan.Linux  or  ~/src/openfan
  src/OpenFan.Curves/    # copy or extract: Graph, Mix, hysteresis, calibration, FanController
  src/OpenFan.Linux.Hw/  # hwmon + NVML backends, IFanActuator
  src/OpenFan.Linux.App/ # UI
  tests/
```

**Do not** reference LHM from the Linux app. Copy curve/config types first (MIT). After Linux v1 works, optionally extract a shared `OpenFan.Curves` NuGet used by both.

### 3.2 Process model (v1)

Same as Windows: **one user session process**.

- Window close (X) → hide to tray (StatusNotifierItem); curves keep applying.
- **Exit** → restore hwmon auto + NVML default, then quit.
- Single instance: `flock` on `$XDG_RUNTIME_DIR/openfan.lock` (not a Windows mutex).
- Tick ~1 s: read sensors, evaluate curves, **write PWM every tick** (do not skip when % unchanged — same SuperIO/EC lesson as NCT6701D).
- Apply checkbox persisted. Missing temp → skip that control (do not restore BIOS after two empty polls).
- Resume from sleep: `org.freedesktop.login1` PrepareForSleep signal → reset applies and keep ticking (NVML + hwmon).

**Phase 2:** systemd **user** service `openfan.service` (`WantedBy=default.target`) so curves survive closing the GUI. Still not a boot-before-login system service.

### 3.3 UI toolkit

| Option | Pros | Cons | Verdict |
|--------|------|------|---------|
| **Avalonia 11 + .NET 8** | Dark cards close to WPF; share C#; tray via `Avalonia.Controls.TrayIcon` | Extra runtime on Ubuntu; not GNOME-native | **Recommended** if visual parity with Windows OpenFan matters |
| GTK4 + libadwaita (C# gir.core or Python) | Native Ubuntu/GNOME | Rewrite all cards; two UIs forever | Better if “feels like Settings” matters more than Fan Control look |
| Qt / QML | Fine | Third ecosystem | Reject |
| Electron | Fast UI | Wrong for a 1 Hz privileged PWM writer | Reject |

**Decision:** Avalonia + .NET 8 for v1 Linux, matching the existing dark card language (underline combos, accent Mix ✕, mini graphs). Revisit GTK only if Avalonia tray/polkit integration is painful.

### 3.4 Privilege model

Writing `/sys/class/hwmon/hwmonX/pwmN` usually requires root **or** the file to be group-writable.

**v1 recommended path (least surprise on a personal workstation):**

1. Ship a udev rule: `SUBSYSTEM=="hwmon", ACTION=="add", RUN+="/usr/local/lib/openfan/pwm-acl.sh"` that `chgrp` pwm nodes to group `openfan` and `chmod g+w`.
2. Install user in group `openfan` (log out/in once).
3. App runs **as the user**, not as root. No pkexec on every tick.
4. NVML fan set typically works as the logged-in user if the NVIDIA device nodes are accessible (`video` / `render` group). If `nvmlDeviceSetFanSpeed_v2` returns NoPermission, show a clear Settings message (do not silently monitor-only forever without saying why).

**Rejected for v1:** setuid root binary (too easy to get wrong).  
> **Linux finding (spike 2026-09-21):** NVML fan *writes* return NO_PERMISSION for uid 1000 even with world-writable `/dev/nvidia*` — the driver gates config writes on root euid. hwmon PWM is fine via the ACL. So a privileged path for GPU fans is **required**, not conditional.

**Helper: BUILT 2026-09-21.** `src/OpenFan.Linux.Helper` → root systemd service `openfan-helper`; Unix socket `/run/openfan/helper.sock` (0660 root:openfan), line protocol accepting only `nvml:*:fan:*` ids, restore-on-disconnect per session (crash-safe). GUI/CLI auto-route GPU writes through it when the socket exists; direct NVML remains for sudo debugging. polkit/user UI for install deferred.

---

## 4. Hardware backends

All backends implement the same shapes Windows already uses:

- `HardwareItem` (Id, Kind = Control | Temperature | Tach, Name, Backend, Group, MinPercent, CanSet)
- `HardwareReading` (Id, Value)
- `IFanActuator.SetPercent` / `SetDefault`

### 4.1 HwmonBackend (replaces LHM)

**Enumerate** `/sys/class/hwmon/hwmon*`

For each device:

- `name` (e.g. `nct6798`, `k10temp`, `nvme`)
- `tempN_input` (milli-°C → °C)
- `tempN_label` if present (else `tempN`)
- `fanN_input` (RPM)
- `pwmN` (0–255)
- `pwmN_enable` (0=off/full, 1=manual, 2=auto — kernel-dependent)

**IDs (stable, like NVML UUIDs / HWiNFO stable gadget ids):**

```
hwmon:{chipName}:{hwmonIndex}:temp:{n}
hwmon:{chipName}:{hwmonIndex}:pwm:{n}
hwmon:{chipName}:{hwmonIndex}:fan:{n}
```

Prefer `name` + canonical labels over raw `hwmon3` index (index can change at boot). If two chips share a name, include PCI/platform path from `device` symlink.

**SetPercent(id, 0–100):**

- Map 0–100 → 0–255: `pwm = round(percent * 255 / 100)`.
- Write `pwmN_enable = 1` (manual) then `pwmN = pwm`.
- Every tick, rewrite both (same as Windows SetSoftware re-fire).

**SetDefault(id):**

- Write `pwmN_enable = 2` if the chip supports auto; else `1` with last BIOS-ish value is not knowable — document as “auto if the driver supports it”.
- Cache the enable mode **before first manual write** so restore is accurate (`_initialEnable[id]`).

**PRO / NVIDIA hwmon:** the `nvidia` hwmon node may expose GPU temp. **NVML still owns GPU fans** if both exist (same merge rule as Windows).

**Calibration:** same PWM% → RPM table as Windows. Slider writes SetPercent; live RPM from paired `fanN_input`.

**Spike (must happen before promising TRX50):**

- On Ubuntu with NCT6701D: `ls /sys/class/hwmon/*/name`, `pwm*_enable`, `pwm*`.  
- If no PWM files: OpenFan Linux can still do **GPU fans + display board temps**, but case fans stay out of v1 for that board until a driver exists. That is an explicit fork in the plan, not a silent failure.

### 4.2 NvmlBackend (port of Windows NVML)

Almost 1:1 with `NvmlBackend.cs`:

- P/Invoke `libnvidia-ml.so.1` instead of `nvml.dll`.
- Same structs, `nvmlDeviceGetPciInfo_v2`, per-fan set/default, PCI bus in labels (`RTX PRO 6000 Blackwell 2D:00.0 Fan 0`).
- Floor 30% if name contains `PRO 6000`, else 0.
- IDs stay `nvml:{uuid}:fan:{n}` so a future shared config could even round-trip GPU curves (board hwmon ids will not).

**GPUs page:** same telemetry (util, VRAM, power, clocks, P-state, processes). **No WDDM/TCC/MCDM** — those are Windows driver models. Replace the Mode dropdown with Linux-only facts: persistence mode, `nvidia-smi -q` power limit, maybe compute vs display. Power limit via `nvmlDeviceSetPowerManagementLimit` if permitted.

### 4.3 Optional read-only extras (v1.1, not v1)

| Source | Role |
|--------|------|
| `liquidctl` / HID AIO | Pump/fan RPM + liquid temp when hwmon does not see the AIO |
| `nvme` hwmon | Already under HwmonBackend if the kernel exposes it |
| lm-sensors `sensors -j` | Debug dump only; do not parse as primary |

No HWiNFO. Chipset / SPD Hub friendly names on ASUS: if hwmon has `tempN_label`, use it; otherwise `Temperature #n` like LHM.

### 4.4 Inventory merge

1. Hwmon controls + temps + tachs.  
2. NVML GPU temps/fans.  
3. Drop hwmon items that look like NVIDIA if NVML is up (name/pci match).  
4. Hot-plug: re-scan hwmon + NVML each tick (USB AIO, GPU bind). Fingerprint IDs; refresh dropdowns without requiring restart (same lesson as HWiNFO-after-login).

### 4.5 Conflicts

Detect and warn (Apply stays off until the user confirms):

- `fancontrol` (lm-sensors) process or systemd `fancontrol.service`
- CoolerControl (`coolercontrold`)
- `nvidia-settings` fan curve / `nvfancontrol`
- Another OpenFan instance

Do not fight silently.

---

## 5. Curves, apply loop, config

**Reuse as-is (logic):**

- Graph piecewise °C→%, hysteresis ~2 °C / 1 s (graphs only).
- Mix Max / Min / Average; no fail-count restore when a child temp is missing.
- Flat percent.
- Calibration rules (≥2 points, RPM ascending, contiguous Avoid); Avoid snap.
- Commanded % on control cards, not the last sysfs read (sysfs can lag or show 0).
- Mini graph: first→last curve points, pad, no live-temp X stretch.
- Hide / Unhide, drag reorder, named configs.

**Config path:** `$XDG_CONFIG_HOME/openfan/config.json` (default `~/.config/openfan/config.json`) + `configs/` for named files. Same JSON shape as Windows where possible (`controls`, `curves`, `applyCurves`, `accentColor`, `startupDelaySeconds`, `powerTargetsWatts`).

**Do not** expect to load a Windows `%LOCALAPPDATA%\OpenFan\config.json` and have board fans map. GPU UUID curves *might* import; document a one-way “Import GPU curves from Windows config” as v1.1.

**Log:** `~/.local/share/openfan/openfan.log` (or `$XDG_STATE_HOME`).

---

## 6. UI map (parity with Windows)

| Windows | Ubuntu v1 |
|---------|-----------|
| Home controls/curves | Same card model |
| GPUs page | Same, minus driver-mode switch |
| Settings | Start at login (`~/.config/autostart/openfan.desktop` or systemd --user), start minimized, sensor delay, nicknames, Edit sources (hwmon chips on/off, NVML on/off), Hidden, Plugins (hwmon list **is** shown — unlike LHM it is usually small enough; cap at ~80 rows with filter) |
| Theme | Dark + accent picker (same default `#E24B4B`) |
| Tray | StatusNotifierItem: Open / Exit |
| About | OpenFan version; **hwmon driver names**; NVML driver version; **Check for OpenFan updates** (GitHub). No LHM nuget check. Optional: kernel version + `pwm` ACL status |

**Edit sources** on Linux = enable/disable individual hwmon chips (by `name`) + NVML. Not LHM Motherboard/CPU checkboxes.

---

## 7. Packaging and install (v1)

- Publish: `dotnet publish -r linux-x64 --self-contained true` (easier for a personal box) **or** framework-dependent if .NET 8 runtime is installed.
- Files:
  - `/opt/openfan/OpenFan` (or `~/.local/opt/openfan`)
  - `/etc/udev/rules.d/99-openfan-pwm.rules`
  - `/usr/share/applications/openfan.desktop`
  - optional `~/.config/autostart/openfan.desktop`
- Dependencies: NVIDIA driver (for GPU), kernel hwmon. `lm-sensors` recommended for `sensors-detect` once.
- **No** `sudo ./OpenFan` as the supported run mode.

---

## 8. Security

- Never run the GUI as root.
- Udev ACL is write access to PWM only, not arbitrary sysfs.
- Tick writer validates percent 0–100 and known ids.
- Autostart does not enable Apply until the user has checked it once (same Windows default).
- Log PWM writes at debug, not every tick at info (spam).

---

## 9. Testing strategy

**Unit (no hardware):** curve math, Mix, hysteresis, PWM 0–100 ↔ 0–255, hwmon path parser (fixture sysfs tree in tests), NVML PCI label format, config round-trip.

**Integration (optional VM):** fake sysfs overlay.

**Manual smoke (real box):**

1. `sensors` lists chips; OpenFan Plugins matches.
2. Apply off: PWM enable stays auto.
3. Flat 40% on a case fan: `cat pwmN` tracks; RPM moves.
4. Exit: `pwmN_enable` back to 2 (or documented fallback).
5. GPU: two PRO 6000s distinguishable by PCI bus; independent fans; 30% floor.
6. Sleep/resume: fans return to curve without restarting the app.
7. CoolerControl running: warn, stay monitor-only until Take over.

---

## 10. Implementation phases

Do **not** start Phase 2 until Phase 0 spike answers are written into this doc.

### Phase 0 — Hardware spike (1 session, no UI)

On the intended Ubuntu machine:

- [x] `uname -r`, NVIDIA driver version, `nvidia-smi`
- [x] `ls /sys/class/hwmon/*/name` and for each: temp/fan/pwm files
- [ ] Can a non-root user write `pwm1` after a one-off `chmod`?
- [ ] NVML: small C# or Python `nvmlInit` + fan count + set 30% on one PRO fan, restore default
- [x] k10temp: which temp labels exist (Tctl, CCD?)
- [x] NCT6701D: present or not

**Exit criteria:** a short `spike-notes.md` with yes/no for board PWM and GPU NVML. If board PWM is no, v1 is **GPU-only + read-only board temps**.

### Phase 1 — Shared curves + fake hwmon

- [x] New `OpenFan.Linux` solution, MIT
- [x] Copy curve/config/FanController tests from Windows Core (strip LHM)
- [x] Fake `ISensorBackend` + Fake actuator; TDD apply loop (re-apply same %)
- [x] JSON config under XDG

**Checkpoint:** `dotnet test` green on Ubuntu and on Windows (the Linux tree should build on both).

### Phase 2 — Real HwmonBackend + CLI smoke

- [x] Enumerate real sysfs
- [x] `openfan-linux --dump` prints inventory (like DumpLhm)
- [x] SetPercent / SetDefault on one pwm with `--apply-once`
- [x] udev rule prototype

**Checkpoint:** command-line can hold a case fan at 40% and restore auto.

### Phase 3 — NVML on Linux

- [x] Port NvmlBackend to `libnvidia-ml.so.1`
- [x] PCI labels, 30% floor, merge rule
- [x] `--dump` includes GPUs

**Checkpoint:** independent PRO fans from CLI. ✅ PASSED 2026-09-21 (root run; user-run needs the §3.4 helper).

### Phase 4 — Avalonia shell

- [ ] Dark window, Home cards (Control / Flat / Graph / Mix) — port layout ideas, not XAML
- [x] Apply checkbox, tray, single-instance, Exit restore — user-verified on target 2026-09-21
- [ ] Graph editor ✓ (canvas add/drag, sensor, hysteresis, max-speed), calibrate, hide, drag-reorder
- [ ] Settings, Theme accent, About (versions + Check for OpenFan updates via GitHub)

**Checkpoint:** daily-driver usable on GPU fans; board fans if Phase 0 allowed.

### Phase 5 — GPUs page + polish

- [ ] Telemetry + power limit (no Windows driver modes)
- [ ] Sleep/resume
- [ ] Conflict detector
- [ ] Autostart
- [ ] README, man-ish `--help`, this spec marked Implemented

### Phase 6 (later)

- [ ] systemd --user service
- [ ] Windows/Linux shared `OpenFan.Curves` package
- [ ] `.deb`
- [ ] liquidctl
- [ ] Import GPU curves from Windows config.json

---

## 11. Task list (for when implementation is approved)

### Phase 0
- [x] Task 0.1: hwmon inventory spike notes
- [ ] Task 0.2: NVML fan set/restore spike
- [x] Task 0.3: Go/no-go on motherboard PWM for v1

### Phase 1
- [x] Task 1.1: Solution skeleton + MIT + gitignore
- [x] Task 1.2: Port curve tests (Graph, Mix, hysteresis, calibration)
- [x] Task 1.3: FanController TDD with fake actuator (always re-apply)
- [x] Task 1.4: XDG config store

### Phase 2
- [x] Task 2.1: Hwmon enumerate + stable IDs (TDD on fixture tree)
- [x] Task 2.2: pwm enable/value write + restore
- [x] Task 2.3: `--dump` / `--apply-once` CLI
- [x] Task 2.4: udev ACL + group — installed on target 2026-09-21; `--apply-once` held pwm4 @40% 15 s and restored auto(5)

### Phase 3
- [x] Task 3.1: NVML P/Invoke on Linux
- [x] Task 3.2: PCI names + floor + merge — live checkpoint PASSED under sudo: fan0→55% ramp, fan1 held 30%, default restored

### Phase 4
- [ ] Task 4.1: Avalonia app shell + tray + apply
- [ ] Task 4.2: Control + curve cards
- [ ] Task 4.3: Graph editor ✓ 2026-09-21 (Graph mode on cards auto-opens editor; live edits applied next tick) + calibrate
- [ ] Task 4.4: Settings / Theme / About

### Phase 5
- [ ] Task 5.1: GPUs page
- [ ] Task 5.2: login1 sleep, conflicts, autostart
- [ ] Task 5.3: Docs

---

## 12. Risks

| Risk | Impact | Mitigation |
|------|--------|------------|
| NCT6701D has no Linux PWM | High on TRX50 | Phase 0 spike; GPU-only v1 if needed |
| Kernel k10temp lacks Turin CCDs | Med | Use Tctl; document kernel version |
| NVML SetFan unsupported on Linux driver | High for PRO 6000s | Spike; monitor-only + message |
| udev ACL not applied until replug | Med | Document log out/in; `udevadm trigger` |
| Avalonia tray broken on GNOME | Med | Fallback: keep window; StatusNotifierItem library |
| Fighting CoolerControl | High | Conflict detector; Apply default off |
| Writing pwm every 1 s wears nothing but looks “busy” | Low | Accept; needed vs EC |
| Sharing JSON with Windows | Low | Don’t promise board id mapping |

---

## 13. Open questions (need russell before coding)

1. **Which Ubuntu machine?** Dual-boot this TRX50, or a different PC? Phase 0 must run there.
2. **UI:** Avalonia (Fan Control look) vs GTK (GNOME native)? Default in this doc: Avalonia.
3. **v1 if board PWM is missing:** GPU-only OpenFan, or wait for kernel/driver?
4. **Install prefix:** `/opt/openfan` (system) vs `~/.local` (user-only, no udev without sudo once)?
5. **Repo:** new `OpenFan.Linux` folder / GitHub repo vs a `linux/` branch in `russellmm/OpenFan`? Recommend **sibling folder + same GitHub org**, MIT, to keep Windows `net8.0-windows` + LHM isolated.

---

## 14. What we are explicitly not doing yet

- No code, no Avalonia project, no udev files, no Ubuntu packages.
- No changes to Windows OpenFan except later optional extract of curves.
- No promise that HWiNFO Chipset/SPD names appear on Linux.

When this document is approved and Phase 0 spike notes exist, implementation starts at Task 1.1.
