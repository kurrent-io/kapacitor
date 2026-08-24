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
///   ReactiveCommand.Execute() invokes its Func&lt;Task&gt; eagerly and unconditionally (verified
///   against the installed ReactiveUI 23.2.28 IL -- Execute() does NOT consult CanExecute/
///   IsExecuting), so a queueing WaitAsync() here would let a double-click spawn two real clients.
///   A call that loses the try-enter is a clean no-op, not a queued retry.
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
    /// Bound; mutated only on the UI scheduler (see class doc comment).
    public TerminalSessionState State {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
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
                    HandleAgentRemoved();
                    break;
            }
        }
    }

    void HandleDtoObserved(AgentStatusDto dto) {
        // Only the FIRST observation is a resolve-gate event; a later update (status text, etc.)
        // after the gate already decided is not re-run through it.
        if (Interlocked.CompareExchange(ref _resolveState, ResolveDtoWon, ResolvePending) != ResolvePending) return;

        _resolveTimer?.Dispose();
        _resolveTimer = null;

        PendingResolveWorkForTesting = ApplyResolvedDtoAsync(dto);
    }

    void HandleAgentRemoved() {
        // Meaningful only once resolved via a DTO (not a pending or timed-out gate, which have
        // their own terminal renderings already).
        if (Volatile.Read(ref _resolveState) != ResolveDtoWon) return;
        // Signal precedence: the run's own Exited/Failed verdict outranks a cache removal.
        if (State.Phase is TerminalSessionPhase.Exited or TerminalSessionPhase.Failed) return;

        State = TerminalSessionState.SessionEnded;
    }

    void OnResolveTimeout() {
        if (Interlocked.CompareExchange(ref _resolveState, ResolveTimeoutWon, ResolvePending) != ResolvePending) return;

        _agentsSub?.Dispose();
        _agentsSub = null;

        // The timer callback fires off-thread in production and synchronously on the CALLING
        // thread under FakeTimeProvider.Advance -- Schedule (not a raw write) is what keeps this
        // on the UI scheduler either way, and synchronous under WithImmediateRxScheduler's
        // ImmediateScheduler so a test can assert right after Advance() returns.
        RxSchedulers.MainThreadScheduler.Schedule(() => State = TerminalSessionState.NotFound);
    }

    async Task RetryResolveAsync() {
        if (Volatile.Read(ref _resolveState) == ResolveDisposed) return;

        var lookup = _daemon.Agents.Lookup(_agentId);
        if (!lookup.HasValue) return;

        Volatile.Write(ref _resolveState, ResolveDtoWon);
        // A prior timeout disposed this; retrying re-arms live removal-watching too.
        _agentsSub ??= _daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged);

        await ApplyResolvedDtoAsync(lookup.Value).ConfigureAwait(false);
    }

    async Task ApplyResolvedDtoAsync(AgentStatusDto dto) {
        if (!HostedHarnessCatalog.ShowsTerminal(dto.HasTerminal, dto.Vendor)) {
            await Dispatcher.UIThread.InvokeAsync(() => State = TerminalSessionState.NoTerminal(NoteFor(dto)));
            return;
        }

        await TryStartAttemptAsync().ConfigureAwait(false);
    }

    static string NoteFor(AgentStatusDto dto) {
        var family = HostedHarnessCatalog.EffectiveFamily(dto.HasTerminal, dto.Vendor).ToUpperInvariant();
        return $"This session runs over {family} — no terminal to attach to.";
    }

    /// Attach or reattach: retires whatever attempt is current (cancel + await-dispose its
    /// client), then swaps in a fresh surface/decoder and starts a new one. Try-entered only --
    /// see the class doc comment for why a losing call is a silent no-op, not a queued retry.
    async Task TryStartAttemptAsync() {
        if (!_attachLane.Wait(0)) return;
        try {
            var prevClient = _client;
            var prevCts = _attemptCts;
            prevCts?.Cancel();
            if (prevClient is not null) {
                try { await prevClient.DisposeAsync().ConfigureAwait(false); }
                catch { /* contained -- teardown diagnostic only, never VM state */ }
            }

            var generation = Interlocked.Increment(ref _attemptGeneration);
            var cts = new CancellationTokenSource();
            _attemptCts = cts;

            // Construction happens INSIDE the dispatch, not just the property assignment: the
            // surface factory wraps an Avalonia-affine control model (Task 11/12), so even
            // building it off the UI thread would be unsafe -- not only assigning it.
            var (surface, decoder) = await Dispatcher.UIThread.InvokeAsync(() => {
                var s = _surfaceFactory();
                var d = new Utf8StreamDecoder();
                Surface = s;
                State = TerminalSessionState.Connecting;
                return (s, d);
            }, DispatcherPriority.Default, cts.Token);

            var client = _factory(
                _agentId,
                (snapshot, reason, ct) => OnAttachedAsync(generation, surface, decoder, snapshot, reason, ct),
                (bytes, ct) => OnOutputAsync(generation, surface, decoder, bytes, ct));
            _client = client;
            WireSurface(surface, client, generation);

            _runTask = RunAttemptAsync(generation, client, cts.Token);
        } catch (OperationCanceledException) {
            // Retired before the swap even finished (e.g. TeardownAsync raced this call) --
            // expected, not an error.
        } finally {
            _attachLane.Release();
        }
    }

    void WireSurface(ITerminalSurface surface, ITerminalAttachClient client, int generation) {
        // Read-only is belt and braces: the client itself also guards SendInputAsync/ResizeAsync,
        // but suppressing here means a read-only attach never even attempts the round trip.
        surface.InputProduced += bytes => {
            if (generation != _attemptGeneration || State.ReadOnly) return;
            _ = client.SendInputAsync(bytes);
        };
        surface.Resized += (cols, rows) => {
            if (generation != _attemptGeneration || State.ReadOnly) return;
            _ = client.ResizeAsync(cols, rows);
        };
    }

    async Task OnAttachedAsync(int generation, ITerminalSurface surface, Utf8StreamDecoder decoder, byte[] snapshot, string? reason, CancellationToken ct) {
        var text = decoder.Decode(snapshot);
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (generation != _attemptGeneration) return;
            surface.Feed(text);
            State = TerminalSessionState.Attached(reason);
        }, DispatcherPriority.Default, ct);
    }

    async Task OnOutputAsync(int generation, ITerminalSurface surface, Utf8StreamDecoder decoder, byte[] bytes, CancellationToken ct) {
        var text = decoder.Decode(bytes);
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (generation != _attemptGeneration) return;
            surface.Feed(text);
        }, DispatcherPriority.Default, ct);
    }

    async Task RunAttemptAsync(int generation, ITerminalAttachClient client, CancellationToken ct) {
        AttachOutcome outcome;
        try {
            outcome = await client.RunAsync(DefaultCols, DefaultRows, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return; // retired attempt's own cancellation -- silent, never an error
        } catch (AttachCallbackException ex) {
            await ApplyOutcomeStateAsync(generation, TerminalSessionState.Failed(Describe(ex))).ConfigureAwait(false);
            return;
        } catch (Exception ex) {
            await ApplyOutcomeStateAsync(generation, TerminalSessionState.Failed(ex.Message)).ConfigureAwait(false);
            return;
        }

        var mapped = outcome switch {
            AttachOutcome.Detached          => TerminalSessionState.DetachedState,
            AttachOutcome.Exited(var code)   => TerminalSessionState.Exited(code),
            AttachOutcome.Failed(var msg)    => TerminalSessionState.Failed(msg),
            AttachOutcome.ConnectionLost    => TerminalSessionState.Failed("the terminal lost connection to the daemon"),
            _                                => TerminalSessionState.Failed("unknown attach outcome"),
        };
        await ApplyOutcomeStateAsync(generation, mapped).ConfigureAwait(false);
    }

    static string Describe(AttachCallbackException ex) => ex.InnerException?.Message ?? ex.Message;

    // CancellationToken.None deliberately: a retired attempt's generation check (inside the
    // dispatched action) already guards correctness, and the outcome must still apply when the
    // attempt's own token is cancelled but the generation is still current (e.g. a graceful
    // Detached racing TeardownAsync's early cts.Cancel()).
    Task ApplyOutcomeStateAsync(int generation, TerminalSessionState state) =>
        Dispatcher.UIThread.InvokeAsync(() => {
            if (generation != _attemptGeneration) return;
            State = state;
        }, DispatcherPriority.Default, CancellationToken.None).GetTask();

    async Task RunDetachAsync() {
        var client = _client;
        if (client is null) return;
        try { await client.DetachAsync().ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: terminal detach failed: {ex.Message}"); }
    }

    /// Bounded teardown (Task 13's tracker calls this when the tab closes): generation bump so any
    /// still-in-flight completion becomes silently retired, detach bounded to 1s, DisposeAsync
    /// immediately after regardless of whether the detach write landed, then the REMAINDER of a
    /// 3s total budget awaiting the run task. Either bound abandons rather than blocks: an
    /// abandoned task's later fault is observed once (Console.Error) and never re-enters VM state.
    public async Task TeardownAsync() {
        if (Interlocked.Exchange(ref _resolveState, ResolveDisposed) == ResolveDisposed) return; // idempotent

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
        if (client is not null) {
            var detach = client.DetachAsync();
            var detachWinner = await Task.WhenAny(detach, Task.Delay(DetachBound, _time)).ConfigureAwait(false);
            if (!ReferenceEquals(detachWinner, detach)) ObserveAbandoned(detach, "detach");

            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch { /* contained -- teardown diagnostic only */ }
        }
        cts?.Dispose();

        if (runTask is not null) {
            var elapsed = _time.GetUtcNow() - start;
            var remaining = TeardownBudget - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            var runWinner = await Task.WhenAny(runTask, Task.Delay(remaining, _time)).ConfigureAwait(false);
            if (!ReferenceEquals(runWinner, runTask)) ObserveAbandoned(runTask, "run");
        }
        _runTask = null;

        await Dispatcher.UIThread.InvokeAsync(() => { Surface = null; });
    }

    static void ObserveAbandoned(Task task, string label) =>
        _ = task.ContinueWith(t => {
            if (t.IsFaulted && t.Exception?.InnerException is { } ex and not OperationCanceledException)
                Console.Error.WriteLine($"kcap: terminal attach teardown: abandoned {label} faulted: {ex.Message}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
