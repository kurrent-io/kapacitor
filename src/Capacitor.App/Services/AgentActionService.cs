using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Shared by the tray menu and the main-window rows (spec §7: one code path) — the single place
/// that calls ILocalControlOps.StopAgentAsync and builds the open-in-web URL, so both surfaces
/// get identical in-flight gating, banner text, and link construction. No local cache mutation:
/// a stopped agent's disappearance comes only from the next daemon snapshot (spec §7).
public sealed class AgentActionService {
    const string DaemonUnreachableReason = "daemon_unreachable";
    const string UnreachableCopy         = "The daemon is not reachable";

    readonly ILocalControlOps _ops;
    readonly IAppNotifier _notifier;
    readonly IUrlOpener _opener;
    readonly CancellationToken _shutdownToken;

    // ONE lock guards both the in-flight set and the latest server URL — both are cheap,
    // occasional writes, never held across the async stop call itself.
    readonly Lock _lock = new();
    ImmutableHashSet<string> _inFlight = ImmutableHashSet<string>.Empty;
    string? _serverUrl;

    readonly BehaviorSubject<IReadOnlySet<string>> _stopsInFlight;

    public AgentActionService(
            ILocalControlOps ops, IAppNotifier notifier, IUrlOpener opener,
            IObservable<DaemonStatusDto> snapshots, CancellationToken shutdownToken) {
        _ops = ops;
        _notifier = notifier;
        _opener = opener;
        _shutdownToken = shutdownToken;
        _stopsInFlight = new BehaviorSubject<IReadOnlySet<string>>(_inFlight);

        // Held for the service's lifetime, same as TrayViewModel's constructor-scoped
        // subscriptions — this service is a singleton for the app's lifetime, never disposed
        // mid-run, so there is no unsubscribe seam to wire up.
        snapshots.Subscribe(s => { lock (_lock) _serverUrl = s.Daemon.ServerUrl; });
    }

    /// Replay-1, starts empty. Consumed by TrayViewModel (and later the main-window grid) to
    /// disable a Stop control while its op is pending (spec §7).
    public IObservable<IReadOnlySet<string>> StopsInFlight => _stopsInFlight.AsObservable();

    /// Per-id gating: a second Stop for the same id no-ops while one is pending; different ids
    /// run concurrently (spec §7). Never throws — this is a UI command target, not a Task the
    /// caller awaits.
    public void RequestStop(string agentId, string label) {
        lock (_lock) {
            if (_inFlight.Contains(agentId)) return;
            _inFlight = _inFlight.Add(agentId);
            _stopsInFlight.OnNext(_inFlight);
        }
        _ = Task.Run(() => RunStopAsync(agentId, label));
    }

    async Task RunStopAsync(string agentId, string label) {
        try {
            var result = await _ops.StopAgentAsync(agentId, force: false, _shutdownToken).ConfigureAwait(false);
            switch (result.Status) {
                case "stopped": break; // no banner — disappearance from the next snapshot is the confirmation
                case "failed":  _notifier.Notify($"Couldn't stop {label}"); break;
                case "skipped": _notifier.Notify($"The daemon declined to stop {label}"); break;
                case "error":   _notifier.Notify(result.Error!); break; // daemon text, verbatim
            }
        } catch (OperationCanceledException) {
            // Deliberate shutdown: absorbed quietly, no banner, no log.
        } catch (LocalControlOpsException ex) {
            _notifier.Notify(ex.Reason == DaemonUnreachableReason ? UnreachableCopy : $"Couldn't stop {label}: {ex.Message}");
        } catch (Exception ex) {
            // An unmapped exception still gets a banner (never a silent drop) — AppNotifier.Notify
            // covers both the banner AND stderr (spec §11), so this one call satisfies both
            // without a separate Console.Error write. The finally below still runs either way.
            _notifier.Notify($"Couldn't stop {label}: {ex.Message}");
        } finally {
            // A completion into an already-vanished row/entry is naturally a no-op — the set
            // just loses a member nothing is rendering against anymore.
            lock (_lock) {
                _inFlight = _inFlight.Remove(agentId);
                _stopsInFlight.OnNext(_inFlight);
            }
        }
    }

    /// Opens {ServerUrl trimmed of trailing '/'}/agents/{Uri.EscapeDataString(id)} from the
    /// latest snapshot in the default browser (spec §7). Never throws.
    public void OpenInWeb(string agentId) {
        string? serverUrl;
        lock (_lock) serverUrl = _serverUrl;

        if (serverUrl is null) {
            _notifier.Notify("Not connected to a daemon yet"); // cannot happen from live UI; defensive only
            return;
        }

        var url = $"{serverUrl.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentId)}";
        try {
            _opener.Open(url);
        } catch (Exception ex) {
            _notifier.Notify($"Couldn't open the browser: {ex.Message}");
        }
    }
}
