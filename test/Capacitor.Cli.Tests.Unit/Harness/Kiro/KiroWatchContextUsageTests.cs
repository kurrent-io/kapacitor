using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

/// <summary>
/// The live-watcher side of Kiro context-%: <see cref="WatchCommand.EnrichKiroContextUsage"/>
/// reads the sibling <c>{id}.json</c> (derived from the TRANSCRIPT path, not the dashless session id)
/// and stamps <c>data._kcap_usage.context_usage_percentage</c> onto AssistantMessage lines at flush,
/// reusing the import path's <see cref="KiroUsage"/>. Best-effort + order-preserving.
/// </summary>
public class KiroWatchContextUsageTests {
    // One turn, anchor message id "a2", context% 5.2612 (mirrors KiroUsageTests).
    const string Meta = """
        {"session_state":{"conversation_metadata":{"user_turn_metadatas":[
          {"message_ids":["u1","a1","t1","a2"],
           "context_usage_percentage":5.2612,
           "metering_usage":[{"value":0.25,"unit":"credit"},{"value":0.5,"unit":"credit"}]}
        ]}}}
        """;
    const string AnchorLine  = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a2","content":[{"kind":"text","data":"done"}]}}""";
    const string PromptLine   = """{"version":"v1","kind":"Prompt","data":{"message_id":"a2","content":[]}}""";
    const string NonAnchorAsst = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a1","content":[{"kind":"text","data":"x"}]}}""";

    // Writes the sibling {stem}.json and returns the (non-existent-is-fine) {stem}.jsonl transcript path.
    // stem is a DASHED id to prove the sibling is derived from the transcript path, not a dashless session id.
    static string SeedSibling(TempDir tmp, string metaJson) {
        var stem = "11111111-2222-3333-4444-555555555555";   // dashed, like Kiro's on-disk files
        tmp.CreateFile(stem + ".json", metaJson);
        return tmp.PathTo(stem + ".jsonl");
    }

    [Test]
    public async Task Enriches_anchor_assistant_line_from_sibling_json() {
        using var tmp = new TempDir();
        var transcriptPath = SeedSibling(tmp, Meta);
        var outLines = WatchCommand.EnrichKiroContextUsage([AnchorLine], transcriptPath);
        await Assert.That(outLines[0]).Contains("_kcap_usage");
        await Assert.That(outLines[0]).Contains("5.2612");
    }

    [Test]
    public async Task Leaves_non_anchor_and_non_assistant_lines_untouched() {
        using var tmp = new TempDir();
        var transcriptPath = SeedSibling(tmp, Meta);
        var outLines = WatchCommand.EnrichKiroContextUsage([NonAnchorAsst, PromptLine], transcriptPath);
        await Assert.That(outLines[0]).DoesNotContain("_kcap_usage");  // assistant, but not the anchor turn
        await Assert.That(outLines[1]).DoesNotContain("_kcap_usage");  // Prompt line
    }

    [Test]
    public async Task Missing_sibling_json_returns_lines_unchanged() {
        using var tmp = new TempDir();
        var transcriptPath = tmp.PathTo("no-sibling.jsonl");   // no {stem}.json next to it
        var outLines = WatchCommand.EnrichKiroContextUsage([AnchorLine], transcriptPath);
        await Assert.That(outLines[0]).IsEqualTo(AnchorLine);          // untouched, best-effort
    }

    [Test]
    public async Task Malformed_sibling_json_returns_lines_unchanged() {
        using var tmp = new TempDir();
        var transcriptPath = SeedSibling(tmp, "{ not valid json");
        var outLines = WatchCommand.EnrichKiroContextUsage([AnchorLine], transcriptPath);
        await Assert.That(outLines[0]).IsEqualTo(AnchorLine);
    }

    [Test]
    public async Task Preserves_batch_order_and_count_when_flushing_a_buffer() {
        // Simulates enriching a flushed buffer (finding 2): a mixed batch keeps order + count,
        // with only the anchor assistant line enriched.
        using var tmp = new TempDir();
        var transcriptPath = SeedSibling(tmp, Meta);
        var batch = new List<string> { PromptLine, NonAnchorAsst, AnchorLine };
        var outLines = WatchCommand.EnrichKiroContextUsage(batch, transcriptPath);
        await Assert.That(outLines.Count).IsEqualTo(3);
        await Assert.That(outLines[0]).DoesNotContain("_kcap_usage");
        await Assert.That(outLines[1]).DoesNotContain("_kcap_usage");
        await Assert.That(outLines[2]).Contains("5.2612");            // anchor stays last, enriched
    }
}
