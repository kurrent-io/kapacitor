using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// The codex app-server notification → <see cref="AcpEventEnvelope"/> mapper (§2.4). Canonical lane =
/// completed snapshots / tool opens / plan / token deltas; ephemeral lane = accumulated live deltas;
/// unknown item types surface generically and bump the drift counter. Shapes are grounded on codex
/// 0.147.0's generated JSON schema.
/// </summary>
public class CodexNotificationMapperTests {
    static CodexNotificationMapper NewMapper(string? model = "gpt-5-codex") =>
        new(() => model, NullLogger.Instance);

    static IReadOnlyList<AcpEventEnvelope> Run(CodexNotificationMapper m, string method, string paramsJson) {
        using var doc = JsonDocument.Parse(paramsJson);
        return m.Map(method, doc.RootElement);
    }

    static AcpEventEnvelope Single(IReadOnlyList<AcpEventEnvelope> r) {
        if (r.Count != 1) throw new InvalidOperationException($"expected exactly one envelope, got {r.Count}");
        return r[0];
    }

    [Test]
    public async Task AgentMessage_completed_maps_to_canonical_assistant_text_with_item_id() {
        var e = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"agentMessage","id":"i1","text":"Hello"},"threadId":"t","turnId":"u","completedAtMs":1699999999000}"""));
        await Assert.That(e.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e.Text).IsEqualTo("Hello");
        await Assert.That(e.ItemId).IsEqualTo("i1");
        await Assert.That(e.Ephemeral).IsFalse();
        await Assert.That(e.TimestampIso).IsNotNull();
    }

    [Test]
    public async Task Reasoning_completed_renders_content_blocks_preferring_content_over_summary() {
        // content/summary are arrays of plain strings per the pinned schema.
        var e = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"reasoning","id":"r1","content":["step A","step B"],"summary":["S"]}}"""));
        await Assert.That(e.Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(e.Text).IsEqualTo("step A\nstep B");
        await Assert.That(e.ItemId).IsEqualTo("r1");
    }

    [Test]
    public async Task Reasoning_completed_falls_back_to_summary_when_content_empty() {
        var e = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"reasoning","id":"r1","content":[],"summary":["the gist"]}}"""));
        await Assert.That(e.Text).IsEqualTo("the gist");
    }

    [Test]
    public async Task CommandExecution_started_opens_a_shell_tool_call() {
        var e = Single(Run(NewMapper(), "item/started",
            """{"item":{"type":"commandExecution","id":"c1","command":"ls -la","cwd":"/repo","status":"inProgress"},"startedAtMs":1699999999000}"""));
        await Assert.That(e.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(e.ToolCallId).IsEqualTo("c1");
        await Assert.That(e.ToolName).IsEqualTo("shell");
        await Assert.That(e.ToolInputJson!).Contains("ls -la");
        await Assert.That(e.ToolInputJson!).Contains("/repo");
    }

    [Test]
    public async Task CommandExecution_completed_is_the_authoritative_result_with_error_from_exit_code() {
        var ok = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"commandExecution","id":"c1","command":"ls","cwd":"/r","aggregatedOutput":"total 0","exitCode":0,"status":"completed"}}"""));
        await Assert.That(ok.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(ok.ToolCallId).IsEqualTo("c1");
        await Assert.That(ok.ToolResult).IsEqualTo("total 0");
        await Assert.That(ok.ToolIsError).IsFalse();

        var bad = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"commandExecution","id":"c2","command":"false","cwd":"/r","aggregatedOutput":"boom","exitCode":2,"status":"failed"}}"""));
        await Assert.That(bad.ToolIsError).IsTrue();
    }

    [Test]
    public async Task FileChange_completed_maps_to_a_paired_tool_call_and_result() {
        var r = Run(NewMapper(), "item/completed",
            """{"item":{"type":"fileChange","id":"f1","status":"completed","changes":[{"path":"a.txt","kind":"update","diff":"@@ -1 +1 @@"}]}}""");
        await Assert.That(r.Count).IsEqualTo(2);

        var call = r[0];
        await Assert.That(call.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(call.ToolName).IsEqualTo("apply_patch");
        await Assert.That(call.ToolCallId).IsEqualTo("f1");
        await Assert.That(call.ToolInputJson!).Contains("a.txt");
        // ToolInputJson must be a JSON object the server can parse into tool arguments.
        using var doc = JsonDocument.Parse(call.ToolInputJson!);
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);

        var result = r[1];
        await Assert.That(result.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(result.ToolCallId).IsEqualTo("f1"); // paired with the call
        await Assert.That(result.ToolIsError).IsFalse();
    }

    [Test]
    public async Task FileChange_failed_apply_flags_the_result_as_error() {
        var r = Run(NewMapper(), "item/completed",
            """{"item":{"type":"fileChange","id":"f2","status":"failed","changes":[{"path":"a.txt","kind":"update","diff":"x"}]}}""");
        await Assert.That(r.Count).IsEqualTo(2);
        await Assert.That(r[1].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(r[1].ToolIsError).IsTrue();
    }

    [Test]
    public async Task McpToolCall_completed_maps_to_tool_result_and_flags_an_error() {
        var ok = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"mcpToolCall","id":"m1","server":"srv","tool":"do","status":"completed","result":{"ok":true}}}"""));
        await Assert.That(ok.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(ok.ToolIsError).IsFalse();

        var bad = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"mcpToolCall","id":"m2","server":"srv","tool":"do","status":"failed","error":"nope"}}"""));
        await Assert.That(bad.ToolIsError).IsTrue();
        await Assert.That(bad.ToolResult!).Contains("nope");

        // A failed call carrying BOTH a result and an error must render the error — the payload must stay
        // consistent with the ToolIsError flag rather than showing the result as if it succeeded.
        var both = Single(Run(NewMapper(), "item/completed",
            """{"item":{"type":"mcpToolCall","id":"m3","server":"srv","tool":"do","status":"failed","result":{"ok":true},"error":"boom"}}"""));
        await Assert.That(both.ToolIsError).IsTrue();
        await Assert.That(both.ToolResult!).Contains("boom");
        await Assert.That(both.ToolResult!).DoesNotContain("ok");
    }

    [Test]
    public async Task Turn_plan_update_maps_to_a_canonical_plan_snapshot() {
        var e = Single(Run(NewMapper(), "turn/plan/updated",
            """{"threadId":"t","turnId":"u","explanation":"Roadmap","plan":[{"step":"Do X","status":"pending"},{"step":"Do Y","status":"completed"}]}"""));
        await Assert.That(e.Kind).IsEqualTo(AcpEventKind.Plan);
        await Assert.That(e.Text!).Contains("Do X");
        await Assert.That(e.Text!).Contains("Do Y");
        await Assert.That(e.Text!).Contains("Roadmap");
        await Assert.That(e.ItemId).IsNull();     // turn-level, not item-level
        await Assert.That(e.Ephemeral).IsFalse();
    }

    [Test]
    public async Task AgentMessage_deltas_accumulate_into_ephemeral_content_and_completion_finalizes() {
        var m = NewMapper();
        var d1 = Single(Run(m, "item/agentMessage/delta", """{"itemId":"i1","delta":"Hel","threadId":"t","turnId":"u"}"""));
        await Assert.That(d1.Ephemeral).IsTrue();
        await Assert.That(d1.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(d1.Text).IsEqualTo("Hel");
        await Assert.That(d1.ItemId).IsEqualTo("i1");

        var d2 = Single(Run(m, "item/agentMessage/delta", """{"itemId":"i1","delta":"lo","threadId":"t","turnId":"u"}"""));
        await Assert.That(d2.Text).IsEqualTo("Hello");

        // The completed snapshot supersedes and finalizes the item's transient state.
        Run(m, "item/completed", """{"item":{"type":"agentMessage","id":"i1","text":"Hello"}}""");
        var after = Single(Run(m, "item/agentMessage/delta", """{"itemId":"i1","delta":"!","threadId":"t","turnId":"u"}"""));
        await Assert.That(after.Text).IsEqualTo("!"); // fresh buffer — the completed item dropped the old state
    }

    [Test]
    public async Task Command_output_deltas_ride_the_ephemeral_tool_result_lane() {
        var m = NewMapper();
        Run(m, "item/commandExecution/outputDelta", """{"itemId":"c1","delta":"line1\n","threadId":"t","turnId":"u"}""");
        var d = Single(Run(m, "item/commandExecution/outputDelta", """{"itemId":"c1","delta":"line2\n","threadId":"t","turnId":"u"}"""));
        await Assert.That(d.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(d.Ephemeral).IsTrue();
        await Assert.That(d.ToolCallId).IsEqualTo("c1");
        await Assert.That(d.ToolResult).IsEqualTo("line1\nline2\n");
    }

    [Test]
    public async Task PatchUpdated_is_a_snapshot_not_accumulated() {
        var m = NewMapper();
        var a = Single(Run(m, "item/fileChange/patchUpdated",
            """{"itemId":"f1","threadId":"t","turnId":"u","changes":[{"path":"a.txt","kind":"update","diff":"v1"}]}"""));
        await Assert.That(a.Ephemeral).IsTrue();
        await Assert.That(a.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(a.ToolInputJson!).Contains("v1");

        // A second snapshot reflects only its own changes — never appended to the first.
        var b = Single(Run(m, "item/fileChange/patchUpdated",
            """{"itemId":"f1","threadId":"t","turnId":"u","changes":[{"path":"a.txt","kind":"update","diff":"v2"}]}"""));
        await Assert.That(b.ToolInputJson!).Contains("v2");
        await Assert.That(b.ToolInputJson!).DoesNotContain("v1");
    }

    [Test]
    public async Task Token_usage_emits_the_first_whole_total_then_a_component_delta() {
        var m = NewMapper();
        var first = Single(Run(m, "thread/tokenUsage/updated",
            """{"threadId":"t","turnId":"u","tokenUsage":{"total":{"inputTokens":100,"cachedInputTokens":20,"cacheWriteInputTokens":5,"outputTokens":40,"reasoningOutputTokens":8,"totalTokens":165}}}"""));
        await Assert.That(first.Kind).IsEqualTo(AcpEventKind.TokenUsage);
        await Assert.That(first.Model).IsEqualTo("gpt-5-codex");
        await Assert.That(first.UsageInputTokens).IsEqualTo(100L);
        await Assert.That(first.UsageCacheWriteInputTokens).IsEqualTo(5L);

        var second = Single(Run(m, "thread/tokenUsage/updated",
            """{"threadId":"t","turnId":"u","tokenUsage":{"total":{"inputTokens":150,"cachedInputTokens":30,"cacheWriteInputTokens":5,"outputTokens":60,"reasoningOutputTokens":12,"totalTokens":245}}}"""));
        await Assert.That(second.UsageInputTokens).IsEqualTo(50L);
        await Assert.That(second.UsageCachedInputTokens).IsEqualTo(10L);
        await Assert.That(second.UsageCacheWriteInputTokens).IsEqualTo(0L);
        await Assert.That(second.UsageOutputTokens).IsEqualTo(20L);
    }

    [Test]
    public async Task Token_usage_zero_delta_emits_nothing() {
        var m = NewMapper();
        const string snapshot =
            """{"tokenUsage":{"total":{"inputTokens":10,"cachedInputTokens":2,"cacheWriteInputTokens":0,"outputTokens":5,"reasoningOutputTokens":1,"totalTokens":18}}}""";
        Run(m, "thread/tokenUsage/updated", snapshot);          // first: whole total
        var repeat = Run(m, "thread/tokenUsage/updated", snapshot); // unchanged cumulative → zero delta
        await Assert.That(repeat).IsEmpty();
    }

    [Test]
    public async Task Unknown_item_type_becomes_a_generic_tool_call_and_counts_drift() {
        var m = NewMapper();
        var e = Single(Run(m, "item/completed",
            """{"item":{"type":"quantumThing","id":"q1","payload":42}}"""));
        await Assert.That(e.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(e.ToolName).IsEqualTo("quantumThing");
        await Assert.That(e.ToolCallId).IsEqualTo("q1");
        await Assert.That(m.UnmappedKindCount).IsEqualTo(1);
    }

    [Test]
    public async Task Unmapped_methods_and_malformed_params_yield_nothing() {
        var m = NewMapper();
        await Assert.That(Run(m, "thread/archived", "{}")).IsEmpty();
        await Assert.That(Run(m, "turn/started", """{"turnId":"u"}""")).IsEmpty();
        await Assert.That(Run(m, "item/completed", "{}")).IsEmpty();                 // no item
        await Assert.That(Run(m, "item/agentMessage/delta", """{"itemId":"i1"}""")).IsEmpty(); // no delta
    }
}
