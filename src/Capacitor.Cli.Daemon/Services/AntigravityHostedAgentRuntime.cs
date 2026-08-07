// src/Capacitor.Cli.Daemon/Services/AntigravityHostedAgentRuntime.cs
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> for Antigravity's CLI (<c>agy</c>) as an unattended review-flow
/// reviewer.
///
/// <para><b>Why this runtime looks nothing like <see cref="PtyHostedAgentRuntime"/> or
/// <c>AcpHostedAgentRuntime</c>.</b> Every other runtime in this daemon wraps ONE long-lived child
/// process (a PTY for Claude/Codex, or an ACP JSON-RPC connection for Cursor/Copilot/Kiro/Gemini)
/// that lives for the whole hosted-agent session. <c>agy</c> has no such thing: every prompt turn is
/// its own <c>agy -p …</c> invocation that exits when the turn ends, and there is NO PROCESS AT ALL
/// between turns. That single fact is why this class needs an explicit phase machine where every
/// other runtime gets logical liveness for free from "is the child still running".</para>
///
/// <para><b>Four rules, each a real defect if broken</b> (see the design spec's rationale for the
/// full incident shapes these prevent):</para>
///
/// <para><b>(a) The phase machine.</b> <see cref="RuntimePhase.Starting"/> → <see cref="RuntimePhase.Executing"/>
/// when turn 1 spawns; <see cref="RuntimePhase.Executing"/> → <see cref="RuntimePhase.Idle"/> ONLY when the
/// turn's child exits AFTER emitting a terminal <c>result</c> event; <see cref="RuntimePhase.Idle"/> →
/// <see cref="RuntimePhase.Executing"/> when the next turn is dequeued; any phase → <see cref="RuntimePhase.Terminal"/>
/// on a launch/turn deadline, a spawn/auth failure, an explicit stop, or — critically — EOF WITHOUT a
/// terminal <c>result</c>. That last transition is the one easiest to get backwards: reading "the
/// child's stdout closed" as "the turn finished" and going to <see cref="RuntimePhase.Idle"/> would park
/// <see cref="ReadOutputAsync"/> forever, because nothing would ever again drive it toward terminal — the
/// orchestrator's <c>FinalizeAgentRunAsync</c> fires from that stream's <c>await foreach</c>'s <c>finally</c>,
/// so the session would simply hang. A child that died without a terminal <c>result</c> has lost the
/// reviewer outright, and flow semantics already fast-fail a round on participant death — going
/// <see cref="RuntimePhase.Terminal"/> here is what lets that existing machinery see the death at all.</para>
///
/// <para><b>(b) <see cref="HasExited"/> reports LOGICAL liveness, not process liveness.</b> Between turns
/// there is genuinely no child process — a naive "no live child ⇒ exited" would make every
/// <c>StopAgentCoreAsync</c> (which uses <see cref="HasExited"/> as its success criterion) trivially
/// succeed while the reviewer is merely idle between turns, and would mis-report the final status once
/// the runtime later does something with the (already falsely "exited") state. <see cref="RuntimePhase.Idle"/>
/// reports <see langword="false"/> for exactly this reason — logically alive, no process needed to prove it.</para>
///
/// <para><b>(c) <see cref="ReadOutputAsync"/> parks on ONE signal, created once.</b> It waits on
/// <see cref="_terminalTcs"/> — a single <see cref="TaskCompletionSource"/> created in the constructor and
/// completed EXACTLY ONCE, on entry to <see cref="RuntimePhase.Terminal"/> (see <see cref="EnterTerminal"/>).
/// It must never be a per-turn signal: the orchestrator drives <c>FinalizeAgentRunAsync</c> from this
/// stream's <c>await foreach</c> ending, so a signal that completed at the end of turn 1 would close the
/// whole hosted-agent session after a single turn — exactly the bug this single, constructor-owned TCS
/// exists to make structurally impossible.</para>
///
/// <para><b>(d) Lock order — <see cref="TerminateAsync"/> must NEVER take <see cref="_turnGate"/>.</b> The
/// turn gate is held for a turn's WHOLE span — spawn, parse-drain, envelope-flush — by
/// <see cref="RunTurnWorkerAsync"/>. <see cref="WaitForTurnIdleAsync"/> acquires it and releases
/// IMMEDIATELY, so it only ever queues behind an in-flight turn; it never holds the gate itself.
/// <see cref="TerminateAsync"/> instead takes the separate <see cref="_stateLock"/>, flips the phase to
/// <see cref="RuntimePhase.Terminal"/>, and — OUTSIDE that lock — cancels <see cref="_ownerCts"/> and signals
/// the current turn's child. Cancelling the owner token is what actually aborts the in-flight turn
/// (<see cref="ProcessTurnAsync"/>'s line-read loop observes it) and so releases the gate. If
/// <see cref="TerminateAsync"/> instead tried to acquire <see cref="_turnGate"/> before doing that, a
/// long-running turn would already be holding it, <see cref="RuntimePhase.Terminal"/> could never be
/// entered, <see cref="_terminalTcs"/> would never complete, and the runtime would be permanently
/// unstoppable — a genuine deadlock, not a style preference. See the mutation check pinned in
/// <c>AntigravityRuntimeLifecycleTests</c> for the reproduction.</para>
///
/// <para><b>Not this class's job (yet).</b> No ACP-style reconnect/resume — a dead turn child is either a
/// clean end-of-turn (→ <see cref="RuntimePhase.Idle"/>) or a lost reviewer (→ <see cref="RuntimePhase.Terminal"/>),
/// never something to resume mid-turn. No terminal capability (<see cref="EmitsTerminalOutput"/> is
/// always <see langword="false"/> — agy's stdout is NDJSON protocol traffic, never terminal bytes).
/// Never emits a <c>session_ended</c> envelope — the server's <c>EndAgentSession</c> owns that
/// transition, exactly as it does for every other <see cref="IAcpTranscriptSource"/> runtime.</para>
/// </summary>
internal sealed class AntigravityHostedAgentRuntime : IHostedAgentRuntime, IAcpTranscriptSource {
    /// <summary>
    /// Runtime lifecycle phase — see the class doc's rule (a). Mutated only under
    /// <see cref="_stateLock"/>, but read lock-free from <see cref="HasExited"/>/<see cref="ExitCode"/>
    /// (a plain enum field gives those readers no visibility guarantee, so the backing store is the
    /// <see langword="int"/> <see cref="_phase"/>, read/written via <see cref="Volatile"/> — the same
    /// reasoning <c>AcpHostedAgentRuntime.Phase</c> documents).
    /// </summary>
    enum RuntimePhase { Starting, Executing, Idle, Terminal }

    /// <summary>One queued prompt turn. <see cref="Written"/> is non-null only for
    /// <see cref="SendUserInputAndWaitForWriteAsync"/> callers — resolved once this turn's process has
    /// actually spawned (agy has no separate "write" step; the prompt IS the spawn argument), faulted
    /// if the turn is dropped instead (terminal, full queue, or a spawn failure).</summary>
    sealed record PendingTurn(string Text, TaskCompletionSource? Written);

    readonly object _stateLock = new();
    int _phase = (int)RuntimePhase.Starting;

    /// <summary>See <see cref="RuntimePhase"/>'s remarks on why this is read via <see cref="Volatile"/>
    /// rather than a plain field access.</summary>
    RuntimePhase Phase => (RuntimePhase)Volatile.Read(ref _phase);

    /// <summary>
    /// The exit code this runtime WOULD report if it entered <see cref="RuntimePhase.Terminal"/> right
    /// now — updated after every turn that ends cleanly (mapped from the agy <c>result</c> event's
    /// <c>status</c>, NOT the OS exit code — see <see cref="ExitCode"/>'s remarks), so a
    /// <see cref="TerminateAsync"/> call that lands while merely <see cref="RuntimePhase.Idle"/> (the
    /// common case — a "terminate while merely idle between turns" test) reports the last
    /// turn's real outcome instead of a fabricated default. Guarded by <see cref="_stateLock"/>; not
    /// meaningful (and not read) until <see cref="Phase"/> is actually <see cref="RuntimePhase.Terminal"/>.
    /// </summary>
    int? _terminalExitCode;

    /// <summary>Completed exactly once, on entry to <see cref="RuntimePhase.Terminal"/> — see the class
    /// doc's rule (c). <see cref="ReadOutputAsync"/> parks on this and nothing else.</summary>
    readonly TaskCompletionSource _terminalTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cancelled by <see cref="TerminateAsync"/>/<see cref="DisposeAsync"/> — see the class
    /// doc's rule (d). Linked into every in-flight turn's spawn/read tokens, so cancelling this ONE
    /// token is what aborts whatever turn is currently running and frees <see cref="_turnGate"/>.</summary>
    readonly CancellationTokenSource _ownerCts = new();

    /// <summary>Held for a turn's WHOLE span (spawn → parse-drain → envelope-flush) by
    /// <see cref="RunTurnWorkerAsync"/>. <see cref="TerminateAsync"/> must never acquire this — see the
    /// class doc's rule (d).</summary>
    readonly SemaphoreSlim _turnGate = new(1, 1);

    /// <summary>This turn's live process, or <see langword="null"/> between turns — the field
    /// <see cref="HasExited"/> reads for its <see cref="RuntimePhase.Executing"/> case. Declared
    /// <see langword="volatile"/> so that lock-free read is well-defined; written only from the single
    /// turn worker (<see cref="ProcessTurnAsync"/>), so no additional synchronization is needed for the
    /// write side.</summary>
    volatile IAgyTurnProcess? _current;

    /// <summary>Injected turn spawner — the seam that keeps this runtime testable without ever
    /// spawning a real process. Takes the prompt text and the currently-known conversation id
    /// (<see langword="null"/> for turn 1), and returns the fresh <see cref="IAgyTurnProcess"/> for
    /// THIS turn only; a real implementation runs <c>agy -p &lt;prompt&gt; [--conversation-id &lt;id&gt;]
    /// --output-format stream-json</c>.</summary>
    readonly Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> _spawnTurn;

    /// <summary>
    /// Bound on how long the very FIRST turn's spawn call may take before <see cref="RuntimePhase.Starting"/>
    /// gives up and goes <see cref="RuntimePhase.Terminal"/> — <see langword="null"/> disables it. Applies
    /// ONLY to the spawn call itself (there is no live process yet to bound anything else against);
    /// every turn's actual execution, including turn 1's, is bounded by <see cref="_turnDeadline"/>
    /// instead once the process exists.
    /// </summary>
    readonly TimeSpan? _launchDeadline;

    /// <summary>Bound on how long ANY single turn (its process's whole run, from spawn to EOF) may take
    /// before it is reaped and the runtime goes <see cref="RuntimePhase.Terminal"/> — <see langword="null"/>
    /// disables it.</summary>
    readonly TimeSpan? _turnDeadline;

    /// <summary>How long a normal (non-deadline, non-cancelled) end-of-turn waits for the OS process to
    /// actually confirm exit after its stdout hits EOF, before giving up and trusting EOF alone. Small
    /// and fixed rather than configurable — this is a courtesy wait for the OS to catch up, not a
    /// meaningful timeout a caller would ever want to tune.</summary>
    static readonly TimeSpan ExitConfirmationGrace = TimeSpan.FromSeconds(5);

    readonly ILogger _logger;
    readonly string  _agentId;
    readonly string? _model;

    /// <summary>The ACP-equivalent conversation id — resolved from turn 1's <c>init</c> event and never
    /// changed again. See <see cref="AcpSessionId"/> and
    /// this runtime's own conversation-id-stability test: a LATER
    /// turn reporting a different id would mean this runtime silently forked the reviewer's history,
    /// which is treated as an unrecoverable protocol violation (→ <see cref="RuntimePhase.Terminal"/>),
    /// never silently accepted.</summary>
    string? _conversationId;

    string? _cwd;
    bool    _sessionStartedEmitted;
    int     _disposed;

    readonly Channel<AcpEventEnvelope> _transcript;
    readonly Channel<PendingTurn>      _pendingTurns;

    Task _turnWorkerTask = Task.CompletedTask;

    const int DefaultTranscriptCapacity  = 2000;
    const int DefaultPendingTurnsCapacity = 64;

    public AntigravityHostedAgentRuntime(
            Func<string, string?, CancellationToken, Task<IAgyTurnProcess>> spawnTurn,
            ILogger                                                        logger,
            string                                                         agentId = "",
            string?                                                        model = null,
            string?                                                        cwd = null,
            TimeSpan?                                                      launchDeadline = null,
            TimeSpan?                                                      turnDeadline = null,
            int?                                                           transcriptCapacity = null,
            int?                                                           pendingTurnsCapacity = null
        ) {
        _spawnTurn      = spawnTurn;
        _logger         = logger;
        _agentId        = agentId;
        _model          = model;
        _cwd            = cwd;
        _launchDeadline = launchDeadline;
        _turnDeadline   = turnDeadline;

        // DropOldest: this runtime is the sole writer (the turn worker), so unlike AcpHostedAgentRuntime
        // there is no concurrent writer to reason about — a stalled forwarder just loses trailing
        // transcript rather than ever blocking the worker.
        _transcript = Channel.CreateBounded<AcpEventEnvelope>(
            new BoundedChannelOptions(transcriptCapacity ?? DefaultTranscriptCapacity)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

        // NOT SingleReader: EnterTerminal's drain (rule below) and the worker's own dequeue loop can
        // both call TryRead around a Terminate race, so this channel must tolerate two readers even
        // though exactly one of them ever "wins" any given item.
        _pendingTurns = Channel.CreateBounded<PendingTurn>(
            new BoundedChannelOptions(pendingTurnsCapacity ?? DefaultPendingTurnsCapacity)
                { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });

        _turnWorkerTask = RunTurnWorkerAsync();
    }

    public string Vendor              => "antigravity";
    public int    Pid                 => _current?.Pid ?? 0;
    public bool   EmitsTerminalOutput => false;

    /// <summary>Rule (b): <see cref="RuntimePhase.Idle"/> is logically alive (no process needed to prove
    /// it — see the class doc), <see cref="RuntimePhase.Terminal"/> is always exited, and
    /// <see cref="RuntimePhase.Starting"/>/<see cref="RuntimePhase.Executing"/> defer to whatever process
    /// (if any) is currently running.</summary>
    public bool HasExited => Phase switch {
        RuntimePhase.Idle     => false,
        RuntimePhase.Terminal => true,
        _                     => _current?.HasExited ?? false,
    };

    /// <summary>
    /// Mapped from the agy <c>result</c> event's <c>status</c> field, NEVER the OS exit code —
    /// <see langword="null"/> while non-terminal; <c>0</c> once <see cref="RuntimePhase.Terminal"/> if the
    /// last completed turn's <c>result.status</c> was <c>SUCCESS</c>, non-zero otherwise (including "a
    /// turn child died without ever emitting a terminal <c>result</c>", rule (a)'s EOF-without-result
    /// case). A per-turn OS exit code describes one subprocess invocation, not the reviewer's overall
    /// outcome, and an upstream agy bug means a clean run can carry an empty <c>response</c> — so
    /// <c>status</c>, not the process's own exit code, is the only trustworthy signal here.
    /// </summary>
    public int? ExitCode => Phase == RuntimePhase.Terminal ? _terminalExitCode : null;

    public string  AcpSessionId  => _conversationId ?? "";
    public string  Cwd           => _cwd ?? "";
    public string? ResolvedModel => _model;
    public ChannelReader<AcpEventEnvelope> Envelopes => _transcript.Reader;

    /// <summary>Rule (c): parks on the single constructor-owned <see cref="_terminalTcs"/> and nothing
    /// else. Yields no bytes ever — agy's stdout is NDJSON protocol traffic, never terminal output
    /// (mirrors <c>AcpHostedAgentRuntime.ReadOutputAsync</c>'s reasoning exactly).</summary>
    public async IAsyncEnumerable<byte[]> ReadOutputAsync(
            [EnumeratorCancellation] CancellationToken ct = default) {
        await _terminalTcs.Task.WaitAsync(ct).ConfigureAwait(false);
        yield break;
    }

    public Task SendUserInputAsync(string text) => EnqueueTurn(text, acknowledgeWrite: false);

    public Task SendUserInputAndWaitForWriteAsync(string text) => EnqueueTurn(text, acknowledgeWrite: true);

    /// <summary>Acquire-then-IMMEDIATELY-release: queues behind an in-flight turn and returns once it's
    /// done, but never itself holds <see cref="_turnGate"/> — see the class doc's rule (d), which is the
    /// exact property <see cref="TerminateAsync"/> also depends on.</summary>
    public async Task WaitForTurnIdleAsync(CancellationToken ct) {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        _turnGate.Release();
    }

    /// <summary>
    /// Enqueues the turn and returns immediately — never blocks on, or observes, the turn's
    /// completion. A dropped enqueue (the runtime is already <see cref="RuntimePhase.Terminal"/>, or the
    /// pending-turns queue is at capacity) never throws when no acknowledgement was requested: the
    /// channel's own atomicity (<see cref="ChannelWriter{T}.TryWrite"/> against a completed or full
    /// bounded channel) is what makes "terminal" and "full" collapse into the same silent-drop path
    /// without a separate check-then-act race.
    /// </summary>
    Task EnqueueTurn(string text, bool acknowledgeWrite) {
        var written = acknowledgeWrite
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        if (!_pendingTurns.Writer.TryWrite(new PendingTurn(text, written))) {
            _logger.LogDebug(
                "Antigravity: dropped a prompt turn — the runtime is terminal or its pending-turns queue is full.");
            written?.TrySetException(new InvalidOperationException(
                "Antigravity runtime dropped this input — it is terminal, or its pending-turns queue is full."));
        }

        return written?.Task ?? Task.CompletedTask;
    }

    /// <summary>
    /// The single, long-running turn worker — the only reader that ever SPAWNS a turn (
    /// <see cref="EnterTerminal"/>'s drain also reads from <see cref="_pendingTurns"/>, but only to fault
    /// stranded turns, never to spawn one). Drains strictly FIFO, one turn fully at a time: the phase
    /// flip to <see cref="RuntimePhase.Executing"/> happens at dequeue (rule (a)'s "next turn dequeued"
    /// trigger, uniformly for turn 1 and every later turn), and the flip back to
    /// <see cref="RuntimePhase.Idle"/> — or onward to <see cref="RuntimePhase.Terminal"/>, whichever
    /// <see cref="ProcessTurnAsync"/> decided — happens BEFORE <see cref="_turnGate"/> is released, so a
    /// <see cref="WaitForTurnIdleAsync"/> caller that returns always observes a fully-settled phase, never
    /// a stale <see cref="RuntimePhase.Executing"/>.
    /// </summary>
    async Task RunTurnWorkerAsync() {
        var ownerCt   = _ownerCts.Token;
        var firstTurn = true;

        try {
            while (await _pendingTurns.Reader.WaitToReadAsync(ownerCt).ConfigureAwait(false)) {
                while (_pendingTurns.Reader.TryRead(out var turn)) {
                    if (Phase == RuntimePhase.Terminal) {
                        // Lost the race with EnterTerminal's own drain for THIS item (or Terminal was
                        // entered between WaitToReadAsync and TryRead) — never spawn a turn once
                        // terminal, no matter which reader happened to pull it off the channel.
                        turn.Written?.TrySetException(new InvalidOperationException(
                            "Antigravity runtime is terminal; this input was never delivered."));
                        continue;
                    }

                    lock (_stateLock) {
                        if (Phase != RuntimePhase.Terminal)
                            Volatile.Write(ref _phase, (int)RuntimePhase.Executing);
                    }

                    await _turnGate.WaitAsync(ownerCt).ConfigureAwait(false);
                    try {
                        await ProcessTurnAsync(turn, firstTurn, ownerCt).ConfigureAwait(false);

                        // Settle the phase BEFORE releasing the gate (not after) — see this method's
                        // remarks: WaitForTurnIdleAsync must never observe a stale Executing.
                        lock (_stateLock) {
                            if (Phase == RuntimePhase.Executing)
                                Volatile.Write(ref _phase, (int)RuntimePhase.Idle);
                        }
                    } finally {
                        _turnGate.Release();
                    }

                    firstTurn = false;
                }
            }
        } catch (OperationCanceledException) {
            // Normal shutdown (TerminateAsync/DisposeAsync cancelled _ownerCts) — Terminal was already
            // entered by whoever cancelled it.
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Antigravity: turn worker ended unexpectedly (agentId={AgentId}).", _agentId);
            EnterTerminal(exitCode: 1);
        }
    }

    /// <summary>
    /// Processes exactly one turn: spawns its process (bounded by <see cref="_launchDeadline"/> for
    /// turn 1 only), reads its NDJSON lines until EOF (bounded by <see cref="_turnDeadline"/>),
    /// translates them into transcript envelopes, and decides the turn's outcome per rule (a) — Idle
    /// only on "child exited after a terminal <c>result</c>", Terminal on every other ending. Never
    /// throws: every failure path reports through <paramref name="turn"/>'s ack and/or
    /// <see cref="EnterTerminal"/> instead, so <see cref="RunTurnWorkerAsync"/>'s loop never sees an
    /// unexpected exception from a turn that merely failed (as opposed to the worker itself faulting).
    /// </summary>
    async Task ProcessTurnAsync(PendingTurn turn, bool firstTurn, CancellationToken ownerCt) {
        IAgyTurnProcess process;

        using (var spawnCts = CancellationTokenSource.CreateLinkedTokenSource(ownerCt)) {
            if (firstTurn && _launchDeadline is { } launchDeadline)
                spawnCts.CancelAfter(launchDeadline);

            try {
                process = await _spawnTurn(turn.Text, _conversationId, spawnCts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ownerCt.IsCancellationRequested) {
                // Stop/dispose raced the spawn — EnterTerminal was already called by TerminateAsync.
                turn.Written?.TrySetException(new InvalidOperationException(
                    "Antigravity runtime stopped before this turn's process spawned."));
                return;
            } catch (OperationCanceledException) {
                // Only the launch deadline could cancel spawnCts without ownerCt also being cancelled.
                turn.Written?.TrySetException(new TimeoutException(
                    "Antigravity: turn 1's process did not spawn within the launch deadline."));
                _logger.LogWarning("Antigravity: turn 1 spawn exceeded the launch deadline; entering Terminal.");
                EnterTerminal(exitCode: 1);
                return;
            } catch (Exception ex) {
                // Spawn failure or auth failure, surfaced by the injected spawner as a thrown exception.
                turn.Written?.TrySetException(ex);
                _logger.LogWarning(ex, "Antigravity: turn process failed to spawn; entering Terminal.");
                EnterTerminal(exitCode: 1);
                return;
            }
        }

        turn.Written?.TrySetResult();
        _current = process;

        var accumulator        = new AntigravityStepAccumulator();
        var sawTerminalResult  = false;
        var conversationChanged = false;
        string? resultStatus   = null;

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ownerCt);
        if (_turnDeadline is { } turnDeadline) turnCts.CancelAfter(turnDeadline);

        try {
            await foreach (var line in process.ReadLinesAsync(turnCts.Token).ConfigureAwait(false)) {
                var evt = AntigravityNdjson.TryParseLine(line);
                if (evt is null) continue;

                switch (evt.Kind) {
                    case AntigravityEventKind.Init:
                        if (!HandleInit(evt)) conversationChanged = true;
                        break;

                    case AntigravityEventKind.StepUpdate:
                        accumulator.Add(evt);
                        foreach (var env in accumulator.Flush(_model)) EmitEnvelope(env);
                        break;

                    case AntigravityEventKind.Result:
                        // Never translated to an envelope (session_ended is the server's to own) —
                        // only read here to drive this runtime's own logical-terminal state.
                        sawTerminalResult = true;
                        resultStatus      = evt.Status;
                        break;
                }

                if (conversationChanged) break; // stop reading — this turn's outcome is already decided.
            }
        } catch (OperationCanceledException) {
            // Distinguished below via ownerCt/turnCts — both are legitimate reasons this can throw.
        } finally {
            _current = null;
        }

        if (ownerCt.IsCancellationRequested) return; // stop/dispose — Terminal already entered by TerminateAsync.

        if (conversationChanged) {
            // A changed conversation id means this runtime silently forked the reviewer's history —
            // an unrecoverable protocol violation, reaped the same way a blown deadline is: kill the
            // child THEN go Terminal, never leave an orphaned process behind.
            try {
                await process.TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Antigravity: failed to terminate a turn after a conversation-id mismatch.");
            }
            EnterTerminal(exitCode: 1);
            return;
        }

        if (turnCts.IsCancellationRequested) {
            // The per-turn deadline fired, not an owner cancel — reap this turn's child and go Terminal.
            try {
                await process.TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Antigravity: failed to terminate a turn that exceeded its deadline.");
            }
            _logger.LogWarning("Antigravity: turn exceeded its per-turn deadline; entering Terminal.");
            EnterTerminal(exitCode: 1);
            return;
        }

        // Normal EOF — give the OS a short, bounded grace period to confirm the exit, then trust EOF
        // regardless: a process whose stdout closed is done producing transcript either way.
        await process.WaitForExitAsync(ExitConfirmationGrace).ConfigureAwait(false);

        if (!sawTerminalResult) {
            // Rule (a)'s load-bearing case: EOF without a terminal `result` means the reviewer died
            // mid-turn. Terminal, never Idle — going Idle here would park ReadOutputAsync forever.
            EnterTerminal(exitCode: process.ExitCode is { } code and not 0 ? code : 1);
            return;
        }

        var success = string.Equals(resultStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase);

        // Not yet Terminal — this only records what Terminal WOULD report if entered right now (see
        // _terminalExitCode's remarks). RunTurnWorkerAsync flips the phase itself, back in its caller.
        lock (_stateLock) _terminalExitCode = success ? 0 : 1;
    }

    /// <summary>Emits <c>session_started</c> at most once (turn 1's <c>init</c> only — a later turn's own
    /// fresh <c>init</c> must never re-fire it), and enforces conversation-id stability: a later turn
    /// reporting a DIFFERENT id from the one turn 1 established would mean this runtime silently forked
    /// the reviewer's history onto a new conversation. Returns <see langword="false"/> on that mismatch
    /// (the caller reaps the turn and enters Terminal) rather than raising it directly — this method
    /// runs inside the read loop, before the caller has decided how to shut the current process down,
    /// so it must never trigger termination itself.</summary>
    bool HandleInit(AntigravityEvent evt) {
        if (evt.ConversationId is { Length: > 0 } cid) {
            if (_conversationId is null) {
                _conversationId = cid;
            } else if (!string.Equals(_conversationId, cid, StringComparison.Ordinal)) {
                _logger.LogError(
                    "Antigravity: conversation id changed from {Old} to {New} mid-session (agentId={AgentId}); entering Terminal.",
                    _conversationId, cid, _agentId);
                return false;
            }
        }

        if (evt.Cwd is { Length: > 0 } cwd) _cwd = cwd;

        if (!_sessionStartedEmitted) {
            _sessionStartedEmitted = true;
            foreach (var env in AntigravityNdjson.ToEnvelopes(evt, _model)) EmitEnvelope(env);
        }

        return true;
    }

    void EmitEnvelope(AcpEventEnvelope env) {
        if (!_transcript.Writer.TryWrite(env))
            _logger.LogDebug("Antigravity: dropped a transcript envelope — the transcript channel is already completed.");
    }

    /// <summary>
    /// Enters <see cref="RuntimePhase.Terminal"/>, idempotently: the phase check-and-flip happens under
    /// <see cref="_stateLock"/> (reentrant-safe — <see cref="TerminateAsync"/> calls this from inside its
    /// own already-held <see cref="_stateLock"/> section, which C#'s <see langword="lock"/> permits on the
    /// same thread), and every other effect — completing <see cref="_terminalTcs"/>, draining
    /// <see cref="_pendingTurns"/>, completing <see cref="_transcript"/> — runs exactly once, only for the
    /// caller that actually performed the transition.
    ///
    /// <para>The pending-turns drain exists because a turn already sitting in the channel when Terminal
    /// is entered would otherwise never be observed: once <see cref="TerminateAsync"/> cancels
    /// <see cref="_ownerCts"/>, <see cref="RunTurnWorkerAsync"/>'s own <c>WaitToReadAsync</c> throws
    /// immediately rather than draining what's left — so this method faults every stranded turn's
    /// <c>Written</c> acknowledgement itself, rather than leaving a
    /// <see cref="SendUserInputAndWaitForWriteAsync"/> caller awaiting a write that will never happen.</para>
    /// </summary>
    /// <returns><see langword="true"/> if this call actually performed the transition;
    /// <see langword="false"/> if the runtime was already <see cref="RuntimePhase.Terminal"/>.</returns>
    bool EnterTerminal(int exitCode) {
        lock (_stateLock) {
            if (Phase == RuntimePhase.Terminal) return false;

            Volatile.Write(ref _phase, (int)RuntimePhase.Terminal);
            _terminalExitCode = exitCode;
        }

        _terminalTcs.TrySetResult();

        _pendingTurns.Writer.TryComplete();
        while (_pendingTurns.Reader.TryRead(out var dropped))
            dropped.Written?.TrySetException(new InvalidOperationException(
                "Antigravity runtime is terminal; this input was never delivered."));

        _transcript.Writer.TryComplete();

        return true;
    }

    public Task SendSpecialKeyAsync(string key) {
        // agy has no special-key channel (there is no live protocol connection between turns to send
        // one over) — best-effort no-op, mirroring AcpHostedAgentRuntime's own no-op for the same
        // reason.
        _logger.LogDebug("Antigravity runtime ignoring SendSpecialKeyAsync({Key}) — no special-key surface.", key);
        return Task.CompletedTask;
    }

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException(
            "Local-attach raw input is a PTY-only surface; the Antigravity runtime has no equivalent channel.");

    public void Resize(ushort cols, ushort rows) {
        // No terminal capability — agy's stdout is NDJSON protocol traffic, not a terminal. No-op.
    }

    /// <summary>
    /// agy has no in-band cancel RPC (unlike ACP's <c>session/cancel</c>) — there is no live connection
    /// to send one over between turns, and interrupting an IN-FLIGHT turn's process is exactly what
    /// <see cref="TerminateAsync"/> already does. Best-effort no-op, matching
    /// <see cref="IHostedAgentRuntime.RequestGracefulStopAsync"/>'s documented contract ("the
    /// orchestrator falls through to terminate").
    /// </summary>
    public Task RequestGracefulStopAsync() {
        _logger.LogDebug("Antigravity runtime: no graceful-stop channel between turns; falling through to terminate.");
        return Task.CompletedTask;
    }

    /// <summary>"Exited" here means logically terminal (see rule (b)) — waits on the same
    /// <see cref="_terminalTcs"/> signal <see cref="ReadOutputAsync"/> parks on. Returns silently on
    /// timeout, per the interface contract, rather than propagating <see cref="TimeoutException"/>.</summary>
    public async Task WaitForExitAsync(TimeSpan? timeout = null) {
        if (timeout is { } t) {
            try {
                await _terminalTcs.Task.WaitAsync(t).ConfigureAwait(false);
            } catch (TimeoutException) {
                // Returns silently on timeout — per this method's interface contract.
            }
        } else {
            await _terminalTcs.Task.ConfigureAwait(false);
        }
    }

    /// <summary>Rule (d) — see the class doc. Takes <see cref="_stateLock"/> (NEVER <see cref="_turnGate"/>),
    /// enters <see cref="RuntimePhase.Terminal"/>, then — OUTSIDE that lock — cancels
    /// <see cref="_ownerCts"/> (which is what actually unblocks an in-flight turn and frees the gate) and
    /// terminates the current turn's process, if any.</summary>
    public async Task TerminateAsync(TimeSpan? timeout = null) {
        IAgyTurnProcess? current;

        lock (_stateLock) {                 // NOT _turnGate — see the class doc's rule (d).
            if (!EnterTerminal(_terminalExitCode ?? 0)) return;
            current = _current;
        }

        await _ownerCts.CancelAsync().ConfigureAwait(false);

        if (current is not null) {
            try {
                await current.TerminateAsync(timeout).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Antigravity: failed to terminate the in-flight turn's process.");
            }
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        try {
            await _turnWorkerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } catch {
            // Best-effort — a stuck turn worker must never hang dispose.
        }

        if (_current is { } current) {
            try {
                await current.DisposeAsync().ConfigureAwait(false);
            } catch {
                // Best-effort.
            }
        }

        _ownerCts.Dispose();
        _turnGate.Dispose();
    }
}
