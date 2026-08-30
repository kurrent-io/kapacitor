using System.Collections.Concurrent;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

internal readonly record struct PermissionSettlement(PermissionDecision Decision, string Outcome, string Source);

internal abstract record PermissionStreamItem {
    public sealed record Pending(PermissionPendingDto Dto) : PermissionStreamItem;
    public sealed record Resolved(PermissionResolvedDto Dto) : PermissionStreamItem;
}

internal static class PermissionSettlements {
    public const string Allow = "allow", Deny = "deny", Withdrawn = "withdrawn";
    public const string SourceApp = "app", SourceServer = "server", SourceAgentGone = "agent_gone",
                        SourceNoUi = "no_ui", SourceDaemonShutdown = "daemon_shutdown";
    public static readonly PermissionDecision DenyDecision = new("deny", null, null);
}

/// The single claim point for a hosted permission request. Every settlement — the app, the
/// server's push, an agent's withdrawal, the no-UI deny, the shutdown claim — goes through
/// TrySettle under one gate that replay/registration also takes, so a subscriber observes a
/// request as nothing, or Pending then Resolved, never Pending alone. The withdrawn set is
/// service-lifetime: agent ids are never reused, so it can never suppress a future agent.
internal sealed class PermissionPromptBroker {
    sealed record Entry(PermissionPendingDto Dto, TaskCompletionSource<PermissionSettlement> Tcs);

    readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<Guid, Channel<PermissionStreamItem>> _subscribers = new();
    readonly HashSet<string> _withdrawnAgents = new(StringComparer.Ordinal);
    readonly object _gate = new();

    public bool HasSubscriber => !_subscribers.IsEmpty;

    public Task<PermissionSettlement> Register(PermissionPendingDto dto) {
        lock (_gate) {
            if (_withdrawnAgents.Contains(dto.AgentId))
                return Task.FromResult(new PermissionSettlement(
                    PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone));

            // Completed while the gate is held: a continuation running inline would re-enter it.
            var entry = new Entry(dto, new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!_pending.TryAdd(dto.RequestId, entry))
                throw new InvalidOperationException($"permission request {dto.RequestId} is already pending");
            Broadcast(new PermissionStreamItem.Pending(dto));
            return entry.Tcs.Task;
        }
    }

    public bool TrySettle(string requestId, PermissionDecision decision, string outcome, string source) {
        lock (_gate) return SettleLocked(requestId, decision, outcome, source);
    }

    public bool TrySettleIfNoSubscriber(string requestId, PermissionDecision decision, string outcome, string source) {
        lock (_gate) return _subscribers.IsEmpty && SettleLocked(requestId, decision, outcome, source);
    }

    public (Guid id, ChannelReader<PermissionStreamItem> reader) Subscribe() {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<PermissionStreamItem>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) {
            foreach (var e in _pending.Values) ch.Writer.TryWrite(new PermissionStreamItem.Pending(e.Dto));
            _subscribers[id] = ch;
        }
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id) {
        Channel<PermissionStreamItem>? ch;
        lock (_gate) _subscribers.TryRemove(id, out ch);
        ch?.Writer.TryComplete();
    }

    public void WithdrawForAgent(string agentId) {
        lock (_gate) {
            _withdrawnAgents.Add(agentId);
            foreach (var e in _pending.Values.Where(e => e.Dto.AgentId == agentId).ToList())
                SettleLocked(e.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone);
        }
    }

    public IReadOnlyList<PermissionPendingDto> PendingSnapshot() {
        lock (_gate) return _pending.Values.Select(e => e.Dto).ToList();
    }

    // Caller holds _gate. Instance-scoped removal, the consent broker's discipline.
    bool SettleLocked(string requestId, PermissionDecision decision, string outcome, string source) {
        if (!_pending.TryGetValue(requestId, out var entry)) return false;
        if (!_pending.TryRemove(new KeyValuePair<string, Entry>(requestId, entry))) return false;
        Broadcast(new PermissionStreamItem.Resolved(new PermissionResolvedDto(requestId, outcome, source)));
        entry.Tcs.TrySetResult(new PermissionSettlement(decision, outcome, source));
        return true;
    }

    void Broadcast(PermissionStreamItem item) {
        foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(item);
    }
}
