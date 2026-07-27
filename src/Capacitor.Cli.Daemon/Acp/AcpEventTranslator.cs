// src/Capacitor.Cli.Daemon/Acp/AcpEventTranslator.cs
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Pure, static, per-update translator from a reduced
/// <see cref="AcpSessionUpdate"/> (daemon-local) to the daemon-local wire
/// <see cref="AcpEventEnvelope"/> (Core — field-for-field mirror of the server's
/// <c>Capacitor.Server.Core.Acp.AcpEventEnvelope</c>). Maps exactly ONE update to AT MOST one
/// envelope (see <c>docs/ai688-option-b-canonical-surfacing-design.md</c>). Deliberately does
/// NOT aggregate chunk streams, correlate multi-update tool-call state, assign real sequence
/// numbers, or forward anything — those are handled elsewhere (aggregation/seq assignment is
/// runtime-owned; <paramref name="seq"/>/<paramref name="timestampIso"/> below are caller-supplied
/// inputs, not derived here). <see cref="Translate"/> never throws: an unmappable/dropped kind
/// returns <see langword="null"/> rather than fabricating an empty envelope.
/// </summary>
internal static partial class AcpEventTranslator {
    /// <summary>
    /// ACP <c>ToolCallStatus</c> values that represent a FINISHED tool call —
    /// <c>pending</c>/<c>in_progress</c>/a missing status are non-terminal, status-only updates that
    /// never emit a <c>ToolResult</c> envelope, regardless of whether <see cref="AcpSessionUpdate.ToolResultText"/>
    /// happens to be set.
    /// </summary>
    static bool IsTerminalToolStatus(string? status) => status is "completed" or "failed";

    /// <summary>
    /// Translates ONE <paramref name="update"/> into an <see cref="AcpEventEnvelope"/>, or
    /// <see langword="null"/> when the kind is dropped:
    /// <list type="bullet">
    /// <item><description><see cref="AcpUpdateKind.AgentMessageChunk"/> → <see cref="AcpEventKind.AssistantText"/>,
    /// <see cref="AcpUpdateKind.AgentThoughtChunk"/> → <see cref="AcpEventKind.AssistantThinking"/> —
    /// both use <paramref name="aggregatedText"/> when supplied (the caller's chunk-aggregation
    /// result — this translator holds no aggregation state of its own), else the update's own
    /// <see cref="AcpSessionUpdate.Text"/>.</description></item>
    /// <item><description><see cref="AcpUpdateKind.ToolCall"/> → <see cref="AcpEventKind.ToolCall"/>,
    /// carrying <see cref="AcpSessionUpdate.ToolCallId"/>/<see cref="AcpSessionUpdate.ToolTitle"/>
    /// (as <see cref="AcpEventEnvelope.ToolName"/> — ACP's <c>tool_call</c> has no separate machine
    /// "name" field, only <c>title</c>/<c>kind</c>; <c>title</c> is the closest analogue and is what
    /// the server's <c>AssistantToolCallsGenerated.ToolCallInfo.ToolName</c> expects to
    /// display)/<see cref="AcpSessionUpdate.ToolInputJson"/>.</description></item>
    /// <item><description><see cref="AcpUpdateKind.ToolCallUpdate"/> → <see cref="AcpEventKind.ToolResult"/>
    /// ONLY when the status is terminal (<see cref="IsTerminalToolStatus"/>) AND
    /// <see cref="AcpSessionUpdate.ToolResultText"/> is non-null — a status-only update (non-terminal,
    /// or terminal with no extractable content) returns <see langword="null"/> rather than an empty
    /// <c>ToolResultReceived</c>.</description></item>
    /// <item><description><see cref="AcpUpdateKind.SessionInfo"/> → <see cref="AcpEventKind.SessionTitle"/>
    /// when the update carries a non-blank <see cref="AcpSessionUpdate.Title"/> (the agent's
    /// auto-generated session title), else <see langword="null"/>.</description></item>
    /// <item><description><see cref="AcpUpdateKind.Plan"/>/<see cref="AcpUpdateKind.AvailableCommands"/>
    /// → always <see langword="null"/> (not transcript content; deferred to future work).</description></item>
    /// <item><description><see cref="AcpUpdateKind.Unknown"/> → <see langword="null"/>, but
    /// logged via <paramref name="logger"/> (when supplied) at debug level — never silently
    /// swallowed. <see cref="AcpSessionUpdate.Raw"/> itself is only logged verbatim when
    /// <paramref name="debugFrames"/> is <see langword="true"/> (the operator-opt-in
    /// <c>KCAP_ACP_DEBUG_FRAMES</c>); otherwise only its length is logged, since an unknown update's
    /// raw JSON can carry prompt/tool/file content.</description></item>
    /// </list>
    /// Every emitted envelope carries <paramref name="seq"/>/<paramref name="timestampIso"/> and the
    /// default <c>ContractVersion = 1</c> (the <see cref="AcpEventEnvelope"/> record's own default —
    /// this translator never overrides it, since v1 is the only contract version this daemon speaks).
    /// </summary>
    public static AcpEventEnvelope? Translate(
            AcpSessionUpdate update,
            long             seq,
            string           timestampIso,
            string?          aggregatedText = null,
            ILogger?         logger         = null,
            bool             debugFrames    = false,
            string?          resolvedModel  = null) {
        switch (update.Kind) {
            case AcpUpdateKind.AgentMessageChunk:
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.AssistantText,
                    Text: aggregatedText ?? update.Text,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.AgentThoughtChunk:
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.AssistantThinking,
                    Text: aggregatedText ?? update.Text,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.ToolCall:
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.ToolCall,
                    ToolCallId: update.ToolCallId,
                    ToolName: update.ToolTitle,
                    ToolInputJson: update.ToolInputJson,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.ToolCallUpdate:
                if (!IsTerminalToolStatus(update.ToolStatus) || update.ToolResultText is null)
                    return null; // status-only update — never an empty ToolResultReceived

                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.ToolResult,
                    ToolCallId: update.ToolCallId,
                    ToolResult: update.ToolResultText,
                    ToolIsError: update.ToolIsError,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.UsageUpdate:
                // Reduce() already validated both fields, so a UsageUpdate here is always emittable.
                // The resolved model is stamped on EVERY usage envelope: the server's mapper is a
                // pure per-envelope function with no session-fold access, so attribution has to
                // travel on the wire rather than be looked up server-side. A null model is
                // harmless - the chip's denominator is the reading's own window.
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.Usage,
                    Model: resolvedModel,
                    ContextUsedTokens: update.ContextUsedTokens,
                    ContextWindowTokens: update.ContextWindowTokens,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.SessionInfo:
                // session_info_update carries the agent's auto-generated session title. Emit a
                // SessionTitle envelope only when a non-blank title is present; a title-less info
                // update has nothing to surface, so it is dropped like the other content-free kinds.
                return string.IsNullOrWhiteSpace(update.Title)
                    ? null
                    : new AcpEventEnvelope(
                        Seq: seq,
                        Kind: AcpEventKind.SessionTitle,
                        Text: update.Title,
                        TimestampIso: timestampIso);

            case AcpUpdateKind.Plan:
            case AcpUpdateKind.AvailableCommands:
                return null;

            case AcpUpdateKind.Unknown:
            default:
                // Gate on IsEnabled(Debug) first so the default path never materializes the raw JSON
                // (GetRawText allocates the full payload) when Debug logging is disabled.
                if (logger is not null && logger.IsEnabled(LogLevel.Debug)) {
                    // KCAP_ACP_DEBUG_FRAMES gate (Off by default): the raw update JSON can carry
                    // prompt/tool/file content, so it is only ever logged verbatim when the operator
                    // has explicitly opted in — otherwise this logs shape (length) only.
                    if (debugFrames)
                        LogUnknownUpdateFull(logger, AcpDebugFrameLog.Cap(update.Raw?.GetRawText() ?? ""));
                    else
                        LogUnknownUpdateShape(logger, update.Raw?.GetRawText()?.Length ?? 0);
                }

                return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP: dropping unrecognized session/update kind (Unknown); Raw={Raw}")]
    static partial void LogUnknownUpdateFull(ILogger logger, string raw);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP: dropping unrecognized session/update kind (Unknown); RawLength={RawLength} chars")]
    static partial void LogUnknownUpdateShape(ILogger logger, int rawLength);

    /// <summary>
    /// Builds the daemon-synthesized <c>SessionStarted</c> envelope — NOT derived
    /// from an <see cref="AcpSessionUpdate"/> (ACP's <c>session/update</c> stream never carries a
    /// session-started variant). The orchestrator calls this exactly once per session, at
    /// <c>Seq 0</c>, AFTER agent registration and paired with the <c>AcpSessionStarted</c> hub bind —
    /// this builder itself has no ordering opinion; it is a pure envelope constructor.
    /// </summary>
    public static AcpEventEnvelope BuildSessionStarted(
            long    seq,
            string  timestampIso,
            string? cwd          = null,
            string? model        = null,
            string? rawSessionId = null,
            string? sessionMode  = null) =>
        new(
            Seq: seq,
            Kind: AcpEventKind.SessionStarted,
            Cwd: cwd,
            Model: model,
            RawSessionId: rawSessionId,
            SessionMode: sessionMode,
            TimestampIso: timestampIso);

    /// <summary>
    /// Builds the daemon-synthesized <c>UserMessage</c> envelope — one per
    /// serialized prompt turn, since ACP's <c>session/prompt</c> request itself never
    /// round-trips through <c>session/update</c> and so has no natural "update" to translate.
    /// </summary>
    public static AcpEventEnvelope BuildUserMessage(long seq, string timestampIso, string text) =>
        new(
            Seq: seq,
            Kind: AcpEventKind.UserMessage,
            Text: text,
            TimestampIso: timestampIso);
}
