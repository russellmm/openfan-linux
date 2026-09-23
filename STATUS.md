# OpenFan Linux — Project Status & Handover

**Last updated:** 2026-09-22 (late) · **State: fully working, committed, pushed to GitHub, safe across reboots**
Branch `main` → github.com/russellmm/openfan-linux. Spec: [`openfan-ubuntu.md`](openfan-ubuntu.md) (approved; checkboxes mostly current). Hardware findings journal: [`spike-notes.md`](spike-notes.md). Manual install chain (incl. helper publish): [`packaging/README.md`](packaging/README.md).
NOTE: history was rewritten once before the first push to scrub a sudo password from old STATUS revisions — all pre-rewrite hashes are gone; hashes below are post-rewrite.

## 1. What this is
Native Ubuntu app owning motherboard/case/AIO fans (hwmon sysfs) + NVIDIA GPU fans (NVML), with
Flat/Graph/Mix curves, mirroring the Windows **OpenFan** reference (design screenshots in `screenshots/`).
Pages: **Home** (fan Controls + Curves library), **GPUs** (per-card telemetry + power limits), **Sensors**
(full inventory, grouped, friendly renaming), Theme/Tray/Settings/About stubs. Avalonia 11.2.3 / .NET 8, FluentTheme Dark.

## 2. Build / run / test (agent workflow — agent rebuilds + restarts, user just looks)

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
cd /run/media/russellm/8TB/deepseek-linux/projects/openfan-linux
dotnet build && dotnet test            # 91 tests green
# GUI smoke: exit 124 after timeout == stable run
timeout 12 ./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan; echo exit=$?
# Relaunch on user's desktop (kill old instance first):
pkill -x openfan; sleep 1
DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 WAYLAND_DISPLAY=wayland-0 \
  setsid nohup ./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan >/tmp/openfan-gui.log 2>&1 </dev/null &
```
- Crash log for the agent-launched instance: `/tmp/openfan-gui.log` (how the calibrate crash was found).
- **See the user's actual window** (required before claiming a UI change works):
  `W=$(DISPLAY=:0 xdotool search --class openfan | head -1); DISPLAY=:0 import -window "$W" /tmp/x.png` then read_image.
  Caught the invisible-nav-rail bug this way — verify visually, don't reason blind.
- Driving the app remotely: **Ctrl+1..7** switches pages, **PageUp/PageDown** scrolls — far more reliable than
  xdotool clicks (mutter frame offsets double-count; client origin ≠ reported Position). `xdotool key --window $W ctrl+3`.
- `pgrep -f "bin/Debug/net8.0/openfan"` self-matches its own bash line → use `pgrep -x openfan`.
- Git identity: `git -c user.name="russellmm" -c user.email="russellmm@users.noreply.github.com" commit`.
- **Never chain `build | grep "0 Error(s)" && … && commit`**: grep exits 0 on match and the chain commits
  even when errors were also printed (happened once; fixed via --amend). Gate on the build explicitly.

## 3. Target machine facts (verified)
- Ubuntu 26.04.1, TRX50 + Threadripper 9970X, NVIDIA driver **595.91.07**.
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
  socket `/run/openfan/helper.sock` (root:openfan 0660). Protocol lines: `ping | set <nvml-fan-id> <pct> |
  default <id> | power <GPU-uuid> <watts> | quit`. Fan writes restore on disconnect; **power limits deliberately
  not restored** (driver persists them). App keeps ONE persistent connection — per-call connections would trip
  restore-on-disconnect. GPU reads go direct via NVML in-process.
- User's own `nvidia-power-limit.service` sets -pm 1 + 150/250/250 W at boot; OpenFan's saved limits were seeded
  to match (config `gpuPowerLimitsW`, reapplied at app start). Interaction documented: last writer wins, no conflict.
- Agent sudo: ask the user for the password per call (removed from docs/history before pushing; user advised to rotate).

## 4. Architecture
**Curve library model (user-mandated, non-negotiable):** curves are first-class named objects; fans reference them.
- `Settings.Curves: List<CurveSettings>` — Id / Type(`flat|graph|mix`) / Name / SensorId / HysteresisC=2 /
  HysteresisS=1 / MinTempC=30 / MaxTempC=100 / MaxSpeedPercent / Function(`max|min|average`) / ChildCurveIds / Points.
  `ControlSettings.CurveId` + `Enabled` reference a curve (many fans → one curve). Mix-in-mix blocked.
- `CurvePointDto(double TempC, double Percent)` — **Percent is DOUBLE**. `GraphCurve.Evaluate(IReadOnlyList<CurvePoint>, …)`
  takes CurvePoint, NOT DTOs; map first.
- Config `~/.config/openfan/config.json` (camelCase): curves, controls, `SensorAliases` (id→friendly),
  `GpuPowerLimitsW` (uuid→W), WindowX/Y/W/H geometry.
- Sensor ids: `hwmon:{chip}:{n}:temp:{label}` / `…:pwm:{n}` / `…:fan:{n}`; GPU `nvml:{uuid}:temp:core`,
  `nvml:{uuid}:fan:{n}` (control + live-% readback), `nvml:{uuid}:tach:{n}`.
- **Power limits**: `FanApp.SetGpuPowerLimit(uuid, watts)` → helper `power` cmd if socket exists else direct NVML;
  saved on success only; reapplied at startup. NvmlBackend also exposes `SnapshotAll()` (full per-GPU telemetry record).

**Key files**
- `src/OpenFan.Core/` — portable engine: Curves/, FanController, SettingsStore, CalibrationMap/Rules.
- `src/OpenFan.Linux.Hw/` — HwmonBackend, NvmlBackend (+NvmlNative P/Invokes incl. telemetry + SetPowerManagementLimit),
  CompositeActuator, InventoryMerger, NvmlHelperClient, HelperProtocol/Server/Sessions (`IGpuPowerWriter`).
- `src/OpenFan.Linux.App/` — MainWindow.axaml(.cs) (~1500 lines: nav rail; Home cards+curves; GPUs page builder;
  Sensors page builder), GraphEditorWindow, CalibrationWindow, FanApp, tray.

**UI conventions**
- Tokens: accent `#F0A03C`, window `#171B1F`, card `#1E2429`, border `#2C363D`, secondary `#8FA0AA`,
  nav rail `#101416`, FAB bg `#4A5D68`, VRAM bar `#4FC3F7`, ok-green `#7BC97B`, bad-red `#EF6B6B`.
- Styles: `RadioButton.nav` (orange bar), `Button.accent`, `Button.fab`, `TextBox.cardname` quiet-affordance rename box.

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

## 5. Pages
**Home** — fan cards (click-to-rename, ☑Curve + dropdown, cmd%/measured% + RPM value line, Calibrate link where a
real tach reads, error line) then Curves section (flat ±5/box, graph live-sensor dropdown + mini preview + Edit,
mix function+children; × delete with unassign flyout). Header: Apply curves · Refresh · ⋮ Save/Load setup · Exit · clock.
FABs Flat/Graph/Mix create curves.
**GPUs** (`144e7e6`, fixes `f8825ec`) — per card: header (GPU n · name busId / uuid), Power [W] Apply editor
(green/red note; persists + saves config), GPU util bar + VRAM used/total GiB bar, stats row Temp · Power draw/limit ·
P-State · Fan % · Fan RPM (— on 595) · Clocks G/S/M, PCIe gen×width + RX/TX rates, process table top-12 by VRAM
(name/PID/kind/mem; rebuild only when the pid|name|mem key changes). WDDM/MCDM badges skipped (Windows-only).
**Sensors** (`7c4808d`, `0a3d0eb`) — all temps + tachs + GPU fan-% controls, sections CPU / GPU / Motherboard /
Chipset-ASUS EC / AMD HSMP / Storage (NVMe) / Network / Other via `SensorGroup()` chip mapping; sections with several
devices get collapsible ▾/▸ per-device headers (NVMe labeled with model from sysfs). Click-to-rename → `SensorAliases`;
aliases drive every sensor dropdown app-wide (curve cards rebuild on commit); tooltip = original name + group + id.
**Calibration window** — see §6 of previous revision / commit `f62c927`: live slider, auto-step sweep, avoid flags,
validation trio gating Ok, get-or-create Cfg() rows.

## 6. Recent commits (newest first)
`0a3d0eb` Sensors fan-% + collapsible subgroups · `e00092d` PageUp/Down scroll · `7c4808d` Sensors page + aliases ·
`f8825ec` GPU page fixes + Ctrl+1..6 · `d83b7fc` docs · `a357a8e` nav rail visibility fix · `144e7e6` GPUs page +
power limits via helper · `3b808a7` STATUS rewrite · `b36f825` calibration crash fix · `f62c927` calibration window.

## 7. Pending / next
1. `.deb` packaging (dpkg-deb script); polkit alternative to the helper service. Manual chain in packaging/README.md.
2. Helper protocol tests exist for `power`; consider integration test for SnapshotAll against live NVML (skip on CI).
3. Nice-to-haves: calibration-estimated RPM while applying; recent-setups submenu; collapsed-state persistence for
   Sensors subgroups; sensor aliases also in tooltips of dropdown items; card drag-reorder (⋮ Move up/down shipped instead).

**DONE this pass (2026-09-22 late):** ⋮ card menus (pair tach/move/hide/release) · ControlOrder + hidden pills ·
Settings page (general/hidden/system incl. conflict detector) · autostart toggle · Theme accent picker (live,
shared mutable brush + DynamicResource) · tray Apply-curves item · About page · **logind sleep/resume**
(LogindMonitor: suspend→RestoreAll, wake+1.5s→ResetApplies; Tmds.DBus.Protocol MUST stay pinned to 0.20.0 —
the version Avalonia.FreeDesktop loads; upgrading it TypeLoads `Connection` at X11 init and crashes the app.
Test signals: `sudo busctl --system emit /org/freedesktop/login1 org.freedesktop.login1.Manager PrepareForSleep b true|false`
— MatchRule drops Sender so synthetic emits work).

## 8. Standing cautions
- Smoke runs load the user's REAL config with real group access — fans may briefly follow curves during test windows.
  GPU auto-restores via helper EOF on disconnect; board resumes next app run. Keep smoke runs short; disclose long ones.
- Write tool requires a read-tool observation after files are touched by bash/python/sed (python in-place edits fine).
- `grep -c` with 0 matches exits 1 — harmless inside chains, but see §2 commit-chain caution.
