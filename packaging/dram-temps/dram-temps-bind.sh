#!/bin/sh
# Machine-specific DDR5 temperature setup. Never probe unrelated SMBus/GPU adapters.
set -eu
board=$(cat /sys/class/dmi/id/board_name)
case "$board" in
  'Pro WS WRX90E-SAGE SE') addresses='50 51 52 53' ;;
  'Pro WS TRX50-SAGE WIFI A') addresses='50 52' ;;
  *) echo "Unsupported board for OpenFan DIMM binding: $board" >&2; exit 1 ;;
esac
modprobe i2c-dev
modprobe spd5118
for adapter in /sys/bus/i2c/devices/i2c-*; do
  [ "$(cat "$adapter/name" 2>/dev/null)" = 'Synopsys DesignWare I2C adapter' ] || continue
  if [ "$board" = 'Pro WS WRX90E-SAGE SE' ]; then
    case "$(readlink -f "$adapter")" in
      /sys/devices/platform/AMDI0010:01/i2c-*|/sys/devices/platform/AMDI0010:02/i2c-*) ;;
      *) continue ;;
    esac
  fi
  bus=${adapter##*i2c-}
  for address in $addresses; do
    [ -e "/sys/bus/i2c/devices/$bus-00$address" ] && continue
    # Read only register 0 on known memory-hub addresses; the kernel validates the chip.
    i2cget -y "$bus" "0x$address" 0x00 >/dev/null 2>&1 || continue
    printf 'spd5118 0x%s\n' "$address" > "$adapter/new_device"
  done
done
