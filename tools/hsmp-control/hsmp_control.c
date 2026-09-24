/* SPDX-License-Identifier: MIT
 * Experimental, boot-scoped Threadripper 9970X socket power controller.
 * Uses Linux's documented HSMP GET/SET socket power limit messages, not RyzenAdj code.
 */
#define _POSIX_C_SOURCE 200809L
#include <asm/amd_hsmp.h>
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <linux/ioctl.h>
#include <limits.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

/* Accepted window, kept identical to openfan-linux's CpuPowerControl (100..300 W) so a limit set
 * in the GUI is never refused here at boot. NOT firmware's unsafe 2000 W maximum; values below
 * ~200 W remain untested on this board, where HSMP may clamp them (readback catches that). */
#define MIN_MW 100000U
#define MAX_MW 300000U
#define STATE_DIR "/run/tr9970x-hsmp"
#define STATE_FILE STATE_DIR "/state"
struct state { char boot[40]; unsigned baseline, expected, pending; };

static bool cpu_matches(void)
{
    FILE *f = fopen("/proc/cpuinfo", "r");
    if (!f) return false;
    char line[512];
    bool name = false, family = false, model = false, found = false;
    while (fgets(line, sizeof(line), f)) {
        unsigned n;
        if (line[0] == '\n') {
            if (name && family && model) { found = true; break; }
            name = family = model = false;
            continue;
        }
        if (strncmp(line, "model name", 10) == 0 && strstr(line, "Threadripper 9970X")) name = true;
        if (sscanf(line, "cpu family : %u", &n) == 1 && n == 26) family = true;
        if (sscanf(line, "model : %u", &n) == 1 && n == 8) model = true;
    }
    found = found || (name && family && model);
    fclose(f);
    return found;
}

static bool boot_id(char out[40])
{
    FILE *f = fopen("/proc/sys/kernel/random/boot_id", "r");
    if (!f) return false;
    bool ok = fgets(out, 40, f) != NULL;
    fclose(f);
    if (!ok || strlen(out) != 37 || out[36] != '\n') return false;
    out[36] = 0;
    return true;
}

static bool get_limit(int fd, unsigned *value)
{
    struct hsmp_message m = {0};
    m.msg_id = HSMP_GET_SOCKET_POWER_LIMIT;
    m.response_sz = 1;
    if (ioctl(fd, HSMP_IOCTL_CMD, &m) < 0) { perror("HSMP GET socket power limit"); return false; }
    *value = m.args[0];
    return true;
}

static bool get_word(int fd, unsigned id, unsigned *value)
{
    if (id != HSMP_GET_PROTO_VER && id != HSMP_GET_SMU_VER &&
        id != HSMP_GET_SOCKET_POWER && id != HSMP_GET_SOCKET_POWER_LIMIT_MAX)
        return false;
    struct hsmp_message m = {0};
    m.msg_id = id;
    m.response_sz = 1;
    if (ioctl(fd, HSMP_IOCTL_CMD, &m) < 0) { perror("HSMP GET telemetry"); return false; }
    *value = m.args[0];
    return true;
}

static bool set_limit(int fd, unsigned value)
{
    struct hsmp_message m = {0};
    m.msg_id = HSMP_SET_SOCKET_POWER_LIMIT;
    m.num_args = 1;
    m.args[0] = value;
    if (ioctl(fd, HSMP_IOCTL_CMD, &m) < 0) { perror("HSMP SET socket power limit"); return false; }
    return true;
}

static bool load_state(struct state *s)
{
    int fd = open(STATE_FILE, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) {
        if (errno == ENOENT) return false;
        perror("open state"); exit(2);
    }
    struct stat st;
    if (fstat(fd, &st) || !S_ISREG(st.st_mode) || st.st_uid != 0 || (st.st_mode & 077) || st.st_nlink != 1) {
        fprintf(stderr, "Refusing unsafe state file.\n"); exit(2);
    }
    char buf[160] = {0};
    ssize_t n = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    char tail;
    if (n <= 0 || n >= (ssize_t)sizeof(buf) - 1 ||
        sscanf(buf, "%39s %u %u %u %c", s->boot, &s->baseline, &s->expected, &s->pending, &tail) != 4 ||
        strlen(s->boot) != 36 || s->baseline < MIN_MW || s->baseline > MAX_MW ||
        s->expected < MIN_MW || s->expected > MAX_MW ||
        (s->pending && (s->pending < MIN_MW || s->pending > MAX_MW))) {
        fprintf(stderr, "Refusing malformed or out-of-range state file.\n"); exit(2);
    }
    return true;
}

static bool store_state(const struct state *s)
{
    char temp[] = STATE_DIR "/.state-XXXXXX";
    int fd = mkstemp(temp);
    if (fd < 0) { perror("create state"); return false; }
    (void)fchmod(fd, 0600);
    char text[160];
    int n = snprintf(text, sizeof(text), "%s %u %u %u\n", s->boot, s->baseline, s->expected, s->pending);
    bool ok = n > 0 && n < (int)sizeof(text) && write(fd, text, n) == n && fsync(fd) == 0;
    if (close(fd)) ok = false;
    if (ok && rename(temp, STATE_FILE) == 0) return true;
    perror("store state");
    unlink(temp);
    return false;
}

static bool make_lock(void)
{
    if (mkdir(STATE_DIR, 0700) && errno != EEXIST) { perror("create state directory"); return false; }
    struct stat st;
    if (lstat(STATE_DIR, &st) || !S_ISDIR(st.st_mode) || st.st_uid != 0 || (st.st_mode & 077)) {
        fprintf(stderr, "Refusing unsafe state directory.\n"); return false;
    }
    int fd = open(STATE_DIR "/lock", O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, 0600);
    if (fd < 0 || fstat(fd, &st) || !S_ISREG(st.st_mode) || st.st_uid != 0 || (st.st_mode & 077) || st.st_nlink != 1 || flock(fd, LOCK_EX)) {
        perror("lock state"); if (fd >= 0) close(fd); return false;
    }
    /* Keep fd open to hold lock until process exits. */
    return true;
}

/* ------------------------------------------------------------------------ *
 * Read-only view of the BIOS "SMU Common Options" fields (TDP, TjMax).
 *
 * These live in the AMD CBS setup variable AmdSetupSHP / GUID
 * 3A997502-647A-4C82-998E-52EF9486A247, at byte offsets into the variable DATA
 * (the efivarfs file prefixes those bytes with a 4-byte attribute word):
 *   1043 u8  TDP Control    (1 = Manual, 0 = Auto/fused)
 *   1044 u32 TDP            [mW]
 *   1053 u8  TjMax Control  (1 = Manual, 0 = Auto/fused)
 *   1054 u32 Tjmax          [degrees C]
 * Offsets come from the BIOS Setting Mapping Table JSON embedded in BIOS 0617
 * and were verified against the live variable; see FINDINGS-CBS-VARSTORE.md.
 *
 * READ-ONLY BY DESIGN: this firmware refuses OS-runtime SetVariable with
 * EFI_SECURITY_VIOLATION while Secure Boot is enabled (verified: even a no-op
 * rewrite of Timeout fails), so there is deliberately no write path here. The
 * values are what Setup holds and CBS programs into the SMU at boot; efivarfs
 * caches them at boot, which is exactly how often they can change anyway.
 * ------------------------------------------------------------------------ */
#define CBS_VAR "/sys/firmware/efi/efivars/AmdSetupSHP-3a997502-647a-4c82-998e-52ef9486a247"
#define CBS_MAGIC 0xE5AF127Cu
#define CBS_OFF_TDP_CTL 1043u
#define CBS_OFF_TDP     1044u
#define CBS_OFF_PPT_CTL 1048u
#define CBS_OFF_PPT     1049u
#define CBS_OFF_TJ_CTL  1053u
#define CBS_OFF_TJMAX   1054u
#define CBS_MIN_TDP_MW       1u
#define CBS_MAX_TDP_MW  600000u
#define CBS_MIN_PPT_MW       1u
#define CBS_MAX_PPT_MW  600000u
#define CBS_MIN_TJMAX       20u
#define CBS_MAX_TJMAX      120u

/* Desired limit for the boot helper (`apply`). One integer per file, # comments allowed. */
#define LIMIT_CONFIG "/etc/hsmp-control/ppt_mw"

struct cbs_limits {
    bool ok;                 /* false => reason explains why nothing is reported */
    bool tdp_manual, ppt_manual, tjmax_manual;
    unsigned tdp_mw, ppt_bios_mw, tjmax_c;
    char reason[96];
};

static uint32_t le32(const unsigned char *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static void cbs_fail(struct cbs_limits *c, const char *why)
{
    c->ok = false;
    snprintf(c->reason, sizeof(c->reason), "%s", why);
}

static bool read_cbs(struct cbs_limits *c)
{
    memset(c, 0, sizeof(*c));
    /* Override for a different board/BIOS whose CBS variable differs; the offsets
     * still come from this build, so re-verify before trusting another BIOS. */
    const char *path = getenv("CBS_SETUP_VAR");
    if (!path || !*path) path = CBS_VAR;
    int fd = open(path, O_RDONLY | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) { cbs_fail(c, "CBS setup variable not readable"); return false; }
    unsigned char buf[4096];
    ssize_t n = read(fd, buf, sizeof(buf));
    close(fd);
    if (n < 0) { cbs_fail(c, "read of CBS variable failed"); return false; }
    if (n >= (ssize_t)sizeof(buf)) { cbs_fail(c, "CBS variable larger than expected"); return false; }
    const unsigned char *d = buf + 4;          /* skip the attribute word */
    size_t len = (size_t)n - 4;
    if (len < CBS_OFF_TJMAX + 5) { cbs_fail(c, "CBS variable shorter than expected"); return false; }
    if (le32(d) != CBS_MAGIC) { cbs_fail(c, "CBS magic mismatch (BIOS layout changed?)"); return false; }
    c->tdp_manual = d[CBS_OFF_TDP_CTL] == 1;
    c->ppt_manual = d[CBS_OFF_PPT_CTL] == 1;
    c->tjmax_manual = d[CBS_OFF_TJ_CTL] == 1;
    c->tdp_mw = le32(d + CBS_OFF_TDP);
    c->ppt_bios_mw = le32(d + CBS_OFF_PPT);
    c->tjmax_c = le32(d + CBS_OFF_TJMAX);
    if (c->tdp_manual && (c->tdp_mw < CBS_MIN_TDP_MW || c->tdp_mw > CBS_MAX_TDP_MW)) {
        cbs_fail(c, "TDP value outside plausible range"); return false;
    }
    if (c->ppt_manual && (c->ppt_bios_mw < CBS_MIN_PPT_MW || c->ppt_bios_mw > CBS_MAX_PPT_MW)) {
        cbs_fail(c, "PPT value outside plausible range"); return false;
    }
    if (c->tjmax_manual && (c->tjmax_c < CBS_MIN_TJMAX || c->tjmax_c > CBS_MAX_TJMAX)) {
        cbs_fail(c, "TjMax value outside plausible range"); return false;
    }
    c->ok = true;
    c->reason[0] = 0;
    return true;
}

/* Desired limit for the boot helper (`apply`). Absent or comment-only file means "no override
 * wanted" and is not an error; a present-but-unusable value is, so the unit fails loudly rather
 * than silently leaving the CPU at a different limit than configured. */
enum cfg_result { CFG_OK, CFG_ABSENT, CFG_BAD };

static enum cfg_result read_config_mw(unsigned *out)
{
    const char *path = getenv("HSMP_LIMIT_CONFIG");
    if (!path || !*path) path = LIMIT_CONFIG;
    FILE *f = fopen(path, "re");
    if (!f) {
        if (errno != ENOENT) fprintf(stderr, "Cannot read %s: %s\n", path, strerror(errno));
        return CFG_ABSENT;
    }
    char line[64];
    enum cfg_result result = CFG_ABSENT;
    while (fgets(line, sizeof(line), f)) {
        char *p = line;
        while (*p == ' ' || *p == '\t') p++;
        if (*p == '#' || *p == '\n' || *p == '\r' || *p == '\0') continue;
        char *end;
        errno = 0;
        unsigned long x = strtoul(p, &end, 10);
        if (errno != 0 || end == p) {
            fprintf(stderr, "Refusing: %s does not hold an integer milliwatt value.\n", path);
            result = CFG_BAD;
        } else if (x < MIN_MW || x > MAX_MW) {
            fprintf(stderr, "Refusing: configured limit %lu mW outside conservative range %u..%u.\n",
                    x, MIN_MW, MAX_MW);
            result = CFG_BAD;
        } else {
            *out = (unsigned)x;
            result = CFG_OK;
        }
        break;                                       /* first value line wins */
    }
    fclose(f);
    return result;
}

static int usage(const char *prog)
{
    fprintf(stderr,
        "usage: %s info | status | limits [--json] | set <100000..300000 mW> --confirm |\n"
        "          restore --confirm | apply\n"
        "\n"
        "  info            HSMP/CBS identity, live socket power, and the BIOS limits\n"
        "  status          current socket power limit plus this boot's journal\n"
        "  limits          PPT (live + BIOS default) and TDP/TjMax (read-only)\n"
        "  limits --json   same values as one JSON object for other programs\n"
        "  set/restore     change or restore the socket power limit (PPT), needs root\n"
        "  apply           re-assert the configured limit at boot; reads " LIMIT_CONFIG "\n"
        "\n"
        "HSMP limits are volatile SMU state: they do not survive a reboot, so persistence means\n"
        "re-applying (see the hsmp-control-apply.service unit). TDP and TjMax are read-only:\n"
        "they live in the BIOS CBS variable AmdSetupSHP and this firmware refuses OS-runtime\n"
        "SetVariable while Secure Boot is enabled, so no write path is offered. Edit them in\n"
        "Setup; they take effect on the next boot.\n",
        prog);
    return 2;
}

/* Contract shared with the openfan-linux C# reader: a value is null when it is Auto (the
 * effective limit then comes from CPU fuses and is not exposed to the OS) or when the CBS
 * variable could not be read/validated. Never 0 as a stand-in for "unknown". */
static void json_u32_or_null(bool present, unsigned v)
{
    if (present) printf("%u", v); else printf("null");
}

static void print_limits_json(unsigned ppt_mw, const struct cbs_limits *c)
{
    printf("{\"ppt_limit_mw\":%u,", ppt_mw);
    if (c->ok) {
        printf("\"tdp_mw\":"); json_u32_or_null(c->tdp_manual, c->tdp_mw);
        printf(",\"ppt_bios_mw\":"); json_u32_or_null(c->ppt_manual, c->ppt_bios_mw);
        printf(",\"tjmax_c\":"); json_u32_or_null(c->tjmax_manual, c->tjmax_c);
        printf(",\"tdp_control\":\"%s\",\"ppt_bios_control\":\"%s\",\"tjmax_control\":\"%s\","
               "\"cbs_ok\":true,",
               c->tdp_manual ? "manual" : "auto",
               c->ppt_manual ? "manual" : "auto",
               c->tjmax_manual ? "manual" : "auto");
    } else {
        printf("\"tdp_mw\":null,\"ppt_bios_mw\":null,\"tjmax_c\":null,"
               "\"tdp_control\":null,\"ppt_bios_control\":null,\"tjmax_control\":null,"
               "\"cbs_ok\":false,");
    }
    printf("\"cbs_reason\":\"%s\",", c->ok ? "" : c->reason);
    printf("\"writable\":{\"ppt\":true,\"tdp\":false,\"tjmax\":false}}\n");
}

static void print_limits_text(unsigned ppt_mw, const struct cbs_limits *c)
{
    printf("PPT   (live socket power limit): %u mW   volatile; writable: set <mW> --confirm\n", ppt_mw);
    if (!c->ok) {
        printf("PPT   (BIOS default, flash) : unavailable (%s)\n", c->reason);
        printf("TDP   (BIOS CBS, read-only) : unavailable (%s)\n", c->reason);
        printf("TjMax (BIOS CBS, read-only) : unavailable (%s)\n", c->reason);
        return;
    }
    if (c->ppt_manual) printf("PPT   (BIOS default, flash) : %u mW   what the SMU gets at next boot\n", c->ppt_bios_mw);
    else printf("PPT   (BIOS default, flash) : Auto - fused default\n");
    if (c->tdp_manual) printf("TDP   (BIOS CBS, read-only) : %u mW\n", c->tdp_mw);
    else printf("TDP   (BIOS CBS, read-only) : Auto - fused default, value not exposed to the OS\n");
    if (c->tjmax_manual) printf("TjMax (BIOS CBS, read-only) : %u C\n", c->tjmax_c);
    else printf("TjMax (BIOS CBS, read-only) : Auto - fused default, value not exposed to the OS\n");
}

int main(int argc, char **argv)
{
    bool status = argc == 2 && strcmp(argv[1], "status") == 0;
    bool info = argc == 2 && strcmp(argv[1], "info") == 0;
    bool limits = (argc == 2 || argc == 3) && strcmp(argv[1], "limits") == 0 &&
                  (argc == 2 || strcmp(argv[2], "--json") == 0);
    bool limits_json = limits && argc == 3;
    bool restore = argc == 3 && strcmp(argv[1], "restore") == 0 && strcmp(argv[2], "--confirm") == 0;
    bool set = argc == 4 && strcmp(argv[1], "set") == 0 && strcmp(argv[3], "--confirm") == 0;
    bool apply = argc == 2 && strcmp(argv[1], "apply") == 0;
    bool ro = status || info || limits;          /* read-only commands: no root, no lock */
    unsigned target = 0;
    if (!status && !info && !limits && !restore && !set && !apply) return usage(argv[0]);
    if (set) {
        char *end;
        errno = 0;
        unsigned long x = strtoul(argv[2], &end, 10);
        if (errno || end == argv[2] || *end || x < MIN_MW || x > MAX_MW) {
            fprintf(stderr, "Refusing: requested mW outside conservative range %u..%u.\n", MIN_MW, MAX_MW);
            return 2;
        }
        target = (unsigned)x;
    }
    if (apply) {
        /* Checked before touching hardware: a machine with no override configured has nothing
         * to do here, and the boot unit should not fail because of that. */
        enum cfg_result r = read_config_mw(&target);
        if (r == CFG_ABSENT) { printf("No configured limit; nothing to apply.\n"); return 0; }
        if (r == CFG_BAD) return 2;
    }
    if (!cpu_matches()) { fprintf(stderr, "Refusing: expected 9970X family 26 model 8.\n"); return 2; }
    if (!ro && geteuid() != 0) { fprintf(stderr, "SET/restore requires root.\n"); return 2; }
    char boot[40];
    if (!boot_id(boot)) { fprintf(stderr, "Cannot identify current boot.\n"); return 2; }
    if (!ro && !make_lock()) return 2;
    int fd = open("/dev/hsmp", (ro ? O_RDONLY : O_RDWR) | O_CLOEXEC | O_NOFOLLOW);
    if (fd < 0) { perror("open /dev/hsmp"); return 2; }
    unsigned protocol, current;
    if (!get_word(fd, HSMP_GET_PROTO_VER, &protocol) || protocol != 7 || !get_limit(fd, &current)) {
        fprintf(stderr, "Refusing: unexpected protocol or GET failed.\n"); close(fd); return 2;
    }
    struct state s = {0};
    bool saved = geteuid() == 0 && load_state(&s);
    if (saved && strcmp(s.boot, boot)) {
        if (status || limits) saved = false;
        else { fprintf(stderr, "Refusing stale state from previous boot; inspect/remove it manually.\n"); close(fd); return 2; }
    }
    struct cbs_limits cbs;
    read_cbs(&cbs);
    if (limits) {
        if (limits_json) print_limits_json(current, &cbs);
        else print_limits_text(current, &cbs);
        close(fd);
        return 0;
    }
    printf("Current socket power limit: %u mW\n", current);
    if (saved) printf("This boot baseline: %u mW; last verified: %u mW; pending: %u mW\n", s.baseline, s.expected, s.pending);
    if (status) { close(fd); return 0; }
    if (info) {
        unsigned smu, power, maximum;
        bool ok = get_word(fd, HSMP_GET_SMU_VER, &smu) &&
                  get_word(fd, HSMP_GET_SOCKET_POWER, &power) &&
                  get_word(fd, HSMP_GET_SOCKET_POWER_LIMIT_MAX, &maximum);
        if (!ok) { close(fd); return 1; }
        printf("CPU: AMD Ryzen Threadripper 9970X (family 0x1a, model 0x08)\n");
        printf("HSMP protocol: %u; SMU firmware: %u.%u.%u\n", protocol,
               smu >> 16, (smu >> 8) & 255, smu & 255);
        printf("Socket power: %.3f W; socket power limit: %.3f W\n", power / 1000.0, current / 1000.0);
        printf("Firmware-reported maximum: %.3f W (NOT a recommended target)\n", maximum / 1000.0);
        if (!cbs.ok)
            printf("TDP/TjMax: unavailable (%s)\n", cbs.reason);
        else {
            if (cbs.tdp_manual) printf("TDP (BIOS CBS, read-only): %u mW\n", cbs.tdp_mw);
            else printf("TDP (BIOS CBS, read-only): Auto - fused default\n");
            if (cbs.tjmax_manual) printf("TjMax (BIOS CBS, read-only): %u C\n", cbs.tjmax_c);
            else printf("TjMax (BIOS CBS, read-only): Auto - fused default\n");
        }
        close(fd);
        return 0;
    }
    if (saved && current != s.expected && current != s.pending) {
        fprintf(stderr, "Refusing: another actor changed the limit; no SET sent.\n"); close(fd); return 2;
    }
    if (!saved) {
        if (restore) { fprintf(stderr, "No baseline recorded by this tool this boot; cannot restore.\n"); close(fd); return 2; }
        if (current < MIN_MW || current > MAX_MW) {
            fprintf(stderr, "Refusing baseline outside conservative range.\n"); close(fd); return 2;
        }
        memcpy(s.boot, boot, sizeof(s.boot));
        s.baseline = s.expected = current;
    }
    if (restore) target = s.baseline;
    if (target < MIN_MW || target > MAX_MW) {
        fprintf(stderr, "Refusing: final target outside experimental range.\n"); close(fd); return 2;
    }
    if (target == current) { printf("Already at target; no SET sent.\n"); close(fd); return 0; }
    /* Journal target before SET so interrupted runs can be examined or restored. */
    s.pending = target;
    if (!store_state(&s)) { close(fd); return 2; }
    unsigned immediately_before;
    unsigned maximum;
    if (!get_limit(fd, &immediately_before) || immediately_before != current ||
        !get_word(fd, HSMP_GET_SOCKET_POWER_LIMIT_MAX, &maximum) || target > maximum) {
        fprintf(stderr, "Refusing: changed limit, failed GET, or target exceeds firmware maximum.\n"); close(fd); return 2;
    }
    if (!set_limit(fd, target)) { close(fd); return 1; }
    unsigned actual;
    if (!get_limit(fd, &actual)) { fprintf(stderr, "SET outcome unknown: readback failed.\n"); close(fd); return 1; }
    printf("Requested: %u mW; readback: %u mW (%s)\n", target, actual, actual == target ? "verified" : "MISMATCH");
    if (actual != target) { close(fd); return 1; }
    s.expected = actual;
    s.pending = 0;
    if (!store_state(&s)) { fprintf(stderr, "Write verified but state update failed; inspect before retry.\n"); close(fd); return 1; }
    close(fd);
    return 0;
}
