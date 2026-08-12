// pipe2() is a GNU/Linux extension; its declaration in <unistd.h> is gated behind _GNU_SOURCE
// (or _DEFAULT_SOURCE) on glibc. Define it BEFORE the first system header is pulled in (via
// pty_shim.h below). Linux-only so macOS/BSD feature-test semantics are untouched.
#if defined(__linux__) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif

#include "pty_shim.h"

#include <sys/ioctl.h>
#include <stddef.h> // offsetof
#include <sys/stat.h>
#include <sys/xattr.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <fcntl.h>
#include <unistd.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <poll.h>
#include <time.h>
#include <signal.h>

#ifdef __APPLE__
#include <util.h> // forkpty
#else
#include <pty.h> // forkpty (glibc/musl)
#endif

#ifdef __linux__
#include <sys/prctl.h>
#endif

// ── existing (unchanged) ────────────────────────────────────────────────────────────────
int pty_set_winsize(int fd, unsigned short rows, unsigned short cols) {
    struct winsize ws = {0};
    ws.ws_row = rows;
    ws.ws_col = cols;
    return ioctl(fd, TIOCSWINSZ, &ws);
}

// ── L1-shim(a): execution-plan construction ─────────────────────────────────────────────
//
// The fd-bound kernel-floor probe (execveat/AT_EMPTY_PATH) and the fd-bound privilege
// preflight (fgetxattr(security.capability)) are genuinely Linux-only kernel/glibc
// features; every use of them below is `#ifdef __linux__`-guarded so this file still
// compiles (and the portable PATH/shebang plan-construction logic still runs) on other
// POSIX platforms such as macOS — but on those platforms containment can never be proven,
// so every plan degrades to uncontained EXEC_PATH/EXEC_FD-uncontained, matching the
// fail-closed rule ("never a false proof").

#define PTY_EXEC_FD   1
#define PTY_EXEC_PATH 2

struct pty_exec_plan {
    int    mode;       // PTY_EXEC_FD or PTY_EXEC_PATH
    int    exec_fd;    // valid iff mode == PTY_EXEC_FD; -1 otherwise. O_CLOEXEC — closes on
                        // successful exec automatically; freed explicitly on every other path.
    char  *exec_path;  // valid iff mode == PTY_EXEC_PATH; NULL otherwise. Owned (strdup'd).
    char **argv;       // NULL-terminated, owned (deep-copied).
    int    contained;  // 1 = EXEC_FD + proven non-privileged; 0 = uncontained.
};

int pty_probe_execveat(void) {
#ifdef __linux__
    errno = 0;
    // Deliberately bogus fd (-1) + empty pathname via the RAW syscall — bypasses any glibc
    // wrapper (only added in glibc 2.34) so the floor stays a KERNEL floor. EBADF means the
    // syscall validated flags and reached fd validation (i.e. it EXISTS); ENOSYS means it
    // doesn't. Every OTHER errno (EINVAL, EPERM from a seccomp filter, anything else) is
    // fail-safe treated as unsupported — classification must never be left undefined.
    long rc = syscall(SYS_execveat, -1, "", NULL, NULL, AT_EMPTY_PATH);
    if (rc == 0) return 1;
    return errno == EBADF ? 1 : 0;
#else
    // execveat is Linux-only; every other platform takes the EXEC_PATH-uncontained fallback.
    return 0;
#endif
}

static char **dup_argv(char *const argv[]) {
    int n = 0;
    while (argv[n]) n++;
    char **copy = calloc((size_t)n + 1, sizeof(char*));
    if (!copy) return NULL;
    for (int i = 0; i < n; i++) {
        copy[i] = strdup(argv[i]);
        if (!copy[i]) { for (int j = 0; j < i; j++) free(copy[j]); free(copy); return NULL; }
    }
    copy[n] = NULL;
    return copy;
}

static void free_argv(char **argv) {
    if (!argv) return;
    for (int i = 0; argv[i]; i++) free(argv[i]);
    free(argv);
}

static int build_execpath_plan(const char *exe_abs_path, char *const orig_argv[], int contained, pty_exec_plan **out_plan) {
    pty_exec_plan *plan = calloc(1, sizeof(*plan));
    if (!plan) return -1;
    plan->mode      = PTY_EXEC_PATH;
    plan->exec_fd   = -1;
    plan->exec_path = strdup(exe_abs_path);
    plan->argv      = dup_argv(orig_argv);
    plan->contained = contained;
    if (!plan->exec_path || !plan->argv) { pty_exec_plan *p = plan; pty_plan_free(&p); return -1; }
    *out_plan = plan;
    return 0;
}

// fd-bound privilege preflight (pinned, spec §4.2): the check and the exec share ONE open
// file — never a path-based stat-then-execve (TOCTOU-broken). Only a clean "no bits, no
// capability xattr" proves non-privileged; any other outcome (including a read/stat ERROR)
// classifies uncontained — never a false proof.
static int fd_is_non_privileged(int fd) {
    struct stat st;
    if (fstat(fd, &st) != 0) return 0;
    if (st.st_mode & (S_ISUID | S_ISGID)) return 0;

#ifdef __linux__
    char buf[1];
    ssize_t r = fgetxattr(fd, "security.capability", buf, sizeof(buf));
    if (r >= 0) return 0; // has SOME capability xattr payload → privileged, uncontained
    // ENODATA (no xattr) or ENOTSUP (fs can't carry xattrs) are the ONLY proofs of absence.
    return (errno == ENODATA || errno == ENOTSUP) ? 1 : 0;
#else
    // No fd-bound capability-xattr proof exists off Linux; never claim proof of
    // non-privilege on a platform that can't back it up (fail-closed to uncontained).
    return 0;
#endif
}

static int build_execfd_plan(int fd, char *const argv[], int contained, pty_exec_plan **out_plan) {
    pty_exec_plan *plan = calloc(1, sizeof(*plan));
    if (!plan) { close(fd); return -1; }
    plan->mode      = PTY_EXEC_FD;
    plan->exec_fd   = fd;
    plan->exec_path = NULL;
    plan->argv      = dup_argv(argv);
    plan->contained = contained;
    if (!plan->argv) { pty_exec_plan *p = plan; pty_plan_free(&p); return -1; }
    *out_plan = plan;
    return 0;
}

// Resolves `name` against `path_env` (colon-separated). Returns a malloc'd absolute path or
// NULL if not found. Sets *saw_relative_component whenever ANY field is empty (a leading/
// trailing ':' or an internal '::' — POSIX treats an empty field as the current directory) or
// relative (does not begin with '/'), so the caller can classify uncontained rather than risk
// resolving against the wrong cwd (spec §4.2 risk #4).
//
// Deliberately a MANUAL ':' scan, NOT strtok: strtok collapses adjacent/leading/trailing
// delimiters, so it silently HIDES exactly the empty (cwd) field we must detect — and it is
// non-reentrant (mutates a hidden static), unsafe on the daemon threadpool threads that call
// this library. The scan detects relative components across the WHOLE PATH even after an
// absolute match, since the kernel would still try the cwd component at runtime.
static char *resolve_in_absolute_path(const char *name, const char *path_env, int *saw_relative_component) {
    *saw_relative_component = 0;
    if (!path_env) return NULL;

    size_t name_len = strlen(name);
    char *result = NULL;
    const char *p = path_env;
    for (;;) {
        const char *colon = strchr(p, ':');
        size_t len = colon ? (size_t)(colon - p) : strlen(p);

        if (len == 0 || p[0] != '/') {
            // Empty field (cwd) or a relative dir → cwd-dependent resolution → uncontained.
            *saw_relative_component = 1;
        } else if (!result) {
            size_t need = len + 1 + name_len + 1;
            char *candidate = malloc(need);
            if (candidate) {
                memcpy(candidate, p, len);
                candidate[len] = '/';
                memcpy(candidate + len + 1, name, name_len + 1); // includes the NUL
                struct stat st;
                if (access(candidate, X_OK) == 0 && stat(candidate, &st) == 0 && S_ISREG(st.st_mode)) {
                    result = candidate;
                } else {
                    free(candidate);
                }
            }
        }

        if (!colon) break;
        p = colon + 1;
    }
    return result;
}

static const char *find_env(char *const envp[], const char *key) {
    size_t klen = strlen(key);
    for (int i = 0; envp && envp[i]; i++) {
        if (strncmp(envp[i], key, klen) == 0 && envp[i][klen] == '=') return envp[i] + klen + 1;
    }
    return NULL;
}

// Returns 1 if the open regular-file `fd` itself begins with a shebang ("#!"), else 0 (a short
// read or any read error counts as "not a shebang" — inability to read it back as a script must
// never be treated as a chain). Uses pread so the fd's offset is left untouched for the exec of
// this same fd on the contained path.
static int fd_starts_with_shebang(int fd) {
    char two[2];
    ssize_t r = pread(fd, two, 2, 0);
    return (r == 2 && two[0] == '#' && two[1] == '!') ? 1 : 0;
}

// Builds the plan for a `#!interp [optarg]` (direct) or `#!/usr/bin/env NAME [...]` shebang.
// `head`/`head_len` is the first-256-bytes sniff already read from the ORIGINAL file (script)
// by the caller; the script fd itself is never exec'd or kept (TOCTOU rule — see spec).
static int build_shebang_plan(
        const char *script_abs_path, const char *head, ssize_t head_len,
        char *const orig_argv[], char *const envp[], int execveat_supported, pty_exec_plan **out_plan) {
    // Parse the shebang line: "#!<rest>\n"
    const char *line_end = memchr(head, '\n', (size_t)head_len);
    size_t line_len = line_end ? (size_t)(line_end - head) : (size_t)head_len;
    char line[256] = {0};
    memcpy(line, head + 2, line_len > 2 ? line_len - 2 : 0);

    // Split into up to 2 tokens (interp path, one optional arg-blob).
    char *save = NULL;
    char *tok0 = strtok_r(line, " \t", &save);
    char *rest = save; // everything after the first token, NOT re-tokenized yet

    if (!tok0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); // malformed → uncontained

    // Only a LITERAL ABSOLUTE `/usr/bin/env` enters the env-rewrite path. A bare `#!env NAME` (or
    // any relative env path) is NOT equivalent to `#!/usr/bin/env NAME`: the kernel resolves `env`
    // itself against the CHILD's post-chdir cwd/PATH, which the parent-side preflight cannot
    // reproduce — so a non-absolute `env` falls through to the direct-shebang branch and is
    // rejected there by the absolute-interpreter guard.
    int is_env = strcmp(tok0, "/usr/bin/env") == 0;

    if (!is_env) {
        // A RELATIVE direct interpreter (`#!bin/interp`, or a bare `#!interp`) is resolved by the
        // kernel against the CHILD's post-chdir cwd — but we would open+preflight it HERE against
        // the DAEMON's cwd, a DIFFERENT inode (or a spurious hit/miss), then exec that wrong fd
        // while reporting it contained. Only a literal ABSOLUTE interpreter path can be preflighted
        // correctly pre-fork; anything else is uncontained and left for the kernel to resolve
        // natively from the original path after chdir.
        if (tok0[0] != '/') return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

        // Direct shebang: at most ONE optional arg is kept as-is; anything with more tokens
        // after that is "an unresolvable shebang" per spec → uncontained.
        char *tok1 = rest ? strtok_r(NULL, " \t", &save) : NULL;
        char *tok2 = rest ? strtok_r(NULL, " \t", &save) : NULL;
        if (tok2) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

        int fd = open(tok0, O_RDONLY | O_CLOEXEC);
        if (fd < 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

        // Two-level (or deeper) script chain: the resolved interpreter is ITSELF a script (it
        // carries its own shebang). Only a single script→native-interpreter level is containable
        // (spec §4.2 / §4.5) — a deeper chain would have the kernel re-resolve THAT script's
        // interpreter by pathname at runtime, which we do not preflight — so classify uncontained
        // and let the kernel resolve the whole chain natively from the original path.
        if (fd_starts_with_shebang(fd)) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }

        // Rewritten argv: [interp, optarg?, script_abs_path, orig_argv[1:]...]. Guard n == 0
        // (empty orig_argv) so the (n - 1) arithmetic can't under-size the array by a slot.
        int n = 0; while (orig_argv[n]) n++;
        if (n < 1) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }

        // Assemble a BORROWED (non-owning) NULL-terminated view, then let build_execfd_plan
        // deep-copy it with per-element NULL checks — no unchecked strdup/calloc on this path,
        // and any allocation failure funnels through to the uncontained EXEC_PATH fallback.
        int extra = tok1 ? 3 : 2; // interp (+ optarg) + script
        const char **view = calloc((size_t)(extra + (n - 1)) + 1, sizeof(char*));
        if (!view) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }
        int k = 0;
        view[k++] = tok0;
        if (tok1) view[k++] = tok1;
        view[k++] = script_abs_path;
        for (int i = 1; i < n; i++) view[k++] = orig_argv[i];
        view[k] = NULL;

        int contained = execveat_supported ? fd_is_non_privileged(fd) : 0;
        int rc = build_execfd_plan(fd, (char *const *)view, contained, out_plan);
        free(view);
        // build_execfd_plan owns fd on success AND failure (it closes fd on any error), so a
        // failed contained plan degrades to the uncontained EXEC_PATH plan (fd already closed).
        if (rc != 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
        return 0;
    }

    // `env [-S ...|VAR=val ...] NAME [args...]` — only the bare "env NAME" form (exactly one
    // token after `env`, no flags/assignments) is rewritten; anything richer → uncontained.
    char *name = rest ? strtok_r(NULL, " \t", &save) : NULL;
    char *extra_tok = name ? strtok_r(NULL, " \t", &save) : NULL;
    if (!name || extra_tok || name[0] == '-' || strchr(name, '=')) {
        return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    }

    const char *child_path = find_env(envp, "PATH");
    int saw_relative = 0;
    char *resolved = NULL;
    if (child_path) {
        resolved = resolve_in_absolute_path(name, child_path, &saw_relative);
    } else {
        // Unset child PATH → confstr(_CS_PATH) verbatim, at runtime, preserving its order.
        // A 0 return (unsupported/failed) or a malloc failure → can't resolve → uncontained.
        size_t need = confstr(_CS_PATH, NULL, 0);
        if (need == 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
        char *cs = malloc(need);
        if (!cs) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
        confstr(_CS_PATH, cs, need);
        resolved = resolve_in_absolute_path(name, cs, &saw_relative);
        free(cs);
    }

    if (saw_relative || !resolved) {
        // Empty/relative PATH component, or NAME simply not found in an absolute-only PATH →
        // uncontained either way (the kernel/env resolves it correctly at runtime; we just
        // forgo containment rather than risk preflighting the wrong inode). `resolved` can be
        // non-NULL here when an absolute component matched but an empty/relative SIBLING tripped
        // saw_relative — free it (free(NULL) is a no-op for the not-found case).
        free(resolved);
        return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    }

    int fd = open(resolved, O_RDONLY | O_CLOEXEC);
    free(resolved);
    if (fd < 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);

    // Same two-level-chain guard as the direct-shebang case: if the interpreter `env` resolved
    // is itself a script, the chain is deeper than one level → uncontained (kernel/env resolve
    // the whole chain natively from the original path).
    if (fd_starts_with_shebang(fd)) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }

    // Guard n == 0 (empty orig_argv) before the [name, script, orig_argv[1:]...] layout.
    int n = 0; while (orig_argv[n]) n++;
    if (n < 1) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }

    // Borrowed NULL-terminated view. argv[0] is the resolved interpreter name (env's target IS
    // the interpreter); the fd is what actually execs. build_execfd_plan deep-copies it with
    // per-element NULL checks — no unchecked strdup/calloc, allocation failure → uncontained.
    const char **view = calloc((size_t)(n + 2), sizeof(char*)); // [name, script, orig_argv[1:]..., NULL]
    if (!view) { close(fd); return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan); }
    int k = 0;
    view[k++] = name;
    view[k++] = script_abs_path;
    for (int i = 1; i < n; i++) view[k++] = orig_argv[i];
    view[k] = NULL;

    int contained = execveat_supported ? fd_is_non_privileged(fd) : 0;
    int rc = build_execfd_plan(fd, (char *const *)view, contained, out_plan);
    free(view);
    if (rc != 0) return build_execpath_plan(script_abs_path, orig_argv, 0, out_plan);
    return 0;
}

int pty_preflight(const char *exe_abs_path, char *const orig_argv[], char *const envp[],
                   int execveat_supported, pty_exec_plan **out_plan) {
    *out_plan = NULL;

    if (!execveat_supported) {
        return build_execpath_plan(exe_abs_path, orig_argv, 0, out_plan);
    }

    int fd = open(exe_abs_path, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        // EACCES/EPERM specifically: we can't READ the target to sniff a shebang or run the
        // fd-bound privilege preflight — but that does NOT prove it's un-executable. An
        // execute-only (e.g. mode 0111, no read bit) native ELF execs fine via execve on Linux
        // without being readable, so degrade to an uncontained EXEC_PATH plan (the kernel
        // resolves + execs the path natively; we simply forgo the fd-bound containment proof we
        // cannot obtain, per the spec §4.2(a) "open EACCES ... degrades to EXEC_PATH-uncontained"
        // rule). Any OTHER errno (ENOENT missing file, etc.) is a genuine "no plan can be built"
        // failure → -1, matching the EXEC_PATH fallback's own execve, which would fail identically.
        if (errno == EACCES || errno == EPERM) {
            return build_execpath_plan(exe_abs_path, orig_argv, 0, out_plan);
        }
        return -1;
    }

    char head[256];
    ssize_t n = read(fd, head, sizeof(head) - 1);
    if (n < 0) {
        close(fd);
        return build_execpath_plan(exe_abs_path, orig_argv, 0, out_plan); // degrade, don't fail
    }
    head[n] = '\0';

    if (n >= 2 && head[0] == '#' && head[1] == '!') {
        close(fd); // the SCRIPT fd is never exec'd — see the TOCTOU note in the header comment
        return build_shebang_plan(exe_abs_path, head, n, orig_argv, envp, execveat_supported, out_plan);
    }

    int contained = fd_is_non_privileged(fd);
    return build_execfd_plan(fd, orig_argv, contained, out_plan);
}

int pty_plan_contained(const pty_exec_plan *plan) {
    return plan ? plan->contained : 0;
}

void pty_plan_free(pty_exec_plan **plan) {
    if (!plan || !*plan) return;
    pty_exec_plan *p = *plan;
    if (p->mode == PTY_EXEC_FD && p->exec_fd >= 0) close(p->exec_fd);
    free(p->exec_path);
    free_argv(p->argv);
    free(p);
    *plan = NULL;
}

// ── L1-shim(b): spawn ────────────────────────────────────────────────────────────────────
//
// Native start-identity capture. Both helpers are called ONLY from the parent, immediately
// after forkpty() returns (the capture-binding rule, see pty_spawn below) — never from the
// child, and never after anything may have waitpid()'d the child.

#ifdef __linux__
static int capture_lx_identity(pid_t pid, char *out, size_t outlen) {
    char statpath[64];
    snprintf(statpath, sizeof(statpath), "/proc/%d/stat", (int)pid);
    int fd = open(statpath, O_RDONLY);
    if (fd < 0) return 0;

    char buf[512];
    ssize_t n = read(fd, buf, sizeof(buf) - 1);
    close(fd);
    if (n <= 0) return 0;
    buf[n] = '\0';

    // Fields after the (possibly space/paren-containing) comm begin after the LAST ')'.
    char *after = strrchr(buf, ')');
    if (!after || !after[1]) return 0;
    after += 2; // skip ") "

    char *tok, *save = NULL;
    int field = 0; // 0-indexed from "state" (field 3 overall == index 0 here)
    char *starttime = NULL;
    for (tok = strtok_r(after, " ", &save); tok; tok = strtok_r(NULL, " ", &save), field++) {
        if (field == 19) { starttime = tok; break; } // starttime is field 22 overall, index 19 here
    }
    if (!starttime) return 0;

    int bfd = open("/proc/sys/kernel/random/boot_id", O_RDONLY);
    char boot[64] = "?";
    if (bfd >= 0) {
        ssize_t bn = read(bfd, boot, sizeof(boot) - 1);
        close(bfd);
        if (bn > 0) { boot[bn] = '\0'; char *nl = strchr(boot, '\n'); if (nl) *nl = '\0'; }
    }

    snprintf(out, outlen, "lx:%s:%s", boot, starttime);
    return 1;
}
#endif

#ifdef __APPLE__
#include <sys/sysctl.h>
#include <libproc.h>

// PROC_PIDUNIQIDENTIFIERINFO (flavor 17) and its struct are #ifdef PRIVATE in the xnu source
// and ABSENT from the shipped SDK's sys/proc_info.h entirely on this toolchain (grepping the
// installed SDK header for "uniqidentifier"/"PIDUNIQIDENTIFIERINFO" finds nothing) —
// proc_pidinfo() itself IS public, only the flavor constant and struct are undeclared.
//
// The struct's TOTAL size is not a stable, documented ABI, and differs from every commonly
// cited historical layout: empirically probing THIS kernel's proc_pidinfo(pid, 17, buf, n)
// directly (varying the requested buffer size, then comparing getpid() vs a freshly forked
// child vs pid 1) shows (a) any requested size below 56 bytes is refused outright (returns 0,
// never a truncated fill) while 56+ all return exactly 56, and (b) the leading 16 bytes look
// like a per-process UUID, the next 8 bytes (offset 16) increment by exactly 1 from a live
// process to its immediately-forked child (a monotonic unique-id counter), and the following
// 8 bytes (offset 24) in the CHILD exactly equal the PARENT's offset-16 value — including the
// pid-1 sentinel case (uniqueid=1, puniqueid=0). That is strong confirmation of the
// historically-cited PREFIX layout (p_uuid[16], p_uniqueid, p_puniqueid) for the three fields
// that matter, even though the trailing reserved region's exact size varies by OS version (a
// hardcoded 40-byte total, as an earlier draft of this file assumed, made every call here
// return 0/uncapturable — the kernel refuses anything under its actual current size). Rather
// than assert an exact total size that has ALREADY been observed to vary, this reads only the
// fixed-offset prefix out of a buffer sized generously larger than any known/plausible total,
// so a future OS growing the reserved tail further keeps working without a code change.
// Verified on this dev box (macOS, arm64) only — flagging to the requester that the exact
// prefix offsets should be cross-checked against whatever OS version the release macOS
// runner actually uses, since this call degrades to "uncapturable" (never a false proof) but
// is not yet confirmed correct there.
#define PTY_PROC_PIDUNIQIDENTIFIERINFO 17
#define PTY_UNIQIDENTIFIERINFO_BUFSIZE 256 // comfortably larger than any known/plausible layout

struct pty_proc_uniqidentifierinfo_prefix {
    uint8_t  p_uuid[16];
    uint64_t p_uniqueid;
    uint64_t p_puniqueid;
    // The real kernel struct continues with additional reserved fields whose count/size vary
    // by OS version (see note above); we never read them, so their shape doesn't matter here.
};

// Spec §4.3: pin the vendored prefix's size/offset at COMPILE time. memcpy() below reads exactly
// these bytes at these offsets out of the kernel's buffer, so any ABI drift (a padding change, a
// field-order change) must fail the build here rather than silently mis-read the identity at
// runtime. p_uuid[16] + p_uniqueid(8) + p_puniqueid(8) = 32, with p_uniqueid at offset 16.
_Static_assert(sizeof(struct pty_proc_uniqidentifierinfo_prefix) == 32, "mac identity prefix ABI drift");
_Static_assert(offsetof(struct pty_proc_uniqidentifierinfo_prefix, p_uniqueid) == 16, "p_uniqueid offset drift");

int pty_capture_mac_identity(pid_t pid, char *out, size_t outlen) {
    unsigned char raw[PTY_UNIQIDENTIFIERINFO_BUFSIZE];
    int n = proc_pidinfo(pid, PTY_PROC_PIDUNIQIDENTIFIERINFO, 0, raw, sizeof(raw));
    if (n < (int)sizeof(struct pty_proc_uniqidentifierinfo_prefix)) return 0; // anomaly → uncapturable, never a false proof

    struct pty_proc_uniqidentifierinfo_prefix info;
    memcpy(&info, raw, sizeof(info)); // avoids any alignment/strict-aliasing assumption on `raw`
    if (info.p_uniqueid == 0) return 0;

    char uuid_str[64];
    size_t uuid_size = sizeof(uuid_str);
    if (sysctlbyname("kern.bootsessionuuid", uuid_str, &uuid_size, NULL, 0) != 0) return 0;

    snprintf(out, outlen, "mac:%s:%llu", uuid_str, (unsigned long long)info.p_uniqueid);
    return 1;
}
#endif

static int capture_start_identity(pid_t pid, char *out, size_t outlen) {
    out[0] = '\0';
#ifdef __linux__
    if (capture_lx_identity(pid, out, outlen)) return 1;
#elif defined(__APPLE__)
    if (pty_capture_mac_identity(pid, out, outlen)) return 1;
#endif
    return 0; // uncapturable — out stays "" (identity_unavailable), never a launch failure
}

// Async-signal-safe: write EXACTLY `len` bytes from `buf`, retrying across EINTR and partial
// writes, until the whole message is delivered or a non-EINTR error occurs. Returns 0 on full
// delivery, -1 otherwise. Only write() is used (no stdio/libc buffering), so it is safe on the
// fork→exec path. This closes the fail-OPEN hole a single unchecked write() left: if write()
// returned -1/EINTR (or a short count) before delivering the whole {step, err} record, the child
// would then die, the parent would read EOF, and EOF is interpreted as a SUCCESSFUL exec (a failed
// launch reported as a running agent). Retrying to full delivery makes the parent's
// "EOF => exec succeeded" assumption sound on every reachable child-failure path. Callers still
// _exit() non-zero afterwards regardless of the return value — the child must never exec after a
// deliberate abort, even if the pipe write itself cannot complete.
static int write_all_signal_safe(int fd, const void *buf, size_t len) {
    const char *p = (const char *)buf;
    size_t off = 0;
    while (off < len) {
        ssize_t w = write(fd, p + off, len - off);
        if (w < 0) {
            if (errno == EINTR) continue;
            return -1;             // non-EINTR error → give up (caller still fails closed)
        }
        if (w == 0) return -1;     // no progress → avoid an infinite spin
        off += (size_t)w;
    }
    return 0;
}

// Async-signal-safe: writes {step, err} to the error pipe and _exit(127)s. Never returns.
// A top-level function (not nested inside pty_spawn) — GCC/Clang nested functions require
// executable-stack trampolines and are NOT universally available (Apple clang rejects them
// without a non-default -fnested-functions flag the build does not pass), so this must not
// depend on that extension to compile with the project's plain `cc -shared` invocation.
static void child_fail_and_die(int errpipe_write_fd, int step, int err) {
    struct { int step; int err; } msg = { step, err };
    write_all_signal_safe(errpipe_write_fd, &msg, sizeof(msg)); // full-delivery retry; fail closed regardless
    _exit(127);
}

// Blocking waitpid retrying across EINTR — every cleanup path below must actually reap the
// child before returning (never leave a live-but-unobserved process behind), and a signal
// landing on the parent mid-wait must not be mistaken for "the child is gone".
static void reap_blocking(pid_t pid) {
    int status;
    while (waitpid(pid, &status, 0) < 0 && errno == EINTR) { /* retry */ }
}

int pty_spawn(const pty_exec_plan *plan, char *const envp[], const char *cwd,
              unsigned short rows, unsigned short cols,
              pid_t expected_parent, int cancel_fd, pty_spawn_result *out) {
#ifndef __linux__
    (void)expected_parent; // only re-checked post-PDEATHSIG on Linux — see the child sequence below
#endif
    memset(out, 0, sizeof(*out));
    out->master_fd = -1;

    // Self-pipe for child-failure reporting — BOTH ends CLOEXEC-flagged BEFORE the fork so a
    // successful exec closes the child's copy of the write end automatically (EOF => success).
    int errpipe[2];
#ifdef __linux__
    // pipe2(O_CLOEXEC) sets CLOEXEC ATOMICALLY at creation. The two-step pipe()+fcntl() leaves a
    // window in which a concurrent fork/exec on another daemon thread inherits errpipe[1]; that
    // stray copy of the write end keeps the pipe from reaching EOF on the child's exec, delaying
    // the exec-success signal until the 30s handshake deadline kills an otherwise-healthy agent.
    if (pipe2(errpipe, O_CLOEXEC) != 0) { out->err_no = errno; out->failed_step = PTY_STEP_FORK; return -1; }
#else
    // macOS/BSD have no pipe2; fall back to pipe() + explicit CLOEXEC on each end. A failed fcntl
    // means an fd could leak into a concurrently-forked child, so treat it as a spawn failure
    // (fail closed) rather than proceeding with an inheritable error pipe.
    if (pipe(errpipe) != 0) { out->err_no = errno; out->failed_step = PTY_STEP_FORK; return -1; }
    if (fcntl(errpipe[0], F_SETFD, FD_CLOEXEC) != 0 || fcntl(errpipe[1], F_SETFD, FD_CLOEXEC) != 0) {
        out->err_no = errno; out->failed_step = PTY_STEP_FORK;
        close(errpipe[0]); close(errpipe[1]);
        return -1;
    }
#endif

    struct winsize ws = {0};
    ws.ws_row = rows; ws.ws_col = cols;

    int master_fd;
    pid_t pid = forkpty(&master_fd, NULL, NULL, &ws);

    if (pid < 0) {
        out->err_no = errno; out->failed_step = PTY_STEP_FORK;
        close(errpipe[0]); close(errpipe[1]);
        return -1;
    }

    if (pid == 0) {
        // ── CHILD ── async-signal-safe calls only from here to exec (or _exit): no malloc,
        // no stdio, no non-reentrant libc — only the syscalls/functions on the POSIX
        // async-signal-safe list (close, write, _exit, chdir, execve, kill, getpid, prctl, getppid).
        close(errpipe[0]);
        // cancel_fd is caller-owned and only meaningful to the PARENT's poll loop; close our
        // inherited copy defensively (in case the caller didn't mark it CLOEXEC) rather than
        // leak an extra fd into whatever this plan execs into.
        if (cancel_fd >= 0) close(cancel_fd);

#ifdef __linux__
        if (prctl(PR_SET_PDEATHSIG, SIGKILL) != 0) {
            child_fail_and_die(errpipe[1], PTY_STEP_PRCTL, errno);
        }
        // Re-check parentage AFTER arming PDEATHSIG: if the real parent had already died
        // between fork() and this line, we'd have been reparented (getppid() !=
        // expected_parent) and PDEATHSIG for the NEW parent will never fire for the OLD
        // one's death — so self-kill explicitly rather than trust a signal that may never
        // come. errno is irrelevant here (not a syscall failure), so report err=0.
        if (getppid() != expected_parent) {
            struct { int step; int err; } msg = { PTY_STEP_PARENT_DIED, 0 };
            // Full-delivery retry (same fail-OPEN hole as child_fail_and_die): a silent single
            // write() that landed on EINTR would deliver nothing, the parent would read EOF and
            // mistake this self-kill for a successful exec. We self-kill regardless afterwards.
            write_all_signal_safe(errpipe[1], &msg, sizeof(msg));
            // kill(getpid(), SIGKILL) rather than raise(SIGKILL): raise() is NOT on the POSIX
            // async-signal-safe list, kill()/getpid() are. Same "die by signal" semantics.
            kill(getpid(), SIGKILL);
            _exit(127); // unreachable unless SIGKILL is somehow blocked
        }
#endif

        if (chdir(cwd) != 0) { child_fail_and_die(errpipe[1], PTY_STEP_CHDIR, errno); }

        if (plan->mode == PTY_EXEC_FD) {
#ifdef __linux__
            syscall(SYS_execveat, plan->exec_fd, "", plan->argv, envp, AT_EMPTY_PATH);
#else
            // No portable fd-exec primitive off Linux. pty_preflight should never hand back
            // an EXEC_FD plan here in practice (callers gate plan construction on
            // pty_probe_execveat(), which is unconditionally 0 off Linux — see pty_shim.h),
            // but fail deterministically rather than report a stale/garbage errno if it ever
            // does.
            errno = ENOSYS;
#endif
        } else {
            execve(plan->exec_path, plan->argv, envp);
        }

        child_fail_and_die(errpipe[1], PTY_STEP_EXEC, errno);
    }

    // ── PARENT ──
    // forkpty returns the master bare — unlike the error pipe above, it carries no CLOEXEC flag,
    // so without this every child the daemon execs later inherits a live read/write descriptor
    // onto THIS PTY agent's terminal, crossing an isolation boundary the daemon draws
    // deliberately. Set it as the first parent statement — the earliest a caller can, since
    // forkpty has no O_CLOEXEC-style atomic variant. That fully closes inheritance by any LATER
    // exec, but only NARROWS (cannot eliminate) the race against a child forked concurrently on
    // another daemon thread, whose fork can begin before forkpty even returns here; a hard
    // guarantee against concurrent spawns would require serializing all process creation across
    // the forkpty+F_SETFD pair, out of scope for this fix.
    // Best-effort, deliberately NOT fail-closed like the error-pipe CLOEXEC above, whose absence
    // is a genuine correctness hazard (a stray write-end copy defers the exec-success EOF). A
    // leaked master is a real isolation/resource cost but never a wrong result, and on a
    // just-returned valid fd this fcntl cannot realistically fail (EBADF/EINVAL both unreachable),
    // so tearing down an otherwise-healthy agent for it would be strictly worse than the residual
    // leak it guards against.
    fcntl(master_fd, F_SETFD, FD_CLOEXEC);
    close(errpipe[1]);

    // CAPTURE-BINDING RULE: identity is captured HERE, immediately after forkpty returns,
    // before anything (including the rest of this very function) waitpid()s the child. A
    // fast-exiting child cannot be reaped-and-replaced (its pid recycled onto an unrelated
    // process) before this line runs, so the token always describes THIS incarnation.
    capture_start_identity(pid, out->start_identity, sizeof(out->start_identity));

    // Bounded handshake: poll the error pipe (+ cancel_fd) against an absolute ~30s
    // deadline, computed via a monotonic clock so a signal that interrupts poll() (EINTR)
    // shrinks the remaining wait instead of resetting a fresh 30s window on every retry.
    struct timespec deadline;
    clock_gettime(CLOCK_MONOTONIC, &deadline);
    deadline.tv_sec += 30;

    struct pollfd fds[2];
    fds[0].fd = errpipe[0]; fds[0].events = POLLIN;
    int nfds = 1;
    if (cancel_fd >= 0) { fds[1].fd = cancel_fd; fds[1].events = POLLIN; nfds = 2; }

    int poll_rc;
    int poll_errno = 0;
    for (;;) {
        struct timespec now;
        clock_gettime(CLOCK_MONOTONIC, &now);
        long remaining_ms = (long)(deadline.tv_sec - now.tv_sec) * 1000
                          + (deadline.tv_nsec - now.tv_nsec) / 1000000;
        if (remaining_ms < 0) remaining_ms = 0;

        poll_rc = poll(fds, (nfds_t)nfds, (int)remaining_ms);
        if (poll_rc >= 0) break;
        if (errno == EINTR) continue;
        poll_errno = errno; // capture the real poll() failure before any later call clobbers errno
        break; // some other poll() failure — treated like a timeout below, not left hanging
    }

    if (poll_rc <= 0) {
        // Deadline reached (poll_rc == 0, poll_errno stays 0), or an unrecoverable poll() error
        // (poll_rc < 0, poll_errno carries the diagnostic errno): either way we can no longer
        // trust the handshake, so kill + reap and report a bounded failure rather than
        // block indefinitely or return success for an unobserved child.
        kill(pid, SIGKILL);
        reap_blocking(pid);
        close(errpipe[0]);
        close(master_fd);
        out->err_no = poll_errno; // 0 on a pure timeout; poll()'s errno on a poll() error
        out->failed_step = PTY_STEP_HANDSHAKE_TIMEOUT;
        return -1;
    }

    if (nfds == 2 && (fds[1].revents & (POLLIN | POLLHUP | POLLERR))) {
        // Shutdown cancellation — either a byte was written or the write end was closed.
        kill(pid, SIGKILL);
        reap_blocking(pid);
        close(errpipe[0]);
        close(master_fd);
        out->failed_step = PTY_STEP_CANCELLED;
        return -1;
    }

    struct { int step; int err; } msg;
    ssize_t n;
    for (;;) {
        n = read(errpipe[0], &msg, sizeof(msg));
        if (n >= 0 || errno != EINTR) break;
    }
    int read_errno = errno;
    close(errpipe[0]);

    if (n < 0) {
        // Unexpected read() failure (not EINTR) — the outcome is indeterminate; fail closed
        // rather than assume success, and still guarantee no live-but-unobserved child.
        kill(pid, SIGKILL);
        reap_blocking(pid);
        close(master_fd);
        out->err_no = read_errno;
        out->failed_step = PTY_STEP_HANDSHAKE_TIMEOUT;
        return -1;
    }

    if (n == sizeof(msg)) {
        // Child reported a failure at a shim-controlled step.
        reap_blocking(pid);
        close(master_fd);
        out->err_no = msg.err;
        out->failed_step = msg.step;
        return -1;
    }

    if (n != 0) {
        // A partial message — should be impossible (writes this small are atomic on a pipe
        // per POSIX, well under PIPE_BUF), but fail closed rather than guess at intent.
        kill(pid, SIGKILL);
        reap_blocking(pid);
        close(master_fd);
        out->failed_step = PTY_STEP_HANDSHAKE_TIMEOUT;
        return -1;
    }

    // EOF (n == 0): the exec replaced the image, closing the CLOEXEC write end. Success.
    out->pid = pid;
    out->master_fd = master_fd;
    out->failed_step = PTY_STEP_NONE;
    return 0;
}
