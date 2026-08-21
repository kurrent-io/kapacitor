using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Parsing helpers for kcap's Kiro agent-hooks file
/// (<c>~/.kiro/agents/kcap.json</c>). Kiro agent JSON carries a <c>hooks</c>
/// block — <c>{"hooks": {eventName: [entry…]}}</c> — where each command entry
/// holds the shell command under <c>command</c> (plus optional <c>timeout_ms</c>
/// / <c>cache_ttl_seconds</c>).
/// </summary>
public static class KiroHooksParser {
    /// <summary>
    /// The Kiro hook events kcap subscribes to. Deliberately just
    /// <c>agentSpawn</c>: it fires on EVERY prompt (the server dedupes on the
    /// session id), so it owns session-start AND keeps the watcher alive
    /// (EnsureWatcherRunning is idempotent) without a second subscription. We
    /// pointedly do NOT subscribe to <c>stop</c> — its captured STDIN payload
    /// carries no <c>session_id</c> (so it can't target a watcher) and a
    /// <c>stop</c> hook that emits any stdout re-injects into the agent and loops
    /// it. Per-tool hooks (pre/postToolUse) are skipped too: that content already
    /// arrives via the transcript. Kiro has no session-end trigger — the watcher
    /// synthesizes it on <c>kiro-cli</c> process exit.
    /// </summary>
    public static readonly string[] KiroHookEvents = [
        "agentSpawn"
    ];

    /// <summary>
    /// True if <paramref name="entry"/> is a command entry referencing the kcap
    /// Kiro hook dispatcher. Kiro uses a single <c>command</c> field (no
    /// per-shell variants), but we tolerate <c>bash</c> / <c>powershell</c> too
    /// in case the schema grows.
    /// </summary>
    public static bool EntryReferencesCapacitorKiroHook(JsonNode? entry) {
        return FieldContains(entry, "command")
            || FieldContains(entry, "bash")
            || FieldContains(entry, "powershell");

        static bool FieldContains(JsonNode? entry, string field) =>
            entry?[field] is JsonValue jv
         && jv.TryGetValue<string>(out var cmd)
         && cmd.Contains("kcap hook --kiro", StringComparison.Ordinal);
    }

    /// <summary>
    /// True if every event in <paramref name="events"/> has at least one entry
    /// referencing the kcap kiro command.
    /// </summary>
    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            if (!entries.Any(EntryReferencesCapacitorKiroHook)) return false;
        }

        return true;
    }
}
