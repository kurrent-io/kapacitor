using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Interactive approval bridge for hosted Codex on app-server. A server-initiated
/// <c>*/requestApproval</c> request is forwarded to the user through the shared ACP interaction
/// channel (<c>requestInteraction</c>) and the user's decision is mapped back to the codex wire shape.
/// Mirrors <see cref="Acp.AcpInteractionBridge"/>'s interactive forward path.
///
/// <para>Every uncertain outcome fails CLOSED — no params, no thread id, a timeout, a thrown delegate,
/// a non-affirmative or unresolvable decision all deny. Only a recognized affirmative outcome carrying
/// a known option id (<c>accept</c>/<c>acceptForSession</c>) grants. The response is always a valid
/// protocol body, never a JSON-RPC error (a throw would become the connection's <c>-32603</c>, whose
/// turn effect is Codex's to define).</para>
///
/// <para>The reviewer path (<c>approvalPolicy: never</c>) does NOT use this bridge — it keeps the
/// always-decline handler, since a request there is a protocol violation, not a user prompt.</para>
/// </summary>
internal sealed class CodexApprovalBridge {
    readonly Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> _requestInteraction;
    readonly string   _agentId;
    readonly ILogger  _logger;
    readonly TimeSpan _timeout;

    // The option ids ARE the codex decision strings, so a resolved SelectedOptionId maps straight to
    // {decision: id}. Kinds let the server render allow-once vs allow-for-session affordances.
    static readonly AcpInteractionOption[] ApprovalOptions = [
        new("accept",           "Allow",                  null, "allow_once"),
        new("acceptForSession", "Allow for this session", null, "allow_always"),
        new("decline",          "Deny",                   null, "reject_once"),
    ];

    // Fail-safe allowlist (mirrors AcpInteractionBridge.AffirmativeOutcomes): only these outcomes can
    // grant; every other string denies. Keep in sync with the server's decision vocabulary.
    static readonly HashSet<string> AffirmativeOutcomes = ["allow", "allow_once", "allow_always", "answered"];

    public CodexApprovalBridge(
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> requestInteraction,
            string agentId, ILogger logger, TimeSpan timeout) {
        _requestInteraction = requestInteraction;
        _agentId            = agentId;
        _logger             = logger;
        _timeout            = timeout;
    }

    public async Task<JsonElement?> HandleAsync(AcpRequest request, CancellationToken ct) {
        var method = request.Method;

        // Only approval requests are forwarded to the user; elicitation / requestUserInput / any other
        // server request keeps the always-decline shapes (interactive elicitation is out of scope here).
        if (!method.EndsWith("/requestApproval", StringComparison.Ordinal))
            return DeclineNonApproval(method);

        // The permissions grant is a DIFFERENT response shape ({permissions,scope}) with no decision
        // enum — a bare {decision:"decline"} is malformed for it, so route it separately. Match on the
        // ITEM-TYPE segment (item/<type>/requestApproval), not a whole-method substring, so an unrelated
        // path segment can't misroute a command/file-change approval into the grant shape.
        var itemType = ItemType(method);
        var isPermissionsGrant = itemType.Contains("permission", StringComparison.OrdinalIgnoreCase);

        JsonElement Deny() => isPermissionsGrant ? EmptyGrant() : DeclineDecision();

        // Correlate on the request's OWN params (never a closed-over field): no resolvable thread id
        // means the server cannot correlate this interaction — fail closed.
        var threadId = TryGetString(request.Params, "threadId");
        if (string.IsNullOrEmpty(threadId)) {
            _logger.LogWarning("codex approval: {Method} carried no threadId; cannot correlate, denying.", method);
            return Deny();
        }

        var interaction = new AcpInteractionRequest(
            AgentId:      _agentId,
            AcpSessionId: threadId,
            Kind:         "permission",
            ToolName:     itemType,
            ToolInput:    request.Params,
            ToolCallId:   TryGetString(request.Params, "itemId"),
            Prompt:       null,
            Options:      ApprovalOptions,
            IsMultiSelect: false);

        AcpInteractionDecision decision;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try {
            decision = await _requestInteraction(interaction, timeoutCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Timeout, connection close, or runtime dispose — deny (fail closed), never an error frame.
            _logger.LogDebug("codex approval: {Method} cancelled or timed out; denying.", method);
            return Deny();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "codex approval: requestInteraction threw for {Method}; denying.", method);
            return Deny();
        }

        return isPermissionsGrant
            ? MapPermissionsGrant(decision, request.Params)
            : MapDecision(decision);
    }

    // command/fileChange requestApproval → {decision: "accept"|"acceptForSession"|"decline"}.
    static JsonElement MapDecision(AcpInteractionDecision decision) {
        if (AffirmativeOutcomes.Contains(decision.Outcome)
         && decision.SelectedOptionId is "accept" or "acceptForSession") {
            return ToElement(new JsonObject { ["decision"] = decision.SelectedOptionId });
        }

        return DeclineDecision();
    }

    // permissions requestApproval → {permissions, scope}. Grant echoes the requested profile ONLY when
    // it is a well-formed object; an affirmative decision over an absent/non-object profile falls to the
    // empty grant (deny) rather than an affirmative-scoped empty one — never grant a profile we can't
    // read back. A denial is likewise a valid EMPTY grant (the always-decline bridge emits
    // {decision:decline}, which is malformed for this method).
    static JsonElement MapPermissionsGrant(AcpInteractionDecision decision, JsonElement? requestParams) {
        if (AffirmativeOutcomes.Contains(decision.Outcome)
         && decision.SelectedOptionId is "accept" or "acceptForSession"
         && TryCloneObject(requestParams, "permissions") is { } granted) {
            var scope = decision.SelectedOptionId == "acceptForSession" ? "session" : "turn";
            return ToElement(new JsonObject { ["permissions"] = granted, ["scope"] = scope });
        }

        return EmptyGrant();
    }

    static JsonElement DeclineDecision() => ToElement(new JsonObject { ["decision"] = "decline" });

    static JsonElement EmptyGrant() =>
        ToElement(new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" });

    // Non-approval server requests keep the pre-existing always-decline shapes.
    static JsonElement DeclineNonApproval(string method) {
        var isElicitation = method.Contains("elicitation", StringComparison.Ordinal)
                         || method.Contains("requestUserInput", StringComparison.Ordinal);
        return ToElement(isElicitation
            ? new JsonObject { ["action"]   = "decline" }
            : new JsonObject { ["decision"] = "decline" });
    }

    // The item type from the method (item/<type>/requestApproval) — used both to route the permissions
    // grant and as a short, non-identifying prompt label. Never the params content.
    static string ItemType(string method) {
        var parts = method.Split('/');
        return parts.Length >= 2 ? parts[^2] : method;
    }

    static string? TryGetString(JsonElement? element, string property) =>
        element is { } e && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Deep-copy a named OBJECT property out of the params (detached from the request frame), or null when
    // it is absent or not an object — the caller must decide what a missing profile means.
    static JsonNode? TryCloneObject(JsonElement? element, string property) =>
        element is { } e && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(value.GetRawText())
            : null;

    // JsonNode → JsonElement without reflection (AOT-safe), matching the runtime's own helper.
    static JsonElement ToElement(JsonNode node) =>
        JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}
