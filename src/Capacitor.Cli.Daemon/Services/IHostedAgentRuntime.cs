namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Vendor-agnostic hosted-agent process abstraction. <see cref="AgentOrchestrator"/> owns the
/// vendor-neutral lifecycle (status flips, terminal fan-out to local sinks, server calls, worktree
/// cleanup, heartbeats); the runtime owns the vendor-specific process and I/O.
///
/// <see cref="PtyHostedAgentRuntime"/> wraps an <see cref="Pty.IPtyProcess"/> for the interactive
/// CLIs (Claude, Codex). <c>AcpHostedAgentRuntime</c> speaks ACP JSON-RPC over stdio for
/// Cursor. The orchestrator only ever touches this interface, never a raw PTY.
/// </summary>
internal interface IHostedAgentRuntime : IAsyncDisposable {
    /// <summary>Vendor token this runtime hosts ("claude", "codex", "cursor").</summary>
    string Vendor { get; }

    /// <summary>OS process id of the hosted agent (for logging).</summary>
    int Pid { get; }

    /// <summary>True once the hosted process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Exit code once <see cref="HasExited"/>; null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>The start-identity token captured NATIVELY, inside the spawn call, immediately
    /// after the child exists (the capture-binding rule) — <c>null</c> when this runtime never
    /// captures one this way (Windows; ACP runtimes have no PTY at all), in which case the caller
    /// falls back to a legacy post-hoc <c>ProcessStartToken.ForPid</c> re-capture. On Unix this is
    /// NEVER null: it's either a real token or <c>""</c> (capture attempted and failed — an
    /// identity-unavailable record), and the caller must NOT re-capture in that case (that would
    /// defeat the whole point of capturing pre-reap).</summary>
    string? StartIdentity => null;

    /// <summary>
    /// True when <see cref="ReadOutputAsync"/> yields real terminal bytes that the orchestrator's
    /// read loop can use as a liveness/lifecycle signal — <c>true</c> for <see cref="PtyHostedAgentRuntime"/>
    /// (Claude/Codex), <c>false</c> for the ACP runtime (its stdout is protocol traffic, never
    /// terminal output, so <see cref="ReadOutputAsync"/> never yields any bytes at all).
    /// <see cref="AgentOrchestrator"/> uses this to pick between two lifecycle strategies (PR #244
    /// review, Fix B/E): a PTY runtime's Starting→Running flip and startup-failure heuristic both
    /// key off the FIRST output chunk / the output stream ending, which never happens for a
    /// no-terminal ACP runtime — without this flag an ACP agent would flip Running only once it
    /// exits (i.e. never while live) and get misclassified as a startup failure on every normal
    /// exit.
    /// </summary>
    bool EmitsTerminalOutput { get; }

    /// <summary>
    /// Terminal byte stream consumed by the orchestrator read loop. PTY runtimes yield real
    /// terminal output; the ACP runtime yields an empty stream until a follow-up adds a terminal
    /// capability (ACP stdout is protocol traffic, never terminal output).
    /// </summary>
    IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default);

    /// <summary>
    /// Hosted-UI text input (server <c>SendInput</c>). PTY runtimes perform the CLI-specific
    /// text-then-Enter split write; the ACP runtime sends a <c>session/prompt</c>.
    /// </summary>
    Task SendUserInputAsync(string text);

    /// <summary>Queue input and complete only after the runtime has written the prompt to its
    /// protocol transport. Borrowed snapshot rounds use this acknowledgement to close the race
    /// between prompt delivery and a subsequent refresh. PTY runtimes use the normal send.</summary>
    Task SendUserInputAndWaitForWriteAsync(string text) => SendUserInputAsync(text);

    /// <summary>Wait until any prior structured prompt turn has received its terminal response.
    /// PTY runtimes have no structured turn boundary and use the completed default.</summary>
    Task WaitForTurnIdleAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Hosted-UI special key (server <c>SendSpecialKey</c>). PTY runtimes translate via
    /// <see cref="SpecialKeyMap"/> and write the bytes; the ACP runtime maps or ignores.
    /// </summary>
    Task SendSpecialKeyAsync(string key);

    /// <summary>
    /// Raw byte input from a local-attached terminal (`kcap agent attach` Stdin frames). PTY runtimes
    /// write the bytes verbatim. Local attach is a PTY-only surface, so the ACP runtime throws
    /// <see cref="NotSupportedException"/>; the caller guards on <see cref="Vendor"/>.
    /// </summary>
    Task SendRawInputAsync(byte[] data);

    /// <summary>Viewer resize. PTY runtimes resize the winsize; the ACP runtime no-ops until
    /// a follow-up adds a terminal capability.</summary>
    void Resize(ushort cols, ushort rows);

    /// <summary>
    /// Request a graceful stop <b>before</b> <see cref="TerminateAsync"/>. PTY runtimes send the
    /// CLI's "/exit" so it can fire its own session-end hook; the ACP runtime sends
    /// <c>session/cancel</c>. Best-effort — the orchestrator falls through to terminate.
    /// </summary>
    Task RequestGracefulStopAsync();

    /// <summary>Wait up to <paramref name="timeout"/> for the process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Terminate the process (SIGTERM then SIGKILL) within <paramref name="timeout"/>.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
