using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum ChatTabPhase { Waiting, Reading, Missing, Unavailable }

/// The Chat tab: the session's transcript, tailed and projected into chat rows, plus the composer
/// that sends through the sibling terminal. Ctor-scoped; TeardownAsync is the one exit.
///
/// Path identity is part of the read generation: a distinct transcript_path clears the rows and
/// installs a fresh tail in one UI-thread step, and any read still in flight for the old file
/// completes under a stale generation and is discarded.
public sealed class ChatTabViewModel : ReactiveObject {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    readonly string _agentId;
    readonly TerminalTabViewModel _terminal;
    readonly ITranscriptProjection? _projection;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly CompositeDisposable _disposables = new();
    readonly AvaloniaList<ChatItemViewModel> _items = new();
    readonly Dictionary<string, ToolCallItem> _pendingTools = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> _loggedFailures = new(StringComparer.Ordinal);

    /// The tail and the generation it belongs to, taken as one reference: reading the two
    /// separately lets a switch land between them and tag a read of the old file with the new
    /// generation, which Apply's guard would then wave through onto the freshly cleared list.
    sealed record TailLease(JsonlTail Tail, int Generation);

    int _generation;
    int _readInFlight;
    string? _path;
    string? _root;
    volatile TailLease? _lease;
    ITimer? _timer;
    volatile Task? _pendingRead;
    readonly BehaviorSubject<string?> _rootSubject = new(null);

    public IAvaloniaReadOnlyList<ChatItemViewModel> Items => _items;

    public ReadOnlyObservableCollection<PendingCardViewModel> PendingCards { get; }
    public IObservable<string?> Root => _rootSubject;

    readonly ObservableAsPropertyHelper<bool> _hasPendingCards;
    public bool HasPendingCards => _hasPendingCards.Value;

    ChatTabPhase _phase;
    public ChatTabPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
        }
    }

    public string PhaseNote => Phase switch {
        ChatTabPhase.Waiting     => "Waiting for the transcript…",
        ChatTabPhase.Missing     => "The transcript file is missing",
        ChatTabPhase.Unavailable => "No chat view for this harness",
        _                        => "",
    };

    string _composerText = "";
    public string ComposerText {
        get => _composerText;
        set => this.RaiseAndSetIfChanged(ref _composerText, value);
    }

    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }

    readonly ObservableAsPropertyHelper<string> _composerHint;
    public string ComposerHint => _composerHint.Value;

    string _vendor = "";
    IReadOnlyList<HarnessOption> _options = HostedHarnessCatalog.Build(null);

    string _vendorLabel = "";
    public string VendorLabel { get => _vendorLabel; private set => this.RaiseAndSetIfChanged(ref _vendorLabel, value); }

    string _modelLabel = "default";
    public string ModelLabel { get => _modelLabel; private set => this.RaiseAndSetIfChanged(ref _modelLabel, value); }

    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    IBrush _statusDot = SessionStatusDots.For("");
    public IBrush StatusDot { get => _statusDot; private set => this.RaiseAndSetIfChanged(ref _statusDot, value); }

    /// The hint is built from the terminal's own availability, so it is true in the windows
    /// where State alone would lie (a reattach or detach under way while State reads Attached).
    internal static string HintFor(SendAvailability availability, TerminalSessionState state, string vendorLabel) => availability switch {
        SendAvailability.Ready         => $"Reply to {vendorLabel} · Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending       => "Sending…",
        SendAvailability.Transitioning => "Updating the terminal connection…",
        SendAvailability.ReadOnly      => $"Read-only: {state.Detail}",
        SendAvailability.Connecting    => "Connecting to the terminal…",
        SendAvailability.Reattach      => "Reattach the terminal to send",
        SendAvailability.Ended         => "This session has ended",
        _                              => "No terminal to send to",
    };

    /// Test-only seam: the read in flight, or the last one started. A switch that loses the
    /// in-flight CAS starts no read of its own, so this still points at the previous file's read —
    /// await that, advance one tick, then await again to see the new path's first rows.
    internal Task? PendingReadForTesting => _pendingRead;

    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, TerminalTabViewModel terminal,
            ITranscriptProjection? projection, IUrlOpener opener, TimeProvider time, IPermissionService permissions) {
        _agentId = agentId;
        _terminal = terminal;
        _projection = projection;
        _opener = opener;
        _time = time;
        _phase = projection is null ? ChatTabPhase.Unavailable : ChatTabPhase.Waiting;

        // ObserveOn BEFORE the binding operator: the cache is mutated on the service's
        // background continuations (IPermissionService.Pending's own doc comment).
        var cards = permissions.Pending
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(p => p.AgentId == agentId)
            .Transform(p => p.Questions is null
                ? (PendingCardViewModel)new PermissionCardViewModel(p, permissions, _rootSubject)
                : new QuestionCardViewModel(p, permissions))
            .DisposeMany()
            .SortAndBind(out var pendingCards, Comparer<PendingCardViewModel>.Create((a, b) => {
                var byTime = a.RequestedAt.CompareTo(b.RequestedAt);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.RequestId, b.RequestId);
            }));
        PendingCards = pendingCards;

        // Hooked before the pipeline subscribes: on the UI thread the scheduler delivers an
        // already-populated cache inline, so a hook installed afterwards would miss the first fill.
        // The delegate-based overload, not the reflection one: ReadOnlyObservableCollection's
        // CollectionChanged is only reachable through this interface, and the reflection overload
        // (Observable.FromEventPattern(target, eventName)) looks up public events only.
        var notifications = (INotifyCollectionChanged)pendingCards;
        _hasPendingCards = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => notifications.CollectionChanged += h, h => notifications.CollectionChanged -= h)
            .Select(_ => pendingCards.Count > 0)
            .ToProperty(this, x => x.HasPendingCards, initialValue: pendingCards.Count > 0)
            .DisposeWith(_disposables);

        cards.Subscribe().DisposeWith(_disposables);

        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged)
            .DisposeWith(_disposables);

        if (projection is not null)
            _timer = time.CreateTimer(_ => OnTick(), null, PollInterval, PollInterval);

        daemon.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot => {
                _options = HostedHarnessCatalog.Build(snapshot.Daemon.SupportedVendors);
                VendorLabel = HostedHarnessCatalog.LabelFor(_options, _vendor);
            })
            .DisposeWith(_disposables);

        _composerHint = Observable.CombineLatest(
                terminal.WhenAnyValue(t => t.SendAvailability, t => t.State, (availability, state) => (availability, state)),
                this.WhenAnyValue(x => x.VendorLabel),
                (t, label) => HintFor(t.availability, t.state, label))
            .ToProperty(this, x => x.ComposerHint, HintFor(terminal.SendAvailability, terminal.State, ""))
            .DisposeWith(_disposables);

        var canSend = Observable.CombineLatest(
            this.WhenAnyValue(x => x.ComposerText),
            terminal.WhenAnyValue(t => t.CanAcceptText),
            (text, can) => can && !string.IsNullOrWhiteSpace(text));
        SendCommand = ReactiveCommand.Create(() => {
            if (_terminal.TrySendText(ComposerText)) ComposerText = "";
        }, canSend);
        _disposables.Add(SendCommand);

        OpenLinkCommand = ReactiveCommand.Create<string>(url => {
            if (!LinkPolicy.IsOpenable(url)) return;
            try { _opener.Open(url); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: open link failed: {ex.Message}"); }
        });
        _disposables.Add(OpenLinkCommand);
    }

    void OnAgentsChanged(IChangeSet<AgentStatusDto, string> changes) {
        foreach (var change in changes) {
            if (change.Key != _agentId || change.Reason is not (ChangeReason.Add or ChangeReason.Update)) continue;
            OnDto(change.Current);
        }
    }

    void OnDto(AgentStatusDto dto) {
        _vendor = dto.Vendor;
        _root = dto.RepoPath;
        _rootSubject.OnNext(dto.RepoPath);
        VendorLabel = HostedHarnessCatalog.LabelFor(_options, dto.Vendor);
        ModelLabel = HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model ?? "");
        StatusText = dto.Status;
        StatusDot = SessionStatusDots.For(dto.Status);
        if (_projection is not null && dto.TranscriptPath is { } path && path != _path) SwitchPath(path);
    }

    void SwitchPath(string path) {
        _items.Clear();
        _pendingTools.Clear();
        _path = path;
        _lease = new TailLease(new JsonlTail(path), Interlocked.Increment(ref _generation));
        var wasWaiting = _phase == ChatTabPhase.Waiting;
        Phase = ChatTabPhase.Waiting;
        // The rows are gone, so the view has to re-read what stands in for them even when the phase
        // is unchanged — and only then, since the setter itself raises the note on a real change.
        if (wasWaiting) this.RaisePropertyChanged(nameof(PhaseNote));
        OnTick();
    }

    void OnTick() {
        if (_lease is not { } lease || _projection is not { } projection) return;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return;
        _pendingRead = ReadAndApplyAsync(lease.Tail, projection, lease.Generation);
    }

    async Task ReadAndApplyAsync(JsonlTail tail, ITranscriptProjection projection, int generation) {
        try {
            var (read, envelopes) = await Task.Run(() => {
                var result = tail.ReadAppended();
                var list = new List<AcpEventEnvelope>();
                foreach (var line in result.Lines) {
                    try { list.AddRange(projection.Project(line)); }
                    catch (Exception ex) { LogOnce($"projection: {ex.Message}"); }
                }
                return (result, list);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => Apply(generation, read, envelopes));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
        }
    }

    void Apply(int generation, TailRead read, List<AcpEventEnvelope> envelopes) {
        if (generation != Volatile.Read(ref _generation)) return;

        switch (read.Status) {
            case TailStatus.Missing:
                Phase = ChatTabPhase.Missing;
                return;
            case TailStatus.Failed:
                LogOnce(read.Failure ?? "read failed");
                return;
            case TailStatus.Reset:
                _items.Clear();
                _pendingTools.Clear();
                break;
        }

        Phase = ChatTabPhase.Reading;
        if (envelopes.Count == 0) return;

        var fresh = new List<ChatItemViewModel>();
        foreach (var e in envelopes) {
            switch (e.Kind) {
                case AcpEventKind.UserMessage:
                    fresh.Add(new UserTurnItem(e.Text ?? ""));
                    break;
                case AcpEventKind.AssistantText:
                    fresh.Add(new AssistantTextItem(e.Text ?? ""));
                    break;
                case AcpEventKind.SystemNote:
                    fresh.Add(new SystemNoteItem(e.Text ?? ""));
                    break;
                case AcpEventKind.ToolCall: {
                    var item = new ToolCallItem(e.ToolName ?? "tool", ToolDetail.From(e.ToolInputJson, _root));
                    if (e.ToolCallId is { } id) _pendingTools[id] = item;
                    fresh.Add(item);
                    break;
                }
                case AcpEventKind.ToolResult:
                    if (e.ToolCallId is { } resultId && _pendingTools.Remove(resultId, out var call))
                        call.Outcome = e.ToolIsError ? ToolOutcome.Error : ToolOutcome.Done;
                    break;
            }
        }
        if (fresh.Count > 0) _items.AddRange(fresh);
    }

    void LogOnce(string reason) {
        if (_loggedFailures.TryAdd(reason, 0)) Console.Error.WriteLine($"kcap: chat transcript: {reason}");
    }

    public Task TeardownAsync() {
        Interlocked.Increment(ref _generation);
        _lease = null;
        _timer?.Dispose();
        _timer = null;
        _disposables.Dispose();
        _rootSubject.Dispose();
        return Task.CompletedTask;
    }
}
