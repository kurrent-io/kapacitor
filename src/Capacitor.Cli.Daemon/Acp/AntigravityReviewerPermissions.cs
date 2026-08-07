using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The <c>permissions.allow</c> rules an unattended Antigravity reviewer's per-launch home grants —
/// the settings-file half of <see cref="AntigravityReviewerHome"/>.
///
/// <para><b>Why a reviewer needs any grant at all.</b> <c>agy -p</c> has no human to answer a tool
/// confirmation, so it auto-denies every one it raises. The reviewer's result channel IS an MCP tool,
/// which made the one call a round depends on the one call print mode refused: measured, the
/// conversation stops at <c>PLANNER_RESPONSE</c> with no <c>TOOL_CALL</c>, agy's own log reads
/// <c>permission check failed for mcp "kcap-flow-result/submit_review_result": user denied permission
/// for mcp</c>, and the round then hangs until the flow times out. agy names the remedy itself: an
/// allow-rule under <c>permissions.allow</c>.</para>
///
/// <para><b>Not <c>--dangerously-skip-permissions</c>.</b> That flag is the reviewer's whole read
/// boundary — measured on agy 1.1.10, with it an absolute <c>view_file</c> OUTSIDE the workspace
/// succeeds and without it the same read is refused with a typed error (see
/// <c>AntigravityHostedAgentRuntimeFactory.BuildTurnPsi</c>). Passing it to buy delivery would trade a
/// containment property for a delivery fix. An allow-rule is scoped to the named tool and moves
/// nothing else.</para>
///
/// <para><b>Measured, not inferred, and the binary carries a string that contradicts it.</b> agy also
/// ships <c>"…auto-denied. Settings allow-rules do not apply; re-run with
/// --dangerously-skip-permissions…"</c>. That notice is NOT the MCP path: a probe against agy 1.1.13
/// under an isolated home with one stub MCP server ran the exact rule below and the tool call reached
/// the server, while the same home without it produced the auto-denial. The rule form is agy's own —
/// its binary ships <c>mcp(chrome_devtools/evaluate_script)</c> and <c>mcp(chrome-devtools/*)</c>.</para>
///
/// <para><b>Exact pairs, never a wildcard.</b> <c>mcp(kcap-flow-result/*)</c> would grant whatever the
/// channel serves next without a reviewed decision, and the exact pair was measured to work — so there
/// is no "the narrow form does not function" case buying the wider one.</para>
///
/// <para><b>Where the tool names come from.</b> The same two authoritative tables every other
/// unattended reviewer's approval surface is built from — <c>KcapMcpRegistry</c>'s
/// <c>ReservedResultChannelUnattendedSafeTools</c> and <c>ReviewFlowUnattendedSafeTools</c>. Naming
/// tools here instead would be a second classification of the same decision, and the one that drifts is
/// silent: a reviewer granted a tool it may not call, or refused one it needs.</para>
/// </summary>
internal static class AntigravityReviewerPermissions {
    /// <summary>
    /// The ordered allow-rules for <paramref name="injected"/> — server order as injected, tools
    /// ordinal-ordered, so the file a launch writes is deterministic.
    ///
    /// <para>Throws when an injected server has no entry in the authoritative tables, rather than
    /// writing a home whose reviewer would be auto-denied on that server's first call. A wedged round
    /// reports nothing an operator can act on; a refused launch names the server.</para>
    /// </summary>
    internal static IReadOnlyList<string> AllowRulesFor(IReadOnlyList<AcpMcpServerSpec> injected) {
        var rules = new List<string>();

        foreach (var server in injected) {
            foreach (var tool in ToolsFor(server.Name)) rules.Add(Rule(server.Name, tool));
        }

        return rules;
    }

    /// <summary>One rule in agy's own syntax. The tool identity is exactly what agy logs on a denial
    /// (<c>kcap-flow-result/submit_review_result</c>), so the rule and the thing it admits are the same
    /// string.</summary>
    internal static string Rule(string server, string tool) => $"mcp({server}/{tool})";

    static IEnumerable<string> ToolsFor(string serverName) {
        // The result channel serves more than the submit tool — send_flow_message is unattended-safe
        // too, and a reviewer that cannot call it loses the out-of-band message lane silently. Ordinal:
        // this feeds an auto-approve, so a case variant of a safe name is not the safe name.
        if (string.Equals(serverName, KcapMcpRegistry.ReservedResultChannelId, StringComparison.Ordinal))
            return KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Order(StringComparer.Ordinal);

        if (KcapMcpRegistry.ReviewFlowUnattendedSafeTools.TryGetValue(serverName, out var tools))
            return tools.Order(StringComparer.Ordinal);

        // Reached by an injected server this vendor is not supposed to get: a borrowed-snapshot
        // kcap-review-context (the factory refuses a borrowed workspace before any home is created), or
        // a per-launch ALIASED name if this vendor ever starts aliasing — agy's MCP surface is the file
        // the launch writes rather than a name-matched allowlist, so it deliberately does not today.
        throw new InvalidOperationException(
            $"antigravity_reviewer_permission_unknown_server: injected MCP server '{serverName}' has no "
          + "entry in the review-flow unattended-safe tool table, so no allow-rule can be written for it. "
          + "Failing the launch rather than handing a reviewer a server whose first tool call headless "
          + "mode would auto-deny.");
    }
}
