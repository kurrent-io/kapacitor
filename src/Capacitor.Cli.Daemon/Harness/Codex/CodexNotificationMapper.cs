using System.Globalization;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Translates codex app-server JSON-RPC notifications into the daemon-local wire
/// <see cref="AcpEventEnvelope"/> vocabulary (§2.4 envelope transcript). Stateful — it composes the
/// per-item <see cref="CodexEphemeralAccumulator"/> and the cumulative→delta
/// <see cref="CodexUsageDeltaConverter"/> — so it is one-per-session and driven from the single
/// notification-handling path (NOT thread-safe).
///
/// <para>Two lanes, per §2.4:</para>
/// <list type="bullet">
/// <item><description><b>Canonical</b>: only terminal facts become sequenced envelopes —
/// <c>item/completed</c> snapshots (the one authoritative event for an item's content), the tool-call
/// OPEN on <c>item/started</c> for executions, <c>turn/plan/updated</c> full snapshots, and the
/// per-event token DELTA. Append-only: a completed item is the single event for its content, and its
/// <see cref="AcpEventEnvelope.ItemId"/> lets a viewer finalize the item's transient ephemeral state.</description></item>
/// <item><description><b>Ephemeral</b>: delta notifications become <see cref="AcpEventEnvelope.Ephemeral"/>
/// envelopes carrying the item's ACCUMULATED content-so-far (idempotent replacement at the viewer; no
/// seq, never persisted). A dropped/duplicated ephemeral is harmless and the completed snapshot converges
/// the viewer.</description></item>
/// </list>
/// Every emitted envelope carries a placeholder <see cref="AcpEventEnvelope.Seq"/> of <c>0</c> — the
/// forwarder assigns the real monotonic seq to canonical envelopes and leaves ephemeral ones unsequenced.
/// An unrecognised item type surfaces as a generic-labeled tool call and bumps
/// <see cref="UnmappedKindCount"/> so vocabulary drift is visible rather than silently dropped.
/// </summary>
internal sealed partial class CodexNotificationMapper {
    static readonly IReadOnlyList<AcpEventEnvelope> None = [];

    readonly CodexEphemeralAccumulator _ephemeral = new();
    readonly CodexUsageDeltaConverter  _usage     = new();
    readonly Func<string?>             _modelAtInstant;
    readonly ILogger                   _logger;
    readonly HashSet<string>           _loggedUnknownTypes = new(StringComparer.Ordinal);
    int _unmappedKindCount;

    public CodexNotificationMapper(Func<string?> modelAtInstant, ILogger logger) {
        _modelAtInstant = modelAtInstant;
        _logger         = logger;
    }

    /// <summary>Count of <c>item/completed</c> notifications whose item type this mapper did not
    /// recognise (surfaced generically, never dropped). A rising count means the app-server vocabulary
    /// grew past this mapper — the signal to widen the switch below.</summary>
    public int UnmappedKindCount => _unmappedKindCount;

    /// <summary>Exact post-resume usage baseline (from <c>thread/read</c>) so the next token delta is
    /// exact. Mirrors <see cref="CodexUsageDeltaConverter.SetExactBaseline"/>.</summary>
    public void SetUsageBaseline(CodexTokenUsage baseline) => _usage.SetExactBaseline(baseline);

    /// <summary>Fallback when no exact resume baseline is available: the next usage snapshot becomes the
    /// baseline and emits nothing. Mirrors <see cref="CodexUsageDeltaConverter.BaselineOnNextNotification"/>.</summary>
    public void UsageBaselineOnNextNotification() => _usage.BaselineOnNextNotification();

    /// <summary>Maps ONE app-server notification to its (0..n) envelopes. Never throws: a malformed or
    /// wrong-typed field reads as absent (the <see cref="JsonElementExtensions"/> contract) and yields
    /// an empty result rather than bubbling out and taking down the notification pump.</summary>
    public IReadOnlyList<AcpEventEnvelope> Map(string method, JsonElement? @params) {
        var p = @params ?? default;
        switch (method) {
            case "item/completed":            return MapItemCompleted(p);
            case "item/started":              return MapItemStarted(p);
            case "turn/plan/updated":         return MapPlanUpdated(p);
            case "thread/tokenUsage/updated": return MapTokenUsage(p);

            // Ephemeral live lane — incremental deltas accumulate into content-so-far.
            case "item/agentMessage/delta":           return EphemeralText(AcpEventKind.AssistantText, p);
            case "item/reasoning/textDelta":          return EphemeralText(AcpEventKind.AssistantThinking, p);
            case "item/reasoning/summaryTextDelta":   return EphemeralText(AcpEventKind.AssistantThinking, p);
            case "item/plan/delta":                   return EphemeralText(AcpEventKind.Plan, p);
            case "item/commandExecution/outputDelta": return EphemeralToolProgress(p);
            case "item/fileChange/outputDelta":       return EphemeralToolProgress(p);
            // patchUpdated is a full-snapshot delta (the changes array), not an increment — replace,
            // never accumulate.
            case "item/fileChange/patchUpdated":      return EphemeralPatchSnapshot(p);

            // Everything else (thread/*, turn/started, turn/completed — dispatcher-owned — turn/diff,
            // approval reviews, realtime audio, …) is not transcript content: dropped, not drift.
            default: return None;
        }
    }

    // ── Canonical: completed items are the one authoritative event per item ─────────────────────
    IReadOnlyList<AcpEventEnvelope> MapItemCompleted(JsonElement p) {
        if (p.Obj("item") is not { } item) return None;
        var id   = item.Str("id");
        var type = item.Str("type");
        var ts   = IsoFromMs(p.Num("completedAtMs"));

        // The completed snapshot supersedes any transient ephemeral state for this item.
        if (id is not null) _ephemeral.Complete(id);

        switch (type) {
            case "agentMessage":
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.AssistantText, Text: item.Str("text") ?? "",
                    ItemId: id, TimestampIso: ts));

            case "reasoning":
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.AssistantThinking, Text: RenderReasoning(item),
                    ItemId: id, TimestampIso: ts));

            case "plan":
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.Plan, Text: item.Str("text") ?? "",
                    ItemId: id, TimestampIso: ts));

            case "userMessage":
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.UserMessage, Text: JoinTexts(item.Arr("content")),
                    ItemId: id, TimestampIso: ts));

            case "commandExecution":
                // The tool-call OPEN came from item/started; the completed item is the authoritative result.
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.ToolResult, ToolCallId: id,
                    ToolResult: item.Str("aggregatedOutput") ?? "", ToolIsError: IsCommandError(item),
                    ItemId: id, TimestampIso: ts));

            case "fileChange":
                // A completed file edit surfaces as a PAIRED tool call (carrying the diff) + result (the
                // apply status), mirroring commandExecution/mcp so a consumer never sees an orphan tool
                // call. fileChange has no item/started, so both envelopes come from the completed item.
                return Two(
                    new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolCall, ToolCallId: id, ToolName: "apply_patch",
                        ToolInputJson: RenderChanges(item.Arr("changes")), ToolKind: AcpToolKind.Edit,
                        ItemId: id, TimestampIso: ts),
                    new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolResult, ToolCallId: id, ToolResult: item.Str("status") ?? "",
                        ToolIsError: item.Str("status") is "failed" or "declined", ItemId: id, TimestampIso: ts));

            case "webSearch":
                // Like fileChange, no item/started row — so the completed item carries both halves and a
                // consumer never sees an orphan call. Every action of the one web tool is a fetch.
                return Two(
                    new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolCall, ToolCallId: id, ToolName: "web_search",
                        ToolInputJson: RenderWebSearchOpen(item), ToolKind: AcpToolKind.Fetch,
                        ItemId: id, TimestampIso: ts),
                    new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolResult, ToolCallId: id, ToolResult: Content(item, "results") ?? "",
                        ItemId: id, TimestampIso: ts));

            case "mcpToolCall":
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.ToolResult, ToolCallId: id,
                    ToolResult: RenderMcpResult(item), ToolIsError: IsMcpError(item),
                    ItemId: id, TimestampIso: ts));

            default:
                return Unmapped(type, id, item, ts);
        }
    }

    // ── Canonical: tool-call OPEN for executions (the only item/started that carries a canonical row) ──
    static IReadOnlyList<AcpEventEnvelope> MapItemStarted(JsonElement p) {
        if (p.Obj("item") is not { } item) return None;
        var id   = item.Str("id");
        var type = item.Str("type");
        var ts   = IsoFromMs(p.Num("startedAtMs"));

        switch (type) {
            case "commandExecution":
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.ToolCall, ToolCallId: id, ToolName: "shell",
                    ToolInputJson: RenderCommandOpen(item), ToolKind: CodexToolKinds.Shell(item.Str("command")),
                    ItemId: id, TimestampIso: ts));

            case "mcpToolCall":
                return One(new AcpEventEnvelope(
                    Kind: AcpEventKind.ToolCall, ToolCallId: id, ToolName: McpToolName(item),
                    ToolInputJson: McpArguments(item), ToolKind: AcpToolKind.Other,
                    ItemId: id, TimestampIso: ts));

            // Text/reasoning/plan/userMessage/fileChange have no separate "open" row — their content
            // arrives via deltas + the completed snapshot. An unknown type is counted once at completion,
            // not here (a start+complete pair would otherwise double-count).
            default: return None;
        }
    }

    // ── Canonical: turn-level full plan snapshot → the NEW Plan kind (latest-wins server-side) ────
    static IReadOnlyList<AcpEventEnvelope> MapPlanUpdated(JsonElement p) {
        var text = RenderPlan(p.Arr("plan"), p.Str("explanation"));
        return text is null ? None : One(new AcpEventEnvelope(Kind: AcpEventKind.Plan, Text: text));
    }

    // ── Canonical: cumulative token usage → per-event additive DELTA (attributed to model-at-instant) ──
    IReadOnlyList<AcpEventEnvelope> MapTokenUsage(JsonElement p) {
        if (p.Obj("tokenUsage") is not { } tu || tu.Obj("total") is not { } total) return None;

        var delta = _usage.Convert(CodexTokenUsage.FromTotal(total));
        if (delta is not { } d || d.IsZero) return None; // baseline consumed, or a no-op reading

        return One(new AcpEventEnvelope(
            Kind: AcpEventKind.TokenUsage, Model: _modelAtInstant(),
            UsageInputTokens:           d.InputTokens,
            UsageCachedInputTokens:     d.CachedInputTokens,
            UsageCacheWriteInputTokens: d.CacheWriteInputTokens,
            UsageOutputTokens:          d.OutputTokens,
            UsageReasoningTokens:       d.ReasoningOutputTokens));
    }

    // ── Ephemeral lane ───────────────────────────────────────────────────────────────────────────
    IReadOnlyList<AcpEventEnvelope> EphemeralText(string kind, JsonElement p) {
        var itemId = p.Str("itemId");
        var delta  = p.Str("delta");
        if (itemId is null || delta is null) return None;

        return One(new AcpEventEnvelope(
            Kind: kind, Text: _ephemeral.Accumulate(itemId, delta), Ephemeral: true, ItemId: itemId));
    }

    IReadOnlyList<AcpEventEnvelope> EphemeralToolProgress(JsonElement p) {
        var itemId = p.Str("itemId");
        var delta  = p.Str("delta");
        if (itemId is null || delta is null) return None;

        return One(new AcpEventEnvelope(
            Kind: AcpEventKind.ToolResult, ToolCallId: itemId,
            ToolResult: _ephemeral.Accumulate(itemId, delta), Ephemeral: true, ItemId: itemId));
    }

    static IReadOnlyList<AcpEventEnvelope> EphemeralPatchSnapshot(JsonElement p) {
        var itemId = p.Str("itemId");
        if (itemId is null || p.Arr("changes") is not { } changes) return None;

        // A full-snapshot delta: the changes array IS the cumulative patch, so it replaces (never
        // accumulates — deliberately bypasses _ephemeral, unlike outputDelta which shares this itemId;
        // the completed item's canonical envelopes are authoritative either way).
        return One(new AcpEventEnvelope(
            Kind: AcpEventKind.ToolCall, ToolCallId: itemId, ToolName: "apply_patch",
            ToolInputJson: RenderChanges(changes), ToolKind: AcpToolKind.Edit,
            Ephemeral: true, ItemId: itemId));
    }

    IReadOnlyList<AcpEventEnvelope> Unmapped(string? type, string? id, JsonElement item, string? ts) {
        _unmappedKindCount++;
        if (type is not null && _loggedUnknownTypes.Add(type))
            LogUnknownItemType(_logger, type);

        // Surface the unknown item's content as a generic tool call rather than dropping it — the raw
        // item is a JSON object, so it rides as the tool's arguments for a viewer to inspect.
        return One(new AcpEventEnvelope(
            Kind: AcpEventKind.ToolCall, ToolCallId: id, ToolName: type ?? "unknown",
            ToolInputJson: item.GetRawText(), ToolKind: AcpToolKind.Other,
            ItemId: id, TimestampIso: ts));
    }

    // ── Rendering helpers (pure) ──────────────────────────────────────────────────────────────────
    static IReadOnlyList<AcpEventEnvelope> One(AcpEventEnvelope e) => [e];
    static IReadOnlyList<AcpEventEnvelope> Two(AcpEventEnvelope a, AcpEventEnvelope b) => [a, b];

    static string? IsoFromMs(long? ms) =>
        ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v).ToString("O", CultureInfo.InvariantCulture) : null;

    // Reasoning content/summary are arrays of plain STRINGS (per the pinned schema), so join the string
    // elements directly — falling back to the summary blocks when content is empty.
    static string RenderReasoning(JsonElement item) {
        var content = JoinStrings(item.Arr("content"));
        return content.Length > 0 ? content : JoinStrings(item.Arr("summary"));
    }

    // For a string[] (reasoning content/summary): join the non-empty string elements.
    static string JoinStrings(JsonElement? arr) {
        if (arr is not { } a) return "";
        var parts = new List<string>();
        foreach (var el in a.EnumerateArray())
            if (el.IsString && el.GetString() is { Length: > 0 } s) parts.Add(s);
        return string.Join("\n", parts);
    }

    // For a UserInput[] (userMessage content): each element is an object with a `text` field (image
    // inputs have no text and are skipped).
    static string JoinTexts(JsonElement? arr) {
        if (arr is not { } a) return "";
        var parts = new List<string>();
        foreach (var el in a.EnumerateArray()) {
            var t = el.Str("text");
            if (!string.IsNullOrEmpty(t)) parts.Add(t);
        }
        return string.Join("\n", parts);
    }

    static string? RenderPlan(JsonElement? planArr, string? explanation) {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(explanation)) sb.Append(explanation).Append('\n');
        if (planArr is { } a)
            foreach (var step in a.EnumerateArray()) {
                var text = step.Str("step");
                if (string.IsNullOrEmpty(text)) continue;
                sb.Append("- [").Append(step.Str("status") ?? "").Append("] ").Append(text).Append('\n');
            }
        var s = sb.ToString().TrimEnd('\n');
        return s.Length == 0 ? null : s;
    }

    // A ToolInputJson must be a JSON OBJECT string (the server parses it into the tool's arguments), so
    // the file-change array is wrapped under a "changes" key. Built with Utf8JsonWriter — the JsonNode
    // Add<T> path is not AOT-safe (IL2026/IL3050).
    static string RenderChanges(JsonElement? changes) {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            w.WriteStartArray("changes");
            if (changes is { } a)
                foreach (var ch in a.EnumerateArray()) {
                    w.WriteStartObject();
                    WriteNullable(w, "path", ch.Str("path"));
                    WriteNullable(w, "kind", ch.Str("kind"));
                    WriteNullable(w, "diff", ch.Str("diff"));
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static string RenderCommandOpen(JsonElement item) {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            WriteNullable(w, "command", item.Str("command"));
            WriteNullable(w, "cwd", item.Str("cwd"));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The query is the item's own required field; the action (search / openPage / find-in-page) rides
    // along verbatim, since its variants carry the url or pattern the query alone does not show.
    static string RenderWebSearchOpen(JsonElement item) {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer)) {
            w.WriteStartObject();
            WriteNullable(w, "query", item.Str("query"));
            if (item.Obj("action") is { } action) {
                w.WritePropertyName("action");
                action.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void WriteNullable(Utf8JsonWriter w, string name, string? value) {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    static string McpToolName(JsonElement item) => $"{item.Str("server")}.{item.Str("tool")}";

    // MCP arguments are opaque JSON — forwarded only when they are an object (the server expects a JSON
    // object string for tool arguments); otherwise omitted rather than passing a non-object through.
    static string? McpArguments(JsonElement item) => item.Obj("arguments")?.GetRawText();

    static string RenderMcpResult(JsonElement item) =>
        // Error content wins over result: a failed call that also carries a result body must not render
        // the result as if it succeeded (keeps the payload consistent with IsMcpError's flag).
        Content(item, "error") ?? Content(item, "result") ?? "";

    // A property rendered as a string: the string value if it is a string, else the raw JSON of an
    // object/array. Null when absent or JSON null. Uses the JsonElementExtensions accessors rather than
    // inspecting ValueKind directly.
    static string? Content(JsonElement item, string property) =>
        item.Str(property) ?? item.Obj(property)?.GetRawText() ?? item.Arr(property)?.GetRawText();

    static bool IsMcpError(JsonElement item) =>
        Content(item, "error") is not null || item.Str("status") == "failed";

    static bool IsCommandError(JsonElement item) =>
        item.Str("status") is "failed" or "declined" || item.Num("exitCode") is { } code && code != 0;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "codex app-server: unmapped item type '{ItemType}' surfaced as a generic tool call")]
    static partial void LogUnknownItemType(ILogger logger, string itemType);
}
