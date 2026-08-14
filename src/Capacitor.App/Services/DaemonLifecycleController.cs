using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Mirrors Capacitor.Cli.Services.VerifyExit (source of truth:
/// src/Capacitor.Cli/Services/ServiceVerify.cs) plus DaemonCommands' detached-start digest gate. The
/// app never references the CLI project, so the coded exits are duplicated here deliberately — the
/// one shared home for every coded daemon-mutation exit, reused by DaemonMutationLane's classifier.
internal static class VerifyExitCodes {
    public const int Ok                  = 0;
    public const int Contended           = 20;
    public const int Viability           = 21;
    public const int BootoutUnknown      = 22;
    public const int StopUnconfirmed     = 23;
    public const int ReadinessTimeout    = 24;
    public const int HelloValidation     = 25;
    public const int RollbackBudget      = 26;
    public const int RestoreVerification = 27;
    public const int StartGate           = 28;
    public const int StartGateDrift      = 29;
    public const int DigestGate          = 43;

    public static string Token(int exitCode) => exitCode switch {
        Contended           => "verify_contended",
        Viability           => "verify_viability",
        BootoutUnknown      => "verify_bootout_unknown",
        StopUnconfirmed     => "verify_stop_unconfirmed",
        ReadinessTimeout    => "verify_readiness_timeout",
        HelloValidation     => "verify_hello_validation",
        RollbackBudget      => "verify_rollback_budget",
        RestoreVerification => "verify_restore_verification",
        StartGate           => "verify_start_gate",
        StartGateDrift      => "verify_start_gate_drift",
        _                   => $"verify_unknown_{exitCode}",
    };
}

/// The state machine of the lifecycle slice (spec §3.2/§4.2): reacts to IDaemonClientService's attach
/// stream, drives the §4.2 startup matrix through IKcapCli, and surfaces every inconsistency via
/// ILifecycleSurface — never a silent mutation outside the matrix's own explicit rows.
public sealed class DaemonLifecycleController : IAsyncDisposable {
    const string IncompatibleReason = "daemon_incompatible";

    /// Decision 3: every unit rewrite this controller offers — same-binary or not — carries this
    /// disclosure. Path equality is not installer provenance.
    internal const string TakeoverDisclosure =
        "This replaces the existing daemon service and re-captures its settings; a failed replacement leaves it uninstalled rather than restored.";

    internal static readonly TimeSpan ConfirmWindow         = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan TxnActiveRequeryDelay = TimeSpan.FromSeconds(2);

    readonly IDaemonClientService _client;
    readonly IKcapCli _cli;
    readonly ILoginShellProbe _probe;
    readonly IAppStateStore _store;
    readonly ILifecycleSurface _surface;
    readonly Func<Task<string?>> _resolveProfileName;
    readonly TimeProvider _time;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    readonly TaskCompletionSource<bool> _phaseClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock _lock = new();

    IDisposable? _subscription;
    IDisposable? _snapshotSubscription;
    bool _armClaimed;
    bool _disposed;
    int _generation;
    (AttachState State, string? Reason) _lastObserved = (AttachState.Connecting, null);
    TaskCompletionSource<bool>? _confirmWaiter;
    int _confirmSinceGen;
    string? _latestSnapshotVersion;
    bool _skewDialogShownThisRun;
    Task _versionCached = Task.CompletedTask;

    public DaemonLifecycleController(
            IDaemonClientService client, IKcapCli cli, ILoginShellProbe probe, IAppStateStore store,
            ILifecycleSurface surface, Func<Task<string?>> resolveProfileName, TimeProvider time) {
        _client             = client;
        _cli                = cli;
        _probe              = probe;
        _store              = store;
        _surface            = surface;
        _resolveProfileName = resolveProfileName;
        _time               = time;
    }

    /// Completes permanently on the first terminal attach outcome (Connected /
    /// daemon_incompatible / a completed daemon_unreachable startup branch). Consumed by later
    /// tasks (shim offer timing, skew dialogs never stacking with startup work).
    public Task PhaseClosed => _phaseClosed.Task;

    /// Cached once at Start(); null when the CLI is missing or --version failed. Consumed by the
    /// skew classification below.
    public string? CliVersion { get; private set; }

    /// Subscribes to the attach stream. MUST be called before the host calls
    /// IDaemonClientService.Start() — the host owns pumping the attach loop; a subscription that
    /// starts after the pump has begun could miss the first terminal outcome the startup phase
    /// hinges on.
    public void Start() {
        // DaemonClientService publishes a Connected snapshot to Snapshots BEFORE the matching
        // Connected AttachStatus (no-stale-pin) — subscribing here first means OnAttachStatus's
        // Connected case always reads an already-current _latestSnapshotVersion (spec §4.3).
        _snapshotSubscription = _client.Snapshots.Subscribe(OnSnapshot);
        _subscription         = _client.Status.Subscribe(OnAttachStatus);
        // Held, not fire-and-forget: RunSkewCheckAsync awaits this — VersionAsync is a process
        // spawn (tens of ms), the attach cycle a sub-ms local-socket dial, so the daemon path can
        // plausibly win the race and reach the skew check before CliVersion is cached.
        _versionCached = CacheVersionAsync();
    }

    void OnSnapshot(DaemonStatusDto snapshot) {
        lock (_lock) _latestSnapshotVersion = snapshot.Daemon.Version;
    }

    async Task CacheVersionAsync() {
        try {
            CliVersion = await _cli.VersionAsync(_lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // shutdown before the version probe returned — nothing to cache
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle version probe failed unexpectedly: {ex.Message}");
        }
    }

    // Every AttachStatus transition bumps the generation, records the last-observed outcome, and
    // may signal an armed confirm waiter — this ALWAYS happens, regardless of the once-per-run
    // arm below. Only a TERMINAL outcome (never Connecting) can claim that arm, and only the
    // FIRST one ever does — but the switch itself still runs for every later event: the arm gates
    // startup AUTO-ACTION eligibility only, never event admission, so a later daemon_incompatible
    // still reaches its skew/takeover case below unconditionally.
    void OnAttachStatus(AttachStatus status) {
        TaskCompletionSource<bool>? toSignal = null;
        bool isFirstTerminalOutcome;

        lock (_lock) {
            _generation++;
            var gen = _generation;
            _lastObserved = (status.State, status.Reason);

            if (status.State == AttachState.Connected && _confirmWaiter is not null && gen > _confirmSinceGen) {
                toSignal       = _confirmWaiter;
                _confirmWaiter = null;
            }

            isFirstTerminalOutcome = status.State != AttachState.Connecting && !_armClaimed;
            if (isFirstTerminalOutcome) _armClaimed = true;
        }

        toSignal?.TrySetResult(true);
        if (status.State == AttachState.Connecting) return;

        switch (status.State) {
            case AttachState.Connected:
                if (isFirstTerminalOutcome) {
                    ClosePhase();
                    _ = RunReconciliationAsync(attached: true);
                }
                // §4.3: every Connected, not just the first — the once-per-run dialog flag (not
                // this arm) is what keeps a later Connected from stacking a second offer.
                _ = RunSkewCheckAsync(LatestSnapshotVersion());
                break;
            case AttachState.Unreachable when status.Reason == IncompatibleReason:
                if (isFirstTerminalOutcome) {
                    ClosePhase();
                    _ = RunReconciliationAsync(attached: false);
                }
                _ = RunSkewCheckAsync(status.DaemonVersion); // every incompatible event, same reason as above
                break;
            case AttachState.Unreachable:
                if (isFirstTerminalOutcome) _ = RunStartupBranchAsync((status.State, status.Reason));
                break;
        }
    }

    void ClosePhase() => _phaseClosed.TrySetResult(true);

    int CurrentGeneration() { lock (_lock) return _generation; }

    bool ObservedStatusChangedSince((AttachState State, string? Reason) triggering) {
        lock (_lock) return _lastObserved != triggering;
    }

    /// The freshest known attach state, read right before a Reconcile call rather than threaded
    /// through as a captured parameter across an async gap — a captured value can go stale (e.g.
    /// a racing Connected arriving mid-query) and silently disable the attached-only checks for
    /// the run's only reconciliation pass.
    bool IsCurrentlyAttached() { lock (_lock) return _lastObserved.State == AttachState.Connected; }

    string? LatestSnapshotVersion() { lock (_lock) return _latestSnapshotVersion; }

    TaskCompletionSource<bool> ArmConfirmWaiter() {
        lock (_lock) {
            _confirmSinceGen = _generation;
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _confirmWaiter = waiter;
            return waiter;
        }
    }

    void DisarmConfirmWaiter(TaskCompletionSource<bool> waiter) {
        lock (_lock) {
            if (ReferenceEquals(_confirmWaiter, waiter)) _confirmWaiter = null;
        }
    }

    async Task<bool> TryAcquireGateAsync(CancellationToken ct) {
        try {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        } catch (OperationCanceledException) {
            return false;
        }
    }

    enum QueryOutcome { Ok, Failed, Stale }

    async Task<(ServiceSnapshot? Snap, QueryOutcome Outcome)> QueryStatusAsync(CancellationToken ct) {
        var gen0 = CurrentGeneration();
        var snap = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false);
        if (CurrentGeneration() != gen0) return (null, QueryOutcome.Stale);
        return snap is null ? (null, QueryOutcome.Failed) : (snap, QueryOutcome.Ok);
    }

    /// General-purpose status query for reconciliation/requery/Start-action callers: one retry on
    /// stale evidence (never silently walks away with zero query), and a genuine CLI-level
    /// failure — on either attempt — surfaces an honest line and is logged (spec §6 "unknown").
    async Task<ServiceSnapshot?> QueryStatusForActionAsync(CancellationToken ct) {
        var (snap, outcome) = await QueryStatusAsync(ct).ConfigureAwait(false);
        if (outcome == QueryOutcome.Stale) (snap, outcome) = await QueryStatusAsync(ct).ConfigureAwait(false);

        if (outcome != QueryOutcome.Ok) {
            _surface.Status("Could not read the daemon service status — skipping automatic action this run.");
            Console.Error.WriteLine("kcap: daemon service status query failed, was unparseable, or never settled");
            return null;
        }

        return snap;
    }

    /// Startup-branch-specific query: retries once ONLY when the evidence is genuinely stale — a
    /// MEANINGFULLY different attach outcome (e.g. a racing Connected) arrived mid-query — never
    /// for a merely duplicate re-observation of the same outcome that triggered this branch. That
    /// distinction is what lets a duplicate daemon_unreachable stay a true no-op (the
    /// once-per-run arm) while a genuine race still gets re-evaluated instead of the branch
    /// silently walking away with zero reconciliation for the whole run.
    async Task<ServiceSnapshot?> QueryForStartupBranchAsync((AttachState State, string? Reason) triggering, CancellationToken ct) {
        var gen0 = CurrentGeneration();
        var snap = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false);

        if (CurrentGeneration() != gen0 && ObservedStatusChangedSince(triggering))
            snap = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false); // one re-evaluation against fresh state

        if (snap is null) {
            _surface.Status("Could not read the daemon service status — skipping automatic action this run.");
            Console.Error.WriteLine("kcap: daemon service status query failed or was unparseable during startup");
        }

        return snap;
    }

    async Task RunReconciliationAsync(bool attached) {
        if (_cli.CliPath is null) return;
        if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;

        try {
            var snap = await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false);
            if (snap is not null) Reconcile(snap, attached, allowTxnActiveRequery: true);
        } catch (OperationCanceledException) {
            // shutdown mid-query
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle reconciliation failed unexpectedly: {ex.Message}");
        } finally {
            _gate.Release();
        }
    }

    /// §4.2: the startup branch. Runs at most once per app run (the arm claimed synchronously in
    /// OnAttachStatus); closes the startup phase only once this full flow — query, optional
    /// txn-active wait, matrix decision, any mutation, and its confirmation wait — has completed.
    async Task RunStartupBranchAsync((AttachState State, string? Reason) triggering) {
        try {
            if (_cli.CliPath is null) {
                _surface.Status("kcap CLI not found — daemon lifecycle management is off for this run.");
                return;
            }

            if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
            try {
                var snap = await QueryForStartupBranchAsync(triggering, _lifetime.Token).ConfigureAwait(false);
                if (snap is null) return; // query failure/unknown — already surfaced above, no mutation (spec §6)

                // The true CURRENT attach state, not the (possibly stale) reason this branch was
                // triggered by: when QueryForStartupBranchAsync had to re-evaluate against a
                // racing Connected, this run's only reconciliation pass must not silently run in
                // permanently-unattached mode — that would make the attached-only checks
                // (ownership mismatch, coexistence) unreachable for the whole run.
                Reconcile(snap, IsCurrentlyAttached(), allowTxnActiveRequery: false);

                if (snap.TxnActive) {
                    // spec §6: a held flock is waited out, never mutated into. One bounded
                    // re-query (not offered as repair); still active afterward → no action this
                    // run rather than risk contending the CLI's own transaction lock.
                    snap = await AwaitOneTxnActiveRequeryAsync(_lifetime.Token).ConfigureAwait(false);
                    if (snap is null || snap.TxnActive) return;
                }

                await RunStartupMatrixAsync(snap, _lifetime.Token).ConfigureAwait(false);
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // shutdown mid-branch
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle startup branch failed unexpectedly: {ex.Message}");
        } finally {
            ClosePhase();
        }
    }

    /// §4.2 table, keyed on the loaded-label/job state before plist presence. An unrecognized wire
    /// state is Unknown, not NotInstalled — positive evidence only, never a silent entry into the
    /// auto-install/start path below.
    async Task RunStartupMatrixAsync(ServiceSnapshot snap, CancellationToken ct) {
        var state = ServiceStateClassifier.Parse(snap.State);
        if (state == ServiceState.Unknown) {
            _surface.Status("Daemon service reported an unrecognized state — skipping automatic action this run.");
            Console.Error.WriteLine($"kcap: daemon lifecycle startup matrix saw an unrecognized service state: {snap.State}");
            return;
        }

        if (state == ServiceState.Running) return; // launchd's own backoff keeps retrying

        if (state == ServiceState.Installed) { // loaded, inactive
            if (!snap.UnitPresent) {
                _surface.Attention("The daemon service label is loaded but its unit file is missing — needs repair.");
                return;
            }
            if (snap.DaemonPid is not null) {
                AttentionCoexistence(snap.DaemonPid.Value);
                return;
            }
            await RunVerifiedMutationAsync(_cli.ServiceStartVerifiedAsync, ct).ConfigureAwait(false);
            return;
        }

        // state == ServiceState.NotInstalled: no loaded label.
        if (snap.UnitPresent) {
            if (snap.DaemonPid is not null) {
                AttentionCoexistence(snap.DaemonPid.Value);
                return;
            }
            await RunVerifiedMutationAsync(_cli.ServiceStartVerifiedAsync, ct).ConfigureAwait(false);
            return;
        }

        var failure = await FailingPreconditionAsync(snap, ct).ConfigureAwait(false);
        if (failure is not null) {
            _surface.Status(failure);
            return;
        }

        // No DaemonPid check here, unlike the start rows above: a racing/wedged manual daemon on
        // this name is the install --verify transaction's own job to detect and safely roll back
        // from (post-install ownership + hello verification, spec §3.4; E2E item 2) — not a
        // pre-flight guess by the app.
        await RunVerifiedMutationAsync(c => _cli.ServiceInstallVerifiedAsync(replace: false, c), ct).ConfigureAwait(false);
    }

    void AttentionCoexistence(int daemonPid) =>
        _surface.Attention(
            $"A daemon is already running (pid {daemonPid}) alongside the installed service — not starting a second one.");

    /// §4.1 preconditions, install-only — start performs no viability check (spec §3.4), so this
    /// is never called on a start row. Returns the honest line to surface on failure, or null
    /// once every precondition passes.
    async Task<string?> FailingPreconditionAsync(ServiceSnapshot snap, CancellationToken ct) {
        if (snap.InstallBinaryPath is null)
            return "kcap can't resolve its own daemon binary — skipping automatic install.";

        var profile = await _resolveProfileName().ConfigureAwait(false);
        if (profile is null)
            return "No profile with a valid server URL is configured — skipping automatic install.";

        var terminalPath = await _probe.TerminalPathAsync(ct).ConfigureAwait(false);
        if (terminalPath is null)
            return "Terminal PATH could not be determined — skipping automatic install.";

        return null;
    }

    /// One CLI mutation call, confirmed by a FRESH Connected (spec §4.2) observed strictly after
    /// this call began — the confirm waiter is armed BEFORE `mutate` runs so a Connected racing
    /// the CLI call still counts. No rollback call exists anywhere in IKcapCli: the CLI
    /// transaction itself already rolled back internally on a coded failure. Returns whether the
    /// CLI call itself exited Ok (a coded failure is fully surfaced already; callers that offered
    /// this mutation via a dialog use the return value to decide whether the user got a
    /// resolution or needs to be able to re-offer).
    async Task<bool> RunVerifiedMutationAsync(Func<CancellationToken, Task<ProcessResult>> mutate, CancellationToken ct) {
        var waiter = ArmConfirmWaiter();
        try {
            var result = await mutate(ct).ConfigureAwait(false);

            // §3.6: a forced kill after the CLI transaction's own bound is exceeded (pathological)
            // — the killed tree's exit code is not a verify outcome and must never be read as one.
            // Re-query fresh evidence and surface Attention; never auto-mutate off a kill the app
            // itself had to force.
            if (result.TimedOut) {
                await SurfaceTimeoutAttentionAsync(ct).ConfigureAwait(false);
                return false;
            }

            _ = _client.RestartLoopAsync(); // kick reattach after any attempted action, success or coded failure

            if (result.ExitCode == VerifyExitCodes.Ok) {
                using var confirmDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(ConfirmWindow, _time, confirmDeadline.Token);
                var won = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
                if (won == waiter.Task)
                    confirmDeadline.Cancel(); // fresh Connected already arrived — release the timer early
                else if (!ct.IsCancellationRequested)
                    _surface.Status("daemon started, app not yet attached — retrying");
                return true;
            }

            _surface.Status($"{VerifyExitCodes.Token(result.ExitCode)}: {result.Stderr.Trim()}");
            return false;
        } finally {
            DisarmConfirmWaiter(waiter);
        }
    }

    async Task SurfaceTimeoutAttentionAsync(CancellationToken ct) {
        var snap = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false);
        _surface.Attention(snap is null
            ? "The daemon service operation timed out and its outcome could not be re-checked — check state."
            : $"The daemon service operation timed out — check state (txn_marker={snap.TxnMarker}, txn_active={snap.TxnActive}).");
    }

    /// §4.3: runs on every Connected (paired with the latest known snapshot version) and every
    /// daemon_incompatible (paired with the hello DaemonVersion, spec decision 6) — never on a
    /// plain daemon_unreachable, which doesn't call this at all. Equal/null versions, a null
    /// cached CliVersion (§6: the version probe failed), an already-declined pair, and the
    /// once-per-run dialog flag are all silent no-ops.
    async Task RunSkewCheckAsync(string? daemonVersion) {
        if (daemonVersion is null) return; // nothing to compare regardless of the cached CLI version
        if (_cli.CliPath is null) return;

        lock (_lock) {
            if (_skewDialogShownThisRun) return;
        }

        // The attach cycle is a sub-ms local-socket dial; VersionAsync is a process spawn (tens
        // of ms) — the first Connected/daemon_incompatible of a run can plausibly arrive before
        // CliVersion is cached. CacheVersionAsync never faults, so this never throws.
        await _versionCached.ConfigureAwait(false);
        if (CliVersion is null || daemonVersion == CliVersion) return;

        var pairKey = $"{daemonVersion}|{CliVersion}";
        var state   = await _store.LoadAsync().ConfigureAwait(false);
        if (state.DeclinedTakeoverPairs?.Contains(pairKey) == true) return;

        if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
        try {
            lock (_lock) {
                if (_skewDialogShownThisRun) return; // a concurrent trigger already claimed this run's one dialog
            }

            var snap = await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false);
            if (snap is null) return; // already surfaced by QueryStatusForActionAsync

            var missing = await FailingSkewPreconditionAsync(snap, _lifetime.Token).ConfigureAwait(false);
            if (missing is not null) {
                _surface.Status(missing);
                return;
            }

            var kind         = ClassifyTakeover(snap);
            var terminalPath = await _probe.TerminalPathAsync(_lifetime.Token).ConfigureAwait(false);
            var prompt       = new LifecyclePrompt(kind, daemonVersion, CliVersion, terminalPath is null, TakeoverDisclosure);

            // §3.5 claim-before-show: persisted before ConfirmAsync so a crash while the dialog is
            // open still suppresses a re-offer of this exact pair on the next run.
            await PersistDeclineAsync(pairKey).ConfigureAwait(false);
            lock (_lock) _skewDialogShownThisRun = true;

            // The retract runs BEFORE the mutation (acceptance itself invalidates the decline
            // claim), not after: RunVerifiedMutationAsync doesn't catch around the CLI call, so an
            // install that throws (shutdown mid-spawn, a process/IO fault) would skip a
            // post-mutation retract entirely and leave an accepted pair mislabeled "declined" on
            // disk. "Declined" must mean the user declined, full stop.
            var (outcome, succeeded) = await ConfirmAndTakeoverAsync(
                prompt, revalidate: fresh => ClassifyTakeover(fresh) == kind,
                onAcceptedNotStale: () => RetractDeclineAsync(pairKey), _lifetime.Token).ConfigureAwait(false);

            switch (outcome) {
                case ConfirmOutcome.Declined:
                    return; // the claim persisted above IS the decline memory
                case ConfirmOutcome.Stale:
                    // Never actually declined — retract the claim and let the next trigger re-offer.
                    await RetractDeclineAsync(pairKey).ConfigureAwait(false);
                    lock (_lock) _skewDialogShownThisRun = false;
                    return;
                case ConfirmOutcome.Attempted:
                    if (!succeeded) lock (_lock) _skewDialogShownThisRun = false; // no resolution — re-offer
                    return;
            }
        } catch (OperationCanceledException) {
            // shutdown mid-check
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle skew check failed unexpectedly: {ex.Message}");
        } finally {
            _gate.Release();
        }
    }

    enum ConfirmOutcome { Declined, Stale, Attempted }

    /// The ONE accept path every unit rewrite in this controller goes through — skew's takeover
    /// (§4.3) and Start's repair affordance (§4.4) both funnel here, so a future kind can never
    /// grow a second `ServiceInstallVerifiedAsync(replace: true)` call site. `onAcceptedNotStale`,
    /// when given, runs after acceptance is confirmed fresh but BEFORE the mutation itself (skew's
    /// decline-claim retract — see its own comment above).
    ///
    /// `revalidate`, when given, re-queries a FRESH status right before mutating and rejects a
    /// classification that no longer matches what the dialog disclosed (spec §3.2: "evidence is
    /// revalidated inside the gate immediately before mutating"). The generation-token check above
    /// only catches changes that produced an attach event — a terminal replacing the plist/unit
    /// directly (no attach event, generation unchanged) needs this second, disk-fresh check. Null
    /// for the repair affordance, which discloses no classification to go stale.
    async Task<(ConfirmOutcome Outcome, bool MutationSucceeded)> ConfirmAndTakeoverAsync(
            LifecyclePrompt prompt, Func<ServiceSnapshot, bool>? revalidate, Func<Task>? onAcceptedNotStale,
            CancellationToken ct) {
        var gen0     = CurrentGeneration(); // captured immediately before ConfirmAsync (stale-consent check below)
        var accepted = await _surface.ConfirmAsync(prompt, ct).ConfigureAwait(false);
        if (!accepted) return (ConfirmOutcome.Declined, false);

        if (CurrentGeneration() != gen0) {
            _surface.Status("The daemon changed while the prompt was open — canceled, nothing changed.");
            return (ConfirmOutcome.Stale, false);
        }

        if (revalidate is not null) {
            var fresh = await QueryStatusForActionAsync(ct).ConfigureAwait(false);
            if (fresh is null) return (ConfirmOutcome.Stale, false); // already surfaced by QueryStatusForActionAsync
            if (!revalidate(fresh)) {
                _surface.Status("The daemon service changed while the prompt was open — canceled, nothing changed.");
                return (ConfirmOutcome.Stale, false);
            }
        }

        if (onAcceptedNotStale is not null) await onAcceptedNotStale().ConfigureAwait(false);

        var succeeded = await RunVerifiedMutationAsync(c => _cli.ServiceInstallVerifiedAsync(replace: true, c), ct)
            .ConfigureAwait(false);
        return (ConfirmOutcome.Attempted, succeeded);
    }

    Task PersistDeclineAsync(string pairKey) =>
        _store.UpdateAsync(s => s.DeclinedTakeoverPairs?.Contains(pairKey) == true
            ? s
            : s with { DeclinedTakeoverPairs = [.. s.DeclinedTakeoverPairs ?? [], pairKey] });

    Task RetractDeclineAsync(string pairKey) =>
        _store.UpdateAsync(s => s.DeclinedTakeoverPairs is null || !s.DeclinedTakeoverPairs.Contains(pairKey)
            ? s
            : s with { DeclinedTakeoverPairs = s.DeclinedTakeoverPairs.Where(p => p != pairKey).ToList() });

    /// §4.1 preconditions the skew/takeover DIALOG itself needs — unlike FailingPreconditionAsync
    /// (the silent-install row), an unknown terminal PATH is NOT one of them: decision 7 lets a
    /// DIALOGED install proceed on disclosure (the prompt's PathDegraded) rather than block. A
    /// missing install binary or an unresolvable profile, though, would make accept a guaranteed
    /// coded viability failure — those still gate the offer itself.
    async Task<string?> FailingSkewPreconditionAsync(ServiceSnapshot snap, CancellationToken ct) {
        if (snap.InstallBinaryPath is null)
            return "kcap can't resolve its own daemon binary — skipping the takeover offer.";

        var profile = await _resolveProfileName().ConfigureAwait(false);
        if (profile is null)
            return "No profile with a valid server URL is configured — skipping the takeover offer.";

        return null;
    }

    /// Classification (spec §4.3): unit_present && canonical binary_path == canonical
    /// install_binary_path is the ONLY same-binary case — path equality is not installer
    /// provenance, so both kinds carry TakeoverDisclosure. A blank/whitespace path (e.g. a
    /// foreign/hand-edited plist) is never treated as a match.
    static string ClassifyTakeover(ServiceSnapshot snap) =>
        snap.UnitPresent && !string.IsNullOrWhiteSpace(snap.BinaryPath) && !string.IsNullOrWhiteSpace(snap.InstallBinaryPath)
            && CanonicalPath(snap.BinaryPath) == CanonicalPath(snap.InstallBinaryPath)
            ? LifecyclePrompt.KindRestartUpdate
            : LifecyclePrompt.KindTakeover;

    static string CanonicalPath(string path) {
        try {
            var fullPath = Path.GetFullPath(path);
            return new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        } catch {
            return path; // empty/invalid path, missing file, permission error, etc. — raw string compare
        }
    }

    /// Reconciliation (spec §3.2): surfaces every inconsistent combination found in one
    /// ServiceStatusAsync snapshot — never mutates. The attached-only checks only make sense (or
    /// would otherwise double-report a startup-matrix row's own Attention for the exact same
    /// evidence) while genuinely connected.
    void Reconcile(ServiceSnapshot snap, bool attached, bool allowTxnActiveRequery) {
        var state = ServiceStateClassifier.Parse(snap.State);

        if (attached) {
            if (snap.JobPid is not null && snap.DaemonPid is not null && snap.JobPid != snap.DaemonPid)
                _surface.Attention(
                    $"The daemon service job (pid {snap.JobPid}) does not match the attached daemon (pid {snap.DaemonPid}).");
            else if (state == ServiceState.Running && snap.DaemonPid is null)
                _surface.Attention("The service reports its job running, but no attached-daemon evidence backs it.");
        }

        if (snap.TxnMarker && !snap.TxnActive)
            _surface.Attention("A previous daemon service operation left a stale transaction marker — repair may be needed.");

        if (snap.TxnActive && allowTxnActiveRequery)
            _ = RunTxnActiveRequeryAsync();

        if (attached && snap.UnitPresent && state == ServiceState.NotInstalled && snap.DaemonPid is not null)
            _surface.Attention(
                $"A daemon is running outside the installed service — the service is stopped while a manual daemon (pid {snap.DaemonPid}) owns the name.");
    }

    // The inline, AWAITED building block: used by RunStartupBranchAsync, which already holds the
    // gate for its whole decision, to both wait out the delay AND gate the matrix's eligibility
    // on the result — no mutation while the flock is (or was very recently) held. `attached` is
    // read fresh (IsCurrentlyAttached) right before reconciling, not captured beforehand — the
    // delay is itself a window a race can land in.
    async Task<ServiceSnapshot?> AwaitOneTxnActiveRequeryAsync(CancellationToken ct) {
        await Task.Delay(TxnActiveRequeryDelay, _time, ct).ConfigureAwait(false);
        var fresh = await QueryStatusForActionAsync(ct).ConfigureAwait(false);
        if (fresh is not null) Reconcile(fresh, IsCurrentlyAttached(), allowTxnActiveRequery: false);
        return fresh;
    }

    // The fire-and-forget building block: scheduled from Reconcile's own txn-active branch
    // (Connected/incompatible reconciliation-only paths, which don't otherwise hold the gate for
    // this). The delay itself runs UNGATED — a passive background check must never block a user's
    // Start click for the whole wait — only the query+reconcile is gate-scoped, exactly like
    // every other mutation-adjacent evidence read (spec §3.2). Exactly one follow-up query
    // (allowTxnActiveRequery: false on the way back in) — an orphaned grandchild that outlives a
    // force-quit is waited out, not repaired (spec §6). Same freshest-read rule as above:
    // `attached` comes from IsCurrentlyAttached() at reconcile time, never a captured parameter.
    async Task RunTxnActiveRequeryAsync() {
        try {
            await Task.Delay(TxnActiveRequeryDelay, _time, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
        try {
            var fresh = await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false);
            if (fresh is not null) Reconcile(fresh, IsCurrentlyAttached(), allowTxnActiveRequery: false);
        } catch (OperationCanceledException) {
            // shutdown mid-requery
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle txn-active requery failed unexpectedly: {ex.Message}");
        } finally {
            _gate.Release();
        }
    }

    /// §4.4: the Start action. Branches on the loaded-label/job state BEFORE plist presence, same
    /// precedence as the startup matrix — but unlike that matrix, a Start click is NEVER itself
    /// consent to rewrite a unit: a mismatched/orphaned/coexisting
    /// unit always goes through the dialoged repair affordance (OfferRepairAsync) instead of a
    /// silent Attention. Public + caller-awaited from the UI, so every path here is exception-safe
    /// — a click must never crash the app.
    public async Task StartActionAsync(CancellationToken ct) {
        try {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var lct = linked.Token;

            if (_cli.CliPath is null) {
                _surface.Status("kcap CLI not found — can't start the daemon service.");
                return;
            }

            if (!await TryAcquireGateAsync(lct).ConfigureAwait(false)) return;
            try {
                // Fresh evidence every call — a Start racing an in-flight mutation blocks on the
                // gate above and, once it clears, re-queries rather than acting on anything it
                // might have observed before the wait.
                var snap = await QueryStatusForActionAsync(lct).ConfigureAwait(false);
                if (snap is null) return; // unknown — already surfaced, no action (spec §6)

                var state = ServiceStateClassifier.Parse(snap.State);
                if (state == ServiceState.Unknown) {
                    _surface.Status("Daemon service reported an unrecognized state — skipping this action.");
                    Console.Error.WriteLine($"kcap: daemon lifecycle start action saw an unrecognized service state: {snap.State}");
                    return;
                }

                if (state == ServiceState.Running) {
                    _ = _client.RestartLoopAsync();
                    return;
                }

                if (state == ServiceState.Installed) {
                    if (snap.UnitPresent && snap.DaemonPid is null)
                        await RunVerifiedMutationAsync(_cli.ServiceStartVerifiedAsync, lct).ConfigureAwait(false);
                    else
                        await OfferRepairAsync(snap, lct).ConfigureAwait(false); // orphan label or coexistence
                    return;
                }

                // state == ServiceState.NotInstalled: no loaded label.
                if (snap.UnitPresent) {
                    if (snap.DaemonPid is null)
                        await RunVerifiedMutationAsync(_cli.ServiceStartVerifiedAsync, lct).ConfigureAwait(false); // bootstrap the stopped unit
                    else
                        await OfferRepairAsync(snap, lct).ConfigureAwait(false); // a manual daemon owns the name
                    return;
                }

                await _cli.DetachedStartAsync(lct).ConfigureAwait(false); // nothing at all — today's detached start, no unit to rewrite
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // caller-cancelled or shutting down — nothing to surface
        } catch (Exception ex) {
            _surface.Status("Starting the daemon failed unexpectedly.");
            Console.Error.WriteLine($"kcap: daemon lifecycle start action failed unexpectedly: {ex.Message}");
        }
    }

    /// §4.4 repair affordance: the SAME dialoged operation as skew's takeover
    /// (ConfirmAndTakeoverAsync), entered from Start instead of a version mismatch — a plist/pid
    /// combination Start refuses to silently mutate into. No daemon/CLI version pair applies here,
    /// so the prompt carries neither.
    async Task OfferRepairAsync(ServiceSnapshot snap, CancellationToken ct) {
        var missing = await FailingSkewPreconditionAsync(snap, ct).ConfigureAwait(false);
        if (missing is not null) {
            _surface.Status(missing);
            return;
        }

        var terminalPath = await _probe.TerminalPathAsync(ct).ConfigureAwait(false);
        var prompt = new LifecyclePrompt(LifecyclePrompt.KindRepair, null, null, terminalPath is null, TakeoverDisclosure);

        await ConfirmAndTakeoverAsync(prompt, revalidate: null, onAcceptedNotStale: null, ct).ConfigureAwait(false);
    }

    /// App shutdown awaits this: completes once no mutation child is in flight. Does not itself
    /// block new mutations from starting — callers stop feeding triggers (DisposeAsync
    /// unsubscribes first) before relying on this as a barrier.
    public async Task QuiescedAsync() {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;

        _subscription?.Dispose();
        _snapshotSubscription?.Dispose();
        _lifetime.Cancel();
        await QuiescedAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
