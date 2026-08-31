using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// <see cref="IAcpProcess"/> over a real <see cref="Process"/> — the <c>cursor-agent acp</c> child
/// spawned by <see cref="Services.AcpHostedAgentRuntimeFactory"/>. Mirrors the
/// terminate/wait semantics of <c>Pty.Unix.UnixPtyProcess</c>/<c>WinPtyProcess</c> (SIGTERM-then-kill
/// via <see cref="Process.Kill(bool)"/>, bounded waits that return silently on timeout) but owns no
/// terminal I/O — stdin/stdout carry ACP JSON-RPC frames, consumed by <see cref="AcpConnection"/>.
///
/// Also owns a background drain of the child's redirected stderr. <c>cursor-agent</c> is spawned
/// with <c>RedirectStandardError = true</c> (see <see cref="Services.AcpHostedAgentRuntimeFactory"/>),
/// and nothing else ever reads that stream — if we didn't drain it here, the OS pipe buffer (~64KB)
/// would eventually fill and cursor-agent would block on its next stderr write, deadlocking a long
/// session. The drain loop reads lines until EOF (process exit) or cancellation and logs each line's
/// LENGTH at Debug by default (stderr can carry paths/prompt fragments/error detail); the full line
/// text is only logged when the operator opts in via <c>KCAP_ACP_DEBUG_FRAMES</c> (see
/// <see cref="DaemonConfig.DebugFrames"/>).
/// </summary>
internal sealed partial class AcpChildProcess : IAcpProcess {
    readonly Process                 _process;
    readonly ILogger                 _logger;
    readonly bool                    _debugFrames;
    readonly string                  _vendor;
    readonly CancellationTokenSource _stderrDrainCts = new();
    readonly Task                    _stderrDrainTask;
    readonly Lock                    _diagnosticsGate = new();
    readonly StringBuilder           _diagnostics     = new();

    int _disposed;

    /// <summary>Enough to carry a startup/auth error or a retry banner and the context around it, and
    /// small enough that a chatty long-lived child cannot grow the daemon's heap over a whole
    /// session. Over-cap output is dropped, never rotated — this exists to explain a failed launch or
    /// a stalled turn, and that explanation is at the start.</summary>
    const int DiagnosticsCap = 4096;

    public string? Diagnostics {
        get { lock (_diagnosticsGate) return _diagnostics.Length == 0 ? null : _diagnostics.ToString(); }
    }

    /// <param name="debugFrames">
    /// <c>KCAP_ACP_DEBUG_FRAMES</c> (<see cref="DaemonConfig.DebugFrames"/>) — off by default. When
    /// on, <see cref="DrainStderrAsync"/> logs full (length-capped) stderr line text at Debug instead
    /// of just its length; cursor-agent stderr can carry paths/prompt fragments/error detail.
    /// </param>
    public AcpChildProcess(Process process, ILogger logger, bool debugFrames = false, string vendor = "cursor") {
        _process     = process;
        _logger      = logger;
        _debugFrames = debugFrames;
        _vendor      = vendor;

        // Fire-and-forget from the ctor's perspective — DisposeAsync cancels and (best-effort)
        // awaits it. Never let this task fault unobserved; DrainStderrAsync swallows everything
        // it can anticipate on shutdown.
        _stderrDrainTask = DrainStderrAsync(_stderrDrainCts.Token);
    }

    /// <summary>
    /// Continuously reads <see cref="Process.StandardError"/> until EOF (the process exited and
    /// closed the pipe) or <paramref name="ct"/> fires, logging each non-empty line at Debug. This
    /// exists purely to keep the stderr pipe drained — see the type-level doc for why an undrained
    /// stderr pipe can deadlock <c>cursor-agent</c>. Never throws: any exception here would become
    /// an unobserved-task-exception on process teardown, and none of the failure modes below need
    /// anything more than "stop draining".
    /// </summary>
    async Task DrainStderrAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null) break; // EOF — process exited and closed the stream.

                if (line.Length == 0) continue;

                // Retained whatever the log level does: the operator opts into per-line logging, not
                // into being able to explain a stall after the fact, and the vendor writes its whole
                // diagnosis here (gemini's "Attempt N failed: RATE_LIMIT_EXCEEDED … Retrying after
                // 70650ms" reached a live daemon and was thrown away). Daemon-local only.
                Capture(line);

                // KCAP_ACP_DEBUG_FRAMES gate (Off by default) — stderr can carry paths, prompt
                // fragments, or error detail, so it is only logged verbatim when explicitly opted in;
                // otherwise only its length is logged.
                if (_debugFrames)
                    LogStderrLineFull(_vendor, AcpDebugFrameLog.Cap(line));
                else
                    LogStderrLineShape(_vendor, line.Length);
            }
        } catch (OperationCanceledException) {
            // Disposal requested the drain stop — expected, not an error.
        } catch (IOException) {
            // Pipe torn down (process killed mid-read) — expected on teardown.
        } catch (ObjectDisposedException) {
            // Stream disposed out from under us (process.Dispose() raced the read) — expected.
        }
    }

    void Capture(string line) {
        lock (_diagnosticsGate) {
            var room = DiagnosticsCap - _diagnostics.Length;
            if (room <= 0) return;

            // Truncated per append, not merely gated on it: a vendor that writes one enormous line —
            // a stack trace, a base64 payload — would otherwise store all of it and carry it into a
            // Warning, which is the bound this cap exists to hold.
            _diagnostics.Append(line.AsSpan(0, Math.Min(line.Length, room - 1))).Append('\n');
        }
    }

    public int Pid {
        get {
            try {
                return _process.HasExited ? 0 : _process.Id;
            } catch {
                // Process already disposed/exited under us — never let a PID read throw.
                return 0;
            }
        }
    }

    public bool HasExited {
        get {
            try {
                return _process.HasExited;
            } catch {
                return true;
            }
        }
    }

    public int? ExitCode {
        get {
            try {
                return _process.HasExited ? _process.ExitCode : null;
            } catch {
                return null;
            }
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        try {
            if (timeout is { } t) {
                using var cts = new CancellationTokenSource(t);

                try {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // Timed out — return silently, matching IPtyProcess's contract.
                }
            } else {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        } catch {
            // Process already exited/disposed — nothing to wait for.
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        try {
            if (_process.HasExited) return;

            _process.Kill(entireProcessTree: true);
        } catch {
            // Already exited/disposed, or the kill raced the exit — either way there's
            // nothing left to terminate.
            return;
        }

        await WaitForExitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        } catch {
            // Best-effort — already exited or inaccessible.
        }

        // Stop the stderr drain. The process is being killed anyway, so this is a bounded,
        // best-effort wait — never let a stuck drain hang dispose.
        try {
            await _stderrDrainCts.CancelAsync().ConfigureAwait(false);
        } catch {
            // Best-effort.
        }

        try {
            await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } catch {
            // Timed out or faulted — DrainStderrAsync already swallows its expected exceptions,
            // so this is just a safety net; never let dispose hang or throw on it.
        }

        _stderrDrainCts.Dispose();

        try {
            _process.Dispose();
        } catch {
            // Best-effort.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Vendor} stderr: {Line}")]
    partial void LogStderrLineFull(string vendor, string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Vendor} stderr: {Length} chars")]
    partial void LogStderrLineShape(string vendor, int length);
}
