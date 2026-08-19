using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Builds the SessionStart "coordination notices" text fragment from a server
/// <c>/hooks/session-start</c> response body. Returns plain text, not a JSON
/// envelope — the caller (see <c>SessionStartAdditionalContext</c>) joins this
/// fragment with the others (guidelines, version nudge, team-memory index) and
/// serializes a single Claude Code <c>hookSpecificOutput</c> envelope.
/// <para>
/// The server's terminal-delivery lane returns <c>coordination_notices</c> as a
/// bounded JSON array of <c>{ text }</c> one-line warnings (with an optional
/// "+N more in the notification centre" tail line) about other people's in-flight
/// work that may overlap this session — the same notices that also reach the
/// in-app notification centre and Slack. Each entry renders as one bullet under a
/// <b>"Coordination notices"</b> heading.
/// </para>
/// <para>
/// Returns <c>null</c> when disabled, absent, empty, or malformed — the caller
/// drops null fragments, so a missing or empty field emits nothing (fail-open,
/// same as guideline injection and the memory index).
/// </para>
/// </summary>
static class CoordinationNoticesEmitter {
    /// <summary>
    /// The capability token the CLI advertises on the SessionStart request
    /// (<c>coordination_notices: "v1"</c>). An old/opted-out CLI sends nothing and
    /// the server does no selection/claim/render; a future wire-shape change bumps
    /// this token so a server keyed on the old one simply declines.
    /// </summary>
    internal const string CapabilityVersion = "v1";

    /// <summary>
    /// Returns the coordination-notices block text, or <c>null</c> when there is
    /// nothing to emit (no <c>coordination_notices</c>, all empty, user opted out,
    /// malformed response).
    /// </summary>
    /// <param name="responseNode">The hook response body parsed as a <see cref="JsonNode"/>.</param>
    /// <param name="disabled">True when the user has set <c>disable_coordination_notices</c> on their active profile.</param>
    public static string? BuildFragment(JsonNode? responseNode, bool disabled) {
        if (disabled) return null;
        if (responseNode is not JsonObject obj) return null;
        if (obj["coordination_notices"] is not JsonArray notices || notices.Count == 0) return null;

        var lines = new List<string>();
        foreach (var node in notices) {
            if (node is not JsonObject o) continue;

            string? text;
            try { text = o["text"]?.GetValue<string>(); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(text)) continue;
            lines.Add($"- {text!.Trim()}");
        }

        if (lines.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Coordination notices");
        sb.AppendLine("Heads-up: other people are working on things that may overlap yours. Coordinate before you collide.");
        foreach (var l in lines) sb.AppendLine(l);
        return sb.ToString().TrimEnd();
    }
}
