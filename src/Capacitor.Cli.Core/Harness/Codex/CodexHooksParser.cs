using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// Parsing helpers for <c>~/.codex/hooks.json</c> (and <c>&lt;repo&gt;/.codex/hooks.json</c>
/// in project-scope installs). Shared by the CLI's <c>plugin install --codex</c>
/// command (which writes the file) and the daemon's <c>CodexLauncher</c> preflight
/// (which reads it before spawning a hosted Codex agent).
/// </summary>
public static class CodexHooksParser {
    /// <summary>Hook event names Codex CLI emits.</summary>
    public static readonly string[] CodexHookEvents = [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PermissionRequest",
        "Stop"
    ];

    /// <summary>
    /// Returns true if <paramref name="entry"/> is a hooks.json group whose
    /// <c>hooks[].command</c> references the kcap Codex hook dispatcher.
    /// Recognises the current <c>kcap hook --codex</c> marker as well as the
    /// two earlier forms — <c>kcap codex-hook</c> (pre-consolidation) and
    /// <c>kapacitor codex-hook</c> (pre-rename) — so <c>kcap uninstall</c>
    /// and the upgrade-time refresh both find and rewrite entries written by
    /// older CLI versions.
    /// </summary>
    public static bool EntryReferencesCapacitorCodexHook(JsonNode? entry) {
        if (entry?["hooks"] is not JsonArray hooks) return false;

        foreach (var hook in hooks) {
            if (hook?["command"] is JsonValue jv &&
                jv.TryGetValue<string>(out var cmd) &&
                IsCapacitorCodexHookCommand(cmd)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="command"/> invokes the kcap Codex hook dispatcher —
    /// the current <c>kcap hook --codex</c> marker or the two earlier forms
    /// (<c>kcap codex-hook</c>, <c>kapacitor codex-hook</c>). The single source of truth for
    /// "is this a kcap-owned Codex hook", shared by the hooks.json preflight and the
    /// app-server <c>hooks/list</c> trust classifier.
    /// </summary>
    public static bool IsCapacitorCodexHookCommand(string? command) =>
        command is not null &&
        (command.Contains("kcap hook --codex",    StringComparison.Ordinal)
      || command.Contains("kcap codex-hook",      StringComparison.Ordinal)
      || command.Contains("kapacitor codex-hook", StringComparison.Ordinal));

    /// <summary>
    /// Returns true if every event in <paramref name="events"/> has at least one
    /// hooks.json entry that invokes <c>kcap codex-hook</c>.
    /// </summary>
    public static bool HasCapacitorHooksFor(JsonObject root, IEnumerable<string> events) {
        if (root["hooks"] is not JsonObject hooks) return false;

        foreach (var evt in events) {
            if (hooks[evt] is not JsonArray entries) return false;

            var any = entries.Any(EntryReferencesCapacitorCodexHook);

            if (!any) return false;
        }

        return true;
    }
}
