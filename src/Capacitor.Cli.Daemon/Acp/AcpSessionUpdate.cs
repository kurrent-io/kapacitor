// src/Capacitor.Cli.Daemon/Acp/AcpSessionUpdate.cs
using System.Text.Json;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Discriminator for <see cref="AcpSessionUpdate.Kind"/>, mirroring the <c>sessionUpdate</c> string
/// values documented in <c>docs/acp-probe-findings.md</c>. <see cref="AgentMessageChunk"/>,
/// <see cref="AvailableCommands"/>, and <see cref="SessionInfo"/> are probe-confirmed;
/// <see cref="AgentThoughtChunk"/>, <see cref="ToolCall"/>, <see cref="ToolCallUpdate"/>, and
/// <see cref="Plan"/> are spec-derived but not yet observed on the wire (see the probe doc's
/// "Recommended follow-up"). <see cref="Unknown"/> covers any future/unrecognized discriminator so
/// the reducer never throws on an unfamiliar variant.
/// </summary>
internal enum AcpUpdateKind {
    AgentMessageChunk,
    AgentThoughtChunk,
    ToolCall,
    ToolCallUpdate,
    Plan,
    AvailableCommands,
    SessionInfo,
    UsageUpdate,
    Unknown,
}

/// <summary>
/// Reduced, AOT-friendly internal DTO for one <c>session/update</c> notification's inner
/// <c>update</c> object. Flat and non-polymorphic by design (scope: reduce the wire shape to
/// something the mapper can turn into canonical events, without modeling every field of every
/// variant). <see cref="Raw"/> always carries the full <c>update</c> object (not the outer
/// notification envelope) so the mapper can reach into fields this DTO doesn't surface yet —
/// notably the untyped <c>plan</c> entries and any fields on spec-derived variants that drift once
/// a follow-up probe re-verifies them against a non-plan-gated account.
///
/// <see cref="ToolInputJson"/> carries the tool_call's
/// <c>rawInput</c>, feeding <c>AcpEventEnvelope.ToolInputJson</c>, and
/// <see cref="ToolResultText"/>/<see cref="ToolIsError"/> carry a tool_call_update's extracted result
/// content + failed-status flag, feeding <c>AcpEventEnvelope.ToolResult</c>/<c>ToolIsError</c> —
/// both extracted mechanically by <c>AcpHostedAgentRuntime.Reduce()</c> from <see cref="Raw"/>,
/// regardless of terminal status; deciding WHETHER a given update should emit a <c>ToolResult</c>
/// envelope (terminal + extractable content) is <c>AcpEventTranslator</c>'s job, not Reduce()'s.
/// </summary>
internal sealed record AcpSessionUpdate(
    AcpUpdateKind Kind,
    string?       Text           = null,
    string?       Title          = null, // session_info_update's agent-authored session title
    string?       ToolCallId     = null,
    string?       ToolTitle      = null,
    string?       ToolKind       = null,
    string?       ToolStatus     = null,
    string?       ToolInputJson  = null,
    string?       ToolResultText = null,
    bool          ToolIsError    = false,
    long?         ContextUsedTokens   = null, // usage_update's `used` (context occupancy)
    long?         ContextWindowTokens = null, // usage_update's `size` (window capacity)
    JsonElement?  Raw            = null
);
