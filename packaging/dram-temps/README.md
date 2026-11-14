# DDR5 sensor startup setup

On the current ASUS **Pro WS WRX90E-SAGE SE**, eight SPD5118 sensors are verified at
0x50, 0x51, 0x52 and 0x53 on each of the two memory controllers AMDI0010:01/:02.
The old TRX50-era script checked only 0x50 and 0x52 and therefore exposed four modules.
The November 14 update adds the four verified missing hubs. All eight direct temperatures
are now available to libsensors and OpenFan's Memory temperatures group.

`dram-temps-bind.sh` recognizes the board before probing. WRX90 uses only the two verified
controller paths and four known addresses; the known TRX50 board retains 0x50/0x52 on
DesignWare adapters. Other boards are refused. Existing clients are skipped; responding
unbound addresses are handed to the kernel's spd5118 driver for chip validation.
It does not scan unrelated adapters, access SPD EEPROM contents, or write alarm thresholds.

This remains **machine-specific setup**, independent of the Debian app installer.
It needs root, i2c-tools and a kernel with spd5118. The updated script is already installed
at `/usr/local/sbin/dram-temps-bind.sh`, with the enabled
`/etc/systemd/system/dram-temps.service`. Restarting the service is verified and idempotent.
The app itself performs no bus scans or privileged device binding.

For an intentional setup/update on these verified boards:

```sh
sudo install -m 755 packaging/dram-temps/dram-temps-bind.sh /usr/local/sbin/dram-temps-bind.sh
sudo install -m 644 packaging/dram-temps/dram-temps.service /etc/systemd/system/dram-temps.service
sudo systemctl daemon-reload
sudo systemctl enable --now dram-temps.service
sudo systemctl restart dram-temps.service
```

Verify hwmon measurements after startup; missing/unsupported hardware must not be inferred
from the service status alone. Existing direct sensor identities and selections are retained;
new rows default unchecked for Use in curves. Configured alarm limits remain separate from
observed Min/Max, and slot letters for direct SPD readings are still unmapped.

See [current and historical hardware notes](../../DRAM-TEMPS-README.md) and
[BMC telemetry](../../docs/wrx90-bmc-fans.md). The original October setup came from
`/mnt/SOLIDIGM/Claude/dramtemps`; its broad search script is not used by OpenFan.
