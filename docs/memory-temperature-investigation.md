# RAM temperature investigation

## October 19 update — working readings

The October 18 investigation below is historical. The later successful setup is documented
in `DRAM-TEMPS-README.md` and preserved in `packaging/dram-temps/`. Four SPD5118 hubs are now
bound on the DesignWare adapters beneath controllers `AMDI0010:01` and `AMDI0010:02`, each
at addresses 0x50 and 0x52. The already-installed, enabled `dram-temps.service` ran successfully
on the October 19 boot. Read-only live samples are approximately 41–45 °C.

OpenFan uses the existing libsensors reader, with no extra sampling process or privileged
GUI operations. Memory temperatures appear in one compact table with controller/address
labels. Stable SPD hub identities survive both hwmon and I2C adapter renumbering; the sysfs
fallback uses the same identities. Custom library labels are honored, and optional alarm
flags remain separate from temperatures. No physical slot mapping or inferred thermal
health threshold is applied. Each row also shows the hub's configured Low, High, Critical low
and Critical high limits, read through libsensors (or sysfs fallback). All four currently report
0, 55, 0 and 85 °C respectively. These read-only alarm settings are separate from observed
Min/Max values and do not establish manufacturer-recommended operating limits. Limit channels
are typed separately so temperature pickers offer only actual measurements. These readings
are the SPD hub sensors, not individual DRAM dies.

Hide/collapse choices save immediately and resume after restart. Hiding or collapsing pauses
optional reads while preserving inputs required by enabled fan curves and the overlay.
The user's existing group visibility and collapse choices are preserved.

## October 18 initial investigation (superseded)

October 18, 2026. Machine: ASUS Pro WS TRX50-SAGE WIFI A, Threadripper 9970X, Ubuntu 26.10,
kernel 7.3.0-9. Firmware reports four Kingston KF556R28-32, 32 GiB DDR5 ECC registered modules,
128 GiB total, configured at 5600 MT/s, on channels A/C/E/G.

## What HWiNFO is probably showing

HWiNFO's author identifies the DDR5 **SPD Hub Temperature** as a sensor on the DIMM's SPD hub chip.
It is a module-local reading, not a temperature measurement for every DRAM chip.
[HWiNFO author's explanation](https://www.hwinfo.com/forum/threads/ram-temps-on-hwinfo.9572/).

Linux's `spd5118` driver supports many DDR5 SPD hub temperature sensors and exports `temp1_input`
through hwmon. OpenFan's new libsensors reader already supports those readings and groups SPD5118
and JC42 devices as memory temperatures. The unresolved issue on this system is obtaining a kernel
sensor device, not adding the temperature unit or display row.
[Kernel driver documentation](https://docs.kernel.org/hwmon/spd5118.html).

## Checks completed

- `spd5118` is included with this kernel. Loaded the signed in-tree module successfully; no temperature
  devices appeared. Unloaded the test module afterward; no permanent modules-load change was made.
- This kernel has `CONFIG_SENSORS_SPD5118_DETECT` disabled. Discovery normally depends on firmware/DMI
  instantiation. Loading the module alone consequently does not guarantee new sensor devices.
- The AMD PIIX4 SMBus driver and three adapters are already present. No SPD I2C clients or I3C memory
  devices are enumerated. Existing DesignWare devices are unrelated input/USB-controller devices.
- Read only the standard SPD identification registers at addresses 0x50–0x57 on the two non-AUX PIIX4
  ports (this boot's adapters 4 and 5). Every address returned ENXIO (no response). No forced access,
  register writes, EEPROM dumps, unrelated-address scan or AUX-bus scan was used. Upstream PIIX4 code
  specifically cautions that AUX buses can falsely report devices with zero data.
  [PIIX4 driver source](https://github.com/torvalds/linux/blob/master/drivers/i2c/busses/i2c-piix4.c).
- Tested the documented read-only HSMP enabled-command query and DIMM thermal query for address 0 and
  0x50. Enabled-command query returned ENOMSG; thermal queries returned EINVAL. No usable temperature was
  returned, and no limits or thermal settings were changed. This does not exhaust every platform-specific
  address/interface, but provides no basis for adding HSMP DIMM temperature rows yet.
  [Kernel HSMP interface](https://docs.kernel.org/arch/x86/amd_hsmp.html).
- ASUS EC and NCT6799 already export their supported channels. A generic TSI/AUX/CPU channel is not
  identified as a DIMM reading; OpenFan does not relabel one as memory temperature without evidence.
- ECC counters are accessible and have been added. EDAC exports eight rank groups for the four physical
  modules; rank groups are not eight DIMM slots. Error counters are not temperature readings.

## Conclusion and next steps

No trustworthy live RAM temperature was obtained through the Linux interfaces tested. This is not
proof that the hardware lacks sensors. The missing link appears to be platform/firmware access to the
DIMM hubs or equivalent telemetry; the exact cause remains unconfirmed.

HWiNFO has published fixes specifically for DIMM polling on ASUS TRX50/WRX90 systems with newer BIOS,
and for AMD TRX50/WRX80/WRX90 generally. This supports investigating a platform-specific path, but does
not document its register sequence or justify copying an unverified access method.
[HWiNFO version history, v8.44 and v8.26](https://www.hwinfo.com/version-history/).

Next: compare the precise HWiNFO sensor labels and board/BIOS information, investigate a documented
ASUS/AMD firmware or kernel-driver path, then validate readings and physical-slot identity. If firmware
exposes SPD5118 devices after a supported configuration/driver change, OpenFan will discover them on
its next scan. Any optional privileged reader should expose fixed, read-only metrics through the helper,
not raw bus/register access from the GUI. No speculative DKMS driver, firmware changes, forced binding
or bus reconfiguration was installed during this investigation.

Evidence stays local under ignored `publish/`: `memory-dmi.log`, `ddr5-hub-results.log`,
`hsmp-memory-results.log`, `hsmp-memory-results-50.log`, and the corresponding read-only probe sources.
