using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Cursor;

/// <summary>
/// Parsing helpers for <c>~/.cursor/hooks.json</c>. Cursor's schema differs
/// from Codex's: entries are flat <c>{"command": "..."}</c> objects keyed
/// directly by the camelCase event name, not nested under <c>"hooks"</c>.
/// </summary>
public static class CursorHooksParser {
    /// <summary>The 8 Cursor hook events this dispatcher handles.</summary>
    public static readonly string[] CursorHookEvents = [
        "sessionStart",
        "sessionEnd",
        "beforeSubmitPrompt",
        "afterAgentResponse",
        "afterAgentThought",
        "preToolUse",
        "postToolUse",
        "postToolUseFailure"
    ];

    /// <summary>
    /// True if <paramref name="entry"/> is an object whose <c>command</c>
    /// string references the kcap Cursor hook dispatcher. Matches the
    /// current <c>kcap hook --cursor</c> marker as well as the pre-rename
    /// <c>kapacitor hook --cursor</c> so <c>kcap uninstall</c> and the
    /// upgrade-time refresh both clean up entries left by older CLI versions.
    /// </summary>
    public static bool EntryReferencesCapacitorCursorHook(JsonNode? entry) {
        return entry?["command"] is JsonValue jv &&
            jv.TryGetValue<string>(out var cmd)  &&
            (cmd.Contains("kcap hook --cursor",      StringComparison.Ordinal)
          || cmd.Contains("kapacitor hook --cursor", StringComparison.Ordinal));
    }

    /// <summary>
    /// True if every event in <paramref name="events"/> has at least one
    /// hooks.json entry referencing the kcap cursor command.
    /// </summary>
    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            var any = entries.Any(EntryReferencesCapacitorCursorHook);

            if (!any) return false;
        }
        return true;
    }
}
