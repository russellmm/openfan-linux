# ASUS Pro WS TRX50-SAGE WIFI A research references

The user supplied an ASUS motherboard block-diagram screenshot on October 19, 2026.
Original: `/mnt/DriveC/Users/russell/Desktop/screenshots/2026-09-23 11_41_27-E26245_Pro_WS_TRX50-SAGE_WIFI_A_EM_WEB.pdf (SECURED) - Adobe Acrobat Pro DC.jpg`.
A local research copy is under ignored `publish/nvme-audit/references/`. The image is reference
material, not an instruction to access registers or probe buses.

The diagram establishes these routes:

| Connection | Route shown |
| --- | --- |
| DDR5 channels A/C/E/G | Direct to the TR5 CPU |
| M.2_1, M.2_2, M.2_4 | CPU PCIe 5.0 ×4 |
| M.2_3 | Chipset PCIe 4.0 ×4, through a MUX/DEMUX shared with SlimSAS_2 |
| SlimSAS_1 | CPU PCIe 4.0 ×4 |
| TRX50 chipset uplink | CPU PCIe ×4 |
| Super I/O and embedded controller | LPC buses through the IT8883 eSPI-to-LPC bridge to the CPU |
| USB4 controller | CPU PCIe 4.0 ×4, with two USB4 rear ports |
| Audio | CPU HDA to Realtek ALC1220P |
| 2.5 Gb LAN | Chipset PCIe 3.0 ×1 |
| 10 Gb LAN | Chipset PCIe 3.0 ×2 |
| Wi-Fi / Bluetooth | Chipset PCIe 3.0 ×1 plus USB 2.0 |

IT8883 is identified as a bridge, not the Super I/O sensor chip. The diagram does not name the
Super I/O, EC, USB4, NIC or other temperature-monitoring ICs, nor does it identify the locations
of a drive's internal temperature probes. Existing kernel hwmon identities (NCT6799, ASUS EC,
SPD5118) are separate observed evidence. Match actual PCI topology and firmware data before
assigning physical slot names; a route in this diagram alone is insufficient to map a PCI BDF
to a silkscreen label.

The user's HWiNFO drive screenshot, also saved locally, shows read/write/total activity,
read/write rates and read/write totals. Linux equivalents in OpenFan use block-device statistics;
their scope and timing differ from Windows counters and are documented in `nvme-sensors.md`.
