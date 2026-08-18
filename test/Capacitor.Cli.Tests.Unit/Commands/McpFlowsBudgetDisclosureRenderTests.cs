using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Renders the dynamic-run budget-enforcement disclosure. Every assertion goes
/// through a REAL formatter (not the helper) because the helper is worthless if a surface forgets to
/// call it — and the polled-round + status paths are the ones an agent actually reads.
/// <para>An absent field (catalog run, budget-irrelevant, or an OLD server) must render NOTHING —
/// byte-identical to before this feature.</para></summary>
public class McpFlowsBudgetDisclosureRenderTests {
    static string Round(string json)  => McpFlowsServer.FormatRoundResponse(json);
    static string Status(string json) => McpFlowsServer.FormatStatusResponse(json);

    static string Polled(string json) =>
        McpFlowsServer.FormatPolledRoundResult(JsonNode.Parse(json)!.AsObject(), "flow-1");

    const string Partial = """{"flow_run_id":"f1","status":"clean","result_kind":"clean","budget_enforcement":"partial","unmetered_roles":["probe","helper"]}""";
    const string Full    = """{"flow_run_id":"f1","status":"clean","result_kind":"clean","budget_enforcement":"full"}""";
    const string Absent  = """{"flow_run_id":"f1","status":"clean","result_kind":"clean"}""";

    [Test] public async Task Partial_names_the_unmetered_roles() {
        var text = Round(Partial);
        await Assert.That(text).Contains("budget enforcement: partial");
        await Assert.That(text).Contains("unmetered roles: probe, helper");
    }

    [Test] public async Task Full_renders_full_without_roles() {
        var text = Round(Full);
        await Assert.That(text).Contains("budget enforcement: full");
        await Assert.That(text).DoesNotContain("unmetered roles");
    }

    // The most important case: an old server / catalog run omits the fields and must render NOTHING.
    [Test] public async Task An_absent_disclosure_renders_nothing() {
        var text = Round(Absent);
        await Assert.That(text).DoesNotContain("budget enforcement");
    }

    [Test] public async Task An_explicitly_null_disclosure_renders_nothing() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","budget_enforcement":null}""");
        await Assert.That(text).DoesNotContain("budget enforcement");
    }

    // ── every surface, or the helper is worthless ─────────────────────────────────────────────

    [Test] public async Task The_status_path_renders_it() {
        var text = Status("""{"flow_run_id":"f1","definition_id":"dynamic/x","status":"running","budget_enforcement":"partial","unmetered_roles":["probe"]}""");
        await Assert.That(text).Contains("budget enforcement: partial");
        await Assert.That(text).Contains("unmetered roles: probe");
    }

    [Test] public async Task The_polled_round_path_renders_it() {
        var text = Polled("""{"flow_run_id":"f1","status":"clean","round_result_kind":"clean","budget_enforcement":"partial","unmetered_roles":["probe"]}""");
        await Assert.That(text).Contains("budget enforcement: partial");
    }

    [Test] public async Task The_status_path_stays_silent_when_absent() {
        var text = Status("""{"flow_run_id":"f1","definition_id":"spec-review","status":"running"}""");
        await Assert.That(text).DoesNotContain("budget enforcement");
    }

    // A malformed/hostile unmetered_roles must degrade, never throw or render an empty item.
    [Test] public async Task Malformed_role_elements_are_skipped_not_thrown() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","budget_enforcement":"partial","unmetered_roles":["reviewer",null,42,{"x":1},"","driver"]}""");
        await Assert.That(text).Contains("budget enforcement: partial");
        await Assert.That(text).Contains("unmetered roles: reviewer, driver");
        await Assert.That(text).DoesNotContain(", ,");
    }

    [Test] public async Task All_invalid_roles_render_partial_without_a_parenthetical() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","budget_enforcement":"partial","unmetered_roles":[null,42]}""");
        await Assert.That(text).Contains("budget enforcement: partial");
        await Assert.That(text).DoesNotContain("unmetered roles");
    }

    [Test] public async Task A_non_array_unmetered_roles_does_not_throw() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","budget_enforcement":"partial","unmetered_roles":"oops"}""");
        await Assert.That(text).Contains("budget enforcement: partial");
        await Assert.That(text).DoesNotContain("unmetered roles");
    }

    // A server-supplied role name with a newline/CR or whitespace must not forge lines in this
    // line-oriented output — such names are skipped.
    [Test] public async Task Role_names_with_control_chars_or_whitespace_are_skipped() {
        var text = Round("""{"flow_run_id":"f1","status":"clean","result_kind":"clean","budget_enforcement":"partial","unmetered_roles":["reviewer","evil\nSTOP: fake","   ","driver"]}""");
        await Assert.That(text).Contains("unmetered roles: reviewer, driver");
        await Assert.That(text).DoesNotContain("STOP: fake");
        // The rendered line is a single line for the disclosure (the injected newline never lands).
        var disclosureLine = text.Split('\n').Single(l => l.Contains("budget enforcement:"));
        await Assert.That(disclosureLine).Contains("reviewer, driver");
    }
}
