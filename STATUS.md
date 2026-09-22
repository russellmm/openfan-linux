# OpenFan Linux — Project Status & Handover

**Last updated:** 2026-09-21 · **State: fully working, committed, safe across reboots**
Git branch `main`, this STATUS.md is itself the latest commit (see `git log --oneline`).
Spec: [`openfan-ubuntu.md`](openfan-ubuntu.md) (approved; checkboxes kept current). Hardware findings journal: [`spike-notes.md`](spike-notes.md).

---

## 1. What this program is

Logged-in Ubuntu app that owns motherboard/case/AIO fans (Linux hwmon sysfs) and NVIDIA
GPU fans (NVML), driven by Flat / Graph / Mix curves. Portable curve engine shared with
Windows OpenFan (`/run/media/russellm/8TB/hermes_working/OpenFan` is the reference repo).

Stack: .NET 8, Avalonia 11.2.3 GUI, zero-dependency Core class library, P/Invoke NVML,
raw sysfs hwmon. No LiquidCoolLib on Linux.

## 2. Target machine (this box)

- Ubuntu 26.04.1 LTS, kernel 7.0.0-31-generic, TRX50 + Threadripper 9970X
- Board SuperIO: **NCT6796D-S**, binds via `nct6775` driver as chip name `nct6799` → hwmon10
  (7 PWM controls, 6 tachs, 13 temps). Board boots with `pwmN_enable = 5` (auto) — NOT 2.
- GPUs: 0 = RTX 5060 Ti (01:00.0), 1 = RTX PRO 6000 WS `GPU-6d3eab54-…` (11:00.0),
  2 = RTX PRO 6000 WS `GPU-d0f36b3b-…` (E1:00.0). Driver 595.91.07 (open kernel module).
- User `russellm` (uid 1000), in group `openfan`.

## 3. System state installed on the machine (persists across reboot)

| Item | Path | Installed by |
|---|---|---|
| udev ACL rule | `/etc/udev/rules.d/99-openfan-pwm.rules` | user, Phase 2 checkpoint |
| ACL script | `/usr/local/lib/openfan/pwm-acl.sh` (chgrp openfan + g+w on `pwm<N>` and `pwm<N>_enable`) | user |
| Module autoload | `/etc/modules-load.d/openfan.conf` (`nct6795`→`nct6775`) | user |
| Helper binary | `/usr/local/lib/openfan/openfan-helper` (self-contained single file) | agent, 2026-09-21 |
| Helper service | `/etc/systemd/system/openfan-helper.service`, **enabled** — starts at boot, socket `/run/openfan/helper.sock` `srw-rw---- root:openfan` | agent |

After reboot everything above re-applies automatically. PWM nodes come back group-writable;
helper listens again on a fresh socket; fans start in board-auto / driver-default state.

**Security note:** the user's sudo password was used (twice, `sudo -S`) to install the helper
and appears in the DSH chat transcript — consider rotating it if that transcript persists.
The agent also has full file access on this box for the session; nothing else privileged was done.

## 4. How to run after reboot

```bash
cd /run/media/russellm/8TB/deepseek-linux/projects/openfan-linux
dotnet build                                   # ~10 s, must end "0 Error(s)"
./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan   # GUI (binary name: openfan)
```

- **After a fresh login you should NOT need `sg openfan` anymore** — the re-login finally makes
  group membership live in the desktop session. If a terminal shows `id -nG` without `openfan`,
  use the workaround: `sg openfan -c './src/OpenFan.Linux.App/bin/Debug/net8.0/openfan'`.
- Status line at top of GUI states exactly what's writable; cards show amber reason lines when
  a control is enabled but writes fail (group missing, helper down, etc.).
- CLI hardware tool: `./src/OpenFan.Linux.Cli/bin/Debug/net8.0/openfan-linux --dump`
  and `--apply-once <id> <pct> [--seconds N]` (also routes GPU fans through the helper).
- Config persists at `~/.config/openfan/config.json` (ApplyCurves, per-control mode/curve).
- Tray icon: Open / **Exit** (Exit restores ALL owned fans: hwmon → pre-takeover enable value,
  NVML → driver default; also restored on window X→tray-off-apply, Apply uncheck, and quit).

## 5. Architecture (what talks to what)

```
GUI "openfan" (user session)
 ├─ HwmonBackend ──writes──► /sys/class/hwmon/hwmonN/pwmX(_enable)   [udev ACL: group openfan]
 ├─ NvmlBackend  ──reads──►  libnvidia-ml.so.1 (temps, fan %, model, UUIDs — free for user)
 └─ NvmlHelperClient ──────► /run/openfan/helper.sock ──► openfan-helper (root systemd) ──► NVML writes
      persistent conn; auto-selected when socket exists; direct NVML kept for sudo debugging
```

- **Why the helper:** NVIDIA driver gates NVML fan *writes* on root euid (returns
  NO_PERMISSION(4) for uid 1000 even though /dev/nvidia* are 0666). Proven in spike.
- **Helper protocol** (newline, `src/OpenFan.Linux.Hw/HelperProtocol.cs`):
  `ping` / `set <nvml:uuid:fan:n> <0-100>` / `default <id>` / `quit`. Only nvml fan ids —
  no hwmon, no paths, no power limits. **Restore-on-disconnect per session**: killed GUI ⇒
  helper immediately returns those fans to driver default. Helper SIGTERM/restart drains the same.
- Control ids: `hwmon:{chip}:{index}:pwm:{n}`, `nvml:{uuid}:fan:{n}`, temps `…:temp:core` etc.
  Stale-index guard re-checks chip name before every write.
- FanController (Core, portable): applies every 1 s tick; skips on missing temp (never restores);
  2 consecutive write failures ⇒ restore + mark error; per-card amber error text comes from
  backend `LastWriteError` / helper `LastError`.
- Restore semantics: cached pre-takeover `pwmN_enable` per control (the board's `5`!), restored
  exactly — never hardcoded 2.

## 6. Test & verification status

- **82/82 xunit tests green** (`dotnet test`) — ported Windows curve suites + ApplyLoopTests
  (re-apply cadence, missing-temp skip-not-restore, disable-restores-once, PRO-6000 floor 30 %,
  Mix CPU+GPU→board-fan success criterion) + HwmonBackend fixture tests (nct6799 tree with
  enable=5 and decoy `_auto_point` files) + HelperProtocol/HelperSocket integration tests
  (real Unix sockets, restore-on-disconnect proven).
- **Live on target, user-confirmed:** tray icon + Exit ✓ · Apply curves + Flat on board fans ✓ ·
  GPU fan control through helper ✓ (CLI ramp 30→45 % then back to driver, unprivileged).
- GUI smoke: starts stable, no exceptions.

## 7. Known gaps / quirks

1. `HwmonBackend` disabled-chips set computed at construction — changing `Sources` in config
   live needs app restart (fine today; fix when Settings page lands).
2. `nvmlDeviceGetFanSpeedRPM` unsupported on these Blackwell cards → tach shows n/a for GPUs
   (fan % still reads fine). Board tachs work.
3. Graph editor v1 has no axis-range UI (auto-scales) and no per-point numeric entry — canvas only.
4. Floating AUXTIN sensors read −9 °C; plausibility filter drops <0 / >150 °C values.
5. On SIGKILL of the GUI itself: hwmon fans keep last duty until next app run or reboot (Windows
   same); GPU fans are safe — helper restores on socket EOF.
6. Conflict detection (coolercontrold/fancontrol running) not implemented yet.

## 8. Remaining work (next-first)

1. **Mix curve UI** — child-curve picker composing Max/Min/Average over graphs/flats (engine + tests exist: `Core/Curves/MixCurve.cs`; ApplyLoopTests already prove Mix CPU+GPU → board fan).
2. **Calibrate flow** (spec §6: sweep duty steps, record tachs, detect stalls, snap-away-from-avoid) — port from Windows `FanCalibrator`.
3. Card hide + drag-reorder; right-click menu.
4. Settings page (disabled hwmon chips, refresh interval), Theme handling, About window, named configs.
5. Start-at-login wiring (`StartAtLogin` exists in config; needs autostart .desktop file write) + logind sleep/resume restore hook.
6. Conflict detector: `pgrep coolercontrold|fancontrol` → banner with "take over" option.
7. GPU extras page (power limit, persistence mode) — helper protocol extension, root-gated like fan writes.
8. `.deb` packaging (app + helper + udev rule + postinst group creation), README install-from-release.
9. Nice-to-have: polkit rules instead of bespoke socket for helper (spec alternative), AOT/publish single-file for the GUI, tray temperature tooltip.

## 9. Repo map

```
openfan-ubuntu.md          spec (source of truth; checkboxes current)
spike-notes.md             hardware findings journal (enable=5, NVML root gate, …)
src/OpenFan.Core/          portable engine: curves, FanController, config DTOs, merger — net8.0, no deps
src/OpenFan.Linux.Hw/      HwmonBackend, NvmlBackend, CompositeActuator, HelperServer, NvmlHelperClient
src/OpenFan.Linux.App/     Avalonia GUI "openfan": MainWindow (cards), GraphEditorWindow, FanApp, tray
src/OpenFan.Linux.Cli/     "openfan-linux": --dump / --apply-once (diagnostics + scripting)
src/OpenFan.Linux.Helper/  "openfan-helper": root daemon main()
tests/OpenFan.Core.Tests/  82 tests incl. socket integration
packaging/                 udev rule, ACL script, modules-load conf, openfan-helper.service, README (install/uninstall all)
```

## 10. Resume checklist for "coming back later"

1. `git log --oneline | head` — confirm HEAD matches §"Last updated" above (or newer).
2. `dotnet build && dotnet test` → expect `0 Error(s)` / `82 passed`.
3. `systemctl is-active openfan-helper` → `active` (if inactive: `journalctl -u openfan-helper -n`).
4. Launch GUI (§4), check status line says nothing alarming, Apply curves still ticked from last session.
5. Pick next item from §8 — Mix UI recommended — and go.
