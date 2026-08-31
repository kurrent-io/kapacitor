using System.Reactive.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// Sole owner of the pending-permission cache. One lock guards the tombstone set and every
/// cache mutation: the tombstone test + upsert, the tombstone add + evict (on an ack and on a
/// Resolved push), the Connected-without-capability clear, the Subscribed clear and the disposed
/// flag. The stream loop, the status subscription and ResolveAsync run on different
/// continuations, and this lock is what makes the ordering hold. Tombstones live for the
/// service lifetime: request ids are never reused, so one can never suppress a future request.
public sealed class PermissionService : IPermissionService {
    const string Capability = "permission/1";
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    readonly SourceCache<PendingPermissionRequest, string> _cache = new(p => p.RequestId);
    readonly HashSet<string> _tombstones = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    readonly ILocalControlOps _ops;
    readonly Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> _subscribe;
    readonly TimeProvider _time;
    readonly CancellationToken _shutdownToken;
    readonly IDisposable _statusSub;
    CancellationTokenSource? _loopCts;
    bool _disposed;

    public PermissionService(
            IDaemonClientService service, ILocalControlOps ops,
            Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> subscribe,
            TimeProvider time, CancellationToken shutdownToken) {
        _ops = ops; _subscribe = subscribe; _time = time; _shutdownToken = shutdownToken;
        _statusSub = service.Status.Subscribe(OnStatus);
    }

    public IObservable<IChangeSet<PendingPermissionRequest, string>> Pending => _cache.Connect();
    public IObservable<int> PendingCount => _cache.CountChanged;
    public IObservable<IReadOnlySet<string>> AgentsWithPending =>
        _cache.Connect()
            .QueryWhenChanged(q => (IReadOnlySet<string>)q.Items.Select(p => p.AgentId).ToHashSet(StringComparer.Ordinal))
            .StartWith((IReadOnlySet<string>)_cache.Items.Select(p => p.AgentId).ToHashSet(StringComparer.Ordinal));

    public async Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct) {
        var decision = answer == PermissionAnswer.Deny ? "deny" : "allow";
        var apply = answer == PermissionAnswer.AllowAlways ? ClaudePermissions.AlwaysAllow(target.ToolName) : (System.Text.Json.JsonElement?)null;
        var dto = new PermissionResolveDto(target.RequestId, decision, apply, null);

        PermissionAckDto ack;
        try {
            ack = await _ops.ResolvePermissionAsync(dto, ct).ConfigureAwait(false);
        } catch (LocalControlOpsException ex) {
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Reason);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: permission resolve failed unexpectedly: {ex.Message}");
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Message);
        }

        Conclude(target.RequestId);
        return new PermissionResolveOutcome(ack.Ok ? PermissionResolveKind.Applied : PermissionResolveKind.AlreadyDecided, ack.Error);
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _statusSub.Dispose();
        StopLoop();
        _cache.Dispose();
    }

    void OnStatus(AttachStatus status) {
        if (status is { State: AttachState.Connected, Capabilities: not null } && status.Capabilities.Contains(Capability)) {
            StartLoop();
            return;
        }
        StopLoop();
        // A Connected daemon without the capability is a different incarnation; disconnected retains.
        if (status.State == AttachState.Connected) lock (_lock) { if (!_disposed) _cache.Clear(); }
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
        lock (_lock) { cts = _loopCts; _loopCts = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task RunLoopAsync(CancellationTokenSource cts) {
        var ct = cts.Token;
        try {
            while (!ct.IsCancellationRequested) {
                try {
                    await foreach (var evt in _subscribe(ct).WithCancellation(ct).ConfigureAwait(false)) {
                        ct.ThrowIfCancellationRequested();
                        switch (evt) {
                            case PermissionStreamEvent.Subscribed: lock (_lock) { if (!_disposed) _cache.Clear(); } break;
                            case PermissionStreamEvent.Pending p:  Upsert(p.Request); break;
                            case PermissionStreamEvent.Resolved r: Conclude(r.Settlement.RequestId); break;
                        }
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"kcap: permission subscription attempt failed: {ex.Message}");
                }
                try { await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        } finally {
            cts.Dispose();
        }
    }

    void Upsert(PermissionPendingDto dto) {
        lock (_lock) {
            if (_disposed || _tombstones.Contains(dto.RequestId)) return;
            _cache.AddOrUpdate(new PendingPermissionRequest(dto));
        }
    }

    void Conclude(string requestId) {
        lock (_lock) {
            if (_disposed) return;
            _tombstones.Add(requestId);
            _cache.Remove(requestId);
        }
    }
}
