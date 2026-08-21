using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The gate keeping a launch from REPORTING a model it is not running.
///
/// <para>Found by code review on the Kiro hosted-agent change: a runtime carrying
/// <see cref="NoOpModelSelector"/> discards a requested model on the ACP wire, but the orchestrator's
/// single <c>effectiveModel</c> was still published on the <c>AgentInstance</c> — driving the live
/// model chip and <c>hosted_agent_started</c> analytics. So Kiro + <c>model="foo"</c> ran Kiro's
/// default while telling everyone <c>foo</c> was live: the exact silent requested-vs-running mismatch
/// the no-op selector had been chosen to avoid.</para>
/// </summary>
public class ModelSelectionLaunchPolicyTests {
    const string Requested = "some-model";

    // ── the runtime CAN select: never interfere ────────────────────────────────

    [Test]
    public async Task SupportsSelection_WithRequestedModel_Honors() {
        await Assert.That(ModelSelectionLaunchPolicy.Evaluate(
                Requested, supportsModelSelection: true, isExplicitReviewerModel: false))
            .IsEqualTo(ModelSelectionDisposition.Honor);
    }

    [Test]
    public async Task SupportsSelection_WithExplicitReviewerModel_Honors() {
        await Assert.That(ModelSelectionLaunchPolicy.Evaluate(
                Requested, supportsModelSelection: true, isExplicitReviewerModel: true))
            .IsEqualTo(ModelSelectionDisposition.Honor);
    }

    // ── no model requested: nothing can be misreported ─────────────────────────

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NoRequestedModel_Honors_EvenWhenSelectionUnsupported(string? requested) {
        // Whitespace counts as absent, matching what the selectors themselves treat as "no request" —
        // otherwise a blank model string would trip a rejection on a launch that asked for nothing.
        await Assert.That(ModelSelectionLaunchPolicy.Evaluate(
                requested, supportsModelSelection: false, isExplicitReviewerModel: false))
            .IsEqualTo(ModelSelectionDisposition.Honor);

        await Assert.That(ModelSelectionLaunchPolicy.Evaluate(
                requested, supportsModelSelection: false, isExplicitReviewerModel: true))
            .IsEqualTo(ModelSelectionDisposition.Honor);
    }

    // ── the runtime CANNOT select: the two negative cases differ ───────────────

    /// <summary>Interactive: proceed with honest metadata. Refusing outright would make the vendor
    /// unlaunchable from any caller that always sends a model.</summary>
    [Test]
    public async Task CannotSelect_InteractiveLaunch_ClearsReportedModel() {
        await Assert.That(ModelSelectionLaunchPolicy.Evaluate(
                Requested, supportsModelSelection: false, isExplicitReviewerModel: false))
            .IsEqualTo(ModelSelectionDisposition.ClearReportedModel);
    }

    /// <summary>A pinned reviewer model is different in kind: a review round's authority depends on
    /// which model produced it, so silently reviewing with another model is worse than not reviewing —
    /// even with truthful metadata.</summary>
    [Test]
    public async Task CannotSelect_ExplicitReviewerModel_Rejects() {
        await Assert.That(ModelSelectionLaunchPolicy.Evaluate(
                Requested, supportsModelSelection: false, isExplicitReviewerModel: true))
            .IsEqualTo(ModelSelectionDisposition.Reject);
    }

    [Test]
    public async Task RejectionReason_NamesTheVendorAndTheModelItCannotHonor() {
        var reason = ModelSelectionLaunchPolicy.RejectionReason("kiro", "claude-opus-4-8");

        await Assert.That(reason).Contains("kiro");
        await Assert.That(reason).Contains("claude-opus-4-8");
    }

    // ── the selectors are the source of truth for the flag above ──────────────

    [Test]
    public async Task ConfigOptionSelector_And_SetModelSelector_CanSelect_NoOpSelector_Cannot() {
        await Assert.That(ConfigOptionModelSelector.Instance.CanSelectModel).IsTrue();
        await Assert.That(SetModelSelector.Instance.CanSelectModel).IsTrue();
        await Assert.That(NoOpModelSelector.Instance.CanSelectModel).IsFalse();
    }

    /// <summary>Pinned per-vendor so flipping a selector cannot quietly re-open the
    /// reported-vs-running mismatch. Kiro flipped to true on the probe that verified
    /// <c>session/set_model</c> at effect level (docs/probes/2026-08-05-kiro-model-override/);
    /// Gemini is now the vendor this policy exists for — its write half stays unverified.</summary>
    [Test]
    public async Task DescriptorSelectors_ReportSelectionCapabilityPerVendor() {
        await Assert.That(AcpVendorDescriptors.Kiro.ModelSelector.CanSelectModel).IsTrue();
        await Assert.That(AcpVendorDescriptors.Gemini.ModelSelector.CanSelectModel).IsFalse();
        await Assert.That(AcpVendorDescriptors.Cursor.ModelSelector.CanSelectModel).IsTrue();
        await Assert.That(AcpVendorDescriptors.Copilot.ModelSelector.CanSelectModel).IsTrue();
    }
}
