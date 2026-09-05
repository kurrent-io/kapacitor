using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using ReactiveUI;
using System.Reactive;

namespace Capacitor.App.ViewModels;

/// One terminal tab: resolves the agent's terminal capability against the daemon's Agents cache,
/// then drives the attach/reattach/detach lifecycle over ITerminalAttachClient. Constructed once
/// per tab (like HomeViewModel/ActivityViewModel), not gated behind IActivatableViewModel — the
/// resolve gate and the Agents-cache watch must be live from construction, and TeardownAsync
/// (not Dispose/WhenActivated) is the one lifecycle exit a tab tracker calls when the tab closes.
///
/// State machine (TerminalSessionPhase): Resolving -> {NoTerminal | NotFound | Connecting} ->
/// {Attached -> Detached/Exited/Failed/SessionEnded}. Two independent linearizations guard it:
///
/// * Resolve gate (_resolveState, CAS 0 pending/1 dto-won/2 timeout-won/3 disposed): the FIRST of
///   {a DTO observed, the 10s TimeProvider timeout, TeardownAsync} wins; the loser no-ops. A
///   timeout-win disposes the Agents subscription outright (a late DTO then has nothing to reach
///   -- RetryResolveCommand is the only way back in). A dto-win leaves the subscription alive so a
///   LATER removal can still render SessionEnded.
/// * Attempt lifecycle (_attemptGeneration): every attach/reattach bumps it; a completion whose
///   captured generation no longer matches the current one is a retired attempt and mutates
///   nothing (silent, not an error). _attachLane (SemaphoreSlim(1,1), TRY-entered via Wait(0) --
///   never awaited) single-flights the swap: two ReattachCommand.Execute() calls issued before the
///   first has reached its own await point collapse to exactly one new client, because
///   ReactiveCommand.Execute() invokes its Func&lt;Task&gt; eagerly, without consulting
///   CanExecute/IsExecuting, so a queueing WaitAsync() here would let a double-click spawn two
///   real clients.
///   A call that loses the try-enter is a clean no-op, not a queued retry.
/// * Send gate (_openingToken): who may accept composer text. BeginAttempt and Invalidate are its
///   only advances, and both the gate and the in-flight flag are DERIVED from ownership
///   (_gateOpenToken / _sendInFlightToken == _openingToken), so an advance settles them with
///   nothing to remember and no window in which a passed token check still sets one. A publish that is not an attempt's own Connecting/Attached invalidates -- a
///   terminal outcome only while its OWN attempt still holds the token, since a reattach takes
///   its token before it disposes the client whose outcome it is retiring. State has one
///   assignment site, Publish, which applies both rules.
///
/// UI affinity: the resolve gate mutates State after ObserveOn(RxSchedulers.MainThreadScheduler)
/// or RxSchedulers.MainThreadScheduler.Schedule(...) (the TimeProvider timer fires off-thread in
/// production, synchronously on the CALLING thread under FakeTimeProvider.Advance -- Schedule onto
/// MainThreadScheduler is what makes that synchronous under WithImmediateRxScheduler too). Every
/// attempt-lifecycle mutation (the swap, OnAttached/OnOutput's Feed, the outcome mapping) goes
/// through Dispatcher.UIThread.InvokeAsync awaited with the attempt's own token -- these arrive
/// from the attach client's own async continuations, which carry no Rx scheduler affinity at all.
public sealed class TerminalTabViewModel : ReactiveObject {
    const int ResolvePending = 0, ResolveDtoWon = 1, ResolveTimeoutWon = 2, ResolveDisposed = 3;

    static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(10);
    static readonly TimeSpan DetachBound   = TimeSpan.FromSeconds(1);
    static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(3);

    // Codex's TUI suppresses Enter-as-submit for 120ms after ingesting a paste and turns a CR
    // inside that window into a newline.
    static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(150);

    const int DefaultCols = 80, DefaultRows = 24;

    readonly string _agentId;
    readonly IDaemonClientService _daemon;
    readonly TerminalAttachClientFactory _factory;
    readonly Func<ITerminalSurface> _surfaceFactory;
    readonly TimeProvider _time;

    // Try-entered only (Wait(0)/Release), never WaitAsync -- see the class doc comment.
    readonly SemaphoreSlim _attachLane = new(1, 1);

    int _resolveState;
    IDisposable? _agentsSub;
    ITimer? _resolveTimer;

    int _attemptGeneration;
    ITerminalAttachClient? _client;
    CancellationTokenSource? _attemptCts;
    Task? _runTask;

    TerminalSessionState _state = TerminalSessionState.Resolving;
    /// Bound; mutated only on the UI scheduler (see class doc comment), and only from Publish.
    public TerminalSessionState State {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    // The send gate. Only BeginAttempt and Invalidate advance the token; an attempt carries the
    // token its BeginAttempt produced and may open the gate only while that token is current.
    // Neither State nor _client can serve as the gate alone: a reattach leaves both Attached and
    // live while it awaits the old client's disposal, and a detach leaves both unchanged while it
    // awaits the detach write.
    int _openingToken;
    // Which attempt's Attached opened the gate, rather than a flag any of them may set. Open is
    // then DERIVED from ownership, so advancing the token is itself what closes the gate: an
    // attempt whose token check passed and whose gate-opening write lands after a concurrent
    // BeginAttempt opens nothing, and no invalidation path has to remember to close it. Starts at
    // a value no token takes, since Interlocked.Increment hands out 1 upwards.
    int _gateOpenToken = -1;
    // Whose send is in flight, on the same derived footing as _gateOpenToken: a check-then-set
    // flag could be set by an acceptance that raced a claim and then never cleared, since a
    // delivery only clears a flag it still owns -- wedging the composer for the tab's whole life.
    int _sendInFlightToken = -1;
    Task? _delivery;

    /// Bound; true from a send's acceptance until its delivery ends.
    public bool SendInFlight => Volatile.Read(ref _sendInFlightToken) == Volatile.Read(ref _openingToken);

    /// The gate itself: the attempt that opened it still owns the token.
    bool GateOpen => Volatile.Read(ref _gateOpenToken) == Volatile.Read(ref _openingToken);

    /// Bound; the one authority TrySendText itself consults, so can-execute and acceptance can
    /// never disagree.
    public bool CanAcceptText => GateOpen && !SendInFlight;

    /// Bound; the gate folded into the state, so a hint built from it is true in every window.
    public SendAvailability SendAvailability {
        get {
            if (SendInFlight) return SendAvailability.Sending;
            if (State is { Phase: TerminalSessionPhase.Attached, ReadOnly: false })
                return GateOpen ? SendAvailability.Ready : SendAvailability.Transitioning;
            return State.Phase switch {
                TerminalSessionPhase.Attached => SendAvailability.ReadOnly,
                TerminalSessionPhase.Resolving or TerminalSessionPhase.Connecting => SendAvailability.Connecting,
                TerminalSessionPhase.Detached or TerminalSessionPhase.Failed => SendAvailability.Reattach,
                TerminalSessionPhase.Exited or TerminalSessionPhase.SessionEnded => SendAvailability.Ended,
                _ => SendAvailability.NoTerminal,
            };
        }
    }

    internal int OpeningTokenForTesting => Volatile.Read(ref _openingToken);
    internal bool SendGateOpenForTesting => GateOpen;
    internal Task? PendingDeliveryForTesting => _delivery;

    void RaiseSendProjections() {
        this.RaisePropertyChanged(nameof(SendInFlight));
        this.RaisePropertyChanged(nameof(CanAcceptText));
        this.RaisePropertyChanged(nameof(SendAvailability));
    }

    /// Closes the gate and hands the caller a fresh token, which that attempt carries for its
    /// whole life.
    int BeginAttempt() {
        var token = Interlocked.Increment(ref _openingToken);
        RaiseSendProjections();
        return token;
    }

    /// Advances ownership past every attempt alive: an already-closed gate still allocates, since
    /// a callback queued mid-Connecting must lose its right to open.
    void Invalidate() {
        Interlocked.Increment(ref _openingToken);
        RaiseSendProjections();
    }

    /// Invalidate, but only while `ownerToken` is still the current one -- as a SINGLE atomic
    /// step. Compare-then-Invalidate would be two, and BeginAttempt runs on whatever thread
    /// called ReattachCommand.Execute (not necessarily the UI one), so a claim landing between
    /// them would be advanced past by the increment that follows: the replacement attempt
    /// retired by the very outcome this guard exists to keep out of its way.
    void InvalidateOwner(int ownerToken) {
        Interlocked.CompareExchange(ref _openingToken, ownerToken + 1, ownerToken);
        RaiseSendProjections();
    }

    /// The one place State is assigned. An attempt-owned publish (Connecting, Attached) carries
    /// its token and is discarded when that token is stale.
    ///
    /// A terminal state renders whether or not its token is still current -- an explicit detach
    /// invalidates BEFORE the Detached outcome it asked for arrives -- but advances the token
    /// only while its own attempt still holds it. A reattach takes its token before it disposes
    /// the client whose outcome this is, so advancing past a stale one would retire the
    /// replacement attempt that already owns it, silently discarding its Connecting and Attached.
    /// `null` is the unconditional form, for the invalidations no attempt owns (a removal, the
    /// resolve gate's verdicts).
    void Publish(TerminalSessionState state, int? ownerToken) {
        if (state.Phase is TerminalSessionPhase.Connecting or TerminalSessionPhase.Attached) {
            if (ownerToken != Volatile.Read(ref _openingToken)) return;
            State = state;
            if (state is { Phase: TerminalSessionPhase.Attached, ReadOnly: false })
                Volatile.Write(ref _gateOpenToken, ownerToken.Value);
            RaiseSendProjections();
            return;
        }
        // State first: Invalidate raises the projections, and they must not be read against the
        // state this publish is replacing.
        State = state;
        if (ownerToken is { } owner) InvalidateOwner(owner);
        else Invalidate();
    }

    /// Synchronous acceptance on the UI thread: true means the text is on its way through the
    /// current client and the composer may clear; false means nothing was written.
    public bool TrySendText(string text) {
        // Token, then client, then token again: _client is only ever replaced (or nulled) AFTER an
        // advance, so an unmoved token across the read proves this client is the one that token
        // owns. Reading the client first instead pairs a retired client with its replacement's
        // token -- which DeliverAsync's own check then reads as current, sending the submit CR
        // into the client being disposed.
        var token = Volatile.Read(ref _openingToken);
        var inFlight = Volatile.Read(ref _sendInFlightToken);
        if (!CanAcceptText || _client is not { } client) return false;
        if (Volatile.Read(ref _openingToken) != token) return false;
        // Claim, rather than assign: CanAcceptText's in-flight half and the write that satisfies
        // it are otherwise two steps, so two acceptances could both pass and the first delivery's
        // clear would release the second while it is still writing.
        if (Interlocked.CompareExchange(ref _sendInFlightToken, token, inFlight) != inFlight) return false;
        RaiseSendProjections();
        _delivery = DeliverAsync(client, token, TerminalInputEncoder.Paste(text));
        return true;
    }

    // Paste, wait out the TUI's post-paste Enter suppression, then one CR -- only if nothing
    // advanced the token meanwhile, since the paste already went to a client a replacement must
    // never follow with a stray Enter. A fault ends the delivery and never touches State: the
    // attach outcome is the transport's own channel.
    async Task DeliverAsync(ITerminalAttachClient client, int token, byte[] paste) {
        try {
            await client.SendInputAsync(paste).ConfigureAwait(false);
            await Task.Delay(SubmitDelay, _time).ConfigureAwait(false);
            if (Volatile.Read(ref _openingToken) != token) return;
            await client.SendInputAsync(TerminalInputEncoder.Submit).ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: composer send failed: {ex.Message}");
        } finally {
            if (Volatile.Read(ref _resolveState) != ResolveDisposed && Volatile.Read(ref _openingToken) == token) {
                await Dispatcher.UIThread.InvokeAsync(() => {
                    if (Volatile.Read(ref _resolveState) == ResolveDisposed) return;
                    if (Volatile.Read(ref _openingToken) != token) return;
                    Interlocked.CompareExchange(ref _sendInFlightToken, -1, token);
                    RaiseSendProjections();
                });
            }
        }
    }

    ITerminalSurface? _surface;
    /// The VM-owned model handle the view binds -- a fresh instance per attempt, assigned on the
    /// UI scheduler before the attempt's RunAsync starts.
    public ITerminalSurface? Surface {
        get => _surface;
        private set => this.RaiseAndSetIfChanged(ref _surface, value);
    }

    public ReactiveCommand<Unit, Unit> ReattachCommand { get; }
    public ReactiveCommand<Unit, Unit> DetachCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryResolveCommand { get; }

    /// Test-only seam: the in-flight resolve-triggered work (the has_terminal gate's Dispatcher
    /// hop, or the first attach attempt's swap) so a test can await the exact completion the
    /// Agents-cache push kicked off, instead of a fixed delay. Null once settled.
    internal Task? PendingResolveWorkForTesting { get; private set; }

    /// Test-only seam: the current attempt's RunAttemptAsync task (attach + pump + outcome
    /// mapping) -- awaited after driving a fake client's Result/RunStarted from outside.
    internal Task? CurrentRunForTesting => _runTask;

    public TerminalTabViewModel(
            string agentId, IDaemonClientService daemon, TerminalAttachClientFactory factory,
            Func<ITerminalSurface> surfaceFactory, TimeProvider time) {
        _agentId = agentId;
        _daemon = daemon;
        _factory = factory;
        _surfaceFactory = surfaceFactory;
        _time = time;

        ReattachCommand = ReactiveCommand.CreateFromTask(TryStartAttemptAsync);
        DetachCommand = ReactiveCommand.CreateFromTask(RunDetachAsync);
        RetryResolveCommand = ReactiveCommand.CreateFromTask(RetryResolveAsync);

        // Alive for the VM's whole lifetime UNLESS the timeout wins (disposed there) or
        // TeardownAsync runs -- a dto-win leaves it live so a LATER removal renders SessionEnded.
        _agentsSub = daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged);

        _resolveTimer = time.CreateTimer(_ => OnResolveTimeout(), null, ResolveBudget, Timeout.InfiniteTimeSpan);
    }

    void OnAgentsChanged(IChangeSet<AgentStatusDto, string> changes) {
        foreach (var change in changes) {
            if (change.Key != _agentId) continue;
            switch (change.Reason) {
                case ChangeReason.Add or ChangeReason.Update:
                    HandleDtoObserved(change.Current);
                    break;
                case ChangeReason.Remove:
                    HandleAgentEnded();
                    break;
            }
        }
    }

    void HandleDtoObserved(AgentStatusDto dto) {
        // First observation wins the resolve gate; later updates only matter when the agent has
        // already finished (Completed/Failed stay in the cache until teardown removes them).
        var prior = Interlocked.CompareExchange(ref _resolveState, ResolveDtoWon, ResolvePending);
        if (prior == ResolvePending) {
            _resolveTimer?.Dispose();
            _resolveTimer = null;
            PendingResolveWorkForTesting = RunResolveWorkAsync(dto);
            return;
        }
        if (prior == ResolveDtoWon && SessionStatusDots.IsTerminal(dto.Status))
            HandleAgentEnded();
    }

    /// Fault-observing wrapper around ApplyResolvedDtoAsync: this Task is fire-and-forget from
    /// HandleDtoObserved in production (nothing else awaits it there), so an unhandled fault
    /// (e.g. the surface factory throwing inside the swap) would otherwise be an unobserved task
    /// exception AND leave the tab stuck in Resolving forever. A non-OCE fault renders as a local
    /// Failed instead -- but only if nothing else has since moved the tab past Resolving (a
    /// concurrent attempt that legitimately succeeded must never be clobbered by a stale fault).
    async Task RunResolveWorkAsync(AgentStatusDto dto) {
        try {
            await ApplyResolvedDtoAsync(dto).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Retired/torn down mid-resolve -- expected, not an error.
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: terminal resolve failed: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => {
                if (Volatile.Read(ref _resolveState) == ResolveDisposed) return;
                if (State.Phase != TerminalSessionPhase.Resolving) return;
                Publish(TerminalSessionState.Failed($"couldn't open the terminal: {ex.Message}"), null);
            });
        }
    }

    void HandleAgentEnded() {
        // Meaningful only once resolved via a DTO (not a pending or timed-out gate, which have
        // their own terminal renderings already).
        if (Volatile.Read(ref _resolveState) != ResolveDtoWon) return;
        // Signal precedence: the run's own Exited/Failed verdict outranks a cache end.
        if (State.Phase is TerminalSessionPhase.Exited or TerminalSessionPhase.Failed
            or TerminalSessionPhase.SessionEnded) return;

        Publish(TerminalSessionState.SessionEnded, null);
    }

    void OnResolveTimeout() {
        if (Interlocked.CompareExchange(ref _resolveState, ResolveTimeoutWon, ResolvePending) != ResolvePending) return;

        _agentsSub?.Dispose();
        _agentsSub = null;

        // The timer callback fires off-thread in production and synchronously on the CALLING
        // thread under FakeTimeProvider.Advance -- Schedule (not a raw write) is what keeps this
        // on the UI scheduler either way, and synchronous under WithImmediateRxScheduler's
        // ImmediateScheduler so a test can assert right after Advance() returns.
        RxSchedulers.MainThreadScheduler.Schedule(() => Publish(TerminalSessionState.NotFound, null));
    }

    /// CAS (not read-then-write): only a timed-out gate is eligible for retry, and only if
    /// nothing else -- least of all a concurrent TeardownAsync -- has since claimed the slot. A
    /// plain Volatile.Write here would resurrect a gate TeardownAsync already disposed (setting
    /// it back to dto-won and re-arming a subscription teardown already tore down), and Retry is
    /// also meaningless from an already-resolved (dto-won) gate -- it must never layer a second
    /// attach attempt on top of one that already succeeded.
    async Task RetryResolveAsync() {
        if (Interlocked.CompareExchange(ref _resolveState, ResolveDtoWon, ResolveTimeoutWon) != ResolveTimeoutWon) return;

        var lookup = _daemon.Agents.Lookup(_agentId);
        if (!lookup.HasValue) {
            // Nothing to resolve against yet -- restore timeout-won (CAS, so a concurrent
            // TeardownAsync's Disposed still wins permanently over this restore) so a later
            // retry can try again.
            Interlocked.CompareExchange(ref _resolveState, ResolveTimeoutWon, ResolveDtoWon);
            return;
        }

        // Re-check right before touching the subscription: a concurrent TeardownAsync may have
        // disposed it already (it always wins the CAS above too, since Disposed != TimeoutWon --
        // this only guards a teardown landing in the tiny window between the CAS and here).
        if (Volatile.Read(ref _resolveState) == ResolveDisposed) return;
        _agentsSub ??= _daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged);

        await RunResolveWorkAsync(lookup.Value).ConfigureAwait(false);
    }

    async Task ApplyResolvedDtoAsync(AgentStatusDto dto) {
        if (!HostedHarnessCatalog.ShowsTerminal(dto.HasTerminal, dto.Vendor)) {
            await Dispatcher.UIThread.InvokeAsync(() => {
                // Disposal wins permanently: never render a note for a tab TeardownAsync already
                // tore down (a concurrent teardown could land while this hop is in flight).
                if (Volatile.Read(ref _resolveState) == ResolveDisposed) return;
                Publish(TerminalSessionState.NoTerminal(HostedHarnessCatalog.NoTerminalNote(dto.HasTerminal, dto.Vendor)), null);
            });
            return;
        }

        await TryStartAttemptAsync().ConfigureAwait(false);
    }

    /// Attach or reattach: retires whatever attempt is current (cancel + await-dispose its
    /// client), then swaps in a fresh surface/decoder and starts a new one. Try-entered only --
    /// see the class doc comment for why a losing call is a silent no-op, not a queued retry.
    ///
    /// Disposal wins permanently, which this method has TWO suspension points to prove against
    /// (the previous client's await-dispose, and the UI swap): a TeardownAsync landing in either
    /// window must neither leak a live, undisposed client (nothing would ever dispose it again --
    /// TeardownAsync is idempotent) nor let this attempt publish over what teardown already tore
    /// down. Every suspension point is followed by a re-check of BOTH `_resolveState ==
    /// ResolveDisposed` (teardown ran, at any point, regardless of generation ordering) and
    /// `generation != _attemptGeneration` (a DIFFERENT concurrent attempt retired this one, the
    /// pre-existing non-teardown case) -- disposing anything this attempt already built before
    /// bailing.
    async Task TryStartAttemptAsync() {
        if (!_attachLane.Wait(0)) return;
        try {
            if (Retired(0)) return; // teardown may have already run before this call got the lane

            var openingToken = BeginAttempt();

            var prevClient = _client;
            var prevCts = _attemptCts;
            prevCts?.Cancel();
            if (prevClient is not null) {
                try { await prevClient.DisposeAsync().ConfigureAwait(false); }
                catch { /* contained -- teardown diagnostic only, never VM state */ }
            }

            // Suspension point 1: TeardownAsync may have landed while the previous client's
            // dispose was in flight.
            if (Retired(0)) return;
            // A removal or an explicit detach landing in that same window retires no attempt
            // generation, so only the opening token catches it -- and an attempt that never
            // reached Connecting must not publish over what that invalidation already rendered.
            if (Volatile.Read(ref _openingToken) != openingToken) return;

            var generation = Interlocked.Increment(ref _attemptGeneration);
            var cts = new CancellationTokenSource();
            // Hoisted once: TeardownAsync never disposes an attempt's CTS (a disposed
            // CancellationTokenSource's .Token getter, and Register on any token sourced from
            // it, both throw ObjectDisposedException regardless of when the token was captured),
            // so this single read stays valid for the rest of the attempt's life.
            var token = cts.Token;
            _attemptCts = cts;

            // Pre-swap fallback -- overwritten below once the real surface exists. Never actually
            // observed as RunAsync's argument on the success path (the only way past the dispatch
            // below without a real surface is the OperationCanceledException catch, which returns
            // before cols/rows are ever read), but keeps the constant genuinely load-bearing rather
            // than a stray literal.
            var (cols, rows) = (DefaultCols, DefaultRows);

            ITerminalSurface surface;
            Utf8StreamDecoder decoder;
            try {
                // Construction happens INSIDE the dispatch, not just the property assignment: the
                // surface factory wraps an Avalonia-affine control model (Task 11/12), so even
                // building it off the UI thread would be unsafe -- not only assigning it.
                (surface, decoder) = await Dispatcher.UIThread.InvokeAsync(() => {
                    var s = _surfaceFactory();
                    var d = new Utf8StreamDecoder();
                    Surface = s;
                    Publish(TerminalSessionState.Connecting, openingToken);
                    return (s, d);
                }, DispatcherPriority.Default, token);
            } catch (OperationCanceledException) {
                return; // retired before the swap even finished
            }

            // By now the dispatched swap above has run the view's Model binding AND the control's
            // own synchronous Model-assignment resize (Task 11/12) -- CurrentSize is the real pane
            // size, not the phantom default above. Read it BEFORE RunAsync starts (not resent
            // after Attached, final review I1/I1-rework): AgentAttachClient's own post-attach nudge
            // writes at whatever size RunAsync was given, and the pump's OWN follow-on write beat a
            // same-callback resend in practice -- last write wins on the wire, so a resend from
            // inside OnAttachedAsync is a structurally defeated no-op. A never-laid-out first open
            // still reads the ctor's own 80x24 (App.axaml.cs's XtermTerminalSurface(80, 24)) here
            // too; WireSurface's Resized lane self-heals that once the control is actually measured.
            (cols, rows) = surface.CurrentSize;

            // Suspension point 2 (the one most likely to straddle a concurrent TeardownAsync):
            // never call the factory for a retired attempt.
            if (Retired(generation)) return;

            var client = _factory(
                _agentId,
                (snapshot, reason, ct) => OnAttachedAsync(generation, openingToken, surface, decoder, snapshot, reason, ct),
                (bytes, ct) => OnOutputAsync(generation, surface, decoder, bytes, ct));

            // One more check right before publishing: a retirement landing exactly between the
            // (synchronous) factory call above and here must still not leak the client it built.
            if (Retired(generation)) {
                try { await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* contained */ }
                return;
            }

            _client = client;
            WireSurface(surface, client, generation);
            _runTask = RunAttemptAsync(generation, openingToken, client, surface, decoder, cols, rows, token);
        } catch (OperationCanceledException) {
            // Retired before the swap even finished (e.g. TeardownAsync raced this call) --
            // expected, not an error.
        } finally {
            _attachLane.Release();
        }
    }

    /// True once TeardownAsync has run (permanently), or once a DIFFERENT concurrent attempt has
    /// retired the one identified by `generation` (0 means "no attempt identity yet" -- only the
    /// disposal half applies). See TryStartAttemptAsync's doc comment for why both are checked.
    bool Retired(int generation) =>
        Volatile.Read(ref _resolveState) == ResolveDisposed
        || (generation != 0 && generation != Volatile.Read(ref _attemptGeneration));

    void WireSurface(ITerminalSurface surface, ITerminalAttachClient client, int generation) {
        // Read-only is belt and braces: the client itself also guards SendInputAsync/ResizeAsync,
        // but suppressing here means a read-only attach never even attempts the round trip.
        surface.InputProduced += bytes => {
            if (Retired(generation) || State.ReadOnly) return;
            _ = client.SendInputAsync(bytes);
        };
        surface.Resized += (cols, rows) => {
            if (Retired(generation) || State.ReadOnly) return;
            _ = client.ResizeAsync(cols, rows);
        };
    }

    async Task OnAttachedAsync(int generation, int openingToken, ITerminalSurface surface, Utf8StreamDecoder decoder, byte[] snapshot, string? reason, CancellationToken ct) {
        var text = decoder.Decode(snapshot);
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (Retired(generation)) return;
            surface.Feed(text);
            Publish(TerminalSessionState.Attached(reason), openingToken);
        }, DispatcherPriority.Default, ct);
    }

    async Task OnOutputAsync(int generation, ITerminalSurface surface, Utf8StreamDecoder decoder, byte[] bytes, CancellationToken ct) {
        var text = decoder.Decode(bytes);
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (Retired(generation)) return;
            surface.Feed(text);
        }, DispatcherPriority.Default, ct);
    }

    async Task RunAttemptAsync(int generation, int openingToken, ITerminalAttachClient client, ITerminalSurface surface, Utf8StreamDecoder decoder, int cols, int rows, CancellationToken ct) {
        AttachOutcome outcome;
        try {
            outcome = await client.RunAsync(cols, rows, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return; // retired attempt's own cancellation -- silent, never an error
        } catch (AttachCallbackException ex) {
            await FinishAttemptAsync(generation, openingToken, surface, decoder, TerminalSessionState.Failed(Describe(ex))).ConfigureAwait(false);
            return;
        } catch (Exception ex) {
            await FinishAttemptAsync(generation, openingToken, surface, decoder, TerminalSessionState.Failed(ex.Message)).ConfigureAwait(false);
            return;
        }

        var mapped = outcome switch {
            AttachOutcome.Detached          => TerminalSessionState.DetachedState,
            AttachOutcome.Exited(var code)   => TerminalSessionState.Exited(code),
            AttachOutcome.Failed(var msg)    => TerminalSessionState.Failed(msg),
            AttachOutcome.ConnectionLost    => TerminalSessionState.Failed("the terminal lost connection to the daemon"),
            _                                => TerminalSessionState.Failed("unknown attach outcome"),
        };
        await FinishAttemptAsync(generation, openingToken, surface, decoder, mapped).ConfigureAwait(false);
    }

    static string Describe(AttachCallbackException ex) => ex.InnerException?.Message ?? ex.Message;

    // CancellationToken.None deliberately: the Retired check inside the dispatched action already
    // guards correctness, and the outcome must still apply when the attempt's own token is
    // cancelled but the attempt is not yet retired (e.g. a graceful Detached racing
    // TeardownAsync's early cts.Cancel(), which lands before its generation bump).
    //
    // Flushes the decoder here too, not just on every live frame: a terminal completion (Exited,
    // ConnectionLost, a mapped Failed) can land with a multibyte code point still buffered inside
    // the decoder's own carry-over state (Utf8StreamDecoder.Decode never flushes on its own), and
    // nothing else in this VM ever calls Flush -- without it, that trailing partial sequence is
    // silently dropped instead of rendering (typically as U+FFFD, same as any other genuinely
    // truncated stream).
    Task FinishAttemptAsync(int generation, int openingToken, ITerminalSurface surface, Utf8StreamDecoder decoder, TerminalSessionState state) =>
        Dispatcher.UIThread.InvokeAsync(() => {
            if (Retired(generation)) return;
            var remainder = decoder.Flush();
            if (remainder.Length > 0) surface.Feed(remainder);
            Publish(state, openingToken);
        }, DispatcherPriority.Default, CancellationToken.None).GetTask();

    async Task RunDetachAsync() {
        Invalidate();
        var client = _client;
        if (client is null) return;
        try { await client.DetachAsync().ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: terminal detach failed: {ex.Message}"); }
    }

    /// Bounded teardown (Task 13's tracker calls this when the tab closes): generation bump so any
    /// still-in-flight completion becomes silently retired, then detach/dispose/the run task each
    /// draw from a SINGLE 3s-total budget in turn (detach itself additionally capped at 1s) --
    /// every step abandons rather than blocks once its share is exhausted: an abandoned task's
    /// later fault is observed once (Console.Error) and never re-enters VM state. DisposeAsync
    /// specifically MUST share this budget, not run unbounded inside it -- the real client's
    /// DisposeAsync awaits its own pump to fully unwind, which is exactly the thing the budget
    /// exists to bound.
    ///
    /// Deliberately never disposes `cts`: TryStartAttemptAsync may still be straddling this same
    /// attempt (reading its hoisted token) when teardown lands, and a disposed
    /// CancellationTokenSource throws ObjectDisposedException from BOTH re-reading .Token and
    /// registering a callback on any token sourced from it, regardless of when that token was
    /// captured -- cancelling it is enough; a small leaked CTS (no timer, no WaitHandle ever
    /// touched) is the acceptable trade against a straddling attempt crashing the app.
    public async Task TeardownAsync() {
        if (Interlocked.Exchange(ref _resolveState, ResolveDisposed) == ResolveDisposed) return; // idempotent

        Invalidate();

        _agentsSub?.Dispose();
        _agentsSub = null;
        _resolveTimer?.Dispose();
        _resolveTimer = null;

        Interlocked.Increment(ref _attemptGeneration);

        var client = _client;
        var cts = _attemptCts;
        var runTask = _runTask;
        _client = null;
        _attemptCts = null;

        cts?.Cancel();

        var start = _time.GetUtcNow();
        TimeSpan Remaining() {
            var left = TeardownBudget - (_time.GetUtcNow() - start);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        if (client is not null) {
            var detach = client.DetachAsync();
            var detachWinner = await Task.WhenAny(detach, Task.Delay(DetachBound, _time)).ConfigureAwait(false);
            if (!ReferenceEquals(detachWinner, detach)) ObserveAbandoned(detach, "detach");

            var dispose = client.DisposeAsync().AsTask();
            var disposeWinner = await Task.WhenAny(dispose, Task.Delay(Remaining(), _time)).ConfigureAwait(false);
            if (ReferenceEquals(disposeWinner, dispose)) {
                // Completed within budget -- still observe (never re-throw) a fault the same way
                // the old direct-await's containment did.
                if (dispose.IsFaulted) _ = dispose.Exception;
            } else {
                ObserveAbandoned(dispose, "dispose");
            }
        }

        if (runTask is not null) {
            var runWinner = await Task.WhenAny(runTask, Task.Delay(Remaining(), _time)).ConfigureAwait(false);
            if (!ReferenceEquals(runWinner, runTask)) ObserveAbandoned(runTask, "run");
        }
        _runTask = null;

        // Bounded, not fire-and-forget: a test proving the surface reference is released depends
        // on this having actually landed by the time TeardownAsync returns, and an unbounded
        // await here would defeat the whole point of a bounded teardown if the dispatcher were
        // ever wedged. Not carved from the (possibly already-exhausted) 3s above -- this is a
        // trivial property set, not something that should ever race the run/dispose budget.
        var clear = Dispatcher.UIThread.InvokeAsync(() => { Surface = null; }).GetTask();
        await Task.WhenAny(clear, Task.Delay(DetachBound, _time)).ConfigureAwait(false);
    }

    static void ObserveAbandoned(Task task, string label) =>
        _ = task.ContinueWith(t => {
            if (t.IsFaulted && t.Exception?.InnerException is { } ex and not OperationCanceledException)
                Console.Error.WriteLine($"kcap: terminal attach teardown: abandoned {label} faulted: {ex.Message}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
