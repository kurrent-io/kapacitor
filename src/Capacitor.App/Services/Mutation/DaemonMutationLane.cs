using System.Diagnostics;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Services.Mutation;

/// The per-verb classification seam the lane's control flow delegates into — kept separate so control flow never has to change when classification rules change.
internal delegate Task<MutationOutcome> MutationClassifier(
    MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
    string? attemptId, CancellationToken ct);

/// The app-lifetime singleton every daemon mutation runs through: one owned action at a time,
/// identical concurrent requests coalesce, a different request queues FIFO for its own fresh probe.
public sealed class DaemonMutationLane : IAsyncDisposable {
    // Named so DeliverFaulted's outcome is self-explanatory instead of a bare magic number.
    internal const int UnexpectedExitCode = -1;

    // The capability a positive service-verb/DetachedStart success requires the observed daemon to advertise.
    const string ConsentCapability = "consent/3";

    // The one EvidenceFailureLeg sentinel that means "not yet confirmable" rather than "a definite problem".
    const string UnreachableLeg = "unreachable";

    internal static readonly TimeSpan DetachedConfirmWindow = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DetachedPollInterval  = TimeSpan.FromSeconds(1);

    readonly ILoginShellProbe _shellProbe;
    readonly OutcomeChannel _channel;
    readonly Func<string?> _cliOverride;
    readonly Func<MutationRequest, string?, IKcapCli> _executorFactory;
    readonly Func<MutationRequest, IDaemonObservation> _oneShotFactory;
    readonly TimeProvider _time;
    readonly CancellationTokenSource _lifetime = new();
    // Captured once, before any dispose: _lifetime.Token throws ObjectDisposedException once the
    // source itself is disposed, but a token value obtained earlier stays safely readable/storable.
    readonly CancellationToken _lifetimeToken;

    readonly object _gate = new();
    ActionSlot? _owned;
    readonly List<ActionSlot> _queue = [];
    TaskCompletionSource _quiescent = CompletedSignal();
    bool _disposed;

    // Atomic slot: an owned action reads this ONCE at start and never again for its own lifetime.
    volatile IDaemonObservation? _liveAdapter;

    // Injectable seam: defaults to the real per-verb classifier below; tests override to pin/observe without running full classification.
    internal MutationClassifier Classify { get; set; }

    public DaemonMutationLane(
            ILoginShellProbe shellProbe, OutcomeChannel channel,
            Func<string?> cliOverride, Func<MutationRequest, string?, IKcapCli> executorFactory,
            Func<MutationRequest, IDaemonObservation> oneShotFactory, TimeProvider time) {
        _shellProbe      = shellProbe;
        _channel         = channel;
        _cliOverride     = cliOverride;
        _executorFactory = executorFactory;
        _oneShotFactory  = oneShotFactory;
        _time            = time;
        _lifetimeToken   = _lifetime.Token;
        Classify         = ClassifyOutcomeAsync;
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
        Task<MutationOutcome>? ownedTask;
        lock (_gate) {
            if (_disposed) return; // idempotent: a second call must not re-cancel/re-dispose the lifetime token
            _disposed = true;

            ownedTask = _owned?.Completion.Task;

            // Cancel every queued waiter up front: shutdown is not an actionable outcome (nothing
            // enqueued to the channel), and draining now means the owned action's own finish has
            // nothing left to admit once it completes. Parameterless TrySetCanceled — _lifetime.Cancel()
            // hasn't run yet at this point, so claiming _lifetime.Token here would let a waiter observe
            // a cancelled task whose token isn't actually cancelled yet.
            foreach (var queued in _queue) queued.Completion.TrySetCanceled();
            _queue.Clear();
        }

        _lifetime.Cancel();

        if (ownedTask is not null) {
            try { await ownedTask.ConfigureAwait(false); } catch { /* terminal disposition already lives on the slot's own TCS */ }
        }

        _lifetime.Dispose(); // only after the drain + owned-action await — no other path can still touch the token
    }

    // --- admission / coalescing ---

    ActionSlot AttachOrCreate(MutationRequest request) {
        ActionSlot slot;
        ActionSlot? toStart = null;
        lock (_gate) {
            if (_disposed) {
                // A disposed lane never spawns: resolve cancelled immediately, nothing owned/queued, nothing enqueued.
                var refused = new ActionSlot(request) { WaiterCount = 1 };
                refused.Completion.TrySetCanceled(_lifetimeToken);
                return refused;
            }

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
        MutationOutcome? outcome = null;
        OperationCanceledException? cancelled = null;
        Exception? fault = null;
        try {
            outcome = await ExecuteActionAsync(slot.Request, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException oce) {
            cancelled = oce;
        } catch (Exception ex) {
            fault = ex;
        }

        // Detach BEFORE resolving the outcome below: closes the window where a fresh RunAsync for
        // the SAME request could coalesce onto a slot whose result is already decided.
        var next = DetachAndAdmitNext(slot);

        if (outcome is not null) Deliver(slot, outcome);
        else if (cancelled is not null) DeliverCancelled(slot, cancelled);
        else DeliverFaulted(slot, fault!);

        // Recursive by construction when actions settle synchronously (fakes, or an already-exited
        // real child) — bounded by queue depth, which stays small in practice; real I/O never does this.
        if (next is not null) StartAction(next);
    }

    ActionSlot? DetachAndAdmitNext(ActionSlot finished) {
        ActionSlot? next = null;
        lock (_gate) {
            Debug.Assert(ReferenceEquals(_owned, finished), "DetachAndAdmitNext: the finishing action must still be the owned slot.");
            _owned = null;
            if (_queue.Count > 0) {
                next = _queue[0];
                _queue.RemoveAt(0);
                _owned = next;
            } else {
                _quiescent.TrySetResult(); // busy -> idle
            }
        }
        return next;
    }

    void Deliver(ActionSlot slot, MutationOutcome outcome) {
        var isSuccess = outcome is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout;
        if (!isSuccess) {
            _channel.Enqueue(new OutcomeEnvelope(slot.Request, outcome));
            slot.Completion.TrySetResult(outcome);
            return;
        }
        slot.Completion.TrySetResult(outcome); // resolve waiters FIRST, then decide waiterless — narrows the detach race
        bool waiterless;
        lock (_gate) { waiterless = slot.WaiterCount <= 0; }
        if (waiterless) LogWaiterlessSuccess(slot.Request, outcome);
    }

    // Lane-lifetime cancellation (Dispose) is not an actionable outcome: never enqueued, only logged when nobody is left to observe it directly.
    void DeliverCancelled(ActionSlot slot, OperationCanceledException oce) {
        slot.Completion.TrySetCanceled(oce.CancellationToken);
        bool waiterless;
        lock (_gate) { waiterless = slot.WaiterCount <= 0; }
        if (waiterless) LogWaiterlessCancellation(slot.Request);
    }

    // An unexpected exception during a mutation attempt is actionable evidence, not a fabricated exception surfaced to waiters — the exception itself stays in the log line only.
    void DeliverFaulted(ActionSlot slot, Exception ex) {
        LogUnexpectedFault(slot.Request, ex);
        var outcome = new MutationOutcome.Failed(UnexpectedExitCode, "internal_error", RecoverySurface.Attention);
        _channel.Enqueue(new OutcomeEnvelope(slot.Request, outcome));
        slot.Completion.TrySetResult(outcome);
    }

    // --- the owned action itself ---

    async Task<MutationOutcome> ExecuteActionAsync(MutationRequest request, CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); // a disposed lane's cancelled token must stop admission before any spawn

        // Pinned before the first await (spec pin-once rule): a concurrent SetLiveAdapter swap
        // during the CLI/version probes below must never change an already-started action's observer.
        var observation = PinObservation(request);

        var pinnedPath = _cliOverride() ?? await _shellProbe.KcapPathAsync(ct, forceRefresh: false).ConfigureAwait(false);
        // A cached negative must not refuse forever: one forced re-probe lets a CLI installed after
        // the cache went negative recover on the very next action, instead of requiring an app restart.
        pinnedPath ??= await _shellProbe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
        if (pinnedPath is null) return new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention);

        var executor = _executorFactory(request, pinnedPath); // built ONCE; the same instance runs the probe and the mutation
        var version = await executor.VersionAsync(ct).ConfigureAwait(false);
        if (!KcapCliCompatibility.Satisfies(version)) return new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention);

        var attemptId = request.Verb == MutationVerb.DetachedStart ? Guid.NewGuid().ToString("N") : null;

        // The runner starts the child before consulting ct, and an already-exited child's own wait
        // can return normally under a cancelled token — this is the last gate before a real spawn.
        ct.ThrowIfCancellationRequested();
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

    // Pinned ONCE at action start (before the caller's first await), so a later SetLiveAdapter call never changes an in-flight action's observer; DetachedStart always pins the one-shot factory, never the live adapter, because a one-shot dials fresh sockets per poll (post-mutation evidence by construction) while the live adapter would synchronously replay possibly pre-mutation status with no fresh-ownership cross-check to catch it. For every other verb an unset live adapter falls back to a plain one-shot probe, and a set one is wrapped in a COMPOSITE that tries live first and falls through to a FRESH one-shot whenever live's result is null or not Reachable (e.g. a mutation that restarted the daemon and tore down the app's own attach) — exactly one observation result feeds a given classification attempt, never a live/one-shot blend.
    IDaemonObservation PinObservation(MutationRequest request) {
        if (request.Verb == MutationVerb.DetachedStart) return _oneShotFactory(request);
        var live = _liveAdapter;
        return live is null ? _oneShotFactory(request) : new CompositeObservation(live, _oneShotFactory);
    }

    sealed class CompositeObservation(IDaemonObservation live, Func<MutationRequest, IDaemonObservation> oneShotFactory) : IDaemonObservation {
        public async Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
            var evidence = await live.ObserveAsync(request, ct).ConfigureAwait(false);
            return evidence is { Reachable: true } ? evidence : await oneShotFactory(request).ObserveAsync(request, ct).ConfigureAwait(false);
        }
    }

    // --- classification (spec §3/§4) ---

    Task<MutationOutcome> ClassifyOutcomeAsync(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
            string? attemptId, CancellationToken ct) =>
        request.Verb == MutationVerb.DetachedStart
            ? ClassifyDetachedStartAsync(request, result, observation, attemptId, ct)
            : ClassifyServiceVerbAsync(request, result, executor, observation, ct);

    // Install/Replace/StartVerified: the CLI's own ServiceVerify transaction engine already
    // performed its readiness poll — exit code alone routes every non-success case.
    static async Task<MutationOutcome> ClassifyServiceVerbAsync(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation, CancellationToken ct) {
        // A forced kill's exit code is not a verify outcome (the transaction may have already
        // committed) — never read as a success OR a coded failure; SucceededAfterTimeout is detached-only.
        if (result.TimedOut) return new MutationOutcome.UnconfirmedNoAttach();

        if (result.ExitCode == 0) return await ClassifyServiceSuccessAsync(request, executor, observation, ct).ConfigureAwait(false);

        if (result.ExitCode == VerifyExitCodes.StartGate) {
            var token = ReasonLine.TrySingle(result.Stderr, "start_gate_reason=");
            return token is null
                ? new MutationOutcome.Failed(VerifyExitCodes.StartGate, null, RecoverySurface.Attention)
                : new MutationOutcome.Failed(VerifyExitCodes.StartGate, token, ReasonRouting.ForStartGate(token));
        }

        if (result.ExitCode == VerifyExitCodes.StartGateDrift) {
            // Drift is never auto-retried — always Attention, regardless of whether a reason line happens to be present.
            var token = ReasonLine.TrySingle(result.Stderr, "start_gate_reason=");
            return new MutationOutcome.Failed(VerifyExitCodes.StartGateDrift, token, RecoverySurface.Attention);
        }

        if (result.ExitCode == VerifyExitCodes.ReadinessTimeout) {
            var token = ReasonLine.TrySingle(result.Stderr, "refusal_reason=");
            return token is null
                ? new MutationOutcome.UnconfirmedNoAttach()
                : new MutationOutcome.Refused(token, ReasonRouting.ForBootRefusal(token));
        }

        return new MutationOutcome.Failed(result.ExitCode, null, RecoverySurface.Attention);
    }

    // Positive evidence only (spec §6): every leg below must independently hold for Succeeded — any
    // gap degrades to AttentionSkew/AttentionRepair/UnconfirmedNoAttach, never a guessed success.
    static async Task<MutationOutcome> ClassifyServiceSuccessAsync(
            MutationRequest request, IKcapCli executor, IDaemonObservation observation, CancellationToken ct) {
        var evidence  = await observation.ObserveAsync(request, ct).ConfigureAwait(false);
        var ownership = await executor.ServiceStatusAsync(ct).ConfigureAwait(false);

        // Ownership-derived repair/skew signals are evaluated regardless of evidence reachability —
        // a stale marker or an orphaned unit is worth flagging even when the daemon can't be reached.
        if (ownership is not null && OwnershipRepairLeg(ownership) is { } repairLeg)
            return new MutationOutcome.AttentionRepair(repairLeg);

        var evidenceLeg = EvidenceFailureLeg(evidence, request);
        if (evidenceLeg == UnreachableLeg)
            // No independent evidence either way vs. a recorded owner with nothing to show for it are different signals.
            return ownership?.DaemonPid is null
                ? new MutationOutcome.UnconfirmedNoAttach()
                : new MutationOutcome.AttentionSkew("unreachable_with_recorded_owner");
        if (evidenceLeg is not null)
            return new MutationOutcome.AttentionSkew(evidenceLeg);

        if (ownership is null)
            return new MutationOutcome.AttentionSkew("ownership_unknown");

        if (ownership.JobPid is null || ownership.DaemonPid is null || ownership.JobPid != ownership.DaemonPid)
            return new MutationOutcome.AttentionSkew("ownership_mismatch");

        if (ownership.DaemonPid != evidence!.Pid) // evidenceLeg == null guarantees evidence is non-null and fully valid here
            return new MutationOutcome.AttentionSkew("instance_pid_mismatch"); // same-instance rule

        return new MutationOutcome.Succeeded();
    }

    // Mirrors DaemonLifecycleController.Reconcile's two orphan-unit predicates plus the stale-marker
    // check — pure signals from ONE ServiceStatusAsync read, independent of any evidence.
    static string? OwnershipRepairLeg(ServiceSnapshot ownership) {
        if (ownership.TxnMarker && !ownership.TxnActive) return "stale_txn_marker";

        var state = ServiceStateClassifier.Parse(ownership.State);
        if (state == ServiceState.Running && ownership.DaemonPid is null) return "running_without_daemon_pid";
        if (ownership.UnitPresent && state == ServiceState.NotInstalled && ownership.DaemonPid is not null) return "daemon_running_outside_service";

        return null;
    }

    // DetachedStart has no CLI-side verify engine — the lane itself confirms via a bounded post-spawn observation window.
    async Task<MutationOutcome> ClassifyDetachedStartAsync(
            MutationRequest request, ProcessResult result, IDaemonObservation observation, string? attemptId, CancellationToken ct) {
        // A forced process-only kill after the wrapper's own bound makes the exit code meaningless — only evidence counts.
        if (result.TimedOut)
            return await AwaitDetachedConfirmationAsync(
                request, observation, attemptId, static () => new MutationOutcome.SucceededAfterTimeout(), ct).ConfigureAwait(false);

        if (result.ExitCode == 0)
            return await AwaitDetachedConfirmationAsync(
                request, observation, attemptId, static () => new MutationOutcome.Succeeded(), ct).ConfigureAwait(false);

        if (result.ExitCode == VerifyExitCodes.DigestGate) {
            var token = ReasonLine.TrySingle(result.Stderr, "daemon_start_reason=");
            return token is null
                ? new MutationOutcome.Failed(VerifyExitCodes.DigestGate, null, RecoverySurface.Attention)
                : new MutationOutcome.Failed(VerifyExitCodes.DigestGate, token, ReasonRouting.ForDaemonStart(token));
        }

        return new MutationOutcome.Failed(result.ExitCode, null, RecoverySurface.Attention);
    }

    // Polls the pinned observation (marker checked first each iteration, so an attributed boot-refusal always wins Refused) for up to DetachedConfirmWindow: full evidence at any poll resolves immediately via onFullEvidence, a non-full non-unreachable leg is remembered and polled through rather than failing fast (only a single-poll snapshot), and only at window expiry does the last leg decide — "unreachable"/never-observed → UnconfirmedNoAttach, anything else → AttentionSkew(leg); entirely TimeProvider-driven. `attemptId` is null only via direct test injection through the Classify seam (a real DetachedStart action always carries one) and must never attribute a marker, since a null-attempt marker belongs to a service-verb refusal, not this action.
    async Task<MutationOutcome> AwaitDetachedConfirmationAsync(
            MutationRequest request, IDaemonObservation observation, string? attemptId,
            Func<MutationOutcome> onFullEvidence, CancellationToken ct) {
        var deadline = _time.GetUtcNow() + DetachedConfirmWindow;
        var lastLeg = UnreachableLeg;
        while (true) {
            // Marker-first: a refusing daemon never attaches, so checking the marker before evidence
            // can never produce a false Refused, while evidence-first could let a pre-existing
            // same-name daemon (or, here, a transient mid-boot evidence shape) mask a real refusal —
            // checked fresh every iteration, so a refusal arriving mid-window is caught on the next poll.
            if (attemptId is not null && BootRefusalMarker.TryAttribute(request.DaemonName, attemptId, request.CanonicalServer) is { } refusal)
                return new MutationOutcome.Refused(refusal.Token, ReasonRouting.ForBootRefusal(refusal.Token));

            var evidence = await observation.ObserveAsync(request, ct).ConfigureAwait(false);
            var leg = EvidenceFailureLeg(evidence, request);
            if (leg is null) return onFullEvidence();
            lastLeg = leg;

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return lastLeg == UnreachableLeg
                    ? new MutationOutcome.UnconfirmedNoAttach()
                    : new MutationOutcome.AttentionSkew(lastLeg);

            var wait = remaining < DetachedPollInterval ? remaining : DetachedPollInterval;
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    // The ONE evidence check both service-verb success and DetachedStart confirmation rely on —
    // returns the name of the first failing leg, or null once evidence is fully positive. Structural:
    // pid/instance presence is checked directly, never inferred from the observation's own
    // IdentityConsistent flag (which a broken/legacy adapter could report true without backing it).
    static string? EvidenceFailureLeg(ObservedEvidence? evidence, MutationRequest request) {
        if (evidence is not { Reachable: true }) return UnreachableLeg;

        if (!ServerIdentity.Matches(evidence.ServerUrl, request.CanonicalServer) || evidence.DaemonName != request.DaemonName)
            return "server_or_name_mismatch";

        if (evidence.Pid is null || evidence.InstanceId is null) return "pre_slice_evidence";

        if (!evidence.IdentityConsistent) return "identity_inconsistent";

        if (evidence.Capabilities is null || !evidence.Capabilities.Contains(ConsentCapability)) return "missing_capability_consent_3";

        if (!KcapCliCompatibility.Satisfies(evidence.DaemonVersion)) return "daemon_below_floor";

        return null;
    }

    static void LogWaiterlessSuccess(MutationRequest request, MutationOutcome outcome) =>
        Console.Error.WriteLine(
            $"DaemonMutationLane: waiterless {outcome.GetType().Name} verb={request.Verb} profile={request.Profile} " +
            $"server={request.CanonicalServer} daemon={request.DaemonName}");

    static void LogWaiterlessCancellation(MutationRequest request) =>
        Console.Error.WriteLine(
            $"DaemonMutationLane: waiterless cancellation (lane lifetime cancel — no envelope) verb={request.Verb} " +
            $"profile={request.Profile} server={request.CanonicalServer} daemon={request.DaemonName}");

    static void LogUnexpectedFault(MutationRequest request, Exception ex) =>
        Console.Error.WriteLine(
            $"DaemonMutationLane: unexpected {ex.GetType().Name} during a mutation attempt verb={request.Verb} " +
            $"profile={request.Profile} server={request.CanonicalServer} daemon={request.DaemonName}: {ex.Message}");

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
