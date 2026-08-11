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
    int _generation;
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
        }
    }

    // Every AttachStatus transition bumps the generation and may signal an armed confirm
    // waiter; only a TERMINAL outcome (never Connecting) can claim the once-per-run arm, and
    // only the FIRST one ever does — a later duplicate/different terminal outcome is inert.
    void OnAttachStatus(AttachStatus status) {
        TaskCompletionSource<bool>? toSignal = null;
        bool claimedHere;

        lock (_lock) {
            _generation++;
            var gen = _generation;

            if (status.State == AttachState.Connected && _confirmWaiter is not null && gen > _confirmSinceGen) {
                toSignal       = _confirmWaiter;
                _confirmWaiter = null;
            }

            claimedHere = status.State != AttachState.Connecting && !_armClaimed;
            if (claimedHere) _armClaimed = true;
        }

        toSignal?.TrySetResult(true);
        if (!claimedHere) return;

        switch (status.State) {
            case AttachState.Connected:
                ClosePhase();
                _ = RunConnectedReconciliationAsync();
                break;
            case AttachState.Unreachable when status.Reason == IncompatibleReason:
                ClosePhase(); // Task 20 adds the skew/takeover offer here
                break;
            case AttachState.Unreachable:
                _ = RunStartupBranchAsync();
                break;
        }
    }

    void ClosePhase() => _phaseClosed.TrySetResult(true);

    int CurrentGeneration() { lock (_lock) return _generation; }

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

    /// Discards a query whose result raced a newer AttachStatus transition — evidence is
    /// revalidated immediately before mutating (spec §3.2), never acted on once stale.
    async Task<ServiceSnapshot?> QueryFreshStatusAsync(CancellationToken ct) {
        var gen0 = CurrentGeneration();
        var snap = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false);
        return CurrentGeneration() == gen0 ? snap : null;
    }

    async Task RunConnectedReconciliationAsync() {
        if (_cli.CliPath is null) return;
        if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;

        try {
            var snap = await QueryFreshStatusAsync(_lifetime.Token).ConfigureAwait(false);
            if (snap is not null) Reconcile(snap, attached: true, allowTxnActiveRequery: true);
        } catch (OperationCanceledException) {
            // shutdown mid-query
        } finally {
            _gate.Release();
        }
    }

    /// §4.2: the startup branch. Runs at most once per app run (the arm claimed synchronously in
    /// OnAttachStatus); closes the startup phase only once this full flow — matrix decision, any
    /// mutation, and its confirmation wait — has completed.
    async Task RunStartupBranchAsync() {
        try {
            if (_cli.CliPath is null) {
                _surface.Status("kcap CLI not found — daemon lifecycle management is off for this run.");
                return;
            }

            if (!await TryAcquireGateAsync(_lifetime.Token).ConfigureAwait(false)) return;
            try {
                var snap = await QueryFreshStatusAsync(_lifetime.Token).ConfigureAwait(false);
                if (snap is null) return; // unknown / stale evidence — no mutation (spec §6)

                Reconcile(snap, attached: false, allowTxnActiveRequery: true);
                await RunStartupMatrixAsync(snap, _lifetime.Token).ConfigureAwait(false);
            } finally {
                _gate.Release();
            }
        } catch (OperationCanceledException) {
            // shutdown mid-branch
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

            if (result.ExitCode == VerifyExitCodes.Ok) {
                _ = _client.RestartLoopAsync(); // kick reattach regardless of the confirm outcome below
                var won = await Task.WhenAny(waiter.Task, Task.Delay(ConfirmWindow, _time, ct)).ConfigureAwait(false);
                if (won != waiter.Task && !ct.IsCancellationRequested)
                    _surface.Status("daemon started, app not yet attached — retrying");
            } else {
                _surface.Status($"{VerifyExitCodes.Token(result.ExitCode)}: {result.Stderr.Trim()}");
            }
        } finally {
            DisarmConfirmWaiter(waiter);
        }
    }

    /// Reconciliation (spec §3.2): surfaces every inconsistent combination found in one
    /// ServiceStatusAsync snapshot — never mutates. `attached` gates the checks that only make
    /// sense while genuinely connected (or that would otherwise double-report a startup-matrix
    /// row's own Attention for the exact same evidence).
    void Reconcile(ServiceSnapshot snap, bool attached, bool allowTxnActiveRequery) {
        if (snap.JobPid is not null && snap.DaemonPid is not null && snap.JobPid != snap.DaemonPid)
            _surface.Attention(
                $"The daemon service job (pid {snap.JobPid}) does not match the attached daemon (pid {snap.DaemonPid}).");
        else if (attached && snap.State == StateRunning && snap.DaemonPid is null)
            _surface.Attention("The service reports its job running, but no attached-daemon evidence backs it.");

        if (snap.TxnMarker && !snap.TxnActive)
            _surface.Attention("A previous daemon service operation left a stale transaction marker — repair may be needed.");

        if (snap.TxnActive && allowTxnActiveRequery)
            _ = RunTxnActiveRequeryAsync(attached);

        if (attached && snap.UnitPresent && snap.State == StateNotInstalled && snap.DaemonPid is not null)
            _surface.Attention(
                $"A daemon is running outside the installed service — the service is stopped while a manual daemon (pid {snap.DaemonPid}) owns the name.");
    }

    // Exactly one follow-up query (allowTxnActiveRequery: false on the way back in) — an
    // orphaned grandchild that outlives a force-quit is waited out, not repaired (spec §6).
    async Task RunTxnActiveRequeryAsync(bool attached) {
        try {
            await Task.Delay(TxnActiveRequeryDelay, _time, _lifetime.Token).ConfigureAwait(false);
            var snap = await QueryFreshStatusAsync(_lifetime.Token).ConfigureAwait(false);
            if (snap is not null) Reconcile(snap, attached, allowTxnActiveRequery: false);
        } catch (OperationCanceledException) {
            // shutdown mid-requery
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
            var snap = await QueryFreshStatusAsync(lct).ConfigureAwait(false);
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
        _subscription?.Dispose();
        _lifetime.Cancel();
        await QuiescedAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }
}
