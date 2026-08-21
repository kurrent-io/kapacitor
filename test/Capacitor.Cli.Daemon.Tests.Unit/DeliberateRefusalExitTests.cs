namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// Decision 6: a supervised daemon's deliberate refusal (local name-lock, server
/// <c>NameInUse</c>) exits 0 so <c>KeepAlive SuccessfulExit=false</c> doesn't respin it forever;
/// a manual daemon keeps 2/3 for scripts. Covers the two pure exit-decision functions and the
/// env-backed <see cref="DaemonRunner.IsSupervised"/> predicate they're driven by, which reads the
/// real process environment via <c>SupervisionDetector.DetectCurrent</c>.
/// </summary>
// KCAP_DAEMON_SUPERVISED is read through SupervisionDetector and inherited by every child
// this suite spawns, so the exclusion has to be assembly-wide rather than keyed.
[NotInParallel]
public class DeliberateRefusalExitTests {
    [Test]
    public async Task LockRefusalExit_Supervised_ReturnsZero() =>
        await Assert.That(DaemonRunner.LockRefusalExit(supervised: true)).IsEqualTo(0);

    [Test]
    public async Task LockRefusalExit_NotSupervised_ReturnsTwo() =>
        await Assert.That(DaemonRunner.LockRefusalExit(supervised: false)).IsEqualTo(2);

    [Test]
    public async Task NameInUseExit_Supervised_ReturnsZero() =>
        await Assert.That(DaemonRunner.NameInUseExit(supervised: true)).IsEqualTo(0);

    [Test]
    public async Task NameInUseExit_NotSupervised_ReturnsThree() =>
        await Assert.That(DaemonRunner.NameInUseExit(supervised: false)).IsEqualTo(3);

    [Test]
    public async Task IsSupervised_EnvMatchesSanitizedName_ReturnsTrue() {
        using var env = EnvScope.Exclusive("KCAP_DAEMON_SUPERVISED", "laptop");

        await Assert.That(DaemonRunner.IsSupervised("laptop")).IsTrue();
    }

    [Test]
    public async Task IsSupervised_EnvMismatch_ReturnsFalse() {
        using var env = EnvScope.Exclusive("KCAP_DAEMON_SUPERVISED", "ci");

        await Assert.That(DaemonRunner.IsSupervised("laptop")).IsFalse();
    }

    [Test]
    public async Task IsSupervised_EnvUnset_ReturnsFalse() {
        using var env = EnvScope.Exclusive("KCAP_DAEMON_SUPERVISED", null);

        await Assert.That(DaemonRunner.IsSupervised("laptop")).IsFalse();
    }

    [Test]
    public async Task IsSupervised_EnvSetToUnsanitizedForm_DoesNotMatch() {
        // SupervisionDetector.Detect compares the marker against the SANITIZED name, ordinally —
        // it never sanitizes the marker itself. "My Daemon!" sanitizes to "my-daemon", so a marker
        // holding the raw pre-sanitize name is a mismatch even though it "looks like" the same
        // daemon. Pins IsSupervised to that exact existing contract rather than a looser one.
        using var env = EnvScope.Exclusive("KCAP_DAEMON_SUPERVISED", "My Daemon!");

        await Assert.That(DaemonRunner.IsSupervised("My Daemon!")).IsFalse();
    }

    [Test]
    public async Task IsSupervised_EnvMatchesAlreadySanitizedForm_ReturnsTrue() {
        // The unit bakes in the SANITIZED name (ServiceEnvironment), so the real-world match shape
        // is: resolvedName "My Daemon!" (sanitizes to "my-daemon") + marker "my-daemon" → true.
        using var env = EnvScope.Exclusive("KCAP_DAEMON_SUPERVISED", "my-daemon");

        await Assert.That(DaemonRunner.IsSupervised("My Daemon!")).IsTrue();
    }
}
