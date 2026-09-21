#!/bin/sh
# OpenFan Linux — grant the 'openfan' group write access to PWM duty + enable nodes
# of any hwmon device (spec §3.4). Invoked by udev with DEVPATH set.
# Deliberately narrow: ONLY pwm<N> and pwm<N>_enable — never auto_point tables,
# temp/fan inputs, or anything else under the hwmon tree.

[ -n "$DEVPATH" ] || exit 0
base="/sys$DEVPATH"
[ -d "$base" ] || exit 0

is_num() {
    case "$1" in
        ''|*[!0-9]*) return 1 ;;
        *) return 0 ;;
    esac
}

for f in "$base"/pwm*; do
    [ -f "$f" ] || continue
    name=${f##*/}
    rest=${name#pwm}
    case "$rest" in
        *_enable) is_num "${rest%_enable}" || continue ;;
        *)        is_num "$rest"           || continue ;;
    esac
    chgrp openfan "$f" 2>/dev/null
    chmod g+w "$f" 2>/dev/null
done

exit 0
