# CPU frequency caps: reboot persistence plan

Status: proposed next step. The current controls apply live caps only; this document does not install or enable boot persistence.

## User experience

Add **Keep after reboot** next to the frequency controls. Apply will continue to validate and apply either the all-core cap or the individual-core caps, then save that complete active configuration only when persistence is selected. The application will show separate results for “applied now” and “saved for boot”; a save failure must never appear as full success. Selecting individual mode by itself will not change hardware or persistence.

On launch, the app reads the live limits and the system's saved boot configuration. It does not overwrite either just because a different user profile was opened. The UI shows whether the saved configuration matches live caps, whether the boot service is installed/enabled, and the most recent boot-application result. Unchecking persistence and applying removes the boot override while leaving the requested live caps applied. Hardware maximum and disabling persistence remain explicit, separate actions.

## Store one system-wide configuration

The privileged helper will atomically write a versioned file at `/etc/openfan/cpu-frequency.json` with root ownership. Store integer kHz, the all-core/individual mode, and CPU identity/topology information. Individual entries identify physical cores by socket/core ids and their expected sibling membership; resolve those entries to current cpufreq policies when applying. Do not assume `policy32` will identify the same core after a firmware or topology change.

Reject unknown versions, unsupported modes, duplicate/conflicting entries, changed CPU identity/topology, and missing required cores before changing any policy. On a changed topology, keep firmware/OS defaults and report that the saved caps need review. Do not silently clamp a saved value that is now outside the driver's accepted range, and do not enable boost to make an old value fit.

## Apply before login

Extend the installed `openfan-helper` executable with a one-shot `--apply-frequency-config` mode. Reuse the existing range validation, batch writing, bounded readback, and rollback code; keep this mode independent of NVML and the GUI. It should validate the saved topology and the current policy limits, apply the complete saved configuration, verify every requested ceiling, and write a clear success/failure record to the journal and `/run/openfan/`.

Install an `openfan-cpufreq-apply.service` system service enabled for `multi-user.target`. This machine currently runs `power-profiles-daemon.service` in power-saver mode. Order it after module loading and Power Profiles daemon initialization, without making that daemon mandatory on other systems. Wait/retry for cpufreq readiness within a bounded window so a late driver does not silently defeat persistence. Service success must mean verified application, not merely that the executable started. Use a root-owned configuration and executable outside the user's checkout so boot does not depend on the development directory or desktop session.

A successful oneshot start alone cannot prove that later power-management changes will preserve the caps. Detect and report live/saved divergence rather than continuously fighting another controller. Test Power Profiles daemon boot ordering and transitions between power-saver, balanced, and performance; report drift if a transition changes a saved ceiling. Resume and CPU hotplug should use the same guarded reapplication path when persistence is enabled; the exact trigger should be selected after checking this kernel's driver behavior.

## Implement and verify in stages

1. Add the saved configuration parser/writer and dry-run validation. Test malformed files, topology changes, mixed per-core values, shared policies, and driver-range changes.
2. Add the one-shot helper mode and service installer/removal flow. Verify applying a fixture through a private helper and a manually started service, including readback failure and rollback. Prove the service works with no GUI or NVIDIA support.
3. Add Keep after reboot and the live/saved/service status to the CPU tab. Test save failure, disabling persistence, and loading unrelated app profiles without changing system caps.
4. Save a modest test cap, reboot, and verify the limits **before launching OpenFan**, using sysfs plus a short controlled-load measurement. Repeat for different individual-core caps, then test resume, a power-profile change, disable persistence, and reboot again to verify defaults return.

Completion requires the actual reboot tests; a manual service restart is only a preliminary check. No reboot will be initiated without the user's instruction.

References: [kernel CPUFreq policy interface](https://docs.kernel.org/admin-guide/pm/cpufreq.html), [systemd service and oneshot semantics](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html).
