using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum WorkContextPhase { WaitingForSession, Loading, Ready, NoWorkItem, SignedOut, NotInPlan, Unreachable, SessionUnknown }

/// The work-context sidebar for one session: facts from the daemon's dto, the work item from the
/// server. Ctor-scoped; TeardownAsync is the one exit.
///
/// The session id is the read's identity. A lease owns one id, its cancellation and its pending
/// read; a result applies only for the current lease, every lease is kept until its read settles
/// so teardown can await them all, and every lease transition happens on the UI thread.
public sealed partial class WorkContextViewModel : ReactiveObject {
    public PullRequestContextViewModel? PullRequests { get; internal set; }
    public bool HasPullRequestContext => PullRequests is not null;
    public bool ShowsLegacyLinks => PullRequests is null;
    public string? PrimaryRepositoryHash { get; private set; }
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    static readonly IReadOnlyList<HarnessOption> DefaultHarnessOptions = HostedHarnessCatalog.Build(null);

    internal const string WaitingNote      = "Waiting for the session to register…";
    internal const string LoadingNote      = "Loading work context…";
    internal const string NoWorkItemNote   = "No work item attached yet. The agent's declare tool attaches one.";
    internal const string NoRepositoryNote = "This session has no repository. A work item cannot attach until the work lands in one.";
    internal const string SignedOutNote    = "Sign in to see the work context.";
    internal const string NotInPlanNote    = "Work items are not in this workspace's plan.";
    internal const string UnreachableNote  = "Couldn't reach the server.";

    sealed class ReadLease(string sessionId) {
        public string SessionId { get; } = sessionId;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Pending;
        public bool RefreshPending;
        public bool IsReading => Pending is { IsCompleted: false };
    }

    readonly IWorkContextSource _source;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly CompositeDisposable _disposables = new();
    readonly List<ReadLease> _outstanding = [];
    ReadLease? _current;
    ITimer? _timer;
    bool _tornDown;
    AgentStatusDto? _dto;

    string _repository = "—";
    public string Repository { get => _repository; private set => this.RaiseAndSetIfChanged(ref _repository, value); }
    string? _repositoryPath;
    public string? RepositoryPath { get => _repositoryPath; private set => this.RaiseAndSetIfChanged(ref _repositoryPath, value); }
    string _worktree = "—";
    public string Worktree { get => _worktree; private set => this.RaiseAndSetIfChanged(ref _worktree, value); }
    string? _worktreePath;
    public string? WorktreePath { get => _worktreePath; private set => this.RaiseAndSetIfChanged(ref _worktreePath, value); }
    string _branch = "—";
    public string Branch { get => _branch; private set => this.RaiseAndSetIfChanged(ref _branch, value); }
    string _harness = "—";
    public string Harness { get => _harness; private set => this.RaiseAndSetIfChanged(ref _harness, value); }
    string _transport = "—";
    public string Transport { get => _transport; private set => this.RaiseAndSetIfChanged(ref _transport, value); }
    string _sessionIdText = "resolving…";
    public string SessionIdText { get => _sessionIdText; private set => this.RaiseAndSetIfChanged(ref _sessionIdText, value); }
    string _sessionSummaryLine = "—";
    public string SessionSummaryLine { get => _sessionSummaryLine; private set => this.RaiseAndSetIfChanged(ref _sessionSummaryLine, value); }

    WorkContextPhase _phase = WorkContextPhase.WaitingForSession;
    public WorkContextPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
            this.RaisePropertyChanged(nameof(IsReady));
            this.RaisePropertyChanged(nameof(ShowsSignIn));
            this.RaisePropertyChanged(nameof(ShowsRetry));
        }
    }

    public string PhaseNote => Phase switch {
        WorkContextPhase.WaitingForSession or WorkContextPhase.SessionUnknown => WaitingNote,
        WorkContextPhase.Loading     => LoadingNote,
        WorkContextPhase.NoWorkItem  => _dto?.RepoPath is null ? NoRepositoryNote : NoWorkItemNote,
        WorkContextPhase.SignedOut   => SignedOutNote,
        WorkContextPhase.NotInPlan   => NotInPlanNote,
        WorkContextPhase.Unreachable => UnreachableNote,
        _                            => "",
    };

    public bool IsReady     => Phase == WorkContextPhase.Ready;
    public bool ShowsSignIn => Phase == WorkContextPhase.SignedOut;
    public bool ShowsRetry  => Phase == WorkContextPhase.Unreachable;

    bool _isStale;
    public bool IsStale { get => _isStale; private set => this.RaiseAndSetIfChanged(ref _isStale, value); }
    bool _isReading;
    public bool IsReading {
        get => _isReading;
        private set {
            if (_isReading == value) return;
            this.RaiseAndSetIfChanged(ref _isReading, value);
            this.RaisePropertyChanged(nameof(RefreshTip));
        }
    }
    bool _hasSession;
    // Subject, not WhenAnyValue — same RxAppBuilder init trap as SessionRailViewModel.SelectedAgentId.
    readonly BehaviorSubject<bool> _hasSessionChanges = new(false);
    public bool HasSession {
        get => _hasSession;
        private set {
            if (_hasSession == value) return;
            this.RaiseAndSetIfChanged(ref _hasSession, value);
            _hasSessionChanges.OnNext(value);
            this.RaisePropertyChanged(nameof(RefreshTip));
        }
    }

    /// Tip on the header refresh control — bound with ShowOnDisabled so a greyed icon still explains itself.
    public string RefreshTip => HasSession
        ? IsReading ? "Refreshing…" : "Refresh"
        : "Waiting for the session ID";

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _current?.Pending ?? _outstanding.LastOrDefault()?.Pending;

    public WorkContextViewModel(
            IObservable<AgentStatusDto?> presence, IWorkContextSource source, TimeProvider time, IUrlOpener opener,
            Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null) {
        _source = source;
        _opener = opener;
        _time = time;
        InitializeProjections();
        _disposables.Add(_hasSessionChanges);

        // Enabled for any known session id. A click while a read is in flight queues one follow-up
        // (RefreshPending) instead of disabling the control — a greyed icon looked broken and ate
        // the click with no feedback.
        RefreshCommand = ReactiveCommand.Create(
            () => {
                if (_current is null) return;
                if (_current.IsReading) _current.RefreshPending = true;
                else StartRead(_current);
            },
            _hasSessionChanges);
        _disposables.Add(RefreshCommand);
        SignInCommand = ReactiveCommand.Create(() => { requestSignIn?.Invoke(); });
        _disposables.Add(SignInCommand);

        presence.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(OnDto).DisposeWith(_disposables);
        signInCompleted?
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => OnSignInCompleted())
            .DisposeWith(_disposables);
        _timer = time.CreateTimer(_ => RxSchedulers.MainThreadScheduler.Schedule(OnTick), null, PollInterval, PollInterval);
    }

    void OnDto(AgentStatusDto? dto) {
        if (_tornDown || dto is null) return;
        _dto = dto;
        UpdateFacts(dto);
        if (dto.SessionId is { Length: > 0 } id && (_current is null || !string.Equals(_current.SessionId, id, StringComparison.Ordinal)))
            SwitchSession(id);
        this.RaisePropertyChanged(nameof(PhaseNote));
    }

    internal static string TransportLabel(string family) => family switch {
        "pty" => "PTY",
        "acp" => "ACP",
        _     => "chat",
    };

    void UpdateFacts(AgentStatusDto dto) {
        Repository = RepoLabel.Leaf(dto.RepoPath);
        RepositoryPath = dto.RepoPath;
        var checkout = CheckoutLabel.CheckoutPathFor(dto);
        WorktreePath = checkout;
        Worktree = checkout is null
            ? "—"
            : CheckoutLabel.Format(checkout, dto.RepoPath ?? "") + (dto.WorkLocation == WorkLocationText.Borrowed ? " · borrowed" : "");
        Branch = string.IsNullOrWhiteSpace(dto.Branch) ? "—" : dto.Branch;
        var vendorLabel = HostedHarnessCatalog.LabelFor(DefaultHarnessOptions, dto.Vendor);
        Harness = $"{vendorLabel} · {HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model ?? "")}";
        Transport = TransportLabel(HostedHarnessCatalog.EffectiveFamily(dto.HasTerminal, dto.Vendor));
        SessionSummaryLine = $"{Harness} · {Transport}";
        if (_current is null) SessionIdText = dto.SessionId ?? "resolving…";
        UpdateRequester(dto, vendorLabel);
    }

    void SwitchSession(string id) {
        var old = _current;
        _current = new ReadLease(id);
        old?.Cts.Cancel();
        HasSession = true;
        SessionIdText = id;
        PrimaryRepositoryHash = null;
        ClearServerProjections();
        IsStale = false;
        Phase = WorkContextPhase.Loading;
        StartRead(_current);
    }

    void StartRead(ReadLease lease) {
        lease.RefreshPending = false;
        lease.Pending = RunReadAsync(lease);
        _outstanding.Add(lease);
        if (ReferenceEquals(lease, _current)) IsReading = true;
    }

    async Task RunReadAsync(ReadLease lease) {
        WorkContextRead? read = null;
        try {
            read = await _source.ReadAsync(lease.SessionId, lease.Cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            read = WorkContextRead.Of(WorkContextReadKind.Unreachable, ex.Message);
        }
        try {
            await Dispatcher.UIThread.InvokeAsync(() => Settle(lease, read));
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: work context: {ex.Message}");
        }
    }

    void Settle(ReadLease lease, WorkContextRead? read) {
        _outstanding.Remove(lease);
        var current = ReferenceEquals(lease, _current) && !_tornDown;
        if (!current) { lease.Cts.Dispose(); return; }
        try {
            if (read is not null) Apply(read);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: work context: {ex.Message}");
        }
        IsReading = false;
        if (lease.RefreshPending) StartRead(lease);
    }

    void OnTick() {
        if (_tornDown || _current is not { IsReading: false } lease) return;
        StartRead(lease);
    }

    void OnSignInCompleted() {
        if (_tornDown || _current is not { } lease) return;
        if (lease.IsReading) lease.RefreshPending = true;
        else StartRead(lease);
    }

    /// Applies one read for the current lease. Section-level merging lives in the projections half.
    void Apply(WorkContextRead read) {
        switch (read.Kind) {
            case WorkContextReadKind.SignedOut:      ApplyTerminal(WorkContextPhase.SignedOut);      return;
            case WorkContextReadKind.NotInPlan:      ApplyTerminal(WorkContextPhase.NotInPlan);      return;
            case WorkContextReadKind.SessionUnknown: ApplyTerminal(WorkContextPhase.SessionUnknown); return;
            case WorkContextReadKind.Unreachable:
                if (Phase is WorkContextPhase.Ready or WorkContextPhase.NoWorkItem) IsStale = true;
                else Phase = WorkContextPhase.Unreachable;
                return;
            case WorkContextReadKind.Ready:
                ApplyReady(read);
                return;
            default:
                Phase = WorkContextPhase.Unreachable;
                return;
        }
    }

    /// The server has just said the viewer may not have this data, so nothing of it stays visible.
    void ApplyTerminal(WorkContextPhase phase) {
        ClearServerProjections();
        Phase = phase;
        IsStale = false;
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _timer?.Dispose();
        _timer = null;
        _disposables.Dispose();
        var leases = _outstanding.ToArray();
        foreach (var lease in leases) lease.Cts.Cancel();
        _current = null;
        foreach (var lease in leases)
            if (lease.Pending is { } pending) {
                try { await pending; } catch (Exception) { }
            }
        foreach (var lease in leases) lease.Cts.Dispose();
    }
}
