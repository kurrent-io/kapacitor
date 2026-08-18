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
    /// Returns true if <paramref name="command"/> actually INVOKES the kcap Codex hook dispatcher —
    /// the current <c>kcap hook --codex</c> form or the two earlier ones (<c>kcap codex-hook</c>,
    /// <c>kapacitor codex-hook</c>), matched as the command's real executable + arguments rather than
    /// a substring. Substring matching would let a foreign command spoof ownership by embedding a
    /// marker (<c>evil # kcap codex-hook</c>) and, since the app-server trust classifier seeds trust
    /// for the hooks it recognises and project-scope <c>.codex/hooks.json</c> from an untrusted branch
    /// is overlaid into the reviewer's worktree, that would trust-seed and run an arbitrary hook.
    /// The executable is compared by basename so a path-qualified <c>/usr/local/bin/kcap</c> still
    /// matches; anything else fails closed (recognised as NOT kcap-owned, so never seeded).
    /// The single source of truth for "is this a kcap-owned Codex hook", shared by the hooks.json
    /// preflight and the app-server <c>hooks/list</c> trust classifier.
    /// </summary>
    public static bool IsCapacitorCodexHookCommand(string? command) {
        if (command is null) return false;

        var tokens = command.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        var exe = ExecutableName(tokens[0]);

        // Current form: `kcap hook --codex`.
        if (exe is "kcap" or "kcap.exe" && tokens is [_, "hook", "--codex"]) return true;

        // Legacy forms: `kcap codex-hook` (pre-consolidation), `kapacitor codex-hook` (pre-rename).
        return exe is "kcap" or "kcap.exe" or "kapacitor" or "kapacitor.exe" && tokens is [_, "codex-hook"];
    }

    /// <summary>Basename of a possibly path-qualified executable token, so a path-qualified kcap
    /// binary still matches while a marker buried in a later argument or comment does not.</summary>
    static string ExecutableName(string token) {
        var slash = token.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? token[(slash + 1)..] : token;
    }

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
