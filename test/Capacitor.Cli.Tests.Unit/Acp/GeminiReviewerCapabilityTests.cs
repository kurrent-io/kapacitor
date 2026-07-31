using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The capability gate is the daemon OPERATOR's consent, and it is fail-closed on every axis: the operator
/// flag, and the certified-version check that stops an enabled flag carrying that consent across a vendor
/// upgrade which invalidates the MCP-allowlist mechanism the reviewer's containment rests on.
/// </summary>
public class GeminiReviewerCapabilityTests {
    const string Certified = "0.53.0";

    [Test]
    public async Task EnabledAndCertified_IsTheOnlyPermittedCombination() {
        await Assert.That(GeminiReviewerCapability.IsEnabled(true, Certified)).IsTrue();
    }

    [Test]
    public async Task DisabledByTheOperator_IsRefusedEvenOnACertifiedVersion() {
        await Assert.That(GeminiReviewerCapability.IsEnabled(false, Certified)).IsFalse();
        await Assert.That(GeminiReviewerCapability.DenialReason(false, Certified))
            .Contains("gemini_unattended_reviewer_disabled");
    }

    /// <summary>An unresolvable version is UNKNOWN, and unknown is denied — not assumed compatible.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnresolvedVersion_IsRefused(string? version) {
        await Assert.That(GeminiReviewerCapability.IsEnabled(true, version)).IsFalse();
    }

    /// <summary>
    /// The direction that matters: a NEWER version is refused, not accepted. The set is a certification
    /// record, not a floor — a later build may have changed the matcher, which is the whole hazard.
    /// </summary>
    [Test]
    [Arguments("0.54.0")]
    [Arguments("0.53.1")]
    [Arguments("1.0.0")]
    [Arguments("0.52.9")]
    public async Task AnUncertifiedVersion_IsRefusedInBothDirections(string version) {
        await Assert.That(GeminiReviewerCapability.IsEnabled(true, version)).IsFalse();
        await Assert.That(GeminiReviewerCapability.DenialReason(true, version))
            .Contains("version_uncertified");
    }

    [Test]
    public async Task AVersionIsMatchedExactly_ButSurroundingWhitespaceIsTolerated() {
        await Assert.That(GeminiReviewerCapability.IsEnabled(true, $"  {Certified}\n")).IsTrue();
        await Assert.That(GeminiReviewerCapability.IsEnabled(true, $"v{Certified}")).IsFalse();
    }

    /// <summary>The denial reason must name the actual cause, or an operator cannot act on it.</summary>
    [Test]
    public async Task TheDenialReasonDistinguishesDisabledFromUncertified() {
        await Assert.That(GeminiReviewerCapability.DenialReason(false, null))
            .Contains("disabled");
        await Assert.That(GeminiReviewerCapability.DenialReason(true, null))
            .Contains("version_unresolved");
    }

    /// <summary>
    /// A guard against the certified set being widened casually: it should hold only versions whose live
    /// certification was actually re-run. If this fails, that happened without the comment being read.
    /// </summary>
    [Test]
    public async Task TheCertifiedSetHoldsOnlyTheVersionThisWorkCertified() {
        await Assert.That(GeminiReviewerCapability.CertifiedVersions.Order(StringComparer.Ordinal))
            .IsEquivalentTo(new[] { Certified });
    }
}
