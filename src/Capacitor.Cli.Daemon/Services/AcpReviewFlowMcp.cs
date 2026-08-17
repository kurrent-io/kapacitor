using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Builds the review-flow reviewer's <c>session/new</c> <see cref="AcpMcpServerSpec"/> list — the
/// ACP analogue of <see cref="ClaudeLauncher"/>'s PTY <c>BuildReviewFlowMcpConfig</c>: the
/// <c>kcap-flow-result</c> submit channel plus the reviewer allowlist.
///
/// Caller (<see cref="AcpHostedAgentRuntimeFactory"/>) supplies <c>allowlistServerIds</c>
/// already resolved and validated via <see cref="KcapMcpRegistry.TryResolveReviewFlowAllowlist"/>,
/// so every id here is a canonical, auto-approvable read-only server. Reads the launch's own
/// <c>ctx</c> fields (validated non-blank) rather than <c>DaemonConfig</c>, matching the factory.
/// </summary>
internal static class AcpReviewFlowMcp {
    internal static IReadOnlyList<AcpMcpServerSpec> Build(RuntimeStartContext ctx, IReadOnlyList<string> allowlistServerIds) {
        // The result channel: both env vars are mandatory (the flow-result MCP server exits when
        // KCAP_FLOW_AGENT_ID is absent). KCAP_FLOW_AGENT_ID is exclusive to it.
        // The channel is injected under the launch's WIRE name, which for an aliasing vendor is the
        // canonical id plus a per-launch GUID. It must be the same instance the argv's MCP allowlist is
        // built from: a second derivation here would produce a launch whose allowlist does not admit its
        // own result channel, and that failure is silent — the agent starts and can never report.
        var channelName = ctx.LaunchIdentity?.ResultChannelWireName
                       ?? KcapMcpRegistry.ReservedResultChannelId;

        // A launch whose HOME is not the daemon user's delivers through a daemon capability, never
        // KCAP_URL — that variable routes the channel at a token store it cannot reach. Mutually
        // exclusive on purpose: passing both leaves the broken path reachable as a silent fallback.
        //
        // Read from the ONE derived flag rather than re-deriving the causes here. Borrowed-ness is
        // only one of them (the Antigravity reviewer borrows nothing and still redirects HOME), and a
        // second derivation would be free to disagree with the mint that produced the capability.
        if (ctx.RequiresBrokeredResultDelivery && string.IsNullOrWhiteSpace(ctx.FlowResultCapabilityUrl))
            throw new InvalidOperationException(
                "Review launch cannot inject kcap-flow-result (missing result capability URL): its "
              + "HOME is redirected away from the token store, so the channel cannot authenticate.");

        var resultEnv = ctx.RequiresBrokeredResultDelivery
            ? new AcpMcpServerEnvVar[] { new("KCAP_FLOW_CAPABILITY_URL", ctx.FlowResultCapabilityUrl!),
                                         new("KCAP_FLOW_AGENT_ID", ctx.AgentId) }
            : [new("KCAP_URL", ctx.ServerUrl!), new("KCAP_FLOW_AGENT_ID", ctx.AgentId)];

        var servers = new List<AcpMcpServerSpec> {
            new(channelName, ctx.CapacitorPath, ["mcp", "flow-result"], resultEnv)
        };

        foreach (var id in allowlistServerIds) {
            // id is a validated canonical id, so Resolve is non-null. Allowlist servers get KCAP_URL only.
            // Injected under the launch's wire name: for an aliasing vendor the canonical id is a fixed,
            // public literal the reviewed repository could declare a server under, so admitting it in the
            // vendor's name gate would be the same impersonation hole the result channel's alias closes.
            var descriptor = KcapMcpRegistry.Resolve(id)!;
            servers.Add(new(WireName(ctx, descriptor.Id), ctx.CapacitorPath, descriptor.Args,
                [new("KCAP_URL", ctx.ServerUrl!)]));
        }

        if (ctx.IsBorrowedSnapshot) {
            if (string.IsNullOrWhiteSpace(ctx.ReviewContextCapabilityUrl))
                throw new InvalidOperationException(
                    "Borrowed-snapshot review cannot inject kcap-review-context (missing capability URL).");
            servers.Add(new(
                WireName(ctx, "kcap-review-context"), ctx.CapacitorPath, ["mcp", "review"],
                [new("KCAP_REVIEW_CONTEXT_MODE", "1"),
                 new("KCAP_REVIEW_CONTEXT_URL", ctx.ReviewContextCapabilityUrl)]));
        }

        return servers;
    }

    // Same fallback shape as channelName above: a context that never went through the factory (a direct
    // test call) keeps canonical names, and every non-aliasing vendor's identity returns the input as-is.
    static string WireName(RuntimeStartContext ctx, string canonicalId) =>
        ctx.LaunchIdentity?.AllowlistWireName(canonicalId) ?? canonicalId;
}
