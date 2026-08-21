using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// Discriminator for <see cref="PiRpcFrame.Kind"/> — Pi's JSONL-RPC protocol has exactly one
/// top-level frame shape carrying <c>"type":"response"</c> (a reply to a command this daemon sent,
/// correlated by <see cref="PiRpcFrame.Id"/>); every other object carrying a non-empty string
/// <c>"type"</c> is an <see cref="Event"/> (Pi-initiated, unsolicited); an object with no
/// recognizable string <c>"type"</c> field is <see cref="Unknown"/> — schema drift, never thrown on.
/// </summary>
internal enum PiRpcFrameKind {
    Response,
    Event,
    Unknown,
}

/// <summary>
/// One parsed line of Pi's JSONL-RPC wire protocol — mirrors <c>AntigravityEvent</c>'s role for agy's
/// NDJSON. <see cref="Root"/> is a CLONED <see cref="JsonElement"/> (the backing
/// <see cref="JsonDocument"/> is disposed inside <see cref="PiRpc.TryParseLine"/>), so it stays valid
/// for however long the caller holds this frame — callers read shape-specific fields off it directly
/// (e.g. <see cref="PiRpcFrame.Type"/>-specific payload fields for a <see cref="PiRpcFrameKind.Event"/>,
/// or <c>"data"</c>/<c>"error"</c> for a <see cref="PiRpcFrameKind.Response"/>) rather than this
/// record duplicating every possible field.
/// </summary>
internal sealed record PiRpcFrame(
    PiRpcFrameKind Kind,
    string         Type,
    string?        Id,
    bool?          Success,
    JsonElement    Root
);

/// <summary>
/// Pure protocol layer for hosted Pi: parses one JSONL-RPC line into a <see cref="PiRpcFrame"/>,
/// translates an event frame into the daemon's canonical <see cref="AcpEventEnvelope"/> transcript
/// events, and builds the JSON text for the three commands this daemon sends to Pi in PR-1
/// (<c>prompt</c>/<c>abort</c>/<c>get_state</c> — <c>set_model</c> is PR-2's, see the reviewer/model
/// lane). No process, no I/O, no state — see <c>AntigravityNdjson</c> for the structural template
/// this mirrors.
///
/// Commands are hand-built as JSON text (never reflection-based serialization — this runs AOT) using
/// <see cref="System.Text.Json.Nodes.JsonObject"/>, matching <c>AcpRpc.cs</c>'s idiom of keeping the
/// wire shape explicit rather than riding a shared source-gen context whose property-naming policy
/// would need to special-case this one vendor's camelCase field names
/// (<c>streamingBehavior</c>) against the rest of the codebase's snake_case
/// <see cref="CapacitorJsonContext"/>.
/// </summary>
internal static class PiRpc {
    const int ExtensionNoteTextCap = 500;

    /// <summary>
    /// Parses one JSONL-RPC line. Returns <see langword="null"/> for a blank/whitespace-only line or
    /// malformed JSON — never throws. A trailing <c>\r</c> (CRLF line endings) is tolerated. A parsed
    /// JSON value that is not an object also returns <see langword="null"/> (Pi's protocol has no
    /// bare-array or bare-scalar frame).
    /// </summary>
    public static PiRpcFrame? TryParseLine(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var trimmed = line.AsSpan().TrimEnd('\r');

        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(trimmed.ToString());
        } catch (JsonException) {
            return null;
        }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return null;

            var type = root.Str("type");
            var kind = type switch {
                "response"           => PiRpcFrameKind.Response,
                { Length: > 0 }      => PiRpcFrameKind.Event,
                _                    => PiRpcFrameKind.Unknown,
            };

            return new PiRpcFrame(
                Kind: kind,
                Type: type ?? "",
                Id: root.Str("id"),
                Success: root.Bool("success"),
                // Clone: `doc` is disposed at the end of this `using` block, and JsonElement values
                // sourced from a disposed JsonDocument throw on access.
                Root: root.Clone());
        }
    }

    /// <summary>
    /// Translates ONE event <paramref name="frame"/> into zero or more <see cref="AcpEventEnvelope"/>s.
    /// A <see cref="PiRpcFrameKind.Response"/> or <see cref="PiRpcFrameKind.Unknown"/> frame always
    /// yields <c>[]</c> — a response is command-correlation plumbing the runtime layer consumes
    /// directly, not transcript content.
    ///
    /// <list type="bullet">
    /// <item><description><c>message_end</c> with an assistant message maps each content item, IN
    /// ORDER, to <c>assistant_text</c> / <c>assistant_thinking</c> / <c>tool_call</c>, then appends
    /// ONE trailing <c>usage</c> envelope when the message carries a <c>usage</c> block. Every
    /// envelope's <see cref="AcpEventEnvelope.Model"/> is the message's own <c>"model"</c> field, else
    /// <paramref name="fallbackModel"/>.</description></item>
    /// <item><description><c>message_end</c> with a user message maps to ONE <c>user_message</c>
    /// envelope carrying the concatenated text — content may be a plain string or a content-part
    /// array (only <c>text</c> parts contribute; a user message has no thinking/toolCall parts).</description></item>
    /// <item><description><c>tool_execution_end</c> maps to ONE <c>tool_result</c>. Upstream's
    /// <c>result</c> is an OBJECT — <c>{"content":[{"type":"text","text":".."}],"details":{..}}</c> —
    /// so the text is the concatenation of its <c>content[]</c> items' <c>text</c> fields (the same
    /// content-part concatenation <see cref="TranslateUserMessage"/> uses). A plain string
    /// <c>result</c> is used verbatim (schema drift, tolerated); anything else — an object with no
    /// <c>content</c> array, or any other shape — falls back to <c>GetRawText()</c> rather than
    /// dropping the result, so this never throws on an unexpected shape.</description></item>
    /// <item><description><c>extension_error</c> maps to ONE <c>system_note</c>, its text capped at
    /// <see cref="ExtensionNoteTextCap"/> characters.</description></item>
    /// <item><description><c>extension_ui_request</c> — a dialog or fire-and-forget UI call from a
    /// user's OWN Pi extension (<c>KCAP_PI_PURE</c> only stands down kcap's own extension) — maps to
    /// ONE <c>system_note</c> naming the request's <c>method</c>, capped at
    /// <see cref="ExtensionNoteTextCap"/> characters. A dialog method (<c>select</c>/<c>confirm</c>/
    /// <c>input</c>/<c>editor</c>) blocks the child on stdin for an <c>extension_ui_response</c> this
    /// daemon never sends, so without this the turn stalls silently; the note is emitted for every
    /// method, not just the blocking ones, since a hosted viewer has no way to tell them apart
    /// either.</description></item>
    /// <item><description>Every other known event (<c>agent_start</c>/<c>agent_end</c>/
    /// <c>agent_settled</c>/<c>turn_start</c>/<c>turn_end</c>/<c>message_start</c>/
    /// <c>message_update</c>/<c>bash_execution_update</c>) and any unrecognized type yield
    /// <c>[]</c> — deliberately no <c>system_note</c> spam for known-but-untranslated events, and no
    /// debug logging either; the only place this protocol layer's caller logs anything is the read
    /// pump, and only for a frame with no recognizable string <c>type</c> at all (see
    /// <see cref="PiRpcFrameKind.Unknown"/>) — a known-or-unknown EVENT type that yields
    /// <c>[]</c> here is silent by design.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<AcpEventEnvelope> ToEnvelopes(PiRpcFrame frame, string? fallbackModel) {
        if (frame.Kind != PiRpcFrameKind.Event) return [];

        return frame.Type switch {
            "message_end"          => TranslateMessageEnd(frame.Root, fallbackModel),
            "tool_execution_end"   => TranslateToolExecutionEnd(frame.Root),
            "extension_error"      => TranslateExtensionError(frame.Root),
            "extension_ui_request" => TranslateExtensionUiRequest(frame.Root),
            _                      => [],
        };
    }

    static IReadOnlyList<AcpEventEnvelope> TranslateMessageEnd(JsonElement root, string? fallbackModel) {
        if (root.Obj("message") is not { } message) return [];

        var role = message.Str("role");
        return role switch {
            "assistant" => TranslateAssistantMessage(message, fallbackModel),
            "user"      => TranslateUserMessage(message),
            _           => [],
        };
    }

    static IReadOnlyList<AcpEventEnvelope> TranslateAssistantMessage(JsonElement message, string? fallbackModel) {
        var model = message.Str("model") ?? fallbackModel;

        List<AcpEventEnvelope>? envelopes = null;
        if (message.Arr("content") is { } content) {
            foreach (var item in content.EnumerateArray()) {
                if (!item.IsObject) continue;

                switch (item.Str("type")) {
                    case "text":
                        if (item.Str("text") is { } text) {
                            (envelopes ??= []).Add(new AcpEventEnvelope(
                                Kind: AcpEventKind.AssistantText,
                                Text: text,
                                Model: model));
                        }
                        break;

                    case "thinking":
                        if (item.Str("thinking") is { } thinking) {
                            (envelopes ??= []).Add(new AcpEventEnvelope(
                                Kind: AcpEventKind.AssistantThinking,
                                Text: thinking,
                                Model: model));
                        }
                        break;

                    case "toolCall":
                        (envelopes ??= []).Add(new AcpEventEnvelope(
                            Kind: AcpEventKind.ToolCall,
                            ToolCallId: item.Str("id"),
                            ToolName: item.Str("name"),
                            ToolInputJson: item.Obj("arguments")?.GetRawText(),
                            Model: model));
                        break;
                }
            }
        }

        if (message.Obj("usage") is { } usage) {
            (envelopes ??= []).Add(new AcpEventEnvelope(
                Kind: AcpEventKind.Usage,
                Model: model,
                ContextUsedTokens: usage.Num("input")));
        }

        return envelopes ?? [];
    }

    static IReadOnlyList<AcpEventEnvelope> TranslateUserMessage(JsonElement message) {
        if (!message.TryGetProperty("content", out var content)) return [];

        string text;
        if (content.IsString) {
            text = content.GetString() ?? "";
        } else if (content.IsArray) {
            var sb = new System.Text.StringBuilder();
            foreach (var item in content.EnumerateArray()) {
                if (item.IsObject && item.Str("type") == "text" && item.Str("text") is { } t) sb.Append(t);
            }
            text = sb.ToString();
        } else {
            return [];
        }

        return [new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: text)];
    }

    static IReadOnlyList<AcpEventEnvelope> TranslateToolExecutionEnd(JsonElement root) {
        var toolCallId = root.Str("toolCallId");
        var isError    = root.Bool("isError") == true;

        string? resultText = null;
        if (root.TryGetProperty("result", out var result)) resultText = ExtractToolResultText(result);

        return [new AcpEventEnvelope(
            Kind: AcpEventKind.ToolResult,
            ToolCallId: toolCallId,
            ToolResult: resultText,
            ToolIsError: isError)];
    }

    /// <summary>Upstream's <c>result</c> is normally an object carrying <c>content[]</c> — the same
    /// content-part shape <see cref="TranslateUserMessage"/> concatenates, so this reuses that
    /// approach rather than a second copy of it. A plain string is schema drift, tolerated verbatim;
    /// anything else (an object with no <c>content</c> array, an array, a number, …) falls back to
    /// <c>GetRawText()</c> so a result is never silently dropped.</summary>
    static string? ExtractToolResultText(JsonElement result) {
        if (result.IsString) return result.GetString();
        if (result.IsNull) return null;

        if (result.IsObject && result.Arr("content") is { } content) {
            var sb = new System.Text.StringBuilder();
            foreach (var item in content.EnumerateArray()) {
                if (item.IsObject && item.Str("type") == "text" && item.Str("text") is { } t) sb.Append(t);
            }
            return sb.ToString();
        }

        return result.GetRawText();
    }

    static IReadOnlyList<AcpEventEnvelope> TranslateExtensionError(JsonElement root) {
        var error = root.Str("error");
        if (error is null) return [];

        return [new AcpEventEnvelope(Kind: AcpEventKind.SystemNote, Text: Cap(error))];
    }

    /// <summary>See the <see cref="ToEnvelopes"/> class doc's <c>extension_ui_request</c> item: a
    /// dialog method blocks the child waiting for an <c>extension_ui_response</c> this daemon never
    /// sends, so a hosted turn that hits one stalls with no other signal anywhere in the
    /// transcript.
    ///
    /// <para><b>Recovery.</b> A dialog method (<c>select</c>/<c>confirm</c>/<c>input</c>/
    /// <c>editor</c>) leaves the child BLOCKED on stdin waiting for an <c>extension_ui_response</c>
    /// this daemon never sends; this note is the only signal anywhere that it happened. The runtime
    /// does not synthesize a response or auto-abort in PR-1 — the only way out is termination
    /// (graceful stop → abort → terminate).</para></summary>
    static IReadOnlyList<AcpEventEnvelope> TranslateExtensionUiRequest(JsonElement root) {
        var method = root.Str("method") ?? "unknown";

        return [new AcpEventEnvelope(
            Kind: AcpEventKind.SystemNote,
            Text: Cap($"Pi extension requested interactive input ({method}); hosted sessions cannot "
                    + "answer, the turn may stall."))];
    }

    static string Cap(string text) => text.Length > ExtensionNoteTextCap ? text[..ExtensionNoteTextCap] : text;

    // ---- Command builders ----
    //
    // Hand-built via JsonObject rather than a source-gen context: these shapes are small, fixed, and
    // vendor-specific (camelCase, unlike the rest of this codebase's snake_case wire contracts), so a
    // one-off object literal is clearer than teaching a shared JsonSerializerContext a per-command
    // naming exception.

    public static string PromptCommand(string id, string message) =>
        new System.Text.Json.Nodes.JsonObject {
            ["id"]                 = id,
            ["type"]               = "prompt",
            ["message"]            = message,
            ["streamingBehavior"]  = "followUp",
        }.ToJsonString();

    public static string AbortCommand(string id) =>
        new System.Text.Json.Nodes.JsonObject {
            ["id"]   = id,
            ["type"] = "abort",
        }.ToJsonString();

    public static string GetStateCommand(string id) =>
        new System.Text.Json.Nodes.JsonObject {
            ["id"]   = id,
            ["type"] = "get_state",
        }.ToJsonString();
}
