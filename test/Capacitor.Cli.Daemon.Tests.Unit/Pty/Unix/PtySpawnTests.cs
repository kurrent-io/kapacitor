using Capacitor.Cli.Daemon.Pty.Unix;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>
/// L1-shim(b) (spec §4.2(a)): pty_spawn — the actual fork/exec, run directly via P/Invoke
/// (bypassing UnixPtyProcess/the spawner thread, which Task 4/5 layer on top). These tests
/// exercise the raw native contract in isolation.
/// </summary>
/// <remarks>
/// Bare <c>[NotInParallel]</c>, for the same reason Console needs one: child reaping is process-global.
/// These tests waitpid a pid pty_spawn has already reaped, so the number is free for the OS to
/// reassign — and a concurrent spawn landing on it turns the wait into a wait on someone else's child.
/// Winning that race is worse than losing it: System.Diagnostics.Process owns most of the others, and
/// stealing one leaves .NET's SIGCHLD handler with ECHILD, which it answers by FailFast-ing the host.
/// The whole suite then dies mid-run with no failing assertion to point at. A group key is not enough.
/// </remarks>
[NotInParallel]
public class PtySpawnTests {
    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task Successful_spawn_returns_a_reapable_child_and_a_captured_identity() {
        var plan = Preflight("/bin/sleep", ["sleep", "5"]);
        try {
            var rc = Spawn(plan, out var result);
            try {
                await Assert.That(rc).IsEqualTo(0);
                await Assert.That(result.Pid).IsGreaterThan(0);
                await Assert.That(result.FailedStep).IsEqualTo(0);
                // The capture-binding rule: identity is non-empty for a healthy spawn on both
                // platforms (barring a genuine private-ABI anomaly, covered separately).
                await Assert.That(result.StartIdentityString).IsNotEmpty();
                await Assert.That(result.StartIdentityString).StartsWith(OperatingSystem.IsLinux() ? "lx:" : "mac:");
            } finally {
                UnixPtyInterop.kill(result.Pid, UnixPtyInterop.SIGKILL);
                UnixPtyInterop.waitpid(result.Pid, out _, 0);
            }
        } finally { Free(plan); }
    }

    [Test, RunOn(OS.Linux)]
    public async Task Missing_original_path_fails_at_preflight_no_child_forked() {
        var rc = UnixPtyInterop.pty_preflight("/no/such/binary-" + Guid.NewGuid(), ["x", null], EmptyEnvp(), 1, out var plan);
        await Assert.That(rc).IsEqualTo(-1);
        await Assert.That(plan).IsEqualTo(IntPtr.Zero);
        // No pty_spawn call at all — this IS the assertion (a preflight failure never reaches spawn).
    }

    [Test, RunOn(OS.Linux)]
    public async Task Child_side_exec_failure_reports_failed_step_exec_and_reaps_cleanly() {
        // Build a valid EXEC_PATH plan, then remove the file between preflight and spawn so
        // the FORK succeeds but the exec fails inside the child. Force EXEC_PATH explicitly:
        // an EXEC_FD plan holds an open fd to the (still-linked) inode, so deleting the path
        // would NOT make the fd-based exec fail — only a path-based re-resolution at exec
        // time observes the deletion.
        using var tmp  = new TempDir();
        var       path = tmp.ExecuteOnlyCopyOf("/bin/true");
        var       plan = Preflight(path, [path], execveatSupported: 0);
        File.Delete(path); // the test's own action, not cleanup: exec must observe the missing path
        try {
            var rc = Spawn(plan, out var result);
            await Assert.That(rc).IsEqualTo(-1);
            await Assert.That(result.FailedStep).IsEqualTo(5 /* PTY_STEP_EXEC */);
            await Assert.That(result.ErrNo).IsEqualTo(2 /* ENOENT */);
            // No zombie/phantom: waitpid on the reported pid must fail with ECHILD (already reaped by pty_spawn).
            var wpRc = UnixPtyInterop.waitpid(result.Pid, out _, 0);
            await Assert.That(wpRc).IsEqualTo(-1);
        } finally { Free(plan); }
    }

    [Test, RunOn(OS.Linux)]
    public async Task Bad_cwd_reports_failed_step_chdir() {
        var plan = Preflight("/bin/true", ["true"]);
        try {
            var rc = Spawn(plan, out var result, cwd: "/no/such/directory-" + Guid.NewGuid());
            await Assert.That(rc).IsEqualTo(-1);
            await Assert.That(result.FailedStep).IsEqualTo(4 /* PTY_STEP_CHDIR */);
        } finally { Free(plan); }
    }

    [Test, RunOn(OS.Linux)]
    public async Task Getppid_mismatch_self_kills_and_reports_parent_died() {
        // Passing a deliberately WRONG expected_parent simulates "the real daemon died and I was
        // reparented" without actually killing anything — the child must self-kill and the
        // parent must see failed_step=parent_died, NEVER a false success.
        var plan = Preflight("/bin/sleep", ["sleep", "5"]);
        try {
            var rc = Spawn(plan, out var result, expectedParent: 1 /* init — never our real parent */);
            await Assert.That(rc).IsEqualTo(-1);
            await Assert.That(result.FailedStep).IsEqualTo(3 /* PTY_STEP_PARENT_DIED */);
        } finally { Free(plan); }
    }

    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task Cancel_fd_during_handshake_kills_and_reaps_returns_cancelled() {
        // A readable cancel_fd during the handshake must deterministically win over a child that
        // would otherwise exec successfully: pty_spawn polls {errpipe, cancel_fd} and MUST take
        // the cancel arm (kill + reap the child, return -1 / PTY_STEP_CANCELLED) rather than read
        // the errpipe's exec-EOF as success.
        //
        // The cancel byte is written BEFORE the spawn, not after a Delay: a real child reaches
        // its exec-EOF in ~1ms, far sooner than any post-spawn Delay could fire, so a delayed
        // write always loses the race to exec-success (verified on macOS — that timing is exactly
        // why the earlier "SIGSTOP after exec" wrapper reported success instead of cancellation:
        // the shell exec'd and the CLOEXEC errpipe reached EOF BEFORE the SIGSTOP or the delayed
        // cancel ran). Pre-arming cancel_fd is the deterministic form of "shutdown cancels an
        // in-flight handshake": the byte is pending for the whole handshake window, so the poll
        // reports it and the cancel arm fires regardless of scheduling. pty_spawn kills + reaps
        // the child on this path, so no process leaks (result.Pid stays 0 on the cancel arm).
        var plan = Preflight("/bin/sleep", ["sleep", "5"]);
        var (cancelRead, cancelWrite) = MakePipe();
        UnixPtyInterop.write(cancelWrite, [1], 1); // arm cancellation before the handshake polls
        try {
            var rc = Spawn(plan, out var result, cancelFd: cancelRead);
            await Assert.That(rc).IsEqualTo(-1);
            await Assert.That(result.FailedStep).IsEqualTo(7 /* PTY_STEP_CANCELLED */);
        } finally {
            Free(plan);
            UnixPtyInterop.close(cancelRead);
            UnixPtyInterop.close(cancelWrite);
        }
    }

    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task Capture_binding_a_fast_exiting_child_never_yields_a_recycled_identity() {
        // Spawn something that exits IMMEDIATELY (`sleep 0` — used instead of /bin/true so
        // this runs identically on both platforms: this environment's macOS has no /bin/true
        // at all, only /usr/bin/true, while /bin/sleep is present on both). The captured
        // identity must describe the ORIGINAL incarnation. This is exactly what the
        // capture-binding rule (capture pre-reap, inside pty_spawn) is supposed to guarantee:
        // nothing has waited on the child before pty_spawn captured its identity, so a
        // well-formed, non-empty token always comes back even though the child may have
        // already exited by the time we read it back out.
        var plan = Preflight("/bin/sleep", ["sleep", "0"]);
        try {
            var rc = Spawn(plan, out var result);
            await Assert.That(rc).IsEqualTo(0);
            await Assert.That(result.StartIdentityString).IsNotEmpty();
            UnixPtyInterop.waitpid(result.Pid, out _, 0); // reap the exited child
        } finally { Free(plan); }
    }

    [Test, RunOn(OS.Linux | OS.MacOs)]
    public async Task Successful_spawn_marks_the_pty_master_fd_close_on_exec() {
        // Regression (spec §3.0a): forkpty returns the master bare, so without an explicit
        // FD_CLOEXEC on it every child the daemon spawns afterwards inherits a live read/write
        // descriptor onto this PTY agent's terminal — crossing an isolation boundary the daemon
        // draws deliberately. The master must therefore be close-on-exec, exactly as the error
        // pipe already is.
        var plan = Preflight("/bin/sleep", ["sleep", "5"]);
        try {
            var rc = Spawn(plan, out var result);
            try {
                await Assert.That(rc).IsEqualTo(0);
                await Assert.That(result.MasterFd).IsGreaterThanOrEqualTo(0);
                var flags = fcntl(result.MasterFd, F_GETFD, 0);
                await Assert.That(flags).IsGreaterThanOrEqualTo(0); // fcntl itself succeeded
                await Assert.That(flags & FD_CLOEXEC).IsEqualTo(FD_CLOEXEC);
            } finally {
                // Cleanup is GUARDED, not gated: it always runs after Spawn returns, so a child
                // created before a failing assertion is still reaped — but each op is conditioned
                // on a valid sentinel, since pty_spawn zero-fills result on failure (Pid 0,
                // MasterFd -1) and kill(0, SIGKILL) would signal the whole process group.
                if (result.MasterFd >= 0) UnixPtyInterop.close(result.MasterFd);
                if (result.Pid > 0) {
                    UnixPtyInterop.kill(result.Pid, UnixPtyInterop.SIGKILL);
                    UnixPtyInterop.waitpid(result.Pid, out _, 0);
                }
            }
        } finally { Free(plan); }
    }

    const int F_GETFD    = 1;
    const int FD_CLOEXEC = 1;
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    static extern int fcntl(int fd, int cmd, int arg);

    // See PtyShimNativeTests for why these NULL-termination helpers exist: argv/envp cross
    // into the shim as a bare `char* const[]` with no length prefix (mirrors execve), so the
    // native walk to the NULL sentinel reads out of bounds unless every array handed across
    // the P/Invoke boundary carries a trailing NULL element.
    static string?[] EmptyEnvp() => [null];
    static string?[] NullTerm(string?[] a) => a.Length > 0 && a[^1] is null ? a : [.. a, null];

    static IntPtr Preflight(string exe, string?[] argv, int? execveatSupported = null) {
        // Mirrors real callers: probe once and pass the result through, rather than hardcoding
        // 1. On macOS pty_probe_execveat() always reports 0, which forces every plan built here
        // to EXEC_PATH — matching pty_spawn's exec step, which has no fd-exec primitive off
        // Linux. Hardcoding 1 here would build an EXEC_FD plan on macOS that pty_spawn's exec
        // step can never actually run, failing every "successful spawn" test on that platform.
        var supported = execveatSupported ?? UnixPtyInterop.pty_probe_execveat();
        var rc = UnixPtyInterop.pty_preflight(exe, NullTerm(argv), EmptyEnvp(), supported, out var plan);
        if (rc != 0) throw new InvalidOperationException($"preflight failed for {exe}");
        return plan;
    }

    static int Spawn(IntPtr plan, out UnixPtyInterop.PtySpawnResult result, string? cwd = null,
            int expectedParent = -1, int cancelFd = -1) {
        var expected = expectedParent == -1 ? Environment.ProcessId : expectedParent;
        return UnixPtyInterop.pty_spawn(plan, EmptyEnvp(), cwd ?? Directory.GetCurrentDirectory(), 40, 120, expected, cancelFd, out result);
    }

    static void Free(IntPtr plan) { var p = plan; UnixPtyInterop.pty_plan_free(ref p); }

    static (int read, int write) MakePipe() {
        var fds = new int[2];
        UnixPtyInterop.pipe(fds);
        return (fds[0], fds[1]);
    }
}
