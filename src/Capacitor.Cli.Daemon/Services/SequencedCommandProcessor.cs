using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal enum SequencedKind { Launch, Stop }

/// <summary>The two un-sequenced server commands that share the sequenced lane (spec §3.3, one
/// execution domain). Deliberately a SEPARATE enum from <see cref="SequencedKind"/>: nothing about an
/// un-sequenced item participates in the epoch/watermark/identity-cache protocol, and a shared enum
/// would invite a call site to pass one where the other belongs.</summary>
internal enum UnsequencedKind { Launch, Stop }

/// <summary>The COMPLETE set of outcomes <see cref="SequencedCommandProcessor.SubmitUnsequenced"/> can
/// return (spec §3.3). <c>Committed</c> — the lane owns execution and fault containment; the caller adds
/// nothing. <c>Coalesced</c> — an equivalent item for the same (target, payload class, launch segment) is
/// already queued and has not started; nothing to do. <c>Refused</c> — the lane has stopped accepting
/// (daemon shutdown); the CALLER owns the consequence. <c>DroppedUnknownTarget</c> — a stop for an id
/// that is neither registered nor an in-flight launch, which would no-op at execution anyway; the
/// processor owns that drop log.</summary>
internal enum SubmitOutcome { Committed, Coalesced, Refused, DroppedUnknownTarget }

internal readonly record struct SequencedItem(
    SequencedKind Kind, string Epoch, long Seq, string CommandId, string AgentId);

/// <summary>One un-sequenced launch/stop committed onto the serial lane. <paramref name="PayloadKey"/>
/// is the coalescing payload CLASS for stops (constant for the legacy agent-id-only stop; a force flag
/// would add a second class) and is unused for launches, which never coalesce — two launches for one id
/// are two distinct instances.</summary>
internal readonly record struct UnsequencedItem(
    UnsequencedKind Kind, string AgentId, string PayloadKey, Func<Task> Execute);

internal readonly record struct CommandOutcome(
    CommandOutcomeKind Kind, string? AgentId = null, string? SessionId = null, CommandRejectedReason? RejectReason = null);

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.2; parent §5.5): the daemon's two-lane sequenced
/// command handler. Exactly two command types are sequenced (Seq'd LaunchAgentCommand + StopAgentV2),
/// executed strictly serially per epoch. Acceptance (bump HighestAcceptedSeq + cache entry + enqueue)
/// is one atomic operation under <c>_lock</c>; LastProcessedSeq is the contiguous terminal prefix
/// (advances only on a terminal outcome). Self-contained + delegate-injected so it is unit-testable
/// with no live orchestrator (mirrors OrphanReaper/AgentKillQuarantine).
///
/// <para>Spec §3.3 (ONE execution domain) widens this from "the sequenced lane" to "the daemon's single
/// server-command execution lane". <see cref="SubmitUnsequenced"/> commits un-sequenced launches and
/// stops onto the SAME serial lane — no seq, no identity cache, no acks — so cross-format arrival order
/// holds by construction. The shipped server mixes formats permanently BY DESIGN (§1.9: the sequenced
/// tuple rides only the review-flow settlement lane, while ordinary launches and every stop are
/// un-sequenced), so nothing here may ever refuse a command for its FORMAT. Both submission entry points
/// decide everything — admissibility, active-launch tracking, launch-aware key clearing, coalescing and
/// the lane write — inside ONE critical section (<c>_lock</c>) before returning, which is what makes the
/// arrival-order guarantee hold in both directions and makes "commit and refuse" mutually exclusive
/// under a shutdown race.</para>
///
/// <para>Item-mutation invariant: a <see cref="LaneItem"/>'s tracking fields
/// (<see cref="LaneItem.ActiveLaunchId"/>, <see cref="LaneItem.PendingStopKey"/>) are written and read
/// ONLY under <c>_lock</c>. A submitter may therefore still set them AFTER the channel write, because the
/// lane's very first act on a dequeued item is to take <c>_lock</c>. The immutable shape fields are set
/// before the write and never touched again.</para>
/// </summary>
internal sealed class SequencedCommandProcessor : IAsyncDisposable {
    sealed class CacheEntry {
        public required string CommandId;
        // Settlement lost-ack redelivery (D1): the item's own identity, retained so a settled entry can build/re-send its
        // terminal ack at status-tick / reconnect time without the original SequencedItem in hand.
        // Epoch is the processor-wide _epoch; Seq is also the cache key, kept here for self-contained
        // rebuilds.
        public required long Seq;
        public required string AgentId;
        public bool Processed;
        public CommandOutcome Outcome;
        // Settlement lost-ack redelivery (D1): the SINGLE published terminal ack. Frozen once (get-or-freeze) so the proactive
        // send, a duplicate-replay, and every tick re-delivery all carry byte-identical liveness — a
        // live-liveness read that changed between two sends can never make two acks for one command
        // disagree. Null until the first successful freeze (a throwing liveness read defers it).
        public CommandAck? FrozenAck;
    }

    /// <summary>One lane entry. EXACTLY ONE shape is populated — sequenced (identity + terminal-answer
    /// machinery + a per-item execution-completion task) or un-sequenced (a bare delegate, no reply
    /// surface). A CLASS, not a struct: reference identity is the coalescing key's identity guard, so an
    /// older item starting can never clear a newer segment's key.</summary>
    sealed class LaneItem {
        public SequencedItem Sequenced;
        public Func<Task<CommandOutcome>>? SequencedExecute;
        public TaskCompletionSource? Done;
        public UnsequencedItem Unsequenced;

        /// <summary>Non-null while this item holds ONE active-launch instance for that id. Nulled by the
        /// single terminal-finalization path, which is what makes retirement idempotent.</summary>
        public string? ActiveLaunchId;

        /// <summary>Non-null while this item is the queued entry a same-payload stop would coalesce
        /// onto. Cleared (with the counter decrement) when the item is dequeued to START, so a
        /// same-payload retry after a started/faulted stop commits a FRESH item.</summary>
        public (string AgentId, string PayloadKey)? PendingStopKey;

        public bool IsSequenced => SequencedExecute is not null;

        public static LaneItem ForSequenced(
                SequencedItem item, Func<Task<CommandOutcome>> execute, TaskCompletionSource done) =>
            new() { Sequenced = item, SequencedExecute = execute, Done = done };

        public static LaneItem ForUnsequenced(UnsequencedItem item) => new() { Unsequenced = item };
    }

    /// <summary>§3.3: queued known-target stops are LOSSLESS while the lane is accepting — losing one
    /// would leave an agent running that the command would have torn down. Depth is therefore an ALARM,
    /// never a cap. Edge-triggered with hysteresis: one Error on crossing
    /// <see cref="StopQueueAlarmThreshold"/>, quiet during further growth, re-armed only after draining
    /// below <see cref="StopQueueAlarmRearmBelow"/>, and never more often than
    /// <see cref="StopQueueAlarmMinInterval"/> — so boundary oscillation cannot turn the alarm into its
    /// own failure mode.</summary>
    internal const int StopQueueAlarmThreshold  = 256;
    internal const int StopQueueAlarmRearmBelow = 128;
    internal static readonly TimeSpan StopQueueAlarmMinInterval = TimeSpan.FromSeconds(60);

    readonly string _epoch;
    readonly Func<string, AgentLiveness> _readLiveness;
    readonly Func<string, bool> _isKnownStopTarget;
    readonly Func<CommandAck, Task> _sendAck;
    readonly Func<CommandRejected, Task> _sendRejected;
    readonly ILogger _logger;
    readonly int _cacheBound;
    readonly TimeProvider _time;
    readonly Task? _startBarrier;
    readonly CancellationTokenSource _laneShutdown = new();

    readonly object _lock = new();
    long _highestAcceptedSeq;
    long _lastProcessedSeq;
    long _lastAckedPrefix;
    readonly Dictionary<long, CacheEntry> _cache = new();

    // ── §3.3 one-execution-domain state. Read and written ONLY under _lock. ──────────────────────────
    // Active launch INSTANCES per agent id, reference-counted: a stop is admissible for an id with a
    // launch in flight (dequeued and parked at the consent gate, say) even though no registry entry
    // exists yet, and the id stays admissible until the LAST instance settles — no id-non-reuse
    // assumption anywhere.
    readonly Dictionary<string, int> _activeLaunches = new(StringComparer.Ordinal);
    // The queued un-seq'd stop a same-(target, payload class) submission coalesces onto, per launch
    // segment (a launch commit for the id clears its keys, so a post-launch stop starts a new segment).
    readonly Dictionary<(string AgentId, string PayloadKey), LaneItem> _pendingStops = [];
    int _queuedStops;
    int _queuedStopsHighWater;
    bool _stopAlarmArmed = true;
    // Monotonic (TimeProvider timestamp), never wall-clock: a UTC step must not stretch or
    // shrink the minimum interval between emitted alarms. Null = never emitted — a separate
    // state, not a reserved value, because zero is a legal timestamp (a fake provider's origin).
    long? _lastStopAlarmAtTimestamp;
    // Set with the writer completion in the same critical section, so a shutdown race can never both
    // commit an item and refuse it.
    bool _closed;
    // Queued + in-flight items, for the test-only quiescence seam. Not a bound and not a metric.
    int _outstanding;
    TaskCompletionSource? _idle;

    readonly Channel<LaneItem> _lane = Channel.CreateUnbounded<LaneItem>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _laneTask;
    int _disposed;

    /// <param name="isKnownStopTarget">§3.3 stop-admission probe — whether this id is a real stop target
    /// OUTSIDE the in-flight-launch set (the orchestrator's registry plus its durable PID records). Called
    /// INSIDE <c>_lock</c>, so it must be cheap and non-blocking. Defaults to fail-closed
    /// ("no target surface": only ids with an active launch instance are admissible) — a processor that
    /// serves real traffic must always pass one.</param>
    /// <param name="startBarrier">§3.3 transition barrier — awaited before the lane executes its FIRST
    /// item, so an inline (pre-publication) un-sequenced item reserved by the orchestrator can never
    /// overlap the lane. Null means "start immediately".</param>
    public SequencedCommandProcessor(
            string epoch, Func<string, AgentLiveness> readLiveness,
            Func<CommandAck, Task> sendAck, Func<CommandRejected, Task> sendRejected,
            ILogger logger, int cacheBound = 256,
            Func<string, bool>? isKnownStopTarget = null, TimeProvider? time = null,
            Task? startBarrier = null) {
        _epoch = epoch; _readLiveness = readLiveness; _sendAck = sendAck; _sendRejected = sendRejected;
        _logger = logger; _cacheBound = cacheBound;
        _isKnownStopTarget = isKnownStopTarget ?? (_ => false);
        _time = time ?? TimeProvider.System;
        _startBarrier = startBarrier;
        _laneTask = Task.Run(RunLaneAsync);
    }

    public string Epoch => _epoch;
    public long HighestAcceptedSeq { get { lock (_lock) return _highestAcceptedSeq; } }
    public long LastProcessedSeq   { get { lock (_lock) return _lastProcessedSeq; } }

    /// <summary>§3.3 metrics: un-sequenced stops currently queued (not yet dequeued to start) and the
    /// high-water mark since boot. Boundedness of the stop queue is MONITORED, not proven — under
    /// sustained agent churn during lane non-dequeue time it grows with churn — so these are the
    /// operator's view alongside the hysteresis alarm.</summary>
    public int QueuedStopDepth     { get { lock (_lock) return _queuedStops; } }
    public int QueuedStopHighWater { get { lock (_lock) return _queuedStopsHighWater; } }

    public Task SubmitAsync(SequencedItem item, Func<Task<CommandOutcome>> execute) {
        CommandOutcome? replay;
        bool acceptedReplay;
        CommandRejected? rejection;
        Task result;

        lock (_lock) {
            result = SubmitLocked(item, execute, out replay, out acceptedReplay, out rejection);
        }

        // Every wire send happens AFTER the lock is released. SendContained catches exceptions, but it
        // still invokes the delegate immediately — it cannot stop synchronous serialization or a
        // blocking transport from extending this processor's critical section and delaying concurrent
        // SubmitAsync/AckPrefix callers. Containment and lock-scope are separate problems; this is the
        // second one, and it applies to rejections exactly as it did to the duplicate answers.
        if (rejection is { } toReject) SendRejectedContained(toReject);

        // The in-progress duplicate answer. Sent outside the lock and contained for the same reasons as
        // the settled one: a synchronous transport throw escaped into the hub, a faulted task went
        // unobserved, and any synchronous work in the delegate ran inside this processor's critical
        // section — which is the section the whole change is trying to keep narrow.
        if (acceptedReplay)
            SendContained(() => _sendAck(new CommandAck(_epoch, item.Seq, item.CommandId, CommandAckState.Accepted)),
                item.Seq, "accepted ack");

        // Recovery path: the server retransmitting a command whose terminal ack it never received.
        // Built and sent outside _lock, contained — see SendSettledAck.
        if (replay is { } outcome) SendSettledAck(item, outcome);

        return result;
    }

    Task SubmitLocked(SequencedItem item, Func<Task<CommandOutcome>> execute,
            out CommandOutcome? replay, out bool acceptedReplay, out CommandRejected? rejection) {
        replay = null;
        acceptedReplay = false;
        rejection = null;

        if (!string.Equals(item.Epoch, _epoch, StringComparison.Ordinal))
            return RejectLocked(item, CommandRejectedReason.StaleEpoch, out rejection); // never touches THIS epoch's lane

        if (_cache.TryGetValue(item.Seq, out var existing))
            return HandleDuplicateLocked(item, existing, out replay, out acceptedReplay, out rejection); // answered, never re-executed

        if (item.Seq != _highestAcceptedSeq + 1)
            return HandleNonNextLocked(item, out rejection);

        if (_cache.Count >= _cacheBound)                                   // never evict unacked identity
            return RejectLocked(item, CommandRejectedReason.Backpressure, out rejection); // reopens only via a validated AckProcessedPrefix

        // ACCEPT + lane-item, atomically under _lock.
        _highestAcceptedSeq = item.Seq;
        _cache[item.Seq] = new CacheEntry { CommandId = item.CommandId, Seq = item.Seq, AgentId = item.AgentId, Processed = false };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = LaneItem.ForSequenced(item, execute, done);

        if (!_lane.Writer.TryWrite(lane)) {
            SynthesizeErrorLocked(item, out rejection); // shutdown/allocation race: watermark must still advance
            done.SetResult();
            return done.Task;                            // nothing tracked: the item was never enqueued
        }

        // §3.3 (one execution domain): the sequenced mutations live in the ACCEPT branch ONLY — the same
        // critical section that advanced the watermark and wrote the lane item above. A duplicate replay
        // and every rejection class (stale epoch, non-next, backpressure, failed enqueue) returned before
        // reaching here and mutate NONE of this. A newly accepted LAUNCH joins the active-instance set
        // (making the id an admissible stop target from this instant) and clears the id's pending-stop
        // keys, so a stop arriving after this launch cannot coalesce onto one that arrived before it —
        // which is what keeps stop(X) -> launch(X) -> stop(X) in order across formats.
        if (item.Kind is SequencedKind.Launch) {
            AddActiveLaunchLocked(lane, item.AgentId);
            ClearPendingStopKeysLocked(item.AgentId);
        }

        TrackCommittedLocked();
        return done.Task;
    }

    /// <summary>§3.3 (one execution domain): commit an UN-SEQUENCED launch/stop onto the same serial lane
    /// the sequenced protocol uses — no seq, no identity cache, no acks. Every predicate and mutation runs
    /// in ONE critical section before this returns (admissibility, active-launch tracking, launch-aware key
    /// clearing, coalescing, the lane write), so pump serialization plus this section IS the arrival-order
    /// guarantee for both formats in both directions, and a shutdown race can never both commit and refuse.
    /// Never refuses for FORMAT — the shipped server mixes formats by design (§1.9).
    ///
    /// <para>The two diagnostics this can produce (the unknown-target drop log and the queued-stop alarm)
    /// are deliberately emitted AFTER the lock is released: a logging provider is a supported input, not a
    /// contract, and must never run on the critical path of a concurrent
    /// <see cref="SubmitAsync"/>/<see cref="AckPrefix"/> caller.</para></summary>
    public SubmitOutcome SubmitUnsequenced(UnsequencedItem item) {
        SubmitOutcome outcome;
        int alarmDepth, alarmHighWater;

        lock (_lock) {
            outcome = SubmitUnsequencedLocked(item, out alarmDepth, out alarmHighWater);
        }

        if (outcome is SubmitOutcome.DroppedUnknownTarget)
            // The processor owns this log: the caller has no reply surface for an un-sequenced command
            // (§1.8) and the drop is observably identical to the unknown-agent no-op it replaces.
            LogQuietlyUnsequenced(null,
                "dropping an un-sequenced stop for unknown target {AgentId} (payload {PayloadKey}) — neither registered nor an in-flight launch",
                item.AgentId, item.PayloadKey);

        if (alarmDepth > 0)
            try {
                _logger.LogError(
                    "SequencedCommandProcessor: queued un-sequenced stops crossed {Threshold} (depth {Depth}, high-water {HighWater}) — "
                  + "the execution lane is not draining (a launch parked on consent, or a long item) while agents churn. "
                  + "Stops are NOT dropped; this is a saturation signal.",
                    StopQueueAlarmThreshold, alarmDepth, alarmHighWater);
            } catch { /* a throwing logger must never become the failure it was reporting */ }

        return outcome;
    }

    SubmitOutcome SubmitUnsequencedLocked(UnsequencedItem item, out int alarmDepth, out int alarmHighWater) {
        alarmDepth = 0;
        alarmHighWater = 0;

        if (_closed) return SubmitOutcome.Refused;

        if (item.Kind is UnsequencedKind.Launch) {
            var launch = LaneItem.ForUnsequenced(item);
            if (!_lane.Writer.TryWrite(launch)) return SubmitOutcome.Refused;

            // Same launch-commit mutations as the sequenced accept branch, so cross-format ordering and
            // stop admissibility do not depend on which format a launch arrived in.
            AddActiveLaunchLocked(launch, item.AgentId);
            ClearPendingStopKeysLocked(item.AgentId);
            TrackCommittedLocked();
            return SubmitOutcome.Committed;
        }

        // Admissible stop targets = the injected target surface UNION the ids with an active launch
        // instance. Anything else drops here: it would reach a no-op at execution anyway, and admitting
        // unknown ids is the one way the queue could grow without a live thing to stop.
        if (!_activeLaunches.ContainsKey(item.AgentId) && !IsKnownStopTargetLocked(item.AgentId))
            return SubmitOutcome.DroppedUnknownTarget;

        var key = (item.AgentId, item.PayloadKey);
        // An entry exists only while its item is still QUEUED (retired at dequeue), so this collapses a
        // burst without ever swallowing a retry after a started/faulted stop.
        if (_pendingStops.ContainsKey(key)) return SubmitOutcome.Coalesced;

        var stop = LaneItem.ForUnsequenced(item);
        if (!_lane.Writer.TryWrite(stop)) return SubmitOutcome.Refused;

        stop.PendingStopKey = key;
        _pendingStops[key] = stop;
        _queuedStops++;
        if (_queuedStops > _queuedStopsHighWater) _queuedStopsHighWater = _queuedStops;
        MaybeAlarmLocked(out alarmDepth, out alarmHighWater);
        TrackCommittedLocked();
        return SubmitOutcome.Committed;
    }

    /// <summary>The injected probe, contained. It walks the orchestrator's live collections, so a throw is
    /// possible; ADMITTING on failure is the safe direction — an un-sequenced stop for a target that turns
    /// out not to exist simply no-ops at execution, whereas dropping one for a live agent would leave it
    /// running.</summary>
    bool IsKnownStopTargetLocked(string agentId) {
        try {
            return _isKnownStopTarget(agentId);
        } catch (Exception ex) {
            try {
                _logger.LogDebug(ex,
                    "SequencedCommandProcessor: the stop-admission probe for {AgentId} threw — admitting the stop",
                    agentId);
            } catch { /* a throwing logger must not decide admission */ }
            return true;
        }
    }

    void AddActiveLaunchLocked(LaneItem lane, string agentId) {
        lane.ActiveLaunchId = agentId;
        _activeLaunches[agentId] = _activeLaunches.GetValueOrDefault(agentId) + 1;
    }

    /// <summary>Drop the pending-stop keys of every queued stop for this id (a launch commit starts a new
    /// coalescing segment). Deliberately does NOT touch those items' own <c>PendingStopKey</c> or the
    /// queued-stop counter: the items are still queued and WILL execute — only their eligibility to absorb
    /// a later same-payload stop ends here. Their dequeue then finds the key gone (or owned by a newer
    /// item) and, thanks to the identity guard, removes nothing.</summary>
    void ClearPendingStopKeysLocked(string agentId) {
        List<(string AgentId, string PayloadKey)>? drop = null;

        foreach (var key in _pendingStops.Keys)
            if (string.Equals(key.AgentId, agentId, StringComparison.Ordinal)) (drop ??= []).Add(key);

        if (drop is null) return;

        foreach (var key in drop) _pendingStops.Remove(key);
    }

    /// <summary>Retire this item's pending-stop key + queued-stop count. Called when the item is DEQUEUED
    /// to start, and again (harmlessly) from the terminal path, so a shutdown discard of a never-started
    /// item also returns the counter to zero. Identity-guarded: the key is removed only while it still
    /// refers to THIS item.</summary>
    void RetireDequeuedStopLocked(LaneItem lane) {
        if (lane.PendingStopKey is not { } key) return;

        lane.PendingStopKey = null;

        if (_pendingStops.TryGetValue(key, out var owner) && ReferenceEquals(owner, lane))
            _pendingStops.Remove(key);

        _queuedStops--;
        if (_queuedStops < StopQueueAlarmRearmBelow) _stopAlarmArmed = true;
    }

    void RetireActiveLaunchLocked(LaneItem lane) {
        if (lane.ActiveLaunchId is not { } agentId) return;

        lane.ActiveLaunchId = null;

        if (!_activeLaunches.TryGetValue(agentId, out var instances)) return;
        if (instances <= 1) _activeLaunches.Remove(agentId);
        else _activeLaunches[agentId] = instances - 1;
    }

    /// <summary>§3.3: the ONE terminal-finalization path, covering every ending an item can have — normal
    /// execute completion, lane failure, shutdown-synthesized settlement (never executed), and shutdown
    /// discard. Retiring an active-launch instance HERE, after execution has returned, is what pins the
    /// removal ordering: the id stops being admissible only once its agent is in the registry (success) or
    /// the launch terminally failed, and only once the LAST instance for that id has settled.</summary>
    void FinalizeLocked(LaneItem lane) {
        RetireDequeuedStopLocked(lane);   // no-op when the item already started
        RetireActiveLaunchLocked(lane);

        if (--_outstanding > 0) return;

        _outstanding = 0;
        var idle = _idle;
        _idle = null;
        idle?.TrySetResult();             // RunContinuationsAsynchronously — never reenters under _lock
    }

    void TrackCommittedLocked() {
        if (_outstanding++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    void MaybeAlarmLocked(out int alarmDepth, out int alarmHighWater) {
        alarmDepth = 0;
        alarmHighWater = 0;

        if (_queuedStops < StopQueueAlarmThreshold || !_stopAlarmArmed) return;

        // Disarm on the CROSSING whether or not the Error is actually emitted: that is what keeps growth
        // past the threshold quiet and stops boundary oscillation inside the minimum interval from
        // emitting anything. Only a drain below the hysteresis watermark re-arms.
        _stopAlarmArmed = false;

        if (_lastStopAlarmAtTimestamp is { } last
                && _time.GetElapsedTime(last) < StopQueueAlarmMinInterval) return;

        _lastStopAlarmAtTimestamp = _time.GetTimestamp();
        alarmDepth = _queuedStops;
        alarmHighWater = _queuedStopsHighWater;
    }

    /// <summary>Diagnostics for the un-sequenced lane that cannot themselves become the failure — same
    /// contract as <see cref="LogQuietly"/>, different argument shape.</summary>
    void LogQuietlyUnsequenced(Exception? error, string template, string agentId, string payloadKey) {
        try {
            _logger.LogDebug(error, "SequencedCommandProcessor: " + template, agentId, payloadKey);
        } catch { /* deliberately empty — see summary */ }
    }

    /// <summary>Test seams for the §3.3 tracking state. Reading them takes <c>_lock</c>, so they observe
    /// exactly what admission and coalescing observe.</summary>
    internal bool IsActiveLaunchTargetForTest(string agentId) { lock (_lock) return _activeLaunches.ContainsKey(agentId); }
    internal int  ActiveLaunchInstancesForTest(string agentId) { lock (_lock) return _activeLaunches.GetValueOrDefault(agentId); }
    internal int  ActiveLaunchIdCountForTest  { get { lock (_lock) return _activeLaunches.Count; } }
    internal int  PendingStopKeyCountForTest  { get { lock (_lock) return _pendingStops.Count; } }

    /// <summary>Test seam: completes once the lane has nothing queued or in flight. The production code
    /// never waits on the lane — that is the whole point of §3.3 — so this exists only so a test can
    /// assert on an un-sequenced item's SIDE EFFECTS, which are otherwise its only observable.</summary>
    internal Task WhenIdleForTest() { lock (_lock) return _outstanding == 0 ? Task.CompletedTask : _idle!.Task; }

    /// <summary>Phase B2-b (sequenced-settlement design): an exact-duplicate <c>(Epoch, Seq, CommandId)</c>
    /// is ANSWERED, never re-executed — <c>Accepted</c> while still processing, or <c>Processed</c> with the
    /// cached outcome. A DIFFERENT CommandId at an already-accepted Seq is a protocol-invariant violation →
    /// <c>duplicate_collision</c>. Called under <c>_lock</c>; the processed arm only CAPTURES the cached
    /// outcome, leaving the ack for <see cref="SubmitAsync"/> to build outside the lock.</summary>
    Task HandleDuplicateLocked(SequencedItem item, CacheEntry existing,
            out CommandOutcome? replay, out bool acceptedReplay, out CommandRejected? rejection) {
        replay = null;
        acceptedReplay = false;
        rejection = null;

        if (!string.Equals(existing.CommandId, item.CommandId, StringComparison.Ordinal)) {
            // A DIFFERENT command claiming an accepted Seq — protocol invariant violation.
            rejection = new CommandRejected(item.Epoch, item.Seq, item.CommandId, CommandRejectedReason.DuplicateCollision, item.AgentId);
            return Task.CompletedTask;
        }

        // Both arms only CAPTURE what to answer with; SubmitAsync sends after releasing the lock.
        if (!existing.Processed) acceptedReplay = true;
        else replay = existing.Outcome;

        return Task.CompletedTask;
    }

    /// <summary>The terminal <c>Processed</c> ack for a settled command — the ONE builder shared by the
    /// duplicate-replay path and the proactive settle ack at the end of <see cref="RunLaneAsync"/>, so a
    /// retransmission can never disagree with the proactive send about outcome/agent/session/rejection-reason.
    /// The CACHED rejection reason rides along so a rejected launch stays distinguishable as daemon_capacity
    /// (requeue) vs semantic (fail) — the exact lost-rejection case the identity cache answers.
    ///
    /// <para>MUST NOT be called under <c>_lock</c>: <c>CurrentState</c> is read live through the
    /// readLiveness delegate, which walks the orchestrator's lifecycle collections, and every settled
    /// command reaches this — holding the lock across it would put that read on the critical path of
    /// concurrent <see cref="SubmitAsync"/>/<see cref="AckPrefix"/> callers. Both callers commit the
    /// outcome to the cache first, so an ack can never advertise an outcome the cache lacks.</para></summary>
    CommandAck BuildProcessedAck(SequencedItem item, CommandOutcome outcome) =>
        BuildProcessedAck(item.Seq, item.CommandId, item.AgentId, outcome);

    /// <summary>Settlement lost-ack redelivery (D1): the primitive builder, so a re-delivery can rebuild an entry's terminal ack
    /// from the cached identity (Seq/CommandId/AgentId + Outcome) without the original SequencedItem. Same
    /// live <c>_readLiveness</c> read and lock-scope contract as the item overload.</summary>
    CommandAck BuildProcessedAck(long seq, string commandId, string agentId, CommandOutcome outcome) {
        var live   = _readLiveness(outcome.AgentId ?? agentId);
        var reason = outcome.RejectReason is { } r ? RejectReasonWireToken(r) : null;

        return new CommandAck(_epoch, seq, commandId, CommandAckState.Processed,
            outcome.Kind, live, outcome.AgentId ?? agentId, outcome.SessionId, reason);
    }

    /// <summary>Settlement lost-ack redelivery (D1) (freeze the terminal ack): get-or-freeze the SINGLE terminal ack for a settled
    /// command. The candidate is built OUTSIDE <c>_lock</c> (readLiveness walks lifecycle collections and
    /// must never run under the lock), then published under <c>_lock</c> IFF the entry has no frozen ack
    /// yet — a single winner. Every sender (proactive settle, duplicate-replay, tick/reconnect
    /// re-delivery) sends the RETURNED winner, so a liveness value that changed between two sends can never
    /// make two terminal acks for one command disagree.
    /// <para>Returns <c>null</c> when the entry is gone (retired) OR the build threw: a throwing
    /// readLiveness must not abandon the item — the outcome stays committed <c>Processed</c>, the freeze is
    /// simply deferred, and a later tick/reconnect re-attempts it. This is the exact containment
    /// <see cref="SendSettledAck"/> used to apply to the inline build.</para></summary>
    CommandAck? TryGetOrFreezeAck(long seq, string commandId, string agentId, CommandOutcome outcome) {
        lock (_lock) {
            if (!_cache.TryGetValue(seq, out var current)) return null;      // retired
            if (current.FrozenAck is { } already) return already;            // fast path: winner exists
        }
        CommandAck candidate;
        try {
            candidate = BuildProcessedAck(seq, commandId, agentId, outcome);
        } catch (Exception ex) {
            // Leave FrozenAck null so a later re-delivery retries; the outcome is already committed.
            _logger.LogDebug(ex, "Deferring terminal-ack freeze for seq {Seq}: liveness read threw.", seq);
            return null;
        }
        lock (_lock) {
            if (!_cache.TryGetValue(seq, out var entry)) return null;        // retired during the build
            return entry.FrozenAck ??= candidate;                            // publish iff unset; losers take the winner
        }
    }

    /// <summary>Settlement lost-ack redelivery (D1) (re-deliver unretired outcomes): every Processed cache entry the server has NOT
    /// confirmed via a validated <c>AckProcessedPrefix</c> (retirement evicts the entry, so presence in the
    /// cache IS the unretired predicate). Snapshotted under <c>_lock</c> so the caller iterates without
    /// holding it. Each carries what a re-send needs: the frozen ack if already published, else the
    /// identity+outcome to (re)freeze at send time.</summary>
    internal IReadOnlyList<(long Seq, string CommandId, string AgentId, CommandOutcome Outcome, CommandAck? FrozenAck)>
        EnumerateUnretiredProcessedEntries() {
        lock (_lock) {
            var result = new List<(long, string, string, CommandOutcome, CommandAck?)>();
            foreach (var e in _cache.Values)
                if (e.Processed) result.Add((e.Seq, e.CommandId, e.AgentId, e.Outcome, e.FrozenAck));
            return result;
        }
    }

    /// <summary>Build AND fire a settled command's terminal ack without ever letting it fault the caller.
    /// The ack is best-effort telemetry — a disconnected/reconnecting server just falls back to the
    /// periodic status-report reconcile — so a synchronous throw AND a faulted task are both swallowed
    /// at Debug. Never awaited: neither the lane nor a hub callback may block on the wire.
    ///
    /// <para>The BUILD is deliberately inside the try. Passing a pre-built ack (
    /// <c>Send(Build(..))</c>) evaluates the argument before entering the containment, so a throwing
    /// <c>_readLiveness</c> escapes: from the lane it faults <see cref="RunLaneAsync"/> and leaves the
    /// item's <c>Done</c> unresolved (hanging that submitter and stopping every later command), and from
    /// <see cref="SubmitAsync"/> it escapes into the hub handler. Both callers reach this only AFTER the
    /// outcome is recorded in the cache, so swallowing the ack costs at most one status-report interval
    /// of slot latency — never a wrong or missing terminal fact.</para></summary>
    void SendSettledAck(SequencedItem item, CommandOutcome outcome) {
        // Settlement lost-ack redelivery (D1): send the FROZEN winner (get-or-freeze), never a freshly-built ack — so the proactive
        // send and any later duplicate-replay/re-delivery are byte-identical. A deferred freeze (liveness
        // read threw) sends nothing now; a later tick/reconnect re-delivery retries it.
        if (TryGetOrFreezeAck(item.Seq, item.CommandId, item.AgentId, outcome) is { } ack)
            SendContained(() => _sendAck(ack), item.Seq, "settled ack");
    }

    /// <summary>Settlement lost-ack redelivery (D1) (re-deliver unretired outcomes): re-send the terminal ack of every unretired
    /// Processed command, freezing any winner-less entry first (a freeze deferred by a throwing liveness
    /// read now retries). The orchestrator calls this after every successful (re)connect + registration and
    /// on each status-report tick while any unretired processed entry remains, so a terminal ack lost in a
    /// reconnect window is re-elicited without waiting for the server to retransmit the command. Sends are
    /// contained + one-way and never block the lane; a validated <see cref="AckProcessedPrefix"/> evicts
    /// retired entries, which is what makes the re-sends stop.</summary>
    public void RedeliverUnretiredProcessedAcks() {
        foreach (var (seq, commandId, agentId, outcome, frozen) in EnumerateUnretiredProcessedEntries()) {
            var ack = frozen ?? TryGetOrFreezeAck(seq, commandId, agentId, outcome);
            if (ack is { } resend) SendContained(() => _sendAck(resend), seq, "re-delivered settled ack");
        }
    }

    /// <summary>Phase B2-b (sequenced-settlement design §5.5): the wire token a cached
    /// <see cref="CommandRejectedReason"/> carries on a processed-duplicate <see cref="CommandAck"/> —
    /// serialized through the SAME context the SignalR hub uses for <c>CommandRejected.Reason</c>, so a
    /// retransmitted duplicate reads <c>daemon_capacity</c>/<c>semantic</c> identically to the first
    /// rejection (the ack's <c>RejectionReason</c> is a STRING; the outcome's <c>RejectReason</c> is the
    /// enum). NEVER <c>.ToString()</c> — that emits the C# name, not the <c>[JsonStringEnumMemberName]</c>
    /// snake_case token.</summary>
    static string RejectReasonWireToken(CommandRejectedReason reason) =>
        JsonSerializer.Serialize(reason, CapacitorJsonContext.Default.CommandRejectedReason).Trim('"');

    /// <summary>Phase B2-b (sequenced-settlement design): a non-next Seq (a gap — Seq &gt; HighestAcceptedSeq+1,
    /// or a too-low already-retired Seq below the frontier) is NEVER accepted out of order. Emit wrong_next so
    /// the server's transport sequencer resyncs (nudge → observe → retransmit); accept path + watermark untouched.</summary>
    Task HandleNonNextLocked(SequencedItem item, out CommandRejected? rejection) =>
        RejectLocked(item, CommandRejectedReason.WrongNext, out rejection);

    /// <summary>Records WHICH rejection is owed; the caller sends it after releasing <c>_lock</c>.</summary>
    Task RejectLocked(SequencedItem item, CommandRejectedReason reason, out CommandRejected? rejection) {
        rejection = new CommandRejected(item.Epoch, item.Seq, item.CommandId, reason, item.AgentId);
        return Task.CompletedTask;
    }

    /// <summary>Every <c>CommandRejected</c> send goes through here. A rejection is a NOTIFICATION, never
    /// the settlement fact itself, so a transport failure must not gate settlement — a bare send that
    /// throws synchronously escapes into whatever is calling (the hub, or worse the serial lane, where it
    /// permanently kills the consumer and strands every queued command). The server reconciles from its
    /// periodic status report regardless.</summary>
    void SendRejectedContained(CommandRejected rejected) =>
        SendContained(() => _sendRejected(rejected), rejected.Seq, "rejection");

    /// <summary>Shared containment for every best-effort wire send: a synchronous throw AND a faulted
    /// task are both swallowed at Debug, and the delegate is invoked INSIDE the try so argument
    /// construction is covered too. Never awaited — no wire call may block the lane or a hub callback.</summary>
    void SendContained(Func<Task> send, long seq, string what) {
        try {
            var sent = send();
            if (sent is { IsCompletedSuccessfully: false })
                _ = sent.ContinueWith(
                    // Guarded: an unguarded LogDebug here would fault this DISCARDED continuation on a
                    // throwing provider — recreating the very unobserved-task-failure this wrapper exists
                    // to prevent, in the code meant to report it.
                    t => LogQuietly(t.Exception, "{What} for seq {Seq} failed to send", what, seq),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        } catch (Exception ex) {
            // Guarded for the same reason, and a sharper one: the hub-side callers (stale epoch, gap,
            // duplicate collision, accepted/processed replay) have NO outer per-item catch, so a
            // transport throw followed by a logging throw escaped straight into the hub.
            LogQuietly(ex, "{What} for seq {Seq} threw", what, seq);
        }
    }

    /// <summary>Diagnostics that cannot themselves become the failure. Every logging call on a
    /// best-effort path routes through here — a throwing <c>ILogger</c> provider is a supported input,
    /// not a contract violation, and losing a Debug line is always preferable to losing the operation
    /// it was describing.</summary>
    void LogQuietly(Exception? error, string template, string what, long seq) {
        try {
            _logger.LogDebug(error, "SequencedCommandProcessor: " + template, what, seq);
        } catch { /* deliberately empty — see summary */ }
    }

    void SynthesizeErrorLocked(SequencedItem item, out CommandRejected? rejection) {
        // Lane-item creation failed AFTER acceptance (shutdown/allocation race) — an advertised-accepted
        // Seq with no processable item is impossible, so mark this Seq terminally errored and advance the
        // watermark THROUGH THE CONTIGUOUS PREFIX only. NEVER set _lastProcessedSeq = item.Seq directly:
        // if the lane is completing while an earlier accepted item is still draining, a direct jump to N
        // would (a) advertise a non-contiguous prefix and (b) be regressed below when the earlier item's
        // consumer later advances to N-1. AdvanceWatermarkLocked is monotonic + contiguous by construction.
        _cache[item.Seq] = new CacheEntry {
            CommandId = item.CommandId, Seq = item.Seq, AgentId = item.AgentId, Processed = true,
            Outcome = new CommandOutcome(CommandOutcomeKind.InternalError, item.AgentId) };
        AdvanceWatermarkLocked();
        rejection = new CommandRejected(item.Epoch, item.Seq, item.CommandId, CommandRejectedReason.InternalError, item.AgentId);
    }

    /// <summary>The watermark is the contiguous terminal-processed prefix. Walk forward through Processed
    /// cache entries from _lastProcessedSeq+1 so a synthesized out-of-order terminal (a shutdown race)
    /// never advances past a still-draining earlier item, and no advance can ever regress the watermark
    /// (monotonic by construction). Retired seqs are always &lt;= _lastProcessedSeq, so the walk is safe.</summary>
    void AdvanceWatermarkLocked() {
        while (_cache.TryGetValue(_lastProcessedSeq + 1, out var next) && next.Processed)
            _lastProcessedSeq++;
    }

    /// <summary>Test seam: complete the lane writer so a subsequent accepted Submit's TryWrite fails,
    /// forcing the SynthesizeErrorLocked path deterministically (mirrors a shutdown race).</summary>
    internal void CompleteLaneForTest() => _lane.Writer.TryComplete();

    /// <summary>Test seam: whether the CALLING thread currently holds <c>_lock</c>. Lets a test's
    /// readLiveness delegate assert directly that <see cref="BuildProcessedAck"/> is never invoked
    /// under the lock, with no timing or thread choreography.</summary>
    internal bool LockHeldByCurrentThreadForTest => Monitor.IsEntered(_lock);

    async Task RunLaneAsync() {
        // §3.3 transition barrier: an un-sequenced handler that snapshotted a NULL processor reserved an
        // inline slot in the same critical section publication uses, and publication handed us its
        // completion. Waiting for it here — before the FIRST item — is what makes "no dual domain, ever"
        // true rather than asserted. Shutdown releases the wait so a never-completed barrier can still
        // drain and settle instead of hanging DisposeAsync.
        if (_startBarrier is { } barrier) {
            try { await barrier.WaitAsync(_laneShutdown.Token); }
            catch (OperationCanceledException) { /* shutting down — fall through to drain + settle */ }
        }

        await foreach (var li in _lane.Reader.ReadAllAsync()) {
            // Dequeue bookkeeping FIRST, under the lock the submitter used: this item stops being the
            // coalescing target for its (agent, payload class) the instant it starts, so a same-payload
            // retry after a started or faulted stop commits a fresh item instead of being swallowed.
            bool draining;
            lock (_lock) {
                draining = _closed;
                RetireDequeuedStopLocked(li);
            }

            // Shutdown supersession (§3.3): daemon-wide teardown supersedes per-agent stops. An accepted
            // SEQUENCED item still gets its terminal answer; an un-sequenced one is discarded silently.
            if (draining) { SettleAtShutdown(li); continue; }

            if (!li.IsSequenced) await RunUnsequencedAsync(li);
            else await RunSequencedAsync(li);
        }
    }

    /// <summary>§3.3: an un-sequenced item has NO reply surface (§1.8), so the log IS its whole failure
    /// outcome. Containment is the lane's duty, not the caller's: an item's exception — including an
    /// <see cref="OperationCanceledException"/> from a shutdown-cancelled launch — must never terminate the
    /// single serial consumer, which is why "a stop queued behind a faulted item still executes" holds for
    /// every non-shutdown fault.</summary>
    async Task RunUnsequencedAsync(LaneItem li) {
        try {
            await li.Unsequenced.Execute();
        } catch (Exception ex) {
            try {
                _logger.LogWarning(ex,
                    "SequencedCommandProcessor: un-sequenced {Kind} for agent {AgentId} faulted — the lane continues",
                    li.Unsequenced.Kind, li.Unsequenced.AgentId);
            } catch { /* a throwing logger must not become the lane's own failure */ }
        } finally {
            lock (_lock) FinalizeLocked(li);
        }
    }

    async Task RunSequencedAsync(LaneItem li) {
        // Nothing between here and Done may escape. The lane is the SINGLE serial consumer: one
        // escaping exception kills it permanently, leaving this command nonterminal, its submitter
        // waiting forever, and every queued command unrun — the server reads that as a permanently
        // held daemon capacity slot.
        //
        // try/FINALLY alone was not enough (and the comment that said it was, was wrong): the
        // finally releases the submitter but the exception still propagates out of the await
        // foreach and faults the lane task — and it reports SUCCESS to a submitter whose command
        // may never have been marked terminal. The realistic path is diagnostics: a logger provider
        // throwing from the execute-fault LogWarning, or from SendContained's own catch. So there
        // is a real per-item CATCH that synthesizes the terminal state when the normal path did not
        // reach it, and the loop continues.
        var settled = false;
        try {
            CommandOutcome outcome;
            CommandRejectedReason? rejection = null;

            try {
                outcome = await li.SequencedExecute!();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "SequencedCommandProcessor: execution faulted for seq {Seq} — internal_error", li.Sequenced.Seq);
                outcome = new CommandOutcome(CommandOutcomeKind.InternalError, li.Sequenced.AgentId);
                rejection = CommandRejectedReason.InternalError;
            }

            // Task 15: an execution-time terminal rejection (daemon_capacity / semantic).
            if (outcome.Kind == CommandOutcomeKind.LaunchRejected && outcome.RejectReason is { } r)
                rejection = r;

            lock (_lock) {
                if (_cache.TryGetValue(li.Sequenced.Seq, out var e)) { e.Processed = true; e.Outcome = outcome; }
                AdvanceWatermarkLocked(); // contiguous terminal prefix — serial lane => normally == prior + 1,
                                          // but shared with SynthesizeErrorLocked so a race can never regress it
            }

            // Settled the instant the commit succeeds, BEFORE any notification. Setting this after
            // the sends meant a throw escaping SendRejectedContained (transport throws, then its own
            // LogDebug throws) reached the outer catch with settled still false, which then
            // OVERWROTE the real terminal outcome with InternalError — a LaunchRejected already
            // cached and possibly announced as daemon_capacity would replay later as a
            // contradictory internal_error. The cache is authoritative from here on; announcing it
            // is strictly best-effort and must never be able to rewrite it.
            settled = true;

            // Both notifications happen AFTER the outcome is recorded, and both are contained. They
            // used to run BEFORE it: a synchronous throw from _sendRejected then escaped the lane
            // with the command still marked nonterminal — the settlement fact lost to a failure in
            // merely announcing it. Relative order (rejection, then ack) is unchanged.
            if (rejection is { } reason)
                SendRejectedContained(new CommandRejected(
                    li.Sequenced.Epoch, li.Sequenced.Seq, li.Sequenced.CommandId, reason, li.Sequenced.AgentId));

            // Settlement-admission design (§3.2 F): a FRESH terminal command is acked proactively, not
            // just a retransmitted duplicate. Without this the server only learns the command settled
            // when its next periodic status report reconciles (up to one report interval later), and
            // its one-nonterminal-command-per-daemon invariant rejects any concurrent launch for that
            // whole window. The server's ack handler is idempotent against replays and unknown/stale
            // acks, so older servers accept it too and simply retire the slot earlier.
            //
            // Built AFTER the cache commit above, outside the lock — see SendSettledAck.
            SendSettledAck(li.Sequenced, outcome);
        } catch (Exception ex) {
            // Deliberately swallow-and-continue. Every diagnostic here is itself guarded, because
            // the most likely cause of arriving in this catch is diagnostics throwing.
            if (!settled)
                try {
                    lock (_lock) {
                        if (_cache.TryGetValue(li.Sequenced.Seq, out var e)) {
                            e.Processed = true;
                            e.Outcome = new CommandOutcome(CommandOutcomeKind.InternalError, li.Sequenced.AgentId);
                        }
                        AdvanceWatermarkLocked();
                    }
                } catch { /* cache/watermark unreachable — the status-report reconcile is the backstop */ }
            try {
                _logger.LogError(ex, "SequencedCommandProcessor: unhandled fault for seq {Seq} — lane continues", li.Sequenced.Seq);
            } catch { /* a throwing logger is exactly how we got here */ }
        } finally {
            // §3.3: retire this item's active-launch instance through the ONE terminal-finalization
            // path BEFORE releasing the submitter, so the id stops being an admissible stop target
            // only once its launch has actually finished (registered or terminally failed).
            lock (_lock) FinalizeLocked(li);
            li.Done!.TrySetResult();
        }
    }

    /// <summary>§3.3 shutdown supersession. An ACCEPTED sequenced item that never ran still gets its
    /// terminal answer through the shipped <see cref="SynthesizeErrorLocked"/> machinery (cache +
    /// contiguous watermark + a best-effort <c>CommandRejected</c>), and its execution-completion task is
    /// completed EXACTLY once — this item never reaches the normal <c>finally</c>, and
    /// <c>TrySetResult</c> makes a double settle impossible. Where the transport is already gone, the
    /// settlement protocol's own recovery (duplicate replay from the cache, boot-epoch fencing on restart)
    /// is the shipped answer to a lost terminal ack.
    ///
    /// <para>A queued UN-SEQUENCED item is discarded silently BY DESIGN: daemon-wide teardown supersedes
    /// per-agent stops, and registered children are killed by the orchestrator's own teardown. The
    /// residual is explicit — a child that starts after that teardown snapshot survives until the NEXT
    /// daemon boot's env-marker/PID-record scan reaps it by durable start identity.</para></summary>
    void SettleAtShutdown(LaneItem li) {
        if (!li.IsSequenced) {
            lock (_lock) FinalizeLocked(li);
            try {
                _logger.LogInformation(
                    "SequencedCommandProcessor: discarding a queued un-sequenced {Kind} for agent {AgentId} at shutdown — "
                  + "daemon-wide teardown supersedes it",
                    li.Unsequenced.Kind, li.Unsequenced.AgentId);
            } catch { /* diagnostics must never block teardown */ }
            return;
        }

        CommandRejected? rejection;
        lock (_lock) {
            SynthesizeErrorLocked(li.Sequenced, out rejection);
            FinalizeLocked(li);
        }

        if (rejection is { } owed) SendRejectedContained(owed);
        li.Done!.TrySetResult();
    }

    /// <summary>Phase B2-b (sequenced-settlement design): retire per-epoch identity-cache entries through a
    /// VALIDATED <c>AckProcessedPrefix</c> — current epoch, not over-ahead of the processed prefix, strictly
    /// monotonic. An unacked entry is NEVER evicted (that is the backpressure contract's other half). Called
    /// off the hub, so it takes <c>_lock</c> itself.</summary>
    public void AckPrefix(AckProcessedPrefix ack) {
        lock (_lock) {
            if (!string.Equals(ack.Epoch, _epoch, StringComparison.Ordinal)) return; // stale epoch — not our lane
            if (ack.UpToSeq > _lastProcessedSeq) return;                              // over-ahead of the processed prefix
            if (ack.UpToSeq <= _lastAckedPrefix) return;                              // regressing / duplicate ack
            _lastAckedPrefix = ack.UpToSeq;
            List<long>? toRemove = null;
            foreach (var seq in _cache.Keys)
                if (seq <= ack.UpToSeq) (toRemove ??= []).Add(seq);
            if (toRemove is not null)
                foreach (var seq in toRemove) _cache.Remove(seq);
        }
    }

    /// <summary>§3.3: stop accepting, without waiting for anything. Closing the gate and completing the
    /// writer happen in the SAME critical section a submitter uses, so no submission can slip past into a
    /// lane that will never run it — after this, <see cref="SubmitUnsequenced"/> returns
    /// <see cref="SubmitOutcome.Refused"/> and the caller owns the consequence. The orchestrator calls this
    /// BEFORE it cancels its shutdown token and tears down agents, so the window between "the daemon is
    /// exiting" and "the lane is closed" cannot let queued per-agent stops run against children teardown is
    /// already killing. Idempotent; <see cref="DisposeAsync"/> repeats it and then awaits the drain.</summary>
    public void StopAcceptingForShutdown() {
        lock (_lock) {
            _closed = true;
            _lane.Writer.TryComplete();
        }
    }

    /// <summary>§3.3 lane shutdown order. The in-flight item is cancelled by the token its own delegate
    /// captured (the daemon shutdown token, cancelled by the orchestrator before it disposes this) and
    /// contained by the per-item catch; every remaining queued item is then settled or discarded by
    /// <see cref="SettleAtShutdown"/> before the lane exits, which also retires the exact active-launch
    /// tokens and returns the queued-stop counter to zero.</summary>
    public async ValueTask DisposeAsync() {
        // Idempotent: the orchestrator disposes explicitly and the DI container may dispose
        // again; a second pass must not CancelAsync an already-disposed CTS (that throws).
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAcceptingForShutdown();

        // Releases the lane's start-barrier wait, so a processor whose barrier never completed (a
        // never-published one in a test, or a stuck inline item) still drains instead of hanging here.
        await _laneShutdown.CancelAsync();
        try { await _laneTask; } catch { /* best-effort */ }
        _laneShutdown.Dispose();
    }
}
