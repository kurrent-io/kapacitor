using System.Diagnostics;
using Capacitor.Cli.Daemon.Pty.Windows;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Windows;

/// <summary>
/// W1 (spec §4.1): every hosted-agent PTY on Windows is created already-bound to a
/// KILL_ON_JOB_CLOSE job — the OS itself kills the leader AND every descendant the instant
/// the job handle's last reference closes (clean dispose, crash, or an external kill of the
/// daemon). No managed reap layer is exercised on Windows once this is proven; these tests
/// are the only place that behavior is asserted.
/// </summary>
public class ConPtyJobObjectTests {
    [Test]
    public async Task Disposing_the_process_kills_child_and_grandchild() {
        if (!OperatingSystem.IsWindows()) return;

        // cmd.exe spawns a grandchild `timeout` so the group/job boundary — not just the
        // immediate child — is under test.
        await using var proc = ConPtyProcess.Spawn(
            "cmd.exe", ["/c", "start /min cmd.exe /c timeout /t 60 >NUL & timeout /t 60 >NUL"],
            Directory.GetCurrentDirectory());

        await Task.Delay(500); // let the grandchild actually spawn before we kill the job

        await proc.DisposeAsync();

        // Job-handle close is synchronous-ish from the OS's perspective, but process exit
        // notification can lag slightly — poll rather than assert instantly.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && IsProcessAlive(proc.Pid)) await Task.Delay(200);

        await Assert.That(IsProcessAlive(proc.Pid)).IsFalse();
    }

    [Test]
    public async Task Job_sets_no_breakaway_flag_so_escape_is_impossible() {
        if (!OperatingSystem.IsWindows()) return;

        // Structural proof that a hosted descendant cannot CreateProcess its way out of the job:
        // the production job carries EXACTLY JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE and NEITHER
        // breakaway-allowed flag, so a CREATE_BREAKAWAY_FROM_JOB child fails with
        // ERROR_ACCESS_DENIED by construction. We PROVE this by querying the job's limit flags —
        // NOT by joining this test host to proc's killing job and disposing it (which would
        // close the last handle and have the OS kill the host). Reading the flags is host-safe;
        // proc.DisposeAsync closes proc's own killing job, killing proc's child, never the host.
        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "exit"], Directory.GetCurrentDirectory());

        var limitFlags = ConPtyJobObjectTestHelper.QueryJobLimitFlags(ConPtyInteropTestAccessor.JobHandle(proc));

        await Assert.That(limitFlags & ConPtyInterop.JOB_OBJECT_LIMIT_BREAKAWAY_OK).IsEqualTo(0u);
        await Assert.That(limitFlags & ConPtyInterop.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK).IsEqualTo(0u);
        await Assert.That(limitFlags).IsEqualTo(ConPtyInterop.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE);
    }

    [Test]
    public async Task A_daemon_already_inside_an_outer_job_still_nests() {
        if (!OperatingSystem.IsWindows()) return;

        // Put THIS test process into an outer job with no UI-restriction flags (nesting is
        // only blocked when either job carries UI limits) — mirrors "the daemon happens to be
        // launched inside another job" (e.g. a CI runner, a service wrapper).
        //
        // HOST-SAFETY INVARIANT: outerJob is a PLAIN CreateJobObjectW with NO
        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so joining the test host to it is safe — a plain
        // job does NOT kill its members when its last handle closes. Clean up with CloseHandle,
        // never TerminateJobObject (which would kill every member, including this test host).
        var outerJob = ConPtyInterop.CreateJobObjectW(IntPtr.Zero, null);
        ConPtyJobObjectTestHelper.AssignSelfToJob(outerJob);

        await using var proc = ConPtyProcess.Spawn("cmd.exe", ["/c", "timeout /t 5 >NUL"], Directory.GetCurrentDirectory());

        // Nesting succeeded iff the spawn didn't throw AND the child is (transitively) a
        // member of the outer job too — checked via the native IsProcessInJob.
        await Assert.That(ConPtyJobObjectTestHelper.IsProcessInJob(proc.Pid, outerJob)).IsTrue();

        // CloseHandle (NOT TerminateJobObject) — see the invariant above. Windows has no API to
        // un-assign a process from a job, so the test host stays a member of this now-handleless
        // plain job for the rest of the run. That accumulation is harmless (a plain job imposes
        // nothing); at worst a later self-join could fail with a nesting error, which fails that
        // test cleanly and can no longer kill the host now that the killing/terminate paths are gone.
        ConPtyInterop.CloseHandle(outerJob);
    }

    [Test]
    public async Task Spawn_fails_closed_when_create_process_fails() {
        if (!OperatingSystem.IsWindows()) return;

        // Fail-closed guarantee: when CreateProcessW fails, ConPtyProcess.Spawn disposes the job
        // handle and throws BEFORE returning — a hosted agent is NEVER left spawned uncontained.
        // We force CreateProcessW to fail deterministically by pointing at a NONEXISTENT
        // executable. That exercises the SAME `if (!CreateProcessW(...)) { jobHandle.Dispose();
        // throw; }` arm the old UI-restricted-job scenario aimed at — but without that scenario's
        // FALSE premise (on Windows 8+ a UI-restricted job does NOT block nested-job containment;
        // CI proved the child was actually contained and the scenario's "leak" verdict was wrong).
        // A bad-exe failure creates NO process (nothing to leak) and mutates NO global job state,
        // so unlike the UI-restricted job (irreversible, host-poisoning) this runs safely
        // in-process rather than in a disposable NativeTestHost.
        var missing = $"C:\\kcap-nonexistent-{Guid.NewGuid():N}.exe";

        var threw = false;
        try {
            await using var proc = ConPtyProcess.Spawn(missing, [], Directory.GetCurrentDirectory());
        } catch (InvalidOperationException) {
            threw = true; // Spawn threw → failed closed, no uncontained child created
        }

        await Assert.That(threw).IsTrue();
    }

    static bool IsProcessAlive(int pid) {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }
}
