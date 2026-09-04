using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core;

/// The one place a stored canonical event becomes the chat vocabulary. Vendor display rules
/// (what to strip or skip) sit beside it under Harness/&lt;Vendor&gt;/, never here.
public static class TranscriptEnvelopes {
    public const int ToolResultCap = 4096;
    const string CapMarker = "…";

    public static IReadOnlyList<AcpEventEnvelope> From(CanonicalEvent evt) {
        var ts = evt.RecordTimestamp ?? evt.Timestamp.ToString("O", CultureInfo.InvariantCulture);
        switch (evt.Payload) {
            case UserMessageReceived m:
                return [new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: m.Content, TimestampIso: ts)];
            case AssistantTextGenerated m:
                return [new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: m.Content, TimestampIso: ts)];
            case AssistantThinkingGenerated m: {
                var empty = m.Content.Length == 0;
                return [new AcpEventEnvelope(Kind: AcpEventKind.AssistantThinking, Text: empty ? null : m.Content, ThinkingEncrypted: m.Encrypted || empty, TimestampIso: ts)];
            }
            case AssistantToolCallsGenerated m: {
                var list = new List<AcpEventEnvelope>(m.ToolCalls.Count);
                foreach (var call in m.ToolCalls)
                    list.Add(new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: call.CallId, ToolName: call.ToolName, ToolInputJson: call.Arguments is null ? "{}" : CompactJson(call.Arguments), TimestampIso: ts));
                return list;
            }
            case ToolResultReceived m:
                return [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: m.CallId, ToolResult: Cap(m.Result), TimestampIso: ts)];
            default:
                return [];
        }
    }

    /// At most ToolResultCap units including the marker; a cut that would split a surrogate pair
    /// drops the high half too, so the result can be one unit short of the cap.
    public static string Cap(string text) {
        if (text.Length <= ToolResultCap) return text;
        var cut = ToolResultCap - CapMarker.Length;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return string.Concat(text.AsSpan(0, cut), CapMarker);
    }

    /// Compact JSON for a Struct, written by hand: the protobuf formatter pads its output and
    /// the chat pins exact strings.
    public static string CompactJson(Struct s) {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, s);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Write(Utf8JsonWriter writer, Struct s) {
        writer.WriteStartObject();
        foreach (var (name, value) in s.Fields) {
            writer.WritePropertyName(name);
            Write(writer, value);
        }
        writer.WriteEndObject();
    }

    static void Write(Utf8JsonWriter writer, Value value) {
        switch (value.KindCase) {
            case Value.KindOneofCase.StringValue: writer.WriteStringValue(value.StringValue); break;
            case Value.KindOneofCase.NumberValue: writer.WriteNumberValue(value.NumberValue); break;
            case Value.KindOneofCase.BoolValue:   writer.WriteBooleanValue(value.BoolValue); break;
            case Value.KindOneofCase.StructValue: Write(writer, value.StructValue); break;
            case Value.KindOneofCase.ListValue:
                writer.WriteStartArray();
                foreach (var item in value.ListValue.Values) Write(writer, item);
                writer.WriteEndArray();
                break;
            default: writer.WriteNullValue(); break;
        }
    }
}
