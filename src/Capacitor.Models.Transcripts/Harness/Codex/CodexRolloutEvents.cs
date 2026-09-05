using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Codex;

public sealed class CodexRolloutContext : TranscriptContext { }

/// Codex's rollout (`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`): an envelope per line with
/// `type` and `payload`; only `response_item` payloads are conversation, the rest is telemetry.
public sealed class CodexRolloutEvents : ITranscriptProjection {
    public static readonly CodexRolloutEvents Instance = new();

    CodexRolloutEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new CodexRolloutContext();

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("type") != "response_item" || root.Obj("payload") is not { } payload) return ProjectionResult.Empty;

            var (at, recordTimestamp) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);
            var ts = Timestamp.FromDateTimeOffset(at);

            IMessage? evt = payload.Str("type") switch {
                "message"          => Message(payload, ts),
                "function_call"    => ToolCall(payload, ArgumentsStruct(payload.Str("arguments")), ts),
                "custom_tool_call" => ToolCall(payload, Wrap("input", payload.Str("input") ?? ""), ts),
                "function_call_output" or "custom_tool_call_output"
                                   => new ToolResultReceived { CallId = payload.Str("call_id") ?? "", Result = OutputText(payload), Timestamp = ts },
                "reasoning"        => Reasoning(payload, ts),
                _                  => null,
            };
            if (evt is null) return ProjectionResult.Empty;
            return ProjectionResult.Of([new CanonicalEvent(CanonicalEventTypes.Of(evt), evt, TranscriptIds.CodexRecord(line), at, recordTimestamp)]);
        }
    }

    static IMessage? Message(JsonElement payload, Timestamp ts) {
        if (payload.Arr("content") is not { } blocks) return null;
        switch (payload.Str("role")) {
            case "user": {
                var text = JoinTextBlocks(blocks, "input_text");
                return text.Length == 0 ? null : new UserMessageReceived { Content = text, Timestamp = ts };
            }
            case "assistant": {
                var text = JoinTextBlocks(blocks, "output_text");
                return text.Length == 0 ? null : new AssistantTextGenerated { Content = text, Timestamp = ts };
            }
            default:
                return null;
        }
    }

    static AssistantToolCallsGenerated ToolCall(JsonElement payload, Struct arguments, Timestamp ts) {
        var call = new AssistantToolCallsGenerated { Timestamp = ts };
        call.ToolCalls.Add(new ToolCallInfo { CallId = payload.Str("call_id") ?? "", ToolName = payload.Str("name") ?? "", Arguments = arguments });
        return call;
    }

    // `arguments` is a JSON string; an object parses as the struct, anything else is wrapped.
    static Struct ArgumentsStruct(string? arguments) {
        if (arguments is not null) {
            try {
                using var doc = JsonDocument.Parse(arguments);
                if (doc.RootElement.IsObject) return StructOf(doc.RootElement);
            } catch (JsonException) { }
        }
        return Wrap("arguments", arguments ?? "");
    }

    static string OutputText(JsonElement payload) =>
        payload.Str("output") ?? (payload.Arr("output") is { } blocks ? JoinTextBlocks(blocks, "input_text") : "");

    static AssistantThinkingGenerated Reasoning(JsonElement payload, Timestamp ts) {
        var summary = payload.Arr("summary") is { } blocks ? JoinTextBlocks(blocks, "summary_text") : "";
        return new AssistantThinkingGenerated {
            Content   = summary,
            Encrypted = summary.Length == 0 && payload.Str("encrypted_content") is not null,
            Timestamp = ts,
        };
    }
}
