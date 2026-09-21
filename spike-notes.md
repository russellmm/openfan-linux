# Phase 0 spike notes — OpenFan on Ubuntu 26.04 (target machine)

**Date:** 2026-09-21  
**Machine:** TRX50 / AMD Threadripper 9970X 32-Core — spec "Hardware profile A"  
**OS:** Ubuntu 26.04.1 LTS, kernel `7.0.0-31-generic`  
**Method:** read-only probing (`/sys/class/hwmon`, NVML via ctypes). No PWM/fan writes performed.

---

## Verdicts (spec §10 Phase 0 exit criteria)

| Question | Answer |
|----------|--------|
| GPU fans controllable via NVML? | **YES** — NVML works as the logged-in user, no root. Fan *set* not yet exercised (see Pending). |
| Motherboard PWM on Linux? | **YES** — controller is **NCT6796D-S** (not NCT6701D as the spec assumed); binds with mainline `nct6775` and reports as chip name `nct6799`. **7 × `pwm*`, 6 × tach, 13 temp nodes.** Full-scope v1 is viable. |
| k10temp present? | **YES** — `Tctl` only (1 temp). No CCD labels exposed by this kernel. |
| NVIDIA driver / NVML | `nvidia-driver-595-open` 595.91.07; `libnvidia-ml.so.1` present; modules loaded. |
| .NET SDK | **8.0.131 installed** (apt) — Phase 1 unblocked. |

---

## hwmon inventory (as found)

| Node | Chip | Temps | Fans (tach) | PWM | Notes |
|------|------|-------|-------------|-----|-------|
| hwmon0–2, 4 | `nvme` ×5 | yes | no | none | NVMe temps |
| hwmon3 | `nvme` | 4 | — | none | |
| hwmon5 | `k10temp` | 1 (`Tctl`) | — | none | Tdie/CCD not exposed |
| hwmon6 | `amd_hsmp_hwmon` | 0 | — | none | HSMP platform interface |
| hwmon7 | `asusec` (ASUS EC) | 5: CPU, CPU Package, T_Sensor, VRM_E, VRM_W | **3: CPU_Opt, VRM_W HS, VRM_E HS** | **none** | Read-only temps + tach; no fan targets |
| hwmon8 | `enp173s0` (NIC) | 2 | — | none | |
| hwmon9 | `asus` (WMI) | 0 | 0 | none | Empty node |
| **hwmon10** | **`nct6799`** (NCT6796D-S via `nct6775`) | 9 labeled: SYSTIN, CPUTIN, AUXTIN0–4, PECI/TSI Agent 0 Calibration, Virtual_TEMP (+4 more) | **6 with live RPM** (1580 / 622 / 1328 / 701 / 1991 / 3524) | **7 (`pwm1`–`pwm7`)** | The board controller. Rich per-PWM knobs: `auto_point1..5`, `temp_sel`, `floor`, `mode`, `step_up/down_time`. |

- **Critical restore detail:** all active PWMs boot with **`pwmN_enable = 5`**, not the textbook `2`. On NCT6796D-family, enable values ≥ 2 are automatic modes and `5` is what BIOS/AI-suite leaves set (`pwm7` shows `0` = off). Restore MUST write back the **cached pre-takeover value** per control (spec §4.1 `_initialEnable[id]`) — hardcoding `2` would be wrong on this board.
- PWM nodes are `root:rw-r--r--` → non-user writes need the spec §3.4 udev ACL (`openfan` group + `g+w`). One-time sudo to install; not needed for Phase 1 (fake hwmon) but required before any real `--apply-once`.

- `nct6775` (with `nct6775_core`) binds the NCT6796D-S cleanly once loaded — it was simply not auto-loaded at boot during the first scan. **Add `nct6775` to `/etc/modules-load.d/openfan.conf`** so OpenFan always finds it (one-time sudo).
- `asusec` remains useful alongside: its tach/temps are a second read-only view; no overlap conflict since it exposes no PWM.
- **Consequence for success criteria:** spec §1 items 1–4 (board fan discovery, CPU-graph → case fan, Mix → board fan, PRO 6000 per-fan) are all achievable on this board. Item 5 (apply/restore semantics) must honor the cached `enable=5` restore noted above.

## NVML findings (read-only probe, driver 595.91.07)

```
NVML devices: 3   (works as regular user — no root, no sudo)
GPU0  RTX 5060 Ti                          GetNumFans=1  fan0=0%   MinMax=30–100%
GPU1  RTX PRO 6000 Blackwell WS @ 11:00.0  GetNumFans=2  fan0=30%  MinMax=30–100%
GPU2  RTX PRO 6000 Blackwell WS @ E1:00.0  GetNumFans=2  fan0=30%  MinMax=30–100%
```

- **Three GPUs**, not two: the dual PRO 6000s plus a GeForce RTX 5060 Ti (PCI `01:00.0`). Multi-GPU inventory/merge must handle all three; PCI bus in names as designed.
- Each PRO 6000 exposes **2 fans** → per-fan `SetFanSpeed_v2` model applies, matching Windows OpenFan.
- `GetMinMaxFanSpeed` = 30–100% on all cards — driver-enforced floor agrees with the spec's 30% PRO floor. (5060 Ti reads 0% now = default fan-stop; NVML min when *setting* is reported as 30 anyway.)
- Fan **write** (`SetFanSpeed_v2`) + `SetDefault` round-trip not yet tested — deliberately, since it spins real fans. Next spike step once russell OKs a live write (bump one PRO fan to e.g. 45% for 10 s, restore).

## Conflicts / misc

- `fancontrol.service`: inactive. No `coolercontrold`/`nvfancontrol` processes. Clean field.
- `.NET SDK 8.0.131` installed via apt on 2026-09-21 — Phase 1 unblocked.
- udev PWM ACL (spec §3.4) **is** needed for board fans (`pwm*` nodes are root-owned), applied to the `nct6799` device; NVML needs no extra privilege here — plain user works.

## Implications for the plan

1. **v1 scope = full OpenFan**: 7 board PWM controls (chip `nct6799`) + per-fan control of 2×PRO 6000 and the 5060 Ti, Flat/Graph/Mix curves fed by k10temp Tctl / ASUS EC / SuperIO / NVMe temps, apply/restore, tray, configs. No scope fork needed.
2. Curve sources worth surfacing: `k10temp Tctl` (primary CPU), `nct6799 PECI/TSI Agent 0 Calibration`, `asusec CPU Package`, NVMe temps. Note SuperIO fan headers have no per-header labels in hwmon (`fanN_label` empty) — UI should show board-style hints where safe, else `Fan N`.
3. Restore semantics: cache `pwmN_enable` before first write; on this chip "auto" = **5**, not 2. If the app ever dies without restoring, BIOS duty-cycling resumes on its own (chip is in auto mode by default).
4. Privilege: NVML as plain user ✓. Board PWM needs one-time sudo for udev ACL + modules-load.d; after that the app never runs elevated.
5. Remaining Phase 0 item: NVML live set/restore round-trip on one PRO fan (needs go-ahead — spins real fans).
