using System.Collections.Immutable;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Harness.Kiro;

/// <summary>
/// The <c>--trust-tools</c> value for one unattended Kiro reviewer launch.
///
/// <para><b>Why this is derived per launch rather than fixed in the descriptor.</b> A review can
/// carry an MCP allowlist, and those servers are injected into <c>session/new</c> alongside the
/// result channel. Their tools appear in no fixed list, so under the <c>Fail</c> interaction policy
/// every call raises a permission frame and kills the round — for exactly the reviews that need
/// repository context most, and it would present as a vendor bug rather than as our own. Gemini never
/// hit this because its blanket approval mode trusts whatever is injected; SCOPING the surface is
/// what creates the obligation to enumerate it.</para>
///
/// <para><b>One derivation, from the injected specs themselves.</b> Building this from server IDS
/// instead of from the built <see cref="AcpMcpServerSpec"/> list would be a second derivation of the
/// same names, and that failure is silent: the reviewer starts normally and can never call its own
/// channel. <see cref="LaunchIdentity"/> exists for this exact hazard.</para>
///
/// <para><b>Measured names.</b> <c>fs_read</c> and <c>thinking</c> are real native tool names on
/// kiro-cli 2.16.0; a misspelling is a WARNING, not an error, and degrades silently to "nothing
/// trusted", which is why the shipped list is asserted by test rather than eyeballed.
/// <c>fs_write</c> and <c>execute_bash</c> are deliberately absent — trusting shell in particular
/// would let a write execute with no permission frame at all, making the read-only posture
/// fiction.</para>
/// </summary>
internal static class KiroReviewerTrustList {
    /// <summary>Native tools a reviewer needs and nothing more. Never <c>fs_write</c>, never
    /// <c>execute_bash</c>.</summary>
    internal static readonly ImmutableArray<string> NativeTools = ["fs_read", "thinking"];

    /// <summary>
    /// The comma-joined value. Throws when an injected server has no entry in the authoritative
    /// safe-tool table — failing the launch rather than injecting a server whose tools cannot be
    /// trusted, which would wedge the round at the first call instead.
    /// </summary>
    internal static string Build(IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) =>
        string.Join(",", NativeTools.Concat(NamespacedEntries(injected, identity)));

    /// <summary>
    /// The <c>@server/tool</c> half of the trust list. Public to the admission policy so the set it
    /// approves and the set the launch trusts are ONE derivation — deriving them separately is how a
    /// reviewer ends up trusted to call a tool the policy then refuses to approve, or the reverse.
    /// </summary>
    internal static IEnumerable<string> NamespacedEntries(
            IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) {
        foreach (var server in injected) {
            foreach (var tool in ToolsFor(server, identity))
                yield return $"@{server.Name}/{tool}";
        }
    }

    /// <summary>Builds the whole <c>--trust-tools</c> argv pair, so the descriptor carries one
    /// callable rather than a flag name a caller could get wrong.</summary>
    internal static ImmutableArray<string> BuildArgv(
            IReadOnlyList<AcpMcpServerSpec> injected, LaunchIdentity identity) =>
        ["--trust-tools", Build(injected, identity)];

    static IEnumerable<string> ToolsFor(AcpMcpServerSpec server, LaunchIdentity identity) {
        // The result channel serves MORE than the submit tool — send_flow_message is unattended-safe
        // too, and a reviewer that cannot call it loses the out-of-band message lane. Read the
        // catalog rather than naming tools here, so a tool added there is trusted in the same change.
        if (server.Name == identity.ResultChannelWireName)
            return KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Order(StringComparer.Ordinal);

        // Reverse the launch's own aliasing to recover the canonical id. Matching on the wire name is
        // what keeps this tied to the identity the specs were built from: a name this identity did not
        // produce resolves to nothing and fails the launch.
        var canonical = KcapMcpRegistry.ReviewFlowUnattendedSafeTools.Keys
            .FirstOrDefault(id => string.Equals(
                identity.AllowlistWireName(id), server.Name, StringComparison.Ordinal));

        if (canonical is null)
            throw new InvalidOperationException(
                $"kiro_reviewer_trust_list_unknown_server: injected MCP server '{server.Name}' has no entry "
              + "in the review-flow unattended-safe tool table, so its tools cannot be trusted. Failing the "
              + "launch rather than injecting a server whose first tool call would wedge the round.");

        return KcapMcpRegistry.ReviewFlowUnattendedSafeTools[canonical].Order(StringComparer.Ordinal);
    }
}
