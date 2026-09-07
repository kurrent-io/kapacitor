using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Shared by the tray menu and the main-window rows (spec §7: one code path) — the single place
/// that calls ILocalControlOps.StopAgentAsync and builds the open-in-web URL, so both surfaces
/// get identical in-flight gating, toast text, and link construction. No local cache mutation:
/// a stopped agent's disappearance comes only from the next daemon snapshot (spec §7).
public sealed class AgentActionService {
    const string DaemonUnreachableReason = "daemon_unreachable";
    const string UnreachableCopy         = "The daemon is not reachable";

    readonly ILocalControlOps _ops;
    readonly IAppNotifier _notifier;
    readonly IUrlOpener _opener;
    readonly CancellationToken _shutdownToken;
    readonly Func<string, Task<bool>> _confirmForceStop;

    // ONE lock guards both the in-flight set and the latest server URL — both are cheap,
    // occasional writes, never held across the async stop call itself.
    readonly Lock _lock = new();
    ImmutableHashSet<string> _inFlight = ImmutableHashSet<string>.Empty;
    string? _serverUrl;

    readonly BehaviorSubject<IReadOnlySet<string>> _stopsInFlight;

    /// <param name="confirmForceStop">
    /// The confirm-then-force seam for a protected kind (decision 5): invoked with the agent's
    /// label, resolves true to proceed with force:true, false to no-op. The composed delegate is
    /// UI glue (a dialog Window) — this service only awaits it, never marshals to the UI thread
    /// itself; that is the caller's job (App.axaml.cs, via Dispatcher.UIThread.InvokeAsync).
    /// </param>
    /// <param name="fallbackServerUrl">
    /// OpenInWeb's server URL before any local snapshot has ever arrived — a remote row with the
    /// local daemon down otherwise has no ServerUrl to build a link from. Null keeps today's
    /// behavior for every caller without a resolved profile.
    /// </param>
    public AgentActionService(
            ILocalControlOps ops, IAppNotifier notifier, IUrlOpener opener,
            IObservable<DaemonStatusDto> snapshots, CancellationToken shutdownToken,
            Func<string, Task<bool>> confirmForceStop, string? fallbackServerUrl = null) {
        _ops = ops;
        _notifier = notifier;
        _opener = opener;
        _shutdownToken = shutdownToken;
        _confirmForceStop = confirmForceStop;
        _stopsInFlight = new BehaviorSubject<IReadOnlySet<string>>(_inFlight);
        _serverUrl = fallbackServerUrl;

        // Held for the service's lifetime, same as TrayViewModel's constructor-scoped
        // subscriptions — this service is a singleton for the app's lifetime, never disposed
        // mid-run, so there is no unsubscribe seam to wire up.
        snapshots.Subscribe(s => { lock (_lock) _serverUrl = s.Daemon.ServerUrl; });
    }

    /// Replay-1, starts empty. Consumed by TrayViewModel (and later the main-window grid) to
    /// disable a Stop control while its op is pending (spec §7).
    public IObservable<IReadOnlySet<string>> StopsInFlight => _stopsInFlight.AsObservable();

    /// A kind other than exactly "agent" is protected (KindText vocabulary: agent|review|
    /// review-flow). Any non-"agent" value — including one this build doesn't recognise — fails
    /// safe as protected, mirroring the daemon's own `Kind != LaunchKind.Default` check and the
    /// CLI's `IsProtectedKind`.
    internal static bool IsProtectedKind(string kind) => kind is not "agent";

    /// Per-id gating: a second Stop for the same id no-ops while one is pending — including while
    /// a protected kind's confirm-then-force dialog is still open, since the id stays in-flight
    /// for the whole RunStopAsync call — different ids run concurrently (spec §7, decision 5).
    /// Never throws — this is a UI command target, not a Task the caller awaits.
    public void RequestStop(string agentId, string label, string kind) {
        lock (_lock) {
            if (_inFlight.Contains(agentId)) return;
            _inFlight = _inFlight.Add(agentId);
            _stopsInFlight.OnNext(_inFlight);
        }
        _ = Task.Run(() => RunStopAsync(agentId, label, kind));
    }

    async Task RunStopAsync(string agentId, string label, string kind) {
        try {
            var force = false;
            if (IsProtectedKind(kind)) {
                // false (dialog cancelled) is a quiet no-op — the finally below still clears the
                // in-flight entry, but nothing is sent to the daemon and no toast is shown.
                if (!await _confirmForceStop(label).ConfigureAwait(false)) return;
                force = true;
            }

            var result = await _ops.StopAgentAsync(agentId, force, _shutdownToken).ConfigureAwait(false);
            switch (result.Status) {
                case "stopped": break; // no toast — disappearance from the next snapshot is the confirmation
                case "failed":  _notifier.Notify($"Couldn't stop {label}"); break;
                case "skipped": _notifier.Notify($"The daemon declined to stop {label}"); break;
                case "error":
                    // The daemon's Error text may name an id or CLI-speak ("Pass --force…") the
                    // app cannot act on (spec §7) — never surfaced verbatim in the UI. The full
                    // text goes to stderr only; the toast stays generic. With force-after-confirm
                    // above, a legitimate protected-refusal Error should no longer occur — this
                    // now covers unknown-id/stale cases.
                    Console.Error.WriteLine($"kcap: stop {agentId} failed: {result.Error}");
                    _notifier.Notify($"Couldn't stop {label}");
                    break;
            }
        } catch (OperationCanceledException) {
            // Deliberate shutdown: absorbed quietly, no toast, no log.
        } catch (LocalControlOpsException ex) {
            _notifier.Notify(ex.Reason == DaemonUnreachableReason ? UnreachableCopy : $"Couldn't stop {label}: {ex.Message}");
        } catch (Exception ex) {
            // An unmapped exception still gets a toast (never a silent drop) — AppNotifier.Notify
            // covers both the toast AND stderr (spec §11), so this one call satisfies both
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
