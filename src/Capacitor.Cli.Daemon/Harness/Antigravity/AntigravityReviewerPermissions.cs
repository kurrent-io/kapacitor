using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Harness.Antigravity;

/// <summary>
/// The <c>permissions.allow</c> rules an unattended Antigravity reviewer's per-launch home grants —
/// the settings-file half of <see cref="AntigravityReviewerHome"/>.
///
/// <para><c>agy -p</c> auto-denies every tool confirmation it raises, and the reviewer's result channel
/// IS an MCP tool — so the one call a round waits for was the one print mode refused, and the round hung
/// to the flow timeout.</para>
///
/// <para><b>agy's binary ships a string saying this cannot work</b> — <c>"…auto-denied. Settings
/// allow-rules do not apply; re-run with --dangerously-skip-permissions…"</c>. It does not describe the
/// MCP path: probed on 1.1.13 under an isolated home, the exact rule below let the call through and its
/// absence reproduced the denial. Believe the probe, not the string; the rule form is agy's own
/// (<c>mcp(chrome-devtools/*)</c> ships in the binary).</para>
///
/// <para>The flag is not the alternative: it is the reviewer's whole read boundary (measured on 1.1.10 —
/// with it an absolute out-of-workspace <c>view_file</c> succeeds), so it would trade containment for
/// delivery. Rules are exact pairs, never <c>mcp(server/*)</c>, since the narrow form was measured to
/// work and the wide one would grant whatever the channel serves next.</para>
///
/// <para>Tool names come from <c>KcapMcpRegistry</c>'s tables rather than being listed here — a second
/// list would drift silently, granting a tool the reviewer may not call or withholding one it needs.</para>
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
