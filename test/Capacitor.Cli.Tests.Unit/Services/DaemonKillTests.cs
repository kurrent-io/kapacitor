using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary><see cref="DaemonKill.KillValidatedOwner"/> makes REAL, unmocked OS calls (no
/// injectable seams by design — see its doc comment) — kept focused per the task brief: the
/// process-TREE kill path (multiple children, orphaned grandchildren) gets real coverage
/// elsewhere; this covers the already-dead short-circuit and the gone-check's use of
/// <see cref="DaemonPidProbe"/> against a single live process.</summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonKillTests {
    [Test]
    public async Task Already_dead_pid_reports_gone_via_the_ArgumentException_path() {
        if (OperatingSystem.IsWindows()) return; // POSIX process spawn below

        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        try {
            using var proc = Process.Start(new ProcessStartInfo("/usr/bin/env", ["true"]) { UseShellExecute = false });
            await Assert.That(proc).IsNotNull();
            proc!.WaitForExit();
            var deadPid = proc.Id;

            // Process.GetProcessById(deadPid) throws ArgumentException inside KillValidatedOwner —
            // the short-circuit "already dead" arm, distinct from the gone-check below.
            var gone = DaemonKill.KillValidatedOwner("dead-owner", deadPid, TimeSpan.FromSeconds(1));

            await Assert.That(gone).IsTrue();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Killing_a_live_process_is_confirmed_gone_via_DaemonPidProbe() {
        if (OperatingSystem.IsWindows()) return; // POSIX process spawn below

        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        Process? proc = null;
        try {
            proc = Process.Start(new ProcessStartInfo("/usr/bin/env", ["sleep", "30"]) { UseShellExecute = false });
            await Assert.That(proc).IsNotNull();

            // Real start token for THIS pid, same technique as DaemonStopSelfPidTests: makes the
            // probe treat the spawned process as "our daemon" before the kill, so ValidatedPid
            // resolving to null afterward is a genuine before/after signal, not a vacuous no-op.
            var token = ProcessStartToken.ForPid(proc!.Id);
            await File.WriteAllTextAsync(DaemonLockPaths.PidPath("live-owner"), $"{proc.Id}\n{token}\n");

            await Assert.That(DaemonPidProbe.ValidatedPid("live-owner")).IsEqualTo(proc.Id);

            var gone = DaemonKill.KillValidatedOwner("live-owner", proc.Id, TimeSpan.FromSeconds(5));

            await Assert.That(gone).IsTrue();
            await Assert.That(proc.HasExited).IsTrue();
            await Assert.That(DaemonPidProbe.ValidatedPid("live-owner")).IsNull();
        } finally {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            proc?.Dispose();
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
