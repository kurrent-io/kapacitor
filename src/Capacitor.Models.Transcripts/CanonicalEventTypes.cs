namespace Capacitor.Models.Transcripts;

/// The name each payload is persisted under. The server pins these against its type map.
public static class CanonicalEventTypes {
    public const string UserMessageReceived         = "UserMessageReceived";
    public const string AssistantTextGenerated      = "AssistantTextGenerated";
    public const string AssistantThinkingGenerated  = "AssistantThinkingGenerated";
    public const string AssistantToolCallsGenerated = "AssistantToolCallsGenerated";
    public const string ToolResultReceived          = "ToolResultReceived";
    public const string SessionStarted              = "SessionStarted";
    public const string UsageApplied                = "UsageApplied";

    public static string Of(object payload) => payload switch {
        Kurrent.Agent.Schema.Events.UserMessageReceived         => UserMessageReceived,
        Kurrent.Agent.Schema.Events.AssistantTextGenerated      => AssistantTextGenerated,
        Kurrent.Agent.Schema.Events.AssistantThinkingGenerated  => AssistantThinkingGenerated,
        Kurrent.Agent.Schema.Events.AssistantToolCallsGenerated => AssistantToolCallsGenerated,
        Kurrent.Agent.Schema.Events.ToolResultReceived          => ToolResultReceived,
        Kurrent.Agent.Schema.Events.SessionStarted              => SessionStarted,
        Transcripts.UsageApplied                                => UsageApplied,
        _ => throw new ArgumentException($"No canonical event type for {payload.GetType().Name}", nameof(payload)),
    };
}
