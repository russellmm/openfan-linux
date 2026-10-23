# CPU stuck at base clock after power-saver (found 2026-10-23)

Found while load-testing on the TRX50 workstation (Threadripper 9970X, Ubuntu 26.10, kernel 7.3.0-9-generic,
power-profiles-daemon 0.30, `amd-pstate-epp` active). Not an OpenFan bug, but OpenFan's CPU frequency page is where
it shows up and where it can be fixed.

## What happens

1. Choosing **Power saver** (GNOME's power menu, `powerprofilesctl`, anything using power-profiles-daemon) turns
   per-policy `boost` **off**. The kernel lowers `cpuinfo_max_freq` to the base clock (4,000 MHz) and holds every
   policy's `scaling_max_freq` there.
2. Switching to **Balanced/Performance** turns `boost` back **on**, and `cpuinfo_max_freq` returns to 5,487 MHz.
   The kernel then puts `scaling_max_freq` back to the **stored max request**.
3. This morning the stored request was **4,000 MHz**, so "performance" never boosted: single-thread clocks stayed at
   ~4.0 GHz instead of ~5.4 GHz on all 64 policies, until the ceilings were rewritten as root.

Observed on this machine before the fix:

```
$ powerprofilesctl get                  → performance
$ cat .../policy*/boost | sort -u       → 1
$ cat .../policy0/cpuinfo_max_freq      → 5487604
$ cat .../policy*/scaling_max_freq      → 4000000 (×64)
```

Writing `cpuinfo_max_freq` back into each `scaling_max_freq` (as root) fixed it immediately; boost clocks returned
(5.41 GHz single-thread on CCD0). **After that write, power-saver ↔ performance round-trips correctly**: tested
twice with OpenHW and OpenFan closed, the ceiling went 5,487 → 4,000 → 5,487 MHz on its own.

So the toggle alone isn't the bug: something left the stored request at the base clock. Not yet proven which:

- **Most likely: the boot sequence.** power-profiles-daemon restores the saved profile at boot. The session had been
  in power-saver since boot, so the CPU likely started with boost off and the stored request at base clock.
  To test: select Power saver, reboot, switch to Performance with OpenFan/OpenHW closed, and read
  `scaling_max_freq`.
- **Or a write of 4,000,000.** OpenFan's **Hardware maximum** writes the *currently advertised*
  `cpuinfo_max_freq`, which is 4,000 MHz while boost is off. Pressing it (or setting a 4,000 MHz all-core cap) in
  power-saver stores 4,000 MHz as the request, and it survives every later profile switch. The openfan-helper
  journal shows no frequency writes this boot, but it doesn't log them, so this can't be ruled out.

The README's note "with per-policy boost off, the available ceiling is 4,000 MHz" describes this same state.

Similar reports elsewhere (no exact upstream bug found yet): CachyOS "amd_pstate clamps … frequency to nominal",
and resume-from-suspend reports where `scaling_max_freq` sticks at base. If the reboot test reproduces it, file it on
Launchpad (`linux`) or with the amd-pstate maintainers.

## What OpenFan can do about it

- **Workaround today:** switch to Balanced/Performance first, then press **Hardware maximum** on the CPU page. It
  writes the currently advertised `cpuinfo_max_freq` (5,487 MHz once boost is on), which lifts the clamp.
- **Don't store the base clock by accident:** while boost is off, Hardware maximum writes 4,000 MHz and makes the
  cap stick. Either disable it (and caps above base) while every policy reports boost off, or write the boost-on
  maximum (`amd_pstate_max_freq`, 5,487,604 here) instead of the current `cpuinfo_max_freq`.
- **Detect it:** the page shows "boost off", but not the case that matters: boost **on** and every
  `scaling_max_freq` below `cpuinfo_max_freq`. Flag that ("CPU capped at 4.00 GHz — boost is on") with a one-click
  Hardware maximum.
- **Fix it automatically (what OpenHW now does):** watch power-profiles-daemon's `ActiveProfile` on the system bus
  (`org.freedesktop.UPower.PowerProfiles`, `PropertiesChanged`). When a profile change turns boost from off to on
  and policies are still below `cpuinfo_max_freq`, have the helper write `cpuinfo_max_freq` back. Only do this on that
  off→on transition, so deliberate caps made in a boost profile are left alone (the kernel already drops those
  to base clock in power-saver).
- **Helper hardening:** if `openfan-helper` runs under a systemd unit with `ProtectSystem=strict` /
  `ProtectKernelTunables=true`, it needs `ReadWritePaths=/sys/devices/system/cpu` for these writes.

OpenHW's implementation for reference (copy, don't reference): `src/OpenHW.Hardware/CpuFrequency/CpuFrequencyLimits.cs`,
`src/OpenHW.App/Power/PowerModeController.cs`, helper command `cpufreqrestore` in
`src/OpenHW.Hardware/Helper/HelperServer.cs` (repo `/mnt/SOLIDIGM/Claude/OpenHW`).
