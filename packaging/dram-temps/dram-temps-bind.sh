#!/bin/sh
# Bind spd5118 to the DDR5 RDIMM SPD hubs on the DesignWare I2C adapters.
modprobe i2c-dev
modprobe spd5118
for a in /sys/bus/i2c/devices/i2c-*; do
  [ "$(cat "$a/name" 2>/dev/null)" = "Synopsys DesignWare I2C adapter" ] || continue
  b=${a##*i2c-}
  for addr in 50 52; do
    [ -e "/sys/bus/i2c/devices/$b-00$addr" ] && continue
    # Only bind where something answers (i2cget exits non-zero otherwise)
    i2cget -y "$b" 0x$addr 0x00 >/dev/null 2>&1 || continue
    echo spd5118 0x$addr > "$a/new_device" 2>/dev/null
  done
done
exit 0
