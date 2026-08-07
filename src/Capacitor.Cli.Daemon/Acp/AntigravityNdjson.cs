// src/Capacitor.Cli.Daemon/Acp/AntigravityNdjson.cs
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Discriminator for <see cref="AntigravityEvent.Kind"/> — agy's <c>--output-format stream-json</c>
/// NDJSON has exactly three known top-level <c>event</c> values (<c>init</c>/<c>step_update</c>/
/// <c>result</c>); anything else, including a missing <c>event</c> field, is <see cref="Unknown"/> so
/// a future/unrecognized variant is dropped rather than thrown on.
/// </summary>
internal enum AntigravityEventKind {
    Init,
    StepUpdate,
    Result,
    Unknown,
}

/// <summary>
/// Reduced, AOT-friendly DTO for one agy NDJSON line — mirrors <c>AcpSessionUpdate</c>'s role for
/// ACP updates. Flat and non-polymorphic: every field below is nullable and only ever populated for
/// the <see cref="Kind"/> it belongs to (see <see cref="AntigravityNdjson.TryParseLine"/>).
///
/// The usage counters (<see cref="InputTokens"/>/<see cref="OutputTokens"/>/
/// <see cref="ThinkingTokens"/>/<see cref="CacheReadTokens"/>/<see cref="TotalTokens"/>) are agy's
/// per-step token breakdown, observed live on a <c>step_update</c> whose <c>state</c> is
/// <c>DONE</c> — captured fixtures satisfy <c>input_tokens + output_tokens == total_tokens</c>, so
/// <c>thinking_tokens</c> reads as a subset of <c>output_tokens</c> rather than additional to it.
/// A terminal <c>result</c> carries the same shape of usage block; it is captured here too even
/// though this task's mapping does not yet surface it (the runtime reads <see cref="Status"/> off
/// this same event to drive logical-terminal state).
///
/// The tool fields (<see cref="ToolName"/>/<see cref="ToolInputJson"/>/<see cref="ToolOutput"/>/
/// <see cref="ToolErrorType"/>/<see cref="ToolErrorMessage"/>) belong to a <c>step_update</c> whose
/// <c>step_type</c> is <c>"tool"</c> — captured live across all three states a tool step reaches:
/// <c>ACTIVE</c> (the call itself — name + parameters), <c>DONE</c> (a string <c>output</c>), and
/// <c>ERROR</c> (an object <c>error</c>). <c>ERROR</c> is an ordinary terminal state for that step,
/// not a parse failure — agy soft-denies rather than crashing.
/// </summary>
internal sealed record AntigravityEvent(
    AntigravityEventKind Kind,
    string? ConversationId  = null,
    string? Cwd             = null, // init only
    long?   StepIndex       = null, // step_update only
    string? State           = null, // step_update only, e.g. "ACTIVE" / "DONE" / "ERROR"
    string? StepType        = null, // step_update only, e.g. "agent_response" / "checkpoint" / "tool" / "unknown"
    string? TextDelta       = null, // step_update only
    string? Status          = null, // result only, e.g. "SUCCESS"
    long?   InputTokens     = null,
    long?   OutputTokens    = null,
    long?   ThinkingTokens  = null,
    long?   CacheReadTokens = null,
    long?   TotalTokens     = null,
    string? ToolName        = null, // step_type "tool" only — tool_name, falling back to tool_info.name
    string? ToolInputJson   = null, // step_type "tool" only — tool_info.parameters, verbatim JSON text
    string? ToolOutput      = null, // step_type "tool" only, state DONE — tool_info.output
    string? ToolErrorType   = null, // step_type "tool" only, state ERROR — tool_info.error.type
    string? ToolErrorMessage = null // step_type "tool" only, state ERROR — tool_info.error.message
);

/// <summary>
/// Pure translation from agy's NDJSON lines to the daemon's canonical <see cref="AcpEventEnvelope"/>
/// transcript events — no processes, no I/O, no state beyond what a caller explicitly threads
/// through <see cref="AntigravityStepAccumulator"/>. See
/// <c>docs/superpowers/specs/2026-08-06-ai1414-agy-unattended-reviewer-design.md</c> §5.4.
///
/// Neither <see cref="TryParseLine"/> nor <see cref="ToEnvelopes"/> stamps
/// <see cref="AcpEventEnvelope.Seq"/>/<see cref="AcpEventEnvelope.TimestampIso"/> — both are
/// caller/runtime-owned downstream, the same split <c>AcpEventTranslator</c> uses for the ACP path
/// (<c>AcpTranscriptForwarder</c> reassigns the real monotonic seq on send). A <see langword="null"/>
/// <c>TimestampIso</c> is harmless: the server falls back to "now".
///
/// <b>Still out of scope here</b>: surfacing a tool's soft-denial as a <c>system_note</c> is added
/// by a later, separately-scoped task (a sibling plan's "surface soft-denials as system_note" task,
/// which extends this same file) — an <c>ERROR</c> tool step maps only to a <c>tool_result</c> below,
/// nothing more.
/// </summary>
internal static class AntigravityNdjson {
    /// <summary>
    /// Parses one NDJSON line into an <see cref="AntigravityEvent"/>. Returns
    /// <see langword="null"/> for a blank line or malformed JSON — never throws. A line that parses
    /// as JSON but carries an unrecognized (or missing) <c>event</c> discriminator still returns a
    /// non-null event with <see cref="AntigravityEventKind.Unknown"/>, so a caller can tell "nothing
    /// to read" (blank/malformed) apart from "read something we don't understand yet" (schema
    /// drift) — the same distinction <c>AcpSessionUpdate</c>'s reduction makes for ACP.
    /// </summary>
    public static AntigravityEvent? TryParseLine(string line) {
        if (string.IsNullOrWhiteSpace(line)) return null;

        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(line);
        } catch (JsonException) {
            return null;
        }

        using (doc) {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            return root.Str("event") switch {
                "init"        => ParseInit(root),
                "step_update" => ParseStepUpdate(root),
                "result"      => ParseResult(root),
                _             => new AntigravityEvent(AntigravityEventKind.Unknown),
            };
        }
    }

    static AntigravityEvent ParseInit(JsonElement root) =>
        new(
            AntigravityEventKind.Init,
            ConversationId: root.Str("conversation_id"),
            Cwd: root.Obj("init")?.Str("cwd"));

    static AntigravityEvent ParseStepUpdate(JsonElement root) {
        // A malformed/schema-drifted line that named itself "step_update" but has no such object is
        // treated as Unknown rather than a step_update with every field null — the caller (the step
        // accumulator) requires a StepIndex to do anything, and a real one is never absent.
        if (root.Obj("step_update") is not { } su) return new AntigravityEvent(AntigravityEventKind.Unknown);

        var usage    = su.Obj("usage");
        var toolInfo = su.Obj("tool_info");
        var toolErr  = toolInfo?.Obj("error");

        return new AntigravityEvent(
            AntigravityEventKind.StepUpdate,
            ConversationId: su.Str("conversation_id"),
            StepIndex: su.Num("step_index"),
            State: su.Str("state"),
            StepType: su.Str("step_type"),
            TextDelta: su.Str("text_delta"),
            InputTokens: usage?.Num("input_tokens"),
            OutputTokens: usage?.Num("output_tokens"),
            ThinkingTokens: usage?.Num("thinking_tokens"),
            CacheReadTokens: usage?.Num("cache_read_tokens"),
            TotalTokens: usage?.Num("total_tokens"),
            // tool_name is the top-level field agy actually sends; tool_info.name is the fallback
            // seen alongside it — never observed to differ, but the top-level field is preferred.
            ToolName: su.Str("tool_name") ?? toolInfo?.Str("name"),
            ToolInputJson: toolInfo?.Obj("parameters")?.GetRawText(),
            ToolOutput: toolInfo?.Str("output"),
            ToolErrorType: toolErr?.Str("type"),
            ToolErrorMessage: toolErr?.Str("message"));
    }

    static AntigravityEvent ParseResult(JsonElement root) {
        if (root.Obj("result") is not { } result) return new AntigravityEvent(AntigravityEventKind.Unknown);

        var usage = result.Obj("usage");
        return new AntigravityEvent(
            AntigravityEventKind.Result,
            ConversationId: result.Str("conversation_id"),
            Status: result.Str("status"),
            InputTokens: usage?.Num("input_tokens"),
            OutputTokens: usage?.Num("output_tokens"),
            ThinkingTokens: usage?.Num("thinking_tokens"),
            CacheReadTokens: usage?.Num("cache_read_tokens"),
            TotalTokens: usage?.Num("total_tokens"));
    }

    /// <summary>
    /// Translates ONE <paramref name="evt"/> directly into envelopes — only
    /// <see cref="AntigravityEventKind.Init"/> yields anything here.
    /// <see cref="AntigravityEventKind.Result"/> NEVER yields a <c>session_ended</c> envelope: the
    /// server's <c>EndAgentSession</c> owns termination, and the runtime instead reads
    /// <see cref="AntigravityEvent.Status"/> off this same event to drive its own logical-terminal
    /// state (a translation concern, not this pure mapper's). <see cref="AntigravityEventKind.StepUpdate"/>
    /// always yields <c>[]</c> here — a single step_update line is never enough on its own; text,
    /// tool calls/results, and usage all aggregate per step and only become envelopes through
    /// <see cref="AntigravityStepAccumulator.Flush"/>. <see cref="AntigravityEventKind.Unknown"/>
    /// yields <c>[]</c>.
    /// </summary>
    public static IReadOnlyList<AcpEventEnvelope> ToEnvelopes(AntigravityEvent evt, string? model) =>
        evt.Kind switch {
            AntigravityEventKind.Init =>
                [new AcpEventEnvelope(
                    Kind: AcpEventKind.SessionStarted,
                    Cwd: evt.Cwd,
                    Model: model,
                    RawSessionId: evt.ConversationId)],

            _ => [],
        };
}

/// <summary>
/// Per-step accumulator for agy's <c>text_delta</c> stream, per-step usage counters, and a tool
/// step's call/result lifecycle — the small piece of state <see cref="AntigravityNdjson"/> itself
/// deliberately stays free of. One instance tracks however many step indices the caller feeds it
/// concurrently; a step's buffer is dropped once it reaches a terminal state, so a caller that never
/// revisits a step index leaks nothing.
///
/// Deltas accumulate regardless of <c>state</c> — a real fixture shows the tail of a text stream
/// arriving on the SAME event that flips a step to <c>DONE</c>, so a delta must never be dropped
/// just because its event happens to be the terminal one for that step. Text/usage envelopes only
/// ever flush for a step whose LATEST <see cref="AntigravityEvent.State"/> is terminal
/// (<c>DONE</c> or <c>ERROR</c> — both are ordinary terminal states for a step, not protocol
/// violations) — never a fabricated envelope, never a still-<c>ACTIVE</c> step's text.
///
/// A <c>step_type: "tool"</c> step is the one case with a THIRD envelope shape and an emission
/// point earlier than terminal: its <c>ACTIVE</c> state carries the call itself (name + parameters),
/// which flushes as a <c>tool_call</c> as soon as it's seen — not deferred to terminal, since a long
/// running tool's call is meaningful transcript content on its own. Its terminal state
/// (<c>DONE</c> with a string <c>output</c>, or <c>ERROR</c> with an object <c>error</c>) then
/// flushes a <c>tool_result</c>. If a step is only ever observed at its terminal state (no prior
/// <c>ACTIVE</c> reached this accumulator, e.g. a reconnect mid-turn), both envelopes flush together
/// on that one <see cref="Flush"/> call, call before result.
/// </summary>
internal sealed class AntigravityStepAccumulator {
    sealed class StepBuffer {
        public readonly StringBuilder Text = new();
        public string?  StepType;
        public string?  State;
        public bool     HasUsage;
        public long?    InputTokens;
        public long?    OutputTokens;
        public long?    ThinkingTokens;
        public long?    CacheReadTokens;
        public long?    TotalTokens;
        public bool     IsTool;
        public bool     ToolCallEmitted;
        public string?  ToolName;
        public string?  ToolInputJson;
        public string?  ToolOutput;
        public string?  ToolErrorType;
        public string?  ToolErrorMessage;
    }

    readonly Dictionary<long, StepBuffer> _steps = new();

    /// <summary>
    /// Folds one <see cref="AntigravityEventKind.StepUpdate"/> event into its step's buffer.
    /// Anything else — including a null <see cref="AntigravityEvent.StepIndex"/>, which a
    /// malformed/partial line can produce — is silently ignored: this accumulator only ever holds
    /// step_update state.
    /// </summary>
    public void Add(AntigravityEvent evt) {
        if (evt.Kind != AntigravityEventKind.StepUpdate || evt.StepIndex is not { } index) return;

        if (!_steps.TryGetValue(index, out var buf)) _steps[index] = buf = new StepBuffer();

        if (evt.StepType is { Length: > 0 }) buf.StepType = evt.StepType;
        buf.State = evt.State;
        if (evt.TextDelta is { Length: > 0 } delta) buf.Text.Append(delta);

        if (evt.InputTokens is not null || evt.OutputTokens is not null || evt.ThinkingTokens is not null
                || evt.CacheReadTokens is not null || evt.TotalTokens is not null) {
            buf.HasUsage        = true;
            buf.InputTokens     = evt.InputTokens;
            buf.OutputTokens    = evt.OutputTokens;
            buf.ThinkingTokens  = evt.ThinkingTokens;
            buf.CacheReadTokens = evt.CacheReadTokens;
            buf.TotalTokens     = evt.TotalTokens;
        }

        if (buf.StepType == "tool") {
            buf.IsTool = true;
            if (evt.ToolName is not null) buf.ToolName = evt.ToolName;
            if (evt.ToolInputJson is not null) buf.ToolInputJson = evt.ToolInputJson;
            if (evt.ToolOutput is not null) buf.ToolOutput = evt.ToolOutput;
            if (evt.ToolErrorType is not null) buf.ToolErrorType = evt.ToolErrorType;
            if (evt.ToolErrorMessage is not null) buf.ToolErrorMessage = evt.ToolErrorMessage;
        }
    }

    /// <summary>
    /// Emits whatever is ready across every tracked step, in ascending step-index order, dropping a
    /// step's buffer once it reaches terminal (<c>DONE</c> or <c>ERROR</c>).
    ///
    /// A <c>tool</c> step contributes a <c>tool_call</c> the first time it has a name (regardless of
    /// state — this is the one non-terminal emission) and, once terminal, a <c>tool_result</c>
    /// carrying <c>output</c> (<c>ToolIsError: false</c>) or the error's <c>message</c> (prefixed
    /// with its <c>type</c> when present, <c>ToolIsError: true</c>). Any other step contributes, only
    /// once terminal, an <c>assistant_text</c> when it accumulated any text and a <c>usage</c> when
    /// its terminal event carried a usage block — a content-free terminal step (e.g. a bare
    /// <c>user_input</c> marker) yields neither. A step still <c>ACTIVE</c> (and not a tool call) is
    /// left untouched for a later <see cref="Flush"/> call.
    ///
    /// <see cref="AcpEventEnvelope.ContextUsedTokens"/> is stamped from <c>input_tokens</c> — of
    /// agy's counters it is the one that matches ACP's "context occupied so far" semantics (tokens
    /// fed IN as context), unlike <c>output_tokens</c>/<c>total_tokens</c>, which are cost/billing
    /// figures for the step rather than window occupancy. agy reports no window/size figure, so
    /// <see cref="AcpEventEnvelope.ContextWindowTokens"/> is always left null here.
    /// </summary>
    public IReadOnlyList<AcpEventEnvelope> Flush(string? model) {
        List<AcpEventEnvelope>? envelopes = null;
        List<long>?             done      = null;

        foreach (var index in _steps.Keys.OrderBy(k => k)) {
            var buf      = _steps[index];
            var terminal = buf.State is "DONE" or "ERROR";

            if (buf.IsTool && !buf.ToolCallEmitted && buf.ToolName is not null) {
                (envelopes ??= []).Add(new AcpEventEnvelope(
                    Kind: AcpEventKind.ToolCall,
                    ToolCallId: index.ToString(),
                    ToolName: buf.ToolName,
                    ToolInputJson: buf.ToolInputJson));
                buf.ToolCallEmitted = true;
            }

            if (!terminal) continue; // still ACTIVE — the call (if any) is out, nothing else is ready yet

            (done ??= []).Add(index);

            if (buf.IsTool) {
                if (buf.ToolOutput is not null) {
                    (envelopes ??= []).Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolResult,
                        ToolCallId: index.ToString(),
                        ToolResult: buf.ToolOutput,
                        ToolIsError: false));
                } else if (buf.ToolErrorMessage is not null) {
                    (envelopes ??= []).Add(new AcpEventEnvelope(
                        Kind: AcpEventKind.ToolResult,
                        ToolCallId: index.ToString(),
                        ToolResult: buf.ToolErrorType is { Length: > 0 } type
                            ? $"{type}: {buf.ToolErrorMessage}"
                            : buf.ToolErrorMessage,
                        ToolIsError: true));
                }
            }

            if (buf.Text.Length > 0) {
                (envelopes ??= []).Add(new AcpEventEnvelope(
                    Kind: AcpEventKind.AssistantText,
                    Text: buf.Text.ToString()));
            }

            if (buf.HasUsage) {
                (envelopes ??= []).Add(new AcpEventEnvelope(
                    Kind: AcpEventKind.Usage,
                    Model: model,
                    ContextUsedTokens: buf.InputTokens));
            }
        }

        if (done is not null) foreach (var index in done) _steps.Remove(index);

        return envelopes ?? [];
    }
}
