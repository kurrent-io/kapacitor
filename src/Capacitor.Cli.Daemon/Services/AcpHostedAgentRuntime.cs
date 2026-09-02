using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Kiro;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> that drives an ACP (Agent Client Protocol) session over
/// <see cref="AcpConnection"/> for any ACP-speaking vendor (Cursor today, more via
/// <c>AcpVendorDescriptor</c>). Owns the <c>initialize</c> →
/// <c>session/new</c> → <c>session/prompt</c> handshake and reduces inbound <c>session/update</c>
/// notifications to <see cref="AcpSessionUpdate"/> DTOs, surfaced via <see cref="Updates"/> for
/// the mapper to turn into canonical events. Scope stops there — no canonical events, no
/// permission bridge (<c>OnServerRequest</c> stays unset, so the connection's default-decline
/// posture answers any inbound server request with a method-not-found error; a follow-up wires
/// the real bridge). Local-attach (raw byte input) and terminal output are PTY-only surfaces the
/// ACP runtime does not support until a follow-up adds a terminal capability.
///
/// Also owns a serialized, single-flight prompt-turn worker (<see cref="RunTurnWorkerAsync"/>/
/// <see cref="ProcessAdmittedTurnAsync"/>) and a chunk aggregator (<see cref="AggregateUpdate"/>) that
/// together turn the raw <c>session/update</c> stream into an ORDERED, per-turn-aggregated
/// <see cref="AcpEventEnvelope"/> transcript, exposed via <see cref="IAcpTranscriptSource"/> for the
/// orchestrator to bind and forward. Prompt turns (the initial launch prompt, and every
/// <see cref="SendUserInputAsync"/>) are enqueued onto a FIFO
/// queue rather than fired as independent background work, so exactly one turn's chunks are ever
/// aggregating at a time — a `stopReason` always flushes the buffer belonging to ITS OWN turn, never
/// a concurrently-fired one. See <see cref="_aggregationLock"/>'s remarks for the thread-safety
/// mechanism between the worker's turn-end flush and the connection read-loop's kind-transition
/// flush.
///
/// For an eligible launch (constructed with an <see cref="AcpReconnectSupport"/>) the runtime also
/// owns crash reconnect/resume: a child-process death is absorbed rather than finalized — the
/// corpse is retired, a fresh child is spawned through the factory's pure spawn closure,
/// <c>session/load</c> restores the same session with its entire replay suppressed (the daemon-side
/// transcript is authoritative — skip-whole-replay), and the send gate reopens after a transcript
/// <c>system_note</c>. The full protocol — incarnation identity, the two-transition send-admission
/// section, corpse retirement, the activation latch, incident chaining, and the interaction
/// router — is the design spec at
/// <c>docs/superpowers/specs/2026-08-04-ai1325-acp-reconnect-resume-design.md</c>; the region
/// comments below reference its section numbers.
/// </summary>
internal sealed partial class AcpHostedAgentRuntime : IHostedAgentRuntime, IAcpTranscriptSource {
    static readonly AcpMcpServerSpec[] NoMcpServers = [];

    /// <summary>
    /// One spawned connection/process pair — the original launch or a reconnect candidate — with
    /// its unique, never-reused incarnation id (reconnect spec §5.1). The runtime serves exactly
    /// one INSTALLED incarnation at a time (<see cref="_installed"/>); crash signals are stamped
    /// with their source incarnation's id and are live only while that id is installed, which is
    /// what makes a disposed candidate's delayed callback structurally inert.
    /// </summary>
    sealed class Incarnation {
        public required long                    Id         { get; init; }
        public required AcpConnection           Connection { get; init; }
        public required IAcpProcess             Process    { get; init; }
        public required CancellationTokenSource LoopCts    { get; init; }
        public Task LoopTask { get; set; } = Task.CompletedTask;
    }

    /// <summary>Runtime lifecycle phase, mutated only under <see cref="_reconnectLock"/>.</summary>
    enum RuntimePhase { Running, Reconnecting, Terminal }

    /// <summary>The §5.3 in-flight registration: the one turn past pre-gate admission, its
    /// write-state advanced by the write path (`not-started` → `entered` → `written`).</summary>
    sealed class InFlightTurn(PendingTurn turn, long incarnationId) {
        public const int NotStarted = 0, Entered = 1, Written = 2;

        public PendingTurn Turn          { get; } = turn;
        public long        IncarnationId { get; } = incarnationId;
        public int         WriteState;
    }

    /// <summary>A registered pending interaction (reconnect spec §5.4): the sweep's bookkeeping
    /// signal (<see cref="CancelledSignal"/>, completed BEFORE the token — no foreign code) is what
    /// the router races against the bridge, so a blocked cancellation callback can never strand the
    /// response or the entry's removal; <see cref="Cts"/> is signalled last, as the best-effort
    /// tail that runs foreign callbacks.</summary>
    // CA1001: disposing Cts on removal severs the sweep's tail (see RouteServerRequestAsync's
    // finally); disposing in the sweep races that method's read of Cts.Token.
#pragma warning disable CA1001
    sealed class PendingInteraction(long incarnationId) {
        public long IncarnationId { get; } = incarnationId;
        public bool Cancelled; // mutated under the reconnect lock only
        public readonly CancellationTokenSource    Cts             = new();
        public readonly TaskCompletionSource       CancelledSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
#pragma warning restore CA1001

    readonly object _reconnectLock = new();
    readonly AcpReconnectSupport? _reconnect;
    readonly List<PendingInteraction> _pendingInteractions = [];

    Incarnation  _installed;
    int          _phase = (int)RuntimePhase.Running;

    /// <summary>The runtime phase with acquire semantics — the backing field is written only under
    /// <see cref="_reconnectLock"/>, but several readers (`HasExited`/`ExitCode`, the graceful-stop
    /// catch filter) run lock-free, and a plain enum field gives them no visibility guarantee
    /// (Qodo review #2). Backed by an int because <c>volatile</c>/<see cref="Volatile"/>
    /// don't apply to enum fields. Reads under the lock use this too — uniform and harmless.</summary>
    RuntimePhase Phase => (RuntimePhase)Volatile.Read(ref _phase);
    volatile bool _suppressNotifications;
    bool         _intentionalStop;
    long         _nextIncarnationId;
    long         _lastHandledCrashIncarnation = -1;
    long         _crashedAgainIncarnation     = -1;
    int          _suppressedUpdates;
    int          _resumeCount;
    bool         _incidentResendSentence;
    InFlightTurn? _inFlight;
    PendingTurn?  _heldTurn;
    bool          _heldTurnSkipEnvelope;
    CancellationTokenSource? _ownerCts;
    Task          _ownerTask = Task.CompletedTask;

    /// <summary>Completed when the send gate is OPEN (phase Running). Entering reconnect swaps in a
    /// fresh, uncompleted instance; the atomic reopen (or the terminal path) completes it.</summary>
    TaskCompletionSource _gateOpen = CompletedGate();

    /// <summary>Completed when the runtime is logically terminal — the re-keyed signal
    /// <see cref="ReadOutputAsync"/> waits on instead of "this process exited" (spec §5.4), so the
    /// orchestrator's finalize trigger never fires for a crash reconnect will absorb.</summary>
    readonly TaskCompletionSource _runtimeTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    static TaskCompletionSource CompletedGate() {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }

    /// <summary>Liveness-supervision spec §0/§1: the per-agent activity clock, assigned by
    /// <see cref="AgentOrchestrator"/> right after this runtime is obtained — before any envelope or
    /// turn activity can occur, and before the owning <c>AgentInstance</c> even exists. Null for every
    /// construction that bypasses that launch path (unit tests, the resume-candidate constructor
    /// paths that build a runtime directly) — every call site below is a no-op guard, never a throw,
    /// so a test that doesn't care about liveness keeps passing unchanged.</summary>
    internal AgentActivityClock? ActivityClock { get; set; }

    /// <summary>
    /// Liveness-supervision spec (Task 13): the fixed per-stage cap for the ACP launch handshake
    /// (<c>initialize</c> → <c>session/new</c> → model selection). A deliberate FIXED wall-clock
    /// bound, not an evidence-based one — the design's one considered exception to that thesis: each
    /// stage is a single RPC with empirically sub-second-to-seconds latency, there is no
    /// finer-grained progress signal inside one, and 90s is roughly an order of magnitude above
    /// observed worst cases. Before this, <c>StartAsync</c> had NO launch-level timeout at all — a
    /// wedged handshake hung forever, invisible to both the startup reaper (never reaches
    /// <c>PublishAgent</c>) and the reviewer reaper (never reaches "Running").
    /// </summary>
    internal static readonly TimeSpan AcpLaunchStageTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Thrown when a single handshake stage's <see cref="AcpLaunchStageTimeout"/> expires. Rethrown
    /// VERBATIM by <see cref="StartAsync"/>'s outer catch (exactly like <see cref="AcpProtocolVersionException"/>)
    /// so the coded <c>acp_launch_stage_timeout:{stage}</c> reason reaches the factory/orchestrator's
    /// LaunchFailed path undecorated by the generic auth-hint wrapper — an operator (or the server's
    /// failure classification) needs the exact stage name.
    ///
    /// <para>The message describes what was ATTEMPTED, not what is assumed to have happened:
    /// termination is best-effort in <see cref="RunHandshakeStageAsync"/> and can fail, so claiming
    /// the child "was terminated" would misdirect an incident responder away from an orphan that is
    /// still running.</para>
    /// </summary>
    internal sealed class AcpLaunchStageTimeoutException(string stage, TimeSpan cap) : InvalidOperationException(
        $"acp_launch_stage_timeout:{stage}: the ACP handshake did not reach '{stage}' within "
      + $"{cap.TotalSeconds:0}s. Termination of the child process was requested (best-effort — a "
      + "failure to terminate is logged, so the process may still be running).") {
        public string Stage { get; } = stage;
    }

    readonly ILogger       _logger;
    readonly TimeProvider  _timeProvider;
    readonly string        _agentId;
    readonly bool          _debugFrames;
    readonly string        _vendor;
    readonly IAcpModelSelector _modelSelector;
    readonly AcpInteractionBridge? _interactionBridge;
    readonly CancellationTokenSource _cts = new();
    // Raw reduced-update surface, used only for test/live-inspection — the production transcript
    // pipeline reads Envelopes, not this. Bounded + DropOldest so a long session with no reader
    // can't grow it without bound (the transcript channel is bounded for the same reason).
    readonly Channel<AcpSessionUpdate> _updates = Channel.CreateBounded<AcpSessionUpdate>(
        new BoundedChannelOptions(2000) { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Default cap for <see cref="_transcript"/> — generous relative to a realistic session's
    /// envelope volume (aggregation already collapses chunk runs into one envelope each; even a long
    /// turn with dozens of tool calls stays well under this), so it only ever bites during a
    /// genuinely stalled/outaged forwarder. Overridable via the constructor so tests can exercise the
    /// drop path with a small cap.
    /// </summary>
    const int DefaultTranscriptCapacity = 2000;

    /// <summary>
    /// Default cap for <see cref="_pendingTurns"/> — user inputs are low-volume (a human typing
    /// follow-ups), so a modest cap is enough to absorb a burst without ever realistically filling up
    /// in normal use.
    /// </summary>
    const int DefaultPendingTurnsCapacity = 50;

    readonly int _transcriptCapacity;
    readonly int _pendingTurnsCapacity;
    int          _droppedTranscriptEnvelopes;
    int          _droppedPendingTurns;

    /// <summary>
    /// The ordered, aggregated <see cref="AcpEventEnvelope"/> transcript — every write goes through
    /// <see cref="EmitEnvelope"/>, which always holds <see cref="_aggregationLock"/>, so this
    /// channel's FIFO write order matches lock-acquisition order across the two call sites that can
    /// write to it (the turn worker, and the connection's notification handler). SingleReader: the
    /// forwarder is the only intended consumer.
    ///
    /// Bounded (see <see cref="DefaultTranscriptCapacity"/>) with
    /// <see cref="BoundedChannelFullMode.DropOldest"/> — NEVER <see cref="BoundedChannelFullMode.Wait"/>.
    /// The sole writer (<see cref="EmitEnvelope"/>) always uses the non-blocking <c>TryWrite</c>, which
    /// never blocks regardless of <see cref="BoundedChannelFullMode"/> — but <c>EmitEnvelope</c> runs
    /// SYNCHRONOUSLY on the ACP connection's own read loop (via <see cref="HandleNotification"/>), so
    /// a future change toward an awaited <c>WriteAsync</c> under <c>Wait</c> would stall that read
    /// loop, not just this session's transcript, if the forwarder (the reader) is itself stalled —
    /// <c>DropOldest</c> forecloses that class of bug entirely: a stalled forwarder degrades to
    /// "lost some trailing transcript", never a blocked connection or unbounded memory growth.
    /// </summary>
    readonly Channel<AcpEventEnvelope> _transcript;

    /// <summary>
    /// FIFO queue of pending prompt-turn texts. Public entry points (<see cref="StartAsync"/>'s
    /// initial prompt, <see cref="SendUserInputAsync"/>) call <see cref="EnqueueTurn"/> and return
    /// immediately; the single <see cref="RunTurnWorkerAsync"/> worker task drains this strictly in
    /// order, one turn fully at a time (see <see cref="ProcessAdmittedTurnAsync"/>). SingleReader: only the
    /// worker reads; SingleWriter=false since both StartAsync and (potentially concurrent)
    /// SendUserInputAsync calls enqueue.
    ///
    /// Bounded (see <see cref="DefaultPendingTurnsCapacity"/>) with
    /// <see cref="BoundedChannelFullMode.DropWrite"/> — a burst of input while the worker is
    /// stuck on a stalled turn drops the NEW input rather than evicting an earlier, still-pending one
    /// (either is a pathological case; this queue realistically never gets anywhere near the cap).
    /// </summary>
    readonly Channel<PendingTurn> _pendingTurns;
    readonly SemaphoreSlim _turnExecutionGate = new(1, 1);

    /// <summary>
    /// Guards the open aggregation run (<see cref="_openRunKind"/>/<see cref="_openRunText"/>) AND
    /// every write to <see cref="_transcript"/> (via <see cref="EmitEnvelope"/>). Two call sites can
    /// mutate/flush the run: the connection's read loop (synchronously, via
    /// <see cref="HandleNotification"/> → <see cref="AggregateUpdate"/>, on a kind transition) and the
    /// turn worker (via <see cref="ProcessAdmittedTurnAsync"/>'s turn-end flush, which runs as the
    /// continuation of an awaited <c>session/prompt</c> response and so is NOT guaranteed to run on
    /// the read-loop's own thread — <see cref="AcpConnection.RequestAsync"/>'s
    /// <c>TaskCompletionSource</c> is created with <c>RunContinuationsAsynchronously</c> specifically
    /// so completing it never runs the awaiter's continuation inline). Because turns are serialized,
    /// these two call sites are never ACTUALLY contending in practice — the worker only sends turn
    /// N+1's <c>session/prompt</c> (and so only that turn's updates can start arriving) after turn
    /// N's flush has already completed — but a plain <c>lock</c> (reentrant on the same thread, so
    /// <see cref="FlushOpenRunLocked"/> calling back into <see cref="EmitEnvelope"/> cannot
    /// self-deadlock) is a cheap, simple guarantee against that invariant ever silently breaking (a
    /// future change to the worker, an agent that violates the one-turn-at-a-time assumption, etc.)
    /// rather than relying solely on the timing argument. A single loop reading both the update
    /// stream and the turn-boundary signal was the other option considered — rejected because it
    /// would require plumbing the connection's notification callback through a channel too, adding a
    /// second unbounded channel + consumer loop for no additional safety over a lock given the
    /// happens-before analysis above.
    /// </summary>
    readonly object _aggregationLock = new();

    AcpUpdateKind?  _openRunKind;
    StringBuilder?  _openRunText;

    Task    _turnWorkerTask    = Task.CompletedTask;
    string? _sessionId;
    string? _cwd;
    string? _resolvedModel;
    string? _requestedModel;
    AcpMcpServerSpec[] _mcpServersForResume = NoMcpServers;
    int     _disposed;

    /// <summary>Present only for an unattended Kiro review launch. Judges every MCP-surface
    /// notification on arrival; a violation reaps the reviewer through the same path a forbidden
    /// interaction frame does, because both mean the same thing — the containment contract this
    /// launch was admitted under no longer holds.</summary>
    readonly KiroMcpSurfaceMonitor? _mcpSurfaceMonitor;

    /// <summary>Runs once the child is gone. Carries the Kiro reviewer's isolated-home deletion: the
    /// home is transcript-bearing, and the FACTORY can only clean up launches that FAILED — a
    /// successful review's home would otherwise sit on disk until a later daemon epoch swept it.</summary>
    readonly Action? _onDisposed;

    /// <summary>
    /// The in-flight out-of-band reap, if one was started. Awaited by disposal so cleanup never runs
    /// ahead of the termination it depends on.
    ///
    /// <para>Guarded by <see cref="_reapLock"/> rather than <c>volatile</c>: volatility publishes the
    /// reference but cannot make "flip the single-shot guard" and "publish the task" atomic, so a
    /// disposal landing between them would read null and skip the wait. Both reap paths — the
    /// tripwire/watchdog violation and the forbidden-interaction frame — publish through it.</para>
    /// </summary>
    Task? _reapTask;
    readonly object _reapLock = new();

    /// <summary>The reap claim's fused, immutable outcome: the writer's coded reason (sanitized —
    /// see <see cref="TryStartReap"/>) plus whether the claim landed while the launch window
    /// (<see cref="_firstTurnSettled"/>) was still open. Published at most once, by whichever reap
    /// wins the claim.</summary>
    public sealed record TerminationVerdict(string Reason, bool ReapedInsideLaunchWindow);

    /// <summary>Null until a reap wins the claim (and forever null if none ever does, or the sole
    /// claim's starter throws). Read by the orchestrator to reclassify a launch/registration failure
    /// with the daemon's coded reason instead of surfacing as an unknown failure.
    ///
    /// <para>The plain auto-property is safe only for a reader that CANNOT race a concurrent claim —
    /// the factory's launch path, which reads it strictly after an ordered
    /// <see cref="DisposeAsync"/> that already awaited the reap. The orchestrator's finalizer CAN race
    /// the claim (the reap's own <c>_cts.Cancel()</c> drives finalization on another thread while the
    /// claimant still holds <see cref="_reapLock"/> and this is still null), so it must read through
    /// <see cref="ReadVerdict"/> instead.</para></summary>
    public TerminationVerdict? Verdict { get; private set; }

    /// <summary>
    /// The verdict, observed UNDER <see cref="_reapLock"/> — so a reader that can race a concurrent
    /// claim blocks until the claimant has committed the publish (or observes the absence of any
    /// claim), never the transient window where the slot is claimed but <see cref="Verdict"/> is not
    /// yet assigned (finding 1). <see cref="TryStartReap"/> publishes the verdict at the tail of the
    /// SAME critical section that claims the slot and runs the (connection-cancelling) starter, so a
    /// finalizer reading the plain auto-property mid-claim would see null and skip
    /// <c>LaunchFailed</c> permanently.
    ///
    /// <para><b>No deadlock:</b> the starter returns the reap Task WITHOUT awaiting termination (it
    /// runs only synchronous log + <c>_cts.Cancel()</c> work before the first await inside
    /// <see cref="ReapUnexpectedInteractionAsync"/>), so the claimant releases <see cref="_reapLock"/>
    /// promptly and never itself waits on any finalizer — the lock is only ever held for that bounded
    /// synchronous burst.</para>
    /// </summary>
    /// <summary>Test-only: fires at the TOP of <see cref="ReadVerdict"/>, before the lock — lets the
    /// finding-1 barrier detect, deterministically, that the finalizer took the SYNCHRONISED read path
    /// and is about to block on <see cref="_reapLock"/> (vs. the regressed plain-<see cref="Verdict"/>
    /// read, which never calls this). That removes any wall-clock hold from the barrier. Null in
    /// production (a single null check per read).</summary>
    internal Action? BeforeReadVerdictLockForTest;

    internal TerminationVerdict? ReadVerdict() {
        // GUARDED: the hook is a fire-and-forget TEST signal (null in production). ReadVerdict is a
        // load-bearing PRODUCTION read — the finalizer calls it to decide whether to send LaunchFailed
        // for a launch-window reap — so a throwing hook (test contamination / a stray assignment) must
        // never make it throw and skip the verdict observation. It still FIRES before the lock (the
        // barrier's happy-path hook never throws), it just can't break the read.
        try { BeforeReadVerdictLockForTest?.Invoke(); }
        catch (Exception ex) { _logger.LogDebug(ex, "ACP: BeforeReadVerdictLockForTest hook threw; continuing to read the verdict."); }

        lock (_reapLock) return Verdict;
    }

    /// <summary>Test-only: invoked inside <see cref="TryInitiateNonFailureStatusSend"/> BETWEEN the
    /// verdict check and the send initiation, while <see cref="_reapLock"/> is held — so a test can
    /// prove a concurrent verdict publication cannot interleave there. Null in production.</summary>
    internal Action? BeforeGatedSendHookForTest;

    /// <summary>
    /// ATOMICALLY, under <see cref="_reapLock"/> (the gate <see cref="TryStartReap"/> publishes the
    /// verdict under): checks for a published launch-window verdict AND, if none, INITIATES
    /// <paramref name="send"/> (finding-2 refinement). Verdict publication therefore cannot interleave
    /// between the check and the send's initiation — either publication wins the lock first (this
    /// returns <see langword="false"/>, nothing is sent), or <paramref name="send"/> is invoked before
    /// the lock is released.
    ///
    /// <para><b>Ordering.</b> <paramref name="send"/> is <c>ServerConnection.AgentStatusChangedAsync</c>
    /// → <c>HubConnection.InvokeAsync</c>, whose synchronous prefix enters the SignalR connection
    /// send-semaphore (FIFO) before its first await — so the non-failure frame is queued while this
    /// lock is still held, strictly before any <c>LaunchFailed</c> the finalizer can only send AFTER
    /// publication (which needs this lock). On the single ordered hub connection the server processes
    /// the non-failure status BEFORE the LaunchFailed and ends Failed+reason — never a non-failure
    /// clear. <paramref name="sendTask"/> is the in-flight send so the caller can await COMPLETION
    /// (the server round-trip) OUTSIDE the lock; the lock is only held across INITIATION.</para>
    /// </summary>
    internal bool TryInitiateNonFailureStatusSend(Func<Task> send, out Task sendTask) {
        lock (_reapLock) {
            if (Verdict is { ReapedInsideLaunchWindow: true }) {
                sendTask = Task.CompletedTask;
                return false;
            }

            BeforeGatedSendHookForTest?.Invoke();
            sendTask = send();
            return true;
        }
    }

    /// <summary>
    /// Claims the single reap slot and, on success, publishes the fused <see cref="TerminationVerdict"/>.
    /// The claim, the launch-window snapshot, and the publication all happen under ONE lock, and
    /// <see cref="TakeReap"/> waits on that same lock — so a disposal can no longer land in the gap
    /// between "the guard flipped" and "the task/verdict exist" and conclude there is nothing to
    /// await or classify. An interlocked flag alone cannot express that, because these are not one
    /// operation.
    ///
    /// <para><paramref name="reason"/> is the writer's raw coded string — sanitized here (single-line
    /// + length-capped via <see cref="SanitizeForForward"/>) before publication, since it can carry
    /// agent-controlled text (e.g. a JSON-RPC method from an inbound frame). The window bit is
    /// snapshotted from <see cref="_firstTurnSettled"/> BEFORE <paramref name="start"/> runs: the
    /// reap's own <c>_cts.Cancel()</c> settles that marker in <see cref="ProcessAdmittedTurnAsync"/>'s
    /// <c>finally</c>, so reading it after <paramref name="start"/> has run would be
    /// self-contaminating — every claimed reap would read "settled" regardless of when it actually
    /// fired.</para>
    /// </summary>
    internal bool TryStartReap(string reason, Func<Task> start) {
        lock (_reapLock) {
            if (_reapClaimed) return false;

            _reapClaimed = true;

            var insideLaunchWindow = !_firstTurnSettled.Task.IsCompleted;

            // Started AND published inside the lock: claiming, releasing, then publishing leaves the
            // gap this exists to close — a disposal taking the lock in between sees a claim with no
            // task and concludes there is nothing to await.
            //
            // The callback runs SYNCHRONOUS work first (logging, _cts.Cancel()), so it can throw —
            // notably when a dispatched notification races disposal past _cts.Dispose(). Unwinding
            // out of here would release the lock with the slot claimed and no task published, which
            // is precisely the prohibited state. So the claim is released on failure — and the
            // verdict below is never reached, so nothing is published for this claim.
            try {
                _reapTask = start();
            } catch {
                _reapClaimed = false;
                throw;
            }

            Verdict = new TerminationVerdict(SanitizeForForward(reason), insideLaunchWindow);

            return true;
        }
    }

    /// <summary>The in-flight reap, or null when none was ever claimed. A claim with no task yet
    /// published cannot be observed: the claimant publishes before releasing the caller.</summary>
    internal Task? TakeReap() { lock (_reapLock) return _reapTask; }

    bool _reapClaimed;

    /// <summary>
    /// How long the FIRST turn may produce nothing at all before the child is reaped. Null disables
    /// it, which is every launch but an unattended Kiro review.
    ///
    /// <para><b>Why first OUTPUT and not turn completion.</b> The obvious reading of "bound the first
    /// prompt" is to time the turn — but a real review turn legitimately runs for minutes, which is
    /// exactly why <see cref="StartAsync"/> enqueues it without awaiting. Bounding completion would
    /// kill good reviews. The failure actually being defended against is a peer that is ALIVE and
    /// SILENT: a kiro-cli whose credential expired sits on an interactive browser prompt and emits
    /// nothing, ever. Time-to-first-update separates those two: once the model starts streaming, the
    /// turn may take as long as it likes.</para>
    /// </summary>
    readonly TimeSpan? _firstOutputDeadline;

    /// <summary>Whether this launch's transcript is review output rather than a conversation someone
    /// is watching. Decides who a silent turn is worth telling.</summary>
    readonly bool _isReviewFlow;

    /// <summary>The <c>@server/tool</c> identities this launch injected — the set
    /// <see cref="AcpUnattendedInteractionPolicy.AllowlistedAutoApprove"/> approves. Null for every
    /// other policy.</summary>
    readonly IReadOnlySet<string>? _admittedToolIds;

    int _sawFirstUpdate;

    /// <summary>Completes when the first turn ends, however it ends — the other way to disarm the
    /// first-output watchdog. Also the launch-window marker <see cref="TryStartReap"/> snapshots
    /// (design spec §3.3): the window is open from spawn until this settles. An empty/absent initial
    /// prompt settles it directly at <see cref="StartAsync"/>'s completion (see that method's tail)
    /// — a deterministic backstop, since no turn ever runs to settle it otherwise.</summary>
    readonly TaskCompletionSource _firstTurnSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Test-only: the SAME instance <see cref="TryStartReap"/> snapshots, so a test can
    /// settle it directly — simulating a reap's own cancel — without driving a real turn through the
    /// connection. Never touched by production code.</summary>
    internal TaskCompletionSource FirstTurnSettledForTest => _firstTurnSettled;

    /// <summary>Test-only: the runtime's shutdown token, so a test can register a THROWING
    /// cancellation callback and fault <see cref="DisposeAsync"/>'s early cancellation phase
    /// (<c>_cts.CancelAsync()</c>) — proving the guaranteed-teardown path still disposes the child
    /// (finding 2). Never touched by production code.</summary>
    internal CancellationToken RuntimeShutdownTokenForTest => _cts.Token;

    /// <summary>Test-only: invoked in <see cref="DisposeAsync"/> IMMEDIATELY before it takes
    /// <see cref="_reconnectLock"/> — the exact window a round-2 pre-lock incarnation read left open —
    /// so a test can commit a reconnect successor there and prove the guaranteed teardown disposes the
    /// SUCCESSOR (finding 2, round 3), not a stale predecessor. Null in production.</summary>
    internal Action? BeforeReconnectLockOnDisposeForTest;

    /// <summary>Test-only: the logical-terminal signal, so a test can assert it fired even when the
    /// early cancellation phase faulted (parked read/finalizer waiters must not be stranded — finding
    /// 2, round 3). Never touched by production code.</summary>
    internal Task RuntimeTerminalForTest => _runtimeTerminal.Task;

    /// <summary>Test-only: the turn-worker task, so a test can assert DisposeAsync always SIGNALS the
    /// worker to exit (channels completed / token cancelled) even when the early cancellation phase
    /// faults — otherwise it parks forever while teardown disposes its resources (Bug 1). Never touched
    /// by production code.</summary>
    internal Task TurnWorkerTaskForTest => _turnWorkerTask;

    /// <summary>Test-only: installs a throwing owner CTS so <see cref="DisposeAsync"/>'s owner-cancel
    /// (the EARLY cancellation callback) faults. Never touched by production code.</summary>
    internal void SetOwnerCtsForTest(CancellationTokenSource cts) { lock (_reconnectLock) _ownerCts = cts; }

    /// <summary>Test-only: commits a reconnect SUCCESSOR incarnation (swaps <see cref="_installed"/> to
    /// a fresh incarnation wrapping <paramref name="successorProcess"/>, reusing the current
    /// connection), simulating a reconnect that committed between a pre-lock read and the lock. The
    /// guaranteed teardown must dispose THIS successor, not the predecessor. Never touched by
    /// production code.</summary>
    internal void CommitSuccessorIncarnationForTest(IAcpProcess successorProcess) {
        lock (_reconnectLock) {
            _installed = new Incarnation {
                Id         = Interlocked.Increment(ref _nextIncarnationId),
                Connection = _installed.Connection,
                Process    = successorProcess,
                LoopCts    = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
            };
        }
    }

    /// <summary>Agent capabilities negotiated by <see cref="StartAsync"/>'s <c>initialize</c> call; null before that.</summary>
    AgentCapabilities? _negotiatedCapabilities;

    /// <summary>The <c>protocolVersion</c> negotiated by <see cref="StartAsync"/>'s <c>initialize</c> call — captured purely for the <see cref="LogHandshakeOk"/> lifecycle log; 0 before that.</summary>
    int _negotiatedProtocolVersion;

    /// <summary>
    /// A deliberate, already-actionable handshake error (unsupported ACP protocol version) — NOT an
    /// auth/connection failure. The handshake catch rethrows it unwrapped so the auth/subscription
    /// hint isn't misapplied to it. Derives from <see cref="InvalidOperationException"/> so callers
    /// that catch that still see it as one.
    /// </summary>
    sealed class AcpProtocolVersionException(string message) : InvalidOperationException(message);

    /// <summary>
    /// The initialize/session-new wrapper's failure, carrying the sanitized transport cause as a
    /// SEPARATE property from the composed, hint-decorated <see cref="Exception.Message"/> (design
    /// spec §3.2). <see cref="Exception.Message"/> is composed exactly as the plain
    /// <see cref="InvalidOperationException"/> this replaces always was — byte-identical for the
    /// no-verdict path — but <see cref="TransportMessage"/> lets a caller (the factory's
    /// launch-failure reclassification) quote just the transport cause, so a reclassified suffix can
    /// never smuggle the auth hint back in. <c>internal</c> (not the sibling exceptions' default
    /// private) because the factory, in a different class, needs to pattern-match on it.
    /// </summary>
    internal sealed class AcpHandshakeFailedException(string stage, string transportMessage, string hint, Exception inner)
        : InvalidOperationException(
            $"ACP handshake ({stage}) failed: {transportMessage} — if this is an auth/subscription issue, {hint}.",
            inner) {
        /// <summary>The sanitized transport-level failure cause alone — no auth hint appended.</summary>
        public string TransportMessage { get; } = transportMessage;
    }

    /// <summary>
    /// Makes an error message safe to fold into a forwarded launch-failure string: collapses line
    /// breaks to spaces and caps the length. The source can be an agent-controlled JSON-RPC
    /// <c>error.message</c> (via <see cref="AcpRpcException"/>), which could otherwise be arbitrarily
    /// long or multi-line and degrade logs/UI downstream. The full original exception is retained as
    /// the thrown exception's <c>InnerException</c>, so nothing is lost for daemon-side diagnostics.
    /// <c>internal</c> (not the class's usual private default) so
    /// <see cref="AcpHostedAgentRuntimeFactory"/>, a different class in the same namespace, has ONE
    /// sanitizer to call for its own transport-cause truncation rather than a byte-for-byte
    /// duplicate — the two must never drift, and this fix (never splitting a Unicode surrogate pair
    /// at the boundary) reaches every caller.
    /// </summary>
    internal static string SanitizeForForward(string message, int maxLength = 500) {
        var oneLine = message.ReplaceLineEndings(" ").Trim();
        if (oneLine.Length <= maxLength) return oneLine;

        // A raw code-unit slice at maxLength can land between a surrogate pair's two halves. Back
        // off one position when that would happen so the cut only ever falls on a whole character —
        // never grow past the cap to include the pair instead. cut > 0 guards the maxLength <= 0
        // edge (nothing to back off from; falls through to the pre-fix substring behavior).
        var cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(oneLine[cut - 1])) cut--;

        return oneLine[..cut] + "…";
    }

    /// <summary>
    /// <paramref name="requestInteraction"/> is optional — when null, matches the
    /// original behavior exactly: <see cref="AcpConnection.OnServerRequest"/> stays unset, and any
    /// <c>session/request_permission</c>/<c>elicitation/create</c> the agent sends gets the
    /// connection's default JSON-RPC "Method not found" response. When provided, it is forwarded
    /// into a new <see cref="AcpInteractionBridge"/> and wired as <see cref="AcpConnection.OnServerRequest"/>.
    ///
    /// <b>Qodo daemon-review Q2:</b> this wiring no longer closes over this runtime's
    /// <see cref="_sessionId"/> — <see cref="AcpInteractionBridge.HandleAsync"/> now sources the ACP
    /// session id solely from the inbound request's OWN params. The prior shape passed
    /// <c>_sessionId ?? ""</c>, which was correct ONLY because a permission/elicitation request can
    /// normally arrive no earlier than a <c>session/prompt</c> turn, by which point
    /// <see cref="StartAsync"/>'s <c>session/new</c> has already resolved <see cref="_sessionId"/> —
    /// but <see cref="AcpConnection"/>'s read loop is started (via <see cref="RunIncarnationLoopAsync"/>)
    /// BEFORE that handshake completes, so a server request arriving out of turn (a buggy or
    /// malicious agent) would have forwarded an <see cref="AcpInteractionRequest"/> with
    /// <c>AcpSessionId == ""</c>, silently breaking server-side correlation instead of failing loud
    /// or safe. Trusting the request's own params removes this whole class of bug and this runtime
    /// no longer needs to expose <see cref="_sessionId"/> to the bridge at all.
    /// </summary>
    public AcpHostedAgentRuntime(
            AcpConnection                                                                  connection,
            IAcpProcess                                                                    process,
            ILogger                                                                        logger,
            string                                                                         agentId = "",
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>?   requestInteraction = null,
            TimeProvider?                                                                  timeProvider = null,
            int?                                                                           transcriptCapacity = null,
            int?                                                                           pendingTurnsCapacity = null,
            bool                                                                           debugFrames = false,
            string                                                                         vendor = "cursor",
            IAcpModelSelector?                                                             modelSelector = null,
            AcpUnattendedInteractionPolicy                                                  unattendedInteractionPolicy = AcpUnattendedInteractionPolicy.Disabled,
            AcpReconnectSupport?                                                            reconnect = null,
            KiroMcpSurfaceMonitor?                                                          mcpSurfaceMonitor = null,
            Action?                                                                         onDisposed = null,
            TimeSpan?                                                                       firstOutputDeadline = null,
            bool                                                                            isReviewFlow = false,
            IReadOnlySet<string>?                                                           admittedToolIds = null,
            AcpLaunchPermissionPreset?                                                      acpPermissionPreset = null,
            Action<AcpAutoApprovalNotice>?                                                  notifyAutoApproval = null,
            PolicySnapshot?                                                                 policySnapshot = null,
            Action<PolicyDecisionEventV1>?                                                  notifyPolicyDecision = null
        ) {
        _admittedToolIds = admittedToolIds;
        _firstOutputDeadline = firstOutputDeadline;
        _isReviewFlow        = isReviewFlow;
        _mcpSurfaceMonitor = mcpSurfaceMonitor;
        _onDisposed        = onDisposed;
        _reconnect     = reconnect;
        _logger        = logger;
        _timeProvider  = timeProvider ?? TimeProvider.System;
        _agentId       = agentId;
        _debugFrames   = debugFrames;
        _vendor        = vendor;
        // LEGACY/COMPAT default (spec-review Finding 3a): null here means
        // ConfigOptionModelSelector.Instance, NOT NoOpModelSelector — every existing call site
        // that constructs this runtime directly (without a descriptor) keeps today's exact Cursor
        // model-selection behavior. The generalized path (AcpHostedAgentRuntimeFactory.StartAsync)
        // always passes descriptor.ModelSelector explicitly and never relies on this default.
        _modelSelector = modelSelector ?? ConfigOptionModelSelector.Instance;

        // Bounded, not unbounded — see the fields' own remarks for the FullMode rationale.
        // transcriptCapacity/pendingTurnsCapacity are production-null (defaults apply); tests
        // override them to exercise the drop path with a small cap instead of writing thousands of
        // envelopes.
        _transcriptCapacity  = transcriptCapacity ?? DefaultTranscriptCapacity;
        _pendingTurnsCapacity = pendingTurnsCapacity ?? DefaultPendingTurnsCapacity;

        _transcript = Channel.CreateBounded<AcpEventEnvelope>(
            new BoundedChannelOptions(_transcriptCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        _pendingTurns = Channel.CreateBounded<PendingTurn>(
            new BoundedChannelOptions(_pendingTurnsCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });

        if (requestInteraction is not null) {
            _interactionBridge = new AcpInteractionBridge(
                requestInteraction,
                agentId,
                logger,
                unattendedInteractionPolicy,
                HandleUnexpectedUnattendedInteraction,
                admittedToolIds,
                acpPermissionPreset,
                notifyAutoApproval,
                policySnapshot,
                // The vendor this runtime already speaks for — the policy vocabulary's vendor field
                // and the launch's vendor are the same fact, so they cannot be given two answers.
                vendor,
                notifyPolicyDecision);
        }

        // The original launch's incarnation. Every later candidate goes through the same wiring
        // (WireIncarnation), so hook stamping, notification routing, and the interaction router are
        // identical for the original child and a resume candidate. The id comes from the SAME
        // monotonic allocator candidates use (code-review r1: a hand-assigned literal here plus a
        // seeded counter is a reuse bug waiting for a refactor — one allocator, no drift).
        _installed = new Incarnation {
            Id         = Interlocked.Increment(ref _nextIncarnationId),
            Connection = connection,
            Process    = process,
            LoopCts    = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
        };
        WireIncarnation(_installed);

        // The process-exit watcher starts at CONSTRUCTION, not StartAsync: the re-keyed
        // ReadOutputAsync waits on the logical-terminal signal, and a child that exits before (or
        // without) StartAsync must still drive it — the pre-reconnect implementation awaited the
        // process directly and had this property implicitly.
        _ = WatchProcessExitAsync(_installed);
    }

    /// <summary>
    /// Wires one spawned incarnation exactly once, at spawn (reconnect spec §6.2 steps 2–4):
    /// notifications into the (suppression-aware) shared handler, the transport-ended pre-fault
    /// hook stamped with THIS incarnation's id — safe arbitrarily early because an uninstalled
    /// stamp is structurally inert (§5.1), and required arbitrarily early so a death at any
    /// instant after commit is reportable with no wiring gap — and the state-derived interaction
    /// router (never the bridge directly; the router declines while this incarnation is
    /// uninstalled or the runtime is not `Running`).
    /// </summary>
    void WireIncarnation(Incarnation incarnation) {
        incarnation.Connection.OnNotification += HandleNotification;
        incarnation.Connection.BeforeFaultingPending = () => HandleTransportEnded(incarnation.Id);

        if (_interactionBridge is not null)
            incarnation.Connection.OnServerRequest =
                (request, ct) => RouteServerRequestAsync(incarnation.Id, request, ct);
    }

    /// <summary>Reaps on a tripwire violation, reusing the forbidden-interaction path: same
    /// out-of-band termination, same single-shot guard, and the same reason it must not await
    /// termination on the read loop.</summary>
    void HandleMcpSurfaceViolation(string violation) {
        // TRACKED, not fire-and-forget. Disposal deletes the reviewer's transcript-bearing home, and
        // doing that while an in-flight reap has not confirmed the child is gone would leave a live
        // reviewer writing into a deleted path — and recreating it.
        // The violation forwards VERBATIM as the published verdict's coded reason.
        if (!TryStartReap(violation, () => {
                _logger.LogError("ACP: reaping unattended reviewer — {Violation}", violation);
                _cts.Cancel();

                return ReapUnexpectedInteractionAsync(violation);
            }))
            return;
    }

    /// <summary>Wired as the bridge's reason-bearing reap callback (design spec §3.1) — <paramref
    /// name="reason"/> arrives ALREADY CODED (<c>unattended_frame_unadmittable: …</c> or
    /// <c>unattended_interaction_forbidden:{method}</c>; see <c>AcpInteractionBridge</c>'s
    /// <c>unexpectedUnattendedInteraction</c> doc), so it forwards verbatim — same shape as <see
    /// cref="HandleMcpSurfaceViolation"/> forwarding its own <c>violation</c> text, and no longer
    /// re-derives the forbidden-method coding itself now that the bridge owns both codings.</summary>
    void HandleUnexpectedUnattendedInteraction(string reason) {
        // Do not await process termination on AcpConnection's read loop: that loop is currently
        // handling the offending request, and the child may wait for its response before exiting.
        // Reap out-of-band and cancel both runtime workers immediately.
        // Same channel as the violation path: this is the SAME termination, so disposal must wait on
        // it too. Leaving either untracked would reintroduce the race for whichever path fired.
        if (!TryStartReap(reason, () => {
                _cts.Cancel();

                return ReapUnexpectedInteractionAsync(reason);
            }))
            return;
    }

    async Task ReapUnexpectedInteractionAsync(string reason) {
        try {
            await _installed.Process.TerminateAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } catch (Exception ex) {
            // Bug 4: callers now pass the full coded reap reason / monitor violation, not a bare
            // JSON-RPC method — label it {Reason}. Bug A: SANITIZED (single-line, length-bounded,
            // surrogate-safe) because a coded reason embeds variable, agent-influenced content (MCP
            // server names, a monitor's 'why' string) that can carry line breaks or run long and
            // bloat this log line — the same normalization the forwarded verdict already uses.
            _logger.LogDebug(ex, "ACP: failed to reap unattended reviewer after {Reason}.", SanitizeForForward(reason));
        }
    }

    public string Vendor              => _vendor;

    /// <summary>The CURRENT (installed) child's pid — changes at a reconnect commit, so consumers
    /// reading it live (registry displays, spawn logs) track the serving incarnation. The durable
    /// PID record is updated separately, at candidate spawn (reconnect spec §6.2 step 1).</summary>
    public int    Pid                 => _installed.Process.Pid;

    // Vendor-aware handshake/auth diagnostic labels. `_vendor` is the vendor KEY (e.g. "cursor"),
    // not the binary name — so Cursor keeps its exact prior strings (binary "cursor-agent" +
    // Team-tier hint) via an explicit branch, while any other vendor gets vendor-named generic text.
    // Kiro needs the same explicit branch as Cursor and for the same reason: its vendor key ("kiro")
    // is NOT its binary name ("kiro-cli"), so the generic fallback would tell an operator to check a
    // command that does not exist on a correct install — the least helpful possible text on exactly the
    // launch-failure path this diagnostic exists to serve.
    string DiagnosticBinary =>
        _vendor == AcpVendorDescriptors.Cursor.Vendor ? "cursor-agent"
            : _vendor == AcpVendorDescriptors.Kiro.Vendor ? "kiro-cli"
            : _vendor;

    // Kiro deliberately gets a hint that names NO command. Cursor and Copilot name a verified login
    // subcommand; for Kiro no auth requirement, auth method, or login command was ever observed
    // (`authMethods` came back empty and a prompt completed with no API key set), so inventing a
    // `kiro-cli login` would be fabricated guidance. The generic fallback is also wrong here for the
    // same reason DiagnosticBinary needed a branch: it interpolates the vendor KEY and would tell an
    // operator to authenticate `kiro`, a command absent from a correct install.
    string DiagnosticAuthHint =>
        _vendor == AcpVendorDescriptors.Cursor.Vendor
            ? "run `cursor-agent login` and verify a Team-tier subscription"
            : _vendor == AcpVendorDescriptors.Copilot.Vendor
                ? "run `copilot login` and verify GitHub Copilot access for your enterprise"
                : _vendor == AcpVendorDescriptors.Kiro.Vendor
                    ? "verify Kiro CLI is signed in and your subscription/entitlement is active"
                    : _vendor == AcpVendorDescriptors.Gemini.Vendor
                        ? GeminiAuthHint
                        : $"authenticate `{_vendor}` and verify your subscription/entitlement";

    /// <summary>
    /// Gemini's hint, worded as a POSSIBILITY rather than a diagnosis — and that is a requirement, not
    /// politeness.
    ///
    /// <para>Gemini reports a missing project as
    /// <c>IneligibleTierError: … no longer supported for Gemini Code Assist for individuals</c>, thrown by
    /// a function named <c>throwIneligibleOrProjectIdError</c> — the SAME message for a missing project id
    /// as for a genuine tier problem. That confidently-wrong attribution cost two retracted conclusions
    /// while this vendor was being specced. Reproducing the pattern here would be worse than saying
    /// nothing, so this text claims nothing: two hedges plus an explicit "or it may be unrelated",
    /// because the failure this is attached to often IS unrelated.</para>
    ///
    /// <para>The daemon-not-your-shell clause is the actual content. A supervised daemon inherits nothing
    /// from an interactive shell, so a project variable exported in a shell profile is invisible to it —
    /// which is precisely how the misdiagnosis happened.</para>
    ///
    /// <para>Kept verbatim in the exception message rather than carried as a structured field: the message
    /// is a wire shape an older server consumes, and the honesty requirement is carried by the wording.
    /// Pinned by a golden test.</para>
    /// </summary>
    internal const string GeminiAuthHint =
        "this may be an authentication or project-configuration problem, or it may be unrelated — if "
      + "hosted Gemini has not worked on this machine before, check `gemini` is logged in and that "
      + "GOOGLE_CLOUD_PROJECT (or GOOGLE_CLOUD_PROJECT_ID) is set where the DAEMON can see it (the "
      + "service unit, not your shell profile), then re-run `kcap daemon service install` and restart "
      + "the daemon";

    /// <summary>LOGICAL liveness (reconnect spec §5.4): while `Reconnecting` the agent is alive by
    /// contract — a dead child mid-swap must not read as agent death to the orchestrator — so this
    /// reports <see langword="false"/> until the runtime is Running (current process's view) or
    /// Terminal (<see langword="true"/>).</summary>
    public bool   HasExited           => Phase switch {
        RuntimePhase.Reconnecting => false,
        RuntimePhase.Terminal     => true,
        _                         => _installed.Process.HasExited
    };

    public int?   ExitCode            => Phase == RuntimePhase.Reconnecting ? null : _installed.Process.ExitCode;
    public bool   EmitsTerminalOutput => false;

    /// <summary>The ACP <c>sessionId</c> once <see cref="StartAsync"/>'s <c>session/new</c> has resolved; null before that.</summary>
    public string? SessionId => _sessionId;

    /// <summary>
    /// Reduced <c>session/update</c> notifications, in arrival order. Unbounded so a mapper that
    /// attaches slightly late (or is momentarily busy) never misses an update — the alternative
    /// (a plain event) would drop anything raised before a subscriber attaches. Every update written
    /// here is ALSO fed into <see cref="AggregateUpdate"/> to build the aggregated
    /// <see cref="Envelopes"/> transcript — the two are independent sinks of the same reduced update,
    /// not a producer/consumer pair.
    /// </summary>
    public ChannelReader<AcpSessionUpdate> Updates => _updates.Reader;

    // ── IAcpTranscriptSource ─────────────────────────────────────────────────────────────────────
    // Exposed here for the orchestrator to pick up; not wired onto HostedRuntimeStart/the
    // orchestrator here (see IAcpTranscriptSource's remarks).

    /// <inheritdoc cref="IAcpTranscriptSource.AcpSessionId"/>
    /// <remarks>Only meaningful once <see cref="StartAsync"/> has resolved <see cref="_sessionId"/> —
    /// callers (the orchestrator, post-registration) only ever see this runtime after that.</remarks>
    public string AcpSessionId => _sessionId!;

    /// <inheritdoc cref="IAcpTranscriptSource.Cwd"/>
    public string Cwd => _cwd!;

    /// <inheritdoc cref="IAcpTranscriptSource.ResolvedModel"/>
    public string? ResolvedModel => _resolvedModel;

    /// <summary>
    /// Agent capabilities negotiated by <see cref="StartAsync"/>'s <c>initialize</c> call; null
    /// before <see cref="StartAsync"/> resolves. The reconnect path's eligibility gate reads
    /// <see cref="AgentCapabilities.LoadSession"/> off this (<see cref="EligibleForReconnectLocked"/>).
    /// </summary>
    public AgentCapabilities? NegotiatedCapabilities => _negotiatedCapabilities;

    /// <summary>Convenience projection of <see cref="NegotiatedCapabilities"/>'s <c>loadSession</c> flag — false before <see cref="StartAsync"/> resolves, or if the agent didn't advertise it.</summary>
    public bool SupportsLoadSession => _negotiatedCapabilities?.LoadSession ?? false;

    /// <summary>Non-null exactly when this launch may attempt reconnect (reconnect spec §4). The
    /// orchestrator wires the PID-record callbacks onto it after agent registration — the record
    /// store and the agent's identity fields are orchestrator-owned, so the runtime can only carry
    /// the seam.</summary>
    internal AcpReconnectSupport? ReconnectSupport => _reconnect;

    /// <summary>Test-only: invoked by the reconnect owner immediately after a successful commit,
    /// before the settlement wait — the instant a successor death is a CHAINED crash (§5.2).
    /// Never set in production.</summary>
    internal Action? TestHookAfterCommit { get; set; }

    /// <summary>Test-only observable for the §5.2 chained-crash marker, so a test injecting a
    /// post-commit successor death can wait for the signal to be OBSERVED before letting the owner
    /// proceed — pinning the chained arm deterministically instead of racing the EOF propagation
    /// (both interleavings are legal in production; the test wants exactly one).</summary>
    internal bool ChainedCrashPendingForTest {
        get { lock (_reconnectLock) return _crashedAgainIncarnation != -1; }
    }

    /// <inheritdoc cref="IAcpTranscriptSource.Envelopes"/>
    public ChannelReader<AcpEventEnvelope> Envelopes => _transcript.Reader;

    /// <summary>
    /// Performs the ACP handshake: starts the connection's read loop, then
    /// <c>initialize</c> → <c>session/new</c> (with the absolute <paramref name="cwd"/>) → an optional
    /// model-selection step — resolves <paramref name="requestedModel"/> against
    /// <c>session/new</c>'s <c>availableModels</c> and, if it matches, sends the vendor's
    /// model-selection RPC (<c>session/set_config_option</c> for Cursor/Copilot,
    /// <c>session/set_model</c> for Kiro — the descriptor's selector decides) and awaits the
    /// response BEFORE the first turn fires (see
    /// <see cref="IAcpModelSelector.TrySelectAsync"/>). If <paramref name="initialPrompt"/> is non-empty,
    /// <see cref="EnqueueTurn"/>s it onto the serialized prompt-turn worker (see
    /// <see cref="RunTurnWorkerAsync"/>) and returns as soon as the session is established — it
    /// does NOT await that prompt turn to completion. Not part of
    /// <see cref="IHostedAgentRuntime"/> — called directly by the runtime factory (and by tests)
    /// once the connection/process are constructed. A failed handshake surfaces a clear exception
    /// (never hangs): the read loop is started before any request is sent, and every request goes
    /// through <see cref="AcpConnection.RequestAsync"/>, which itself never hangs past
    /// <paramref name="ct"/> cancellation. Model selection is NEVER part of that "failed handshake"
    /// exception path — an unresolved or rejected model just falls back to the vendor's own default
    /// (see <see cref="IAcpModelSelector.TrySelectAsync"/>'s remarks).
    /// </summary>
    public async Task StartAsync(
            string cwd, string? initialPrompt, CancellationToken ct, string? requestedModel = null,
            IReadOnlyList<AcpMcpServerSpec>? mcpServers = null, string? modelOwedAnExplanation = null) {
        _cwd            = cwd;
        _requestedModel = requestedModel;
        // Captured for session/load: a resume must hand the agent the SAME server list the
        // original launch carried (reconnect spec §6.2 step 6).
        _mcpServersForResume = mcpServers?.ToArray() ?? NoMcpServers;

        var connection = _installed.Connection;

        _installed.LoopTask = RunIncarnationLoopAsync(_installed);
        _turnWorkerTask     = RunTurnWorkerAsync(_cts.Token);

        // Liveness-supervision spec §0/§1 (Task 13): the child process already exists by the time
        // this method runs — the factory spawns it and constructs this runtime before ever calling
        // StartAsync — so "spawned" is stamped immediately, with no cap of its own: there is nothing
        // left to bound (the OS spawn already completed synchronously) and no timeout on it could
        // ever have anything to kill. Each of the three REAL awaited stages below gets its own
        // independent RunHandshakeStageAsync cap.
        ActivityClock?.SetLaunchStage("spawned");

        JsonElement sessionNewResult;

        try {
            // Advertise NO client fs/terminal: cursor-agent does file/shell ops itself and never asks
            // the client to serve them (rationale: docs/ai-687-fs-terminal-capability-decision-design.md).
            // Any unadvertised request is declined -32601 by AcpConnection, never falsely acknowledged.
            // Elicitation IS advertised (form mode only, never url) — the end-to-end multi-select
            // lane shipped on both sides, so agents may now send elicitation/create; the bridge's
            // gate pipeline still owns every per-frame accept/cancel decision.
            var initializeParams = JsonSerializer.SerializeToElement(
                new InitializeParams(
                    ProtocolVersion: 1,
                    ClientCapabilities: new ClientCapabilities(
                        Fs: new FsCapabilities(ReadTextFile: false, WriteTextFile: false),
                        Terminal: false,
                        Elicitation: new ElicitationCapabilities(Form: new ElicitationFormCapabilities()))),
                CapacitorJsonContext.Default.InitializeParams);

            var initializeResultElement = await RunHandshakeStageAsync(
                    "initialized", stageCt => connection.RequestAsync("initialize", initializeParams, stageCt), ct)
                .ConfigureAwait(false);

            // Defensive: a malformed initialize response (wrong-typed protocolVersion, etc.) must not
            // surface as a raw JsonException. We distinguish a parse failure from a real version
            // mismatch so the error doesn't misreport a malformed response as "negotiated version 0".
            InitializeResult? initializeResult;
            try {
                initializeResult = JsonSerializer.Deserialize(initializeResultElement.GetRawText(), CapacitorJsonContext.Default.InitializeResult);
            } catch (JsonException) {
                initializeResult = null;
            }

            // This build only ever speaks version 1 — fail loud and clearly BEFORE session/new,
            // distinguishing a malformed/parse-failed response from a real version mismatch.
            if (initializeResult is null) {
                AcpMetrics.RecordFailure("handshake");
                throw new AcpProtocolVersionException(
                    $"{DiagnosticBinary}'s initialize response was malformed or omitted protocolVersion; this build supports ACP protocol version 1 — update kcap or {DiagnosticBinary}.");
            }
            if (initializeResult.ProtocolVersion != 1) {
                AcpMetrics.RecordFailure("handshake");
                throw new AcpProtocolVersionException(
                    $"{DiagnosticBinary} negotiated ACP protocol version {initializeResult.ProtocolVersion}; this build supports version 1 — update kcap or {DiagnosticBinary}.");
            }

            _negotiatedProtocolVersion = initializeResult.ProtocolVersion;

            // Missing agentCapabilities defensively means "advertises nothing" (loadSession=false),
            // not a throw — the reconnect eligibility gate reads loadSession off this.
            _negotiatedCapabilities = initializeResult.AgentCapabilities ?? new AgentCapabilities(LoadSession: false);

            var sessionNewParams = JsonSerializer.SerializeToElement(
                new SessionNewParams(Cwd: cwd, McpServers: mcpServers?.ToArray() ?? NoMcpServers),
                CapacitorJsonContext.Default.SessionNewParams);

            sessionNewResult = await RunHandshakeStageAsync(
                    "session_created", stageCt => connection.RequestAsync("session/new", sessionNewParams, stageCt), ct)
                .ConfigureAwait(false);

            if (!sessionNewResult.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.GetString() is not { Length: > 0 } sessionId)
                throw new InvalidOperationException("ACP session/new response did not contain a sessionId.");

            _sessionId = sessionId;

            LogSessionStarted(_agentId, sessionId);
            AcpMetrics.SessionsStarted.Add(1);
        } catch (AcpProtocolVersionException) {
            // Already actionable and NOT an auth issue — rethrow verbatim, without the auth hint.
            // Failure already recorded above, at the point the version mismatch was detected.
            throw;
        } catch (AcpLaunchStageTimeoutException) {
            // Already actionable and NOT an auth issue — rethrow verbatim so the coded
            // acp_launch_stage_timeout:{stage} reason reaches the caller undecorated, exactly like
            // AcpProtocolVersionException above.
            throw;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            AcpMetrics.RecordFailure("handshake");

            // Fold the original error's message into this one (single-lined + length-capped, since an
            // AcpRpcException carries the agent's arbitrary JSON-RPC error.message and this text is
            // forwarded to the server/UI via LaunchFailedAsync) and append a generic, actionable hint.
            // The full original exception is kept as InnerException, so daemon logs retain everything.
            // Deliberately conservative: the exact wire shape of a logged-out/unsubscribed cursor-agent
            // failure is unverified, so this does NOT pattern-match specific error text (a live
            // logged-out probe is a follow-up). Never masks the original error, only annotates it.
            throw new AcpHandshakeFailedException(
                "initialize/session-new", SanitizeForForward(ex.Message), DiagnosticAuthHint, ex);
        }

        // Select the requested model (if any) BEFORE the first prompt fires. Awaited, but never
        // fatal for a resolution failure — see IAcpModelSelector's cancellation-contract remarks
        // for why a canceled ct is the one exception to that (it propagates, aborting StartAsync).
        // Liveness-supervision spec (Task 13): also capped and stage-stamped like the two RPCs
        // above — a selector with no model to apply returns near-instantly (TrySelectAsync's
        // no-request-or-no-match paths never touch the wire), so this stage only ever actually
        // WAITS when a model was requested and the vendor's selector RPC hangs.
        _resolvedModel = await RunHandshakeStageAsync(
                "model_set",
                stageCt => _modelSelector.TrySelectAsync(connection, _sessionId!, sessionNewResult, requestedModel, _logger, stageCt),
                ct)
            .ConfigureAwait(false);

        // Handshake is now fully complete (initialize + session/new + best-effort model selection) —
        // one consolidated Info log carrying the negotiated protocol version, loadSession, and the
        // resolved model (null if none was requested/matched).
        LogHandshakeOk(_agentId, _negotiatedProtocolVersion, _negotiatedCapabilities.LoadSession, _resolvedModel);

        // A dropped model is only knowable after session/new publishes the vendor's list, so nothing
        // upstream can refuse the launch over it: the agent runs, answering as a model the user did
        // not pick. Names the model the LAUNCH asked for, which is not always the one that reached
        // the selector. Emitted before the initial turn is enqueued, so it precedes that turn's output.
        if (modelOwedAnExplanation is { Length: > 0 } owed && _resolvedModel is null)
            EmitModelFallbackNote(owed);

        // The session is established (initialize + session/new both completed) — the caller
        // (orchestrator) can now treat this agent as live. Enqueue the initial turn without
        // awaiting it: a real ACP turn can run arbitrarily long, and blocking StartAsync on it would
        // delay agent registration/stoppability for the whole turn. Completion is
        // observed via the Updates/Envelopes channels, not this method's return.
        if (!string.IsNullOrEmpty(initialPrompt)) {
            _ = EnqueueTurn(initialPrompt, acknowledgeWrite: false);
            ArmFirstOutputWatchdog();
        } else {
            // Deterministic backstop (design spec §3.3): no turn will ever run to settle the marker
            // via ProcessAdmittedTurnAsync when the launch carries no initial prompt — reviewer
            // launches always carry one, but the window still needs a defined close for any launch
            // that doesn't, or a later reap would misclassify as still inside it forever.
            _firstTurnSettled.TrySetResult();
        }
    }

    /// <summary>
    /// Wraps one ACP handshake step in its own <see cref="AcpLaunchStageTimeout"/> (liveness-
    /// supervision spec §5). A FRESH <see cref="CancellationTokenSource"/> per call is what makes each
    /// stage's budget independent, and it runs off <see cref="_timeProvider"/> — monotonic, so a
    /// wall-clock jump can never fail a healthy handshake. On success, stamps
    /// <see cref="AgentActivityClock.SetLaunchStage"/>, the evidence the out-of-cycle status report
    /// extends the server's registration wait on.
    ///
    /// <para>The timeout logs the stage and agent id BEFORE attempting the kill — the process (and any
    /// further stderr) is about to be gone. Termination is BEST-EFFORT and a failure only logs, so the
    /// thrown message must not claim the child died; see
    /// <see cref="AcpLaunchStageTimeoutException"/>.</para>
    /// </summary>
    async Task<T> RunHandshakeStageAsync<T>(string stage, Func<CancellationToken, Task<T>> operation, CancellationToken ct) {
        using var stageTimeout = new CancellationTokenSource(AcpLaunchStageTimeout, _timeProvider);
        using var linked       = CancellationTokenSource.CreateLinkedTokenSource(ct, stageTimeout.Token);

        T result;
        try {
            result = await operation(linked.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (stageTimeout.IsCancellationRequested && !ct.IsCancellationRequested) {
            AcpMetrics.RecordFailure("handshake");
            LogLaunchStageTimeout(_agentId, stage, AcpLaunchStageTimeout.TotalSeconds);

            try {
                await _installed.Process.TerminateAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (Exception ex) {
                // Warning, not Debug: the exception message says termination was only REQUESTED, so
                // this line is the only record that an orphaned child may still be running.
                _logger.LogWarning(ex, "ACP: failed to reap a wedged handshake at stage '{Stage}' — the child process may still be running.", stage);
            }

            throw new AcpLaunchStageTimeoutException(stage, AcpLaunchStageTimeout);
        }

        ActivityClock?.SetLaunchStage(stage);

        return result;
    }

    /// <summary>
    /// Reaps a first turn that produces NO output at all within the deadline. Fire-and-forget by
    /// design: StartAsync must not block on the turn (see <see cref="_firstOutputDeadline"/>), so the
    /// bound cannot be a cancellation token threaded through it.
    /// </summary>
    void ArmFirstOutputWatchdog() {
        if (_firstOutputDeadline is not { } deadline) return;


        _ = Task.Run(async () => {
            try {
                await Task.Delay(deadline, _timeProvider, _cts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;   // disposed, reaped, or already finished — nothing to police
            }

            if (Volatile.Read(ref _sawFirstUpdate) != 0) return;

            HandleMcpSurfaceViolation(
                $"kiro_reviewer_first_output_timeout: the reviewer produced no output within "
              + $"{deadline.TotalSeconds:0}s of its first prompt. A kiro-cli whose credential has "
              + "expired stays alive on an interactive browser prompt rather than failing, which is "
              + "the shape this bound exists for — check that the daemon user's kiro-cli is still "
              + "authenticated.");
        });
    }

    /// <summary>
    /// Enqueues a prompt-turn's text onto <see cref="_pendingTurns"/> and returns immediately — never
    /// blocks on, or observes, that turn's completion. Used by both
    /// <see cref="StartAsync"/> (initial prompt) and <see cref="SendUserInputAsync"/> (follow-up
    /// turns), preserving a non-blocking contract for both callers. The single
    /// <see cref="RunTurnWorkerAsync"/> worker drains this queue strictly in order; turn completion is
    /// observed via <see cref="Updates"/>/<see cref="Envelopes"/>, not this method's return.
    ///
    /// <see cref="_pendingTurns"/> is bounded (<see cref="BoundedChannelFullMode.DropWrite"/>) —
    /// checked explicitly BEFORE writing so a full queue logs a clear warning (with a running
    /// dropped-count) rather than silently discarding the input via the channel's own drop-write
    /// behavior.
    /// </summary>
    Task EnqueueTurn(string text, bool acknowledgeWrite) {
        var written = acknowledgeWrite
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        if (_pendingTurns.Reader.Count >= _pendingTurnsCapacity) {
            var dropped = Interlocked.Increment(ref _droppedPendingTurns);
            _logger.LogWarning(
                "ACP: pending-turns queue full (capacity={Capacity}) — dropping this input; {DroppedCount} dropped this session so far (the turn worker is likely stuck on a stalled turn).",
                _pendingTurnsCapacity, dropped);

            written?.TrySetException(new InvalidOperationException("ACP pending-turns queue is full."));
            return written?.Task ?? Task.CompletedTask;
        }

        if (!_pendingTurns.Writer.TryWrite(new PendingTurn(text, written))) {
            _logger.LogDebug("ACP: dropped a prompt turn — pending-turns channel already completed.");
            written?.TrySetException(new ObjectDisposedException(nameof(AcpHostedAgentRuntime)));
        }
        return written?.Task ?? Task.CompletedTask;
    }

    /// <summary>
    /// The single, long-running prompt-turn worker. Drains <see cref="_pendingTurns"/> strictly FIFO,
    /// processing exactly one turn (<see cref="ProcessAdmittedTurnAsync"/>) fully to completion before
    /// starting the next — this single-flight serialization is what makes "the aggregation buffer
    /// unambiguously belongs to the active turn" true. Cancellable: <c>ChannelReader.ReadAllAsync(ct)</c> observes
    /// <paramref name="ct"/> both between turns and (via <see cref="ProcessAdmittedTurnAsync"/>'s own use of
    /// <paramref name="ct"/> in <see cref="SendPromptAsync"/>) inside an in-flight turn, so a turn
    /// whose <c>stopReason</c> never arrives cannot pin <see cref="DisposeAsync"/>.
    /// </summary>
    async Task RunTurnWorkerAsync(CancellationToken ct) {
        try {
            while (true) {
                PendingTurn turn;
                var skipEnvelope = false;

                // The held-turn slot outranks the queue (reconnect spec §6.4 ordering: held turn
                // first, then queued turns). At most one turn can be held: the worker is
                // single-flight, so only one turn is ever past dequeue.
                if (_heldTurn is { } held) {
                    turn                  = held;
                    skipEnvelope          = _heldTurnSkipEnvelope;
                    _heldTurn             = null;
                    _heldTurnSkipEnvelope = false;
                } else {
                    if (!await _pendingTurns.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                        break; // channel completed — shutdown

                    if (!_pendingTurns.Reader.TryRead(out turn!))
                        continue;
                }

                // Pre-gate admission (reconnect spec §5.3): the park-or-register decision and —
                // when admitted — the UserMessage envelope emission share ONE critical section
                // under the reconnect lock (the aggregation lock nests inside, §5.1), so a crash
                // snapshot can never observe a registered turn with indeterminate envelope state,
                // and a turn dequeued after the gate closed parks instead of being sent at a dead
                // incarnation. Parking holds no gate.
                Task? reopen   = null;
                var   terminal = false;

                lock (_reconnectLock) {
                    if (Phase == RuntimePhase.Terminal) {
                        terminal = true;
                    } else if (Phase == RuntimePhase.Reconnecting) {
                        _heldTurn             = turn;
                        _heldTurnSkipEnvelope = skipEnvelope;
                        reopen                = _gateOpen.Task;
                    } else {
                        _inFlight = new InFlightTurn(turn, _installed.Id);
                        if (!skipEnvelope)
                            EmitEnvelope(AcpEventTranslator.BuildUserMessage(seq: 0, NowIso(), turn.Text));
                    }
                }

                if (terminal) {
                    // The session is over; the turn can never be delivered. Fault its ack honestly
                    // rather than leaving a caller awaiting a write that will never happen.
                    turn.Written?.TrySetException(new InvalidOperationException(
                        "ACP session ended while this input was pending delivery."));
                    break;
                }

                if (reopen is not null) {
                    await reopen.WaitAsync(ct).ConfigureAwait(false);
                    continue; // re-picks the held turn (or observes Terminal) next iteration
                }

                await ProcessAdmittedTurnAsync(turn, ct).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // normal shutdown — see this method's remarks.
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP: prompt-turn worker ended unexpectedly.");
        }
    }

    /// <summary>
    /// Processes exactly one ADMITTED prompt turn (its <c>UserMessage</c> envelope was already
    /// emitted inside the pre-gate admission critical section — reconnect spec §5.3): (a) performs
    /// the write-entry transition (or parks on refusal); (b) sends <c>session/prompt</c> and awaits
    /// its <c>stopReason</c> response (reusing <see cref="SendPromptAsync"/>); (c) performs this
    /// turn's end-of-turn flush of the aggregation buffer in a <c>finally</c> — this runs whether
    /// the turn completed normally, faulted (logged, non-fatal), or was cancelled (a courtesy flush
    /// of whatever partial text had accumulated; see <see cref="_aggregationLock"/>'s remarks on why
    /// this can never hang <see cref="DisposeAsync"/> — flushing is a pure in-memory operation, never
    /// I/O). A cancellation still propagates out of this method (the <c>when</c> filter below only
    /// catches non-cancellation faults) so <see cref="RunTurnWorkerAsync"/>'s loop stops promptly.
    /// </summary>
    async Task ProcessAdmittedTurnAsync(PendingTurn turn, CancellationToken ct) {
        await _turnExecutionGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            // Write entry (reconnect spec §5.3, `TryEnterWrite`): re-check `Reconnecting` and
            // advance `not-started → entered` atomically, INSIDE the turn-execution gate,
            // immediately before the send. A refusal parks the turn with its envelope already in
            // the transcript (skip-user-envelope) and NEVER faults the ack — the failed write
            // entry is what guarantees this turn's bytes never reached any incarnation, the race
            // an installed-id check alone cannot close because no swap has happened yet. The park
            // path returns through this method's finally, releasing the gate BEFORE the worker
            // awaits reopen, which is what keeps the owner's settlement wait deadlock-free.
            AcpConnection connection;

            lock (_reconnectLock) {
                if (Phase != RuntimePhase.Running || _inFlight is not { } inFlight || !ReferenceEquals(inFlight.Turn, turn)) {
                    _heldTurn             = turn;
                    _heldTurnSkipEnvelope = true;
                    _inFlight             = null;
                    return;
                }

                inFlight.WriteState = InFlightTurn.Entered;
                connection          = _installed.Connection;
            }

            // Liveness-supervision spec §0/§1: the turn gate is genuinely held from here — a parked
            // (not-yet-entered) turn above never reaches this line, so TurnInFlight never flips true
            // for a turn that isn't really in flight yet.
            ActivityClock?.SetTurnInFlight(true);

            // Diagnostic pair with AgentOrchestrator's "SendInput received"/"SendInput delivered" —
            // those stop at the daemon→runtime boundary, so a hung follow-up round is otherwise
            // indistinguishable in the logs from a delivered prompt the agent never acted on. One
            // line per turn, here at the same bracket point as TurnInFlight above (never per envelope
            // or chunk).
            LogTurnStarted(_agentId, _vendor);

            using var silenceNotice = ArmTurnSilenceNotice(turn);

            try {
                await SendPromptAsync(connection, turn, ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Only reachable once write entry succeeded: faulting the ack is correct for
                // `entered` and a structural no-op for `written` (the TCS already resolved at
                // onWritten; TrySetException against a resolved TCS does nothing — normative per
                // reconnect spec §5.3).
                turn.Written?.TrySetException(ex);
                _logger.LogDebug(ex, "ACP: session/prompt turn faulted; flushing this turn's partial buffer.");
            } finally {
                // C4 inverted (reconnect spec §5.4): the flush is KEPT on the crash path — under
                // skip-whole-replay nothing is ever re-emitted from replay, so the flushed partial
                // run cannot be duplicated; it is the only copy of what the agent said before dying.
                FlushOpenRun();

                // However this turn ended — stopReason, fault, cancellation — it is settled, which
                // disarms the first-output watchdog. Without this a turn that legitimately produced
                // no session/update at all would leave the timer armed to reap a healthy reviewer.
                // Disarms the first-output watchdog SYNCHRONOUSLY, here, rather than through a
                // continuation on _firstTurnSettled: continuations run asynchronously, so the
                // watchdog could read zero after the turn settled but before the continuation ran,
                // and reap a healthy reviewer whose zero-update turn finished near the deadline.
                Interlocked.Exchange(ref _sawFirstUpdate, 1);
                _firstTurnSettled.TrySetResult();

                // Liveness-supervision spec §0/§1: the turn ends here regardless of how it ended
                // (stopReason, fault, or cancellation) — this finally always runs for a turn that
                // reached the entered state above, so the true/false pair is exactly bracketed.
                ActivityClock?.SetTurnInFlight(false);

                // Same bracket as LogTurnStarted above — logged unconditionally so a turn that began
                // and never finished is visible as a started-with-no-matching-ended line.
                LogTurnEnded(_agentId, _vendor);

                lock (_reconnectLock) {
                    if (_inFlight is { } f && ReferenceEquals(f.Turn, turn))
                        _inFlight = null;
                }
            }
        } finally {
            _turnExecutionGate.Release();
        }
    }

    /// <summary>
    /// One incarnation's connection read loop — used for the original launch AND for every
    /// reconnect candidate (the owner starts a candidate's loop through this same method, which is
    /// what lets the candidate's <c>initialize</c>/<c>session/load</c> responses resolve at all).
    /// The `finally` routes through <see cref="HandleTransportEnded"/>, whose incarnation-stamped,
    /// state-dispatched logic decides between absorbing the end into a reconnect incident and the
    /// terminal path — a candidate's loop ending is inert here (uninstalled stamp), and completing
    /// <see cref="_updates"/> is centralized in the terminal dispositions rather than done
    /// unconditionally, so a crash that reconnect will absorb never signals terminal to the
    /// orchestrator (reconnect spec §5.4).
    /// </summary>
    async Task RunIncarnationLoopAsync(Incarnation incarnation) {
        try {
            await incarnation.Connection.RunAsync(incarnation.LoopCts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // normal shutdown
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP connection read loop ended unexpectedly.");
        } finally {
            HandleTransportEnded(incarnation.Id);
        }
    }

    /// <summary>
    /// Belt-and-braces transport-death watcher: a child whose PROCESS exits while something else
    /// (e.g. a self-re-exec'd inner process — the measured Gemini shape) holds its stdout pipe open
    /// never EOFs the read loop, so process exit must independently drive the same
    /// incarnation-stamped path. Idempotent with the read-loop trigger by construction
    /// (<see cref="HandleTransportEnded"/>'s duplicate-signal arm).
    /// </summary>
    async Task WatchProcessExitAsync(Incarnation incarnation) {
        try {
            await incarnation.Process.WaitForExitAsync().ConfigureAwait(false);
        } catch {
            // WaitForExitAsync swallows its own faults; this is pure defense.
        }

        HandleTransportEnded(incarnation.Id);
    }

    /// <summary>
    /// Never yields a byte — ACP stdout is protocol traffic, never terminal output (no terminal
    /// capability yet). Crucially, this must NOT complete until the process exits or
    /// <paramref name="ct"/> cancels (Fix B/E): <see cref="AgentOrchestrator.ReadAgentOutputAsync"/>
    /// treats the enumerable ending as "the agent's output stream ended" and finalizes the agent —
    /// for a PTY that's a real signal (the CLI exited), but the old implementation here
    /// (<c>yield break</c> on the first call) made a LIVE ACP agent look like it had already
    /// finished, so the orchestrator immediately finalized it as failed. Staying open for the
    /// process's whole lifetime means the orchestrator's read loop parks harmlessly (yielding
    /// nothing) instead of ending prematurely; <see cref="IHostedAgentRuntime.EmitsTerminalOutput"/>
    /// tells the orchestrator not to use this stream for the Starting→Running/startup-failure
    /// signals it uses for PTY runtimes.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        // Re-keyed from "this process exited" to "the runtime went logically terminal" (reconnect
        // spec §5.4): the orchestrator treats this enumerable ending as the finalize trigger, so it
        // must NOT fire for a crash that reconnect will absorb. _runtimeTerminal completes on an
        // ineligible crash (same moment the process-exit wait used to fire, via
        // HandleTransportEnded), on reconnect exhaustion, and on intentional stop/dispose — never
        // during an incident being absorbed.
        var ctTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var reg = ct.Register(() => ctTcs.TrySetResult());

        await Task.WhenAny(_runtimeTerminal.Task, ctTcs.Task).ConfigureAwait(false);

        yield break;
    }

    /// <summary>
    /// Sends a follow-up <c>session/prompt</c> for hosted-UI text input (server <c>SendInput</c>).
    /// Returns as soon as the text is enqueued (see <see cref="EnqueueTurn"/>) — it does NOT await the
    /// turn's <c>stopReason</c> response: a real turn can run arbitrarily long, and the
    /// pre-fix behavior (awaiting the full round trip) blocked this call — and therefore the
    /// orchestrator's <c>HandleSendInput</c> — for the whole turn. If a prior turn is still in
    /// flight, this text is queued FIFO and the worker sends it only once that turn's own
    /// <c>stopReason</c> has been received and its buffer flushed — turn completion is
    /// observed via <see cref="Updates"/>/<see cref="Envelopes"/>, not this method's return.
    /// </summary>
    public Task SendUserInputAsync(string text) {
        RequireSessionId();
        return EnqueueTurn(text, acknowledgeWrite: false);
    }

    public Task SendUserInputAndWaitForWriteAsync(string text) {
        RequireSessionId();
        return EnqueueTurn(text, acknowledgeWrite: true);
    }

    public async Task WaitForTurnIdleAsync(CancellationToken ct) {
        await _turnExecutionGate.WaitAsync(ct).ConfigureAwait(false);
        _turnExecutionGate.Release();
    }

    async Task SendPromptAsync(AcpConnection connection, PendingTurn turn, CancellationToken ct) {
        var promptParams = JsonSerializer.SerializeToElement(
            new SessionPromptParams(
                SessionId: _sessionId!,
                Prompt: [new PromptContentBlock(Type: "text", Text: turn.Text)]),
            CapacitorJsonContext.Default.SessionPromptParams);

        await connection.RequestAsync(
            "session/prompt", promptParams, ct, () => {
                turn.Written?.TrySetResult();

                // Advance the §5.3 write-state to `written` — from here on, a crash's disposition
                // for this turn is "surfaced, ack already resolved", never a re-send.
                lock (_reconnectLock) {
                    if (_inFlight is { } inFlight && ReferenceEquals(inFlight.Turn, turn))
                        inFlight.WriteState = InFlightTurn.Written;
                }
            }).ConfigureAwait(false);
    }

    sealed record PendingTurn(string Text, TaskCompletionSource? Written);

    public Task SendSpecialKeyAsync(string key) {
        // ACP has no special-key channel — best-effort no-op.
        _logger.LogDebug("ACP runtime ignoring SendSpecialKeyAsync({Key}) — no special-key surface in ACP.", key);
        return Task.CompletedTask;
    }

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException("Local-attach raw input is a PTY-only surface; the ACP runtime has no equivalent channel.");

    public void Resize(ushort cols, ushort rows) {
        // No terminal capability until — no-op.
    }

    public async Task RequestGracefulStopAsync() {
        if (_sessionId is not { Length: > 0 } sessionId) {
            _logger.LogDebug("ACP runtime RequestGracefulStopAsync called before a session was established; nothing to cancel.");
            return;
        }

        var cancelParams = JsonSerializer.SerializeToElement(
            new SessionCancelParams(SessionId: sessionId),
            CapacitorJsonContext.Default.SessionCancelParams);

        try {
            await _installed.Connection.NotifyAsync("session/cancel", cancelParams).ConfigureAwait(false);
        } catch (Exception ex) when (Phase != RuntimePhase.Running) {
            // A graceful-stop notify against a dead or mid-swap connection is expected while
            // reconnecting/terminal — swallow and log (reconnect spec §9); the hard stop path
            // (TerminateAsync) is what actually ends the incident.
            _logger.LogDebug(ex, "ACP: session/cancel against a non-running connection; ignoring.");
        }
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _installed.Process.WaitForExitAsync(timeout);

    /// <summary>
    /// The hard stop: marks intentional stop FIRST (under the reconnect lock — from here on every
    /// crash signal is a no-op, no swap and no held-turn delivery can happen, and an in-flight
    /// reconnect owner's next checkpoint unwinds it), sweeps pending interactions, then terminates
    /// the current child. C7's lock-scope rule holds: the lock guards only the flag flips and the
    /// owner-CTS cancellation; the process termination and the interaction tokens run outside it.
    /// </summary>
    public Task TerminateAsync(TimeSpan? timeout = null) {
        IAcpProcess current;
        List<PendingInteraction>? swept;

        lock (_reconnectLock) {
            _intentionalStop = true;
            _ownerCts?.Cancel();
            swept   = MarkPendingInteractionsCancelledLocked();
            current = _installed.Process;
        }

        ScheduleInteractionSweep(swept);

        return current.TerminateAsync(timeout);
    }

    void HandleNotification(AcpNotification notification) {
        // Before the session/update filter: the MCP-surface notifications are a different method, and
        // enforcement runs for the WHOLE session rather than a window — a late server initialization
        // is exactly the case a sampling scheme would miss.
        if (_mcpSurfaceMonitor is { } monitor) {
            monitor.Observe(notification);

            if (monitor.Violation is { } violation)
                HandleMcpSurfaceViolation(violation);
        }

        if (notification.Method != "session/update")
            return;

        // Notification suppression (reconnect spec §5.4): while an incident is in flight, every
        // session/update — the dying connection's last gasps and the candidate's entire replay —
        // is dropped before it can reach _updates or aggregation. A volatile read, never a lock:
        // this runs synchronously on a connection read loop, which must never contend with
        // admission (§5.1). Scope is deliberately notifications-only — the turn worker's flush
        // path and the owner's own emissions are not suppressed.
        if (_suppressNotifications) {
            Interlocked.Increment(ref _suppressedUpdates);
            return;
        }

        if (notification.Params is not { } @params || !@params.TryGetProperty("update", out var updateElement)) {
            _logger.LogDebug("ACP: session/update notification missing 'update' object; skipping.");
            return;
        }

        var reduced = Reduce(updateElement.Clone());

        // Only an update that carries actual TURN OUTPUT disarms the watchdog.
        //
        // "Recognized kind" is not the same predicate and was too weak: Reduce validates the
        // discriminator, not its payload, so an empty agent_message_chunk or an id-less tool_call
        // yields a known kind while saying nothing — one such frame plus a never-settled turn would
        // buy the unbounded silence this exists to catch. The session-scoped kinds
        // (available_commands, session_info, usage) are excluded for the same reason: a peer can emit
        // them and still never begin the turn.
        if (reduced.Kind is AcpUpdateKind.AgentMessageChunk or AcpUpdateKind.AgentThoughtChunk
                         or AcpUpdateKind.ToolCall or AcpUpdateKind.ToolCallUpdate
                         or AcpUpdateKind.Plan)
            Interlocked.Exchange(ref _sawFirstUpdate, 1);
        if (!_updates.Writer.TryWrite(reduced))
            _logger.LogDebug("ACP: dropped a session/update — updates channel already completed.");

        // Fed synchronously, on THIS notification callback's own thread — AcpConnection.RunAsync's
        // single read loop calls HandleNotification directly (never concurrently with itself), so
        // every update this runtime ever sees is aggregated in strict arrival order without needing
        // its own queue/consumer loop.
        AggregateUpdate(reduced);
    }

    static AcpSessionUpdate Reduce(JsonElement update) {
        var kindText = update.TryGetProperty("sessionUpdate", out var kindEl) ? kindEl.GetString() : null;

        return kindText switch {
            "agent_message_chunk" => new AcpSessionUpdate(
                AcpUpdateKind.AgentMessageChunk,
                Text: ExtractContentText(update),
                Raw: update),

            "agent_thought_chunk" => new AcpSessionUpdate(
                AcpUpdateKind.AgentThoughtChunk,
                Text: ExtractContentText(update),
                Raw: update),

            "tool_call" => new AcpSessionUpdate(
                AcpUpdateKind.ToolCall,
                ToolCallId: GetStringOrNull(update, "toolCallId"),
                ToolTitle: GetStringOrNull(update, "title"),
                ToolKind: GetStringOrNull(update, "kind"),
                ToolStatus: GetStringOrNull(update, "status"),
                ToolInputJson: GetRawTextOrNull(update, "rawInput"),
                Raw: update),

            "tool_call_update" => new AcpSessionUpdate(
                AcpUpdateKind.ToolCallUpdate,
                ToolCallId: GetStringOrNull(update, "toolCallId"),
                ToolStatus: GetStringOrNull(update, "status"),
                ToolResultText: ExtractToolResultText(update),
                ToolIsError: GetStringOrNull(update, "status") == "failed",
                Raw: update),

            "plan" => new AcpSessionUpdate(AcpUpdateKind.Plan, Raw: update),

            "available_commands_update" => new AcpSessionUpdate(AcpUpdateKind.AvailableCommands, Raw: update),

            "session_info_update" => new AcpSessionUpdate(
                AcpUpdateKind.SessionInfo,
                Title: GetStringOrNull(update, "title"),
                Raw: update),

            // The ACP spec's Session Usage RFD. No vendor implements it yet, so this arm is
            // dormant; it exists so the context chip lights up for every ACP-hosted vendor the
            // moment any of them ships it. Content-free unless both fields are present and
            // sane - a partial reading is worse than none, since `size` is the chip's
            // denominator. `used > size` is kept: reported windows can be advisory. A zero
            // `used` is dropped to match the server's re-validation, where it would be
            // durably appended yet invisible to every consumer (the peak comparison they all
            // share ignores a zero candidate). Any `cost` or unknown siblings are ignored.
            "usage_update" => update.Num("used") is { } used and > 0 && update.Num("size") is { } size and > 0
                ? new AcpSessionUpdate(
                    AcpUpdateKind.UsageUpdate,
                    ContextUsedTokens: used,
                    ContextWindowTokens: size,
                    Raw: update)
                : new AcpSessionUpdate(AcpUpdateKind.Unknown, Raw: update),

            _ => new AcpSessionUpdate(AcpUpdateKind.Unknown, Raw: update),
        };
    }

    static string? ExtractContentText(JsonElement update) =>
        update.TryGetProperty("content", out var content) && content.TryGetProperty("text", out var textEl)
            ? textEl.GetString()
            : null;

    static string? GetStringOrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null; // guard the ValueKind: GetString() THROWS on a non-string value (number/object/
                    // array), which would let a schema-drift frame bubble an exception up and skip
                    // the entire notification. A wrong-typed field is treated as absent instead.

    /// <summary>
    /// Verbatim JSON text of <paramref name="propertyName"/> when it's a JSON object (e.g. a
    /// <c>tool_call</c>'s <c>rawInput</c>), else <see langword="null"/> — used to populate
    /// <see cref="AcpSessionUpdate.ToolInputJson"/> without re-serializing/reshaping the tool's input
    /// args (the mapper on the server side parses this raw text itself; see
    /// <c>AcpSessionMapper.BuildToolCall</c>).
    /// </summary>
    static string? GetRawTextOrNull(JsonElement element, string propertyName) =>
        element.Obj(propertyName)?.GetRawText();

    /// <summary>
    /// Extracts a tool_call_update's RESULT text for <see cref="AcpSessionUpdate.ToolResultText"/>,
    /// mechanically and regardless of <c>status</c> (the terminal-status gate lives in
    /// <c>AcpEventTranslator</c>, not here). Prefers the ACP-spec <c>content</c> array's text-block
    /// shape (<c>ToolCallContent</c>: <c>{type:"content", content:{type:"text", text:"..."}}</c>) —
    /// concatenating every such block found (newline-joined); non-text content variants
    /// (<c>diff</c>/<c>terminal</c>) are not extracted here, degrading to "no result text from this
    /// block" rather than throwing. Falls back to the verbatim <c>rawOutput</c> JSON text when no
    /// text content block is present. Returns <see langword="null"/> when neither is
    /// present/extractable, so <c>AcpEventTranslator.Translate</c> never emits an empty
    /// <c>ToolResultReceived</c>. This shape is defensive/spec-derived, not yet probe-confirmed
    /// against real Cursor output (see docs/acp-probe-findings.md).
    /// </summary>
    static string? ExtractToolResultText(JsonElement update) {
        if (update.Arr("content") is { } contentEl) {
            List<string>? texts = null;

            foreach (var block in contentEl.EnumerateArray()) {
                if (block.Str("type") != "content") continue;
                if (block.Obj("content") is not { } inner) continue;
                if (inner.Str("text") is not { } text) continue;

                (texts ??= []).Add(text);
            }

            if (texts is { Count: > 0 })
                return string.Join("\n", texts);
        }

        return GetRawTextOrNull(update, "rawOutput");
    }

    // ── Chunk aggregation ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates ONE reduced update: a same-kind
    /// <see cref="AcpUpdateKind.AgentMessageChunk"/>/<see cref="AcpUpdateKind.AgentThoughtChunk"/> run
    /// grows the open buffer; any other kind (or a kind transition between message/thought) flushes
    /// the open run first, then — for the non-aggregated kinds — translates <paramref name="update"/>
    /// 1:1 and emits it if non-null (tool_call/tool_call_update/plan/available_commands/unknown all
    /// take this path). Entirely under <see cref="_aggregationLock"/> so the
    /// check-open-run-kind-then-append-or-flush decision is atomic against a concurrent turn-end
    /// flush from the worker (see the lock field's remarks for why that matters even though the two
    /// call sites are serialized in practice).
    /// </summary>
    void AggregateUpdate(AcpSessionUpdate update) {
        lock (_aggregationLock) {
            switch (update.Kind) {
                case AcpUpdateKind.AgentMessageChunk:
                case AcpUpdateKind.AgentThoughtChunk:
                    if (_openRunKind == update.Kind) {
                        _openRunText!.Append(update.Text);
                    } else {
                        FlushOpenRunLocked();
                        _openRunKind = update.Kind;
                        _openRunText = new StringBuilder(update.Text ?? "");
                    }
                    break;

                case AcpUpdateKind.SessionInfo:
                case AcpUpdateKind.UsageUpdate:
                    // Neither is transcript content, so neither must close an open chunk run. One
                    // interleaved between two message/thought chunks would otherwise flush the run
                    // mid-stream and split one contiguous assistant message into two envelopes.
                    // Emit standalone, leaving _openRunKind/_openRunText untouched so the
                    // surrounding chunks still aggregate into one run — both are orderless
                    // metadata, so their relative seq is immaterial.
                    var metaEnvelope = AcpEventTranslator.Translate(
                        update, seq: 0, NowIso(), logger: _logger, debugFrames: _debugFrames,
                        resolvedModel: _resolvedModel);
                    if (metaEnvelope is { } m)
                        EmitEnvelope(m);
                    break;

                default:
                    FlushOpenRunLocked(); // kind-transition — the open run (if any) ends here
                    var envelope = AcpEventTranslator.Translate(update, seq: 0, NowIso(), logger: _logger, debugFrames: _debugFrames);
                    if (envelope is { } e)
                        EmitEnvelope(e);
                    break;
            }
        }
    }

    /// <summary>
    /// Turn-end / session-end flush entry point: flushes the open aggregation run, if any.
    /// Called by <see cref="ProcessAdmittedTurnAsync"/> on its turn's <c>stopReason</c>/fault/cancellation,
    /// and defensively by <see cref="DisposeAsync"/> as a session-end safety net. Idempotent — a
    /// second call with no open run is a no-op.
    /// </summary>
    void FlushOpenRun() {
        lock (_aggregationLock) FlushOpenRunLocked();
    }

    /// <summary>
    /// Flushes the open run — MUST be called while already holding <see cref="_aggregationLock"/>.
    /// Builds a representative <see cref="AcpSessionUpdate"/> carrying only the run's
    /// <see cref="AcpUpdateKind"/> (the translator only needs the kind to pick
    /// <see cref="AcpEventKind.AssistantText"/> vs <see cref="AcpEventKind.AssistantThinking"/> when
    /// <c>aggregatedText</c> is supplied — see <c>AcpEventTranslator.Translate</c>'s remarks) and
    /// translates it with the accumulated buffer as <c>aggregatedText</c>, emitting exactly ONE
    /// envelope for the whole run.
    /// </summary>
    void FlushOpenRunLocked() {
        if (_openRunKind is not { } kind)
            return;

        var text = _openRunText!.ToString();
        _openRunKind = null;
        _openRunText = null;

        var representative = new AcpSessionUpdate(kind);
        var envelope        = AcpEventTranslator.Translate(representative, seq: 0, NowIso(), aggregatedText: text, logger: _logger, debugFrames: _debugFrames);
        if (envelope is { } e)
            EmitEnvelope(e);
    }

    /// <summary>
    /// The ONLY call site that writes to <see cref="_transcript"/> — always under
    /// <see cref="_aggregationLock"/> (reentrant, so callers already holding it from
    /// <see cref="AggregateUpdate"/>/<see cref="FlushOpenRunLocked"/> do not deadlock) so envelope
    /// order on the channel matches lock-acquisition order across every writer.
    ///
    /// <see cref="_transcript"/> is bounded (<see cref="BoundedChannelFullMode.DropOldest"/>) —
    /// checked explicitly BEFORE writing (under
    /// the same lock, so no other writer can race this check) so a full channel logs a clear warning
    /// (with a running dropped-count) at the exact write that triggers the eviction, rather than
    /// relying on <c>TryWrite</c>'s return value, which is <see langword="true"/> for BOTH a normal
    /// write and a drop-and-evict write under this FullMode — it cannot distinguish the two.
    /// </summary>
    void EmitEnvelope(AcpEventEnvelope envelope) {
        // Liveness-supervision spec §1: advance BEFORE the channel write below, never after — a
        // reader blocked on Envelopes.ReadAsync can wake and run the instant TryWrite makes the item
        // visible, on another thread, with no ordering relationship to whatever this thread does
        // next. Sequencing Advance() first (same-thread, so it is guaranteed complete before the
        // write that unblocks the reader) makes "the envelope was observed" a sound proof that the
        // clock already moved; the reverse order is a genuine race a fast reader can win, observing
        // the envelope before the seq bump (caught by ActivityClockTurnAndEnvelopeWiringTests under
        // load — see that test's remarks). Every emitted envelope (assistant text, tool calls, plans,
        // session-info/usage metadata) is activity, independent of whether a turn is currently
        // admitted — session_info_update/usage_update reach here with no turn in flight at all (see
        // AggregateUpdate's standalone-emit case). Advance even on the dropped-because-completed path
        // below: the content was genuinely produced, and by the time the channel is completed nothing
        // downstream is reading idle state for this agent anyway.
        ActivityClock?.Advance();

        // Named positively, so a metadata kind added later cannot count as the agent speaking by
        // accident: usage and session-info envelopes reach here with no turn in flight at all, and a
        // vendor that pings usage while producing nothing would otherwise read as a healthy turn.
        if (envelope.Kind is AcpEventKind.AssistantText or AcpEventKind.AssistantThinking
                          or AcpEventKind.ToolCall or AcpEventKind.ToolResult or AcpEventKind.Plan)
            Interlocked.Increment(ref _turnOutputEnvelopes);

        lock (_aggregationLock) {
            if (_transcript.Reader.Count >= _transcriptCapacity) {
                var dropped = Interlocked.Increment(ref _droppedTranscriptEnvelopes);
                _logger.LogWarning(
                    "ACP: transcript channel full (capacity={Capacity}) — dropping the oldest buffered envelope to make room for Kind={Kind}; {DroppedCount} dropped this session so far (the forwarder is likely stalled).",
                    _transcriptCapacity, envelope.Kind, dropped);
            }

            if (!_transcript.Writer.TryWrite(envelope))
                _logger.LogDebug("ACP: dropped an ACP transcript envelope (Kind={Kind}) — transcript channel already completed.", envelope.Kind);
        }
    }

    /// <summary>
    /// A real timestamp for every envelope this runtime emits (Seq itself stays a <c>0</c> placeholder
    /// — the forwarder assigns the real monotonic seq on dequeue). Uses <see cref="_timeProvider"/>
    /// (defaults to <see cref="TimeProvider.System"/>, overridable in tests for determinism) rather
    /// than <see cref="DateTimeOffset.UtcNow"/> directly.
    /// </summary>
    string NowIso() => _timeProvider.GetUtcNow().ToString("O");

    void RequireSessionId() {
        if (_sessionId is not { Length: > 0 })
            throw new InvalidOperationException("AcpHostedAgentRuntime.SendUserInputAsync called before StartAsync established a session.");
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Only emit the session-ended lifecycle event when a session actually started — a startup
        // failure (bad protocol version / handshake / session/new) disposes the runtime before
        // _sessionId is assigned, and pairing "ended" with a session that never started would make
        // the lifecycle telemetry incoherent. Contained: a logging fault must not skip teardown.
        if (_sessionId is { Length: > 0 } endedSessionId) {
            try { LogSessionEnded(_agentId, endedSessionId); }
            catch (Exception ex) { _logger.LogDebug(ex, "ACP: session-ended log failed during dispose for agent {AgentId}.", _agentId); }
        }

        // Set false only when the child's exit could not be confirmed — see below.
        var cleanupSafe = true;

        BeforeReconnectLockOnDisposeForTest?.Invoke();

        // Capture the CURRENT incarnation + owner AND publish the terminal/gate signals BEFORE any
        // throwing cancellation callback (finding 2, round 3). A fault in the owner-cancel below must
        // neither (a) select a STALE incarnation — leaking a reconnect successor committed since (a
        // successor can no longer be committed once _intentionalStop is set here: TryCommit throws
        // under this SAME lock) — nor (b) skip _gateOpen / the interaction sweep / _runtimeTerminal,
        // which unpark parked send/read/finalizer work. So `installed` is assigned ONLY under the lock
        // (never a pre-lock read that a Cancel throw could strand), and every signal fires before the
        // cancel.
        //
        // Intentional stop is marked FIRST (reconnect spec §9): a concurrent crash cannot resurrect a
        // disposing runtime, an in-flight owner's next checkpoint unwinds, and the send gate opens into
        // the terminal path so a parked worker never waits on a gate nobody will reopen.
        Incarnation installed;
        CancellationTokenSource? ownerCts;
        List<PendingInteraction>? swept;

        lock (_reconnectLock) {
            _intentionalStop = true;
            _phase           = (int)RuntimePhase.Terminal;
            installed        = _installed; // successor-consistent: the CURRENT incarnation, final under _intentionalStop
            ownerCts         = _ownerCts;  // captured to cancel OUTSIDE the lock, below
            swept            = MarkPendingInteractionsCancelledLocked();
            _gateOpen.TrySetResult();
        }

        ScheduleInteractionSweep(swept);
        _runtimeTerminal.TrySetResult();
        installed.Connection.OnNotification -= HandleNotification;

        // ── Early cancellation/drain phase ── CONTAINED (finding 2). The owner cancel and
        // _cts.CancelAsync() run cancellation callbacks that can throw; a fault must not skip the
        // guaranteed teardown below (the child/streams would leak with the _disposed latch set). The
        // owner is cancelled here — OUTSIDE _reconnectLock and AFTER the terminal/gate signals — so a
        // throwing owner callback can neither run under the lock nor strand waiters, and teardown still
        // runs against the successor-consistent `installed` captured above.
        try {
            // INDEPENDENTLY guarded (Bug 1): the owner cancel and _cts.CancelAsync() each run a
            // cancellation callback that can throw (the whole reason finding 2 exists). Their faults
            // must not skip the worker-unblock below — cancelling _cts AND completing _pendingTurns are
            // exactly the two signals RunTurnWorkerAsync exits on (it parks on
            // _pendingTurns.WaitToReadAsync(_cts.Token)); skipping them would leave the worker parked
            // while the guaranteed teardown disposes its connection/process out from under it.
            try { ownerCts?.Cancel(); }
            catch (Exception ex) { _logger.LogDebug(ex, "ACP: owner cancel faulted during dispose for agent {AgentId}.", _agentId); }

            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "ACP: shutdown-token cancel faulted during dispose for agent {AgentId}.", _agentId); }

            _updates.Writer.TryComplete();
            _pendingTurns.Writer.TryComplete();

            // Best-effort, bounded: the owner's own finally disposes any candidate it still holds; a
            // stuck owner must never hang dispose.
            try {
                await _ownerTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            } catch {
                // Best-effort.
            }

            // The turn worker's in-flight SendPromptAsync await is keyed off _cts.Token via
            // AcpConnection.RequestAsync's own cancellation registration, so cancelling _cts above
            // already unblocks it — ProcessAdmittedTurnAsync's own `finally` still runs a courtesy flush of that
            // turn's partial buffer (see FlushOpenRun) before the worker loop observes the cancellation
            // and returns. This is just a bounded wait for that to actually happen.
            try {
                await _turnWorkerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch {
                // Best-effort — a stuck turn worker must never hang dispose.
            }

            // Session-end flush: a belt-and-suspenders flush of any still-open aggregation run. In the
            // normal shutdown path ProcessAdmittedTurnAsync's own finally already flushed the active turn's
            // buffer above, making this a no-op — it only matters for the (currently unreachable in
            // practice) case where the worker task itself never ran a turn to begin with.
            FlushOpenRun();
            _transcript.Writer.TryComplete();

            try {
                await installed.LoopTask.ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // expected shutdown path
            }

            _cts.Dispose();
        } catch (Exception ex) {
            // The early cancellation/drain phase faulted — proceed to the guaranteed teardown below
            // regardless, so the child and its streams are still disposed (finding 2).
            _logger.LogDebug(ex, "ACP: dispose early phase faulted; proceeding to guaranteed teardown for agent {AgentId}.", _agentId);
        }

        // ── Guaranteed teardown: ALWAYS reached, even when the early phase above faulted (finding 2).
        // In a finally-equivalent position because _disposed is latched at the top: if connection or
        // process disposal faults, a retry returns immediately and the callback would never run at
        // all. For the Kiro reviewer that callback deletes the transcript-bearing home, so skipping it
        // on a faulted teardown is precisely the leak this hook exists to close.

        // UNCONDITIONAL, independent of _onDisposed: a claimed reap's Verdict is published
        // synchronously at claim time (TryStartReap), but "post-disposal the verdict is final" also
        // needs the reap's TASK to have settled before any caller (the factory's launch-failure
        // catch) treats disposal as done — otherwise a no-hook (cursor-shaped) runtime could return
        // from DisposeAsync while an in-flight reap is still tearing down the same process disposal
        // is about to touch below. Design spec §3.2: this was previously gated on _onDisposed is not
        // null, which is a Kiro/OpenCode-only concern (the isolated-home cleanup hook) that has
        // nothing to do with whether a reap needs awaiting.
        if (TakeReap() is { } reap) {
            try {
                // Bounded defensively, same rationale and value as the exit-confirmation wait below:
                // production reap tasks bottom out in AcpChildProcess.TerminateAsync, which is
                // self-bounded (2-5s callers), so this isn't live today — but the await is now
                // unconditional for every vendor, and a future/test IAcpProcess whose TerminateAsync
                // ignores its own timeout must not be able to wedge disposal outright. A reap that
                // won't settle is a "didn't confirm" signal, not a reason to hang teardown.
                await reap.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            } catch (Exception ex) {
                // The reap's OWN outcome is already logged; this is a SEPARATE, symmetric Debug
                // line for the bound itself (matching the exit-confirmation wait below, which also
                // never escalates past Debug) — a reap that hasn't settled within 5s during
                // disposal is a real anomaly worth a daemon-log line, not a reason to hang or fault
                // teardown.
                _logger.LogDebug(ex, "ACP: reap task did not settle before disposal completed.");
            }
        }

        // BEFORE disposing anything, when the child is still observable. AcpChildProcess.HasExited
        // reports true as soon as the underlying Process is disposed, so asking afterwards mistakes
        // "no longer observable" for "confirmed exited" — the opposite of what this gate is for.
        if (_onDisposed is not null) {
            try {
                // Bounded HERE with WaitAsync rather than by trusting the timeout argument: the
                // interface takes one but does not oblige an implementation to honour it, and the
                // test doubles return a task completing only on an explicit exit signal — so relying
                // on the parameter hung every suite that disposes a fake.
                await installed.Process.WaitForExitAsync(TimeSpan.FromSeconds(5))
                                       .WaitAsync(TimeSpan.FromSeconds(5))
                                       .ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "ACP: could not confirm child exit before post-dispose cleanup.");
            }

            // Unconfirmed means SKIP the deletion, not force it: deleting under a live reviewer would
            // leave it writing into an unlinked path and recreating the directory, which is worse
            // than leaving it. The epoch-keyed startup sweep collects it on the next boot.
            if (!installed.Process.HasExited) {
                _logger.LogWarning(
                    "ACP: child for agent {AgentId} did not confirm exit; leaving its reviewer home "
                  + "for the startup sweep rather than deleting it under a live process.", _agentId);

                cleanupSafe = false;
            }
        }

        try {
            // NESTED, not sequential in one try: a faulting Connection.DisposeAsync would otherwise
            // skip Process.DisposeAsync entirely.
            try {
                await installed.Connection.DisposeAsync().ConfigureAwait(false);
            } finally {
                await installed.Process.DisposeAsync().ConfigureAwait(false);
            }
        } finally {
            try {
                if (cleanupSafe) _onDisposed?.Invoke();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "ACP: post-dispose cleanup failed for agent {AgentId}.", _agentId);
            }
        }
    }

    // ── Reconnect/resume (skip-whole-replay — docs/superpowers/specs/2026-08-04-ai1325-acp-reconnect-resume-design.md) ──

    /// <summary>Whether a crash on the INSTALLED incarnation may open a reconnect incident. Caller
    /// holds the reconnect lock. Conjunctive with the construction-time gate (probe-verified
    /// vendor, interactive launch, kill switch — those decide whether <see cref="_reconnect"/> is
    /// non-null at all): the handshake must actually have advertised <c>loadSession</c>, and the
    /// session must be under its counted-resume cap.</summary>
    bool EligibleForReconnectLocked() =>
        _reconnect is not null
     && SupportsLoadSession
     && _sessionId is { Length: > 0 }
     && _resumeCount < _reconnect.MaxResumesPerSession;

    /// <summary>
    /// The single crash entry point (reconnect spec §5.2) — invoked by the connection's pre-fault
    /// hook (stamped via closure), the process-exit watcher, and the read-loop wrapper's finally;
    /// synchronous and non-blocking (takes only the reconnect lock). Four-arm dispatch: stale
    /// stamp / intentional stop → no-op; Running + eligible → open the incident (close the gate,
    /// start suppression, snapshot the in-flight registration, schedule the owner); Running +
    /// ineligible → today's terminal path; already Reconnecting → duplicate-signal idempotence via
    /// <see cref="_lastHandledCrashIncarnation"/>, or the id-qualified chained-crash marker for a
    /// committed successor's death.
    /// </summary>
    void HandleTransportEnded(long incarnationId) {
        var  scheduleOwner = false;
        var  terminal      = false;
        List<PendingInteraction>? swept = null;

        lock (_reconnectLock) {
            if (Phase == RuntimePhase.Terminal) {
                // Terminal already has an owner — HandleTransportEnded's own ineligible arm,
                // TerminalizeIncidentAsync, or DisposeAsync — and THAT owner completes the signals
                // after its cleanup settles. Completing here would defeat the retire-first,
                // signal-afterward ordering: the terminal path's own connection disposal re-enters
                // this method mid-retirement (code-review r3). Inert.
                return;
            } else if (incarnationId != _installed.Id) {
                // A candidate's or disposed incarnation's signal — structurally inert (§5.1), and
                // deliberately checked BEFORE any stop handling (code-review r2): a delayed stale
                // callback arriving after a stop of the healthy successor must not become a
                // premature finalize trigger while that successor's own termination is still in
                // flight.
                return;
            } else if (Phase == RuntimePhase.Reconnecting) {
                // While an incident is in flight, its atomically-published owner is the sole
                // terminal authority — including under intentional stop (code-review r3: the
                // corpse's SECOND signal still carries the installed stamp here, and a stop arm
                // ahead of this one signalled finalize while the owner still held a candidate).
                // Stop cancels the owner's token; the owner's finally terminalizes and completes
                // the signals after ITS cleanup.
                if (incarnationId == _lastHandledCrashIncarnation)
                    return; // duplicate signal for the crash already being handled (r4 B1)

                if (_intentionalStop)
                    return; // the owner's unwind owns completion

                // A committed successor died during the settlement/note/reopen window: chain it
                // into the SAME incident — never a second owner, never a discarded crash (r3 B1).
                _crashedAgainIncarnation = incarnationId;
                LogCrashedAgain(_agentId, incarnationId);
                return;
            } else {
                // Phase == Running: no owner exists, so this arm owns the disposition.
                if (_intentionalStop) {
                    // The INSTALLED child's transport ended under an intentional stop: fire the
                    // terminal signals (code-review r1: TerminateAsync from Running otherwise left
                    // ReadOutputAsync waiting forever — the finalize trigger the old process-exit
                    // wait used to fire). Only valid in Running — in Reconnecting the owner
                    // completes (above), and its cleanup must not be overtaken.
                    terminal = true;
                }
                else {
                _lastHandledCrashIncarnation = incarnationId;

                if (!EligibleForReconnectLocked()) {
                    _phase = (int)RuntimePhase.Terminal;
                    _gateOpen.TrySetResult();
                    swept    = MarkPendingInteractionsCancelledLocked();
                    terminal = true;
                } else {
                    _phase                 = (int)RuntimePhase.Reconnecting;
                    // Counter reset BEFORE the volatile flag publishes (code-review r1 minor): a
                    // notification that observes the flag can only increment a counter that has
                    // already been zeroed for this incident.
                    _suppressedUpdates     = 0;
                    _suppressNotifications = true;
                    _gateOpen              = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    // The C8 snapshot (§5.3/§7): entered/written ⇒ the surfaced cases ⇒ the note's
                    // resend sentence. A not-started registration will park at write entry and be
                    // delivered automatically — no sentence.
                    _incidentResendSentence = _inFlight is { WriteState: >= InFlightTurn.Entered };
                    _ownerCts              = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    swept                  = MarkPendingInteractionsCancelledLocked();
                    // Owner PUBLICATION happens inside the lock (code-review r1: publishing after
                    // release let DisposeAsync snapshot a stale completed _ownerTask in the gap and
                    // return while the new owner still ran). Task.Run only schedules — the hook
                    // stays non-blocking.
                    _ownerTask    = Task.Run(RunReconnectOwnerAsync);
                    scheduleOwner = true;
                }
                }
            }
        }

        ScheduleInteractionSweep(swept);

        if (terminal) {
            // Today's behavior, byte-for-byte in effect: the transport is gone and no reconnect
            // will absorb it, so the re-keyed terminal signal fires here — the same moment the old
            // process-exit wait used to. TryComplete/TrySetResult are idempotent against the
            // dispose/terminalize paths.
            _updates.Writer.TryComplete();
            _runtimeTerminal.TrySetResult();
            return;
        }

        if (scheduleOwner)
            LogReconnectStarted(_agentId, _vendor, incarnationId);
    }

    /// <summary>
    /// The reconnect owner — exactly one per incident (chained successor crashes fold back into
    /// this same loop, reconnect spec §5.2/§6.4). Up to 3 candidate spawns at t=0/+1s/+4s; a
    /// `session/load` refusal, protocol downgrade, withdrawn `loadSession`, unconfirmed corpse
    /// retirement, or settlement timeout is terminal for the incident without further attempts.
    /// Every step runs under the owner's stop-cancellable token and re-checks the chained-crash
    /// marker at each checkpoint.
    /// </summary>
    async Task RunReconnectOwnerAsync() {
        var support = _reconnect!;
        var ownerCt = _ownerCts!.Token;
        var reason  = "exhausted";
        var resumed = false;
        Incarnation? candidate = null;
        // The PID-callback bundle each candidate actually RECORDED with (code-review r3): cleanup
        // must clear through the same generation that recorded, and clear nothing when the record
        // never happened — re-reading support.PidCallbacks at cleanup time could observe a
        // newly-wired real bundle and delete the ORIGINAL agent's record for a candidate that
        // recorded nothing under the throwing placeholder.
        AcpPidRecordCallbacks? recordedWith = null;

        try {
            var attempts = 0;

            while (true) {
                ownerCt.ThrowIfCancellationRequested();
                ConsumeChainMarker();

                // Step 0 (§6.1): retire the corpse — confirmed exit is a precondition for every
                // candidate handshake; an unconfirmed old tree is terminal, never shrugged past.
                if (!await RetireInstalledAsync(support).ConfigureAwait(false))
                    throw new AcpReconnectTerminalException("retirement_unconfirmed",
                        "old ACP child's process tree could not be confirmed exited");

                if (attempts >= 1 + support.AttemptDelays.Count)
                    break; // exhausted

                if (attempts > 0)
                    await Task.Delay(support.AttemptDelays[attempts - 1], support.TimeProvider, ownerCt).ConfigureAwait(false);

                attempts++;
                ownerCt.ThrowIfCancellationRequested();

                try {
                    candidate = SpawnCandidate(support);

                    // §6.2 step 1: the durable PID record precedes ANY handshake — a spawned child
                    // the daemon cannot durably record must not proceed (leak containment: if the
                    // daemon dies mid-attempt, restart reclamation kills the recorded candidate).
                    // A throw here lands in the attempt-failure catch below, which disposes the
                    // candidate WITHOUT clearing (recordedWith stays null — nothing was recorded).
                    // The bundle is snapshotted once and carried: Record and any later Clear must
                    // be the same generation (code-review r3).
                    var attemptPidCallbacks = support.PidCallbacks;
                    attemptPidCallbacks.Record(candidate.Process.Pid);
                    recordedWith = attemptPidCallbacks;

                    await InitializeCandidateAsync(candidate, ownerCt).ConfigureAwait(false);
                    await LoadSessionAsync(candidate, ownerCt).ConfigureAwait(false);
                    await ReapplyModelAsync(candidate, ownerCt).ConfigureAwait(false);

                    ownerCt.ThrowIfCancellationRequested();

                    if (!TryCommit(candidate)) {
                        await DisposeCandidateAsync(candidate, recordedWith).ConfigureAwait(false);
                        candidate = null;
                        continue; // attempt failed (candidate died pre-install)
                    }

                    candidate = null; // installed — ownership transferred to the runtime

                    // Deterministic seam for the chained-crash tests: fires at the exact
                    // post-commit, pre-settlement instant a successor death takes the §5.2
                    // chained arm. Production never sets it.
                    TestHookAfterCommit?.Invoke();

                    // Settlement (§6.4): the faulted incident turn — including its retained
                    // partial flush — must complete before the note, so transcript order is
                    // deterministic: partial flush → note → held turn → queued turns. Bounded via
                    // a cancelled WAIT (never an abandoned one — a timed-out-but-still-pending
                    // semaphore wait would later acquire a permit nobody releases and deadlock the
                    // worker); a pathological hang goes terminal rather than waiting forever.
                    using (var settleTimeout = new CancellationTokenSource(support.SettlementWait, support.TimeProvider))
                    using (var settle = CancellationTokenSource.CreateLinkedTokenSource(ownerCt, settleTimeout.Token)) {
                        try {
                            await _turnExecutionGate.WaitAsync(settle.Token).ConfigureAwait(false);
                            _turnExecutionGate.Release();
                        } catch (OperationCanceledException) when (settleTimeout.IsCancellationRequested && !ownerCt.IsCancellationRequested) {
                            throw new AcpReconnectTerminalException("settlement_timeout",
                                "the interrupted turn never settled after reconnect commit");
                        }
                    }

                    if (ChainMarkerSet())
                        continue; // the just-committed successor already died — loop retires it

                    EmitSystemNote();

                    if (TryReopen()) {
                        resumed = true;
                        return;
                    }

                    // Reopen refused: either a chained crash (loop continues, retiring the dead
                    // successor) or stop (the throw below unwinds via the cancellation check).
                    ownerCt.ThrowIfCancellationRequested();
                } catch (AcpReconnectTerminalException) {
                    throw;
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    LogAttemptFailed(ex, _agentId, attempts);

                    if (candidate is not null) {
                        await DisposeCandidateAsync(candidate, recordedWith).ConfigureAwait(false);
                        candidate = null;
                    }
                }
            }
        } catch (OperationCanceledException) {
            reason = "stopped";
        } catch (AcpReconnectTerminalException ex) {
            reason = ex.Reason;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "ACP reconnect owner ended unexpectedly for agent {AgentId}.", _agentId);
            reason = "owner_fault";
        } finally {
            if (!resumed)
                await TerminalizeIncidentAsync(reason, candidate, recordedWith, support).ConfigureAwait(false);
        }
    }

    bool ChainMarkerSet() {
        lock (_reconnectLock) return _crashedAgainIncarnation != -1;
    }

    /// <summary>Records a chained successor crash as handled (spec §5.2): the marker's id becomes
    /// <see cref="_lastHandledCrashIncarnation"/>, so that dead successor's duplicate signals
    /// no-op from here on, and the marker clears for the next successor.</summary>
    void ConsumeChainMarker() {
        lock (_reconnectLock) {
            if (_crashedAgainIncarnation == -1)
                return;

            _lastHandledCrashIncarnation = _crashedAgainIncarnation;
            _crashedAgainIncarnation     = -1;
        }
    }

    /// <summary>
    /// Step 0 (§6.1): retire the installed corpse — cancel its read loop, dispose its connection,
    /// terminate its process TREE, and CONFIRM exit within the bounded wait. Idempotent (a second
    /// entry finds the work done); runs entirely outside the reconnect lock. Returns false when
    /// exit cannot be confirmed — terminal for the incident, because `session/load` must never race
    /// a possibly-live prior owner (the measured Kiro-lock failure mode).
    /// </summary>
    async Task<bool> RetireInstalledAsync(AcpReconnectSupport support) {
        Incarnation corpse;
        lock (_reconnectLock) corpse = _installed;

        try { corpse.LoopCts.Cancel(); } catch (ObjectDisposedException) { /* already retired */ }

        try {
            await corpse.Connection.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: disposing the old connection failed; continuing retirement.");
        }

        await corpse.Process.TerminateAsync(support.RetirementWait).ConfigureAwait(false);

        var confirmed = corpse.Process.HasExited;
        LogCorpseRetired(_agentId, corpse.Id, confirmed);
        return confirmed;
    }

    /// <summary>
    /// §6.2 steps 2–4: invoke the pure spawn closure, assign the next never-reused incarnation id,
    /// and wire the candidate exactly like the original launch (hook stamped with its own id —
    /// inert until installed; suppressed notifications; the state-derived interaction router). The
    /// candidate's read loop starts here so its handshake responses can resolve. The durable PID
    /// record (step 1) is written by the OWNER immediately after this returns, so its failure path
    /// flows through the same attempt-failure disposal as every other candidate fault.
    /// </summary>
    Incarnation SpawnCandidate(AcpReconnectSupport support) {
        var (input, output, process) = support.Spawn();
        var connection = new AcpConnection(input, output, _logger, _debugFrames);

        var candidate = new Incarnation {
            Id         = Interlocked.Increment(ref _nextIncarnationId),
            Connection = connection,
            Process    = process,
            LoopCts    = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
        };

        WireIncarnation(candidate);
        candidate.LoopTask = RunIncarnationLoopAsync(candidate);
        _ = WatchProcessExitAsync(candidate);

        return candidate;
    }

    static async Task InitializeCandidateAsync(Incarnation candidate, CancellationToken ct) {
        // Must advertise the SAME capability set as StartAsync's initialize — a reconnect
        // candidate that silently dropped the elicitation advertisement would flip the agent
        // back to never asking, mid-session.
        var initializeParams = JsonSerializer.SerializeToElement(
            new InitializeParams(
                ProtocolVersion: 1,
                ClientCapabilities: new ClientCapabilities(
                    Fs: new FsCapabilities(ReadTextFile: false, WriteTextFile: false),
                    Terminal: false,
                    Elicitation: new ElicitationCapabilities(Form: new ElicitationFormCapabilities()))),
            CapacitorJsonContext.Default.InitializeParams);

        var resultElement = await candidate.Connection.RequestAsync("initialize", initializeParams, ct).ConfigureAwait(false);

        InitializeResult? result;
        try {
            result = JsonSerializer.Deserialize(resultElement.GetRawText(), CapacitorJsonContext.Default.InitializeResult);
        } catch (JsonException) {
            result = null;
        }

        if (result is null || result.ProtocolVersion != 1)
            throw new AcpReconnectTerminalException("protocol_mismatch",
                "reconnect candidate negotiated an unsupported ACP protocol version");

        if (result.AgentCapabilities is not { LoadSession: true })
            throw new AcpReconnectTerminalException("load_session_withdrawn",
                "reconnect candidate no longer advertises loadSession");
    }

    async Task LoadSessionAsync(Incarnation candidate, CancellationToken ct) {
        var loadParams = JsonSerializer.SerializeToElement(
            new SessionLoadParams(SessionId: _sessionId!, Cwd: _cwd!, McpServers: _mcpServersForResume),
            CapacitorJsonContext.Default.SessionLoadParams);

        try {
            _lastLoadResult = await candidate.Connection.RequestAsync("session/load", loadParams, ct).ConfigureAwait(false);
        } catch (AcpRpcException ex) {
            // A JSON-RPC refusal is terminal for the incident (§6): both measured refusal classes
            // (Kiro's durable stale-owner lock, Gemini's unpersisted session) do not clear with
            // retries — a session the vendor refuses to load will not become loadable seconds later.
            throw new AcpReconnectTerminalException("load_refused",
                SanitizeForForward($"session/load refused: {ex.Message}"));
        }

        AcpMetrics.SessionsLoaded.Add(1);
    }

    /// <summary>The most recent successful `session/load` RESPONSE — the model re-application's
    /// resolution source (it carries the same modes/models shape as `session/new`). Owner-only.</summary>
    JsonElement _lastLoadResult;

    /// <summary>Best-effort model re-application (§6.2 step 7): the load RESPONSE carries the same
    /// modes/models shape as `session/new` (probe-verified for Cursor), so the vendor's own
    /// selector resolves against it; failure falls back to the loaded session's current model,
    /// exactly like launch. A no-op for `NoOpModelSelector` vendors or when no model was
    /// requested.</summary>
    async Task ReapplyModelAsync(Incarnation candidate, CancellationToken ct) =>
        _resolvedModel = await _modelSelector
            .TrySelectAsync(candidate.Connection, _sessionId!, _lastLoadResult, _requestedModel, _logger, ct)
            .ConfigureAwait(false) ?? _resolvedModel;

    /// <summary>
    /// Commit (§6.3), under the reconnect lock: stop re-check, candidate liveness via the
    /// TRANSPORT-ENDED LATCH (set strictly before any hook fires, so a death whose only hook
    /// invocation was discarded as uninstalled is still visible here — r4 B2) plus process exit,
    /// then swap, unwire the corpse's notifications, and install the candidate's id. Returns false
    /// when the liveness check fails (the attempt fails normally). A death after the install fires
    /// the candidate's already-wired hook with the installed stamp and takes the chained-crash arm.
    /// </summary>
    bool TryCommit(Incarnation candidate) {
        lock (_reconnectLock) {
            if (_intentionalStop || Phase == RuntimePhase.Terminal)
                throw new OperationCanceledException("stop during reconnect commit");

            if (candidate.Connection.TransportEnded || candidate.Process.HasExited)
                return false;

            var corpse = _installed;
            corpse.Connection.OnNotification -= HandleNotification;

            _installed = candidate;
            return true;
        }
    }

    /// <summary>Envelopes that represent the agent actually saying something. Read only as a
    /// difference across a silence window — the absolute value means nothing.</summary>
    long _turnOutputEnvelopes;

    /// <summary>How long an interactive turn may produce nothing before the user is told. Long enough
    /// that an ordinary slow first token stays quiet (a rate-limited gemini backs off ~70s per
    /// attempt, so a shorter window would fire mid-wave on a turn that recovers).</summary>
    internal static readonly TimeSpan TurnSilenceWindow = TimeSpan.FromMinutes(3);

    /// <summary>Watches one turn for total wire silence and, once, says so. Never reaps: a silent turn
    /// is usually a vendor waiting out a retry it will win, and killing it would lose work the user
    /// cannot see is coming. Armed only where nothing else bounds the turn — a reviewer launch has
    /// <see cref="_firstOutputDeadline"/>, which does reap, and two voices on one silence would
    /// contradict each other. Nor into a review flow's transcript, which is consumed as the round's
    /// output — not every reviewer vendor carries that deadline, so the two conditions are separate.</summary>
    IDisposable ArmTurnSilenceNotice(PendingTurn turn) {
        if (_isReviewFlow || _firstOutputDeadline is not null) return NullDisposable.Instance;

        var cts = new CancellationTokenSource();
        _ = WatchForTurnSilenceAsync(turn, cts.Token);

        // Cancel, then dispose: disposing a CancellationTokenSource does not cancel its token, so
        // returning it directly would leave the watcher holding its delay for the whole window after
        // every turn.
        return new CancelOnDispose(cts);
    }

    sealed class CancelOnDispose(CancellationTokenSource cts) : IDisposable {
        public void Dispose() {
            cts.Cancel();
            cts.Dispose();
        }
    }

    async Task WatchForTurnSilenceAsync(PendingTurn turn, CancellationToken ct) {
        var before = Interlocked.Read(ref _turnOutputEnvelopes);

        try {
            await Task.Delay(TurnSilenceWindow, _timeProvider, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return; // the turn ended inside the window — nothing to report
        }

        // The delay can complete and queue its continuation while the turn is ending, so neither the
        // token nor the counter alone proves the turn is still running: a note published after the
        // agent answered would be a lie about the state it describes. The registration is the truth.
        lock (_reconnectLock) {
            if (_inFlight is not { } f || !ReferenceEquals(f.Turn, turn)) return;
        }

        if (ct.IsCancellationRequested) return;

        if (Interlocked.Read(ref _turnOutputEnvelopes) != before) return;

        // Behind the same gate the per-line drain uses: stderr can carry paths and prompt fragments,
        // and a stall is not a reason to widen a privacy decision made about the same bytes. Its
        // SIZE goes unconditionally, so an operator knows there is something to opt into.
        var diagnostics = _installed.Process.Diagnostics;

        if (_debugFrames && diagnostics is { Length: > 0 })
            LogTurnSilentWithStderr(_agentId, _vendor, TurnSilenceWindow.TotalMinutes, AcpDebugFrameLog.Cap(diagnostics));
        else
            LogTurnSilent(_agentId, _vendor, TurnSilenceWindow.TotalMinutes, diagnostics?.Length ?? 0);

        EmitEnvelope(new AcpEventEnvelope(
            Seq: 0,
            Kind: AcpEventKind.SystemNote,
            Text: $"No output from {_vendor} for {TurnSilenceWindow.TotalMinutes:0} minutes. The agent is still "
                + "running — vendors go quiet like this while retrying a rate-limited or unauthenticated model "
                + "call. The daemon log carries whatever the child reported.",
            TimestampIso: NowIso()));
    }

    sealed class NullDisposable : IDisposable {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }

    /// <summary>Tells the user, in the transcript itself, that the model they picked is not the one
    /// answering. Worded for every reason the selector returns null — no match in the published list,
    /// a list published in no shape it reads, or the agent refusing the selection RPC — since which
    /// one it was does not reach here.</summary>
    void EmitModelFallbackNote(string requestedModel) =>
        EmitEnvelope(new AcpEventEnvelope(
            Seq: 0,
            Kind: AcpEventKind.SystemNote,
            Text: $"{_vendor} could not apply the model '{requestedModel}'; this session is running {_vendor}'s default model instead.",
            TimestampIso: NowIso()));

    /// <summary>The §8 surfacing envelope — emitted after commit and settlement, before reopen,
    /// while the worker is still parked, so it deterministically precedes every resumed envelope.
    /// The resend sentence appears exactly for the incident's surfaced cases (a turn whose
    /// write-state reached `entered`/`written` at snapshot time).</summary>
    void EmitSystemNote() {
        var text = _incidentResendSentence
            ? "Agent process restarted; the session was resumed. Your last message may not have been processed — resend it if the agent doesn't continue."
            : "Agent process restarted; the session was resumed.";

        EmitEnvelope(new AcpEventEnvelope(
            Seq: 0,
            Kind: AcpEventKind.SystemNote,
            Text: text,
            TimestampIso: NowIso()));
    }

    /// <summary>
    /// The atomic reopen transition (§6.4, r4 B3): ONE lock-linearized operation that re-checks
    /// stop and the chained-crash marker and — only if clean — sets Running, ends suppression,
    /// counts the resume (the success linearization point: cap + metric + log increment here and
    /// only here), and opens the gate before releasing the lock. A crash callback serializing
    /// before this sets the marker and the reopen is refused; one serializing after observes
    /// Running and opens a fresh incident. No marker can be stranded across the reopen.
    /// </summary>
    bool TryReopen() {
        int suppressed;

        lock (_reconnectLock) {
            if (_intentionalStop || Phase == RuntimePhase.Terminal)
                throw new OperationCanceledException("stop during reconnect reopen");

            if (_crashedAgainIncarnation != -1)
                return false;

            _phase                 = (int)RuntimePhase.Running;
            _suppressNotifications = false;
            _resumeCount++;
            suppressed = _suppressedUpdates;
            _gateOpen.TrySetResult();
        }

        AcpMetrics.RecordReconnect("resumed");
        LogResumed(_agentId, _installed.Id, _resumeCount, suppressed);
        return true;
    }

    async Task DisposeCandidateAsync(Incarnation candidate, AcpPidRecordCallbacks? recordedWith) {
        try { candidate.LoopCts.Cancel(); } catch (ObjectDisposedException) { }

        // Each cleanup step is INDEPENDENTLY best-effort (code-review r2): a throwing connection
        // disposal must never suppress process-tree retirement — that ordering would leave an
        // untracked live child right before its PID record is cleared.
        try {
            await candidate.Connection.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: candidate connection disposal failed (best-effort).");
        }

        try {
            await candidate.Process.TerminateAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: candidate process termination failed (best-effort).");
        }

        try {
            await candidate.Process.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: candidate process disposal failed (best-effort).");
        }

        // Clear ONLY through the generation that recorded, and only when a record actually
        // happened (code-review r3 — a null recordedWith means the Record threw or never ran, so
        // there is nothing of ours to delete and the original agent's record must survive).
        try {
            recordedWith?.Clear();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: clearing the candidate PID record failed (best-effort).");
        }
    }

    /// <summary>
    /// The incident's terminal path (§6.4): gate opens INTO terminal (the parked worker observes
    /// Terminal and exits, faulting a held turn's ack honestly), any leftover candidate is
    /// disposed and its record cleared, and the re-keyed terminal signal finally fires so the
    /// orchestrator finalizes exactly as an ineligible crash does today — once.
    /// </summary>
    async Task TerminalizeIncidentAsync(
            string reason, Incarnation? leftoverCandidate, AcpPidRecordCallbacks? recordedWith,
            AcpReconnectSupport support) {
        List<PendingInteraction>? swept;
        Incarnation installed;

        lock (_reconnectLock) {
            _phase                 = (int)RuntimePhase.Terminal;
            _suppressNotifications = false;
            swept                  = MarkPendingInteractionsCancelledLocked();
            installed              = _installed;
            _gateOpen.TrySetResult();
        }

        ScheduleInteractionSweep(swept);

        if (leftoverCandidate is not null) {
            await DisposeCandidateAsync(leftoverCandidate, recordedWith).ConfigureAwait(false);
            recordedWith = null; // its clear (if any) just ran — the installed block below must not double-clear
        }

        // A successor that COMMITTED and then went terminal (settlement timeout, chained-crash
        // exhaustion) is the INSTALLED incarnation, not a leftover candidate. Run the FULL
        // retirement on it — loop cancel, connection disposal, tree termination with a bounded
        // confirm, and the PID-record clear — on the incident's own authority (code-review r1+r2),
        // BEFORE the terminal signals fire, so finalize never overlaps a still-live successor this
        // path knew about. Idempotent for the already-retired-original case (a corpse is already
        // dead and disposed), and the orchestrator's finalize->dispose remains the backstop for
        // anything a refusing process leaves behind (logged loudly below, never silently).
        try { installed.LoopCts.Cancel(); } catch (ObjectDisposedException) { }

        try {
            await installed.Connection.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: terminal-path connection disposal failed (best-effort).");
        }

        // Independent best-effort blocks (code-review r3 — same rule as DisposeCandidateAsync): a
        // throwing termination must not suppress process disposal, and neither may suppress the
        // record clear.
        try {
            await installed.Process.TerminateAsync(support.RetirementWait).ConfigureAwait(false);
            if (!installed.Process.HasExited)
                _logger.LogWarning(
                    "ACP reconnect: installed child (pid {Pid}) did not confirm exit during terminal-path retirement; the finalize path's dispose is the backstop.",
                    installed.Process.Pid);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: terminal-path termination of the installed child failed (best-effort).");
        }

        try {
            await installed.Process.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: terminal-path process disposal failed (best-effort).");
        }

        try {
            recordedWith?.Clear();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP reconnect: terminal-path PID-record clear failed (best-effort).");
        }

        AcpMetrics.RecordReconnect(reason == "stopped" ? "stopped" : "exhausted");
        LogGaveUp(_agentId, reason);

        _updates.Writer.TryComplete();
        _runtimeTerminal.TrySetResult();
    }

    // ── Pending-interaction registry + state-derived router (§5.4) ──────────────────────────────

    /// <summary>
    /// The state-derived interaction router — one per incarnation, installed at spawn, never
    /// swapped. Phase one, under the reconnect lock: (installed ∧ Running) or immediate decline,
    /// and — when admitted — atomic registration in the pending registry. Phase two, outside the
    /// lock: a lock-acquired may-start re-check (a filter, not an atomicity claim — the
    /// post-check/pre-invoke window's contract is surfaced-then-cancelled), then the real bridge
    /// under the entry's token, RACED against the sweep's bookkeeping signal so a blocked foreign
    /// cancellation callback can never strand the response or the entry's removal (r5–r8).
    /// Exactly one response per request is the connection layer's own guarantee; the entry's claim
    /// decides real-vs-cancelled.
    /// </summary>
    async Task<JsonElement?> RouteServerRequestAsync(long incarnationId, AcpRequest request, CancellationToken ct) {
        PendingInteraction entry;

        lock (_reconnectLock) {
            if (_intentionalStop || Phase != RuntimePhase.Running || incarnationId != _installed.Id)
                return DeclineFor(request);

            entry = new PendingInteraction(incarnationId);
            _pendingInteractions.Add(entry);
        }

        try {
            lock (_reconnectLock) {
                if (entry.Cancelled)
                    return DeclineFor(request); // swept between registration and start — never invoke the bridge
            }

            // NOT `using`: the linked source must outlive this method when the sweep wins the race,
            // because the sweep's best-effort token tail is what cancels the abandoned in-flight
            // bridge call (and with it the server-side card) — disposing here would sever that
            // propagation path. Disposal rides the bridge task's own settlement instead.
            var linked     = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.Cts.Token);
            var bridgeTask = _interactionBridge!.HandleAsync(request, linked.Token);
            _ = bridgeTask.ContinueWith(
                t => { _ = t.Exception; linked.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            var winner = await Task.WhenAny(bridgeTask, entry.CancelledSignal.Task).ConfigureAwait(false);

            bool claimedByBridge;
            lock (_reconnectLock) claimedByBridge = !entry.Cancelled && ReferenceEquals(winner, bridgeTask);

            if (!claimedByBridge)
                return DeclineFor(request); // the sweep owns the outcome (terminal-claim rule, §5.4)

            return await bridgeTask.ConfigureAwait(false);
        } finally {
            // Terminal bookkeeping is claim-winner-independent and callback-independent (r8 I1):
            // the entry leaves the registry here, always. The entry's own CTS is deliberately NOT
            // disposed — the sweep's tail may still need to signal it, and a per-interaction CTS
            // left to the GC is the documented cost of never severing that path.
            lock (_reconnectLock) _pendingInteractions.Remove(entry);
        }
    }

    /// <summary>Each method declines in ITS OWN protocol's result shape — the stabilized
    /// elicitation response (`{"action":"cancel"}`) is a different object from the permission
    /// outcome (the stabilized elicitation-lane contract of #453, honored here too). An
    /// elicitation declined
    /// by the router is an elicitation cancelled before routing to a human, so it carries the same
    /// reason-tagged metric the bridge's own pre-routing cancels do.</summary>
    JsonElement? DeclineFor(AcpRequest request) {
        switch (request.Method) {
            case "session/request_permission":
                return AcpInteractionBridge.CancelledResult();
            case "elicitation/create":
                AcpMetrics.RecordElicitationUnrenderable("runtime_not_serving");
                _logger.LogDebug(
                    "ACP: elicitation declined by the reconnect router (uninstalled incarnation or non-running phase) for agent {AgentId}.",
                    _agentId);
                return AcpInteractionBridge.ElicitationCancelResult();
            default:
                return null; // unclaimed methods keep the connection's -32601 default-decline posture
        }
    }

    /// <summary>Step one of the three-step sweep (§5.4): mark every live entry cancelled — plain
    /// field writes, no token signalled, no callback can run. Caller holds the reconnect lock;
    /// returns the marked set for the off-stack step two, or null when there was nothing to
    /// sweep.</summary>
    List<PendingInteraction>? MarkPendingInteractionsCancelledLocked() {
        if (_pendingInteractions.Count == 0)
            return null;

        var marked = new List<PendingInteraction>(_pendingInteractions.Count);

        foreach (var entry in _pendingInteractions) {
            if (entry.Cancelled)
                continue;

            entry.Cancelled = true;
            marked.Add(entry);
        }

        return marked.Count == 0 ? null : marked;
    }

    /// <summary>Steps two of the sweep, dispatched as separately supervised fire-and-forget work
    /// (never awaited by the owner or the pre-fault hook — r6 B1/r7 I2): per entry, the
    /// BOOKKEEPING signal first (a TCS completion with async continuations — no foreign code; this
    /// is what resolves the router's race and thus the response + entry removal), then the token —
    /// the one step that executes foreign callbacks — as a best-effort tail a blocked callback can
    /// strand without consequence (r8 I1).</summary>
    void ScheduleInteractionSweep(List<PendingInteraction>? marked) {
        if (marked is null)
            return;

        _ = Task.Run(() => {
            foreach (var entry in marked)
                entry.CancelledSignal.TrySetResult();

            foreach (var entry in marked) {
                try {
                    entry.Cts.Cancel();
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "ACP: pending-interaction token cancellation threw (best-effort tail).");
                }
            }
        });
    }

    // ── LoggerMessage source-generated methods ──────────────────────────────────────────────────
    // Payload-free by construction: ids, protocol/capability metadata, and the resolved model NAME
    // only — never prompt/assistant/tool content.

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP session started: agentId={AgentId} acpSessionId={AcpSessionId}")]
    partial void LogSessionStarted(string agentId, string acpSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP handshake OK: agentId={AgentId} protocolVersion={ProtocolVersion} loadSession={LoadSession} resolvedModel={ResolvedModel}")]
    partial void LogHandshakeOk(string agentId, int protocolVersion, bool loadSession, string? resolvedModel);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP hosted agent session ended: agentId={AgentId} acpSessionId={AcpSessionId}")]
    partial void LogSessionEnded(string agentId, string acpSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP turn started for agent {AgentId} (vendor={Vendor})")]
    partial void LogTurnStarted(string agentId, string vendor);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP turn ended for agent {AgentId} (vendor={Vendor})")]
    partial void LogTurnEnded(string agentId, string vendor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP turn for agent {AgentId} (vendor={Vendor}) has produced nothing for {Minutes} minutes; the child is still running and has written {StderrChars} chars to stderr (set KCAP_ACP_DEBUG_FRAMES=1 to log it).")]
    partial void LogTurnSilent(string agentId, string vendor, double minutes, int stderrChars);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP turn for agent {AgentId} (vendor={Vendor}) has produced nothing for {Minutes} minutes; the child is still running. Its recent stderr: {Diagnostics}")]
    partial void LogTurnSilentWithStderr(string agentId, string vendor, double minutes, string diagnostics);

    [LoggerMessage(Level = LogLevel.Error, Message = "ACP launch handshake wedged at stage '{Stage}': agentId={AgentId} did not advance within {CapSeconds}s — terminating the child.")]
    partial void LogLaunchStageTimeout(string agentId, string stage, double capSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP reconnect started: agentId={AgentId} vendor={Vendor} crashedIncarnation={Incarnation}")]
    partial void LogReconnectStarted(string agentId, string vendor, long incarnation);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP reconnect: corpse retired: agentId={AgentId} incarnation={Incarnation} exitConfirmed={Confirmed}")]
    partial void LogCorpseRetired(string agentId, long incarnation, bool confirmed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP reconnect: committed successor crashed before reopen (chained): agentId={AgentId} incarnation={Incarnation}")]
    partial void LogCrashedAgain(string agentId, long incarnation);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP reconnect: session resumed: agentId={AgentId} incarnation={Incarnation} resumeCount={ResumeCount} suppressedReplayUpdates={Suppressed}")]
    partial void LogResumed(string agentId, long incarnation, int resumeCount, int suppressed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP reconnect: giving up ({Reason}): agentId={AgentId} — finalizing the session")]
    partial void LogGaveUp(string agentId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACP reconnect: attempt {Attempt} failed for agentId={AgentId}")]
    partial void LogAttemptFailed(Exception ex, string agentId, int attempt);
}
