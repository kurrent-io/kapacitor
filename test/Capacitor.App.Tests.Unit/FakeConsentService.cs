using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// PendingConsent fixtures shared by the prompt ViewModel and coordinator suites. DeadlineHint /
/// PruneAfter are computed exactly as ConsentService computes them (anchor + timeout, + 5s grace),
/// so the ViewModel's sort key (which derives the anchor back out of the hint) stays truthful.
static class ConsentEntries {
    public static readonly DateTimeOffset T0 = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    public static PendingConsent Entry(
            string requestId = "a1", string promptId = "p1", string? requester = "github:1",
            string? requesterDisplay = "Alice", string kind = "agent", string repoPath = "/repos/kcap-cli",
            string vendor = "claude", DateTimeOffset? requestedAt = null, int timeoutSeconds = 30) {
        var anchor = requestedAt ?? T0;
        var dto = new ConsentPendingDto(
            requestId, requester, kind, repoPath, vendor, anchor.ToString("O"), timeoutSeconds,
            requesterDisplay, promptId);
        var deadline = anchor + TimeSpan.FromSeconds(timeoutSeconds);
        return new PendingConsent(dto, deadline, deadline + TimeSpan.FromSeconds(5));
    }
}

/// Scripted IConsentService: a real SourceCache plus a per-call TaskCompletionSource queue for
/// ResolveAsync, so a test arms the NEXT resolve's outcome (or holds it open) before clicking.
/// Two behaviors deliberately mirror the real service (spec §5), because the ViewModel's advance
/// rules are only honest against them: a conclusive outcome evicts its target from the cache
/// (identity-guarded) BEFORE the awaiting caller resumes, and a transport failure keeps it.
/// EntryAdded fires on the FIRST SURFACING of a PromptId — a same-key successor fires, a replay
/// after <see cref="Clear"/> does not — from whatever thread Add is called on.
sealed class FakeConsentService : IConsentService {
    public readonly SourceCache<PendingConsent, string> Cache = new(p => p.RequestId);

    readonly Subject<ReactiveUnit> _entryAdded = new();
    readonly HashSet<string> _surfaced = [];
    readonly Queue<TaskCompletionSource<ConsentResolveOutcome>> _outcomes = new();
    readonly List<PendingConsent> _deferredConclusions = [];

    public readonly List<(string PromptId, bool Allow, bool SaveRule)> Resolved = [];

    /// Withholds the conclusive eviction until <see cref="FlushConclusions"/>, modelling the
    /// ordering the ViewModel cannot assume away: the cache edit and the ack's continuation are
    /// two independently posted jobs, so the ack can be observed while the queue view still holds
    /// the concluded entry.
    public bool ConcludeLate;

    public IObservable<IChangeSet<PendingConsent, string>> Pending => Cache.Connect();
    public IObservable<int> PendingCount => Cache.CountChanged;
    public IObservable<ReactiveUnit> EntryAdded => _entryAdded;

    public void Add(PendingConsent entry) {
        bool isNew;
        lock (_surfaced) isNew = _surfaced.Add(entry.PromptId);
        Cache.AddOrUpdate(entry);
        if (isNew) _entryAdded.OnNext(ReactiveUnit.Default);
    }

    /// The §5 prune: an entry disappearing under the ViewModel with no ack involved.
    public void Prune(PendingConsent entry) => Cache.Remove(entry.RequestId);

    /// The §5 Subscribed boundary: the cache is emptied, the daemon's replay re-adds through
    /// <see cref="Add"/>. Surfaced identities survive it, exactly as in the real service.
    public void Clear() => Cache.Clear();

    public TaskCompletionSource<ConsentResolveOutcome> Arm() {
        var tcs = new TaskCompletionSource<ConsentResolveOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outcomes.Enqueue(tcs);
        return tcs;
    }

    public void Queue(ConsentResolveKind kind, ConsentRuleOutcome rule, string? error = null) =>
        Arm().SetResult(new ConsentResolveOutcome(kind, rule, error));

    public void QueueCancellation() => Arm().SetCanceled();

    public async Task<ConsentResolveOutcome> ResolveAsync(
            PendingConsent target, bool allow, bool saveRule, CancellationToken ct) {
        Resolved.Add((target.PromptId, allow, saveRule));
        if (_outcomes.Count == 0) throw new InvalidOperationException("FakeConsentService: unscripted resolve call");

        var outcome = await _outcomes.Dequeue().Task; // cancellation propagates as OCE, same as the real lane
        if (outcome.Kind == ConsentResolveKind.TransportFailure) return outcome;

        if (ConcludeLate) _deferredConclusions.Add(target); else Conclude(target);
        return outcome;
    }

    public void FlushConclusions() {
        foreach (var target in _deferredConclusions) Conclude(target);
        _deferredConclusions.Clear();
    }

    public void Dispose() {
        Cache.Dispose();
        _entryAdded.Dispose();
    }

    void Conclude(PendingConsent target) =>
        Cache.Edit(u => {
            if (u.Lookup(target.RequestId) is { HasValue: true, Value.PromptId: var id } && id == target.PromptId)
                u.Remove(target.RequestId);
        });
}
