using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Builds the review-flow reviewer's <c>session/new</c> <see cref="AcpMcpServerSpec"/> list — the
/// ACP analogue of <see cref="ClaudeLauncher"/>'s PTY <c>BuildReviewFlowMcpConfig</c>: the
/// <c>kcap-flow-result</c> submit channel plus the reviewer allowlist.
///
/// Caller (<see cref="AcpHostedAgentRuntimeFactory"/>) supplies <paramref name="allowlistServerIds"/>
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

        var servers = new List<AcpMcpServerSpec> {
            new(channelName, ctx.CapacitorPath, ["mcp", "flow-result"],
                [new("KCAP_URL", ctx.ServerUrl!), new("KCAP_FLOW_AGENT_ID", ctx.AgentId)])
        };

        foreach (var id in allowlistServerIds) {
            // id is a validated canonical id, so Resolve is non-null. Allowlist servers get KCAP_URL only.
            var descriptor = KcapMcpRegistry.Resolve(id)!;
            servers.Add(new(descriptor.Id, ctx.CapacitorPath, descriptor.Args, [new("KCAP_URL", ctx.ServerUrl!)]));
        }

        if (ctx.IsBorrowedSnapshot) {
            if (string.IsNullOrWhiteSpace(ctx.ReviewContextCapabilityUrl))
                throw new InvalidOperationException(
                    "Borrowed-snapshot review cannot inject kcap-review-context (missing capability URL).");
            servers.Add(new(
                "kcap-review-context", ctx.CapacitorPath, ["mcp", "review"],
                [new("KCAP_REVIEW_CONTEXT_MODE", "1"),
                 new("KCAP_REVIEW_CONTEXT_URL", ctx.ReviewContextCapabilityUrl)]));
        }

        return servers;
    }
}
