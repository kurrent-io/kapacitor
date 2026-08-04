// src/Capacitor.Cli.Daemon/Acp/AcpInteractionBridge.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Bridges ACP server→client interaction requests (<c>session/request_permission</c>, and a
/// capability-gated, defensive <c>elicitation/create</c>) to the Capacitor server's
/// <c>AcpRequestInteraction</c> hub method, and maps the returned decision back to the
/// ACP JSON-RPC result shape. Wired as (a closure over) <see cref="AcpConnection.OnServerRequest"/>
/// by <see cref="Capacitor.Cli.Daemon.Services.AcpHostedAgentRuntime"/> (Task B4).
///
/// <paramref name="requestInteraction"/> is injected as a plain delegate — matching the shape of
/// <see cref="Capacitor.Cli.Daemon.Services.ServerConnection.RequestAcpInteractionAsync"/> — rather
/// than taking a concrete <c>ServerConnection</c> dependency, so this class is unit-testable
/// without a real SignalR connection (see <c>AcpInteractionBridgeTests</c>).
///
/// Defensive-by-construction: every code path that can fail (missing/malformed params, the
/// server call throwing, a decision that doesn't map to any offered option) returns a
/// <c>"cancelled"</c>/safest-available-option result rather than propagating an exception —
/// <see cref="AcpConnection.HandleServerRequestAsync"/> already guarantees exactly one response
/// frame is always written for a given request id, but this bridge additionally guarantees that
/// response is always a well-formed ACP outcome, never a generic JSON-RPC "Internal error" that
/// would tell the agent nothing about WHY the permission was denied.
/// </summary>
internal sealed partial class AcpInteractionBridge(
        Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> requestInteraction,
        string                                                                       agentId,
        ILogger                                                                      logger,
        AcpUnattendedInteractionPolicy                                                unattendedPolicy = AcpUnattendedInteractionPolicy.Disabled,
        Action<string>?                                                               unexpectedUnattendedInteraction = null
    ) {
    /// <summary>
    /// Handles one inbound <see cref="AcpRequest"/>. Returns <see langword="null"/> for any method
    /// this bridge doesn't recognize (letting <see cref="AcpConnection.HandleServerRequestAsync"/>'s
    /// existing default-decline posture answer with a JSON-RPC "Method not found" error, unchanged
    /// from the prior behavior for every method except the two this bridge now claims).
    ///
    /// <b>Qodo daemon-review Q2:</b> takes NO caller-supplied session id — the ACP session id used to
    /// correlate this interaction server-side comes ONLY from the request's OWN
    /// <c>params.sessionId</c> (<see cref="SessionRequestPermissionParams.SessionId"/> /
    /// <see cref="ElicitationCreateParams.SessionId"/>), never from a runtime field closed over at
    /// wiring time. The prior shape took an <c>acpSessionId</c> parameter that
    /// <see cref="Capacitor.Cli.Daemon.Services.AcpHostedAgentRuntime"/> supplied as
    /// <c>_sessionId ?? ""</c> — a server→client request handled before <c>session/new</c>'s
    /// response assigned <c>_sessionId</c> (the read loop can start before that completes) forwarded
    /// an <see cref="AcpInteractionRequest"/> with <c>AcpSessionId == ""</c>, breaking server-side
    /// correlation. Trusting the request's own params instead removes that whole class of bug: the
    /// session id is authoritative and available at the exact moment the request itself exists.
    /// </summary>
    public async Task<JsonElement?> HandleAsync(AcpRequest request, CancellationToken ct) {
        // Cursor review flows launch with Cursor's own no-prompt flags. Any server→client request
        // therefore proves that the zero-interaction contract has regressed. Never turn it into a
        // local approval and never forward it to a human; signal the runtime to reap the reviewer.
        if (unattendedPolicy == AcpUnattendedInteractionPolicy.Fail) {
            LogUnexpectedUnattendedInteraction(agentId, request.Method);
            unexpectedUnattendedInteraction?.Invoke(request.Method);

            return request.Method is "session/request_permission" or "elicitation/create"
                ? CancelledResult()
                : null;
        }

        return request.Method switch {
            "session/request_permission" => await HandlePermissionAsync(request, ct).ConfigureAwait(false),
            "elicitation/create"         => await HandleElicitationAsync(request, ct).ConfigureAwait(false),
            _                            => null
        };
    }

    async Task<JsonElement?> HandlePermissionAsync(AcpRequest request, CancellationToken ct) {
        SessionRequestPermissionParams parsed;

        try {
            if (request.Params is not { } p)
                return CancelledResult();

            parsed = p.Deserialize(CapacitorJsonContext.Default.SessionRequestPermissionParams)
                ?? throw new JsonException("null params");
        } catch (JsonException ex) {
            logger.LogDebug(ex, "ACP: malformed session/request_permission params for agent {AgentId}", agentId);

            return CancelledResult();
        }

        // Qodo daemon-review Q2: the request's OWN params are the sole source of truth for
        // correlation — no resolvable session id at all means the server has no way to correlate
        // this interaction, so fail safe rather than forwarding an empty/placeholder id.
        if (string.IsNullOrEmpty(parsed.SessionId)) {
            logger.LogDebug("ACP: session/request_permission params carried no sessionId for agent {AgentId}; cannot correlate, defaulting to cancelled", agentId);

            return CancelledResult();
        }

        // Qodo daemon-review Q1 (fail-safe hole): System.Text.Json does NOT enforce
        // SessionRequestPermissionParams.Options' non-nullable C# annotation — an omitted OR
        // explicit-null `options` field on the wire (this shape is spec-derived, NOT
        // probe-confirmed; see docs/acp-probe-findings.md) deserializes to `parsed.Options == null`,
        // which used to NRE inside `.Select(...)` below and propagate all the way out to
        // AcpConnection.HandleServerRequestAsync's generic catch-all (-32603) instead of this
        // bridge's well-formed `cancelled`. Normalize once, up front, filtering out any null
        // element too (an options ARRAY with a null entry is equally unenforceable on the wire) —
        // the normalized array is used for BOTH the forwarded AcpInteractionRequest.Options and the
        // later MapPermissionDecision call, so there is exactly one empty-options code path
        // (MapPermissionDecision's existing `options.Count == 0 → cancelled` branch) rather than two
        // separate null-checks that could drift.
        var options = parsed.Options?.Where(o => o is not null).ToArray() ?? [];

        // Unattended reviewer: auto-approve a least-privilege allow option without a human, fail
        // closed when there's no unambiguous one. A trust decision — it does not inspect the tool.
        if (unattendedPolicy == AcpUnattendedInteractionPolicy.AutoApprove) {
            var chosen = TrySelectLeastPrivilegeAllow(options);

            if (chosen is not null) {
                // Pinned audit fields; tool title is untrusted (ToolCall is opaque). No path logged.
                LogUnattendedAutoApproved(agentId, chosen.Kind ?? "", TryGetToolTitle(parsed.ToolCall) ?? "(untitled)");

                return SelectedResult(chosen);
            }

            LogUnattendedAutoApproveDeclined(agentId, "no unambiguous allow option offered");

            return CancelledResult();
        }

        var interactionRequest = new AcpInteractionRequest(
            AgentId: agentId,
            AcpSessionId: parsed.SessionId,
            Kind: "permission",
            ToolName: TryGetToolTitle(parsed.ToolCall),
            // Qodo daemon-review Q1: a default/undefined JsonElement (e.g. `toolCall` itself omitted
            // from the wire frame — ToolCall is non-nullable on SessionRequestPermissionParams, but
            // again unenforced by System.Text.Json) must not be forwarded as a bare JsonElement,
            // which can throw when the caller later tries to serialize/inspect it. Undefined maps to
            // null; a genuine (even non-object) value forwards as-is, same as before.
            ToolInput: parsed.ToolCall.ValueKind == JsonValueKind.Undefined ? null : parsed.ToolCall,
            ToolCallId: TryGetToolCallId(parsed.ToolCall),
            Prompt: null,
            // Spec-review Finding 6: carry OptionId through to the server-facing DTO so a UI
            // decision can round-trip it back — Options: o.Name (Label) is now display-only.
            Options: options.Select(o => new AcpInteractionOption(o.OptionId, o.Name, null, o.Kind)).ToArray(),
            IsMultiSelect: false
        );

        AcpInteractionDecision decision;

        // Payload-free lifecycle logging — kind + eventual decision ONLY, never the tool
        // name/args/options content already captured above in interactionRequest.
        LogInteractionIssued(agentId, "permission");
        AcpMetrics.RecordBlockingRequest("permission");

        try {
            decision = await requestInteraction(interactionRequest, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Spec-review Finding 3(b): connection-closed / runtime-disposing / CT-cancelled while
            // this interaction is pending (AcpHostedAgentRuntime.DisposeAsync cancels its _cts,
            // which is the SAME token flowing through AcpConnection's read loop → HandleServerRequestAsync
            // → this bridge → RequestAcpInteractionAsync → PendingAcpInteractionRegistry.AwaitDecisionAsync,
            // whose own ct.Register callback removes the pending entry and TrySetCanceled()s it —
            // see Task B2). PRE-FIX, this exception type was explicitly excluded from the catch
            // below and propagated uncaught, so AcpConnection.HandleServerRequestAsync's own
            // catch-all converted it to a generic JSON-RPC "Internal error" (-32603) instead of the
            // well-formed ACP `cancelled` outcome every OTHER failure path in this bridge produces.
            // Best-effort: WriteServerResponseAsync may itself fail if the wire is already torn
            // down (AcpConnection's own catch around the write handles that silently) — this
            // bridge's only job is to make the ATTEMPTED response well-formed.
            logger.LogDebug("ACP: session/request_permission cancelled (connection closing) for agent {AgentId}; defaulting to cancelled", agentId);
            LogInteractionResolved(agentId, "permission", "cancelled");

            return CancelledResult();
        } catch (Exception ex) {
            logger.LogDebug(ex, "ACP: RequestAcpInteractionAsync threw for agent {AgentId}; defaulting to cancelled", agentId);
            LogInteractionResolved(agentId, "permission", "cancelled");

            return CancelledResult();
        }

        var mapped = MapPermissionDecision(decision, options);
        LogInteractionResolved(agentId, "permission", OutcomeLabel(mapped));

        return mapped;
    }

    async Task<JsonElement?> HandleElicitationAsync(AcpRequest request, CancellationToken ct) {
        // Unattended review-flow reviewer: there is no human to answer an elicitation, and a reviewer
        // should proceed on its own assumptions (and state them in its findings) rather than block.
        // Decline deterministically without routing anywhere.
        if (unattendedPolicy == AcpUnattendedInteractionPolicy.AutoApprove) {
            LogUnattendedElicitationDeclined(agentId);

            return CancelledResult();
        }

        // Never advertised in `initialize` (see AcpHostedAgentRuntime.StartAsync's minimal
        // ClientCapabilities, unchanged by this plan) — handled defensively in case a real agent
        // sends it unprompted, per R3's open question on whether Cursor uses a vendor-specific
        // elicitation shape at all.
        ElicitationCreateParams parsed;

        try {
            if (request.Params is not { } p)
                return CancelledResult();

            parsed = p.Deserialize(CapacitorJsonContext.Default.ElicitationCreateParams)
                ?? throw new JsonException("null params");
        } catch (JsonException ex) {
            logger.LogDebug(ex, "ACP: malformed elicitation/create params for agent {AgentId}", agentId);

            return CancelledResult();
        }

        // Qodo daemon-review Q2 — same session-id-from-params contract as HandlePermissionAsync.
        if (string.IsNullOrEmpty(parsed.SessionId)) {
            logger.LogDebug("ACP: elicitation/create params carried no sessionId for agent {AgentId}; cannot correlate, defaulting to cancelled", agentId);

            return CancelledResult();
        }

        // Qodo daemon-review Q1 — same null/omitted-array AND null-element normalization as
        // HandlePermissionAsync above (this path was already null-coalescing the whole array, but
        // not filtering individual null elements).
        var options = parsed.Options?.Where(o => o is not null).ToArray() ?? [];

        var interactionRequest = new AcpInteractionRequest(
            AgentId: agentId,
            AcpSessionId: parsed.SessionId,
            Kind: "elicitation",
            ToolName: null,
            ToolInput: null,
            ToolCallId: null,
            Prompt: parsed.Message,
            // Spec-review Finding 6: same OptionId carry-through as the permission path above.
            Options: options.Select(o => new AcpInteractionOption(o.OptionId, o.Name, null, o.Kind)).ToArray(),
            IsMultiSelect: false,
            // Spec-review Finding 1: forward RequestedSchema verbatim — this bridge never validates,
            // re-serializes, or renders it; the server (Task A1/A3) applies the actual 32KB/depth-8
            // caps and decides the generic-card fallback. Forwarding an oversized/malformed schema
            // here is safe: JsonElement.Deserialize already succeeded (it's syntactically valid JSON
            // by the time we have a JsonElement at all — a genuinely malformed JSON payload for the
            // whole params object was already caught by the JsonException handler above), and this
            // bridge does zero further inspection of RequestedSchema's shape.
            RequestedSchema: parsed.RequestedSchema
        );

        AcpInteractionDecision decision;

        LogInteractionIssued(agentId, "elicitation");
        AcpMetrics.RecordBlockingRequest("elicitation");

        try {
            decision = await requestInteraction(interactionRequest, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Spec-review Finding 3(b) — same disconnect handling as HandlePermissionAsync above.
            logger.LogDebug("ACP: elicitation/create cancelled (connection closing) for agent {AgentId}; defaulting to cancelled", agentId);
            LogInteractionResolved(agentId, "elicitation", "cancelled");

            return CancelledResult();
        } catch (Exception ex) {
            logger.LogDebug(ex, "ACP: RequestAcpInteractionAsync threw for elicitation on agent {AgentId}; defaulting to cancelled", agentId);
            LogInteractionResolved(agentId, "elicitation", "cancelled");

            return CancelledResult();
        }

        var mapped = MapPermissionDecision(decision, options);
        LogInteractionResolved(agentId, "elicitation", OutcomeLabel(mapped));

        return mapped;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "ACP: unattended reviewer {AgentId} emitted forbidden interaction request {Method}; terminating reviewer")]
    partial void LogUnexpectedUnattendedInteraction(string agentId, string method);

    /// <summary>
    /// Maps a resolved <see cref="AcpInteractionDecision"/> to the ACP outcome result shape.
    ///
    /// <b>Spec-review Finding 2 (security-critical, fail-safe-by-default):</b> this method uses
    /// an EXPLICIT ALLOWLIST of recognized affirmative outcome strings (<c>"allow"</c>/
    /// <c>"allow_once"</c>/<c>"allow_always"</c>/<c>"answered"</c> — the daemon does not reference
    /// the server-only <c>InterruptOutcomes</c> type per this plan's AOT/trim Global Constraints, so
    /// these are the literal string values of the SAME canonical vocabulary Task A2 documents, kept
    /// in sync by the <c>AffirmativeOutcomes</c> constant below) rather than a denylist of recognized
    /// negative outcomes with an "everything else selects" fallthrough. The PRIOR shape
    /// (<c>decision.Outcome is "deny" or "cancel" ? cancelled : selected</c>) meant ANY unrecognized
    /// outcome string — a typo (the canonical negative outcome is <c>"cancel"</c>, NOT
    /// <c>"cancelled"</c> — see Task A2's Interfaces note for exactly this drift happening once
    /// already in this plan), a future outcome vocabulary addition the daemon hasn't been updated
    /// for, or a malformed/corrupted decision — fell through to <c>selected</c>/<c>options[0]</c>,
    /// i.e. GRANTED permission for an outcome nobody explicitly asked to grant. Fail-safe now means:
    /// not on the allowlist → always <see cref="CancelledResult"/>, full stop, regardless of how many
    /// options were offered.
    ///
    /// <b>Fresh-review finding (this revision): a null <see cref="AcpInteractionDecision.SelectedOptionId"/>
    /// used to STILL fall back to the first offered option even for a recognized affirmative
    /// outcome</b> — an earlier draft of this fix treated "no id supplied" as "assume the human meant
    /// the first option," which is exactly the same class of bug spec-review Finding 2 exists to
    /// close: an ACP options request with two-or-more offered options (e.g. "Allow once" / "Deny")
    /// resolved via a decision-submit path that (for whatever reason — a bug, a stale client, a
    /// future code path that forgets to thread the id) omits <see cref="AcpInteractionDecision.SelectedOptionId"/>
    /// would silently grant `options[0]` — often "Allow" — regardless of what the human actually
    /// intended, or even whether the human ever saw the request. This method now FAILS CLOSED for
    /// that case too: for an ACP interaction that offered ANY options, an affirmative outcome with
    /// a null/unknown/unresolvable <see cref="AcpInteractionDecision.SelectedOptionId"/> ALWAYS maps
    /// to <c>cancelled</c> — there is NO first-option fallback anywhere in this method, full stop.
    /// The only way to get a <c>selected</c> result is an explicit, resolvable <c>SelectedOptionId</c>
    /// matching one of the offered <see cref="PermissionOptionDto.OptionId"/>s.
    ///
    /// For a recognized affirmative outcome (spec-review Finding 6): selects the option whose
    /// <see cref="PermissionOptionDto.OptionId"/> matches <see cref="AcpInteractionDecision.SelectedOptionId"/> —
    /// NEVER by re-matching <see cref="PermissionOptionDto.Name"/>/<see cref="AcpInteractionDecision.SelectedOptionLabel"/>,
    /// since duplicate or reordered option labels (which the ACP wire shape does not forbid) would
    /// otherwise resolve to the wrong option. Two cases, both fail-closed: a
    /// <see cref="AcpInteractionDecision.SelectedOptionId"/> that matches an offered
    /// <see cref="PermissionOptionDto.OptionId"/> → that option, <c>selected</c>; ANYTHING else — no
    /// id supplied at all (<see langword="null"/>), or an id that matches NONE of the offered
    /// options — → <c>cancelled</c> (an absent or unresolvable id, whether from a decision-submit
    /// path that forgot to echo one back or a correlation bug/stale/replayed decision, must never
    /// silently grant an arbitrary option instead).
    /// <c>"deny"</c>/<c>"cancel"</c>, no options offered at all, or ANY other outcome string not on
    /// the allowlist above, map to <c>cancelled</c>.
    /// </summary>
    static readonly HashSet<string> AffirmativeOutcomes = ["allow", "allow_once", "allow_always", "answered"];

    static JsonElement MapPermissionDecision(AcpInteractionDecision decision, IReadOnlyList<PermissionOptionDto> options) {
        // Fail-safe allowlist: only a RECOGNIZED affirmative outcome can ever produce "selected".
        // Everything else — deny, cancel, an unrecognized/typo'd string, or no options offered —
        // maps to cancelled. There is deliberately no "default: selected" path anywhere below.
        if (options.Count == 0 || !AffirmativeOutcomes.Contains(decision.Outcome))
            return CancelledResult()!.Value;

        // Fresh-review fix: resolve by OptionId ONLY, and FAIL CLOSED with no first-option
        // fallback for either "no id supplied" or "id supplied but unresolvable" — both map to
        // cancelled. There is deliberately no `options[0]` anywhere in this method:
        //   1. No SelectedOptionId supplied at all (null) → cancelled. An affirmative outcome with
        //      no specific option chosen is NOT treated as "assume the first option" — that was
        //      precisely the bug this fix closes (see the doc comment above).
        //   2. SelectedOptionId supplied and it matches an offered option's OptionId → that option.
        //   3. SelectedOptionId supplied but does NOT match any offered OptionId → treat as
        //      cancelled. An id that was explicitly set but doesn't resolve indicates a correlation
        //      bug or a stale/replayed decision — silently granting an unrelated option in that
        //      case would be worse than failing safe.
        // Cases 1 and 3 are DELIBERATELY THE SAME "cancelled" outcome, handled by the same
        // fallthrough below — there is no `options[0]`/first-option branch anywhere in this method.
        if (decision.SelectedOptionId is not { } optionId)
            return CancelledResult()!.Value;

        var matched = options.FirstOrDefault(o => o.OptionId == optionId);

        return matched is not null ? SelectedResult(matched) : CancelledResult()!.Value;
    }

    /// <summary>
    /// Auto-selects the least-privilege ALLOW option among the request's OFFERED options, by exact
    /// <see cref="PermissionOptionDto.Kind"/> — never by <see cref="PermissionOptionDto.Name"/>/label,
    /// which a hostile agent controls. Least-privilege = prefer a single <c>allow_once</c> over
    /// <c>allow_always</c>; exactly one <c>allow_once</c> wins even when <c>allow_always</c> options
    /// are also offered. Returns <see langword="null"/> (→ caller returns <c>cancelled</c>) when there
    /// is no allow option, the allow set is ambiguous (≥2 <c>allow_once</c>, or 0 <c>allow_once</c>
    /// with ≥2 <c>allow_always</c>), or the chosen option's <see cref="PermissionOptionDto.OptionId"/>
    /// is blank or not unique across the offered options. <c>OptionId</c> is non-nullable in C# but
    /// the wire deserializer enforces neither non-null nor uniqueness, so both are validated here — a
    /// blank or colliding id can't address an unambiguous option and echoing it risks selecting the
    /// wrong one server-side.
    /// </summary>
    static PermissionOptionDto? TrySelectLeastPrivilegeAllow(IReadOnlyList<PermissionOptionDto> options) {
        var once   = options.Where(o => o.Kind == "allow_once").ToArray();
        var always = options.Where(o => o.Kind == "allow_always").ToArray();

        var chosen =
            once.Length   == 1                          ? once[0]   :
            once.Length   == 0 && always.Length == 1    ? always[0] :
            null;

        if (chosen is null || string.IsNullOrWhiteSpace(chosen.OptionId)) return null;
        if (options.Count(o => o.OptionId == chosen.OptionId) != 1)       return null;

        return chosen;
    }

    static JsonElement SelectedResult(PermissionOptionDto chosen) =>
        JsonSerializer.SerializeToElement(
            new PermissionOutcomeResult(new PermissionOutcomeDto("selected", chosen.OptionId)),
            CapacitorJsonContext.Default.PermissionOutcomeResult);

    /// <summary>Internal (not private) so the ACP runtime's reconnect interaction router can answer
    /// a declined/uninstalled-incarnation request with the SAME well-formed cancelled outcome this
    /// bridge uses everywhere — one decline shape, never two that could drift.</summary>
    internal static JsonElement? CancelledResult() =>
        JsonSerializer.SerializeToElement(
            new PermissionOutcomeResult(new PermissionOutcomeDto("cancelled")),
            CapacitorJsonContext.Default.PermissionOutcomeResult);

    /// <summary>
    /// Pulls just the <c>"selected"</c>/<c>"cancelled"</c> discriminator back out of a mapped result
    /// for the "resolved" lifecycle log — never the chosen <c>optionId</c> or anything else
    /// payload-shaped.
    /// </summary>
    static string OutcomeLabel(JsonElement mapped) =>
        mapped.GetProperty("outcome").GetProperty("outcome").GetString() ?? "cancelled";

    static string? TryGetToolTitle(JsonElement toolCall) =>
        toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

    static string? TryGetToolCallId(JsonElement toolCall) =>
        toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("toolCallId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    // ── LoggerMessage source-generated methods ──────────────────────────────────────────────────
    // Payload-free by construction: kind ("permission"/"elicitation") and decision
    // ("selected"/"cancelled") ONLY — never tool name/args, prompt text, or option content.

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP blocking request issued: agentId={AgentId} kind={Kind}")]
    partial void LogInteractionIssued(string agentId, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP blocking request resolved: agentId={AgentId} kind={Kind} decision={Decision}")]
    partial void LogInteractionResolved(string agentId, string kind, string decision);

    // ── Unattended review-flow auto-approve audit ───────────────────────────────────────────────
    // Pinned fields only: agentId + the selected allow Kind, plus the tool title as EXPLICITLY
    // untrusted, agent-supplied context. Deliberately NO path field — the bridge has no trustworthy
    // path (ToolCall is opaque), so a path would be a fabricated assurance of what was touched.

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP unattended review-flow: auto-approved '{Kind}' permission for agent {AgentId} (tool title, untrusted: {ToolTitle})")]
    partial void LogUnattendedAutoApproved(string agentId, string kind, string toolTitle);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP unattended review-flow: declined permission for agent {AgentId} ({Reason}); returning cancelled")]
    partial void LogUnattendedAutoApproveDeclined(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP unattended review-flow: declined elicitation for agent {AgentId} (reviewers state assumptions in findings); returning cancelled")]
    partial void LogUnattendedElicitationDeclined(string agentId);
}
