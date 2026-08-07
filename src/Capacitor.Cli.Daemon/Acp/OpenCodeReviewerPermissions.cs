using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// The <c>OPENCODE_PERMISSION</c> document for one unattended OpenCode reviewer launch — this
/// vendor's equivalent of <see cref="KiroReviewerTrustList"/>, in env form because OpenCode has no
/// trust argv.
///
/// <para><b>Deny-all first, then exactly what a reviewer needs.</b> <c>{"*":"allow"}</c> also produces
/// a frame-free launch and is one line shorter, and it would give the reviewer shell and write tools —
/// broader than Kiro's read-only trust list, on a posture nobody asked for. Measured
/// (<c>docs/probes/2026-08-07-opencode-acp/</c> §6b) with the control that makes the claim mean
/// something: flipping <c>bash</c> to <c>allow</c> makes the shell actually run, so the denial is
/// attributable to the rule rather than to a model that chose not to shell out.</para>
///
/// <para><b>The injected-server entry is load-bearing, not belt-and-braces.</b> OpenCode presents an
/// injected MCP tool to the model FLATTENED as <c>{serverName}_{toolName}</c>, and those flattened
/// names go through the same permission table as native tools. With the entry removed and everything
/// else identical, the result channel is absent from the model's toolset entirely ("no such tool… I
/// only have <c>glob</c>, <c>grep</c>, and <c>read</c>") — so a reviewer could not report at all. That
/// control was run precisely because the alternative explanation (MCP tools bypassing permissions)
/// would have made this entry decorative while reading as protection.</para>
///
/// <para><b>What the mechanism actually is.</b> A denied tool is ABSENT from the model's surface, not
/// refused when called — which is why every measured arm raises zero
/// <c>session/request_permission</c> frames, and why the descriptor can honestly carry
/// <see cref="AcpUnattendedInteractionPolicy.Fail"/>: on this posture a frame means the launch
/// contract regressed.</para>
///
/// <para><b>Built from the injected specs, never from server ids.</b> Same hazard
/// <see cref="KiroReviewerTrustList"/> documents: a second derivation of the same names fails
/// SILENTLY — the reviewer starts normally and can never call its own channel.</para>
///
/// <para><b>What this does NOT contain, and why it is a consent decision rather than a gap.</b>
/// <c>read</c>/<c>grep</c>/<c>glob</c>/<c>list</c> are not path-scoped, so a reviewer can read
/// anything the daemon user can. Identical to Kiro's accepted residual, and gated the same way by
/// <see cref="OpenCodeReviewerCapability"/> — not something this table can bound.</para>
/// </summary>
internal static class OpenCodeReviewerPermissions {
    /// <summary>
    /// Native tools a reviewer needs and nothing more. Never <c>write</c>, <c>edit</c>, <c>patch</c>
    /// or <c>bash</c>.
    ///
    /// <para>These are real tool names on opencode 1.18.9. A misspelling degrades toward DENIED here
    /// (the entry simply matches nothing and <c>"*": "deny"</c> stands), which is the safe direction —
    /// a reviewer that cannot read is a visible failure, unlike Kiro's trust list where a typo
    /// silently widened nothing and narrowed nothing. The shipped list is still asserted by test
    /// rather than eyeballed.</para>
    /// </summary>
    internal static readonly ImmutableArray<string> ReadTools = ["read", "grep", "glob", "list"];

    /// <summary>
    /// Tools that must never appear as <c>allow</c>. Asserted by test against the built document, so a
    /// future edit that widens the posture goes red rather than shipping.
    ///
    /// <para><c>skill</c> is on this list for a reason beyond tool hygiene: OpenCode reads skills from
    /// <c>~/.agents/skills</c> and <c>~/.claude</c>, which are HOME-relative and therefore NOT
    /// suppressed by the isolated config dir — so the review-flows skill is visible to a reviewer, the
    /// derail hazard where a reviewer tries to START a flow instead of submitting its result. Denying
    /// the <c>skill</c> tool closes that structurally rather than by wording: OpenCode omits the skills
    /// section from its system prompt entirely when the tool is disabled, so there is nothing for a
    /// reviewer to be derailed by.</para>
    /// </summary>
    internal static readonly ImmutableArray<string> ForbiddenTools =
        ["write", "edit", "patch", "bash", "webfetch", "websearch", "task", "skill", "todowrite"];

    /// <summary>
    /// The JSON value for <c>OPENCODE_PERMISSION</c>: <c>"*": "deny"</c>, the read family, and one
    /// <c>{server}_*</c> entry per injected server.
    ///
    /// <para>Built with <c>(JsonNode?)</c> string casts rather than collection expressions or
    /// <c>JsonValue.Create</c> — the latter lower to a generic <c>Add&lt;T&gt;</c> that trips
    /// NativeAOT (IL3050), the same rule <see cref="AntigravityReviewerHome"/> follows.</para>
    /// </summary>
    internal static string Build(IReadOnlyList<AcpMcpServerSpec> injected) {
        var permission = new JsonObject {
            // FIRST, and the reason the rest of this document is an allowlist rather than a
            // blocklist: a blocklist would admit every tool a future OpenCode release adds.
            ["*"] = (JsonNode?)"deny"
        };

        foreach (var tool in ReadTools)
            permission[tool] = (JsonNode?)"allow";

        foreach (var server in injected) {
            if (string.IsNullOrWhiteSpace(server.Name))
                throw new InvalidOperationException(
                    "opencode_reviewer_permission_unnamed_server: an injected MCP server has no wire "
                  + "name, so its flattened tool names cannot be admitted. Refusing rather than "
                  + "launching a reviewer that cannot reach its own result channel.");

            permission[$"{server.Name}_*"] = (JsonNode?)"allow";
        }

        return permission.ToJsonString();
    }
}
