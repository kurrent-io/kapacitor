namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// What to do with a caller-supplied model when the selected runtime cannot apply one.
/// </summary>
internal enum ModelSelectionDisposition {
    /// <summary>The runtime can select, or no model was requested: use the requested value as-is.</summary>
    Honor,

    /// <summary>The runtime cannot select and the request is an ordinary interactive launch: proceed,
    /// but report no model rather than one the process is not running.</summary>
    ClearReportedModel,

    /// <summary>The runtime cannot select and the request pins a reviewer model: refuse the launch.</summary>
    Reject
}

/// <summary>
/// Gate for the model a launch REPORTS versus the model it actually runs.
///
/// <para><b>The bug this exists to prevent.</b> A runtime whose model selector is a no-op discards a
/// requested model — correctly and by design. But the orchestrator computes one
/// <c>effectiveModel</c> and uses it for two different purposes: it becomes
/// <c>RuntimeStartContext.Model</c> (where the no-op selector drops it) AND the
/// <c>AgentInstance</c> model published to the server, which drives the live model chip and
/// <c>hosted_agent_started</c> analytics. Without this gate, launching such a vendor with
/// <c>model="foo"</c> runs the vendor's default while telling the dashboard and analytics that
/// <c>foo</c> is live — a silent requested-vs-running mismatch, and precisely the failure mode the
/// no-op selector was chosen to avoid.</para>
///
/// <para><b>Why the two negative cases differ.</b> Clearing the reported model is right for an
/// interactive launch: the user gets a working agent and honest metadata, and refusing outright would
/// make the vendor unlaunchable from any caller that always sends a model. It is NOT right for a
/// pinned reviewer model: a review round's authority depends on which model produced it, so silently
/// reviewing with a different model — even with truthful metadata — is worse than not reviewing.
/// There, fail loudly.</para>
///
/// <para>Pure so it is unit-testable without the orchestrator, mirroring
/// <see cref="UnattendedLaunchPolicy"/>.</para>
/// </summary>
internal static class ModelSelectionLaunchPolicy {
    /// <param name="requestedModel">The model this launch would report — the orchestrator's
    /// <c>effectiveModel</c>, i.e. an explicit reviewer model if present, else the raw command model.</param>
    /// <param name="supportsModelSelection">The selected runtime's
    /// <see cref="IHostedAgentRuntimeFactory.SupportsModelSelection"/>.</param>
    /// <param name="isExplicitReviewerModel">Whether <paramref name="requestedModel"/> came from a
    /// server-resolved explicit reviewer-model block rather than an ordinary launch.</param>
    public static ModelSelectionDisposition Evaluate(
            string? requestedModel, bool supportsModelSelection, bool isExplicitReviewerModel) {
        // No model requested ⇒ nothing to misreport, whatever the runtime supports. Whitespace counts
        // as absent, matching what the selectors themselves treat as "no request".
        if (string.IsNullOrWhiteSpace(requestedModel))
            return ModelSelectionDisposition.Honor;

        if (supportsModelSelection)
            return ModelSelectionDisposition.Honor;

        return isExplicitReviewerModel
            ? ModelSelectionDisposition.Reject
            : ModelSelectionDisposition.ClearReportedModel;
    }

    /// <summary>User-facing rejection message for <see cref="ModelSelectionDisposition.Reject"/>.</summary>
    public static string RejectionReason(string vendor, string requestedModel) =>
        $"Vendor '{vendor}' cannot apply a requested model, so it cannot honor the pinned reviewer " +
        $"model '{requestedModel}'; the round would silently run this vendor's default model instead. " +
        $"Retry without a reviewer-model override, or select a vendor that supports model selection.";
}
