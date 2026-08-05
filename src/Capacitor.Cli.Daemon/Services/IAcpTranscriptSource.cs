// src/Capacitor.Cli.Daemon/Services/IAcpTranscriptSource.cs
using System.Threading.Channels;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Exposes an ACP runtime's ordered, aggregated transcript plus the session metadata the
/// orchestrator needs to bind (<c>AcpSessionStarted</c>) and forward (<c>AcpSessionEvents</c>) —
/// without downcasting <see cref="IHostedAgentRuntime"/> or re-deriving state
/// <c>AcpHostedAgentRuntime.StartAsync</c> already resolved during the handshake.
/// <c>AcpHostedAgentRuntime</c> implements this directly; <see cref="PtyHostedAgentRuntime"/>
/// (Claude/Codex) does not — the factory wiring on <c>HostedRuntimeStart</c> is responsible for
/// exposing a null reference for non-ACP runtimes, not this interface itself.
/// </summary>
internal interface IAcpTranscriptSource {
    /// <summary>The ACP <c>sessionId</c> resolved by <c>session/new</c> during the handshake.</summary>
    string AcpSessionId { get; }

    /// <summary>The absolute working directory the ACP session was started with.</summary>
    string Cwd { get; }

    /// <summary>
    /// The model id actually resolved AND applied by model selection (i.e. the vendor's
    /// model-selection RPC — <c>session/set_config_option</c> or <c>session/set_model</c>,
    /// whichever the descriptor's selector sends — was sent and answered without error) — or
    /// <see langword="null"/> when no model was requested, resolution found no match in
    /// <c>session/new</c>'s <c>availableModels</c>, or the agent rejected the option. In every "null"
    /// case the vendor's own default model applies (Cursor's for a Cursor session, Kiro's for a
    /// Kiro session, …), which is why this is nullable rather than falling back to the
    /// requested-but-unconfirmed id.
    /// </summary>
    string? ResolvedModel { get; }

    /// <summary>
    /// The ordered, aggregated, per-serialized-turn <see cref="AcpEventEnvelope"/> stream (chunk
    /// aggregation + single-flight prompt-turn worker). Every envelope carries a placeholder
    /// <see cref="AcpEventEnvelope.Seq"/> of <c>0</c> — the forwarder assigns the real
    /// monotonic seq on dequeue. Channel FIFO order is the contract the forwarder relies on; nothing
    /// downstream of this reader may reorder it.
    /// </summary>
    ChannelReader<AcpEventEnvelope> Envelopes { get; }
}
