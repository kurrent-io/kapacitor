using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// Sole owner of the pending-consent cache (spec §5): it subscribes to the daemon's consent
/// stream, holds the pending queue, and is the only place entries are inserted or removed —
/// ViewModels read and pin, never mutate.
///
/// Four guards carry the reviewed reasoning:
///
/// * <b>EntryAdded is the FIRST SURFACING of a PromptId, never a new cache key</b> — the signal
///   is the raise trigger (spec §6), so it has to mean "a request the user has not been offered
///   yet". Keyed on the cache key it was wrong in both directions: a successor B under A's
///   RequestId (a relaunch — the likeliest second prompt there is) replaced the slot in silence
///   and never raised, while a resubscribe's clear+replay made every replayed entry look new and
///   re-raised a window the user had explicitly deferred. `_surfaced` is therefore a
///   service-lifetime PromptId set with the tombstone argument behind it — never-reused GUIDs, so
///   it can never suppress a future request, at ~50 bytes per request ever seen. Tombstones are a
///   subset of it (a concluded request was surfaced first) and stay separate because they do a
///   different job: a tombstone DROPS the frame, `_surfaced` only keeps it quiet.
/// * <b>Tombstones live for the service lifetime</b> — no TTL, no per-connection retirement, no
///   size cap. The broker snapshots its pending set without synchronizing against a concurrent
///   resolve, and a snapshotted frame can sit in the per-subscriber channel arbitrarily long, so a
///   ghost for an already-concluded request can arrive before OR after its own ack. Any retirement
///   boundary reopens that window — in particular a client-local <c>Subscribed</c> is not ordered
///   against a concurrent ack. Nothing is traded away: a PromptId is a never-reused GUID, so a
///   lifetime tombstone can never suppress a future request, at ~50 bytes per human decision.
/// * <b>The cache clear sits at the <c>Subscribed</c> boundary, never before the dial</b> — a
///   failed dial while the status socket still reports Connected must not erase a still-actionable
///   queue; retained entries keep the tray attention and their countdowns alive through the
///   transient. After the boundary the daemon's replay is authoritative.
/// * <b>The prune skips the in-flight resolve target and a transport failure refreshes it</b> — a
///   user clicking just before the prune boundary must not have the entry vanish mid-call (the
///   lane's settlement disposes of it instead), and the promised entry-stays-interactive state
///   after an unreachable daemon has to actually exist for a beat.
///
/// Nothing here marshals to the UI thread: cache mutations land on the loop's thread-pool
/// continuations, and consumers ObserveOn(RxSchedulers.MainThreadScheduler).
public sealed class ConsentService : IConsentService {
    const string ConsentV2Capability = "consent/2";

    static readonly TimeSpan PruneGrace = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    readonly SourceCache<PendingConsent, string> _cache = new(p => p.RequestId);
    readonly HashSet<string> _tombstones = [];
    readonly HashSet<string> _surfaced = [];
    readonly Lock _lock = new();          // guards _tombstones, _surfaced, _inFlightPromptId, _loopCts, _disposed
    readonly SemaphoreSlim _lane = new(1, 1); // one resolve in flight (PauseController discipline)
    readonly Subject<Unit> _entryAdded = new();

    readonly ILocalControlOps _ops;
    readonly Func<CancellationToken, IAsyncEnumerable<ConsentStreamEvent>> _subscribe;
    readonly TimeProvider _time;
    readonly CancellationToken _shutdownToken;
    readonly IDisposable _statusSub;
    readonly IDisposable _tickSub;

    string? _inFlightPromptId;
    CancellationTokenSource? _loopCts;
    bool _disposed;

    public ConsentService(
            IDaemonClientService service, ILocalControlOps ops, ITicker ticker,
            Func<CancellationToken, IAsyncEnumerable<ConsentStreamEvent>> subscribe,
            TimeProvider time, CancellationToken shutdownToken) {
        _ops           = ops;
        _subscribe     = subscribe;
        _time          = time;
        _shutdownToken = shutdownToken;
        _tickSub       = ticker.Ticks.Subscribe(_ => Prune());
        _statusSub     = service.Status.Subscribe(OnStatus);
    }

    public IObservable<IChangeSet<PendingConsent, string>> Pending => _cache.Connect();
    public IObservable<int> PendingCount => _cache.CountChanged;
    public IObservable<Unit> EntryAdded => _entryAdded.AsObservable();

    public async Task<ConsentResolveOutcome> ResolveAsync(
            PendingConsent target, bool allow, bool saveRule, CancellationToken ct) {
        await _lane.WaitAsync(ct).ConfigureAwait(false); // OCE propagates: entry kept, no tombstone
        try {
            // Safety boundary, not UX: a null/empty requester would serialize into a wildcard
            // allow-everything rule, so the save is dropped and reported instead of sent.
            var sendRule = saveRule && !string.IsNullOrEmpty(target.Dto.Requester);
            var skipped  = saveRule && !sendRule;
            var resolve  = new ConsentResolveDto(
                target.RequestId, allow ? "allow" : "deny",
                sendRule ? new ConsentRuleDto("allow", target.Dto.Requester, null, null, null) : null,
                target.PromptId); // ALWAYS the echo — the daemon's identity check (spec §4.1)

            lock (_lock) _inFlightPromptId = target.PromptId;

            ConsentAckDto ack;
            try {
                ack = await _ops.ResolveConsentAsync(resolve, ct).ConfigureAwait(false);
            } catch (LocalControlOpsException ex) {
                return Unsettled(ex.Reason);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // An unmapped failure (LocalControlOps does not classify every socket-construction
                // error) must never reach a UI command — and it settled nothing, so it keeps the
                // entry exactly like a transport failure does.
                Console.Error.WriteLine($"kcap: consent resolve failed unexpectedly: {ex.Message}");
                return Unsettled(ex.Message);
            }

            Conclude(target);
            var ruleOutcome = RuleOf(ack);
            var kind =
                !ack.Ok    ? ConsentResolveKind.AlreadyDecided
                : skipped  ? ConsentResolveKind.RuleSkippedNoRequester
                : ruleOutcome is ConsentRuleOutcome.Rejected or ConsentRuleOutcome.Unknown
                    ? ConsentResolveKind.AppliedRuleRejected
                    : ConsentResolveKind.Applied;
            return new ConsentResolveOutcome(kind, ruleOutcome, ack.Error);

            ConsentResolveOutcome Unsettled(string reason) {
                RefreshPrune(target);
                return new ConsentResolveOutcome(ConsentResolveKind.TransportFailure, RuleOf(null), reason);
            }

            ConsentRuleOutcome RuleOf(ConsentAckDto? a) =>
                skipped      ? ConsentRuleOutcome.SkippedNoRequester
                : !sendRule  ? ConsentRuleOutcome.NotRequested
                : a?.RuleSaved switch {
                    true  => ConsentRuleOutcome.Saved,
                    false => ConsentRuleOutcome.Rejected,
                    // A pre-rule_saved ack that applied cleanly reported success the only way it
                    // could: Ok with no warning (spec §4.1's carve-out).
                    _     => a is { Ok: true, Error: null } ? ConsentRuleOutcome.Saved : ConsentRuleOutcome.Unknown,
                };
        } finally {
            lock (_lock) _inFlightPromptId = null;
            try { _lane.Release(); } catch (ObjectDisposedException) { } // disposed mid-resolve
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _statusSub.Dispose();
        _tickSub.Dispose();
        StopLoop();
        _entryAdded.OnCompleted();
        _entryAdded.Dispose();
        _cache.Dispose();
        _lane.Dispose();
    }

    void OnStatus(AttachStatus status) {
        if (status is { State: AttachState.Connected, Capabilities: not null } &&
            status.Capabilities.Contains(ConsentV2Capability)) {
            StartLoop();
            return;
        }
        StopLoop();
        // A daemon that answers without consent/2 is a different incarnation than the one those
        // entries came from, and it would resolve without the identity check — they can never be
        // safely answered against it. Disconnected states retain instead: the daemon may still be
        // alive holding live prompts.
        //
        // Under the same lock Upsert holds: this runs on the status thread, so an Upsert that had
        // already passed its tombstone test would otherwise land its insert AFTER the clear and
        // resurrect a previous incarnation's entry into a cache that must be empty.
        if (status.State == AttachState.Connected) lock (_lock) _cache.Clear();
    }

    void StartLoop() {
        CancellationTokenSource cts;
        lock (_lock) {
            if (_disposed || _loopCts is not null) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            _loopCts = cts;
        }
        _ = Task.Run(() => RunLoopAsync(cts));
    }

    void StopLoop() {
        CancellationTokenSource? cts;
        lock (_lock) {
            cts = _loopCts;
            _loopCts = null;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { } // already unwound and self-disposed
    }

    async Task RunLoopAsync(CancellationTokenSource cts) {
        var ct = cts.Token;
        try {
            while (!ct.IsCancellationRequested) {
                try {
                    await foreach (var evt in _subscribe(ct).WithCancellation(ct).ConfigureAwait(false)) {
                        ct.ThrowIfCancellationRequested(); // a superseded loop never touches the cache
                        switch (evt) {
                            case ConsentStreamEvent.Subscribed:  _cache.Clear();  break;
                            case ConsentStreamEvent.Pending p:   Upsert(p.Request); break;
                        }
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    // Never kill the loop: the enumeration ending badly is just "this attempt is
                    // over" — the retry below is the whole recovery story.
                    Console.Error.WriteLine($"kcap: consent subscription attempt failed: {ex.Message}");
                }

                try {
                    await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        } finally {
            cts.Dispose();
        }
    }

    void Upsert(ConsentPendingDto dto) {
        var deadline = DeadlineFor(dto);
        var entry    = new PendingConsent(dto, deadline, deadline + PruneGrace);
        bool added;
        // The tombstone test and the insert share one critical section with Conclude's
        // record-and-evict, so a ghost can never slip in between an ack's two halves.
        lock (_lock) {
            if (_tombstones.Contains(entry.PromptId)) return;
            added = _surfaced.Add(entry.PromptId);
            _cache.Edit(u => u.AddOrUpdate(entry));
        }
        if (added) _entryAdded.OnNext(Unit.Default);
    }

    /// Records the concluded identity and evicts it in one critical section. The eviction compares
    /// PromptIds, not object references, so a REPLAYED instance of the same request goes too — while
    /// a successor sharing only the RequestId survives (spec §5's ABA defense).
    void Conclude(PendingConsent target) {
        lock (_lock) {
            _tombstones.Add(target.PromptId);
            _cache.Edit(u => {
                if (u.Lookup(target.RequestId) is { HasValue: true, Value.PromptId: var id } && id == target.PromptId)
                    u.Remove(target.RequestId);
            });
        }
    }

    void RefreshPrune(PendingConsent target) {
        target.PruneAfter = _time.GetUtcNow() + PruneGrace;
        _cache.Edit(u => {
            if (u.Lookup(target.RequestId) is { HasValue: true, Value.PromptId: var id } && id == target.PromptId)
                u.AddOrUpdate(target);
        });
    }

    // Availability hygiene, not settlement — and deliberately NOT a tombstone: a request the app
    // merely gave up on locally is still live daemon-side (the clock-step case) and must be allowed
    // to reappear.
    void Prune() {
        var now = _time.GetUtcNow();
        try {
            // The in-flight marker is read INSIDE the section that edits: snapshotting it first
            // would let a resolve claim its target in the gap and still lose it mid-call, after
            // which a TransportFailure's refresh finds nothing cached and cannot re-add it.
            lock (_lock) {
                var inFlight = _inFlightPromptId;
                _cache.Edit(u => {
                    foreach (var stale in u.Items.Where(p => now > p.PruneAfter && p.PromptId != inFlight).ToList()) {
                        if (u.Lookup(stale.RequestId) is { HasValue: true, Value.PromptId: var id } && id == stale.PromptId)
                            u.Remove(stale.RequestId);
                    }
                });
            }
        } catch (Exception ex) {
            // The ticker is SHARED (tray, rows, countdowns): a throw from this handler would tear
            // down everyone's 1 Hz heartbeat, e.g. on a tick racing shutdown's cache disposal.
            Console.Error.WriteLine($"kcap: consent prune failed: {ex.Message}");
        }
    }

    DateTimeOffset DeadlineFor(ConsentPendingDto dto) {
        var anchor = DateTimeOffset.TryParse(
            dto.RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamped)
            ? stamped
            : _time.GetUtcNow();
        return anchor + TimeSpan.FromSeconds(dto.TimeoutSeconds);
    }
}
