using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Per-vendor hook for resolving + applying a requested model against an ACP session, called once
/// from AcpHostedAgentRuntime.StartAsync after session/new resolves and before the first
/// session/prompt fires.
///
/// <b>Cancellation contract (spec-review Finding 2):</b> a canceled <c>ct</c> is NOT
/// one of the "never throws" failure modes below — <see cref="AcpConnection.RequestAsync"/> throws
/// <see cref="OperationCanceledException"/> when <c>ct</c> is canceled, and every
/// implementation of this method MUST let that propagate uncaught, aborting <c>StartAsync</c>
/// entirely (no runtime is ever handed back to a caller who already canceled the launch). Only a
/// best-effort RESOLUTION failure — no requested model, a <c>session/new</c> result publishing no
/// selectable-model list in either shape <see cref="AcpSessionModelList"/> reads, no match within
/// that list, or a well-formed JSON-RPC ERROR
/// response to whatever RPC this selector sends — returns <see langword="null"/> and lets
/// <c>StartAsync</c> continue to the first prompt with the vendor's own default model.
/// <see cref="ConfigOptionModelSelector.TrySelectAsync"/>'s <c>catch (Exception ex) when (ex is not
/// OperationCanceledException)</c> around its `session/set_config_option` call is what enforces
/// this split for Cursor: an <see cref="OperationCanceledException"/> is deliberately NOT caught by
/// that guard and propagates straight out of `TrySelectAsync`, then out of `StartAsync`. The
/// earlier `catch (JsonException ex)` around the `models` parse is narrower still — a
/// <see cref="System.Text.Json.JsonException"/> is never an
/// <see cref="OperationCanceledException"/>, so it structurally cannot swallow one either; a future
/// implementation of this interface must preserve the same shape (catch concrete failure types,
/// never a bare `catch (Exception)` that would also eat cancellation).
/// </summary>
internal interface IAcpModelSelector {
    /// <summary>
    /// Whether this selector can actually apply a caller-requested model. <see langword="false"/> means
    /// a requested model will be silently discarded, so callers must not REPORT one as the model the
    /// process is running.
    ///
    /// <para>This lives on the selector, not as a parallel descriptor flag, because the selector object
    /// is deliberately the single source of truth for model selection (see
    /// <c>AcpVendorDescriptor</c>'s note on the removed <c>SupportsModelSelection</c> field — a second
    /// boolean that also had to agree with the selector was dead state guarded asymmetrically). Asking
    /// the selector cannot disagree with itself, and it works for a future vendor's own
    /// implementation or a test double, neither of which is reference-equal to the two singletons
    /// here.</para>
    ///
    /// <para>Consumers must not infer this by type-testing for <see cref="NoOpModelSelector"/>.</para>
    /// </summary>
    bool CanSelectModel { get; }

    Task<string?> TrySelectAsync(
        AcpConnection     connection,
        string            sessionId,
        JsonElement       sessionNewResult,
        string?           requestedModel,
        ILogger           logger,
        CancellationToken ct);
}

/// <summary>
/// The resolution half both wire selectors share: read <c>session/new</c>'s selectable-model list
/// and resolve the requested string to an exact wire value via <see cref="AcpModelResolver"/>. Pure
/// and synchronous — the cancellation contract above concerns only the wire write, which each
/// selector keeps for itself.
///
/// <para>The list comes from <see cref="AcpSessionModelList.Extract"/>, which reads BOTH published
/// shapes (<c>models.availableModels</c> and OpenCode's <c>configOptions</c>) and is total: a shape
/// it cannot read contributes nothing rather than throwing, so this method structurally cannot
/// swallow an <see cref="OperationCanceledException"/> either — there is no longer a
/// <c>catch</c> here at all.</para>
/// </summary>
file static class SessionModelResolution {
    /// <summary>Null when nothing was requested, no model list was published in either shape, or
    /// nothing matched (the no-match case logs the Warning both selectors share).</summary>
    public static string? ResolveOrNull(
            JsonElement sessionNewResult, string? requestedModel, ILogger logger) {
        if (string.IsNullOrWhiteSpace(requestedModel))
            return null;

        var availableModels = AcpSessionModelList.Extract(sessionNewResult);

        var resolvedModelId = AcpModelResolver.Resolve(requestedModel, availableModels);
        if (resolvedModelId is null) {
            logger.LogWarning(
                "ACP: requested model '{RequestedModel}' was not found in session/new's selectable-model list; continuing with the vendor's default model.",
                requestedModel);
        }

        return resolvedModelId;
    }
}

/// <summary>
/// Today's original Cursor behavior, unchanged, generalized to take sessionId/connection as
/// parameters instead of reading AcpHostedAgentRuntime's private fields: parses session/new's
/// `models.availableModels` via AcpModelResolver.Resolve, and — on a match — sends
/// session/set_config_option {sessionId, configId: "model", value} and awaits it. Every failure
/// mode (missing/unparsable `models`, no match, a JSON-RPC error response) is caught and logged,
/// never fatal — matches the "model selection is a nice-to-have, never a launch precondition"
/// contract from docs/ai-688-cursor-prototype-findings.md.
/// </summary>
internal sealed class ConfigOptionModelSelector : IAcpModelSelector {
    public static readonly ConfigOptionModelSelector Instance = new();

    /// <summary>This selector really does send <c>session/set_config_option</c>, so a requested model
    /// may be applied. Note it can still fail to MATCH a model and return null — "can select" is a
    /// capability, not a promise about a specific request.</summary>
    public bool CanSelectModel => true;

    public async Task<string?> TrySelectAsync(
            AcpConnection connection, string sessionId, JsonElement sessionNewResult,
            string? requestedModel, ILogger logger, CancellationToken ct) {
        var resolvedModelId = SessionModelResolution.ResolveOrNull(sessionNewResult, requestedModel, logger);
        if (resolvedModelId is null)
            return null;

        var setConfigOptionParams = JsonSerializer.SerializeToElement(
            new SetConfigOptionParams(SessionId: sessionId, ConfigId: "model", Value: resolvedModelId),
            CapacitorJsonContext.Default.SetConfigOptionParams);

        try {
            await connection.RequestAsync("session/set_config_option", setConfigOptionParams, ct).ConfigureAwait(false);
            return resolvedModelId;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex,
                "ACP: session/set_config_option failed for model '{ResolvedModelId}'; continuing with the vendor's default model.",
                resolvedModelId);
            return null;
        }
    }
}

/// <summary>
/// The <c>session/set_model</c> twin of <see cref="ConfigOptionModelSelector"/>, for vendors that
/// implement the stabilized ACP model-selection method instead of the config-option one. Same
/// resolution half (<see cref="SessionModelResolution"/>), same never-fatal contract, same
/// cancellation shape (the catch below cannot swallow <see cref="OperationCanceledException"/>) —
/// only the wire write differs: <c>session/set_model {sessionId, modelId}</c>.
///
/// <para>Carried by Kiro on direct measurement (<c>docs/probes/2026-08-05-kiro-model-override/</c>,
/// kiro-cli 2.16.0): Kiro answers <c>session/set_config_option</c> with <c>-32601 Method not
/// found</c>, while <c>session/set_model</c> succeeds AND takes effect — the next turn's backend
/// request carried the requested <c>modelId</c>, the reply self-identified as it, and Kiro's own
/// persisted session state recorded it with model-specific parameters. A vendor adopting this
/// selector needs that same effect-level evidence, not just a success response: an RPC that
/// returns <c>{}</c> and changes nothing would make the session report a model it is not
/// running.</para>
/// </summary>
internal sealed class SetModelSelector : IAcpModelSelector {
    public static readonly SetModelSelector Instance = new();

    /// <summary>This selector really does send <c>session/set_model</c>, so a requested model may
    /// be applied. It can still fail to MATCH and return null — "can select" is a capability, not a
    /// promise about a specific request.</summary>
    public bool CanSelectModel => true;

    public async Task<string?> TrySelectAsync(
            AcpConnection connection, string sessionId, JsonElement sessionNewResult,
            string? requestedModel, ILogger logger, CancellationToken ct) {
        var resolvedModelId = SessionModelResolution.ResolveOrNull(sessionNewResult, requestedModel, logger);
        if (resolvedModelId is null)
            return null;

        var setModelParams = JsonSerializer.SerializeToElement(
            new SetModelParams(SessionId: sessionId, ModelId: resolvedModelId),
            CapacitorJsonContext.Default.SetModelParams);

        try {
            await connection.RequestAsync("session/set_model", setModelParams, ct).ConfigureAwait(false);
            return resolvedModelId;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex,
                "ACP: session/set_model failed for model '{ResolvedModelId}'; continuing with the vendor's default model.",
                resolvedModelId);
            return null;
        }
    }
}

/// <summary>Used by a descriptor whose vendor has no model-selection hook at all — never touches
/// the wire, never inspects sessionNewResult. <b>Round 2 Finding 2:</b> there is no
/// SupportsModelSelection flag to check this instance against — a vendor opts out of model
/// selection simply by carrying THIS instance as its ModelSelector, and
/// AcpHostedAgentRuntimeFactory.StartAsync (D4) forwards descriptor.ModelSelector unconditionally
/// for every descriptor. This type is exactly as valid a ModelSelector as
/// ConfigOptionModelSelector.Instance — the object itself is the whole contract, not a paired
/// boolean.</summary>
internal sealed class NoOpModelSelector : IAcpModelSelector {
    public static readonly NoOpModelSelector Instance = new();

    /// <summary>No hook exists, so a requested model is always discarded. Callers use this to avoid
    /// REPORTING a model the process is not running — discarding the request quietly is fine, but
    /// telling the dashboard and analytics that the discarded model is live is not.</summary>
    public bool CanSelectModel => false;

    public Task<string?> TrySelectAsync(
            AcpConnection connection, string sessionId, JsonElement sessionNewResult,
            string? requestedModel, ILogger logger, CancellationToken ct) {
        if (!string.IsNullOrWhiteSpace(requestedModel))
            logger.LogDebug("ACP: this vendor has no model-selection hook; continuing with its default model.");
        return Task.FromResult<string?>(null);
    }
}
