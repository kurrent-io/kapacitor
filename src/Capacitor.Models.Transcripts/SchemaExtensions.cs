using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// Reads the extensions map the schema puts on every conversational message; the messages share
/// the property but no interface, so this is the one switch over their types.
public static class SchemaExtensions {
    public static MapField<string, Struct>? Of(object payload) => payload switch {
        UserMessageReceived m         => m.Extensions,
        AssistantTextGenerated m      => m.Extensions,
        AssistantThinkingGenerated m  => m.Extensions,
        AssistantToolCallsGenerated m => m.Extensions,
        ToolResultReceived m          => m.Extensions,
        SessionStarted m              => m.Extensions,
        _                             => null,
    };

    public static Struct? Slug(object payload, string slug) =>
        Of(payload) is { } extensions && extensions.TryGetValue(slug, out var block) ? block : null;

    public static bool Flag(Struct? slug, string field) =>
        slug is not null && slug.Fields.TryGetValue(field, out var v) && v.KindCase == Value.KindOneofCase.BoolValue && v.BoolValue;

    public static string? Text(Struct? slug, string field) =>
        slug is not null && slug.Fields.TryGetValue(field, out var v) && v.KindCase == Value.KindOneofCase.StringValue ? v.StringValue : null;
}
