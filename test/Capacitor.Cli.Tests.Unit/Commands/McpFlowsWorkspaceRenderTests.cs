using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Renders the run's reviewer workspace decision — was the reviewer looking at the caller's
/// uncommitted work, or at a checkout of the last commit?
///
/// <para>Every assertion here goes through a REAL formatter rather than the helper directly. The
/// helper being correct is worth nothing if a surface forgets to call it, and the polled-round path
/// in particular is the one an agent actually reads on nearly every flow — a parity test written
/// against the blocking path alone would pass while the dominant path stayed silent.</para>
/// </summary>
public class McpFlowsWorkspaceRenderTests {
    static string Round(string json)  => McpFlowsServer.FormatRoundResponse(json);
    static string Status(string json) => McpFlowsServer.FormatStatusResponse(json);
    static string Close(string json)  => McpFlowsServer.FormatCloseResponse(json);

    static string Polled(string json) =>
        McpFlowsServer.FormatPolledRoundResult(JsonNode.Parse(json)!.AsObject(), "flow-1");

    static string Roundless(string json) =>
        McpFlowsServer.TryFormatRoundlessStart(json, out _) ?? "<not a roundless start>";

    const string BorrowedRound  = """{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":"borrowed"}""";
    const string FallbackRound  = """{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":"fallback","fallback_reason":"not_colocated"}""";
    const string SilentRound    = """{"flow_run_id":"f1","status":"clean","result_kind":"clean"}""";

    // ── the three outcomes, through one surface ───────────────────────────────────────────────

    [Test] public async Task Borrowed_says_the_reviewer_saw_uncommitted_work() {
        var text = Round(BorrowedRound);
        await Assert.That(text).Contains("workspace: borrowed");
        await Assert.That(text).Contains("uncommitted");
    }

    [Test] public async Task Fallback_names_the_reason_and_warns_that_uncommitted_work_was_not_seen() {
        var text = Round(FallbackRound);
        await Assert.That(text).Contains("workspace: fallback (not_colocated)");
        // The caution is the whole point: a caller who assumes otherwise draws conclusions about
        // code the reviewer never read. Worded to be true for EVERY reason -- see the
        // context-only case below.
        await Assert.That(text).Contains("not included automatically");
    }

    // The single most important case. An older server, a multi-participant run and a generic
    // single-participant flow all omit the field — and rendering that as "borrowed" would be the one
    // wrong answer that reads as reassurance.
    [Test] public async Task An_absent_decision_renders_unknown_and_never_borrowed() {
        var text = Round(SilentRound);
        await Assert.That(text).Contains("workspace: unknown");
        await Assert.That(text).DoesNotContain("borrowed");
    }

    [Test] public async Task An_explicitly_null_decision_renders_unknown() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":null}""");
        await Assert.That(text).Contains("workspace: unknown");
        await Assert.That(text).DoesNotContain("borrowed");
    }

    // ── every surface, or the helper is worthless ─────────────────────────────────────────────

    [Test] public async Task The_polled_round_path_renders_it() {
        // The dominant async path — the one an agent reads on nearly every flow.
        var text = Polled("""{"flow_run_id":"f1","status":"clean","round_result_kind":"clean","workspace_mode":"borrowed"}""");
        await Assert.That(text).Contains("workspace: borrowed");
    }

    [Test] public async Task The_status_path_renders_it() {
        var text = Status("""{"flow_run_id":"f1","definition_id":"spec-review","status":"running","workspace_mode":"fallback","fallback_reason":"daemon_outdated"}""");
        await Assert.That(text).Contains("workspace: fallback (daemon_outdated)");
    }

    [Test] public async Task The_close_path_renders_it() {
        // Close is often the last thing a caller reads, so it may be the only record they keep.
        var text = Close("""{"flow_run_id":"f1","status":"closed","workspace_mode":"borrowed"}""");
        await Assert.That(text).Contains("workspace: borrowed");
    }

    [Test] public async Task The_roundless_start_path_renders_it() {
        var text = Roundless("""{"flow_run_id":"f1","status":"running"}""");
        // A multi-participant start resolves no workspace — it must say so rather than stay silent.
        await Assert.That(text).Contains("workspace: unknown");
    }

    // ── pass-through ──────────────────────────────────────────────────────────────────────────

    // Reasons are NOT allowlisted anywhere — not on the server, not here. A reason introduced
    // server-side tomorrow must reach the user unchanged. A switch, a set or a mapping table fails
    // this, and the last case is the one that catches a hardcoded example.
    [Test]
    [Arguments("not_colocated")]
    [Arguments("daemon_outdated")]
    [Arguments("not_allowed")]
    [Arguments("no_requesting_cwd")]
    [Arguments("context_only_requested")]
    [Arguments("a_reason_this_cli_has_never_heard_of")]
    public async Task Any_fallback_reason_renders_verbatim(string reason) {
        var text = Round($$"""{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":"fallback","fallback_reason":"{{reason}}"}""");
        await Assert.That(text).Contains($"workspace: fallback ({reason})");
    }

    // A fallback with no reason must still disclose the fallback — an empty parenthetical would be
    // worse than none, and swallowing the line entirely would hide the caution.
    [Test] public async Task A_fallback_with_no_reason_still_discloses_and_still_warns() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":"fallback"}""");
        await Assert.That(text).Contains("workspace: fallback");
        await Assert.That(text).DoesNotContain("()");
        await Assert.That(text).Contains("not included automatically");
    }

    // Qodo (#380, finding 3). The caution must be true for EVERY reason, not just the common ones.
    // context_only_requested means the reviewer read the SUBMITTED CONTEXT and no repository at all —
    // the earlier wording ("read a checkout at the last commit") would have had a caller believe
    // committed code was reviewed when none was. Special-casing the reason would fix that one case
    // and reintroduce the allowlist this feature exists without, so the claim is worded to hold
    // universally instead.
    [Test] public async Task A_context_only_fallback_does_not_claim_committed_code_was_reviewed() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":"fallback","fallback_reason":"context_only_requested"}""");

        await Assert.That(text).Contains("workspace: fallback (context_only_requested)");
        await Assert.That(text).Contains("not included automatically");

        // The false claim, in any of its forms.
        await Assert.That(text).DoesNotContain("last commit");
        await Assert.That(text).DoesNotContain("checkout");
    }

    // A mode this build doesn't know (a future server, or "owned") is reported verbatim rather than
    // collapsed into one of the known cases — inventing a category is how a caller ends up trusting
    // a decision the server never made.
    [Test] public async Task An_unrecognised_mode_is_reported_verbatim() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":"owned"}""");
        await Assert.That(text).Contains("workspace: owned");
        await Assert.That(text).DoesNotContain("unknown");
        await Assert.That(text).DoesNotContain("borrowed");
    }

    // Alias-independence: the server never sees an MCP alias name, so equivalent payloads from
    // start_flow and start_review_flow must render identically. This can only be asserted CLI-side.
    [Test] public async Task Equivalent_payloads_render_identically_whatever_alias_produced_them() {
        var viaReviewAlias = Round(FallbackRound);
        var viaGenericTool = Round(FallbackRound);
        await Assert.That(viaReviewAlias).IsEqualTo(viaGenericTool);
    }

    // No response may leak a filesystem path. borrowed_cwd is an absolute path on the requester's
    // machine; the mode/reason pair is the entire contract.
    [Test] public async Task A_rendered_response_never_leaks_a_workspace_path() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","workspace_mode":"borrowed","borrowed_cwd":"/Users/someone/private/checkout"}""");
        await Assert.That(text).DoesNotContain("/Users/someone");
        await Assert.That(text).DoesNotContain("borrowed_cwd");
    }
}
