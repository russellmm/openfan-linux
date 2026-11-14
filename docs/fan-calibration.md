# Fan calibration

Open **Home → Calibrate** on a motherboard or NVIDIA fan card. The link enables when that fan
has a real paired RPM reading; a stopped fan's 0 RPM is valid. It updates after startup as readings
arrive. GPUs without working RPM feedback retain the manual **RPM table** fallback.

Move the slider to command the fan, then let its RPM settle. The slider percentage, live command
label and **Point %** follow together. **Add (…%, …RPM)** records the current slider command and
measured RPM. The adjacent **Add** uses Point % instead: edit Point % to deliberately override
the point's percentage without changing the fan command. Moving the slider resets Point % to match.

Automatic stepping starts at the selected control's minimum and proceeds to 100% in 10% steps.
BMC and other zero-floor controls include 0%, 10% and 20%. The RTX PRO 6000
starts at 30%; lower commands are not added to its sweep. GPU and BMC steps wait eight seconds;
direct hwmon motherboard steps wait 3.5 seconds. Allow additional settling and replace a point manually if
needed. Stop a sweep by clicking its button again or by moving the slider yourself. A rejected
command or missing RPM stops the sweep without adding a point for that step; missing readings
are never converted to zero. Manual Add buttons disable when the command failed or RPM is missing.

On the RTX 5060 Ti, 0% uses automatic GPU control so firmware can stop the fan; positive commands
use manual control. Firmware may run the fan at zero target when its thermal rules require it.
Hardware floors still apply to other cards. The RPM column always records the actual paired tach
reading rather than an interpolated estimate or an assumption about the target.

Since 0.4.3 all ASUS BMC fan headers, including W_PUMP+, expose 0–100% PWM targets. The driver
adds no 20% minimum, fixed pump duty or temperature override. A zero target programs zero
duty in every BMC curve point; the RPM readout remains measured. CPU_FAN and CPU_OPT still
share one hardware zone. Existing calibration tables are retained; add low-duty points or
run a new sweep to measure them.

On the tested BMC firmware, actual zero-duty commands are rejected with OEM code 0xD9.
OpenFan reports the firmware error and restores the original curve; no 0% calibration
sample is added and an automatic sweep stops. Lower nonzero commands (1%, 5%, 10%)
were accepted on CHA_FAN1. Actual fan-stop via this BMC interface remains unresolved.

Only the selected fan is handed to the calibration window; other curves continue. The selected
curve pauses temporarily without changing its saved Enabled flag. Closing the dialog restores
default fan control and lets its saved curve resume on the next sample. Calibration's RPM demand
stays active even when its sensor group is hidden. The modal dialog prevents simultaneous
calibrations or switching the active profile during calibration.

## Saving

**OK** validates and saves the table immediately to the active configuration. **Cancel** or closing
the dialog discards unsaved table edits. **Save configuration** (Ctrl+S) saves accepted points again;
**Save configuration as…** includes them in the new profile as well. Clicking Save configuration
does not accept edits still open in a calibration dialog: finish with OK first.

Each fan has its own `controls` entry with a `calibration` array containing `percent`, `rpm` and
`avoid`. For example:

```json
"calibration": [
  { "percent": 30, "rpm": 1200, "avoid": false },
  { "percent": 60, "rpm": 1850, "avoid": true },
  { "percent": 100, "rpm": 2867, "avoid": false }
]
```

The active profile on this workstation is `/home/russellm/Documents/openfan2.json`; its selection
is stored in `~/.config/openfan/active`. Settings replacement is atomic via a same-directory rename.
Avoid points influence which commands the curve controller skips; a manual RPM table supplies an
estimate only when real RPM is unavailable. Calibration does not replace real tach readings.
