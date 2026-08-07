// src/Capacitor.Cli.Daemon/Services/AntigravityHostedAgentRuntime.cs
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// The per-turn durable PID-record callbacks — the exec-per-turn analogue of
/// <c>AcpPidRecordCallbacks</c>, published as ONE immutable bundle for the same reason: two
/// independently-settable callbacks have a partial-wiring window in which a real recorder is
/// installed but the clearer is not, and a turn could then persist a record nothing would clear.
///
/// <para><b>Cadence: record on turn spawn, clear on CONFIRMED turn exit.</b> Every round of a review
/// is a different child with a different pid, so a one-shot record taken at launch names turn 1's pid
/// and nothing after it — a daemon SIGKILL during round 2 would leave a child reapable by neither the
/// record pass nor the env-marker pass (which is Linux-only, and this reviewer is POSIX-meaning-macOS
/// in practice).</para>
///
/// <para><b><see cref="Record"/> MUST throw on failure</b>, and the runtime treats a throw as the turn
/// failing: a spawned child the daemon cannot durably record is reaped and the runtime goes terminal
/// rather than running untracked. <see cref="Clear"/> runs only once the turn's child is OBSERVED
/// exited — an unconfirmed survivor keeps its record, because the record is the only thing that can
/// still reap it.</para>
///
/// <para>Left <see langword="null"/> on <see cref="AntigravityHostedAgentRuntime.PidCallbacks"/> the
/// runtime records nothing. That is the deliberate pre-wiring state, not a fail-open hole: turn 1
/// spawns INSIDE the factory's <c>StartAsync</c>, before the orchestrator can wire anything, and the
/// orchestrator's own one-shot record covers exactly that turn immediately after the launch
/// returns.</para>
/// </summary>
internal sealed record AgyPidRecordCallbacks(Action<int> Record, Action Clear);

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
/// <para><b>(e) <see cref="WaitForConversationIdAsync"/> is not optional for a factory that binds a
/// transcript.</b> <see cref="AcpSessionId"/> reads <c>""</c> until turn 1's <c>init</c> resolves it, and
/// nothing else in this class's public surface forces that ordering: <see cref="SendUserInputAsync"/>
/// returns as soon as the turn is enqueued — before the worker even dequeues it —
/// <see cref="SendUserInputAndWaitForWriteAsync"/> resolves at SPAWN, still before any line is read, and
/// <see cref="WaitForTurnIdleAsync"/> is NOT a substitute (its enqueue→gate hand-off is itself
/// asynchronous, so it can observe a momentarily-free gate and return before the turn has even started).
/// A factory that sends the initial prompt and immediately hands this runtime to the orchestrator — which
/// reads <see cref="AcpSessionId"/> unconditionally and synchronously the moment a launch returns — WILL
/// bind the transcript to <c>""</c>: a silent, permanent correlation break, not a flaky race. <b>Any
/// factory for this runtime MUST <see langword="await"/> <see cref="WaitForConversationIdAsync"/> after
/// sending the initial prompt and BEFORE returning control to the orchestrator.</b> The barrier itself is
/// what guarantees this never hangs — it resolves on EVERY path a turn can end on, not just the obvious
/// ones: <see cref="EnterTerminal"/> faults it for every route to <see cref="RuntimePhase.Terminal"/>
/// (spawn/auth failure, either deadline, EOF-without-a-result, an explicit stop), and the clean-success
/// tail of <see cref="ProcessTurnAsync"/> ALSO faults it (via
/// <see cref="FaultConversationIdBarrierIfUnresolved"/>) for the one path that does NOT call
/// <see cref="EnterTerminal"/> at all — a turn that settles to <see cref="RuntimePhase.Idle"/> having
/// never seen a non-empty <c>conversation_id</c> on an <c>init</c> event (a malformed or missing
/// <c>init</c> whose transcript nonetheless reaches a terminal <c>result</c>). A first cut of this fix
/// missed exactly that second path — the SAME failure mode the barrier exists to prevent, reopened
/// through a different door, because a healthy-looking Idle transition is easy to assume can't need
/// fault handling. As belt-and-braces ONLY (never the primary defense, which is the barrier's own total
/// resolution above), a factory MAY additionally pass its own launch-deadline token into
/// <see cref="WaitForConversationIdAsync"/>.</para>
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

    /// <summary>Completed once <see cref="_conversationId"/> is first resolved (turn 1's <c>init</c>), or
    /// faulted on entry to <see cref="RuntimePhase.Terminal"/> if that never happens (e.g. turn 1's spawn
    /// itself failed) — see rule (e) and <see cref="WaitForConversationIdAsync"/>. <c>TrySet*</c> on both
    /// sides means whichever happens first wins and the other is a harmless no-op.</summary>
    readonly TaskCompletionSource _conversationIdResolved = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>
    /// Best-effort teardown callback, invoked at most once at the very END of <see cref="DisposeAsync"/>
    /// — and only when every bound on that path actually SETTLED rather than merely expired (see
    /// <see cref="DisposeAsync"/>'s gate and <see cref="_turnExitConfirmed"/>). The factory uses it to
    /// remove the per-launch <c>HOME</c>, whose content is the reviewer's own conversation JSONL (the
    /// caller's diff, source excerpts and findings) — disposal, not disk hygiene, and not something
    /// the daemon-epoch sweep should be left to do at the next boot.
    /// Never throws: a home we cannot delete must not turn a completed review into a failed dispose.
    /// </summary>
    readonly Action? _onDisposed;

    /// <summary>
    /// Whether the LAST turn's child was OBSERVED exited while it was still observable — read inside
    /// <see cref="ProcessTurnAsync"/>'s outer <c>finally</c>, immediately before this runtime lets go
    /// of that process handle, because a disposed process object reports
    /// <see cref="IAgyTurnProcess.HasExited"/> <see langword="true"/> and asking afterwards would
    /// mistake "no longer observable" for "confirmed exited".
    ///
    /// <para>Starts <see langword="true"/> — a runtime that never spawned a turn has no child to
    /// confirm — and is cleared the instant one exists. <see cref="DisposeAsync"/>'s cleanup gate is
    /// its only consumer; declared <see langword="volatile"/> because the turn worker writes it and
    /// the disposing caller reads it.</para>
    /// </summary>
    volatile bool _turnExitConfirmed = true;

    /// <summary>
    /// Liveness-supervision: the per-launch activity clock, assigned by the factory BEFORE the first
    /// turn is enqueued. Every stamp below is a no-op guard rather than a throw, so a direct
    /// construction (tests, a caller that does not care about liveness) keeps working — which is
    /// exactly why a clock assigned LATE is dangerous: it fails silently, leaving the launch's whole
    /// stamp sequence written against nothing.
    ///
    /// <para><b>The exec-per-turn shape makes <see cref="AgentActivityClock.TurnInFlight"/> the
    /// load-bearing one.</b> <c>AgentOrchestrator.FindReviewersToReap</c> lets a held turn suppress
    /// the plain idle rule OUTRIGHT, so a flag stuck <see langword="true"/> produces a reviewer that
    /// is never idle-reaped — and between turns this runtime has no process at all, so nothing
    /// external would ever contradict it. It is therefore cleared in a <c>finally</c> around the whole
    /// turn AND on entry to <see cref="RuntimePhase.Terminal"/>.</para>
    /// </summary>
    internal AgentActivityClock? ActivityClock { get; set; }

    /// <summary>The per-turn durable PID-record seam — see <see cref="AgyPidRecordCallbacks"/> for the
    /// cadence and the fail-closed contract. Assigned by the orchestrator immediately after
    /// registration, which is why turn 1 (spawned inside the factory's launch) is deliberately not
    /// covered here.</summary>
    internal AgyPidRecordCallbacks? PidCallbacks { get; set; }

    /// <summary>The ACP-equivalent conversation id — resolved from turn 1's <c>init</c> event and never
    /// changed again. See <see cref="AcpSessionId"/> and
    /// this runtime's own conversation-id-stability test: a LATER
    /// turn reporting a different id would mean this runtime silently forked the reviewer's history,
    /// which is treated as an unrecoverable protocol violation (→ <see cref="RuntimePhase.Terminal"/>),
    /// never silently accepted.
    ///
    /// <para>Written only from the single turn worker (inside <see cref="HandleInit"/>), but read
    /// cross-thread from <see cref="AcpSessionId"/> — declared <see langword="volatile"/> for the same
    /// reason <see cref="_current"/> is, so a reader on another thread is guaranteed to see the write
    /// rather than a stale value (a plain field gives no such guarantee, e.g. on arm64). The primary
    /// ordering guarantee for a factory is still <see cref="WaitForConversationIdAsync"/> (rule (e)) —
    /// this is the belt-and-braces fix for every OTHER read of <see cref="AcpSessionId"/>/<see cref="Cwd"/>
    /// that isn't preceded by that barrier.</para>
    /// </summary>
    volatile string? _conversationId;

    /// <summary>See <see cref="_conversationId"/>'s remarks — same cross-thread visibility reasoning.</summary>
    volatile string? _cwd;

    /// <summary>Written and read only inside <see cref="HandleInit"/>, which always runs on the single
    /// turn worker — not read cross-thread today, but declared <see langword="volatile"/> alongside its
    /// siblings above so that stays true even if a future change adds a cross-thread reader.</summary>
    volatile bool _sessionStartedEmitted;

    int _disposed;

    readonly Channel<AcpEventEnvelope> _transcript;
    readonly Channel<PendingTurn>      _pendingTurns;
    readonly int                       _pendingTurnsCapacity;
    int                                _droppedPendingTurns;

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
            int?                                                           pendingTurnsCapacity = null,
            Action?                                                        onDisposed = null
        ) {
        _spawnTurn            = spawnTurn;
        _logger               = logger;
        _agentId              = agentId;
        _model                = model;
        _cwd                  = cwd;
        _launchDeadline       = launchDeadline;
        _turnDeadline         = turnDeadline;
        _pendingTurnsCapacity = pendingTurnsCapacity ?? DefaultPendingTurnsCapacity;
        _onDisposed           = onDisposed;

        // DropOldest: the turn worker is the only writer that matters for ordering, but
        // SingleWriter=false — EnterTerminal's TryComplete() can run concurrently with an in-flight
        // TryWrite from the worker thread (a Terminate racing the tail end of a turn), which is outside
        // the SingleWriter contract even though only one thread ever calls TryWrite itself.
        _transcript = Channel.CreateBounded<AcpEventEnvelope>(
            new BoundedChannelOptions(transcriptCapacity ?? DefaultTranscriptCapacity)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        // NOT SingleReader: EnterTerminal's drain (rule below) and the worker's own dequeue loop can
        // both call TryRead around a Terminate race, so this channel must tolerate two readers even
        // though exactly one of them ever "wins" any given item.
        _pendingTurns = Channel.CreateBounded<PendingTurn>(
            new BoundedChannelOptions(_pendingTurnsCapacity)
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
    /// exact property <see cref="TerminateAsync"/> also depends on.
    ///
    /// <para><b>NOT a "has this turn started" barrier.</b> The enqueue→gate hand-off is itself
    /// asynchronous (the worker has to wake up from its own await and actually acquire the gate), so a
    /// caller that enqueues a turn and immediately awaits this can observe a momentarily-free gate and
    /// return before that turn has even started — see rule (e) and
    /// <see cref="WaitForConversationIdAsync"/> for the barrier that doesn't have this gap.</para>
    /// </summary>
    public async Task WaitForTurnIdleAsync(CancellationToken ct) {
        await _turnGate.WaitAsync(ct).ConfigureAwait(false);
        _turnGate.Release();
    }

    /// <summary>
    /// Rule (e) — see the class doc. Completes once <see cref="AcpSessionId"/> has been resolved from
    /// turn 1's <c>init</c> event, or faults if the runtime reaches <see cref="RuntimePhase.Terminal"/>
    /// first (e.g. turn 1's spawn itself failed) — so a caller can never hang on a conversation id that
    /// will never arrive. <b>Any factory for this runtime must await this after sending the initial
    /// prompt and before returning control to the orchestrator</b>, which reads
    /// <see cref="AcpSessionId"/> unconditionally and synchronously the moment a launch returns.
    /// </summary>
    public Task WaitForConversationIdAsync(CancellationToken ct) => _conversationIdResolved.Task.WaitAsync(ct);

    /// <summary>
    /// Enqueues the turn and returns immediately — never blocks on, or observes, the turn's
    /// completion. A dropped enqueue never throws when no acknowledgement was requested: the channel's
    /// own atomicity (<see cref="ChannelWriter{T}.TryWrite"/> against a completed or full bounded
    /// channel) is what makes "terminal" and "full" collapse into one non-racy check, but the two are
    /// still logged (and faulted, for an acknowledging caller) distinctly below — a full queue is an
    /// operational signal worth a warning and a running count, matching
    /// <c>AcpHostedAgentRuntime.EnqueueTurn</c>'s same distinction for its own pending-turns queue.
    /// </summary>
    Task EnqueueTurn(string text, bool acknowledgeWrite) {
        var written = acknowledgeWrite
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        if (!_pendingTurns.Writer.TryWrite(new PendingTurn(text, written))) {
            if (Phase == RuntimePhase.Terminal) {
                _logger.LogDebug("Antigravity: dropped a prompt turn — the runtime is terminal.");
                written?.TrySetException(new InvalidOperationException(
                    "Antigravity runtime is terminal; this input was dropped."));
            } else {
                // The only other reason TryWrite can fail on this bounded, not-yet-completed channel.
                var dropped = Interlocked.Increment(ref _droppedPendingTurns);
                _logger.LogWarning(
                    "Antigravity: pending-turns queue full (capacity={Capacity}) — dropping this input; "
                  + "{DroppedCount} dropped this session so far (the turn worker is likely stuck on a "
                  + "stalled turn).", _pendingTurnsCapacity, dropped);
                written?.TrySetException(new InvalidOperationException(
                    "Antigravity pending-turns queue is full; this input was dropped."));
            }
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

                    // The turn is in flight from here — set BEFORE the spawn, because a turn whose
                    // process is still being created is legitimately not idle. The finally is what
                    // makes the pair exact: a faulted, deadlined or cancelled turn must never leave
                    // the flag held, since a held turn suppresses the reaper's idle rule outright.
                    ActivityClock?.SetTurnInFlight(true);

                    try {
                        await ProcessTurnAsync(turn, firstTurn, ownerCt).ConfigureAwait(false);

                        // Settle the phase BEFORE releasing the gate (not after) — see this method's
                        // remarks: WaitForTurnIdleAsync must never observe a stale Executing.
                        lock (_stateLock) {
                            if (Phase == RuntimePhase.Executing)
                                Volatile.Write(ref _phase, (int)RuntimePhase.Idle);
                        }
                    } finally {
                        // Also before the release, for the same reason the phase settles first: a
                        // WaitForTurnIdleAsync caller that returns must observe a fully-settled agent,
                        // never one that still claims a turn.
                        ActivityClock?.SetTurnInFlight(false);
                        _turnGate.Release();
                    }

                    firstTurn = false;
                }
            }
        } catch (OperationCanceledException) when (ownerCt.IsCancellationRequested) {
            // Normal shutdown (TerminateAsync/DisposeAsync cancelled _ownerCts) — Terminal was already
            // entered by whoever cancelled it. FILTERED on the owner token deliberately: an unfiltered
            // catch here makes "we asked for this" and "something cancelled that we did not expect"
            // indistinguishable, and the second one must reach EnterTerminal below. ProcessTurnAsync
            // individually catches every IAgyTurnProcess call EXCEPT its bounded exit-confirmation
            // wait, whose contract ("returns silently on timeout") does not forbid an implementation
            // propagating its own internal cancellation — and this runtime is built against the
            // interface, not against AgyTurnProcess. Swallowing that would exit this loop without
            // entering Terminal: _terminalTcs never completes, ReadOutputAsync parks forever and
            // FinalizeAgentRunAsync never fires — rule (a)'s hang through a door rule (a) does not
            // cover.
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
    /// The spawned process is disposed on EVERY exit path (the outer <c>finally</c>) — every round of a
    /// review would otherwise leak that turn's pipes/handles.
    /// </summary>
    async Task ProcessTurnAsync(PendingTurn turn, bool firstTurn, CancellationToken ownerCt) {
        var process = await SpawnTurnProcessAsync(turn, firstTurn, ownerCt).ConfigureAwait(false);
        if (process is null) return; // spawn failed/cancelled — SpawnTurnProcessAsync already handled it.

        // A child exists from here on, so DisposeAsync may no longer assume there is nothing to
        // confirm. Re-established (from what the process itself reports) in this method's outer
        // finally, while it is still observable.
        _turnExitConfirmed = false;

        // The first of two launch-handshake stamps (the second is `session_created`, when this turn's
        // `init` resolves the conversation id). Turn 1 only: LaunchStage is Starting-only and the
        // orchestrator clears it the instant the agent reaches Running, so a later turn re-stamping it
        // would resurrect a stage on an already-running agent.
        if (firstTurn) ActivityClock?.SetLaunchStage("spawned");

        try {
            // The durable PID record precedes anything else this turn does with its child, and
            // precedes the _current publish so a refusal leaves nothing published to reconcile. Same
            // fail-closed contract the ACP reconnect seam applies per candidate, applied here per TURN
            // because every round spawns a differently-pid'd child (see AgyPidRecordCallbacks). A
            // throw is the record store refusing: the child is reaped and the runtime goes terminal
            // rather than running untracked — never swallowed.
            if (PidCallbacks is { } pids) {
                try {
                    pids.Record(process.Pid);
                } catch (Exception ex) {
                    _logger.LogWarning(
                        ex, "Antigravity: could not durably record this turn's child (agentId={AgentId}, "
                          + "pid={Pid}); failing the turn rather than running it untracked.",
                        _agentId, process.Pid);

                    turn.Written?.TrySetException(ex);

                    try {
                        await process.TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    } catch (Exception terminateEx) {
                        _logger.LogDebug(
                            terminateEx, "Antigravity: failed to terminate a turn whose PID record was refused.");
                    }

                    EnterTerminal(exitCode: 1);
                    return;
                }
            }

            // Publish _current and check for an already-Terminal runtime as ONE atomic operation under
            // _stateLock — the SAME lock TerminateAsync captures _current under. This closes a real
            // orphan-child race: TerminateAsync's capture and this publish can interleave in either
            // order around the spawn call above, and whichever one acquires _stateLock SECOND sees the
            // other's effect and takes responsibility for the child — so exactly one of the two ever
            // terminates it, never zero. (If TerminateAsync's capture logic instead ran outside a lock
            // shared with this publish, the capture could read null — this turn's process not yet
            // published — while this method's later cancellation-observation also finds ownerCt already
            // cancelled and does nothing, and the child would never be reaped by either side.)
            bool alreadyTerminal;
            lock (_stateLock) {
                alreadyTerminal = Phase == RuntimePhase.Terminal;
                if (!alreadyTerminal) _current = process;
            }

            if (alreadyTerminal) {
                // The spawn itself succeeded (the prompt WAS delivered — agy -p's argument list IS the
                // write), so the ack reflects that; the runtime going terminal is what stops this turn
                // from being tracked/continued, not a failure to deliver.
                turn.Written?.TrySetResult();
                try {
                    await process.TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Antigravity: failed to terminate a turn orphaned by a concurrent Terminate.");
                }
                return;
            }

            turn.Written?.TrySetResult();

            var accumulator         = new AntigravityStepAccumulator();
            var sawTerminalResult   = false;
            var conversationChanged = false;
            string? resultStatus    = null;

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

            // Rule (e), second call site: this turn settled cleanly (→ Idle, no EnterTerminal call at
            // all) WITHOUT ever resolving a conversation id — e.g. a transcript whose `init` line was
            // missing or carried an empty conversation_id, yet still reached a terminal `result`. A
            // no-op once an id already resolved (from this turn's own init, or an earlier turn's).
            // Without this, a factory correctly following rule (e) would hang forever awaiting
            // WaitForConversationIdAsync on a runtime that is otherwise perfectly healthy.
            FaultConversationIdBarrierIfUnresolved(
                "Antigravity: this turn ended without the runtime ever resolving a conversation id from an `init` event.");
        } finally {
            // Every exit path above — clean success, conversation-id mismatch, deadline, EOF-without-
            // result, the already-Terminal race, even ownerCt cancellation — lands here. A turn's
            // process is never reused, so it is always disposed exactly once, right here, regardless of
            // how the turn ended.

            // Asked BEFORE that disposal, which is the only point this is a truthful question: a
            // disposed process object reports HasExited true, so the same read afterwards would
            // manufacture a confirmation. DisposeAsync's cleanup gate is the consumer.
            _turnExitConfirmed = process.HasExited;

            // Cleared on CONFIRMED exit only, off that same single read: an unconfirmed survivor keeps
            // its record, because the record is the only thing that can still reap it. Never allowed
            // to fault the turn — a record we failed to delete is stale bookkeeping, not a live child.
            if (_turnExitConfirmed && PidCallbacks is { } recorded) {
                try {
                    recorded.Clear();
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Antigravity: failed to clear a completed turn's PID record.");
                }
            }

            try {
                await process.DisposeAsync().ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Antigravity: failed to dispose a turn's process.");
            }
        }
    }

    /// <summary>
    /// Spawns ONE turn's process (bounded by <see cref="_launchDeadline"/> for turn 1 only). Returns
    /// <see langword="null"/> on any failure — spawn/auth failure, the launch deadline, or an owner
    /// cancel racing the spawn — having already reported it through <paramref name="turn"/>'s ack and,
    /// where it means the runtime cannot continue, <see cref="EnterTerminal"/>. Extracted from
    /// <see cref="ProcessTurnAsync"/> so the caller's <c>try/finally</c> only ever has to dispose a
    /// process that actually exists.
    /// </summary>
    async Task<IAgyTurnProcess?> SpawnTurnProcessAsync(PendingTurn turn, bool firstTurn, CancellationToken ownerCt) {
        using var spawnCts = CancellationTokenSource.CreateLinkedTokenSource(ownerCt);
        if (firstTurn && _launchDeadline is { } launchDeadline)
            spawnCts.CancelAfter(launchDeadline);

        try {
            return await _spawnTurn(turn.Text, _conversationId, spawnCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ownerCt.IsCancellationRequested) {
            // Stop/dispose raced the spawn — EnterTerminal was already called by TerminateAsync.
            turn.Written?.TrySetException(new InvalidOperationException(
                "Antigravity runtime stopped before this turn's process spawned."));
            return null;
        } catch (OperationCanceledException) {
            // Only the launch deadline could cancel spawnCts without ownerCt also being cancelled.
            turn.Written?.TrySetException(new TimeoutException(
                "Antigravity: turn 1's process did not spawn within the launch deadline."));
            _logger.LogWarning("Antigravity: turn 1 spawn exceeded the launch deadline; entering Terminal.");
            EnterTerminal(exitCode: 1);
            return null;
        } catch (Exception ex) {
            // Spawn failure or auth failure, surfaced by the injected spawner as a thrown exception.
            turn.Written?.TrySetException(ex);
            _logger.LogWarning(ex, "Antigravity: turn process failed to spawn; entering Terminal.");
            EnterTerminal(exitCode: 1);
            return null;
        }
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

                // Rule (e): the ONE place this ever completes successfully — unblocks any factory
                // awaiting WaitForConversationIdAsync before it reads AcpSessionId.
                _conversationIdResolved.TrySetResult();

                // The handshake's second and last stamp — this is the moment the runtime has a
                // conversation, the exec-per-turn analogue of an ACP `session/new` returning. Guarded
                // by the same "id was null" branch, so it can fire at most once per runtime.
                ActivityClock?.SetLaunchStage("session_created");
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
        // Advance BEFORE the channel write, never after: a reader blocked on Envelopes.ReadAsync can
        // wake the instant TryWrite makes the item visible, on another thread, with no ordering
        // relationship to what this one does next — so the reverse order is a race a fast reader wins,
        // observing an envelope whose activity the clock has not yet recorded. Same reasoning
        // AcpHostedAgentRuntime.EmitEnvelope documents. Advanced even on the dropped-because-completed
        // path below: the content was genuinely produced.
        ActivityClock?.Advance();

        if (!_transcript.Writer.TryWrite(env))
            _logger.LogDebug("Antigravity: dropped a transcript envelope — the transcript channel is already completed.");
    }

    /// <summary>
    /// Enters <see cref="RuntimePhase.Terminal"/>, idempotently: the phase check-and-flip happens under
    /// <see cref="_stateLock"/> (reentrant-safe — <see cref="TerminateAsync"/> calls this from inside its
    /// own already-held <see cref="_stateLock"/> section, which C#'s <see langword="lock"/> permits on the
    /// same thread), and every other effect — completing <see cref="_terminalTcs"/>, faulting
    /// <see cref="_conversationIdResolved"/> (rule (e); a no-op if it already resolved successfully),
    /// draining <see cref="_pendingTurns"/>, completing <see cref="_transcript"/> — runs exactly once,
    /// only for the caller that actually performed the transition.
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

        // Terminal is absorbing and no turn can run past it, so the reaper must never see a held turn
        // from here on. The turn worker's own finally clears this too — but it does so only once the
        // in-flight turn actually unwinds, which is AFTER TerminateAsync returns to its caller (and
        // never at all, if a turn's process ignores cancellation). Clearing here is what makes "the
        // runtime is stopped" and "the reviewer holds no turn" the same instant.
        ActivityClock?.SetTurnInFlight(false);

        // Rule (e): unblock a factory parked in WaitForConversationIdAsync when a conversation id is
        // never going to arrive (e.g. turn 1's spawn itself failed) — never hang that caller forever.
        FaultConversationIdBarrierIfUnresolved(
            "Antigravity runtime went terminal before establishing a conversation id.");

        _pendingTurns.Writer.TryComplete();
        while (_pendingTurns.Reader.TryRead(out var dropped))
            dropped.Written?.TrySetException(new InvalidOperationException(
                "Antigravity runtime is terminal; this input was never delivered."));

        _transcript.Writer.TryComplete();

        return true;
    }

    /// <summary>
    /// Faults <see cref="_conversationIdResolved"/> if — and only if — nothing has resolved it yet
    /// (<c>TrySetException</c> is a no-op against an already-successfully-completed task, so this can
    /// never clobber a real id that was already established). Two call sites use this, not one:
    /// <see cref="EnterTerminal"/> covers "the runtime went terminal before an id ever arrived", and
    /// the clean-success tail of <see cref="ProcessTurnAsync"/> covers the MORE subtle case rule (e)'s
    /// first cut missed — a turn can settle to <see cref="RuntimePhase.Idle"/> (no <see cref="EnterTerminal"/>
    /// call at all) having never seen an <c>init</c> event with a non-empty <c>conversation_id</c> (a
    /// malformed/missing <c>init</c> whose transcript nonetheless carries a terminal <c>result</c>) —
    /// without this second call site, a caller correctly following rule (e) and awaiting
    /// <see cref="WaitForConversationIdAsync"/> would hang forever on a HEALTHY (non-terminal) runtime.
    ///
    /// <para>Immediately "observes" a fault it actually caused via a synchronous, exception-swallowing
    /// continuation — otherwise an unobserved faulted <see cref="Task"/> (e.g. a caller that never calls
    /// <see cref="WaitForConversationIdAsync"/> at all, which every non-factory caller of this runtime
    /// legitimately never does) risks <see cref="TaskScheduler.UnobservedTaskException"/> once the GC
    /// finalizes it — benign under default .NET, but noisy or fatal under a host that escalates it. This
    /// is purely about silencing that observability warning; a REAL caller of
    /// <see cref="WaitForConversationIdAsync"/> still observes and can act on the actual exception via
    /// its own <see langword="await"/>.</para>
    /// </summary>
    void FaultConversationIdBarrierIfUnresolved(string message) {
        if (!_conversationIdResolved.TrySetException(new InvalidOperationException(message)))
            return;

        _conversationIdResolved.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

        // Whether the worker actually JOINED, not merely that we waited for it. Every bound on this
        // path is best-effort and swallowed, so a WaitAsync that timed out is otherwise
        // indistinguishable from one that succeeded — and the cleanup gate below must not read the
        // two the same way.
        var workerJoined = true;

        try {
            await _turnWorkerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } catch (Exception ex) {
            // Best-effort — a stuck turn worker must never hang dispose. It is NOT evidence of
            // quiescence, which is what `workerJoined` records.
            workerJoined = false;
            _logger.LogDebug(
                ex, "Antigravity: the turn worker did not join within the dispose budget (agentId={AgentId}).",
                _agentId);
        }

        // Asked BEFORE the handle is disposed, for the same reason ProcessTurnAsync's own capture is:
        // a disposed process object reports HasExited true, so asking afterwards mistakes "no longer
        // observable" for "confirmed exited" — the opposite of what the gate below is for.
        var inFlightConfirmed = _current is not { } inFlight || inFlight.HasExited;

        if (_current is { } current) {
            try {
                await current.DisposeAsync().ConfigureAwait(false);
            } catch {
                // Best-effort.
            }
        }

        _ownerCts.Dispose();
        _turnGate.Dispose();

        if (_onDisposed is null) return;

        // LAST, and gated on MEASURED quiescence rather than an assumption of it. The callback removes
        // the per-launch HOME, which holds the reviewer's own conversation JSONL — the caller's diff,
        // source excerpts and findings. Kill(entireProcessTree: true) is not atomic against a
        // grandchild forked between tree enumeration and signal, and agy's children include its MCP
        // stdio servers, so a survivor past these budgets is a real shape rather than a hypothetical.
        //
        // Unconfirmed means SKIP the deletion, not force it: deleting under a live reviewer would
        // leave it writing into an unlinked path and recreating the directory, which is worse than
        // leaving it. The epoch-keyed startup sweep collects it on the next boot. Never silent — a
        // retained transcript-bearing home is exactly what an operator has to be able to find.
        if (!workerJoined || !inFlightConfirmed || !_turnExitConfirmed) {
            _logger.LogWarning(
                "Antigravity: could not confirm this reviewer's turn children exited (agentId={AgentId}, "
              + "workerJoined={WorkerJoined}, inFlightChildExited={InFlightConfirmed}, "
              + "lastTurnExitConfirmed={TurnExitConfirmed}); leaving its reviewer home for the startup "
              + "sweep rather than deleting it under a live process.",
                _agentId, workerJoined, inFlightConfirmed, _turnExitConfirmed);

            return;
        }

        try {
            _onDisposed();
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Antigravity: the runtime's disposal callback failed (agentId={AgentId}).", _agentId);
        }
    }
}
