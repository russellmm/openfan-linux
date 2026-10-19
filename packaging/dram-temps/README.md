# Working TRX50 DDR5 sensor setup

These two files are copied unchanged from `/mnt/SOLIDIGM/Claude/dramtemps`, the working
October 18 setup for this ASUS Pro WS TRX50-SAGE WIFI A. Updated setup notes are in the
project root as `DRAM-TEMPS-README.md`; the original notes remain in the source directory.
The earlier `dram-temps.sh` SMBus search
script is intentionally not included: that was not the successful route.

`dram-temps-bind.sh` loads the kernel's `spd5118` driver and binds responding addresses
0x50 and 0x52 on adapters named `Synopsys DesignWare I2C adapter`. This is a **machine-specific
startup setup**, not a general-purpose sensor discovery script. It needs root, `i2cget`
from i2c-tools, and a kernel with spd5118. Inspect the hardware before using it elsewhere.
The script exits successfully even if no hubs answer; verify hwmon readings after boot.

On the development machine these files are already installed as
`/usr/local/sbin/dram-temps-bind.sh` and `/etc/systemd/system/dram-temps.service`, with
the service enabled. The service ran successfully on the October 19 boot and four
SPD5118 temperature devices are currently readable. No reinstallation is needed.

OpenFan does not run these scripts, scan buses, read SPD EEPROM contents, or bind devices.
It discovers the existing kernel devices through libsensors and reads their temperatures
without privilege. The normal sysfs fallback also supports them. Startup device binding
and OpenFan's saved display preferences are independent.

In **Sensors → Memory temperatures**, four rows show the SPD hub measurements with current,
minimum, maximum and average values. Controller/address labels distinguish modules without
claiming physical slot letters. Tooltips show the controller path and current I2C address.
Each row also displays the configured low/high/critical-low/critical-high alarm limits.
These are read-only threshold readbacks, separate from the observed minimum and maximum;
they are not offered as live temperature sources for curves or overlay tiles.
User names, curves and overlay tiles use a controller/address identity
that survives changes in hwmon and I2C adapter numbering. Existing numeric ids on other
sensor types are unchanged. Older saved SPD5118 numeric references should be reselected
once; this machine had none before this update.

Group hide and collapse choices save immediately in the active OpenFan profile and restore
on the next launch. Optional reads stop while hidden/collapsed. Sensors needed by enabled
fan curves, the overlay or a visible dependent page still update. Expand/show the group
to resume its ordinary polling. Statistics start again at each app launch.
