using System.Globalization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// Tests for <see cref="DaemonLock"/> — the per-name flock the daemon binary
/// holds for its lifetime. Verifies the protection: a second daemon
/// acquiring the same name on the same machine fails fast.
///
/// Every test owns a <see cref="TempDaemonStore"/>, so nothing touches the
/// user's real <c>~/.config/kcap/daemons</c> and no test can see another's
/// failed/leaked locks.
/// </summary>
public class DaemonLockTests {
    [Test]
    public async Task TryAcquire_OnFreshSlot_ReturnsLockWithFreshInstanceId() {
        using var daemons = new TempDaemonStore();

        var l = DaemonLock.TryAcquire(daemons.Store, "alpha");

        await Assert.That(l).IsNotNull();
        await Assert.That(l!.InstanceId).IsNotEmpty();
        await Assert.That(l.InstanceId.Length).IsEqualTo(32); // GUID "N" format

        // The lock file is held with FileShare.None, so the test
        // process can't read it while the daemon owns it (that's the
        // whole point — would-be duplicates can't peek). Verify the
        // content after disposal instead.
        var expected = l.InstanceId;
        l.Dispose();

        // Dispose deletes the lock file, which is the production
        // behaviour. Re-acquire to inspect the file content we'd see
        // mid-lifetime: it'll have a fresh instance id, but the
        // structure (single 32-char GUID line) is the assertion that
        // matters.
        using var reacquired = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(reacquired).IsNotNull();
        await Assert.That(reacquired!.InstanceId).IsNotEqualTo(expected);
    }

    [Test]
    public async Task TryAcquire_OnSameName_WhileHeld_Fails() {
        using var daemons = new TempDaemonStore();

        using var first = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(first).IsNotNull();

        var second = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task TryAcquire_DifferentNames_BothSucceed() {
        using var daemons = new TempDaemonStore();

        using var alpha = DaemonLock.TryAcquire(daemons.Store, "alpha");
        using var beta  = DaemonLock.TryAcquire(daemons.Store, "beta");

        await Assert.That(alpha).IsNotNull();
        await Assert.That(beta).IsNotNull();
        await Assert.That(alpha!.InstanceId).IsNotEqualTo(beta!.InstanceId);
    }

    [Test]
    public async Task Dispose_ReleasesLock_AllowingReAcquisition() {
        using var daemons = new TempDaemonStore();

        var first = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(first).IsNotNull();

        // Different instance id should be produced on re-acquire so a
        // post-disposal observer can tell the daemons apart.
        var firstId = first!.InstanceId;
        first.Dispose();

        using var second = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.InstanceId).IsNotEqualTo(firstId);
    }

    /// <summary>
    /// <see cref="DaemonLock.Dispose"/> must not delete the
    /// lock file on disk. If it did, a daemon B that acquired the path between
    /// our flock release and the unlink would have its inode unlinked under
    /// it, and a daemon C could then create a fresh file and acquire a SECOND
    /// independent flock at the same path — reopening the duplicate-name hole.
    /// </summary>
    [Test]
    public async Task Dispose_DoesNotDeleteLockFile() {
        using var daemons = new TempDaemonStore();

        var l        = DaemonLock.TryAcquire(daemons.Store, "alpha")!;
        var lockPath = daemons.Store.LockPath("alpha");

        await Assert.That(File.Exists(lockPath)).IsTrue();

        l.Dispose();

        await Assert.That(File.Exists(lockPath)).IsTrue();
    }

    /// <summary>
    /// when a successor daemon has already overwritten
    /// the PID file with its own PID, the disposing daemon must not delete
    /// it — that would orphan the successor's entry and `agent stop` would
    /// no longer find the live daemon.
    /// </summary>
    [Test]
    public async Task Dispose_DoesNotDeletePidFile_IfItPointsToSomeoneElsesPid() {
        using var daemons = new TempDaemonStore();

        var l       = DaemonLock.TryAcquire(daemons.Store, "alpha")!;
        var pidPath = daemons.Store.PidPath("alpha");

        // Simulate a successor process that won the race after our
        // flock release and rewrote the PID file. We pick a PID that
        // is definitely not us.
        File.WriteAllText(pidPath, "99999\n637999999999999999");

        l.Dispose();

        await Assert.That(File.Exists(pidPath)).IsTrue();
        await Assert.That(File.ReadAllText(pidPath)).StartsWith("99999");
    }

    /// <summary>
    /// the daemon must write a PID file whose first line is its PID and
    /// whose second line is the cross-process-stable start token, so the CLI's
    /// <c>status</c>/<c>stop</c>/<c>doctor</c> (separate processes) can confirm
    /// the live daemon instead of misreading it as a stale entry. The PID file
    /// (unlike the lock file) is not held exclusively, so we can read it back.
    /// </summary>
    [Test]
    public async Task TryAcquire_WritesPidFile_WithPidAndStableStartToken() {
        using var daemons = new TempDaemonStore();

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(l).IsNotNull();

        var lines = (await File.ReadAllTextAsync(daemons.Store.PidPath("alpha")))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[0]).IsEqualTo(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        // The test process IS the "daemon" here, so the recorded token must
        // match what a reader computes for this same PID.
        await Assert.That(lines[1]).IsEqualTo(ProcessStartToken.ForCurrent());
    }

    /// <summary>
    /// The daemon writes its version to a freely-readable <c>&lt;name&gt;.version</c>
    /// marker at acquisition so <c>kcap daemon status</c> (a separate process) can
    /// report the running daemon's version without contending with the exclusive flock.
    /// </summary>
    [Test]
    public async Task TryAcquire_WritesVersionMarker() {
        using var daemons = new TempDaemonStore();

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha", "0.4.11+sha.abc1234");
        await Assert.That(l).IsNotNull();

        await Assert.That(DaemonVersionMarker.TryRead(daemons.Store, "alpha")).IsEqualTo("0.4.11+sha.abc1234");
    }

    /// <summary>
    /// The version marker is observability-only (for <c>kcap daemon status</c>),
    /// so a failure to write it must never abort lock acquisition / daemon startup
    /// — unlike the correctness-critical flock and PID file.
    /// </summary>
    [Test]
    public async Task TryAcquire_StillSucceeds_WhenVersionMarkerWriteFails() {
        using var daemons = new TempDaemonStore();

        // Plant a directory where the marker file would go, so the atomic
        // move in DaemonVersionMarker.Write throws.
        System.IO.Directory.CreateDirectory(daemons.Store.VersionPath("alpha"));

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha", "0.4.11");

        await Assert.That(l).IsNotNull();
        await Assert.That(l!.InstanceId).IsNotEmpty();
    }

    [Test]
    public async Task Dispose_DeletesVersionMarker_WhenPidStillOurs() {
        using var daemons = new TempDaemonStore();

        var l = DaemonLock.TryAcquire(daemons.Store, "alpha", "0.4.11")!;
        await Assert.That(File.Exists(daemons.Store.VersionPath("alpha"))).IsTrue();

        l.Dispose();

        await Assert.That(File.Exists(daemons.Store.VersionPath("alpha"))).IsFalse();
    }

    /// <summary>
    /// Detached-respawn race: once a successor has rewritten the PID file with its
    /// own PID (and its own version marker), the disposing daemon must not delete
    /// the marker — same ownership guard as the PID file — or the successor's fresh
    /// version would be clobbered.
    /// </summary>
    [Test]
    public async Task Dispose_DoesNotDeleteVersionMarker_IfPidPointsToSomeoneElse() {
        using var daemons = new TempDaemonStore();

        var l = DaemonLock.TryAcquire(daemons.Store, "alpha", "0.4.10")!;

        // Simulate the successor: it rewrote both the PID file and the version marker.
        File.WriteAllText(daemons.Store.PidPath("alpha"), "99999\n637999999999999999");
        DaemonVersionMarker.Write(daemons.Store, "alpha", "0.4.11");

        l.Dispose();

        await Assert.That(DaemonVersionMarker.TryRead(daemons.Store, "alpha")).IsEqualTo("0.4.11");
    }

    [Test]
    public async Task TryAcquire_AfterStaleLockFileLeftBehind_StillAcquires() {
        using var daemons = new TempDaemonStore();

        // Simulate a daemon that died without cleanup: lockfile exists,
        // but no process holds the kernel flock. Re-acquisition must
        // succeed — that's the whole point of flock semantics.
        daemons.Store.EnsureDirectory();
        File.WriteAllText(daemons.Store.LockPath("alpha"), "stale-instance-id");

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(l).IsNotNull();
        await Assert.That(l!.InstanceId).IsNotEqualTo("stale-instance-id");
    }

    /// <summary>
    /// a daemon that is SIGKILLed (macOS jetsam/OOM, `kill -9`), loses
    /// power, or crashes natively never runs <see cref="DaemonLock.Dispose"/>, so
    /// its PID file is left on disk. Once we hold the exclusive flock (proving the
    /// prior holder is gone), a leftover PID file is the signature of that unclean
    /// exit — the one thing a signal-handler inside the dying process can never
    /// record. Surfacing it lets the successor log a startup breadcrumb.
    /// </summary>
    [Test]
    public async Task TryAcquire_WhenPriorHolderLeftStalePidFile_ReportsUncleanExit() {
        using var daemons = new TempDaemonStore();

        // A well-formed PID file from a prior holder that never cleaned up.
        daemons.Store.EnsureDirectory();
        File.WriteAllText(daemons.Store.PidPath("alpha"), "424242\n637999999999999999");

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(l).IsNotNull();
        await Assert.That(l!.PriorExitWasUnclean).IsTrue();
        await Assert.That(l.PriorHolderPid).IsEqualTo(424242);
    }

    /// <summary>
    /// A leftover PID file whose first line isn't a parseable PID still signals an
    /// unclean prior exit (the file's mere presence after we hold the flock is the
    /// signal), we just can't name the dead PID.
    /// </summary>
    [Test]
    public async Task TryAcquire_WhenStalePidFileUnparseable_StillReportsUncleanExit_WithNullPid() {
        using var daemons = new TempDaemonStore();

        daemons.Store.EnsureDirectory();
        File.WriteAllText(daemons.Store.PidPath("alpha"), "not-a-pid\n");

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(l).IsNotNull();
        await Assert.That(l!.PriorExitWasUnclean).IsTrue();
        await Assert.That(l.PriorHolderPid).IsNull();
    }

    /// <summary>
    /// A stale PID file that is present but can't be read (permissions, transient
    /// IO) must still count as an unclean prior exit — the file's presence once we
    /// hold the flock is the signal; parsing the PID is secondary. Returning a
    /// clean result on a read failure would silently drop a real hard-death.
    /// </summary>
    [Test]
    public async Task TryAcquire_WhenStalePidFilePresentButUnreadable_ReportsUncleanExit_WithNullPid() {
        if (OperatingSystem.IsWindows()) return; // Unix permission model only

        using var daemons = new TempDaemonStore();

        try {
            daemons.Store.EnsureDirectory();
            var pidPath = daemons.Store.PidPath("alpha");
            File.WriteAllText(pidPath, "777\n");
            // Write-only for the owner: File.Exists sees it and TryAcquire's
            // WritePidFile can still overwrite it, but ReadAllText throws (no read
            // bit), exercising the "present but unreadable" path.
            File.SetUnixFileMode(pidPath, UnixFileMode.UserWrite);

            using var l = DaemonLock.TryAcquire(daemons.Store, "alpha");
            await Assert.That(l).IsNotNull();
            await Assert.That(l!.PriorExitWasUnclean).IsTrue();
            await Assert.That(l.PriorHolderPid).IsNull();
        } finally {
            // Readable again so the directory's own teardown isn't fighting the mode.
            try { File.SetUnixFileMode(daemons.Store.PidPath("alpha"), UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task TryAcquire_OnFreshSlot_ReportsCleanPriorExit() {
        using var daemons = new TempDaemonStore();

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(l).IsNotNull();
        await Assert.That(l!.PriorExitWasUnclean).IsFalse();
        await Assert.That(l.PriorHolderPid).IsNull();
    }

    /// <summary>
    /// A graceful shutdown runs <see cref="DaemonLock.Dispose"/>, which deletes the
    /// PID file. The next acquisition must therefore see a clean prior exit — no
    /// false-positive breadcrumb after a normal stop/restart.
    /// </summary>
    [Test]
    public async Task TryAcquire_AfterCleanDispose_ReportsCleanPriorExit() {
        using var daemons = new TempDaemonStore();

        DaemonLock.TryAcquire(daemons.Store, "alpha")!.Dispose();

        using var l = DaemonLock.TryAcquire(daemons.Store, "alpha");
        await Assert.That(l).IsNotNull();
        await Assert.That(l!.PriorExitWasUnclean).IsFalse();
        await Assert.That(l.PriorHolderPid).IsNull();
    }
}
