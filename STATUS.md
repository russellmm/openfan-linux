# OpenFan Linux — Project Status & Handover

**Last updated:** 2026-09-22 · **State: fully working, committed, safe across reboots**
Git branch `main`. Spec: [`openfan-ubuntu.md`](openfan-ubuntu.md) (approved; checkboxes mostly current). Hardware findings journal: [`spike-notes.md`](spike-notes.md). Manual install chain (incl. helper publish): [`packaging/README.md`](packaging/README.md).

## 1. What this is
Native Ubuntu app owning motherboard/case/AIO fans (hwmon sysfs) + NVIDIA GPU fans (NVML), with
Flat/Graph/Mix curves, mirroring the Windows **OpenFan** reference
(`/run/media/russellm/8TB/hermes_working/OpenFan`; design screenshots in `screenshots/`:
main page, graph editor, GPU screen, calibrate). Avalonia 11.2.3 / .NET 8, FluentTheme Dark.

## 2. Build / run / test (agent workflow — agent rebuilds + restarts, user just looks)
```bash
cd /run/media/russellm/8TB/deepseek-linux/projects/openfan-linux
dotnet build && dotnet test            # 82 tests green as of b36f825
# GUI smoke: exit 124 after timeout == stable run
timeout 12 ./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan; echo exit=$?
# Relaunch on user's desktop (kill old instance first):
pkill -x openfan; sleep 1
DISPLAY=:0 XDG_RUNTIME_DIR=/run/user/1000 WAYLAND_DISPLAY=wayland-0 \
  setsid nohup ./src/OpenFan.Linux.App/bin/Debug/net8.0/openfan >/tmp/openfan-gui.log 2>&1 </dev/null &
```
- Crash log for the agent-launched instance: `/tmp/openfan-gui.log` (this is how the calibrate crash was found).
- **See the user's actual window**: `scrot`/`imagemagick`/`xdotool` installed.
  `W=$(DISPLAY=:0 xdotool search --class openfan | head -1); DISPLAY=:0 import -window "$W" /tmp/x.png` then read_image.
  Caught the invisible-nav-rail bug this way (a1c4a4b) — use it to verify UI changes, don't reason blind.
- `pgrep -f "bin/Debug/net8.0/openfan"` self-matches its own bash line → use `pgrep -x openfan`.
- Git identity: `git -c user.name="russellmm" -c user.email="russellmm@users.noreply.github.com" commit`.
- **Never chain `build | grep "0 Error(s)" && … && commit`**: grep exits 0 on match and the chain commits
  even when errors were also printed (happened once; fixed via --amend). Gate on the build explicitly.

## 3. Target machine facts (verified)
- Ubuntu 26.04.1, TRX50 + Threadripper 9970X, NVIDIA driver **595.91.07**.
- GPUs: RTX 5060 Ti `GPU-551a52fc…`; PRO 6000 WS `GPU-6d3eab54…` (slot 1); PRO 6000 WS `GPU-d0f36b3b…` (slot 3).
- **GPU fan RPM unavailable on this driver** — verified by direct ctypes probe: `nvmlDeviceGetFanSpeedRPM`
  fails on all 3 cards with every struct-version encoding, and `/sys/class/hwmon` has **no nvidia chip**.
  Windows shows RPM only because its driver is newer (610.88). Decision (user-agreed): GPU fan cards show
  measured duty % (`nvmlDeviceGetFanSpeed` works fine); GPUs are NOT calibratable — commanded % taken at face
  value. On a driver update, tach readings appear automatically: NvmlBackend already writes
  `nvml:{uuid}:tach:{n}` whenever the RPM call succeeds, and cards upgrade to `% + RPM` by themselves.
- Board sensors: NCT6796D-S binds as **nct6799** (hwmon5, 6 fan tachs) + **asusec** (hwmon8).
  `/etc/modules-load.d/openfan.conf` loads nct6775 at boot. Board boots with `pwmN_enable=5` — app restores
  the exact pre-takeover value on release/exit.
- PWM write access: udev rule `/etc/udev/rules.d/99-openfan-pwm.rules` → group `openfan`; user is a member
  (rebooted — plain launch works, no `sg openfan` needed).
- GPU writes: root helper `/usr/local/lib/openfan/openfan-helper` + systemd unit **openfan-helper.service**
  (enabled, active, survives reboot), socket `/run/openfan/helper.sock`
  (root:openfan 0660). App auto-routes NVML writes through it when the socket exists (`NvmlHelper.IsAvailable`);
  client keeps ONE persistent connection — per-call connections would trip restore-on-disconnect after every set.
  GPU reads go direct via NVML in-process.
- Agent sudo: `echo '<sudo-password>' | sudo -S bash -c '…'`; ticket NOT cached across tool calls (fresh sandbox each).
  User advised to rotate the password (it is in the transcript).

## 4. Architecture
**Curve library model (user-mandated, non-negotiable):** curves are first-class named objects; fans reference them.
- `Settings.Curves: List<CurveSettings>` — Id / Type(`flat|graph|mix`) / Name / SensorId / HysteresisC=2 /
  HysteresisS=1 / MinTempC=30 / MaxTempC=100 / MaxSpeedPercent / Function(`max|min|average`) / ChildCurveIds / Points.
  `ControlSettings.CurveId` + `Enabled` reference a curve (many fans → one curve). Graph editor edits a working-copy
  clone; Ok replaces the library entry. Mix-in-mix blocked.
- `CurvePointDto(double TempC, double Percent)` — **Percent is DOUBLE** (CS0266 trap when assigning to decimal?).
  `GraphCurve.Evaluate(IReadOnlyList<CurvePoint>, tempC, floor=0, maxSpeed=100)` — takes CurvePoint, NOT DTOs; map first.
- `FanController.Evaluate(curve, Settings.Curves, readings)` resolves any curve type incl. mix recursion.
  Runtime: after curve evaluation, applies `CalibrationRules.SnapAwayFromAvoid(cfg.Calibration, target)`.
- Config `~/.config/openfan/config.json` (SettingsStore, camelCase). Legacy ids `flat:{id}`/`graph:{id}` still load.
  **Window geometry** WindowX/Y/W/H persisted: restore in ctor (Manual startup, guards off-screen), save debounced
  1.2 s after PositionChanged/Resized + capture in OnClosing (covers hide-to-tray). Verified round-trip.
- Sensor/control ids: `hwmon:{chip}:{n}:temp:{label}`, control `…:pwm:{n}`, tach `…:fan:{n}`;
  GPU temp `nvml:{uuid}:temp:core`, control `nvml:{uuid}:fan:{n}`, tach `nvml:{uuid}:tach:{n}`.
  Tach pairing = substring swap on the id (`FanApp.PairedTachId` / `PairedRpm`).

**Key files**
- `src/OpenFan.Core/` — portable engine (namespace stays OpenFan.Core for Windows portability): Curves/,
  ControlLoop/FanController.cs, Config/SettingsStore.cs, CalibrationMap + CalibrationRules (ported from Windows:
  validation trio + avoid-snap).
- `src/OpenFan.Linux.Hw/` — HwmonBackend, NvmlBackend (+NvmlNative P/Invokes), CompositeActuator
  (`SetPercent`/`SetDefault` route by id prefix), InventoryMerger (drops hwmon GPU dupes when NVML covers them),
  NvmlHelper socket client.
- `src/OpenFan.Linux.App/` — MainWindow.axaml(.cs) (~1100 lines: nav rail Home/GPUs/Theme/Tray/Settings/About;
  header Apply curves · Refresh · ⋮ Save/Load setup · Exit · clock; Home = fan cards then Curves section; FABs
  bottom-right Flat/Graph/Mix), GraphEditorWindow, **CalibrationWindow**, FanApp (readings, CommandedPercent,
  CurveOutput, PairedRpm/PairedTachId, ManualSetPercent/ManualRestore, LoadProfile), tray. `CurveLibraryWindow`
  deleted — superseded by Curves section + FABs.

**UI conventions**
- Tokens: accent `#F0A03C`, window `#171B1F`, card `#1E2429`, border `#2C363D`, secondary text `#8FA0AA`,
  nav rail `#101416`, FAB bg `#4A5D68`, curve area fill ARGB 0x59F0A03C, ok-green `#7BC97B`, bad-red `#EF6B6B`.
- App.axaml styles: `RadioButton.nav` (orange bar), `Button.accent`, `Button.fab` (74×74 circle),
  `TextBox.cardname` — borderless transparent "quiet affordance": looks like plain text until clicked. Used for
  curve names, fan-card titles, and the flat-curve % value (Enter/blur commits, clamps, repairs bad input).
- **Avalonia gotchas (all personally verified):** code-created ComboBoxes DROP selection set before attach →
  re-assert on `AttachedToVisualTree`; self-heal must force through `SelectedIndex=-1` to rebuild a stale blank
  selection box (index-equality checks alone miss it). NumericUpDown spinners eat ~55 px → Width≥120.
  `ComboBox.Items` get-only → Items.Add loop or ItemsSource. Focus-away: `TopLevel.GetTopLevel(x)?.FocusManager?.ClearFocus()`.
  Pointer capture: `e.Pointer.Capture(control)` / `(null)`. Shapes need `Avalonia.Controls.Shapes.` qualification —
  do NOT `using` it namespace-wide (clashes with `System.IO.Path`). Detached MenuFlyout:
  `FlyoutBase.SetAttachedFlyout(anchor, fly); FlyoutBase.ShowAttachedFlyout(anchor);`. Populate items + set
  selection BEFORE attaching SelectionChanged; guard null-selection writes (re-assert instead of persisting null).

## 5. Fan cards (Home → Controls)
Click-to-rename title (→ `ControlSettings.Name`; tooltip = original name + id), ☑ Curve checkbox + curve dropdown
(mutually synced via suppress-flag; picking a curve arms the fan, Monitor disarms), value line:
applying → `cmd %` (+ `RPM` when tach exists; + `now X %` when measured differs >1.5 pts mid-ramp);
monitoring → GPU shows measured `%`, chassis `auto … RPM`. **Calibrate** link (orange) only where a tach exists,
plus `calibrated ✓ (n pts)` badge. Error line for actuator failures.

## 6. Calibration window (`f62c927` + crash fix `b36f825`)
Reference: `screenshots/OpenFan-calibrate-fan.jpg`. Flow: ctor sets `cfg.Enabled=false` (controller restores & skips),
slider drives fan live via `FanApp.ManualSetPercent` (direct actuator). Sampling three ways: Add-current button,
Point-% box, **Auto step 20–100%** (steps of 10, 3.5 s dwell, 1 s DispatcherTimer records tach; click again = stop
early keeping samples). Table rows editable (command %, rpm, Avoid checkbox, X delete) — each `RowCtl` carries a
mutable `Key` (percent at build, updated on commit); do NOT index-map sorted rows onto insertion-order samples.
Live validation checklist gates Ok (`CalibrationRules.IsValid`). Every close path funnels to
`FinalizeCalibration()` (guarded by `_finalized`; `Closed` event covers X/Cancel/Ok): ManualRestore →
restore `_wasEnabled` → Save. **Cfg() must get-or-create the ControlSettings row** — `.First()` crashed on
never-configured fans (b36f825, found via /tmp/openfan-gui.log).

## 7. Curves section + setups
Curve cards ordered flat→graph→mix: header = type tag + click-to-rename name + **× delete** (in-use curves offer
"Unassign all & delete" flyout → dependents go Monitor, restored next tick); flat = ±5 % steppers + click-to-edit %;
graph = live-reading sensor dropdown (`name · group — 41 °C`), big output %, Edit link → GraphEditorWindow, mini
preview canvas (area polygon + white polyline + orange current-temp dot); mix = Function box, Add fan curve box
(excludes self/mixes/existing children), children list with × rows. FABs create curves (graph opens editor immediately).
⋮ menu: **Save setup as… / Load setup…** over `~/.config/openfan/profiles/*.json` via StorageProvider pickers
(save falls back to timestamped file if no portal — user has NOT reported the fallback message, so pickers work).
Load = `FanApp.LoadProfile`: RestoreAll → swap Settings → Save (pins active) → RefreshInventory(force).

## 8. Today's commits (newest first)
`b36f825` calibration crash fix · `f62c927` Manual Fan Calibration window · `175e423` GPU measured % on cards ·
`5da3b95` fan card rename · `2cf0ebc` blank-dropdown fix (deferred combo selection + -1 force heal) ·
`d7da24a` attach curve-card header (× delete had been invisible) · `1493312` header actions (Refresh/⋮/Exit) +
force-delete + save/load setups · `7931964` flat % click-to-edit · `c46494b` window geometry persistence + sensor self-heal.

## 9. Pending / next
1. **GPUs page** — power limits, clocks, PCIe, processes per `screenshots/OpenFan-GPU-Screen.jpg`. Requires helper
   protocol extension (add NVML read/write calls to openfan-helper Program.cs + client wiring in NvmlHelperClient;
   fan writes already route through the helper). Placeholder stub live on the page.
2. Re-pair tach management, ⋮ menus per card, card hide/drag-reorder (Windows Controls section features).
3. Settings page (enable/disable hwmon chips — note: HwmonBackend set is computed at construction, needs rebuild hook),
   Theme page (accent picker → AccentHex + tray pixels), Tray polish.
4. Start-at-login autostart .desktop file; logind sleep/resume → restore on suspend / reapply on wake;
   conflict detector (`pgrep coolercontrold|fancontrol` warning banner).
5. `.deb` packaging; polkit alternative to the helper service. Manual chain in packaging/README.md.
6. Nice-to-haves: calibration-estimated RPM on cards while applying; recent-setups submenu if pickers ever fail.

## 10. Standing cautions
- Smoke runs load the user's REAL config with real group access — fans may briefly follow curves during test windows.
  GPU auto-restores via helper EOF on disconnect; board resumes next app run. Keep smoke runs short; disclose long ones.
- Write tool requires a read-tool observation after files are touched by bash/python/sed (python in-place edits are fine;
  track staleness).
- `grep -c` with 0 matches exits 1 — harmless inside chains, but see §2 commit-chain caution.
