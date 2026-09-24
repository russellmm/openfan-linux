#!/bin/sh
# Read-only smoke tests; never send a SET or open /dev/hsmp for writing.
set -eu
cc -std=c11 -Wall -Wextra -Werror -O2 -o hsmp-control hsmp_control.c
./hsmp-control info
./hsmp-control status
./hsmp-control limits

# limits --json must parse, carry the live PPT, and advertise TDP/TjMax as read-only.
./hsmp-control limits --json | python3 -c '
import json, sys
d = json.load(sys.stdin)
assert d["ppt_limit_mw"] > 0, d
assert d["writable"] == {"ppt": True, "tdp": False, "tjmax": False}, d["writable"]
for key in ("tdp_mw", "ppt_bios_mw", "tjmax_c"):
    assert key in d, d
if d["cbs_ok"]:
    # A value is null exactly when its control says auto: never 0 standing in for unknown.
    for value, control in (("tdp_mw", "tdp_control"), ("ppt_bios_mw", "ppt_bios_control"),
                           ("tjmax_c", "tjmax_control")):
        assert d[control] in ("manual", "auto"), d
        assert (d[value] is None) == (d[control] == "auto"), (value, d)
else:
    assert all(d[k] is None for k in ("tdp_mw", "ppt_bios_mw", "tjmax_c")), d
    assert d["cbs_reason"], d
'

# The CBS reader must degrade honestly instead of reporting garbage.
CBS_SETUP_VAR=/nonexistent ./hsmp-control limits --json | python3 -c '
import json, sys
d = json.load(sys.stdin)
assert d["cbs_ok"] is False and d["tdp_mw"] is None and d["cbs_reason"], d
'
CBS_SETUP_VAR=/sys/firmware/efi/efivars/Timeout-8be4df61-93ca-11d2-aa0d-00e098032b8c \
    ./hsmp-control limits --json | python3 -c '
import json, sys
d = json.load(sys.stdin)
assert d["cbs_ok"] is False and d["tdp_mw"] is None, d
'

# apply: no config is a successful no-op; anything unusable must fail before any SET.
if [ -e /etc/hsmp-control/ppt_mw ]; then
    printf 'SKIP: /etc/hsmp-control/ppt_mw exists; refusing to run apply checks against it.\n'
else
    HSMP_LIMIT_CONFIG=/nonexistent ./hsmp-control apply >/dev/null
fi
tmp_bad=$(mktemp); printf 'lots of watts\n' > "$tmp_bad"
tmp_oor=$(mktemp); printf '# comment\n400000\n' > "$tmp_oor"
tmp_ok=$(mktemp);  printf '290000\n' > "$tmp_ok"
tmp_low=$(mktemp); printf '150000\n' > "$tmp_low"
trap 'rm -f "$tmp_bad" "$tmp_oor" "$tmp_ok" "$tmp_low"' EXIT

expect_refusal() {
    if "$@" >/dev/null 2>&1; then
        printf 'FAIL: unexpectedly accepted unsafe command\n' >&2
        exit 1
    fi
}
expect_refusal ./hsmp-control set 400000 --confirm
expect_refusal ./hsmp-control set 0 --confirm
expect_refusal ./hsmp-control set nope --confirm
expect_refusal ./hsmp-control set 290000
expect_refusal ./hsmp-control restore
# There is deliberately no write path for the read-only BIOS fields.
expect_refusal ./hsmp-control set-tdp 250000 --confirm
expect_refusal ./hsmp-control set-tjmax 85 --confirm
expect_refusal ./hsmp-control limits --xml
# apply must refuse bad config, and (as an unprivileged user) any real write attempt.
expect_refusal env HSMP_LIMIT_CONFIG="$tmp_bad" ./hsmp-control apply
expect_refusal env HSMP_LIMIT_CONFIG="$tmp_oor" ./hsmp-control apply
if [ "$(id -u)" -ne 0 ]; then
    # Only checked unprivileged: as root a valid config would legitimately send a SET, and this
    # script never changes the limit.
    expect_refusal env HSMP_LIMIT_CONFIG="$tmp_ok" ./hsmp-control apply

    # The window matches openfan-linux's 100-300 W, so 150 W must be accepted as a *valid*
    # configuration — refused only for lacking root, never as out of range. Otherwise a limit set
    # in the GUI would silently fail to apply at boot.
    low_msg=$(HSMP_LIMIT_CONFIG="$tmp_low" ./hsmp-control apply 2>&1 || true)
    case "$low_msg" in
        *"outside conservative range"*)
            printf 'FAIL: 150 W rejected as out of window; the two tools disagree\n' >&2
            exit 1 ;;
    esac
fi
printf 'CLI validation checks passed; no SET issued.\n'
