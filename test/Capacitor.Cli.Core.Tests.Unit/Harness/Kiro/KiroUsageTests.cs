using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

/// <summary>
/// Covers <see cref="KiroUsage"/>: reading per-turn credits + context% from the
/// session's <c>{id}.json</c> metadata (anchored on the turn's final message id)
/// and injecting them onto the matching <c>AssistantMessage</c> transcript line.
/// </summary>
public class KiroUsageTests {
    // One turn: 4 message ids; credits 0.25 + 0.5 = 0.75 (exact in binary, so the
    // sum assertion stays float-safe); context% 5.2612. Anchor = the last id.
    const string Meta = """
        {"session_state":{"conversation_metadata":{"user_turn_metadatas":[
          {"message_ids":["u1","a1","t1","a2"],
           "input_token_count":0,"output_token_count":0,
           "context_usage_percentage":5.2612,
           "metering_usage":[{"value":0.25,"unit":"credit"},{"value":0.5,"unit":"credit"}]}
        ]}}}
        """;

    [Test]
    public async Task anchor_map_sums_credits_and_keys_on_final_message() {
        var map = KiroUsage.AnchorMap(Meta);

        await Assert.That(map.Count).IsEqualTo(1);
        await Assert.That(map.ContainsKey("a2")).IsTrue();   // last message_id = final assistant response
        await Assert.That(map["a2"].Credits).IsEqualTo(0.75);
        await Assert.That(map["a2"].ContextPct).IsEqualTo(5.2612);
    }

    [Test]
    public async Task enrich_line_injects_usage_on_anchor_assistant_line_only() {
        var map = KiroUsage.AnchorMap(Meta);

        var anchor = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a2","content":[{"kind":"text","data":"done"}]}}""";
        var enriched = KiroUsage.EnrichLine(anchor, map);
        await Assert.That(enriched).Contains("_kcap_usage");
        await Assert.That(enriched).Contains("0.75");
        await Assert.That(enriched).Contains("5.2612");

        // A non-anchor assistant line is untouched.
        var other = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a1","content":[{"kind":"text","data":"x"}]}}""";
        await Assert.That(KiroUsage.EnrichLine(other, map)).DoesNotContain("_kcap_usage");

        // Only AssistantMessage lines are enriched — a Prompt with the anchor id is left alone.
        var prompt = """{"version":"v1","kind":"Prompt","data":{"message_id":"a2","content":[]}}""";
        await Assert.That(KiroUsage.EnrichLine(prompt, map)).DoesNotContain("_kcap_usage");
    }

    [Test]
    [Arguments(null)]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("""{"session_state":{"conversation_metadata":{"user_turn_metadatas":[]}}}""")]
    public async Task anchor_map_empty_on_missing_or_malformed(string? json) {
        await Assert.That(KiroUsage.AnchorMap(json).Count).IsEqualTo(0);
    }

    // ── token-count hedge ──────────────────────────────────────────
    // Kiro's schema carries input/output_token_count per turn and a session model
    // id, but the CLI persists them as 0 today (upstream aws#2397). When a future
    // release populates them, the CLI must forward the (non-zero) counts + model so
    // the server can stamp a canonical $usage — no further code change needed.

    // One turn WITH real token counts and a session-level model id.
    const string MetaWithTokens = """
        {"session_state":{
          "rts_model_state":{"model_info":{"model_id":"minimax-m2.5","context_window_tokens":196000}},
          "conversation_metadata":{"user_turn_metadatas":[
            {"message_ids":["u1","a1"],
             "input_token_count":1200,"output_token_count":340,
             "context_usage_percentage":7.5,
             "metering_usage":[{"value":0.1,"unit":"credit"}]}
          ]}}}
        """;

    [Test]
    public async Task anchor_map_captures_nonzero_token_counts_and_session_model() {
        var map = KiroUsage.AnchorMap(MetaWithTokens);

        await Assert.That(map.ContainsKey("a1")).IsTrue();
        await Assert.That(map["a1"].InputTokens).IsEqualTo(1200L);
        await Assert.That(map["a1"].OutputTokens).IsEqualTo(340L);
        await Assert.That(map["a1"].Model).IsEqualTo("minimax-m2.5");
    }

    [Test]
    public async Task enrich_line_injects_token_counts_and_model_when_present() {
        var map    = KiroUsage.AnchorMap(MetaWithTokens);
        var anchor = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a1","content":[{"kind":"text","data":"done"}]}}""";

        var enriched = KiroUsage.EnrichLine(anchor, map);

        await Assert.That(enriched).Contains("input_token_count");
        await Assert.That(enriched).Contains("1200");
        await Assert.That(enriched).Contains("output_token_count");
        await Assert.That(enriched).Contains("340");
        await Assert.That(enriched).Contains("minimax-m2.5");
    }

    [Test]
    public async Task anchor_map_tolerates_non_string_model_id() {
        // Review finding: a malformed/variant model_id (present but not a JSON string)
        // must NOT abort the whole anchor map — usage enrichment is best-effort. Here the
        // turn's credits/context% must still be captured.
        const string metaBadModel = """
            {"session_state":{
              "rts_model_state":{"model_info":{"model_id":12345}},
              "conversation_metadata":{"user_turn_metadatas":[
                {"message_ids":["u1","a1"],
                 "context_usage_percentage":7.5,
                 "metering_usage":[{"value":0.1,"unit":"credit"}]}
              ]}}}
            """;

        var map = KiroUsage.AnchorMap(metaBadModel);

        await Assert.That(map.ContainsKey("a1")).IsTrue();
        await Assert.That(map["a1"].Credits).IsEqualTo(0.1);
        await Assert.That(map["a1"].ContextPct).IsEqualTo(7.5);
    }

    [Test]
    public async Task zero_token_counts_stay_dormant() {
        // The default fixture has input/output_token_count = 0 — the hedge must NOT
        // fabricate token/model fields (today's behaviour: credits/context% only).
        var map = KiroUsage.AnchorMap(Meta);

        await Assert.That(map["a2"].InputTokens).IsNull();
        await Assert.That(map["a2"].OutputTokens).IsNull();

        var anchor   = """{"version":"v1","kind":"AssistantMessage","data":{"message_id":"a2","content":[{"kind":"text","data":"done"}]}}""";
        var enriched = KiroUsage.EnrichLine(anchor, map);

        await Assert.That(enriched).DoesNotContain("input_token_count");
        await Assert.That(enriched).DoesNotContain("output_token_count");
    }
}
