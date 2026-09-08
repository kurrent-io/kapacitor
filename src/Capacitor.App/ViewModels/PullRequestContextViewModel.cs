using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel : ReactiveObject {
    sealed record Position(string Section, string? Thread, string Resolved, double Scroll, long Touched);
    readonly IPullRequestSource _source;
    readonly TimeProvider _time;
    readonly IUrlOpener _opener;
    readonly Action _openReader;
    readonly Func<string?>? _primaryRepo;
    readonly CompositeDisposable _subscriptions = new();
    readonly HashSet<Task> _tasks = [];
    readonly List<CancellationTokenSource> _retired = [];
    readonly Dictionary<string, PullRequestSectionState> _sections = new(StringComparer.Ordinal);
    readonly Dictionary<PullRequestSubjectDto, Position> _positions = [];
    readonly HashSet<string> _pageRequests = new(StringComparer.Ordinal);
    readonly AvaloniaList<PullRequestChoice> _choices = [];
    readonly ITimer _timer;
    CancellationTokenSource _cancel = new();
    long _generation;
    string? _session;
    string? _branch;
    PullRequestChoice? _selected;
    bool _explicitSelection;
    bool _updatingChoices;
    PullRequestOverviewDto? _overview;
    PullRequestRead<PullRequestOverviewDto>? _overviewRead;
    long _accessStarted;
    int _accessSeconds;
    long _graceStarted;
    string? _graceSection;
    bool _grace;
    bool _hasDisplayed;
    bool _foreground;
    bool _readerVisible;
    bool _masked = true;
    bool _refreshing;
    bool _overviewPending;
    bool _queuedRefresh;
    bool _stopped;
    bool _disposed;
    bool _legacy;
    long? _lastRefresh;
    long? _lastOverview;
    DateTime? _retryAt;
    int _pollAfter = 30;
    string _section = "overview";
    string? _thread;
    string _notice = "Waiting for the session to register…";

    public IAvaloniaReadOnlyList<PullRequestChoice> Choices => _choices;
    public PullRequestChoice? Selected {
        get => _selected;
        set { if (!_updatingChoices) Select(value, explicitSelection: true); }
    }
    public string Notice => _notice;
    public bool IsReading => _refreshing || _overviewPending || _pageRequests.Count > 0;
    public bool HasChoice => _selected is not null;
    public bool IsLegacy => _legacy;
    public string Section => _section;
    public double ScrollOffset { get; set; }
    public bool CanReveal => _foreground && !_masked && Remaining > 0 && !_legacy;
    public bool CanDisplay => _foreground && !_masked && (Remaining > 0 || _grace && _time.GetElapsedTime(_graceStarted) < TimeSpan.FromMinutes(5));
    bool CanDisplayReader => _readerVisible && CanDisplay && (Remaining > 0 || _graceSection == SectionKey);
    double Remaining => Math.Max(0, _accessSeconds - _time.GetElapsedTime(_accessStarted).TotalSeconds);
    string SectionKey => _section == "thread_comments" ? _section + ":" + _thread : _section == "threads" ? _section + ":" + _resolved : _section;
    string _resolved = "unresolved";
    public bool IncludeResolved {
        get => _resolved == "all";
        set {
            if (value == IncludeResolved || !CanReveal) return;
            _resolved = value ? "all" : "unresolved";
            this.RaisePropertyChanged();
            ShowSection("threads");
        }
    }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenReaderCommand { get; }
    public ReactiveCommand<string, Unit> ShowSectionCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadEarlierCommand { get; }
    public ReactiveCommand<PullRequestRow, Unit> ExpandThreadCommand { get; }
    public ReactiveCommand<PullRequestRow, Unit> OpenRowCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
    public ReactiveCommand<string, Unit> OpenBodyLinkCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }
    public ReactiveCommand<Unit, Unit> LinkGitHubCommand { get; }

    public PullRequestContextViewModel(IObservable<AgentStatusDto?> presence, IPullRequestSource source, TimeProvider time, IUrlOpener opener,
        Action openReader, Action? signIn = null, Action? linkGitHub = null, IObservable<Unit>? signInCompleted = null, Func<string?>? primaryRepo = null) {
        _source = source;
        _time = time;
        _opener = opener;
        _openReader = openReader;
        _primaryRepo = primaryRepo;
        RefreshCommand = ReactiveCommand.Create(() => RequestRefresh(manual: true));
        OpenReaderCommand = ReactiveCommand.Create(() => { _openReader(); SetReaderVisible(true); });
        ShowSectionCommand = ReactiveCommand.Create<string>(ShowSection);
        LoadMoreCommand = ReactiveCommand.Create(() => { if (CurrentSection?.Next is { } cursor) RequestPage(cursor); });
        ReloadEarlierCommand = ReactiveCommand.Create(() => { if (CurrentSection?.Evicted is { } cursor) RequestPage(cursor, earlier: true); });
        ExpandThreadCommand = ReactiveCommand.Create<PullRequestRow>(row => {
            if (!CanReveal || !row.IsThread) return;
            _thread = row.Id;
            ShowSection("thread_comments");
        });
        OpenRowCommand = ReactiveCommand.Create<PullRequestRow>(row => {
            if (CanDisplayReader) LinkPolicy.Open(_opener, row.IsCheck ? PullRequestWire.CheckLink(row.Url) : _selected is null ? null : PullRequestWire.PrLink(row.Url, _selected.Subject));
        });
        OpenGitHubCommand = ReactiveCommand.Create(() => {
            if (_selected is { IsAvailable: true } choice)
                LinkPolicy.Open(_opener, PullRequestWire.IsGitHub(choice.Subject)
                    ? PullRequestWire.PrLink(choice.Link.Url, choice.Subject) : PullRequestWire.SafeLink(choice.Link.Url));
        });
        OpenBodyLinkCommand = ReactiveCommand.Create<string>(url => { if (CanDisplayReader) LinkPolicy.Open(_opener, PullRequestWire.BodyLink(url)); });
        SignInCommand = ReactiveCommand.Create(() => signIn?.Invoke());
        LinkGitHubCommand = ReactiveCommand.Create(() => linkGitHub?.Invoke());
        _subscriptions.Add(RefreshCommand); _subscriptions.Add(OpenReaderCommand); _subscriptions.Add(ShowSectionCommand);
        _subscriptions.Add(LoadMoreCommand); _subscriptions.Add(ReloadEarlierCommand); _subscriptions.Add(ExpandThreadCommand);
        _subscriptions.Add(OpenRowCommand); _subscriptions.Add(OpenGitHubCommand); _subscriptions.Add(OpenBodyLinkCommand);
        _subscriptions.Add(SignInCommand); _subscriptions.Add(LinkGitHubCommand);
        presence.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(dto => {
            if (_disposed || dto is null) return;
            _branch = dto.Branch;
            if (dto.SessionId is not { Length: > 0 } id || _session == id) return;
            CancelReads();
            _session = id;
            _choices.Clear();
            _selected = null;
            _explicitSelection = false;
            _positions.Clear();
            ClearProtected();
            _stopped = false;
            _lastRefresh = null;
            SetNotice("Loading pull requests…");
            RequestRefresh();
        }).DisposeWith(_subscriptions);
        signInCompleted?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => {
            if (_session is null || _disposed) return;
            CancelReads(); ClearProtected(); _source.ResetSession(_session); _stopped = false; _lastRefresh = null; RequestRefresh(manual: true);
        }).DisposeWith(_subscriptions);
        _timer = time.CreateTimer(_ => Dispatcher.UIThread.Post(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void SetForeground(bool foreground) {
        if (_disposed || _foreground == foreground) return;
        _foreground = foreground;
        CancelReads();
        _accessSeconds = 0;
        _masked = true;
        _grace = false;
        _graceSection = null;
        Notify();
        if (foreground) { _lastRefresh = null; RequestRefresh(); }
    }
    public void Reconnected() {
        if (_disposed) return;
        _refreshDiscovery = true;
        RequestRefresh();
    }
    public void SetReaderVisible(bool visible) {
        if (_readerVisible == visible) return;
        _readerVisible = visible;
        if (!visible && _grace) _graceSection = null;
        Notify();
        if (visible && CanReveal && _section != "overview" && CurrentSection is null) RequestPage(null);
    }
    void Select(PullRequestChoice? choice, bool explicitSelection = false) {
        if (_disposed || choice?.Subject == _selected?.Subject) return;
        if (choice is not null && !_choices.Contains(choice)) return;
        _explicitSelection = explicitSelection;
        if (_selected is not null) {
            if (_positions.Count >= 20 && !_positions.ContainsKey(_selected.Subject)) _positions.Remove(_positions.MinBy(x => x.Value.Touched).Key);
            _positions[_selected.Subject] = new(_section, _thread, _resolved, ScrollOffset, _time.GetTimestamp());
        }
        CancelReads();
        _selected = choice;
        ClearProtected();
        _section = "overview"; _thread = null; _resolved = "unresolved"; ScrollOffset = 0;
        if (choice is not null && _positions.TryGetValue(choice.Subject, out var position)) {
            _section = position.Section; _thread = position.Thread; _resolved = position.Resolved; ScrollOffset = position.Scroll;
        }
        this.RaisePropertyChanged(nameof(Selected));
        Notify();
        RequestOverview();
    }
    void ShowSection(string section) {
        if (!CanReveal || section is not ("overview" or "checks" or "reviewers" or "reviews" or "threads" or "thread_comments" or "conversation")) return;
        _section = section;
        ScrollOffset = 0;
        _openReader();
        _readerVisible = true;
        Notify();
        if (section != "overview" && CurrentSection is null) RequestPage(null);
    }
    void Tick() {
        if (_disposed || !_foreground) return;
        if (!_masked && _accessSeconds > 0 && Remaining <= 0 && !_grace) EnterGrace();
        if (_grace && _time.GetElapsedTime(_graceStarted) >= TimeSpan.FromMinutes(5)) { _masked = true; _grace = false; Notify(); }
        if (_retryAt > _time.GetUtcNow().UtcDateTime || _stopped) return;
        if (_queuedRefresh || _lastRefresh is null || _time.GetElapsedTime(_lastRefresh.Value).TotalSeconds >= Math.Max(30, _pollAfter)) RequestRefresh();
        else if (Remaining <= 5 && !_legacy) RequestOverview();
    }
    void EnterGrace() {
        _accessSeconds = 0;
        if (!_hasDisplayed || !_foreground || _masked) { _masked = true; Notify(); return; }
        if (!_grace) { _grace = true; _graceStarted = _time.GetTimestamp(); _graceSection = _readerVisible ? SectionKey : null; }
        SetNotice("Access could not be refreshed. Showing the last view temporarily; opening more content is paused.");
    }
    bool AcceptAccess<T>(PullRequestRead<T> read) where T : class {
        if (!read.CanReveal(_time) || read.Subject != _selected?.Subject) return false;
        _accessStarted = read.RequestStarted;
        _accessSeconds = read.AccessValidForSeconds;
        _masked = false;
        _grace = false;
        _hasDisplayed = true;
        _retryAt = read.RetryAt;
        _pollAfter = Math.Max(15, read.PollAfterSeconds);
        return true;
    }
    void ClearProtected() {
        _overview = null; _overviewRead = null; _sections.Clear(); _accessSeconds = 0;
        _masked = true; _grace = false; _hasDisplayed = false; _graceSection = null;
        Notify();
    }
    void CancelReads() {
        _generation++;
        _cancel.Cancel();
        if (_tasks.Count == 0) _cancel.Dispose();
        else _retired.Add(_cancel);
        _cancel = new();
        _refreshing = false; _overviewPending = false; _pageRequests.Clear(); _lastOverview = null;
    }
    void Start(Func<CancellationToken, Task<Action>> operation, Action settled) {
        var generation = _generation;
        var token = _cancel.Token;
        var task = Run();
        _tasks.Add(task);
        _ = task.ContinueWith(done => Dispatcher.UIThread.Post(() => {
            _tasks.Remove(done);
            if (_tasks.Count != 0 || _disposed) return;
            foreach (var retired in _retired) retired.Dispose();
            _retired.Clear();
        }), TaskScheduler.Default);
        async Task Run() {
            try {
                var apply = await operation(token).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => { if (!_disposed && generation == _generation && !token.IsCancellationRequested) apply(); });
            } catch (OperationCanceledException) { }
            catch (Exception) {
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (!_disposed && generation == _generation) { ClearProtected(); SetNotice("Couldn't load pull request context. Retry when the server is reachable."); }
                });
            } finally {
                await Dispatcher.UIThread.InvokeAsync(() => { if (!_disposed && generation == _generation) { settled(); Notify(); } });
            }
        }
    }
    void SetNotice(string value) { _notice = value; Notify(); }
    public async Task TeardownAsync() {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose(); _subscriptions.Dispose(); _cancel.Cancel();
        try { await Task.WhenAll(_tasks.ToArray()); } catch (OperationCanceledException) { }
        _cancel.Dispose(); foreach (var retired in _retired) retired.Dispose();
        _tasks.Clear(); _retired.Clear(); _positions.Clear(); _choices.Clear(); ClearProtected();
    }
}
