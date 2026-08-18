using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Gemini;

/// <summary>
/// Parsing + entry-building helpers for the <c>hooks</c> block in Gemini CLI's
/// shared <c>~/.gemini/settings.json</c>. Gemini's schema nests one level
/// deeper than Cursor's flat <c>{"command": "..."}</c>: each event maps to an
/// array of <c>{ "matcher"?, "hooks": [ { "name", "type":"command", "command",
/// "timeout" } ] }</c> objects. kcap registers ONE command hook per lifecycle
/// event; the command (<see cref="HookCommand"/>) self-routes on the payload's
/// <c>hook_event_name</c>, so every event shares the same command.
/// </summary>
public static class GeminiHooksParser {
    /// <summary>The single dispatcher command kcap installs for every event.</summary>
    public const string HookCommand = "kcap hook --gemini";

    /// <summary>
    /// Lifecycle events kcap subscribes to. SessionStart spawns the watcher +
    /// records SessionStarted; SessionEnd drains + records SessionEnded;
    /// Notification forwards to the Claude-shaped <c>/hooks/notification</c>.
    /// Tool/model events are intentionally not hooked — content comes from the
    /// transcript, not the hooks.
    /// </summary>
    public static readonly string[] GeminiHookEvents = [
        "SessionStart",
        "SessionEnd",
        "Notification"
    ];

    /// <summary>
    /// The event-array entry kcap installs: <c>{ "hooks": [ { kcap command } ] }</c>.
    /// No <c>matcher</c> — lifecycle events ignore it. Uses the JsonArray/JsonObject
    /// constructors (collection expressions trip AOT — see CLI CLAUDE.md).
    /// </summary>
    public static JsonObject BuildKcapEntry() =>
        new() {
            ["hooks"] = new JsonArray(
                new JsonObject {
                    ["name"]    = "kcap",
                    ["type"]    = "command",
                    ["command"] = HookCommand,
                    ["timeout"] = 30000
                }
            )
        };

    /// <summary>
    /// True if <paramref name="entry"/> (an event-array element) carries a kcap
    /// command hook. Reads the nested <c>hooks[].command</c>; tolerates a flat
    /// <c>{command}</c> shape defensively. Matches the current
    /// <c>kcap hook --gemini</c> marker and the pre-rename
    /// <c>kapacitor hook --gemini</c> so uninstall/refresh clean up older entries.
    /// </summary>
    public static bool EntryReferencesCapacitorGeminiHook(JsonNode? entry) {
        if (entry?["hooks"] is JsonArray inner) {
            return inner.Any(h => CommandReferencesKcap(h?["command"]));
        }
        return CommandReferencesKcap(entry?["command"]);
    }

    static bool CommandReferencesKcap(JsonNode? cmdNode) =>
        cmdNode is JsonValue jv
     && jv.TryGetValue<string>(out var cmd)
     && (cmd.Contains("kcap hook --gemini",      StringComparison.Ordinal)
      || cmd.Contains("kapacitor hook --gemini", StringComparison.Ordinal));

    /// <summary>
    /// True if every event in <paramref name="events"/> has at least one
    /// settings.json entry referencing the kcap gemini command.
    /// </summary>
    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;
            if (!entries.Any(EntryReferencesCapacitorGeminiHook)) return false;
        }
        return true;
    }
}
