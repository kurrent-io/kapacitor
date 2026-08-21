using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Capacitor.Cli.Daemon.Services;

/// In-memory rendezvous between the consent gate (awaiting a verdict) and local-socket
/// subscribers (the desktop app / kcap daemon consent). Resolution is CLAIM-based: TryResolve
/// only succeeds if it wins the race to remove the pending entry, so a successful TryResolve
/// (surfaced to the IPC caller as Ok=true) is a hard guarantee the decision actually applied to
/// the launch. PromptAsync's own TIMEOUT path performs the same claiming removal before giving
/// up — if it loses that race to a concurrent TryResolve, it awaits and returns the resolver's
/// verdict instead of a stale timeout denial. EXTERNAL CANCELLATION (the caller's own `ct` firing
/// — daemon shutdown / launch teardown, distinguished from a timeout by `ct.IsCancellationRequested`)
/// performs the identical claim-or-defer removal but then RETHROWS OperationCanceledException
/// instead of ever producing a verdict — the gate must abort without fabricating a decision, so
/// no decision (and no decision-log record) is ever produced for a launch torn down before
/// deciding. A request vanishes on resolve, timeout, or external cancellation, whichever claims
/// it first; expiry surfaces as TryResolve returning false. Never persisted — a daemon restart
/// clears pending prompts (the server retries or fails the launch with the coded timeout denial).
///
/// EVERY removal — PromptAsync's timeout claim, its OCE-catch claim attempt, its outer finally,
/// and TryResolve's own claim (echo-matched or legacy) — is INSTANCE-scoped
/// via the ConcurrentDictionary KeyValuePair-conditional overload, keyed on the exact `Pending`
/// object PromptAsync added — never a plain key-based remove. This closes an ABA race: if a new
/// prompt B reuses the same RequestId (agent-id retry, legacy/sequenced lane overlap) and TryAdds
/// its own entry before request A's cleanup runs, A's cleanup can only ever remove A's own
/// instance — it is structurally incapable of evicting B's. A successor prompt with the same id
/// is therefore never evicted by a predecessor's cleanup.
///
/// Delivery to each subscriber is exactly-once per request (replay xor broadcast, never both),
/// enforced by the _deliveryGate lock. There is NO withdrawal push — subscribers learn of
/// expiration only when TryResolve returns false (Task 7's consumer relies on this).
internal sealed class LaunchConsentBroker : ILaunchConsentPrompter {
    sealed record Pending(LaunchConsentPromptRequest Request, TaskCompletionSource<bool> Tcs);

    readonly ConcurrentDictionary<string, Pending> _pending = new();
    readonly ConcurrentDictionary<Guid, Channel<LaunchConsentPromptRequest>> _subscribers = new();
    readonly object _deliveryGate = new();

    // "The next 0→1 subscriber transition." One instance is shared by every concurrent waiter in
    // the current zero-subscriber generation. Created at construction and re-armed (a fresh
    // incomplete instance) on each 1→0 transition in Unsubscribe; COMPLETED — never replaced — by
    // the 0→1 transition in Subscribe. A waiter's own timeout or cancellation must never complete
    // or replace it, so an earlier generation's completed source can never satisfy a later
    // zero-subscriber wait. All transitions happen under _deliveryGate.
    //
    // RunContinuationsAsynchronously is mandatory, not a nicety: TrySetResult() is called from
    // Subscribe() while _deliveryGate is held. Without it, a waiter's continuation (which for
    // WaitForSubscriberAsync's caller is the gate's prompt path) would run INLINE on the
    // Subscribe() call stack, still inside the lock — anything that continuation does (including,
    // transitively, another attempt to take _deliveryGate) would then deadlock against the very
    // lock Subscribe() is holding.
    TaskCompletionSource _subscriberArrival = NewArrival();
    static TaskCompletionSource NewArrival() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HasSubscriber => !_subscribers.IsEmpty;

    public (Guid id, ChannelReader<LaunchConsentPromptRequest> reader) Subscribe() {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<LaunchConsentPromptRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        lock (_deliveryGate) {
            foreach (var p in _pending.Values) ch.Writer.TryWrite(p.Request);
            _subscribers[id] = ch;
            if (_subscribers.Count == 1) _subscriberArrival.TrySetResult();
        }
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id) {
        Channel<LaunchConsentPromptRequest>? ch;
        lock (_deliveryGate) {
            // Re-arm only on an ACTUAL 1→0 transition. An unknown/duplicate id on an already-empty
            // map is a no-op remove, not a transition — re-arming anyway would orphan every waiter
            // already holding the current generation's source: they'd never see the fresh instance
            // Subscribe() later completes, and would have to burn their full wait budget before the
            // timeout recheck rescues them.
            if (_subscribers.TryRemove(id, out ch) && _subscribers.IsEmpty) _subscriberArrival = NewArrival();
        }
        ch?.Writer.TryComplete();
    }

    public async Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) {
        TaskCompletionSource arrival;
        lock (_deliveryGate) {
            if (!_subscribers.IsEmpty) return true;
            arrival = _subscriberArrival;
        }
        try {
            await arrival.Task.WaitAsync(wait, time, ct);
            return true;
        } catch (TimeoutException) {
            // Arrival wins ties: a subscriber that landed inside the race window counts.
            lock (_deliveryGate) return !_subscribers.IsEmpty;
        }
    }

    public async Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Pending(req, tcs);
        lock (_deliveryGate) {
            if (!_pending.TryAdd(req.RequestId, pending)) return null;
            foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(req);
        }
        try {
            try {
                // TimeProvider-aware: the same injected clock drives this wait as
                // WaitForSubscriberAsync's, so a controlled provider deterministically drives
                // every timeout on this path in tests (spec §3.2).
                return await tcs.Task.WaitAsync(timeout, time, ct);
            } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) {
                if (ex is OperationCanceledException && ct.IsCancellationRequested) {
                    // External teardown (the caller's own token — daemon shutdown / launch
                    // cancellation), NOT a timeout: claim-or-defer exactly like the timeout path
                    // below, then RETHROW — the gate must abort without fabricating a decision,
                    // so no verdict (and no decision-log record) is ever produced for a launch
                    // torn down before deciding.
                    if (!_pending.TryRemove(new KeyValuePair<string, Pending>(req.RequestId, pending)))
                        { try { await tcs.Task; } catch { } }
                    throw;
                }
                // The wait timed out, but TryResolve claims the entry by REMOVING it — so race it
                // the same way here, and do it INSTANCE-scoped (key + value) so this can only ever
                // remove OUR OWN entry, never a same-id successor prompt that TryAdded after we
                // were already claimed (see the class doc's ABA note). If OUR removal wins, no
                // resolver got there first: the timeout is real, deny. If it FAILS, a resolver
                // already claimed our instance and is completing (or has completed) tcs — honor
                // that decision instead of denying a request a human/rule just answered inside the
                // race window. A same-id entry can only exist at this point if OUR instance was
                // already removed by a resolver, so awaiting tcs here still completes with that
                // resolver's verdict, never a successor's.
                if (_pending.TryRemove(new KeyValuePair<string, Pending>(req.RequestId, pending)))
                    return tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : null;
                return await tcs.Task;
            }
        } finally {
            // Instance-scoped for the same reason as above: an unconditional key-based remove
            // here would evict a same-id successor's entry out from under it (ABA).
            _pending.TryRemove(new KeyValuePair<string, Pending>(req.RequestId, pending));
        }
    }

    public bool TryResolve(string requestId, bool allow) => TryResolve(requestId, allow, null);

    /// Non-null echo: resolve succeeds only for the pending entry whose PromptId matches
    /// exactly, and match+removal is ONE atomic claim — the KeyValuePair-conditional remove is
    /// the same ABA primitive the timeout/cleanup paths use (class doc). A lost claim (the
    /// matched instance replaced between lookup and removal) returns false and NEVER retries
    /// against the successor. Null echo: legacy resolve-by-id (v1 frame callers).
    public bool TryResolve(string requestId, bool allow, string? promptIdEcho) {
        if (!_pending.TryGetValue(requestId, out var p)) return false;
        if (promptIdEcho is not null &&
            !string.Equals(promptIdEcho, p.Request.PromptId, StringComparison.Ordinal)) return false;
        if (!_pending.TryRemove(new KeyValuePair<string, Pending>(requestId, p))) return false;
        return p.Tcs.TrySetResult(allow);
    }

    public IReadOnlyList<LaunchConsentPromptRequest> PendingSnapshot() =>
        _pending.Values.Select(p => p.Request).ToList();
}
