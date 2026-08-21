using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Reads Kiro's per-turn usage from a session's <c>{id}.json</c> metadata and
/// injects it onto the matching JSONL transcript line. Each
/// <c>user_turn_metadatas[]</c> entry carries billing <c>metering_usage[]</c>
/// (credits) and an end-of-turn <c>context_usage_percentage</c>, plus
/// <c>input_token_count</c>/<c>output_token_count</c> that Kiro persists as 0
/// today (the CLI discards the Amazon-Q API's TokenUsage — upstream aws#2397).
/// This lives in the <c>.json</c>, not the <c>.jsonl</c> the server normalizer
/// sees. So the CLI maps each turn's usage to its <b>final</b> message id (the
/// turn's last entry in <c>message_ids[]</c> — the final assistant response) and
/// stamps a <c>_kcap_usage</c> object on that transcript line;
/// <c>KiroTranscriptNormalizer</c> lifts credits/context% onto
/// <c>extensions.kiro</c> and, when the token counts are non-zero, stamps a
/// canonical <c>$usage</c> (the hedge: dormant now, auto-lights if a
/// future kiro-cli release populates them). The session <c>model_id</c> rides
/// alongside the token counts so that <c>$usage</c> has a model. Import-only today
/// (the live path would key off Kiro's per-turn <c>stop</c> hook).
/// </summary>
public static class KiroUsage {
    public readonly record struct TurnUsage(
        double  Credits,
        double? ContextPct,
        long?   InputTokens  = null,
        long?   OutputTokens = null,
        string? Model        = null);

    /// <summary>
    /// Builds <c>anchor message_id → usage</c> from the metadata JSON. The anchor
    /// is the turn's last <c>message_ids[]</c> entry. Empty on absent/malformed
    /// metadata or when a turn has neither credits nor a context percentage.
    /// </summary>
    public static IReadOnlyDictionary<string, TurnUsage> AnchorMap(string? metadataJson) {
        var map = new Dictionary<string, TurnUsage>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(metadataJson)) return map;

        try {
            var root  = JsonNode.Parse(metadataJson);
            var turns = root
                ?["session_state"]?["conversation_metadata"]?["user_turn_metadatas"] as JsonArray;
            if (turns is null) return map;

            // Session-level model id (e.g. "minimax-m2.5", "auto"). Rides alongside the
            // token counts so the server can stamp a canonical $usage with a model —
            // AccumulateTokens drops entries with no model (HasModel guard).
            // Defensive read: GetValue<string>() throws if model_id is present but not a
            // JSON string, and this whole method is one best-effort try/catch — a bad
            // optional field must not abort the map and drop credits/context% enrichment.
            var sessionModel = root?["session_state"]?["rts_model_state"]?["model_info"]?["model_id"]
                is JsonValue mv && mv.TryGetValue<string>(out var sm) ? sm : null;

            foreach (var t in turns) {
                if (t?["message_ids"] is not JsonArray mids || mids.Count == 0) continue;
                if (mids[^1]?.GetValue<string>() is not { Length: > 0 } anchor) continue;

                double credits = 0;
                var hasCredits = false;
                if (t["metering_usage"] is JsonArray metering) {
                    foreach (var m in metering) {
                        if (m?["unit"]?.GetValue<string>() == "credit"
                         && m["value"] is JsonValue v && v.TryGetValue<double>(out var d)) {
                            credits += d;
                            hasCredits = true;
                        }
                    }
                }

                double? ctx = t["context_usage_percentage"] is JsonValue c && c.TryGetValue<double>(out var cp) ? cp : null;

                // Token-count hedge: Kiro persists these as 0 today (upstream
                // aws#2397 — the CLI discards the API's TokenUsage). Carry them ONLY when
                // non-zero, so the hedge stays dormant now and lights up automatically if
                // a future kiro-cli release starts populating them.
                var inTok     = Tokens(t, "input_token_count");
                var outTok    = Tokens(t, "output_token_count");
                var hasTokens = inTok is not null || outTok is not null;

                if (hasCredits || ctx is not null || hasTokens)
                    map[anchor] = new TurnUsage(
                        hasCredits ? credits : 0, ctx,
                        inTok, outTok,
                        hasTokens ? sessionModel : null);
            }
        } catch {
            // Malformed metadata — usage is best-effort enrichment, never fatal.
        }

        return map;

        // Reads a per-turn token counter, returning null for absent/non-positive
        // values so a 0 (Kiro's value today) never flows through as a real count.
        static long? Tokens(JsonNode? turn, string prop) =>
            turn?[prop] is JsonValue v && v.TryGetValue<long>(out var n) && n > 0 ? n : null;
    }

    /// <summary>
    /// Returns <paramref name="line"/> with a <c>data._kcap_usage</c> object
    /// added when it is an <c>AssistantMessage</c> whose <c>message_id</c> is a
    /// turn anchor; otherwise returns it unchanged.
    /// </summary>
    public static string EnrichLine(string line, IReadOnlyDictionary<string, TurnUsage> anchors) {
        if (anchors.Count == 0) return line;

        try {
            if (JsonNode.Parse(line) is not JsonObject root) return line;
            if (root["kind"]?.GetValue<string>() != "AssistantMessage") return line;
            if (root["data"] is not JsonObject data) return line;
            if (data["message_id"]?.GetValue<string>() is not { } mid || !anchors.TryGetValue(mid, out var u)) return line;

            // bool/int/double/long/string assign to JsonObject is AOT-reflection-free,
            // so no JsonNode.Parse dance. Credits/context% are doubles; token counts are
            // longs and only present when non-zero (hedge); model rides with them.
            var usage = new JsonObject { ["credits"] = u.Credits };
            if (u.ContextPct is { } pct)          usage["context_usage_percentage"] = pct;
            if (u.InputTokens is { } inTok)       usage["input_token_count"]        = inTok;
            if (u.OutputTokens is { } outTok)     usage["output_token_count"]       = outTok;
            if (u.Model is { Length: > 0 } model) usage["model"]                    = model;
            data["_kcap_usage"] = usage;

            return root.ToJsonString();
        } catch {
            return line;
        }
    }
}
