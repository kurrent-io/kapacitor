using System.Reactive.Linq;

namespace Capacitor.App.Services;

/// Mirrors Capacitor.Cli.Services.VerifyExit (source of truth:
/// src/Capacitor.Cli/Services/ServiceVerify.cs). The app never references the CLI project, so
/// the coded exits are duplicated here deliberately.
static class VerifyExitCodes {
    public const int Ok                  = 0;
    public const int Contended           = 20;
    public const int Viability           = 21;
    public const int BootoutUnknown      = 22;
    public const int StopUnconfirmed     = 23;
    public const int ReadinessTimeout    = 24;
    public const int HelloValidation     = 25;
    public const int RollbackBudget      = 26;
    public const int RestoreVerification = 27;

    public static string Token(int exitCode) => exitCode switch {
        Contended           => "verify_contended",
        Viability           => "verify_viability",
        BootoutUnknown      => "verify_bootout_unknown",
        StopUnconfirmed     => "verify_stop_unconfirmed",
        ReadinessTimeout    => "verify_readiness_timeout",
        HelloValidation     => "verify_hello_validation",
        RollbackBudget      => "verify_rollback_budget",
        RestoreVerification => "verify_restore_verification",
        _                   => $"verify_unknown_{exitCode}",
    };
}

/// The state machine of AI-1654 (spec §3.2/§4.2): reacts to IDaemonClientService's attach
/// stream, drives the §4.2 startup matrix through IKcapCli, and surfaces every inconsistency via
/// ILifecycleSurface — never a silent mutation outside the matrix's own explicit rows.
public sealed class DaemonLifecycleController : IAsyncDisposable {
    const string IncompatibleReason = "daemon_incompatible";
    const string StateRunning       = "running";
    const string StateInstalled     = "installed";
    const string StateNotInstalled  = "not_installed";

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
    bool _armClaimed;
    bool _disposed;
    int _generation;
    (AttachState State, string? Reason) _lastObserved = (AttachState.Connecting, null);
    TaskCompletionSource<bool>? _confirmWaiter;
    int _confirmSinceGen;

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

    /// Cached once at Start(); null when the CLI is missing or --version failed. Consumed by
    /// Task 20's skew classification.
    public string? CliVersion { get; private set; }

    /// Subscribes to the attach stream. MUST be called before the host calls
    /// IDaemonClientService.Start() — the host owns pumping the attach loop; a subscription that
    /// starts after the pump has begun could miss the first terminal outcome the startup phase
    /// hinges on.
    public void Start() {
        _subscription = _client.Status.Subscribe(OnAttachStatus);
        _ = CacheVersionAsync();
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
    // (say) still reaches its case — a future skew/takeover hook (Task 20) can act on it there
    // unconditionally without this dispatcher changing shape.
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
                break;
            case AttachState.Unreachable when status.Reason == IncompatibleReason:
                if (isFirstTerminalOutcome) {
                    ClosePhase();
                    _ = RunReconciliationAsync(attached: false); // Task 20 adds the skew/takeover offer here too
                }
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

                Reconcile(snap, attached: false, allowTxnActiveRequery: false);

                if (snap.TxnActive) {
                    // spec §6: a held flock is waited out, never mutated into. One bounded
                    // re-query (not offered as repair); still active afterward → no action this
                    // run rather than risk contending the CLI's own transaction lock.
                    snap = await AwaitOneTxnActiveRequeryAsync(attached: false, _lifetime.Token).ConfigureAwait(false);
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

    /// §4.2 table, keyed on the loaded-label/job state before plist presence.
    async Task RunStartupMatrixAsync(ServiceSnapshot snap, CancellationToken ct) {
        if (snap.State == StateRunning) return; // launchd's own backoff keeps retrying

        if (snap.State == StateInstalled) { // loaded, inactive
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

        // snap.State == StateNotInstalled: no loaded label.
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
    /// transaction itself already rolled back internally on a coded failure.
    async Task RunVerifiedMutationAsync(Func<CancellationToken, Task<ProcessResult>> mutate, CancellationToken ct) {
        var waiter = ArmConfirmWaiter();
        try {
            var result = await mutate(ct).ConfigureAwait(false);
            _ = _client.RestartLoopAsync(); // kick reattach after any attempted action, success or coded failure

            if (result.ExitCode == VerifyExitCodes.Ok) {
                using var confirmDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(ConfirmWindow, _time, confirmDeadline.Token);
                var won = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
                if (won == waiter.Task)
                    confirmDeadline.Cancel(); // fresh Connected already arrived — release the timer early
                else if (!ct.IsCancellationRequested)
                    _surface.Status("daemon started, app not yet attached — retrying");
            } else {
                _surface.Status($"{VerifyExitCodes.Token(result.ExitCode)}: {result.Stderr.Trim()}");
            }
        } finally {
            DisarmConfirmWaiter(waiter);
        }
    }

    /// Reconciliation (spec §3.2): surfaces every inconsistent combination found in one
    /// ServiceStatusAsync snapshot — never mutates. The attached-only checks only make sense (or
    /// would otherwise double-report a startup-matrix row's own Attention for the exact same
    /// evidence) while genuinely connected.
    void Reconcile(ServiceSnapshot snap, bool attached, bool allowTxnActiveRequery) {
        if (attached) {
            if (snap.JobPid is not null && snap.DaemonPid is not null && snap.JobPid != snap.DaemonPid)
                _surface.Attention(
                    $"The daemon service job (pid {snap.JobPid}) does not match the attached daemon (pid {snap.DaemonPid}).");
            else if (snap.State == StateRunning && snap.DaemonPid is null)
                _surface.Attention("The service reports its job running, but no attached-daemon evidence backs it.");
        }

        if (snap.TxnMarker && !snap.TxnActive)
            _surface.Attention("A previous daemon service operation left a stale transaction marker — repair may be needed.");

        if (snap.TxnActive && allowTxnActiveRequery)
            _ = RunTxnActiveRequeryAsync(attached);

        if (attached && snap.UnitPresent && snap.State == StateNotInstalled && snap.DaemonPid is not null)
            _surface.Attention(
                $"A daemon is running outside the installed service — the service is stopped while a manual daemon (pid {snap.DaemonPid}) owns the name.");
    }

    // The inline, AWAITED building block: used by RunStartupBranchAsync, which already holds the
    // gate for its whole decision, to both wait out the delay AND gate the matrix's eligibility
    // on the result — no mutation while the flock is (or was very recently) held.
    async Task<ServiceSnapshot?> AwaitOneTxnActiveRequeryAsync(bool attached, CancellationToken ct) {
        await Task.Delay(TxnActiveRequeryDelay, _time, ct).ConfigureAwait(false);
        var fresh = await QueryStatusForActionAsync(ct).ConfigureAwait(false);
        if (fresh is not null) Reconcile(fresh, attached, allowTxnActiveRequery: false);
        return fresh;
    }

    // The fire-and-forget building block: scheduled from Reconcile's own txn-active branch
    // (Connected/incompatible reconciliation-only paths, which don't otherwise hold the gate for
    // this). The delay itself runs UNGATED — a passive background check must never block a user's
    // Start click for the whole wait — only the query+reconcile is gate-scoped, exactly like
    // every other mutation-adjacent evidence read (spec §3.2). Exactly one follow-up query
    // (allowTxnActiveRequery: false on the way back in) — an orphaned grandchild that outlives a
    // force-quit is waited out, not repaired (spec §6).
    async Task RunTxnActiveRequeryAsync(bool attached) {
        try {
            await Task.Delay(TxnActiveRequeryDelay, _time, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
        try {
            var fresh = await QueryStatusForActionAsync(_lifetime.Token).ConfigureAwait(false);
            if (fresh is not null) Reconcile(fresh, attached, allowTxnActiveRequery: false);
        } catch (OperationCanceledException) {
            // shutdown mid-requery
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: daemon lifecycle txn-active requery failed unexpectedly: {ex.Message}");
        } finally {
            _gate.Release();
        }
    }

    /// §4.4: the tray Start action (Task 21 wires the trigger). Never consent to rewrite a unit —
    /// every branch either starts an existing unit, kicks a reattach, or falls back to today's
    /// detached `daemon start -d`; only the startup branch above ever installs.
    public async Task StartActionAsync(CancellationToken ct) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var lct = linked.Token;

        if (_cli.CliPath is null) {
            _surface.Status("kcap CLI not found — can't start the daemon service.");
            return;
        }

        if (!await TryAcquireGateAsync(lct).ConfigureAwait(false)) return;
        try {
            var snap = await QueryStatusForActionAsync(lct).ConfigureAwait(false);
            if (snap is null) return;

            if (snap.State == StateRunning) {
                _ = _client.RestartLoopAsync();
                return;
            }

            if (snap.State == StateInstalled) {
                if (!snap.UnitPresent || snap.DaemonPid is not null) {
                    _surface.Attention("Starting now would race an existing daemon service — needs attention first.");
                    return;
                }
                await RunVerifiedMutationAsync(_cli.ServiceStartVerifiedAsync, lct).ConfigureAwait(false);
                return;
            }

            if (snap.UnitPresent) {
                if (snap.DaemonPid is not null) {
                    _surface.Attention("Starting now would race an existing daemon service — needs attention first.");
                    return;
                }
                await RunVerifiedMutationAsync(_cli.ServiceStartVerifiedAsync, lct).ConfigureAwait(false);
                return;
            }

            await _client.StartDaemonAsync(lct).ConfigureAwait(false); // today's detached start — no unit to rewrite
        } catch (OperationCanceledException) {
            // caller-cancelled or shutting down — nothing to surface
        } finally {
            _gate.Release();
        }
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
        _lifetime.Cancel();
        await QuiescedAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
