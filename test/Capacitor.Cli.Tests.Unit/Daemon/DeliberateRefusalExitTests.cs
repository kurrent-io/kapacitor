using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Decision 6: a supervised daemon's deliberate refusal (local name-lock, server
/// <c>NameInUse</c>) exits 0 so <c>KeepAlive SuccessfulExit=false</c> doesn't respin it forever;
/// a manual daemon keeps 2/3 for scripts. Covers the two pure exit-decision functions and the
/// env-backed <see cref="DaemonRunner.IsSupervised"/> predicate they're driven by — mirrors
/// <c>DaemonNameResolverTests</c>' env save/restore pattern since <c>IsSupervised</c> reads the
/// real process environment via <c>SupervisionDetector.DetectCurrent</c>.
/// </summary>
[NotInParallel("KCAP_DAEMON_SUPERVISED")]
public class DeliberateRefusalExitTests {
    static readonly string? OriginalEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_SUPERVISED");

    static void Reset() => Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", OriginalEnv);

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
        Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", "laptop");

        try {
            await Assert.That(DaemonRunner.IsSupervised("laptop")).IsTrue();
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task IsSupervised_EnvMismatch_ReturnsFalse() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", "ci");

        try {
            await Assert.That(DaemonRunner.IsSupervised("laptop")).IsFalse();
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task IsSupervised_EnvUnset_ReturnsFalse() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", null);

        try {
            await Assert.That(DaemonRunner.IsSupervised("laptop")).IsFalse();
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task IsSupervised_EnvSetToUnsanitizedForm_DoesNotMatch() {
        // SupervisionDetector.Detect compares the marker against the SANITIZED name, ordinally —
        // it never sanitizes the marker itself. "My Daemon!" sanitizes to "my-daemon", so a marker
        // holding the raw pre-sanitize name is a mismatch even though it "looks like" the same
        // daemon. Pins IsSupervised to that exact existing contract rather than a looser one.
        Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", "My Daemon!");

        try {
            await Assert.That(DaemonRunner.IsSupervised("My Daemon!")).IsFalse();
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task IsSupervised_EnvMatchesAlreadySanitizedForm_ReturnsTrue() {
        // The unit bakes in the SANITIZED name (ServiceEnvironment), so the real-world match shape
        // is: resolvedName "My Daemon!" (sanitizes to "my-daemon") + marker "my-daemon" → true.
        Environment.SetEnvironmentVariable("KCAP_DAEMON_SUPERVISED", "my-daemon");

        try {
            await Assert.That(DaemonRunner.IsSupervised("My Daemon!")).IsTrue();
        } finally {
            Reset();
        }
    }
}
