using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>The on-disk format mirrored here is the real one: line 1 is the PID, line 2 (optional)
/// is the start token.</summary>
public class DaemonPidProbeTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Null_when_no_pid_file() {
        await Assert.That(DaemonPidProbe.ValidatedPid(Daemons.Store, "nosuch")).IsNull();
    }

    [Test]
    public async Task Null_for_dead_pid() {
        // PID 999999999 is far above any real pid_max, so it never resolves to a live
        // process — Process.GetProcessById throws ArgumentException, which IsOurDaemon
        // (moved into the probe) treats as "not ours" regardless of the token.
        File.WriteAllText(Daemons.Store.PidPath("x"), "999999999\ntok:deadbeef\n");

        await Assert.That(DaemonPidProbe.ValidatedPid(Daemons.Store, "x")).IsNull();
    }

    [Test]
    public async Task Null_for_unparseable_pid_file() {
        File.WriteAllText(Daemons.Store.PidPath("y"), "not-a-pid\n");

        await Assert.That(DaemonPidProbe.ValidatedPid(Daemons.Store, "y")).IsNull();
    }

    [Test]
    public async Task Returns_pid_for_a_live_owned_process() {
        // Same technique as DaemonStopSelfPidTests: a pid file naming the CURRENT process
        // with its REAL start token is indistinguishable from a live daemon's own file to
        // the identity check, so it's the one live process a unit test can safely probe.
        var token = ProcessStartToken.ForPid(Environment.ProcessId);

        await File.WriteAllTextAsync(
            Daemons.Store.PidPath("self"), $"{Environment.ProcessId}\n{token}\n");

        await Assert.That(DaemonPidProbe.ValidatedPid(Daemons.Store, "self")).IsEqualTo(Environment.ProcessId);
    }
}
