using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Capacitor.Cli.Daemon.Services;

/// In-memory rendezvous between the consent gate (awaiting a verdict) and local-socket
/// subscribers (the desktop app / kcap daemon consent). Resolution is CLAIM-based: TryResolve
/// only succeeds if it wins the race to remove the pending entry, so a successful TryResolve
/// (surfaced to the IPC caller as Ok=true) is a hard guarantee the decision actually applied to
/// the launch. PromptAsync's own timeout/cancellation path performs the same claiming removal
/// before giving up — if it loses that race to a concurrent TryResolve, it awaits and returns
/// the resolver's verdict instead of a stale timeout denial. A request vanishes on resolve or
/// timeout, whichever claims it first; expiry surfaces as TryResolve returning false. Never
/// persisted — a daemon restart clears pending prompts (the server retries or fails the launch
/// with the coded timeout denial).
///
/// Delivery to each subscriber is exactly-once per request (replay xor broadcast, never both),
/// enforced by the _deliveryGate lock. There is NO withdrawal push — subscribers learn of
/// expiration only when TryResolve returns false (Task 7's consumer relies on this).
internal sealed class LaunchConsentBroker : ILaunchConsentPrompter {
    sealed record Pending(LaunchConsentPromptRequest Request, TaskCompletionSource<bool> Tcs);

    readonly ConcurrentDictionary<string, Pending> _pending = new();
    readonly ConcurrentDictionary<Guid, Channel<LaunchConsentPromptRequest>> _subscribers = new();
    readonly object _deliveryGate = new();

    public bool HasSubscriber => !_subscribers.IsEmpty;

    public (Guid id, ChannelReader<LaunchConsentPromptRequest> reader) Subscribe() {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<LaunchConsentPromptRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        lock (_deliveryGate) {
            foreach (var p in _pending.Values) ch.Writer.TryWrite(p.Request);
            _subscribers[id] = ch;
        }
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id) {
        if (_subscribers.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public async Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_deliveryGate) {
            if (!_pending.TryAdd(req.RequestId, new Pending(req, tcs))) return null;
            foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(req);
        }
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try {
                return await tcs.Task.WaitAsync(cts.Token);
            } catch (OperationCanceledException) {
                // The wait timed out (or ct fired), but TryResolve claims the entry by REMOVING
                // it — so race it the same way here. If OUR removal wins, no resolver got there
                // first: the timeout is real, deny. If it FAILS, a resolver already claimed the
                // entry and is completing (or has completed) tcs — honor that decision instead of
                // denying a request a human/rule just answered inside the race window.
                if (_pending.TryRemove(req.RequestId, out _))
                    return tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : null;
                return await tcs.Task;
            }
        } finally {
            _pending.TryRemove(req.RequestId, out _);
        }
    }

    public bool TryResolve(string requestId, bool allow) =>
        _pending.TryRemove(requestId, out var p) && p.Tcs.TrySetResult(allow);

    public IReadOnlyList<LaunchConsentPromptRequest> PendingSnapshot() =>
        _pending.Values.Select(p => p.Request).ToList();
}
