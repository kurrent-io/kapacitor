using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Ready/Expired are the two INTERACTIVE states — hint expiry is not a verdict (spec §6), so the
/// buttons behave identically in both and only the countdown line differs. Concluded is the
/// 2-second terminal hold: the pinned request is settled and its disclosure is on screen — either
/// "Already decided" or an applied decision whose rule did not save.
public enum ConsentPromptPhase { Ready, Resolving, Concluded, Expired }

/// Renders ONE pinned consent request at a time (spec §6). The pin is what the user is looking at:
/// it is taken from the sorted queue head and released only on an advance — never swapped out from
/// under a click by an arrival, a prune, or a replay. The ViewModel NEVER removes a cache entry;
/// conclusive acks and the prune do that inside ConsentService (spec §5), and its identity guard
/// makes any stale action a no-op.
///
/// Four honesty rules carry the reviewed reasoning:
///
/// * <b>Expiry is never a verdict.</b> The deadline hint is wall-clock and the daemon's deadline is
///   monotonic, so a hint that reached zero proves nothing. The countdown line says so and the
///   buttons stay ACTIVE — a click after zero either applies normally (backward clock step) or
///   comes back Ok=false and runs the honest "Already decided" path.
/// * <b>An in-flight resolve outranks the clock.</b> Past zero mid-call the line reads "Expiring…"
///   and the phase stays Resolving: the ack, not the hint, settles the request.
/// * <b>A transport failure discloses nothing about the rule.</b> Its outcome carries a rule value
///   that was never sent, so this path shows only the transport toast, re-enables the buttons and
///   keeps the entry.
/// * <b>A warning outlives the decision that raised it.</b> A toast posted on the same beat the
///   window closes is never seen, so a rule-not-saved disclosure with nothing left to advance to
///   holds the window for the terminal hold instead of closing under its own toast.
/// * <b>Only a DECISION closes the window on the spot.</b> A queue the cache emptied — a
///   resubscribe's clear, whose replay arrives as a separate changeset — waits one beat before
///   closing, so a reconnect blip can no longer flicker the window shut and rebuild it (see
///   Advance).
public sealed class ConsentPromptViewModel : ReactiveObject, IActivatableViewModel {
    const string ExpiredCopy     = "Response time elapsed — unanswered requests are denied by the daemon";
    const string ExpiringCopy    = "Expiring…";
    const string DecidedCopy     = "Already decided";
    const string TransportCopy   = "Daemon unreachable — the request is still pending";
    const string UnknownRuleCopy = "Decision applied — this daemon version doesn't report whether the rule was saved";

    // 1 Hz ticker (spec §6: a 2-second hold), counted in ticks so no test ever sleeps.
    const int TerminalHoldTicks = 2;

    static readonly IComparer<PendingConsent> QueueComparer = Comparer<PendingConsent>.Create((a, b) => {
        var byRequested = RequestedAt(a).CompareTo(RequestedAt(b));
        return byRequested != 0 ? byRequested : string.CompareOrdinal(a.RequestId, b.RequestId);
    });

    // RequestedAt as the SERVICE resolved it: DeadlineHint is anchor + TimeoutSeconds, where the
    // anchor is the parsed stamp or — unparseable — arrival time. Deriving it back out keeps the
    // sort key and the countdown on one value, with no second parse and no second fallback rule.
    static DateTimeOffset RequestedAt(PendingConsent entry) =>
        entry.DeadlineHint - TimeSpan.FromSeconds(entry.Dto.TimeoutSeconds);

    readonly IConsentService _consent;
    readonly IAppNotifier _notifier;
    readonly TimeProvider _time;
    readonly CancellationToken _shutdownToken;
    readonly Action? _onConcluded;
    readonly Subject<Unit> _closeRequested = new();

    // ONE stable collection, created once and never replaced (the lesson recorded on
    // MainWindowViewModel._agentsSource — a swapped instance leaves the view bound to a dead
    // collection): WhenActivated re-runs on every window, and SortAndBind's IList overload mutates
    // THIS instance in place.
    readonly ObservableCollectionExtended<PendingConsent> _queueSource = new();

    // The pin's own conclusion, held until the next advance. The service evicts a concluded entry
    // inside ResolveAsync, but this ViewModel sees the cache through an ObserveOn — so the eviction
    // and the ack's continuation are two independently posted jobs and their order is not ours to
    // assume. Skipping the settled identity makes the advance correct under either one.
    PendingConsent? _settled;

    // Armed when the CACHE emptied the queue (a resubscribe's clear, a prune) rather than a local
    // settle; disarmed by the next entry to arrive. See Advance.
    bool _closeDeferred;

    int _heldTicks;

    public ViewModelActivator Activator { get; } = new();

    public ReadOnlyObservableCollection<PendingConsent> Queue { get; }

    /// Fires when an advance finds nothing left to show. The window closes itself on this (spec
    /// §6: "the next pending or closes when the queue empties"); the ViewModel owns no window.
    public IObservable<Unit> CloseRequested => _closeRequested.AsObservable();

    PendingConsent? _current;
    /// The pinned request — the ONLY thing rendered. Null when the queue is empty.
    public PendingConsent? Current {
        get => _current;
        private set => this.RaiseAndSetIfChanged(ref _current, value);
    }

    ConsentPromptPhase _phase;
    public ConsentPromptPhase Phase {
        get => _phase;
        private set => this.RaiseAndSetIfChanged(ref _phase, value);
    }

    string? _phaseText;
    /// Terminal-state line: "Already decided" with its §4.1 rule-side-effect disclosure, or a
    /// rule-not-saved warning being held on screen. Null while the request is still answerable.
    public string? PhaseText {
        get => _phaseText;
        private set => this.RaiseAndSetIfChanged(ref _phaseText, value);
    }

    string _positionText = "";
    public string PositionText {
        get => _positionText;
        private set => this.RaiseAndSetIfChanged(ref _positionText, value);
    }

    bool _positionVisible;
    public bool PositionVisible {
        get => _positionVisible;
        private set => this.RaiseAndSetIfChanged(ref _positionVisible, value);
    }

    readonly ObservableAsPropertyHelper<string> _requesterText;
    public string RequesterText => _requesterText.Value;

    readonly ObservableAsPropertyHelper<string> _kindLabel;
    public string KindLabel => _kindLabel.Value;

    readonly ObservableAsPropertyHelper<string> _vendorText;
    public string VendorText => _vendorText.Value;

    readonly ObservableAsPropertyHelper<string> _repoLeaf;
    public string RepoLeaf => _repoLeaf.Value;

    readonly ObservableAsPropertyHelper<string> _repoFull;
    public string RepoFull => _repoFull.Value;

    // Hidden when the requester is null OR empty — the same predicate as ConsentService's save
    // guard, which stays the real safety boundary (a wildcard allow-everything rule, spec §6).
    readonly ObservableAsPropertyHelper<bool> _allowRememberVisible;
    public bool AllowRememberVisible => _allowRememberVisible.Value;

    readonly ObservableAsPropertyHelper<bool> _buttonsEnabled;
    public bool ButtonsEnabled => _buttonsEnabled.Value;

    readonly ObservableAsPropertyHelper<bool> _buttonsVisible;
    public bool ButtonsVisible => _buttonsVisible.Value;

    // Activation-scoped, unlike the projections above: this one subscribes to the SHARED,
    // app-lifetime ticker, so a closed window's ViewModel must stop recomputing on every beat.
    ObservableAsPropertyHelper<string>? _countdownText;
    public string CountdownText => _countdownText?.Value ?? "";

    public ReactiveCommand<Unit, Unit> AllowOnceCommand { get; }
    public ReactiveCommand<Unit, Unit> AllowRememberCommand { get; }
    public ReactiveCommand<Unit, Unit> DenyCommand { get; }

    /// <param name="onConcluded">
    /// Invoked after every conclusive ack (AlreadyDecided or a landed decision — never a
    /// TransportFailure, which settles nothing). Composition wires this to the app's single
    /// ActivityViewModel's RequestRefresh (spec §7): own decisions are eventual, not instant — see
    /// that method's own doc comment.
    /// </param>
    public ConsentPromptViewModel(
            IConsentService consent, IAppNotifier notifier, ITicker ticker, TimeProvider time,
            CancellationToken shutdownToken, Action? onConcluded = null) {
        _consent       = consent;
        _notifier      = notifier;
        _time          = time;
        _shutdownToken = shutdownToken;
        _onConcluded   = onConcluded;
        Queue          = new ReadOnlyObservableCollection<PendingConsent>(_queueSource);

        var current = this.WhenAnyValue(x => x.Current);

        _requesterText = current.Select(RequesterOf).ToProperty(this, x => x.RequesterText, "");
        _kindLabel     = current.Select(c => c is null ? "" : KindLabelOf(c.Dto.Kind)).ToProperty(this, x => x.KindLabel, "");
        _vendorText    = current.Select(c => c?.Dto.Vendor ?? "").ToProperty(this, x => x.VendorText, "");
        _repoLeaf      = current.Select(c => c is null ? "" : RepoLabel.Leaf(c.Dto.RepoPath)).ToProperty(this, x => x.RepoLeaf, "");
        _repoFull      = current.Select(c => c?.Dto.RepoPath ?? "").ToProperty(this, x => x.RepoFull, "");

        _allowRememberVisible = current
            .Select(c => c is not null && !string.IsNullOrEmpty(c.Dto.Requester))
            .ToProperty(this, x => x.AllowRememberVisible, initialValue: false);

        // Constructor-scoped, exactly like MainWindowViewModel's Start/RetryVisible: the commands
        // must exist and be assertable before any window activates, and these observables only
        // ever observe THIS object's own property changes (always raised on the UI thread).
        var canResolve = this
            .WhenAnyValue(x => x.Current, x => x.Phase,
                (target, phase) => target is not null && phase is ConsentPromptPhase.Ready or ConsentPromptPhase.Expired);

        _buttonsEnabled = canResolve.ToProperty(this, x => x.ButtonsEnabled, initialValue: false);
        _buttonsVisible = this
            .WhenAnyValue(x => x.Current, x => x.Phase, (target, phase) => target is not null && phase != ConsentPromptPhase.Concluded)
            .ToProperty(this, x => x.ButtonsVisible, initialValue: false);

        AllowOnceCommand     = ReactiveCommand.CreateFromTask(() => RunResolveAsync(allow: true, saveRule: false), canResolve);
        AllowRememberCommand = ReactiveCommand.CreateFromTask(() => RunResolveAsync(allow: true, saveRule: true), canResolve);
        DenyCommand          = ReactiveCommand.CreateFromTask(() => RunResolveAsync(allow: false, saveRule: false), canResolve);

        this.WhenActivated(disposables => {
            // The pipeline below is brand new on every activation while _queueSource is reused —
            // without this Clear a reactivation's initial replay would insert a second copy of
            // everything currently cached (MainWindowViewModel's identical note).
            _queueSource.Clear();
            _closeDeferred = false; // a close armed under the previous activation is not this one's

            // ObserveOn BEFORE the binding operator: the cache is mutated on the service's
            // background continuations (spec §5).
            consent.Pending
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .SortAndBind(_queueSource, QueueComparer)
                .Subscribe(_ => OnQueueChanged())
                .DisposeWith(disposables);

            ticker.Ticks
                .Subscribe(_ => OnTick())
                .DisposeWith(disposables);

            _countdownText = ticker.Ticks.Select(_ => Unit.Default)
                .Merge(this.WhenAnyValue(x => x.Current).Select(_ => Unit.Default))
                .Merge(this.WhenAnyValue(x => x.Phase).Select(_ => Unit.Default))
                .Select(_ => Countdown())
                .ToProperty(this, x => x.CountdownText, Countdown())
                .DisposeWith(disposables);
        });
    }

    internal static string KindLabelOf(string kind) => kind switch {
        "agent"       => "Agent",
        "review"      => "Review",
        "review-flow" => "Review flow",
        _             => kind, // an unrecognized kind renders verbatim rather than as a wrong label
    };

    static string RequesterOf(PendingConsent? entry) =>
        entry is null                                              ? ""
        : !string.IsNullOrWhiteSpace(entry.Dto.RequesterDisplay)   ? entry.Dto.RequesterDisplay
        : !string.IsNullOrWhiteSpace(entry.Dto.Requester)          ? entry.Dto.Requester
        : "unknown";

    /// Cache changes never swap the display while the pinned request is being resolved or is
    /// holding a terminal state; otherwise the pin survives until it leaves the cache (the
    /// hint-expired prune, spec §6).
    void OnQueueChanged() {
        if (Phase is ConsentPromptPhase.Resolving or ConsentPromptPhase.Concluded) {
            UpdatePosition();
            return;
        }

        if (Current is null || !StillQueued(Current)) Advance(settled: false); else UpdatePosition();
    }

    void OnTick() {
        if (Phase == ConsentPromptPhase.Concluded) {
            if (++_heldTicks >= TerminalHoldTicks) Advance(settled: true);
            return;
        }

        // Still empty a beat after the cache emptied it: the emptiness was real, not a
        // resubscribe's clear with its replay in flight. One-shot — a queue that stays empty
        // never re-fires it.
        if (_closeDeferred) {
            _closeDeferred = false;
            _closeRequested.OnNext(Unit.Default);
            return;
        }

        if (Phase != ConsentPromptPhase.Resolving) Phase = RestingPhase();
    }

    /// Releases the pin and takes the sorted head — or signals empty. Nothing here removes: the
    /// service evicts a concluded entry, and _settled covers the beat before that eviction shows up.
    ///
    /// <paramref name="settled"/> marks the LOCAL paths — an ack, or the end of its terminal hold —
    /// which close on this very beat (spec §6). An emptiness the CACHE caused waits one ticker beat
    /// instead: a resubscribe clears and replays as two separate changesets, and closing on the
    /// intermediate one flickered the window shut and rebuilt it a moment later with a fresh
    /// ViewModel, a reset pin and stolen focus. The pin still releases immediately either way —
    /// nothing that left the cache stays on screen.
    void Advance(bool settled) {
        var wasPinned = Current is not null;

        _heldTicks = 0;
        PhaseText  = null;
        Current    = NextAfterSettled();
        _settled   = null;
        Phase      = RestingPhase();
        UpdatePosition();

        if (Current is not null) {
            _closeDeferred = false; // the queue came back: a deferred close is off
            return;
        }

        // Only a released pin can empty the window. A ViewModel activating before its first
        // changeset arrives (ObserveOn POSTS the initial replay) has nothing to close over yet —
        // and neither does a second empty changeset arriving while a close is already armed.
        if (!wasPinned) return;

        if (settled) _closeRequested.OnNext(Unit.Default); else _closeDeferred = true;
    }

    ConsentPromptPhase RestingPhase() =>
        Current is not null && _time.GetUtcNow() >= Current.DeadlineHint
            ? ConsentPromptPhase.Expired
            : ConsentPromptPhase.Ready;

    /// What an advance would land on: the sorted head, skipping the identity this pin just
    /// concluded (see _settled). Null means the window has nothing left to show.
    PendingConsent? NextAfterSettled() =>
        _queueSource.FirstOrDefault(p => _settled is null || p.PromptId != _settled.PromptId);

    bool StillQueued(PendingConsent target) =>
        _queueSource.Any(p => p.RequestId == target.RequestId && p.PromptId == target.PromptId);

    void UpdatePosition() {
        var count = _queueSource.Count;
        var index = Current is null ? -1 : _queueSource.IndexOf(Current);
        PositionText    = $"{Math.Max(index, 0) + 1} of {count}";
        PositionVisible = count > 1;
    }

    string Countdown() {
        var target = Current;
        if (target is null || Phase == ConsentPromptPhase.Concluded) return "";

        var remaining = target.DeadlineHint - _time.GetUtcNow();
        if (remaining > TimeSpan.Zero) return $"Expires in {(int)Math.Ceiling(remaining.TotalSeconds)}s";

        // Past the hint: an in-flight resolve owns the outcome, otherwise the non-authoritative
        // copy — and the buttons stay live either way (spec §6).
        return Phase == ConsentPromptPhase.Resolving ? ExpiringCopy : ExpiredCopy;
    }

    async Task RunResolveAsync(bool allow, bool saveRule) {
        var target = Current;
        if (target is null || Phase is ConsentPromptPhase.Resolving or ConsentPromptPhase.Concluded) return;

        Phase = ConsentPromptPhase.Resolving;
        try {
            Settle(await _consent.ResolveAsync(target, allow, saveRule, _shutdownToken));
        } catch (OperationCanceledException) {
            // Shutdown (or a cancelled lane wait): a silent abort — no toast, no removal, and the
            // window is closing anyway. Nothing was decided, so the request stays answerable.
            Phase = RestingPhase();
        } catch (Exception ex) {
            // ResolveAsync already contains its own unmapped failures; anything still escaping
            // (e.g. a disposed service, which the §5 shutdown order makes unreachable) must not
            // reach ReactiveCommand.ThrownExceptions, whose default handler rethrows on the
            // dispatcher and takes the app down.
            Console.Error.WriteLine($"kcap: consent resolve failed unexpectedly: {ex.Message}");
            Phase = RestingPhase();
        }
    }

    void Settle(ConsentResolveOutcome outcome) {
        switch (outcome.Kind) {
            case ConsentResolveKind.TransportFailure:
                // Deliberately NO rule disclosure: nothing was sent, so the outcome's rule value
                // (possibly Unknown) says nothing about a rule and must not be rendered.
                _notifier.Notify(TransportCopy);
                Phase = RestingPhase();
                break;

            case ConsentResolveKind.AlreadyDecided:
                _settled = Current;
                _onConcluded?.Invoke();
                Hold(DecidedText(outcome.RuleOutcome));
                break;

            // Applied / AppliedRuleRejected / RuleSkippedNoRequester: the decision landed.
            default:
                _settled = Current;
                _onConcluded?.Invoke();
                var warning = RuleWarning(outcome);
                if (warning is not null) _notifier.Notify(warning);

                // With something else queued the advance keeps the window up and the toast lands
                // over the next request. On the LAST one the advance closes the window on this
                // very beat, and a posted toast over a closed window is no disclosure at all — so
                // the warning takes the same terminal hold "Already decided" gets, and the close
                // follows it.
                if (warning is not null && NextAfterSettled() is null) Hold(warning); else Advance(settled: true);
                break;
        }
    }

    /// The 2-tick terminal hold: the pin stays on screen carrying its disclosure, the buttons go
    /// (there is nothing left to click), and OnTick advances out of it.
    void Hold(string disclosure) {
        PhaseText  = disclosure;
        _heldTicks = 0;
        Phase      = ConsentPromptPhase.Concluded;
    }

    /// Never a silent success (spec §6) — and after an Allow &amp; remember the §4.1 side effect is
    /// disclosed: the daemon persists a rule BEFORE it attempts the resolution, so a request that
    /// was already decided elsewhere can still have installed one.
    string DecidedText(ConsentRuleOutcome rule) => rule switch {
        ConsentRuleOutcome.Saved    => $"Already decided — your allow rule for {RequesterText} was still saved.",
        ConsentRuleOutcome.Rejected or ConsentRuleOutcome.SkippedNoRequester => "Already decided — no rule was saved.",
        ConsentRuleOutcome.Unknown  => "Already decided — this daemon version doesn't report whether your allow rule was saved.",
        _                           => DecidedCopy,
    };

    static string? RuleWarning(ConsentResolveOutcome outcome) =>
        outcome.Kind is not (ConsentResolveKind.AppliedRuleRejected or ConsentResolveKind.RuleSkippedNoRequester) ? null
        : outcome.RuleOutcome == ConsentRuleOutcome.Unknown ? UnknownRuleCopy
        : $"Decision applied — rule not saved: {RuleFailureReason(outcome)}";

    static string RuleFailureReason(ConsentResolveOutcome outcome) =>
        !string.IsNullOrEmpty(outcome.Error)                                ? outcome.Error
        : outcome.RuleOutcome == ConsentRuleOutcome.SkippedNoRequester      ? "the request had no requester identity"
        : "the daemon didn't say why";
}
