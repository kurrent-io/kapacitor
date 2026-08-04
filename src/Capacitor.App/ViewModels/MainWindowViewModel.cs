using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Projects IDaemonClientService.Status/Snapshots into display text and drives Start/Retry.
/// All display projections are activation-scoped (WhenActivated) — the service outlives this
/// ViewModel and owns its subjects (spec §5), so nothing here disposes the service itself.
/// DEVIATION: StartDaemonCommand/RetryCommand and their canExecute pipelines are built in the
/// CONSTRUCTOR, not inside WhenActivated — commands must exist (and be assertable via
/// CanExecute) independent of window activation; service.Status/service.Snapshots are the
/// service's own long-lived subjects, not resources the VM needs to scope to a window's
/// lifetime.
public sealed class MainWindowViewModel : ReactiveObject, IActivatableViewModel {
    const string IncompatibleReason = "daemon_incompatible";
    const string UnreachableReason  = "daemon_unreachable";

    // Neutral wording (spec §5): §4.2's incompatibility classification is a broad heuristic —
    // an unexpected frame can equally mean the APP is the older side — so the UI must not
    // prescribe an upgrade direction.
    const string SkewMessage = "app and daemon are incompatible — make sure both are up to date";

    readonly IDaemonClientService _service;

    public ViewModelActivator Activator { get; } = new();

    ObservableAsPropertyHelper<string>? _daemonName;
    public string DaemonName => _daemonName?.Value ?? "";

    ObservableAsPropertyHelper<string>? _daemonVersion;
    public string DaemonVersion => _daemonVersion?.Value ?? "";

    ObservableAsPropertyHelper<string>? _serverUrl;
    public string ServerUrl => _serverUrl?.Value ?? "";

    // The daemon's OWN upstream connection to the Capacitor server (DaemonInfoDto.Connection):
    // connected|connecting|reconnecting|disconnected. Distinct from State/Reason below, which
    // are this app's local attach status to the daemon.
    ObservableAsPropertyHelper<string>? _connectionText;
    public string ConnectionText => _connectionText?.Value ?? "";

    // "n of m agents" only while Connected (spec §1.5: active_agents is a display count, never
    // a free-slots/launch-capacity claim) — "—" otherwise, even though the last-known snapshot
    // (and the Agents cache) is retained by the service across disconnects.
    ObservableAsPropertyHelper<string>? _agentCountText;
    public string AgentCountText => _agentCountText?.Value ?? "—";

    ObservableAsPropertyHelper<AttachState>? _state;
    public AttachState State => _state?.Value ?? AttachState.Connecting;

    // Display text for why we're not connected: the raw wire reason for daemon_unreachable, but
    // the NEUTRAL skew message (never an upgrade-direction verdict) for daemon_incompatible; null
    // outside Unreachable.
    ObservableAsPropertyHelper<string?>? _reason;
    public string? Reason => _reason?.Value;

    string? _startMessage;
    // Start-daemon failure text. Cleared on every new start attempt AND on any transition to
    // Connected (spec §5); set only when a start attempt actually fails.
    public string? StartMessage {
        get => _startMessage;
        private set => this.RaiseAndSetIfChanged(ref _startMessage, value);
    }

    public ReactiveCommand<Unit, Unit> StartDaemonCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    /// <param name="shutdownToken">
    /// Abandons StartDaemonAsync's WAIT (never the spawned daemon) on app shutdown. MUST be a
    /// token linked to the app lifetime — never CancellationToken.None (Task 4 carry-note: an
    /// unbounded wait would survive app exit).
    /// </param>
    public MainWindowViewModel(IDaemonClientService service, CancellationToken shutdownToken) {
        _service = service;

        // ReactiveCommand's own CanExecute observable already ANDs the supplied canExecute with
        // "not currently executing" (confirmed against the installed ReactiveUI 23.2.28 API
        // docs) — no separate in-flight flag is needed to satisfy "Start also disabled while a
        // start is in flight".
        //
        // ReactiveCommand does NOT reschedule the SUPPLIED canExecute onto outputScheduler
        // (decompile-verified: only IsExecuting/ThrownExceptions ride outputScheduler) — without
        // an explicit ObserveOn here, a Status event arriving on a background thread (the
        // service's pump thread) would carry CanExecuteChanged, and therefore a bound Button's
        // IsEnabled write, onto that same background thread, tripping Avalonia's dispatcher
        // thread-affinity check. These stay constructor-scoped (not inside WhenActivated) since
        // commands must exist and be assertable pre-activation — see the class doc comment.
        var canStart = service.Status
            .Select(s => s.State == AttachState.Unreachable && s.Reason == UnreachableReason)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        var canRetry = service.Status
            .Select(s => s.State != AttachState.Connected)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        StartDaemonCommand = ReactiveCommand.CreateFromTask(() => RunStartAsync(shutdownToken), canStart);
        RetryCommand        = ReactiveCommand.CreateFromTask(service.RestartLoopAsync, canRetry);

        this.WhenActivated(disposables => {
            var status    = service.Status.ObserveOn(RxSchedulers.MainThreadScheduler);
            var snapshots = service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler);

            _daemonName = snapshots.Select(s => s.Daemon.Name)
                .ToProperty(this, x => x.DaemonName, "")
                .DisposeWith(disposables);

            _daemonVersion = snapshots.Select(s => s.Daemon.Version)
                .ToProperty(this, x => x.DaemonVersion, "")
                .DisposeWith(disposables);

            _serverUrl = snapshots.Select(s => s.Daemon.ServerUrl)
                .ToProperty(this, x => x.ServerUrl, "")
                .DisposeWith(disposables);

            _connectionText = snapshots.Select(s => s.Daemon.Connection)
                .ToProperty(this, x => x.ConnectionText, "")
                .DisposeWith(disposables);

            _agentCountText = status.CombineLatest(snapshots, (st, snap) => (st, snap))
                .Select(t => t.st.State == AttachState.Connected
                    ? $"{t.snap.Daemon.ActiveAgents} of {t.snap.Daemon.MaxAgents} agents"
                    : "—")
                .ToProperty(this, x => x.AgentCountText, "—")
                .DisposeWith(disposables);

            _state = status.Select(s => s.State)
                .ToProperty(this, x => x.State, AttachState.Connecting)
                .DisposeWith(disposables);

            _reason = status.Select(ReasonText)
                .ToProperty(this, x => x.Reason, (string?)null)
                .DisposeWith(disposables);

            status.Where(s => s.State == AttachState.Connected)
                .Subscribe(_ => StartMessage = null)
                .DisposeWith(disposables);
        });
    }

    static string? ReasonText(AttachStatus status) => status.State switch {
        AttachState.Unreachable when status.Reason == IncompatibleReason => SkewMessage,
        AttachState.Unreachable => status.Reason,
        _ => null,
    };

    async Task RunStartAsync(CancellationToken ct) {
        StartMessage = null; // clear on every new attempt
        var result = await _service.StartDaemonAsync(ct);
        if (!result.Ok) StartMessage = result.Message;
    }
}
