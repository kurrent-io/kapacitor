using System.Text.Json.Nodes;

namespace kapacitor;

/// <summary>
/// Builds the SessionStart <c>additionalContext</c> JSON envelope from the server's
/// session-start hook response (DEV-1676). The server may attach a
/// <c>top_clusters</c> array to the response body summarising recurring lessons
/// from prior sessions in the same repository; the CLI emits that to stdout in
/// the Claude Code SessionStart hook output format so the model receives it as
/// additional context.
/// </summary>
static class SessionGuidelinesEmitter {
    /// <summary>
    /// Returns the JSON envelope to write to stdout, or <c>null</c> if nothing
    /// should be emitted (no top_clusters, all empty, or user opted out).
    /// </summary>
    /// <param name="responseBody">The hook response body returned by the server.</param>
    /// <param name="disabled">True when the user has set <c>disable_session_guidelines</c> on their active profile.</param>
    public static string? BuildAdditionalContext(string responseBody, bool disabled) {
        if (disabled) return null;

        JsonNode? responseNode;

        try {
            responseNode = JsonNode.Parse(responseBody);
        } catch {
            return null;
        }

        return BuildAdditionalContext(responseNode, disabled);
    }

    /// <summary>
    /// Overload taking an already-parsed response node so callers that need
    /// the parsed tree for other purposes (e.g. slug-fallback plan injection)
    /// don't have to parse twice.
    /// </summary>
    public static string? BuildAdditionalContext(JsonNode? responseNode, bool disabled) {
        if (disabled) return null;

        var topClusters = responseNode?["top_clusters"]?.AsArray();

        if (topClusters is not { Count: > 0 }) return null;

        var lines = new List<string>(topClusters.Count);

        foreach (var node in topClusters) {
            string? text = null;

            try {
                text = node?["text"]?.GetValue<string>();
            } catch {
                // Tolerate non-string/missing text entries.
            }

            if (!string.IsNullOrWhiteSpace(text)) lines.Add($"- {text}");
        }

        if (lines.Count == 0) return null;

        var ctx = "Recurring lessons from prior sessions in this repo (no action required unless relevant):\n"
                + string.Join("\n", lines);

        var output = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"]     = "SessionStart",
                ["additionalContext"] = ctx
            }
        };

        return output.ToJsonString();
    }
}
