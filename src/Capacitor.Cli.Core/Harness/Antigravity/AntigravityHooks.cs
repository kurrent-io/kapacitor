using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Antigravity;

/// <summary>
/// Builder + detection for kcap's hook block inside Antigravity's <c>hooks.json</c>.
/// Antigravity's config nests differently from Gemini's shared
/// <c>settings.json</c>: <c>hooks.json</c> is a map of user-named blocks →
/// <c>{ event → entries }</c>, so kcap owns exactly one block (<see cref="BlockName"/>)
/// and never disturbs user blocks.
///
/// Two entry shapes (verified against Antigravity 2.2.1, spike): tool events
/// (<c>PreToolUse</c>/<c>PostToolUse</c>) use <c>[{ matcher, hooks:[…] }]</c>; lifecycle
/// events (<c>PreInvocation</c>/<c>PostInvocation</c>/<c>Stop</c>) use a DIRECT handler
/// list <c>[{ type, command, timeout }]</c> (a matcher there is silently ignored / can
/// suppress the hook). The payload carries NO event-name field, so each event gets a
/// DISTINCT command (<c>kcap hook --antigravity &lt;Event&gt;</c>) rather than one
/// self-routing dispatcher. The command must be fast + always exit 0 (control hook).
/// </summary>
public static class AntigravityHooks {
    public const string BlockName = "kcap";

    /// <summary>Lifecycle events — direct handler list (no matcher).</summary>
    public static readonly string[] LifecycleEvents = ["PreInvocation", "PostInvocation", "Stop"];

    /// <summary>Tool events — matcher + nested <c>hooks[]</c>.</summary>
    public static readonly string[] ToolEvents = ["PreToolUse", "PostToolUse"];

    const int TimeoutMs = 15000;

    public static string HookCommand(string @event) => $"kcap hook --antigravity {@event}";

    static JsonObject Handler(string @event) =>
        new() {
            ["type"]    = "command",
            ["command"] = HookCommand(@event),
            ["timeout"] = TimeoutMs
        };

    /// <summary>Semver of the kcap plugin's manifest/hook layout (NOT the kcap build version —
    /// that's tracked by the sibling <c>.kcap-hooks-version</c> marker). Kept a plain dotted
    /// triple: the GUI was only verified to accept a simple <c>x.y.z</c> (GUI re-test),
    /// so we avoid prerelease/build-metadata suffixes that a stricter loader might reject.</summary>
    public const string PluginVersion = "1.0.0";

    /// <summary>
    /// The plugin manifest (<c>plugin.json</c>) the Antigravity GUI REQUIRES to recognize a
    /// directory under <c>config/plugins/</c> as a loadable plugin. Without this marker the GUI
    /// silently ignores the dir and never loads the sibling <c>hooks.json</c> — the reason the
    /// CLI-dir install shipped in #256 was invisible to the running IDE (GUI re-test).
    /// <c>name</c> is optional (the GUI defaults it to the dir name) but we set it explicitly.
    /// </summary>
    public static JsonObject BuildPluginManifest() =>
        new() {
            ["name"]        = BlockName,
            ["version"]     = PluginVersion,
            ["description"] = "Kurrent Capacitor — session capture for Antigravity"
        };

    /// <summary>Builds kcap's block: every event mapped to its (shape-correct) entries.</summary>
    public static JsonObject BuildKcapBlock() {
        var block = new JsonObject();

        foreach (var e in LifecycleEvents)
            block[e] = new JsonArray(Handler(e)); // direct handler list

        foreach (var e in ToolEvents)
            block[e] = new JsonArray(
                new JsonObject {
                    ["matcher"] = "*",
                    ["hooks"]   = new JsonArray(Handler(e))
                });

        return block;
    }

    /// <summary>
    /// True when the named block (an <c>{ event → entries }</c> object) references a kcap
    /// antigravity command in either entry shape. Also matches the pre-rename
    /// <c>kapacitor hook --antigravity</c> so uninstall/refresh clean up older entries.
    /// </summary>
    public static bool BlockReferencesKcap(JsonNode? block) {
        if (block is not JsonObject events) return false;

        foreach (var (_, entries) in events) {
            if (entries is not JsonArray arr) continue;
            foreach (var entry in arr) {
                if (CommandReferencesKcap(entry?["command"])) return true;                       // lifecycle handler
                if (entry?["hooks"] is JsonArray inner && inner.Any(h => CommandReferencesKcap(h?["command"]))) return true; // tool
            }
        }
        return false;
    }

    static bool CommandReferencesKcap(JsonNode? cmdNode) =>
        cmdNode is JsonValue jv
     && jv.TryGetValue<string>(out var cmd)
     && (cmd.Contains("kcap hook --antigravity",      StringComparison.Ordinal)
      || cmd.Contains("kapacitor hook --antigravity", StringComparison.Ordinal));
}
