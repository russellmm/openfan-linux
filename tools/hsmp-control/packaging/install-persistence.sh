#!/bin/sh
# Install boot-persistent CPU socket power limiting (hsmp-control + systemd oneshot unit).
#
#   sudo ./install-persistence.sh <watts>        # e.g. 295
#
# Pass the limit you actually want. Enabling the unit applies it IMMEDIATELY as well as at every
# later boot, so pass your current value (see `./hsmp-control limits`) if you want no change today.
# Accepted window: 100-300 W — the same range openfan-linux's CPU tab offers.
#
# Reversible with:  sudo ./install-persistence.sh --remove
set -eu

UNIT_NAME="hsmp-control-apply.service"
BIN_SRC="./hsmp-control"
BIN_SRC_C="./hsmp_control.c"
REPO_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$REPO_DIR"

require_root() {
    if [ "$(id -u)" -ne 0 ]; then
        printf 'needs root:  sudo %s ...\n' "$0" >&2
        exit 1
    fi
}

remove() {
    systemctl disable --now "$UNIT_NAME" 2>/dev/null || true
    rm -f "/etc/systemd/system/$UNIT_NAME" /etc/hsmp-control/ppt_mw
    rmdir /etc/hsmp-control 2>/dev/null || true
    systemctl daemon-reload
    printf 'removed %s and /etc/hsmp-control (the live limit is untouched; reboot restores the BIOS value)\n' "$UNIT_NAME"
}

if [ "${1:-}" = "--remove" ]; then require_root; remove; exit 0; fi

watts=${1:-}
case "$watts" in
    ''|*[!0-9]*) printf 'usage: %s <watts>   (integer 100-300)\n' "$0" >&2; exit 2 ;;
esac
if [ "$watts" -lt 100 ] || [ "$watts" -gt 300 ]; then
    printf 'refusing %s W: outside the accepted 100-300 W window\n' "$watts" >&2
    exit 2
fi
require_root

# Build if missing or older than the source, so the installed binary matches this repo.
if [ ! -x "$BIN_SRC" ] || [ "$BIN_SRC_C" -nt "$BIN_SRC" ]; then
    printf 'building hsmp-control...\n'
    cc -std=c11 -Wall -Wextra -Werror -O2 -o "$BIN_SRC" "$BIN_SRC_C"
fi

printf 'installing /usr/local/bin/hsmp-control\n'
install -d -m 0755 /usr/local/bin
install -m 0755 "$BIN_SRC" /usr/local/bin/hsmp-control

install -d -m 0755 /etc/hsmp-control
if [ -e /etc/hsmp-control/ppt_mw ]; then
    backup="/etc/hsmp-control/ppt_mw.pre-$(date +%Y%m%d-%H%M%S)"
    cp -p /etc/hsmp-control/ppt_mw "$backup"
    printf 'existing config saved to %s\n' "$backup"
fi
printf '%s000\n' "$watts" > /etc/hsmp-control/ppt_mw      # the tool reads milliwatts
chmod 0644 /etc/hsmp-control/ppt_mw

install -m 0644 "packaging/$UNIT_NAME" "/etc/systemd/system/$UNIT_NAME"
systemctl daemon-reload
systemctl enable --now "$UNIT_NAME"

printf '\n--- verification ---\n'
printf 'unit enabled : %s\n' "$(systemctl is-enabled "$UNIT_NAME")"
printf 'unit active  : %s\n' "$(systemctl is-active "$UNIT_NAME")"
/usr/local/bin/hsmp-control limits || true

live=$(/usr/local/bin/hsmp-control limits --json | python3 -c 'import json,sys;print(json.load(sys.stdin)["ppt_limit_mw"])')
want=$((watts * 1000))
if [ "$live" -eq "$want" ]; then
    printf '\nPASS: live limit is %s W and will be re-asserted at every boot.\n' "$watts"
    printf 'openfan-linux should now show a green "re-applied at every boot".\n'
else
    printf '\nCHECK: configured %s W but live limit reads %s mW — inspect:\n' "$watts" "$live"
    printf '  journalctl -u %s --no-pager -n 20\n' "$UNIT_NAME"
    exit 1
fi
