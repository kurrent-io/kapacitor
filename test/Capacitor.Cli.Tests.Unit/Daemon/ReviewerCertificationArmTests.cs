using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// The reviewer-certification gate, arm by arm.
///
/// <para>Two defects motivated these. A transient `claude --version` probe timeout at daemon
/// registration advertised a NULL CLI version for the daemon's lifetime; the launch-time equality
/// check then compared that null against a successful probe and rejected every launch. And all four
/// arms collapsed into one message that named the certification revision (which matched) and told
/// the operator to update a CLI that was correct and in range — so the real cause was undiagnosable
/// from the error alone.</para>
/// </summary>
public class ReviewerCertificationArmTests {
    const string Conn   = "conn-1";
    const string Policy = "claude-unattended-v1";

    static ReviewerCertificationRequirement Cert(
            string vendor = "claude", string ranges = ">=2.0.0 <3.0.0",
            string? expectedCliVersion = "2.1.212", string connectionId = Conn) =>
        new(vendor, ranges, Policy, Policy, connectionId, expectedCliVersion!);

    [Test] public async Task A_matching_certification_passes() {
        var (ok, _) = AgentOrchestrator.EvaluateReviewerCertification("claude", "2.1.212", Conn, Cert());
        await Assert.That(ok).IsTrue();
    }

    // THE regression. A null advertised version means the registration probe failed -- a transient
    // condition, not evidence the CLI changed. The equality arm exists to catch a CLI SWAP between
    // advertisement and launch, and null-vs-value is not a swap.
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task A_failed_registration_probe_does_not_reject_a_launch(string? advertised) {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", "2.1.212", Conn, Cert(expectedCliVersion: advertised));

        await Assert.That(ok).IsTrue().Because($"advertised '{advertised ?? "null"}' means the probe failed, not that the CLI changed: {reason}");
    }

    // ...but a genuine swap must still be caught, or the relaxation above would gut the arm.
    [Test] public async Task A_genuine_cli_swap_is_still_rejected() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", "2.9.9", Conn, Cert(expectedCliVersion: "2.1.212"));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("2.1.212");
        await Assert.That(reason).Contains("restart the daemon");
    }

    // A null advertised version must still fall through to the RANGE check -- that is the real gate,
    // and relaxing the equality arm must not let an out-of-range CLI through.
    [Test] public async Task A_failed_probe_still_enforces_the_allowed_range() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", "1.0.0", Conn, Cert(expectedCliVersion: null));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("outside");
        await Assert.That(reason).Contains(">=2.0.0 <3.0.0");
    }

    // Each arm names ITSELF. The old single message reported the certification revision -- which
    // matches on every one of these paths -- and blamed the CLI regardless of the actual cause.
    [Test] public async Task A_vendor_mismatch_names_the_vendors() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "codex", "2.1.212", Conn, Cert(vendor: "claude"));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("codex");
        await Assert.That(reason).Contains("claude");
    }

    [Test] public async Task A_reconnect_names_the_connection_change_and_says_retry() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", "2.1.212", "conn-2", Cert(connectionId: "conn-1"));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("reconnected");
        await Assert.That(reason).Contains("retry");
    }

    // The probe failing AND no advertised version: the message must say the probe failed rather than
    // printing an empty string, which reads as "the CLI reports no version".
    [Test] public async Task A_failed_probe_outside_range_says_the_probe_failed() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", null, Conn, Cert(expectedCliVersion: null));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("version probe failed");
    }

    // No arm may claim the revision matched/mismatched -- the revision is equal on every failure
    // path here, and naming it was the misdirection.
    [Test] public async Task No_rejection_blames_the_certification_revision() {
        foreach (var (v, probed, conn, cert) in new (string, string?, string, ReviewerCertificationRequirement)[] {
            ("codex",  "2.1.212", Conn,     Cert(vendor: "claude")),
            ("claude", "2.1.212", "conn-2", Cert()),
            ("claude", "2.9.9",   Conn,     Cert(expectedCliVersion: "2.1.212")),
            ("claude", "1.0.0",   Conn,     Cert(expectedCliVersion: null)),
        }) {
            var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(v, probed, conn, cert);
            await Assert.That(ok).IsFalse();
            await Assert.That(reason).DoesNotContain("revision");
        }
    }
}
