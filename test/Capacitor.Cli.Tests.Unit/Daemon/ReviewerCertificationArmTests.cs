using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>Each arm of the reviewer-certification gate, including a failed version probe, and the
/// requirement that every rejection names its own cause and remedy.</summary>
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
    // In-range on purpose: an out-of-range replacement is the range arm's business (above), so this
    // pins the swap arm specifically.
    [Test] public async Task A_genuine_in_range_cli_swap_is_still_rejected() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", "2.9.9", Conn, Cert(expectedCliVersion: "2.1.212"));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("2.1.212");
        await Assert.That(reason).Contains("restart the daemon");
    }

    // A null ADVERTISED version must still fall through to the RANGE check -- that is the real gate,
    // and relaxing the swap arm must not let an out-of-range CLI through.
    [Test] public async Task A_null_advertisement_still_enforces_the_allowed_range() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", "1.0.0", Conn, Cert(expectedCliVersion: null));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("outside");
        await Assert.That(reason).Contains(">=2.0.0 <3.0.0");
    }

    // Codex review P1 #2: a failed LAUNCH-time probe is not a swap. With a version advertised at
    // registration and the launch probe timing out, the swap arm used to fire and tell the operator
    // the CLI had changed and to restart -- when nothing changed and restarting repeats the failure.
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task A_failed_launch_probe_is_diagnosed_as_transient_not_as_a_swap(string? probed) {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", probed, Conn, Cert(expectedCliVersion: "2.1.212"));

        await Assert.That(ok).IsFalse();               // still fails closed
        await Assert.That(reason).Contains("probe");
        await Assert.That(reason).Contains("retry");
        await Assert.That(reason).DoesNotContain("advertised");   // not the swap story
        await Assert.That(reason).DoesNotContain("restart");      // not the swap remedy
    }

    // Codex review P2 #3: a CLI genuinely replaced with an OUT-OF-RANGE version must be told about
    // the range, not told to restart -- restarting re-advertises the same out-of-range version, so
    // that remedy can never work.
    [Test] public async Task An_out_of_range_replacement_reports_the_range_not_a_restart() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", "9.9.9", Conn, Cert(expectedCliVersion: "2.1.212"));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("outside");
        await Assert.That(reason).Contains(">=2.0.0 <3.0.0");
        await Assert.That(reason).DoesNotContain("restart the daemon");
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

    // Probe failed AND nothing advertised: still the dedicated transient arm, never an empty-string
    // version in the text (which would read as "the CLI reports no version").
    [Test] public async Task A_failed_probe_with_no_advertisement_still_reports_the_probe() {
        var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(
            "claude", null, Conn, Cert(expectedCliVersion: null));

        await Assert.That(ok).IsFalse();
        await Assert.That(reason).Contains("probe");
        await Assert.That(reason).Contains("retry");
    }

    // No arm may claim the revision matched/mismatched -- the revision is equal on every failure
    // path here, and naming it was the misdirection.
    [Test] public async Task No_rejection_blames_the_certification_revision() {
        foreach (var (v, probed, conn, cert) in new (string, string?, string, ReviewerCertificationRequirement)[] {
            ("codex",  "2.1.212", Conn,     Cert(vendor: "claude")),
            ("claude", "2.1.212", "conn-2", Cert()),
            ("claude", "2.9.9",   Conn,     Cert(expectedCliVersion: "2.1.212")),
            ("claude", "1.0.0",   Conn,     Cert(expectedCliVersion: null)),
            ("claude", null,      Conn,     Cert(expectedCliVersion: "2.1.212")),
        }) {
            var (ok, reason) = AgentOrchestrator.EvaluateReviewerCertification(v, probed, conn, cert);
            await Assert.That(ok).IsFalse();
            await Assert.That(reason).DoesNotContain("revision");
        }
    }
}
