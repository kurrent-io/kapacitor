using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Claude;

public sealed class ClaudeTranscriptContext(string idScope) : TranscriptContext {
    /// "{session}:{agent}" for a subagent stream, the bare session id otherwise; attachment ids
    /// hash it.
    public string IdScope { get; } = idScope;
}

/// Claude Code's project transcript (`~/.claude/projects/&lt;slug&gt;/&lt;session&gt;.jsonl`): one JSON
/// record per line, `type` at the root, the API message under `message`.
public sealed class ClaudeTranscriptEvents : ITranscriptProjection {
    public static readonly ClaudeTranscriptEvents Instance = new();

    ClaudeTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new ClaudeTranscriptContext(agentId is null ? sessionId : $"{sessionId}:{agentId}");

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");

            // Only a record the projection reads needs a usable id; every other type is ignored
            // whatever its uuid holds.
            var type = root.Str("type");
            if (type is not ("user" or "assistant")) return ProjectionResult.Empty;

            Guid recordId;
            switch (root.Prop("uuid")) {
                case null:
                    recordId = TranscriptIds.ClaudeFallback(lineNumber, line);
                    break;
                case { } uuid when uuid.IsString && Guid.TryParse(uuid.GetString(), out var parsed):
                    recordId = parsed;
                    break;
                default:
                    return ProjectionResult.Reject("uuid is not a GUID");
            }

            var (at, recordTimestamp) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);
            var record = new Record(recordId, at, recordTimestamp, root.Str("parentUuid"), root.Bool("isSidechain") == true);

            return ProjectionResult.Of(type == "user" ? ProjectUser(root, record) : ProjectAssistant(root, record));
        }
    }

    readonly record struct Record(Guid Id, DateTimeOffset At, string? RecordTimestamp, string? CausedBy, bool IsSidechain) {
        public Timestamp ProtoTimestamp => Timestamp.FromDateTimeOffset(At);
    }

    /// Assigns ids in emission order: the record id to the first event, a block sibling id to each
    /// later one, keyed by the raw index of the block that produced it.
    sealed class Emitter(Record record) {
        readonly List<CanonicalEvent> _events = [];

        public IReadOnlyList<CanonicalEvent> Events => _events;

        public void Add(int blockIndex, IMessage payload, Struct? claudeCode) {
            if (claudeCode is not null) SchemaExtensions.Of(payload)![ClaudeCodeExtension.Slug] = claudeCode;
            var id = _events.Count == 0 ? record.Id : TranscriptIds.ClaudeBlock(record.Id, blockIndex);
            _events.Add(new CanonicalEvent(CanonicalEventTypes.Of(payload), payload, id, record.At, record.RecordTimestamp, record.CausedBy));
        }
    }

    static IReadOnlyList<CanonicalEvent> ProjectUser(JsonElement root, Record record) {
        if (root.Obj("message") is not { } message) return [];
        var emitter    = new Emitter(record);
        var isMeta     = root.Bool("isMeta") == true;
        var originKind = root.Obj("origin")?.Str("kind");

        if (message.Str("content") is { } text) {
            if (!IsDeferredToolsInjection(text))
                emitter.Add(0, UserMessage(text, record), ClaudeCodeExtension.Flags(record.IsSidechain, isMeta, originKind));
            return emitter.Events;
        }
        if (message.Arr("content") is not { } blocks) return [];

        var texts = new List<string>();
        var index = 0;
        var sawResult = false;
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "tool_result":
                    sawResult = true;
                    emitter.Add(index, ToolResult(block, record), ClaudeCodeExtension.Flags(record.IsSidechain, isError: block.Bool("is_error") == true));
                    break;
                case "text":
                    if (block.Str("text") is { } t) texts.Add(t);
                    break;
            }
            index++;
        }
        // Text and image blocks beside tool results are dropped: the results are the record.
        if (sawResult || texts.Count == 0) return emitter.Events;

        var joined = string.Join("\n", texts);
        if (IsDeferredToolsInjection(joined)) return [];
        emitter.Add(0, UserMessage(joined, record), ClaudeCodeExtension.Flags(record.IsSidechain, isMeta, originKind));
        return emitter.Events;
    }

    static UserMessageReceived UserMessage(string text, Record record) =>
        new() { Content = text, Timestamp = record.ProtoTimestamp };

    static ToolResultReceived ToolResult(JsonElement block, Record record) {
        var evt = new ToolResultReceived { CallId = block.Str("tool_use_id") ?? "", Timestamp = record.ProtoTimestamp };
        if (ResultText(block) is { } result) evt.Result = result;
        return evt;
    }

    // A string result as is; an array by its text blocks, or verbatim when it has none.
    static string? ResultText(JsonElement block) {
        if (block.Str("content") is { } text) return text;
        if (block.Arr("content") is not { } blocks) return null;
        var joined = JoinTextBlocks(blocks, "text");
        return joined.Length > 0 || HasTextBlock(blocks) ? joined : blocks.GetRawText();
    }

    static bool HasTextBlock(JsonElement blocks) {
        foreach (var block in blocks.EnumerateArray()) if (block.Str("type") == "text") return true;
        return false;
    }

    static bool IsDeferredToolsInjection(string text) => text.AsSpan().TrimStart().StartsWith("<available-deferred-tools");

    static IReadOnlyList<CanonicalEvent> ProjectAssistant(JsonElement root, Record record) {
        if (root.Obj("message") is not { } message || message.Arr("content") is not { } blocks) return [];
        var emitter = new Emitter(record);
        var index   = 0;
        // Flags are built per block: two messages must never share one Struct instance.
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { Length: > 0 } text)
                        emitter.Add(index, new AssistantTextGenerated { Content = text, Timestamp = record.ProtoTimestamp }, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                case "thinking": {
                    var thinking = new AssistantThinkingGenerated { Content = block.Str("thinking") ?? "", Encrypted = false, Timestamp = record.ProtoTimestamp };
                    if (block.Str("signature") is { } signature) thinking.Signature = signature;
                    emitter.Add(index, thinking, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                }
                case "tool_use": {
                    var call = new AssistantToolCallsGenerated { Timestamp = record.ProtoTimestamp };
                    call.ToolCalls.Add(new ToolCallInfo {
                        CallId    = block.Str("id") ?? "",
                        ToolName  = block.Str("name") ?? "",
                        Arguments = ToolInput(block),
                        ToolKind  = ClaudeToolKinds.Of(block.Str("name")),
                    });
                    emitter.Add(index, call, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                }
            }
            index++;
        }
        return emitter.Events;
    }

    // Arguments is always an object: a non-object input is wrapped, an absent one is empty.
    static Struct ToolInput(JsonElement block) =>
        block.Obj("input") is { } obj ? StructOf(obj)
        : block.Prop("input") is { } value ? Wrap("input", value)
        : new Struct();
}
