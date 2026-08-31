using System.Text.Json;
using static Capacitor.Cli.Core.TranscriptProjectionText;

namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex's rollout (`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`): an envelope per line with
/// `type` and `payload`; only `response_item` payloads are conversation, the rest is telemetry.
public sealed class CodexRolloutEvents : ITranscriptProjection {
    public static readonly CodexRolloutEvents Instance = new();

    static readonly string[] InjectedPreludes = [
        "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>", "<permissions instructions>",
    ];

    CodexRolloutEvents() { }

    public IReadOnlyList<AcpEventEnvelope> Project(string line) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return []; }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Str("type") != "response_item" || root.Obj("payload") is not { } payload) return [];
            var ts = root.Str("timestamp");

            return payload.Str("type") switch {
                "message"          => ProjectMessage(payload, ts),
                "function_call"    => [new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: payload.Str("call_id"), ToolName: payload.Str("name"), ToolInputJson: ArgumentsJson(payload.Str("arguments")), TimestampIso: ts)],
                "custom_tool_call" => [new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: payload.Str("call_id"), ToolName: payload.Str("name"), ToolInputJson: WrapAsObject("input", payload.Str("input") ?? ""), TimestampIso: ts)],
                "function_call_output" or "custom_tool_call_output"
                                   => [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: payload.Str("call_id"), ToolResult: Cap(OutputText(payload)), TimestampIso: ts)],
                "reasoning"        => [Reasoning(payload, ts)],
                _                  => [],
            };
        }
    }

    static List<AcpEventEnvelope> ProjectMessage(JsonElement payload, string? ts) {
        if (payload.Arr("content") is not { } blocks) return [];
        switch (payload.Str("role")) {
            case "user": {
                var text = JoinTextBlocks(blocks, "input_text");
                if (text.Length == 0 || IsInjectedPrelude(text)) return [];
                return [new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: text, TimestampIso: ts)];
            }
            case "assistant": {
                var text = JoinTextBlocks(blocks, "output_text");
                return text.Length == 0 ? [] : [new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: text, TimestampIso: ts)];
            }
            default:
                return [];
        }
    }

    static bool IsInjectedPrelude(string text) {
        var trimmed = text.TrimStart();
        foreach (var prelude in InjectedPreludes) {
            if (trimmed.StartsWith(prelude, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static string ArgumentsJson(string? arguments) {
        if (arguments is not null) {
            try {
                using var doc = JsonDocument.Parse(arguments);
                if (doc.RootElement.IsObject) return doc.RootElement.GetRawText();
            } catch (JsonException) { }
        }
        return WrapAsObject("arguments", arguments ?? "");
    }

    static string OutputText(JsonElement payload) =>
        payload.Str("output") ?? (payload.Arr("output") is { } blocks ? JoinTextBlocks(blocks, "input_text") : "");

    static AcpEventEnvelope Reasoning(JsonElement payload, string? ts) {
        var summary = payload.Arr("summary") is { } blocks ? JoinTextBlocks(blocks, "summary_text") : "";
        return new AcpEventEnvelope(
            Kind: AcpEventKind.AssistantThinking,
            Text: summary.Length == 0 ? null : summary,
            ThinkingEncrypted: summary.Length == 0 && payload.Str("encrypted_content") is not null,
            TimestampIso: ts);
    }
}
