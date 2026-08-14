namespace Capacitor.App.Services.Mutation;

/// The per-verb classification seam Task 9b implements; kept separate so the lane's control flow never has to change to swap it.
internal delegate Task<MutationOutcome> MutationClassifier(
    MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
    string? attemptId, CancellationToken ct);

/// The app-lifetime singleton every daemon mutation runs through: one owned action at a time,
/// identical concurrent requests coalesce, a different request queues FIFO for its own fresh probe.
public sealed class DaemonMutationLane : IAsyncDisposable {
    readonly IProcessRunner _runner;
    readonly ILoginShellProbe _shellProbe;
    readonly OutcomeChannel _channel;
    readonly Func<string?> _cliOverride;
    readonly Func<MutationRequest, string?, IKcapCli> _executorFactory;
    readonly Func<MutationRequest, IDaemonObservation> _oneShotFactory;
    readonly TimeProvider _time;
    readonly CancellationTokenSource _lifetime = new();

    readonly object _gate = new();
    ActionSlot? _owned;
    readonly List<ActionSlot> _queue = [];
    TaskCompletionSource _quiescent = CompletedSignal();

    // Atomic slot: an owned action reads this ONCE at start and never again for its own lifetime.
    volatile IDaemonObservation? _liveAdapter;

    // Injectable seam: 9a wires the placeholder below, Task 9b swaps in the real classifier.
    internal MutationClassifier Classify { get; set; } = ClassifyPlaceholder;

    public DaemonMutationLane(
            IProcessRunner runner, ILoginShellProbe shellProbe, OutcomeChannel channel,
            Func<string?> cliOverride, Func<MutationRequest, string?, IKcapCli> executorFactory,
            Func<MutationRequest, IDaemonObservation> oneShotFactory, TimeProvider time) {
        _runner          = runner;
        _shellProbe      = shellProbe;
        _channel         = channel;
        _cliOverride     = cliOverride;
        _executorFactory = executorFactory;
        _oneShotFactory  = oneShotFactory;
        _time            = time;
    }

    public void SetLiveAdapter(IDaemonObservation? live) => _liveAdapter = live;

    public async Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken waiterCt) {
        var slot = AttachOrCreate(request);
        try {
            return await slot.Completion.Task.WaitAsync(waiterCt).ConfigureAwait(false);
        } catch (OperationCanceledException) when (waiterCt.IsCancellationRequested) {
            DetachWaiter(slot); // this waiter only — the owned action keeps running under the lane's own token
            throw;
        }
    }

    public async Task QuiescedAsync(CancellationToken ct) {
        while (true) {
            Task wait;
            lock (_gate) {
                if (_owned is null && _queue.Count == 0) return;
                wait = _quiescent.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() {
        Task<MutationOutcome>? owned;
        lock (_gate) { owned = _owned?.Completion.Task; }

        _lifetime.Cancel();

        if (owned is not null) {
            try { await owned.ConfigureAwait(false); } catch { /* terminal disposition already lives on the slot's own TCS */ }
        }
        _lifetime.Dispose();
    }

    // --- admission / coalescing ---

    ActionSlot AttachOrCreate(MutationRequest request) {
        ActionSlot slot;
        ActionSlot? toStart = null;
        lock (_gate) {
            var existing = _owned is { } owned && owned.Request == request ? owned : _queue.Find(s => s.Request == request);
            if (existing is not null) {
                existing.WaiterCount++;
                return existing;
            }

            slot = new ActionSlot(request) { WaiterCount = 1 };
            if (_owned is null) {
                _owned = slot;
                if (_quiescent.Task.IsCompleted) _quiescent = NewSignal(); // idle -> busy
                toStart = slot;
            } else {
                _queue.Add(slot);
            }
        }
        if (toStart is not null) StartAction(toStart);
        return slot;
    }

    void DetachWaiter(ActionSlot slot) {
        lock (_gate) { slot.WaiterCount--; }
    }

    void StartAction(ActionSlot slot) => _ = RunOwnedAsync(slot);

    async Task RunOwnedAsync(ActionSlot slot) {
        try {
            var outcome = await ExecuteActionAsync(slot.Request, _lifetime.Token).ConfigureAwait(false);
            Deliver(slot, outcome);
        } catch (OperationCanceledException oce) {
            slot.Completion.TrySetCanceled(oce.CancellationToken);
        } catch (Exception ex) {
            slot.Completion.TrySetException(ex);
        } finally {
            FinishAndAdmitNext(slot);
        }
    }

    void FinishAndAdmitNext(ActionSlot finished) {
        ActionSlot? next = null;
        lock (_gate) {
            if (ReferenceEquals(_owned, finished)) _owned = null;
            if (_queue.Count > 0) {
                next = _queue[0];
                _queue.RemoveAt(0);
                _owned = next;
            } else {
                _quiescent.TrySetResult(); // busy -> idle
            }
        }
        if (next is not null) StartAction(next);
    }

    void Deliver(ActionSlot slot, MutationOutcome outcome) {
        var isSuccess = outcome is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout;
        if (!isSuccess) {
            _channel.Enqueue(new OutcomeEnvelope(slot.Request, outcome));
        } else {
            bool waiterless;
            lock (_gate) { waiterless = slot.WaiterCount <= 0; }
            if (waiterless) LogWaiterlessSuccess(slot.Request, outcome);
        }
        slot.Completion.TrySetResult(outcome);
    }

    // --- the owned action itself ---

    async Task<MutationOutcome> ExecuteActionAsync(MutationRequest request, CancellationToken ct) {
        var pinnedPath = _cliOverride() ?? await _shellProbe.KcapPathAsync(ct, forceRefresh: false).ConfigureAwait(false);
        if (pinnedPath is null) return new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention);

        var executor = _executorFactory(request, pinnedPath); // built ONCE; the same instance runs the probe and the mutation
        var version = await executor.VersionAsync(ct).ConfigureAwait(false);
        if (!KcapCliCompatibility.Satisfies(version)) return new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention);

        var observation = await PinObservationAsync(request, ct).ConfigureAwait(false);

        var attemptId = request.Verb == MutationVerb.DetachedStart ? Guid.NewGuid().ToString("N") : null;
        var result = await Dispatch(executor, request, attemptId, ct).ConfigureAwait(false);

        return await Classify(request, result, executor, observation, attemptId, ct).ConfigureAwait(false);
    }

    static Task<ProcessResult> Dispatch(IKcapCli executor, MutationRequest request, string? attemptId, CancellationToken ct) =>
        request.Verb switch {
            MutationVerb.Install       => executor.ServiceInstallVerifiedAsync(replace: false, ct),
            MutationVerb.Replace       => executor.ServiceInstallVerifiedAsync(replace: true, ct),
            MutationVerb.StartVerified => executor.ServiceStartVerifiedAsync(ct),
            MutationVerb.DetachedStart => executor.DetachedStartAsync(attemptId!, ct),
            // Fail closed, never permissive: an unnamed enum value must halt, not silently pick a verb.
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Verb, "unknown MutationVerb"),
        };

    // Live adapter usable only when it can actually target this request's identity; unset or a mismatch falls back to one-shot.
    async Task<IDaemonObservation> PinObservationAsync(MutationRequest request, CancellationToken ct) {
        var live = _liveAdapter;
        if (live is not null && await live.ObserveAsync(request, ct).ConfigureAwait(false) is not null) return live;
        return _oneShotFactory(request);
    }

    // Task 9b replaces this placeholder classification.
    static Task<MutationOutcome> ClassifyPlaceholder(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
            string? attemptId, CancellationToken ct) =>
        Task.FromResult<MutationOutcome>(result.ExitCode == 0
            ? new MutationOutcome.Succeeded()
            : new MutationOutcome.Failed(result.ExitCode, null, RecoverySurface.Attention));

    static void LogWaiterlessSuccess(MutationRequest request, MutationOutcome outcome) =>
        Console.Error.WriteLine(
            $"DaemonMutationLane: waiterless {outcome.GetType().Name} verb={request.Verb} profile={request.Profile} " +
            $"server={request.CanonicalServer} daemon={request.DaemonName}");

    static TaskCompletionSource CompletedSignal() {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    sealed class ActionSlot(MutationRequest request) {
        public MutationRequest Request { get; } = request;
        public TaskCompletionSource<MutationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaiterCount;
    }
}
