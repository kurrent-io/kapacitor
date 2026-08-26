using System.Text.Json;
using System.Text.RegularExpressions;
using static Capacitor.Cli.Core.TranscriptProjectionText;

namespace Capacitor.Cli.Core.Harness.Claude;

/// Claude Code's project transcript (`~/.claude/projects/&lt;slug&gt;/&lt;session&gt;.jsonl`): one JSON
/// record per line, `type` at the root, the API message under `message`.
public sealed partial class ClaudeTranscriptEvents : ITranscriptProjection {
    public static readonly ClaudeTranscriptEvents Instance = new();

    ClaudeTranscriptEvents() { }

    public IReadOnlyList<AcpEventEnvelope> Project(string line) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException) { return []; }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject || root.Bool("isSidechain") == true) return [];
            var ts = root.Str("timestamp");
            return root.Str("type") switch {
                "user"      => root.Bool("isMeta") == true ? [] : ProjectUser(root, ts),
                "assistant" => ProjectAssistant(root, ts),
                _           => [],
            };
        }
    }

    static List<AcpEventEnvelope> ProjectUser(JsonElement root, string? ts) {
        var result = new List<AcpEventEnvelope>();
        if (root.Obj("message") is not { } message) return result;

        if (message.Str("content") is { } text) {
            AddUserText(result, text, ts);
            return result;
        }
        if (message.Arr("content") is not { } blocks) return result;

        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { } t) AddUserText(result, t, ts);
                    break;
                case "tool_result":
                    result.Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolResult,
                        ToolCallId: block.Str("tool_use_id"),
                        ToolResult: Cap(ToolResultText(block)),
                        ToolIsError: block.Bool("is_error") == true,
                        TimestampIso: ts));
                    break;
            }
        }
        return result;
    }

    static string ToolResultText(JsonElement block) =>
        block.Str("content") ?? (block.Arr("content") is { } blocks ? JoinTextBlocks(blocks, "text") : "");

    static void AddUserText(List<AcpEventEnvelope> result, string raw, string? ts) {
        var text = StripWrappers(raw);
        if (text.Length == 0) return;
        result.Add(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: text, TimestampIso: ts));
    }

    static List<AcpEventEnvelope> ProjectAssistant(JsonElement root, string? ts) {
        var result = new List<AcpEventEnvelope>();
        if (root.Obj("message") is not { } message || message.Arr("content") is not { } blocks) return result;
        var model = message.Str("model");

        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { Length: > 0 } text)
                        result.Add(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: text, Model: model, TimestampIso: ts));
                    break;
                case "thinking": {
                    var thinking = block.Str("thinking");
                    result.Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.AssistantThinking,
                        Text: string.IsNullOrEmpty(thinking) ? null : thinking,
                        ThinkingEncrypted: string.IsNullOrEmpty(thinking),
                        Model: model, TimestampIso: ts));
                    break;
                }
                case "tool_use":
                    result.Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolCall,
                        ToolCallId: block.Str("id"),
                        ToolName: block.Str("name"),
                        ToolInputJson: InputJson(block),
                        Model: model, TimestampIso: ts));
                    break;
            }
        }
        return result;
    }

    // ToolInputJson must always be a JSON object string: a non-object input is wrapped
    // rather than copied, and an absent one becomes the empty object.
    static string InputJson(JsonElement block) =>
        block.Obj("input") is { } obj ? obj.GetRawText()
        : block.Prop("input") is { } value ? WrapAsObject("input", value)
        : "{}";

    /// Removes the blocks Claude Code injects around a user turn — reminders and slash-command
    /// echoes — so only what the user typed remains.
    internal static string StripWrappers(string text) => Wrappers().Replace(text, "").Trim();

    [GeneratedRegex(@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-caveat)>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex Wrappers();
}
