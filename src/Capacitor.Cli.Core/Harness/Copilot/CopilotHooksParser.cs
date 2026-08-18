using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// Parsing helpers for kcap's Copilot hooks file
/// (<c>~/.copilot/hooks/kcap.json</c>). Copilot's schema is
/// <c>{"version": 1, "hooks": {eventName: [entry…]}}</c> where each command
/// entry carries the shell command under <c>command</c> (cross-platform),
/// <c>bash</c>, or <c>powershell</c>.
/// </summary>
public static class CopilotHooksParser {
    /// <summary>
    /// The Copilot hook events kcap subscribes to. Deliberately minimal —
    /// every hook execution writes a hook.start/hook.end noise pair into the
    /// session's events.jsonl, so per-tool events (preToolUse/postToolUse/
    /// permissionRequest) would double transcript noise for content the
    /// transcript already carries. sessionStart/sessionEnd own lifecycle,
    /// agentStop refreshes watcher liveness each turn, notification forwards
    /// to the server's notification record.
    /// </summary>
    public static readonly string[] CopilotHookEvents = [
        "sessionStart",
        "sessionEnd",
        "agentStop",
        "notification"
    ];

    /// <summary>
    /// True if <paramref name="entry"/> is a command entry referencing the
    /// kcap Copilot hook dispatcher, in any of the three command fields.
    /// </summary>
    public static bool EntryReferencesCapacitorCopilotHook(JsonNode? entry) {
        return FieldContains(entry, "command")
            || FieldContains(entry, "bash")
            || FieldContains(entry, "powershell");

        static bool FieldContains(JsonNode? entry, string field) =>
            entry?[field] is JsonValue jv
         && jv.TryGetValue<string>(out var cmd)
         && cmd.Contains("kcap hook --copilot", StringComparison.Ordinal);
    }

    /// <summary>
    /// True if every event in <paramref name="events"/> has at least one
    /// entry referencing the kcap copilot command.
    /// </summary>
    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            if (!entries.Any(EntryReferencesCapacitorCopilotHook)) return false;
        }

        return true;
    }
}
