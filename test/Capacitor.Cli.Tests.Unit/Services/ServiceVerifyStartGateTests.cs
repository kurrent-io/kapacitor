using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Pure decision-core tests for <see cref="ServiceVerify.EvaluateStartGate"/>.
/// No filesystem, no lock, no manager — every case is driven purely through the function's own
/// parameters, per the task brief verbatim.</summary>
public class ServiceVerifyStartGateTests {
    [Test]
    public async Task Gate_inactive_without_invoking_directive() {
        var r = ServiceVerify.EvaluateStartGate(new Dictionary<string, string>(), "/b", "/b", _ => null);
        await Assert.That(r).IsNull();
    }

    [Test]
    public async Task Missing_unit_directive_is_directive_missing() {
        var r = ServiceVerify.EvaluateStartGate(
            new Dictionary<string, string>(),  // unit bakes nothing
            "/b", "/b", k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
        await Assert.That(r).IsEqualTo(StartGateReason.DirectiveMissing);
    }

    [Test]
    public async Task Unit_directive_with_wrong_value_is_directive_invalid() {
        var unit = new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "allow" };
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b",
            k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
        await Assert.That(r).IsEqualTo(StartGateReason.DirectiveInvalid);
    }

    [Test]
    public async Task Digest_mismatch_at_canonical_sibling_is_package_inconsistent_elsewhere_foreign() {
        // placeholder digest in test builds → Matches() false for any file:
        var unit = new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt" };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt", _ => null };

        var same = ServiceVerify.EvaluateStartGate(unit, "/opt/kcap/kcap-daemon", "/opt/kcap/kcap-daemon", Env);
        await Assert.That(same).IsEqualTo(StartGateReason.PackageInconsistent);

        var other = ServiceVerify.EvaluateStartGate(unit, "/somewhere/else/kcap-daemon", "/opt/kcap/kcap-daemon", Env);
        await Assert.That(other).IsEqualTo(StartGateReason.ForeignBinary);
    }

    // ── SameBinaryPath: OS-aware comparison split out of the digest-mismatch branch above ──

    [Test]
    public async Task Ordinal_comparison_treats_case_only_difference_as_a_different_path() {
        var r = ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/opt/kcap/KCAP-DAEMON", StringComparison.Ordinal);
        await Assert.That(r).IsFalse();
    }

    [Test]
    public async Task OrdinalIgnoreCase_comparison_treats_case_only_difference_as_the_same_path() {
        var r = ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/opt/kcap/KCAP-DAEMON", StringComparison.OrdinalIgnoreCase);
        await Assert.That(r).IsTrue();
    }

    [Test]
    public async Task Both_comparisons_agree_a_genuinely_different_path_is_different() {
        await Assert.That(ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/somewhere/else/kcap-daemon", StringComparison.Ordinal)).IsFalse();
        await Assert.That(ServiceVerify.SameBinaryPath("/opt/kcap/kcap-daemon", "/somewhere/else/kcap-daemon", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task Stale_unit_expectation_is_identity_mismatch() {
        var unit = new Dictionary<string, string> {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_PROFILE"] = "a",
            ["KCAP_URL"] = "https://s.example",              // unit resolves S
            ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
        };
        string? Env(string k) => k switch {
            "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
            "KCAP_PROFILE" => "a",
            "KCAP_EXPECT_SERVER_URL" => "https://t.example",  // fresh invocation expects T
            _ => null };
        // digest can't pass in test builds — inject a digest-pass seam for identity-only tests:
        var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
        await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
    }
}
