// src/Capacitor.Cli.Daemon/Acp/IPiRpcProcess.cs
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Minimal process-lifecycle abstraction for a hosted Pi <c>pi</c> child speaking the stdio JSONL
/// RPC protocol (see <c>PiRpc</c>). Unlike <see cref="IAgyTurnProcess"/> — an exec-per-turn child
/// whose stdin is closed the instant it is spawned — a Pi RPC child is LONG-LIVED: one process backs
/// the whole hosted session, and its stdin stays open for that entire lifetime so the runtime can
/// keep sending <c>prompt</c>/<c>abort</c>/<c>get_state</c>/<c>set_model</c> commands to it turn
/// after turn. That is exactly the capability this interface adds over <see cref="IAgyTurnProcess"/>:
/// <see cref="WriteLineAsync"/>.
///
/// <para>Exists so a Pi hosted-agent runtime is testable without spawning a real process; a later
/// task's factory implements this over <see cref="System.Diagnostics.Process"/> (<see cref="PiRpcProcess"/>,
/// this file).</para>
///
/// <para><b>Two contracts a real implementation must honor — copied verbatim from
/// <see cref="IAgyTurnProcess"/>, because the same runtime shapes (a turn worker's own
/// <c>finally</c> racing the runtime's own teardown) apply here too:</b></para>
/// <para>0. <see cref="IAsyncDisposable.DisposeAsync"/> MAY terminate a still-running child, and an
/// implementation over a real process does. It is therefore NOT a source of exit evidence: a caller
/// that needs to know whether a child exited must terminate it explicitly and read
/// <see cref="HasExited"/> while the handle is still valid, BEFORE disposing — a disposed process
/// object reports exited whether or not it is.</para>
/// <para>1. <see cref="IAsyncDisposable.DisposeAsync"/> MUST be idempotent. A second call on the same
/// instance must be a safe no-op, never throw.</para>
/// <para>2. <see cref="TerminateAsync"/> MUST be safe to call AFTER
/// <see cref="IAsyncDisposable.DisposeAsync"/> has already run — this must degrade gracefully (a
/// no-op, or a caught/logged failure internally), never throw an exception a caller doesn't already
/// catch.</para>
/// <para>3. <see cref="WriteLineAsync"/> calls MUST be serialized against each other so that two
/// concurrent writers can never interleave partial lines on the child's stdin — a torn line is
/// unparseable JSON on the other end, and unlike a read race (which just ends the enumerable early),
/// a write race corrupts the wire for every command after it.</para>
/// </summary>
internal interface IPiRpcProcess : IAsyncDisposable {
    /// <summary>OS process id of the hosted <c>pi</c> child.</summary>
    int Pid { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>OS exit code once <see cref="HasExited"/>; null while running or if unknown.</summary>
    int? ExitCode { get; }

    /// <summary>A bounded capture of whatever the child wrote to stderr, or null if it wrote
    /// nothing. Read on a failed launch (see <c>PiRpcHostedAgentRuntimeFactory</c>'s post-spawn
    /// catch) or an unexpected exit, to turn silence into a reason an operator can act on.</summary>
    string? Diagnostics { get; }

    /// <summary>
    /// Reads this child's stdout, LF-framed, one JSONL line at a time, ending when stdout hits EOF
    /// (the process exited, or is about to). Must not throw for a normal EOF — the sequence simply
    /// ends; it throws <see cref="OperationCanceledException"/> if <paramref name="ct"/> is
    /// cancelled first.
    /// </summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="json"/> followed by a single <c>\n</c> to the child's stdin and
    /// flushes, so the line is on the wire before this returns. Concurrent callers are serialized
    /// against each other (contract 3 on this interface's class doc) — never call this expecting an
    /// implementation to accept interleaved writes.
    /// </summary>
    Task WriteLineAsync(string json, CancellationToken ct);

    /// <summary>Wait up to <paramref name="timeout"/> for the process to exit (returns silently on timeout).</summary>
    Task WaitForExitAsync(TimeSpan? timeout = null);

    /// <summary>Terminate the process — an immediate kill of the whole process tree, matching
    /// <c>AgyTurnProcess</c> (not a graceful signal first) — within <paramref name="timeout"/>. Must
    /// be safe to call even after <see cref="IAsyncDisposable.DisposeAsync"/> has already run — see
    /// this interface's class doc.</summary>
    Task TerminateAsync(TimeSpan? timeout = null);
}

/// <summary>
/// <see cref="IPiRpcProcess"/> over a real <see cref="Process"/> — the long-lived <c>pi</c> child
/// backing one hosted Pi session for its whole lifetime. Mirrors <c>AgyTurnProcess</c>'s
/// terminate/wait/dispose semantics (an immediate kill of the whole process tree via
/// <see cref="Process.Kill(bool)"/> — not a graceful signal first — bounded waits that return
/// silently on timeout, idempotent dispose, terminate-safe-after-dispose) and its bounded stderr
/// diagnostics capture, but differs in the one place the two runtimes differ: stdin.
/// <c>AgyTurnProcess</c> closes it the instant the child spawns (a fresh exec-per-turn process that
/// never needs a second write); this type deliberately leaves it open and adds
/// <see cref="WriteLineAsync"/>, serialized under a <see cref="SemaphoreSlim"/> so two commands fired
/// close together (e.g. a user prompt racing an operator-initiated abort) can never tear each other's
/// line in half on the wire.
///
/// <para><b>Identity is captured at construction</b>, not read on demand: <see cref="Process.Id"/>
/// throws once the process object is disposed, and the PID is exactly what an orphan reaper needs
/// most after the fact.</para>
/// </summary>
internal sealed partial class PiRpcProcess : IPiRpcProcess {
    /// <summary>Enough to carry a startup/auth error and the context around it, and small enough
    /// that a chatty long-lived child cannot grow the daemon's heap over a session's whole
    /// lifetime. Over-cap output is dropped, never rotated — this exists to explain a FAILED launch
    /// or an unexpected exit, and that explanation is at the start.</summary>
    const int DiagnosticsCap = 4096;

    readonly Process                 _process;
    readonly ILogger                 _logger;
    readonly CancellationTokenSource _stderrDrainCts = new();
    readonly Task                    _stderrDrainTask;
    readonly Lock                    _diagnosticsGate = new();
    readonly StringBuilder           _diagnostics     = new();
    readonly SemaphoreSlim           _stdinGate       = new(1, 1);

    int _disposed;

    /// <summary>Read wherever a caller needs to know disposal has started WITHOUT racing
    /// <see cref="DisposeAsync"/>'s own idempotency check — <see cref="_disposed"/> is set via
    /// <see cref="Interlocked.Exchange(ref int, int)"/> the instant <see cref="DisposeAsync"/>
    /// begins, so a plain volatile read here is already coherent with that write.</summary>
    bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal PiRpcProcess(ProcessStartInfo psi, ILogger logger)
        : this(Process.Start(psi) ?? throw new InvalidOperationException(
                   $"pi_rpc_spawn_failed: '{psi.FileName}' did not start (Process.Start returned null)."),
               logger) { }

    internal PiRpcProcess(Process process, ILogger logger) {
        _process = process;
        _logger  = logger;
        Pid      = SafePid(process);

        // Deliberately NOT closed — unlike AgyTurnProcess's exec-per-turn child, this process backs
        // the whole hosted session and must keep receiving commands (prompt/abort/get_state/
        // set_model) on stdin for its entire lifetime.
        _stderrDrainTask = DrainStderrAsync(_stderrDrainCts.Token);
    }

    public int  Pid       { get; }
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

    public int? ExitCode {
        get {
            try {
                return _process.HasExited ? _process.ExitCode : null;
            } catch {
                return null;
            }
        }
    }

    /// <summary>A bounded capture of whatever the child wrote to stderr, or null if it wrote
    /// nothing. Read on a failed launch or an unexpected exit, to turn silence into a reason an
    /// operator can act on. Mirrors <c>AgyTurnProcess.Diagnostics</c>'s shape and naming.</summary>
    public string? Diagnostics {
        get { lock (_diagnosticsGate) return _diagnostics.Length == 0 ? null : _diagnostics.ToString(); }
    }

    /// <summary>This child's stdout, LF-framed, one JSONL line at a time, ending at EOF.</summary>
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct) {
        while (true) {
            string? line;

            try {
                line = await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            } catch (IOException) {
                break;   // pipe torn down (the child was killed mid-read) — EOF, per this method's contract
            } catch (ObjectDisposedException) {
                break;   // stream disposed under us by a racing DisposeAsync — likewise EOF
            }

            if (line is null) break;

            yield return line;
        }
    }

    /// <summary>
    /// Writes <paramref name="json"/> + <c>\n</c> to stdin and flushes, serialized under
    /// <see cref="_stdinGate"/> so concurrent callers never interleave partial lines on the wire.
    ///
    /// <para>Checked for disposal BOTH before and after acquiring the gate. The pre-check is the
    /// fast path once disposal has already finished (no need to queue behind a gate nobody will
    /// ever signal again); the post-check catches a writer that was already queued in
    /// <c>WaitAsync</c> when <see cref="DisposeAsync"/> ran and only got the gate because the
    /// PREVIOUS holder's own <c>finally</c> released it — without this second check that writer
    /// would go on to write into a process <see cref="DisposeAsync"/> just killed. Either check
    /// throws <see cref="ObjectDisposedException"/>, matching the type this method's contract
    /// promises for a call after disposal.</para>
    /// </summary>
    public async Task WriteLineAsync(string json, CancellationToken ct) {
        if (IsDisposed) throw new ObjectDisposedException(nameof(PiRpcProcess));

        await _stdinGate.WaitAsync(ct).ConfigureAwait(false);

        try {
            if (IsDisposed) throw new ObjectDisposedException(nameof(PiRpcProcess));

            await _process.StandardInput.WriteAsync((json + "\n").AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        } finally {
            _stdinGate.Release();
        }
    }

    /// <summary>Keeps the stderr pipe drained — an undrained one fills at ~64KB and blocks the child
    /// on its next write, which for a long-lived RPC process would wedge every future turn, not just
    /// one. Never logs the text itself: a hosted agent's stderr can carry prompt fragments, paths,
    /// or auth detail.</summary>
    async Task DrainStderrAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                var line = await _process.StandardError.ReadLineAsync(ct).ConfigureAwait(false);

                if (line is null) break;      // EOF — the child exited and closed the stream
                if (line.Length == 0) continue;

                Capture(line);
                LogStderrShape(line.Length);
            }
        } catch (OperationCanceledException) {
            // Disposal asked the drain to stop — expected.
        } catch (IOException) {
            // Pipe torn down on teardown — expected.
        } catch (ObjectDisposedException) {
            // Stream disposed out from under the read — expected.
        }
    }

    void Capture(string line) {
        lock (_diagnosticsGate) {
            if (_diagnostics.Length >= DiagnosticsCap) return;

            _diagnostics.Append(line).Append('\n');
        }
    }

    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        try {
            if (timeout is { } t) {
                using var cts = new CancellationTokenSource(t);

                try {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // Timed out — return silently, per this method's contract.
                }
            } else {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        } catch {
            // Already exited or disposed — nothing left to wait for.
        }
    }

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        try {
            if (_process.HasExited) return;

            _process.Kill(entireProcessTree: true);
        } catch {
            // Already exited, already disposed (the contract explicitly permits this call after
            // DisposeAsync), or the kill raced the exit — nothing left to terminate either way.
            return;
        }

        await WaitForExitAsync(timeout ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // idempotent, per the interface contract

        // The kill happens FIRST, before anything else in this method, and that ordering is load
        // bearing for WriteLineAsync's post-dispose behaviour: a writer already inside the
        // `_stdinGate` critical section (mid WriteAsync/FlushAsync on stdin) is not cancelled by
        // disposal — killing the child here breaks its pipe out from under it instead, so that
        // in-flight write fails fast (IOException/ObjectDisposedException) rather than hanging on a
        // dead pipe that will never accept more bytes.
        //
        // No bounded wait after the kill, deliberately — see AgyTurnProcess's identical comment.
        // Kill(entireProcessTree: true) is an immediate kill (SIGKILL on POSIX), which no child can
        // catch or defer, so the death is already effectively synchronous with the call. Callers
        // that need a CONFIRMED exit terminate first and read HasExited while the handle is still
        // valid.
        try {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        } catch {
            // Best-effort — already exited or inaccessible.
        }

        try {
            await _stderrDrainCts.CancelAsync().ConfigureAwait(false);
        } catch {
            // Best-effort.
        }

        try {
            await _stderrDrainTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } catch {
            // DrainStderrAsync already swallows its expected exceptions; never let a stuck drain hang
            // or fault a dispose.
        }

        _stderrDrainCts.Dispose();

        // _stdinGate is deliberately left undisposed. Disposing a SemaphoreSlim does NOT release
        // outstanding async waiters — a WriteLineAsync already blocked in WaitAsync when this method
        // ran would then hang forever, and the holder that eventually releases it would have its
        // `finally { Release() }` throw ObjectDisposedException on top of whatever it was already
        // unwinding, potentially masking the real failure. A SemaphoreSlim holds no unmanaged state
        // unless its AvailableWaitHandle is touched (never is, here), so leaving it undisposed is
        // accepted .NET practice. WriteLineAsync's own IsDisposed checks (before AND after acquiring
        // the gate) are what actually turn a post-dispose writer away — cleanly, and without ever
        // waiting on a signal that would otherwise never come.
        try {
            _process.Dispose();
        } catch {
            // Best-effort.
        }
    }

    static int SafePid(Process process) {
        try {
            return process.Id;
        } catch {
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pi RPC stderr: {Length} chars")]
    partial void LogStderrShape(int length);
}
