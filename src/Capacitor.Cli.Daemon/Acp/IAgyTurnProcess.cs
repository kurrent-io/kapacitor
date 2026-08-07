// src/Capacitor.Cli.Daemon/Acp/IAgyTurnProcess.cs
namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Minimal process-lifecycle abstraction for ONE <c>agy -p</c> turn — the exec-per-turn analogue of
/// <see cref="IAcpProcess"/>. Every other hosted runtime in this daemon (PTY or ACP) wraps a single
/// long-lived child that outlives the whole session; <c>agy</c> has no such thing — each prompt turn
/// spawns its own process that exits when the turn ends, and there is no process at all in between.
/// So this seam is scoped to exactly one turn's lifetime: <c>AntigravityHostedAgentRuntime</c>'s
/// injected spawner (<c>Func&lt;string prompt, string? conversationId, CancellationToken,
/// Task&lt;IAgyTurnProcess&gt;&gt;</c>) returns a fresh instance per turn, never a reused one.
///
/// <para>Unlike <see cref="IAcpProcess"/>, this also carries the NDJSON line stream — agy has no
/// persistent connection object (no <c>AcpConnection</c> equivalent) for the runtime to read from
/// instead, so the process abstraction itself is the only place that stream can live.</para>
///
/// Exists so <c>AntigravityHostedAgentRuntime</c> is testable without spawning a real process; a
/// later task's factory implements this over <see cref="System.Diagnostics.Process"/>.
/// </summary>
internal interface IAgyTurnProcess : IAsyncDisposable {
    /// <summary>OS process id of this turn's <c>agy -p</c> child.</summary>
    int Pid { get; }

    /// <summary>True once this turn's process has exited.</summary>
    bool HasExited { get; }

    /// <summary>OS exit code once <see cref="HasExited"/>; null while running or if unknown. This is
    /// the raw OS code for THIS ONE TURN — never confused with
    /// <c>AntigravityHostedAgentRuntime.ExitCode</c>, which is derived from the agy <c>result</c>
    /// event's <c>status</c> field across the runtime's whole logical lifetime, not any single
    /// turn's OS exit code (upstream bug: a clean run can carry an empty <c>response</c>, so
    /// <c>status</c> is the trustworthy signal, and a per-turn OS code describes one turn, not the
    /// agent).</summary>
    int? ExitCode { get; }

    /// <summary>
    /// Reads this turn's stdout NDJSON lines as they arrive, ending when stdout hits EOF (the
    /// process exited, or is about to). Must not throw for a normal EOF — the sequence simply ends;
    /// it throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is cancelled
    /// first (the runtime's per-turn deadline or owner-cancel path).
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    /// <summary>Wait up to <paramref name="timeout"/> for this turn's process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Terminate this turn's process (SIGTERM then SIGKILL) within <paramref name="timeout"/>.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
