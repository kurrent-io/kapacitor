using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Harness.Kiro;

namespace Capacitor.Cli.Daemon.Harness.OpenCode;

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
    /// <summary>
    /// The characters an injected server name may contain, as an ALLOWLIST.
    ///
    /// <para><b>Deliberately not a list of glob metacharacters.</b> That is what this was first, and
    /// review pointed out the obvious problem: <c>* ? [ ] !</c> is not exhaustive for whatever glob
    /// engine OpenCode uses — extglobs (<c>?()</c>, <c>*()</c>, <c>!()</c>), braces, pipes, parens and
    /// backslash are all plausible and were all absent. Enumerating another engine's syntax is a
    /// losing game, and the error message claimed totality the list did not have. A conservative
    /// allowlist is total by construction: no character outside it can mean anything to any engine.
    /// The house rule this follows is the one about escaping — admit only what is verifiable, else
    /// refuse.</para>
    /// </summary>
    static bool IsAdmissibleServerNameChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';

    internal static string Build(IReadOnlyList<AcpMcpServerSpec> injected) {
        var permission = new JsonObject {
            // FIRST, and the reason the rest of this document is an allowlist rather than a
            // blocklist: a blocklist would admit every tool a future OpenCode release adds.
            ["*"] = (JsonNode?)"deny"
        };

        // EXPLICIT per-tool denies on top of the wildcard, which is belt-and-braces about a merge rule
        // this code should not have to be right about.
        //
        // What was measured is that OPENCODE_PERMISSION overrides an operator config saying
        // `"*": "ask"`. What was NOT measured — a review of this change caught the gap — is whether a
        // wildcard from the ENV beats a SPECIFIC key from a file, e.g. an operator config carrying
        // `bash: "allow"`. OpenCode merges the two per key and then resolves patterns
        // specific-before-wildcard, so on that reading a file's `bash: "allow"` would survive our
        // `"*": "deny"` — and the only thing standing between that and a reviewer with a shell would be
        // the assumption that the isolated config dir stops the operator's file loading at all.
        //
        // Naming each forbidden tool makes the question moot: the env now carries a specific key for
        // each, so it wins per key on any merge order, whether or not any file config loaded. It also
        // makes ForbiddenTools load-bearing rather than a list only tests read.
        foreach (var tool in ForbiddenTools)
            permission[tool] = (JsonNode?)"deny";

        foreach (var tool in ReadTools)
            permission[tool] = (JsonNode?)"allow";

        foreach (var server in injected) {
            if (string.IsNullOrWhiteSpace(server.Name))
                throw new InvalidOperationException(
                    "opencode_reviewer_permission_unnamed_server: an injected MCP server has no wire "
                  + "name, so its flattened tool names cannot be admitted. Refusing rather than "
                  + "launching a reviewer that cannot reach its own result channel.");

            // The name is interpolated into a GLOB, so anything in it that a glob engine reads as
            // syntax would widen the entry past this server. Today every injected name is a per-launch
            // alias this daemon generated, so this is unreachable — which is exactly why it is a cheap
            // refusal rather than a design change: it stops the day a name starts coming from somewhere
            // else, instead of that day silently granting a wider surface.
            if (!server.Name.All(IsAdmissibleServerNameChar))
                throw new InvalidOperationException(
                    $"opencode_reviewer_permission_unsafe_server_name: injected MCP server "
                  + $"'{server.Name}' contains a character outside [A-Za-z0-9._-], so interpolating it "
                  + "into a permission glob could widen the reviewer's tool surface beyond that server. "
                  + "Refusing the launch.");

            permission[$"{server.Name}_*"] = (JsonNode?)"allow";
        }

        return permission.ToJsonString();
    }
}
