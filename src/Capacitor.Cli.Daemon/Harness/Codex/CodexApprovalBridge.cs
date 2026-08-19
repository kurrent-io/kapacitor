using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Interactive approval bridge for hosted Codex on app-server: a server-initiated
/// <c>*/requestApproval</c> is forwarded to the user through the shared ACP interaction channel and the
/// user's decision is mapped back to the codex wire shape. Mirrors <see cref="Acp.AcpInteractionBridge"/>.
///
/// <para>Fail-closed by construction: only a recognized affirmative outcome carrying a known option id
/// grants; every other case (no params, no thread id, timeout, thrown delegate, non-affirmative or
/// unresolvable decision) denies, and the response is always a valid protocol body — never a JSON-RPC
/// error, which the connection would map to <c>-32603</c>. The reviewer path (<c>approvalPolicy:never</c>)
/// does not use this bridge.</para>
/// </summary>
internal sealed class CodexApprovalBridge {
    readonly Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> _requestInteraction;
    readonly string   _agentId;
    readonly ILogger  _logger;
    readonly TimeSpan _timeout;

    // The option ids ARE the codex decision strings, so a resolved SelectedOptionId maps straight to
    // {decision: id}; Kind drives the allow-once / allow-for-session affordances server-side.
    static readonly AcpInteractionOption[] ApprovalOptions = [
        new("accept",           "Allow",                  null, "allow_once"),
        new("acceptForSession", "Allow for this session", null, "allow_always"),
        new("decline",          "Deny",                   null, "reject_once"),
    ];

    // Approval grants only on an allow-family outcome. Excludes the ACP "answered" outcome — that is an
    // elicitation result, and this bridge handles approvals only.
    static readonly HashSet<string> AffirmativeOutcomes = ["allow", "allow_once", "allow_always"];

    // Bounds CancelAfter (which throws above ~24.8 days) and keeps a misconfigured value sane.
    static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(1);
    static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    public CodexApprovalBridge(
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> requestInteraction,
            string agentId, ILogger logger, TimeSpan timeout) {
        _requestInteraction = requestInteraction;
        _agentId            = agentId;
        _logger             = logger;
        _timeout            = timeout < MinTimeout ? MinTimeout : timeout > MaxTimeout ? MaxTimeout : timeout;
    }

    public async Task<JsonElement?> HandleAsync(AcpRequest request, CancellationToken ct) {
        var method = request.Method;

        // Non-approval server requests (elicitation / requestUserInput / unknown) keep the always-decline
        // shapes; interactive elicitation is out of scope here.
        if (!method.EndsWith("/requestApproval", StringComparison.Ordinal))
            return DeclineNonApproval(method);

        // The permissions grant is a distinct {permissions,scope} response with no decision enum. Route it
        // by EXACT item-type segment (item/<type>/requestApproval), so a command-type item whose name
        // merely contains "permission" can't misroute into the grant shape.
        var itemType = ItemType(method);
        var isPermissionsGrant = string.Equals(itemType, "permissions", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(itemType, "permission",  StringComparison.OrdinalIgnoreCase);

        JsonElement Deny() => isPermissionsGrant ? EmptyGrant() : DeclineDecision();

        try {
            // Correlate on the request's OWN params: no thread id means the server can't correlate — deny.
            var threadId = request.Params?.Str("threadId");
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
                ToolCallId:   request.Params?.Str("itemId"),
                Prompt:       null,
                Options:      ApprovalOptions,
                IsMultiSelect: false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var decision = await _requestInteraction(interaction, timeoutCts.Token).ConfigureAwait(false);

            return isPermissionsGrant ? MapPermissionsGrant(decision, request.Params) : MapDecision(decision);
        } catch (OperationCanceledException) {
            _logger.LogDebug("codex approval: {Method} cancelled or timed out; denying.", method);
            return Deny();
        } catch (Exception ex) {
            // The handler must never throw — the connection maps a throw to -32603. Deny with a valid body.
            _logger.LogWarning(ex, "codex approval: unexpected failure handling {Method}; denying.", method);
            return Deny();
        }
    }

    static JsonElement MapDecision(AcpInteractionDecision decision) =>
        AffirmativeOutcomes.Contains(decision.Outcome) && decision.SelectedOptionId is "accept" or "acceptForSession"
            ? ToElement(new JsonObject { ["decision"] = decision.SelectedOptionId })
            : DeclineDecision();

    // Grant echoes the requested profile only when it is a well-formed object — an affirmative decision
    // over an absent/non-object profile falls to the empty grant rather than an affirmative-scoped empty
    // one (never grant a profile we can't read back). Deny is a valid empty grant.
    static JsonElement MapPermissionsGrant(AcpInteractionDecision decision, JsonElement? requestParams) {
        if (AffirmativeOutcomes.Contains(decision.Outcome)
         && decision.SelectedOptionId is "accept" or "acceptForSession"
         && requestParams?.Obj("permissions") is { } profile
         && JsonNode.Parse(profile.GetRawText()) is { } granted) {
            var scope = decision.SelectedOptionId == "acceptForSession" ? "session" : "turn";
            return ToElement(new JsonObject { ["permissions"] = granted, ["scope"] = scope });
        }

        return EmptyGrant();
    }

    static JsonElement DeclineDecision() => ToElement(new JsonObject { ["decision"] = "decline" });

    static JsonElement EmptyGrant() =>
        ToElement(new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" });

    static JsonElement DeclineNonApproval(string method) {
        var isElicitation = method.Contains("elicitation", StringComparison.Ordinal)
                         || method.Contains("requestUserInput", StringComparison.Ordinal);
        return ToElement(isElicitation
            ? new JsonObject { ["action"]   = "decline" }
            : new JsonObject { ["decision"] = "decline" });
    }

    // The item type from item/<type>/requestApproval — routes the permissions grant and labels the prompt.
    static string ItemType(string method) {
        var parts = method.Split('/');
        return parts.Length >= 2 ? parts[^2] : method;
    }

    // JsonNode → JsonElement without reflection (AOT-safe). The clone is independent of the parsed
    // document's pooled buffers, so the document is disposed rather than leaked.
    static JsonElement ToElement(JsonNode node) {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}
