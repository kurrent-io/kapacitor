using System.Reactive;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels.Onboarding;

/// One classified row of the spec §3 step-7 matrix — the step's whole decision, named.
public enum DaemonRow {
    CliMissing, RequiresSignIn, NoServerConfigured, BinaryUnresolved, StatusUnknown, TransactionActive,
    AlreadyEnabled, OwnedIdentityMismatch, ManualIdentityMatch, ManualIdentityMismatch,
    OrphanLabel, StaleMarker, RunningUnconfirmed, Stopped, NotInstalled,
}

/// The single action a row offers, or None.
public enum DaemonAffordance { None, Install, Start, Takeover, Repair }

/// Daemon enablement over the lifecycle state matrix; mutations update this step's status only — dialogs/recovery are presented by the channel's single consumer.
public sealed class DaemonStepViewModel : ReactiveObject, IWizardStep {
    internal const string ConsentV3Capability = "consent/3";

    internal const string CliMissingMessage    = "kcap CLI not found";
    internal const string RequiresSignInMessage = "Enabling the daemon requires sign-in — sign in first, or skip this step.";
    internal const string NoServerMessage       = "Server not configured — complete the Sign in step first.";
    internal const string BinaryUnresolvedMessage = "kcap can't resolve its own daemon binary — reinstall kcap.";
    internal const string StatusUnknownMessage = "Could not read the daemon service status — no changes made.";
    internal const string UnrecognizedStateMessage = "The daemon service reported an unrecognized state — no changes made.";
    internal const string TxnWaitingMessage    = "Waiting for a daemon service operation to finish…";
    internal const string TxnActiveMessage     = "A daemon service operation is still in progress — try again in a moment.";
    internal const string AlreadyEnabledMessage = "The daemon service is already enabled.";
    internal const string StaleMarkerNote      = "A previous operation left a stale marker.";
    internal const string StaleMarkerMessage   = "A previous daemon service operation left a stale marker — repair the service.";
    internal const string OrphanLabelMessage   = "The daemon service label is loaded but its unit file is missing — repair the service.";
    internal const string RunningUnconfirmedMessage = "The service reports its job running, but no daemon answers it yet — try again in a moment.";
    internal const string StoppedMessage       = "The daemon service is installed but not running.";
    internal const string NotInstalledMessage  = "kcap will install the daemon service and start it.";
    internal const string TakeoverDeclinedMessage = "Left as it is — the daemon service was not replaced.";
    internal const string DetachedMessage      = "kcap is still finishing this operation in the background.";
    internal const string ClaimMissingCapabilityMessage =
        "This daemon is too old to accept the consent update — kcap will apply it once the daemon updates.";
    internal const string ClaimFailedMessage =
        "kcap could not update this daemon's consent default — run `kcap daemon consent set-default prompt`, or re-run onboarding.";
    internal const string ClaimAlreadyStricterMessage =
        "This daemon already denies launches by default — kcap left that stricter setting alone.";

    internal static readonly TimeSpan TxnPollInterval = TimeSpan.FromSeconds(2);
    internal const int MaxTxnPolls = 30; // 30 × 2s ≈ the CLI transaction's own 60s worst case

    readonly IKcapCli _cli;
    readonly Func<MutationRequest, CancellationToken, Task<MutationOutcome>> _runMutation;
    readonly Func<(string Profile, string Server, string DaemonName)?> _resolveIdentity;
    readonly IDaemonObservation _observation;
    readonly ILocalControlOps _ops;
    readonly ConsentFlipClaims _claims;
    readonly Func<(string Profile, string Server, string DaemonName)> _resolveIdentityUnderConfigLock;
    readonly ILifecycleSurface _surface;
    readonly Func<CancellationToken, Task<string?>> _terminalPathAsync;
    readonly TimeProvider _time;

    MutationRequest? _request;
    ObservedEvidence? _evidence;
    CancellationTokenSource? _classifyCts;
    CancellationTokenSource? _actionCts;
    Task? _classifyRun;
    Task? _actionRun;

    DaemonRow _row = DaemonRow.StatusUnknown;
    DaemonAffordance _affordance;
    bool _busy;
    bool _satisfied;
    string? _message;
    string? _status;

    public DaemonStepViewModel(
            IKcapCli cli,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<(string Profile, string Server, string DaemonName)?> resolveIdentity,
            IDaemonObservation observation,
            ILocalControlOps ops,
            ConsentFlipClaims claims,
            Func<(string Profile, string Server, string DaemonName)> resolveIdentityUnderConfigLock,
            ILifecycleSurface surface,
            Func<CancellationToken, Task<string?>> terminalPathAsync,
            TimeProvider time) {
        _cli                            = cli;
        _runMutation                    = runMutation;
        _resolveIdentity                = resolveIdentity;
        _observation                    = observation;
        _ops                            = ops;
        _claims                         = claims;
        _resolveIdentityUnderConfigLock = resolveIdentityUnderConfigLock;
        _surface                        = surface;
        _terminalPathAsync              = terminalPathAsync;
        _time                           = time;

        var idle = this.WhenAnyValue(x => x.Busy, busy => !busy);
        ActionCommand  = ReactiveCommand.CreateFromTask(RunActionAsync,
            this.WhenAnyValue(x => x.Busy, x => x.Affordance, (busy, affordance) => !busy && affordance != DaemonAffordance.None));
        RefreshCommand = ReactiveCommand.CreateFromTask(() => RefreshAsync(CancellationToken.None), idle);
    }

    public WizardStepId Id         => WizardStepId.Daemon;
    public string       Title      => "Enable the daemon";
    public bool         Applicable => true;

    /// Set ONLY by the lane's own success outcome or the already-enabled row, and never cleared —
    /// a later re-classification must not re-derive (or revoke) a mutation's success from a snapshot.
    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public DaemonRow Row {
        get => _row;
        private set => this.RaiseAndSetIfChanged(ref _row, value);
    }

    public DaemonAffordance Affordance {
        get => _affordance;
        private set {
            this.RaiseAndSetIfChanged(ref _affordance, value);
            this.RaisePropertyChanged(nameof(ActionLabel));
        }
    }

    public string? ActionLabel => Affordance switch {
        DaemonAffordance.Install  => "Enable daemon",
        DaemonAffordance.Start    => "Start daemon",
        DaemonAffordance.Takeover => "Replace daemon service",
        DaemonAffordance.Repair   => "Repair daemon service",
        _                         => null,
    };

    public bool Busy {
        get => _busy;
        private set {
            this.RaiseAndSetIfChanged(ref _busy, value);
            this.RaisePropertyChanged(nameof(Idle));
        }
    }

    public bool Idle => !Busy;

    /// What the current row found — the honest line, present on every row.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// The last action's result, mapped from the lane's own outcome — never a re-derived one.
    public string? Status {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public ReactiveCommand<Unit, Unit> ActionCommand  { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public Task OnEnterAsync(CancellationToken ct) => RefreshAsync(ct);

    /// Never vetoes: a running mutation belongs to the LANE (§6a), so cancelling detaches this
    /// waiter only. A running claim put is untokened and simply awaited (bounded by LocalControlOps).
    public async Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct) {
        _classifyCts?.Cancel();
        _actionCts?.Cancel();

        await AwaitQuietlyAsync(_classifyRun).ConfigureAwait(false);
        await AwaitQuietlyAsync(_actionRun).ConfigureAwait(false);

        return true;
    }

    static async Task AwaitQuietlyAsync(Task? task) {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* leaving mid-work is not a failure */ }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: wizard daemon step failed unexpectedly: {ex.Message}"); }
    }

    internal Task RefreshAsync(CancellationToken ct) {
        if (Busy) return Task.CompletedTask;

        _classifyCts?.Dispose();
        _classifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return _classifyRun = ClassifyAsync(_classifyCts.Token);
    }

    async Task ClassifyAsync(CancellationToken ct) {
        Busy = true;
        try {
            _request  = null;
            _evidence = null;
            Status    = null;

            if (_cli.CliPath is null) {
                Set(DaemonRow.CliMissing, CliMissingMessage, DaemonAffordance.None);
                return;
            }

            if (_resolveIdentity() is not { } identity) {
                Set(DaemonRow.RequiresSignIn, RequiresSignInMessage, DaemonAffordance.None);
                return;
            }

            // Guarded before any snapshot read — an unbindable server never reaches a mutation.
            if (MutationRequestFactory.TryBuild(
                    MutationVerb.StartVerified, identity.Profile, identity.Server, identity.DaemonName, out var request)
                is MutationOutcome.Refused(var guardReason, _)) {
                Set(DaemonRow.NoServerConfigured, NoServerMessage, DaemonAffordance.None);
                Console.Error.WriteLine($"kcap: wizard daemon step refused before status: {guardReason}");
                return;
            }

            _request = request!;
            var snapshot = await ReadStatusAsync(ct).ConfigureAwait(false);
            await ClassifySnapshotAsync(snapshot, ct).ConfigureAwait(false);
            WithdrawUnitWritingOfferWithoutBinary(snapshot);
        } catch (OperationCanceledException) {
            // left the step (or shutting down) mid-classification — nothing to surface
        } catch (Exception ex) {
            Set(DaemonRow.StatusUnknown, StatusUnknownMessage, DaemonAffordance.None); // unknown, never a positive row
            Console.Error.WriteLine($"kcap: wizard daemon step classification failed unexpectedly: {ex.Message}");
        } finally {
            Busy = false;
        }
    }

    /// spec §6a: a held transaction is waited out, never mutated into. Bounded — an orphaned
    /// grandchild that outlives a force-quit must not wedge the step forever.
    async Task<ServiceSnapshot?> ReadStatusAsync(CancellationToken ct) {
        for (var poll = 0; ; poll++) {
            var snapshot = await _cli.ServiceStatusAsync(ct).ConfigureAwait(false);
            if (snapshot is null || !snapshot.TxnActive || poll >= MaxTxnPolls) return snapshot;

            Message = TxnWaitingMessage;
            await Task.Delay(TxnPollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    // Precedence: unreadable evidence, live transaction, the pid-keyed ownership rows, then the
    // repair rows — a stale marker precedes every install/start row, never a blind reinstall (§6a).
    async Task ClassifySnapshotAsync(ServiceSnapshot? snapshot, CancellationToken ct) {
        if (snapshot is null) {
            Set(DaemonRow.StatusUnknown, StatusUnknownMessage, DaemonAffordance.None);
            return;
        }

        var state = ServiceStateClassifier.Parse(snapshot.State);
        if (state == ServiceState.Unknown) {
            Set(DaemonRow.StatusUnknown, UnrecognizedStateMessage, DaemonAffordance.None);
            return;
        }

        if (snapshot.TxnActive) {
            Set(DaemonRow.TransactionActive, TxnActiveMessage, DaemonAffordance.None);
            return;
        }

        if (snapshot.DaemonPid is { } daemonPid) {
            await ClassifyLiveDaemonAsync(snapshot, daemonPid, ct).ConfigureAwait(false);
            return;
        }

        if (state == ServiceState.Installed && !snapshot.UnitPresent) {
            Set(DaemonRow.OrphanLabel, OrphanLabelMessage, DaemonAffordance.Repair);
            return;
        }

        if (snapshot.TxnMarker) {
            Set(DaemonRow.StaleMarker, StaleMarkerMessage, DaemonAffordance.Repair);
            return;
        }

        if (state == ServiceState.Running) {
            Set(DaemonRow.RunningUnconfirmed, RunningUnconfirmedMessage, DaemonAffordance.None);
            return;
        }

        if (snapshot.UnitPresent) {
            Set(DaemonRow.Stopped, StoppedMessage, DaemonAffordance.Start);
            return;
        }

        Set(DaemonRow.NotInstalled, NotInstalledMessage, DaemonAffordance.Install);
    }

    async Task ClassifyLiveDaemonAsync(ServiceSnapshot snapshot, int daemonPid, CancellationToken ct) {
        _evidence = await _observation.ObserveAsync(_request!, ct).ConfigureAwait(false);
        var matched = IdentityMatches(_evidence, _request!);
        var owned   = snapshot.JobPid is not null && snapshot.JobPid == snapshot.DaemonPid;

        if (owned && matched) {
            Set(DaemonRow.AlreadyEnabled,
                snapshot.TxnMarker ? $"{AlreadyEnabledMessage} {StaleMarkerNote}" : AlreadyEnabledMessage,
                DaemonAffordance.None);
            Satisfied = true;
            await ApplyPendingClaimAsync(_request!, _evidence).ConfigureAwait(false);
            return;
        }

        if (owned) {
            Set(DaemonRow.OwnedIdentityMismatch,
                $"The installed daemon service is running {DescribeForeign(_evidence)} — replacing it re-points the service at this profile.",
                DaemonAffordance.Takeover);
            return;
        }

        if (matched) {
            // Not enablement: a manual daemon dies at logout and no later startup phase repairs it.
            Set(DaemonRow.ManualIdentityMatch,
                $"A daemon is already running outside the service (PID {daemonPid}) — it stops at logout unless kcap manages it.",
                DaemonAffordance.Takeover);
            return;
        }

        Set(DaemonRow.ManualIdentityMismatch,
            $"{DescribeForeign(_evidence)} is already running under this name (PID {daemonPid}) — kcap changed nothing.",
            DaemonAffordance.Takeover);
    }

    /// The install-only viability precondition: a unit-writing offer without a resolvable daemon
    /// binary is a guaranteed coded failure, so it is withdrawn rather than offered. The start row
    /// is exempt — starting an installed unit writes none.
    void WithdrawUnitWritingOfferWithoutBinary(ServiceSnapshot? snapshot) {
        if (snapshot?.InstallBinaryPath is not null) return;
        if (Affordance is not (DaemonAffordance.Install or DaemonAffordance.Takeover or DaemonAffordance.Repair)) return;

        Set(DaemonRow.BinaryUnresolved, BinaryUnresolvedMessage, DaemonAffordance.None);
    }

    static string DescribeForeign(ObservedEvidence? evidence) =>
        evidence is { Reachable: true, ServerUrl: { Length: > 0 } server }
            ? $"a daemon for {server}"
            : "a daemon kcap could not identify";

    /// Fail closed (§6 probe): a positive match needs a reachable daemon whose two probe dials
    /// provably landed on the SAME process and whose name and canonical server both agree.
    static bool IdentityMatches(ObservedEvidence? evidence, MutationRequest request) =>
        evidence is { Reachable: true, IdentityConsistent: true }
        && evidence.DaemonName == request.DaemonName
        && ServerIdentity.Matches(evidence.ServerUrl, request.CanonicalServer);

    void Set(DaemonRow row, string message, DaemonAffordance affordance) {
        Row        = row;
        Message    = message;
        Affordance = affordance;
    }

    internal Task RunActionAsync() {
        if (Busy) return Task.CompletedTask;

        _actionCts?.Dispose();
        _actionCts = new CancellationTokenSource();
        return _actionRun = RunActionCoreAsync(_actionCts.Token);
    }

    async Task RunActionCoreAsync(CancellationToken ct) {
        Busy = true;
        try {
            switch (Affordance) {
                case DaemonAffordance.Install:
                    await RunMutationAsync(MutationVerb.Install, ct).ConfigureAwait(false);
                    break;
                case DaemonAffordance.Start:
                    await RunMutationAsync(MutationVerb.StartVerified, ct).ConfigureAwait(false);
                    break;
                case DaemonAffordance.Takeover:
                    await RunConsentedReplaceAsync(LifecyclePrompt.KindTakeover, ct).ConfigureAwait(false);
                    break;
                case DaemonAffordance.Repair:
                    await RunConsentedReplaceAsync(LifecyclePrompt.KindRepair, ct).ConfigureAwait(false);
                    break;
            }
        } catch (OperationCanceledException) {
            Status = DetachedMessage;
        } catch (Exception ex) {
            Status = $"The daemon service operation failed unexpectedly: {ex.Message}";
        } finally {
            Busy = false;
        }
    }

    // The ONLY dialog this step opens: consent BEFORE a unit rewrite (spec decision 3's
    // disclosure). Outcome presentation belongs to the channel consumer, never to this waiter.
    async Task RunConsentedReplaceAsync(string kind, CancellationToken ct) {
        var pathDegraded = await _terminalPathAsync(ct).ConfigureAwait(false) is null;
        var prompt = new LifecyclePrompt(
            kind, null, null, pathDegraded, DaemonLifecycleController.TakeoverDisclosure);

        if (!await _surface.ConfirmAsync(prompt, ct).ConfigureAwait(false)) {
            Status = TakeoverDeclinedMessage;
            // Decline leaves the step visibly incomplete — but the flip protects the owner
            // regardless of which process hosts an identity-MATCHED daemon (§3 step 7).
            if (Row == DaemonRow.ManualIdentityMatch) await ApplyPendingClaimAsync(_request!, _evidence).ConfigureAwait(false);
            return;
        }

        await RunMutationAsync(MutationVerb.Replace, ct).ConfigureAwait(false);
    }

    async Task RunMutationAsync(MutationVerb verb, CancellationToken ct) {
        var request = _request! with { Verb = verb };
        var outcome = await _runMutation(request, ct).ConfigureAwait(false);

        // The lane's own outcome IS the predicate — never a status snapshot read afterwards.
        Status = OutcomeStatus(verb, outcome);
        if (outcome is not (MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout)) return;

        Satisfied = true;
        _evidence = await _observation.ObserveAsync(request, ct).ConfigureAwait(false);
        if (IdentityMatches(_evidence, request)) await ApplyPendingClaimAsync(request, _evidence).ConfigureAwait(false);
    }

    /// Reads the live policy first because the put replaces it wholesale, and stays untokened so leaving the step awaits rather than cancels mid-claim.
    async Task ApplyPendingClaimAsync(MutationRequest request, ObservedEvidence? evidence) {
        try {
            await ApplyPendingClaimCoreAsync(request, evidence).ConfigureAwait(false);
        } catch (Exception ex) {
            Status = Append(Status, ClaimFailedMessage); // claim retained for the post-wizard coordinator
            Console.Error.WriteLine($"kcap: wizard daemon step consent flip failed unexpectedly: {ex.Message}");
        }
    }

    async Task ApplyPendingClaimCoreAsync(MutationRequest request, ObservedEvidence? evidence) {
        var pending = await Task.Run(_claims.Pending).ConfigureAwait(false);
        var claim = pending.FirstOrDefault(
            c => c.Profile == request.Profile && c.CanonicalServer == request.CanonicalServer);
        if (claim is null) return;

        if (evidence?.Capabilities?.Contains(ConsentV3Capability) != true) {
            Status = Append(Status, ClaimMissingCapabilityMessage);
            return;
        }

        ConsentAckDto ack;
        try {
            var policy = await _ops.GetConsentPolicyAsync(CancellationToken.None).ConfigureAwait(false);
            // §6 seeding respects an operator's deny: it is stricter than prompt, so the flip is inert here.
            if (policy.Default == "deny") {
                Status = Append(Status, ClaimAlreadyStricterMessage);
                return;
            }

            var put = new ConsentPolicyPutV2Dto(request.DaemonName, claim.CanonicalServer, policy with { Default = "prompt" });
            ack = await _ops.PutConsentPolicyV2Async(put, CancellationToken.None).ConfigureAwait(false);
        } catch (LocalControlOpsException) {
            Status = Append(Status, ClaimFailedMessage); // claim retained for the post-wizard coordinator
            return;
        }

        if (!ack.Ok) {
            Status = Append(Status, ClaimFailedMessage); // identity_mismatch or any other rejection
            return;
        }

        await Task.Run(() => _claims.TryConsume(claim, ResolveCanonical, request.DaemonName)).ConfigureAwait(false);
    }

    // Both this step's own match and TryConsume's re-resolve must observe identical canonicalization — the store never re-canonicalizes.
    (string Profile, string Server, string DaemonName) ResolveCanonical() {
        var (profile, server, daemonName) = _resolveIdentityUnderConfigLock();
        return (profile, ServerIdentity.Canonicalize(server) ?? server, daemonName);
    }

    static string Append(string? status, string sentence) =>
        string.IsNullOrEmpty(status) ? sentence : $"{status} {sentence}";

    internal static string OutcomeStatus(MutationVerb verb, MutationOutcome outcome) => outcome switch {
        MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout => "The daemon service is enabled.",
        MutationOutcome.UnconfirmedNoAttach   => $"The daemon {VerbDisplay(verb)} is not yet confirmed — kcap will follow up.",
        MutationOutcome.AttentionSkew(var d)  => $"The daemon {VerbDisplay(verb)} needs attention ({d}) — kcap will follow up.",
        MutationOutcome.AttentionRepair(var d) => $"The daemon {VerbDisplay(verb)} needs attention ({d}) — kcap will follow up.",
        MutationOutcome.Refused(var reason, _) => $"kcap did not change the daemon service ({reason}).",
        MutationOutcome.Failed(var exitCode, var reason, _) =>
            $"The daemon {VerbDisplay(verb)} failed ({reason ?? VerifyExitCodes.Token(exitCode)}).",
        _ => $"The daemon {VerbDisplay(verb)} did not complete.",
    };

    static string VerbDisplay(MutationVerb verb) => verb switch {
        MutationVerb.Install       => "install",
        MutationVerb.Replace       => "replacement",
        MutationVerb.StartVerified => "start",
        MutationVerb.DetachedStart => "start",
        _                          => verb.ToString(),
    };
}
