using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

/// <summary>
/// Covers the live-watch Kiro usage-backfill synthetic line:
/// <see cref="WatchCommand.BuildKiroUsageBackfillLine"/> builds the JSONL the server
/// recognizes, and <see cref="WatchCommand.AppendKiroUsageBackfillLines"/> reads the
/// sidecar <c>{id}.json</c> via <see cref="KiroUsage.AnchorMap"/>
/// and emits one line per NOT-yet-emitted anchor, tracked on
/// <see cref="WatchState.KiroUsageEmittedAnchors"/> so a live drain never double-emits.
/// </summary>
public class KiroLiveUsageBackfillTests {
    [Test]
    public async Task build_line_carries_anchor_credits_and_context() {
        var line = WatchCommand.BuildKiroUsageBackfillLine("msg-42", credits: 1.5, contextPct: 37.0);
        var node = JsonNode.Parse(line)!;
        await Assert.That(node["kind"]!.GetValue<string>()).IsEqualTo("KiroUsageBackfilled");
        await Assert.That(node["data"]!["message_id"]!.GetValue<string>()).IsEqualTo("msg-42");
        await Assert.That(node["data"]!["credits"]!.GetValue<double>()).IsEqualTo(1.5);
        await Assert.That(node["data"]!["context_usage_percentage"]!.GetValue<double>()).IsEqualTo(37.0);
    }

    [Test]
    public async Task build_line_omits_context_when_absent() {
        var line = WatchCommand.BuildKiroUsageBackfillLine("msg-1", credits: 0.5, contextPct: null);
        var node = JsonNode.Parse(line)!;
        await Assert.That(node["data"]!["context_usage_percentage"]).IsNull();
    }

    [Test]
    public async Task append_emits_each_anchor_once() {
        using var tmp = new TempDir();
        // Write a sidecar {id}.json with one turn (anchor msg-1, 2 credits, 40%).
        var jsonl = tmp.PathTo("sess.jsonl");
        var meta  = tmp.PathTo("sess.json");
        await File.WriteAllTextAsync(jsonl, "");
        await File.WriteAllTextAsync(meta, """
        {"session_state":{"conversation_metadata":{"user_turn_metadatas":[
          {"message_ids":["msg-1"],"metering_usage":[{"unit":"credit","value":2.0}],"context_usage_percentage":40.0}
        ]}}}
        """);
        var state = new WatchState { ThresholdReached = true };
        var lines = new List<string>(); var nums = new List<int>();
        var n1 = WatchCommand.AppendKiroUsageBackfillLines(state, lines, nums, jsonl);
        // simulate a successful send commit
        foreach (var a in new[]{"msg-1"}) state.KiroUsageEmittedAnchors.Add(a);
        var lines2 = new List<string>(); var nums2 = new List<int>();
        var n2 = WatchCommand.AppendKiroUsageBackfillLines(state, lines2, nums2, jsonl);

        await Assert.That(n1).IsEqualTo(1);
        await Assert.That(n2).IsEqualTo(0); // already emitted → not re-appended
        await Assert.That(lines[0]).Contains("KiroUsageBackfilled");
        await Assert.That(lines.Count).IsEqualTo(nums.Count);
    }

    [Test]
    public async Task append_backfills_after_sidecar_lands_later() {
        // Reproduces the sidecar-lands-after-line-sent scenario: the drain that sent
        // the AssistantMessage line ran before Kiro flushed {id}.json, so no usage was
        // captured then. A later drain re-reads the (now-populated) sidecar and emits
        // the synthetic backfill line for the same anchor.
        using var tmp = new TempDir();
        var jsonl = tmp.PathTo("sess.jsonl");
        var meta  = tmp.PathTo("sess.json");
        await File.WriteAllTextAsync(jsonl, "");
        // No sidecar yet — first drain sees nothing to backfill.
        var state  = new WatchState { ThresholdReached = true };
        var lines0 = new List<string>(); var nums0 = new List<int>();
        var n0     = WatchCommand.AppendKiroUsageBackfillLines(state, lines0, nums0, jsonl);
        await Assert.That(n0).IsEqualTo(0);

        // Sidecar lands after the anchor line was already sent.
        await File.WriteAllTextAsync(meta, """
        {"session_state":{"conversation_metadata":{"user_turn_metadatas":[
          {"message_ids":["msg-late"],"metering_usage":[{"unit":"credit","value":1.0}],"context_usage_percentage":12.0}
        ]}}}
        """);

        var lines1 = new List<string>(); var nums1 = new List<int>();
        var n1     = WatchCommand.AppendKiroUsageBackfillLines(state, lines1, nums1, jsonl);
        await Assert.That(n1).IsEqualTo(1);
        await Assert.That(lines1[0]).Contains("msg-late");
    }

    [Test]
    public async Task append_returns_zero_when_sidecar_missing() {
        using var tmp = new TempDir();
        var jsonl = tmp.PathTo("sess.jsonl");
        await File.WriteAllTextAsync(jsonl, "");
        var state = new WatchState { ThresholdReached = true };
        var lines = new List<string>(); var nums = new List<int>();
        var n = WatchCommand.AppendKiroUsageBackfillLines(state, lines, nums, jsonl);
        await Assert.That(n).IsEqualTo(0);
        await Assert.That(lines.Count).IsEqualTo(0);
    }
}
