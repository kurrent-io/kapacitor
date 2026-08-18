using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// <see cref="DaemonPidProbe.ValidatedPid"/> is the extracted, reusable form of the PID-file
/// validation <c>DaemonCommands.StopByName</c> already relied on (moved from its private
/// <c>ReadPidFile</c>/<c>IsOurDaemon</c> helpers). The on-disk format mirrored here is the real
/// one: line 1 is the PID, line 2 (optional) is the start token — see
/// <c>DaemonLock</c>/<c>DaemonStopSelfPidTests</c> for the same shape.
/// </summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonPidProbeTests {
    [Test]
    public async Task Null_when_no_pid_file() {
        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        try {
            await Assert.That(DaemonPidProbe.ValidatedPid("nosuch")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Null_for_dead_pid() {
        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        try {
            // PID 999999999 is far above any real pid_max, so it never resolves to a live
            // process — Process.GetProcessById throws ArgumentException, which IsOurDaemon
            // (moved into the probe) treats as "not ours" regardless of the token.
            File.WriteAllText(DaemonLockPaths.PidPath("x"), "999999999\ntok:deadbeef\n");

            await Assert.That(DaemonPidProbe.ValidatedPid("x")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Null_for_unparseable_pid_file() {
        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        try {
            File.WriteAllText(DaemonLockPaths.PidPath("y"), "not-a-pid\n");

            await Assert.That(DaemonPidProbe.ValidatedPid("y")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Returns_pid_for_a_live_owned_process() {
        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        try {
            // Same technique as DaemonStopSelfPidTests: a pid file naming the CURRENT process
            // with its REAL start token is indistinguishable from a live daemon's own file to
            // the identity check, so it's the one live process a unit test can safely probe.
            var token = ProcessStartToken.ForPid(Environment.ProcessId);

            await File.WriteAllTextAsync(
                DaemonLockPaths.PidPath("self"), $"{Environment.ProcessId}\n{token}\n");

            await Assert.That(DaemonPidProbe.ValidatedPid("self")).IsEqualTo(Environment.ProcessId);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
