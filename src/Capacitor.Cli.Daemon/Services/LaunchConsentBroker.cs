using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Capacitor.Cli.Daemon.Services;

/// In-memory rendezvous between the consent gate (awaiting a verdict) and local-socket
/// subscribers (the desktop app / kcap daemon consent). First resolution wins; a request
/// vanishes on resolve or timeout. Never persisted — a daemon restart clears pending prompts
/// (the server retries or fails the launch with the coded timeout denial).
internal sealed class LaunchConsentBroker : ILaunchConsentPrompter {
    sealed record Pending(LaunchConsentPromptRequest Request, TaskCompletionSource<bool> Tcs);

    readonly ConcurrentDictionary<string, Pending> _pending = new();
    readonly ConcurrentDictionary<Guid, Channel<LaunchConsentPromptRequest>> _subscribers = new();

    public bool HasSubscriber => !_subscribers.IsEmpty;

    public (Guid id, ChannelReader<LaunchConsentPromptRequest> reader) Subscribe() {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<LaunchConsentPromptRequest>(
            new UnboundedChannelOptions { SingleReader = true });
        foreach (var p in _pending.Values) ch.Writer.TryWrite(p.Request);
        _subscribers[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id) {
        if (_subscribers.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public async Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, CancellationToken ct) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(req.RequestId, new Pending(req, tcs))) return null;
        try {
            foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(req);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { return await tcs.Task.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { return null; }
        } finally {
            _pending.TryRemove(req.RequestId, out _);
        }
    }

    public bool TryResolve(string requestId, bool allow) =>
        _pending.TryGetValue(requestId, out var p) && p.Tcs.TrySetResult(allow);

    public IReadOnlyList<LaunchConsentPromptRequest> PendingSnapshot() =>
        _pending.Values.Select(p => p.Request).ToList();
}
