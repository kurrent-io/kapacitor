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
/// </summary>
internal sealed record AntigravityEvent(
    AntigravityEventKind Kind,
    string? ConversationId  = null,
    string? Cwd             = null, // init only
    long?   StepIndex       = null, // step_update only
    string? State           = null, // step_update only, e.g. "ACTIVE" / "DONE"
    string? StepType        = null, // step_update only, e.g. "agent_response" / "checkpoint" / "unknown"
    string? TextDelta       = null, // step_update only
    string? Status          = null, // result only, e.g. "SUCCESS"
    long?   InputTokens     = null,
    long?   OutputTokens    = null,
    long?   ThinkingTokens  = null,
    long?   CacheReadTokens = null,
    long?   TotalTokens     = null
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
/// <b>Deliberately out of scope here</b>: agy's <c>tool_info</c> shape (surfacing tool calls,
/// results, and soft-denials) is added by a later, separately-scoped task once its wire shape is
/// verified against real output (a sibling plan adds a "surface soft-denials as system_note" task
/// that extends this same file). A <c>step_update</c> that happens to carry <c>tool_info</c> is
/// handled today the same as any other <see cref="AntigravityEventKind.StepUpdate"/> — its
/// <c>text_delta</c>/usage still aggregate normally; it just yields no tool-specific envelope yet.
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

        var usage = su.Obj("usage");
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
            TotalTokens: usage?.Num("total_tokens"));
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
    /// always yields <c>[]</c> here — a single step_update line is never enough on its own; text
    /// aggregates per step and only becomes an envelope through
    /// <see cref="AntigravityStepAccumulator.Flush"/> once that step reaches <c>DONE</c>.
    /// <see cref="AntigravityEventKind.Unknown"/> yields <c>[]</c>.
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

    /// <summary>
    /// Heuristic step-type name match for a "thinking" step. <b>Unverified against real agy
    /// output</b> — every captured <c>step_type</c> to date is <c>agent_response</c>/
    /// <c>checkpoint</c>/<c>user_input</c>/<c>unknown</c>; none carries a thinking-shaped delta.
    /// Kept narrow and named so a wrong guess degrades to a benign misclassification
    /// (<c>assistant_text</c> instead of <c>assistant_thinking</c>) rather than a thrown exception
    /// or a silently dropped delta.
    /// </summary>
    internal static bool IsThinkingStepType(string? stepType) =>
        stepType is not null &&
        (stepType.Contains("thought", StringComparison.OrdinalIgnoreCase) ||
         stepType.Contains("thinking", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Per-step accumulator for agy's <c>text_delta</c> stream and per-step usage counters — the small
/// piece of state <see cref="AntigravityNdjson"/> itself deliberately stays free of. One instance
/// tracks however many step indices the caller feeds it concurrently; a step's buffer is dropped the
/// moment it flushes, so a caller that never revisits a step index leaks nothing.
///
/// Deltas accumulate regardless of <c>state</c> — a real fixture shows the tail of a stream arriving
/// on the SAME event that flips a step to <c>DONE</c>, so a delta must never be dropped just because
/// its event happens to be the terminal one for that step. <see cref="Flush"/> only ever emits for a
/// step whose LATEST <see cref="AntigravityEvent.State"/> is <c>DONE</c> — never a fabricated
/// envelope, never a still-<c>ACTIVE</c> step's text.
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
    }

    /// <summary>
    /// Emits and DROPS the buffer for every step currently at <c>DONE</c>, in ascending step-index
    /// order. A step contributes at most two envelopes: an <c>assistant_text</c> (or
    /// <c>assistant_thinking</c> — see <see cref="AntigravityNdjson.IsThinkingStepType"/>) when it
    /// accumulated any text, and a <c>usage</c> when its DONE event carried a usage block. A
    /// content-free DONE step (e.g. a bare <c>user_input</c> marker) yields neither. A step still
    /// <c>ACTIVE</c> is left untouched for a later <see cref="Flush"/> call.
    ///
    /// <see cref="AcpEventEnvelope.ContextUsedTokens"/> is stamped from <c>input_tokens</c> — the
    /// closest agy counter to ACP's "context occupied so far" semantics (the context fed IN, as
    /// opposed to <c>output_tokens</c>/<c>total_tokens</c>, which are cost/billing figures for the
    /// step rather than window occupancy). agy reports no window/size figure, so
    /// <see cref="AcpEventEnvelope.ContextWindowTokens"/> is always left null here.
    /// </summary>
    public IReadOnlyList<AcpEventEnvelope> Flush(string? model) {
        List<AcpEventEnvelope>? envelopes = null;
        List<long>?             done      = null;

        foreach (var index in _steps.Keys.OrderBy(k => k)) {
            var buf = _steps[index];
            if (buf.State != "DONE") continue;

            (done ??= []).Add(index);

            if (buf.Text.Length > 0) {
                (envelopes ??= []).Add(new AcpEventEnvelope(
                    Kind: AntigravityNdjson.IsThinkingStepType(buf.StepType)
                        ? AcpEventKind.AssistantThinking
                        : AcpEventKind.AssistantText,
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
