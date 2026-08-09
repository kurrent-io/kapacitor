using System.Reactive;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// How a resolve settled. Only <see cref="TransportFailure"/> leaves the request pending; every
/// other value is conclusive (the entry is removed and its identity tombstoned). Caller
/// cancellation is not a value here — it propagates as OperationCanceledException (spec §5).
public enum ConsentResolveKind { Applied, AppliedRuleRejected, AlreadyDecided, RuleSkippedNoRequester, TransportFailure }

/// What became of the optional "remember this requester" rule. The service disambiguates because
/// only it knows whether a `save_rule` was actually sent: <see cref="Unknown"/> is a save that was
/// sent but not reported (unreachable behind the consent/2 gate, honest if it ever fires) and
/// <see cref="SkippedNoRequester"/> is a save the service refused to send at all.
public enum ConsentRuleOutcome { NotRequested, Saved, Rejected, Unknown, SkippedNoRequester }

/// <param name="Error">The ack's warning/failure detail, or the coded transport reason
/// (e.g. <c>daemon_unreachable</c>) on <see cref="ConsentResolveKind.TransportFailure"/>.</param>
public sealed record ConsentResolveOutcome(ConsentResolveKind Kind, ConsentRuleOutcome RuleOutcome, string? Error);

/// One pending consent request. <see cref="RequestId"/> is the cache key and the per-agent queue
/// identity (the daemon reuses it across launches); <see cref="PromptId"/> is the daemon-minted
/// REQUEST identity every cache guard and the resolve echo key on (spec §4.1).
public sealed class PendingConsent {
    internal PendingConsent(ConsentPendingDto dto, DateTimeOffset deadlineHint, DateTimeOffset pruneAfter) {
        Dto          = dto;
        RequestId    = dto.RequestId;
        PromptId     = dto.PromptId!; // ConsentSubscription's structural validation guarantees it
        DeadlineHint = deadlineHint;
        PruneAfter   = pruneAfter;
    }

    public ConsentPendingDto Dto { get; }
    public string RequestId { get; }
    public string PromptId { get; }

    /// `RequestedAt + TimeoutSeconds` — a HEURISTIC, never an outcome. The daemon enforces its
    /// timeout on a monotonic clock; this is wall-clock metadata, so a clock step can make the two
    /// disagree in either direction. Only a resolve ack settles a request.
    public DateTimeOffset DeadlineHint { get; }

    public DateTimeOffset PruneAfter { get; internal set; }
}

public interface IConsentService : IDisposable {
    /// Mutated on background continuations (the subscription loop and resolve completions) —
    /// consumers marshal with ObserveOn(RxSchedulers.MainThreadScheduler).
    IObservable<IChangeSet<PendingConsent, string>> Pending { get; }

    IObservable<int> PendingCount { get; }

    /// Fires once per newly-keyed entry, unconditionally: the service knows nothing about windows,
    /// so the prompt-window coordinator is what filters by visibility (spec §5/§6).
    IObservable<Unit> EntryAdded { get; }

    Task<ConsentResolveOutcome> ResolveAsync(PendingConsent target, bool allow, bool saveRule, CancellationToken ct);
}
