namespace Capacitor.Cli.Harness.Cursor;

/// <summary>
/// Maps a Cursor <c>hook_event_name</c> (camelCase) to its server route
/// segment (kebab-case) and flags whether its POST failure should land
/// in the per-session spool (canonical-event-bearing) or be dropped
/// (telemetry-only).
/// </summary>
public static class CursorHookEventMap {
    public readonly record struct Mapping(string RouteSegment, bool SpoolOnFailure);

    // SpoolOnFailure = true for the four canonical-event-bearing hooks
    // (sessionStart, sessionEnd, beforeSubmitPrompt, afterAgentThought).
    // The other four are telemetry-only — failures are accepted lossy.
    static readonly Dictionary<string, Mapping> Map = new(StringComparer.Ordinal) {
        ["sessionStart"]        = new("session-start/cursor",         SpoolOnFailure: true),
        ["sessionEnd"]          = new("session-end/cursor",           SpoolOnFailure: true),
        ["beforeSubmitPrompt"]  = new("user-prompt/cursor",           SpoolOnFailure: true),
        ["afterAgentThought"]   = new("agent-thought/cursor",         SpoolOnFailure: true),
        ["afterAgentResponse"]  = new("agent-response/cursor",        SpoolOnFailure: false),
        ["preToolUse"]          = new("pre-tool-use/cursor",          SpoolOnFailure: false),
        ["postToolUse"]         = new("post-tool-use/cursor",         SpoolOnFailure: false),
        ["postToolUseFailure"]  = new("post-tool-use-failure/cursor", SpoolOnFailure: false),
    };

    public static bool TryResolve(string? eventName, out Mapping mapping) {
        if (string.IsNullOrEmpty(eventName)) { mapping = default; return false; }
        return Map.TryGetValue(eventName, out mapping);
    }
}
