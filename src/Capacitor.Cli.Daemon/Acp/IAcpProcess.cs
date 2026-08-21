namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Minimal process-lifecycle abstraction for the <c>cursor-agent acp</c> child process, mirroring
/// the shape of <see cref="Pty.IPtyProcess"/> but without any terminal I/O (ACP stdio carries
/// JSON-RPC protocol traffic, not terminal bytes — that goes through <see cref="AcpConnection"/>
/// instead). Exists so <c>AcpHostedAgentRuntime</c> is testable without spawning a
/// real process; Task 10's factory implements this over <see cref="System.Diagnostics.Process"/>.
/// </summary>
internal interface IAcpProcess : IAsyncDisposable {
    /// <summary>OS process id of the hosted <c>cursor-agent acp</c> process.</summary>
    int Pid { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Exit code once <see cref="HasExited"/>; null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>Wait up to <paramref name="timeout"/> for the process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Terminate the process (SIGTERM then SIGKILL) within <paramref name="timeout"/>.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}
