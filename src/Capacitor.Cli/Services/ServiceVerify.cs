using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Services;

/// <summary>Coded, stable exit codes for the <see cref="ServiceVerify"/> transaction engine.
/// Every non-<see cref="Ok"/> member has a matching stderr token defined beside it.</summary>
public static class VerifyExit {
    public const int Ok = 0;

    /// <summary>Service lock contended (start/install), OR install's pre-mutation probe found the
    /// label already Loaded — a fresh install without <c>--replace</c> must not clear it.</summary>
    public const int Contended = 20;
    public const string ContendedToken = "verify_contended";

    /// <summary>Install-verify's pre-mutation viability check: <c>spec.DaemonBinaryPath</c> does not
    /// exist. Nothing is touched — checked before the pre-mutation <c>Query</c> even runs.</summary>
    public const int Viability = 21;
    public const string ViabilityToken = "verify_viability";

    /// <summary>Install-verify's pre-mutation probe classified the label <c>Unknown</c> — neither
    /// clearly loaded nor clearly absent, so a fresh install refuses to guess and writes nothing.</summary>
    public const int BootoutUnknown = 22;
    public const string BootoutUnknownToken = "verify_bootout_unknown";

    /// <summary>Install-verify's <c>--replace</c> ownership matrix killed a validated live owner but
    /// couldn't confirm it gone (validated pid null AND a fresh hello dial failing) within the
    /// forward budget. Nothing is written yet at this point — the marker is retained at whatever
    /// phase the matrix last reached (<c>label-cleared</c> or <c>captured</c>).</summary>
    public const int StopUnconfirmed = 23;
    public const string StopUnconfirmedToken = "verify_stop_unconfirmed";

    /// <summary>Forward cutoff hit and rollback restored the verified-safe state (start: unloaded,
    /// plist retained; install: label absent AND unit file removed).</summary>
    public const int ReadinessTimeout = 24;
    public const string ReadinessTimeoutToken = "verify_readiness_timeout";

    /// <summary>Install-verify's hello was well-formed but reported a <c>DaemonVersion</c> other than
    /// the expected one — a deterministic mismatch, so rollback fires without waiting out the forward
    /// budget. Applies equally to a fresh install and <c>--replace</c> — both share the same
    /// write+bootstrap+verify tail.</summary>
    public const int HelloValidation = 25;
    public const string HelloValidationToken = "verify_hello_validation";

    /// <summary>Rollback's bounded poll ran out with the state still genuinely undetermined — the
    /// LAST observation was <see cref="LabelProbe.Unknown"/>, so there's no way to tell whether the
    /// restore actually happened. A timeout, not an observed-wrong state: compare
    /// <see cref="RestoreVerification"/>, which is what a Loaded (or file-still-present) last
    /// observation gets even at the same reserve expiry. The marker is retained either way.</summary>
    public const int RollbackBudget = 26;
    public const string RollbackBudgetToken = "verify_rollback_budget";

    /// <summary>Rollback (or the final recheck) affirmatively found the wrong state: still Loaded,
    /// unloaded but the unit file still present, or a plist on disk whose fingerprint doesn't match
    /// what this transaction wrote (a foreign writer) — including when the reserve ran out and THAT
    /// was the last observation (see <see cref="RollbackBudget"/> for the Unknown-only timeout case).
    /// The marker, and any foreign file, are left alone so an operator/resumer can see the
    /// transaction never reached a verified-safe state.</summary>
    public const int RestoreVerification = 27;
    public const string RestoreVerificationToken = "verify_restore_verification";
}

/// <summary>
/// Spec §3.4 transaction engine: marker → mutate → ownership+readiness poll → final recheck →
/// commit, or rollback to the verified-safe failure state. Injectable seams (manager, pid probe,
/// hello probe, clock) make every case drivable without shelling out to <c>launchctl</c>.
/// </summary>
sealed class ServiceVerify(
    IServiceManager manager,
    Func<string, int?> validatedDaemonPid,
    Func<string, TimeSpan, Task<HelloProbeResult>> hello,
    TimeProvider time,
    TimeSpan? forwardBudget = null,
    TimeSpan? rollbackReserve = null,
    Func<string, string?>? readPlist = null,
    Func<string, bool>? plistExists = null) {
    static readonly TimeSpan LockWait      = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PollInterval  = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan KillWait      = TimeSpan.FromSeconds(5);

    readonly TimeSpan _forwardBudget    = forwardBudget ?? TimeSpan.FromSeconds(20);
    readonly TimeSpan _rollbackReserve  = rollbackReserve ?? TimeSpan.FromSeconds(10);

    /// <summary>Install-only seam for the on-disk plist read (final recheck + rollback's foreign-file
    /// guard). Real launchd needs HOME to compute the path at all, so tests inject this directly
    /// rather than fiddling with the environment for fakes that aren't real launchd anyway.</summary>
    readonly Func<string, string?> _readPlist = readPlist ?? (path => {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    });

    /// <summary>Entry-recovery-only seam: distinguishes "absent" from "present but unreadable" when
    /// <see cref="_readPlist"/> returns null for both — a permission error or transient I/O failure
    /// on a file that demonstrably exists must never be treated the same as the file simply not being
    /// there (see <see cref="RecoverLeftoverMarker"/>).</summary>
    readonly Func<string, bool> _plistExists = plistExists ?? File.Exists;

    /// <summary>Closed-stdio tolerance: the npm grandchild shares the GUI's pipes, so a broken
    /// pipe must never abort the transaction.</summary>
    static void Say(string line) {
        try { Console.Error.WriteLine(line); } catch (IOException) { }
    }

    static string DescribeQuery(ServiceQuery q) =>
        $"{q.Probe.ToString().ToLowerInvariant()}|{(q.UnitPresent ? "unit" : "nounit")}|{q.BinaryPath}|pid={q.JobPid}";

    /// <summary>start --verify: no viability check (spec: start writes nothing). Accepts ANY
    /// well-formed hello — capability-incompatible old daemons included.</summary>
    public async Task<int> StartVerifiedAsync(string serviceId) {
        using var txn = ServiceTxnLock.TryAcquire(serviceId, LockWait);
        if (txn is null) {
            Say(VerifyExit.ContendedToken);
            return VerifyExit.Contended;
        }

        var pre = manager.Query(serviceId);
        ServiceTxnMarker.Write(serviceId,
            new TxnMarker(1, "start", "captured", DescribeQuery(pre), "unloaded-plist-retained", null));

        // bootstrap-or-kickstart (Task 7); a false return doesn't short-circuit — the readiness
        // poll below is the source of truth — but the reason is worth surfacing if it never recovers.
        if (!manager.Start(serviceId, out var startError) && startError is not null) Say($"start: {startError}");

        ServiceTxnMarker.Write(serviceId,
            new TxnMarker(1, "start", "bootstrapped", DescribeQuery(pre), "unloaded-plist-retained", null));

        var deadline = time.GetUtcNow() + _forwardBudget;

        while (time.GetUtcNow() < deadline) {
            if (await IsReadyAsync(serviceId, deadline - time.GetUtcNow())) {
                // Confirming what the primary check JUST observed, not a fresh independent
                // check — must not be budget-starved by the same forward deadline that gated
                // the primary check (a slow primary hello can legitimately resolve with almost
                // nothing left), or a genuinely healthy daemon gets rolled back on a clock
                // technicality. Floor it at one poll interval.
                var confirmBudget = deadline - time.GetUtcNow();
                if (confirmBudget < PollInterval) confirmBudget = PollInterval;

                if (await IsReadyAsync(serviceId, confirmBudget)) {
                    ServiceTxnMarker.Delete(serviceId);
                    return VerifyExit.Ok;
                }
            }

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        return await Rollback(serviceId);
    }

    /// <summary>Ownership + readiness predicate: hello well-formed AND the freshly-queried job
    /// pid matches the validated daemon pid (both non-null).</summary>
    async Task<bool> IsReadyAsync(string serviceId, TimeSpan budget) {
        if (budget <= TimeSpan.Zero) return false;

        var h = await hello(serviceId, budget);
        if (!h.WellFormed) return false;

        var jobPid    = manager.Query(serviceId).JobPid;
        var daemonPid = validatedDaemonPid(serviceId);
        return jobPid is not null && daemonPid is not null && jobPid == daemonPid;
    }

    /// <summary>Bootout (plist retained) then verify the restore, polled (bounded by
    /// <see cref="_rollbackReserve"/>) rather than single-shot — <c>launchctl bootout</c> can
    /// return before the label is actually gone. Absent → the rollback itself is the
    /// verified-safe state, marker deleted. Still loaded when the reserve runs out → the marker
    /// is kept so a resumer can see the transaction never settled.</summary>
    async Task<int> Rollback(string serviceId) {
        manager.Stop(serviceId, out var stopError);
        if (stopError is not null) Say($"stop: {stopError}");

        var rollbackDeadline = time.GetUtcNow() + _rollbackReserve;
        LabelProbe lastProbe;

        while (true) {
            lastProbe = manager.Query(serviceId).Probe;
            if (lastProbe == LabelProbe.Absent) {
                ServiceTxnMarker.Delete(serviceId);
                Say(VerifyExit.ReadinessTimeoutToken);
                return VerifyExit.ReadinessTimeout;
            }

            if (time.GetUtcNow() >= rollbackDeadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        // 26 vs 27 turns on the LAST observation, not just "reserve expired": Unknown means we
        // genuinely couldn't tell (a timeout); still Loaded is an affirmatively wrong state (see
        // VerifyExit) even though it was also observed at the reserve deadline.
        if (lastProbe == LabelProbe.Unknown) {
            Say(VerifyExit.RollbackBudgetToken);
            return VerifyExit.RollbackBudget;
        }

        Say(VerifyExit.RestoreVerificationToken);
        return VerifyExit.RestoreVerification;
    }

    enum InstallReady { NotReady, Ready, VersionMismatch }

    /// <summary>install [--replace] --verify. <paramref name="replace"/> selects the ownership
    /// matrix (spec §3.4): a fresh install refuses to touch an existing label/unit (<see
    /// cref="VerifyExit.Contended"/>), while <c>--replace</c> clears/takes it over first.</summary>
    public async Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion) {
        var serviceId = spec.ServiceId;
        using var txn = ServiceTxnLock.TryAcquire(serviceId, LockWait);
        if (txn is null) {
            Say(VerifyExit.ContendedToken);
            return VerifyExit.Contended;
        }

        // Viability is checked BEFORE marker recovery (unlike Task 10's non-destructive placeholder,
        // recovery below can now call Uninstall) so VerifyExit.Viability's documented "nothing is
        // touched" contract stays true even when a leftover marker exists — a missing binary aborts
        // before anything on disk is touched, recovery or otherwise.
        if (!File.Exists(spec.DaemonBinaryPath)) {
            Say(VerifyExit.ViabilityToken);
            return VerifyExit.Viability;
        }

        // A leftover marker means a prior attempt never reached a terminal state. "committed" is
        // just a crash between writing that phase and deleting the marker — self-heal
        // unconditionally. Any other phase may have left real residue on disk; recovery authority
        // there is scoped by CONTENT (see RecoverLeftoverMarker), never by presence alone.
        if (ServiceTxnMarker.Read(serviceId) is { } leftover) {
            if (leftover.Phase == "committed") {
                ServiceTxnMarker.Delete(serviceId);
            } else if (await RecoverLeftoverMarker(spec, serviceId, leftover) is { } recoveryExit) {
                return recoveryExit;
            }
        }

        var pre = manager.Query(serviceId);
        if (pre.Probe == LabelProbe.Unknown) {
            // Neither clearly loaded nor clearly absent — nothing destructive after an unknown,
            // for a fresh install OR a replace.
            Say(VerifyExit.BootoutUnknownToken);
            return VerifyExit.BootoutUnknown;
        }

        var preState = DescribeQuery(pre);
        var op       = replace ? "replace" : "install";

        if (!replace) {
            if (pre.Probe == LabelProbe.Loaded || (pre.Probe == LabelProbe.Absent && pre.UnitPresent)) {
                // Already installed — loaded, or stopped-but-installed (`service stop` retains the
                // plist by design). A fresh install must not overwrite or clear an existing unit;
                // that's --replace's job.
                Say(VerifyExit.ContendedToken);
                return VerifyExit.Contended;
            }
        } else {
            // Marker discipline extends to the matrix's own destructive steps (Uninstall / kill):
            // write "captured" before touching anything, same as the fresh path does below.
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "captured", preState, "no-unit", null));

            if (await ApplyReplaceMatrixAsync(serviceId, pre, preState, op) is { } stopExit) {
                return stopExit;
            }
        }

        // For --replace, the marker is already at the correct phase here — either "captured" (the
        // matrix did nothing) or "label-cleared"/"owner-stopped" (it did) — re-writing "captured"
        // unconditionally would regress a phase that already recorded real destructive work.
        if (!replace) {
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "captured", preState, "no-unit", null));
        }

        // GenerateFiles is a pure computation (no I/O for any current manager) — a throw here (e.g.
        // an invalid captured env value) never touched disk, so there's nothing for a rollback to
        // undo; just report it and clear the marker we just wrote.
        GeneratedFile generated;
        try {
            generated = manager.GenerateFiles(spec).Single();
        } catch (Exception ex) {
            Say($"{op}: {ex.Message}");
            ServiceTxnMarker.Delete(serviceId);
            Say(VerifyExit.ReadinessTimeoutToken);
            return VerifyExit.ReadinessTimeout;
        }

        var fingerprint = ServiceTxnMarker.Fingerprint(generated.Content);
        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "written", preState, "no-unit", fingerprint));

        try {
            manager.WriteAndBootstrap(spec);
        } catch (Exception ex) {
            // Unlike GenerateFiles, WriteAndBootstrap writes the unit file before it bootstraps —
            // a throw here (EPERM under MDM, launchctl I/O error, ...) can still leave the plist on
            // disk, so route through the same fingerprint-gated rollback as every other failure.
            Say($"{op}: {ex.Message}");
            return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
        }

        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "bootstrapped", preState, "no-unit", fingerprint));

        var deadline = time.GetUtcNow() + _forwardBudget;

        while (time.GetUtcNow() < deadline) {
            var primary = await IsInstallReadyAsync(serviceId, expectedVersion, deadline - time.GetUtcNow());
            if (primary == InstallReady.VersionMismatch)
                return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

            if (primary == InstallReady.Ready) {
                // Same floor rationale as start: confirming what the primary check just observed
                // must not be starved by the same forward deadline that gated it.
                var confirmBudget = deadline - time.GetUtcNow();
                if (confirmBudget < PollInterval) confirmBudget = PollInterval;

                var confirm = await IsInstallReadyAsync(serviceId, expectedVersion, confirmBudget);
                if (confirm == InstallReady.VersionMismatch)
                    return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

                if (confirm == InstallReady.Ready) {
                    // Final recheck adds what start's doesn't need: the plist on disk must still be
                    // the one this transaction wrote. A foreign writer's replacement is never deleted
                    // by us — the marker and the foreign file are both left for an operator to see.
                    // Uses the manager-provided path (not a launchd-specific helper) so this stays
                    // meaningful on every platform.
                    var onDisk = _readPlist(generated.Path);
                    if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) == fingerprint) {
                        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "committed", preState, "no-unit", fingerprint));
                        ServiceTxnMarker.Delete(serviceId);
                        return VerifyExit.Ok;
                    }

                    Say(VerifyExit.RestoreVerificationToken);
                    return VerifyExit.RestoreVerification;
                }
            }

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
    }

    /// <summary>
    /// Entry-time recovery for a leftover marker whose phase is anything other than "committed".
    /// Recovery authority is scoped by CONTENT, not presence: only a plist whose fingerprint
    /// matches what the dead transaction itself recorded is provably its own residue and safe to
    /// clean; everything else is surfaced (<see cref="VerifyExit.RestoreVerification"/>) rather than
    /// guessed at. Returns null when it's safe to proceed (the marker has already been deleted);
    /// otherwise the exit code to return immediately (the marker is left untouched).
    /// </summary>
    async Task<int?> RecoverLeftoverMarker(ServiceSpec spec, string serviceId, TxnMarker leftover) {
        if (leftover.PlistFingerprint is null) {
            // Died before ever writing a plist (captured, or one of --replace's pre-write phases) —
            // nothing on disk to clean up.
            ServiceTxnMarker.Delete(serviceId);
            return null;
        }

        string onDiskPath;
        try {
            onDiskPath = manager.GenerateFiles(spec).Single().Path;
        } catch {
            // Can't even compute where our own residue would live — never pave over something we
            // can't examine.
            Say(VerifyExit.RestoreVerificationToken);
            return VerifyExit.RestoreVerification;
        }

        var onDisk = _readPlist(onDiskPath);
        if (onDisk is null) {
            if (_plistExists(onDiskPath)) {
                // Present but unreadable (permission error, transient I/O failure, ...) is NOT the
                // same as absent — the file demonstrably exists and was never fingerprint-compared,
                // so never guess it's our own gone residue.
                Say(VerifyExit.RestoreVerificationToken);
                return VerifyExit.RestoreVerification;
            }

            // Confirmed absent — residue already gone.
            ServiceTxnMarker.Delete(serviceId);
            return null;
        }

        if (ServiceTxnMarker.Fingerprint(onDisk) != leftover.PlistFingerprint) {
            // A foreign writer touched the file after the dead transaction — surface, never pave.
            Say(VerifyExit.RestoreVerificationToken);
            return VerifyExit.RestoreVerification;
        }

        // The dead transaction's own residue (benign-absence semantics — a label that's already
        // gone costs nothing to "clear" again): clean it up via the SAME confirmed-Absent clear the
        // --replace matrix uses — Uninstall returning true on a bare bootout exit 0 does not by
        // itself prove the label is gone (the exact race ClearLabelAsync polls past), so trusting
        // the raw bool here would risk deleting the marker on an unconfirmed clear.
        if (await ClearLabelAsync(serviceId) is { } clearExit) return clearExit;

        ServiceTxnMarker.Delete(serviceId);
        return null;
    }

    /// <summary>
    /// Uninstall (clear the label/plist) and poll until confirmed <see cref="LabelProbe.Absent"/>,
    /// bounded by <see cref="_rollbackReserve"/> — mirrors <see cref="InstallRollback"/>'s own
    /// reasoning: <c>launchctl bootout</c> can return before the label is actually gone, and a
    /// failed/incomplete Uninstall (still Loaded/Unknown) must never be treated as cleared before
    /// writing a NEW unit over it. Returns the terminal exit code on failure (nothing further is
    /// written), or null once Absent is confirmed and it's safe to proceed. The pinned split is
    /// deliberate: <see cref="VerifyExit.BootoutUnknown"/>(22) is the PRE-mutation abort (nothing
    /// touched yet), while this poll's own Unknown-at-reserve-expiry is POST-mutation and genuinely
    /// undetermined — that's <see cref="VerifyExit.RollbackBudget"/>(26), never 22.
    /// </summary>
    async Task<int?> ClearLabelAsync(string serviceId) {
        manager.Uninstall(serviceId, out var error);
        if (error is not null) Say($"uninstall: {error}");

        var deadline = time.GetUtcNow() + _rollbackReserve;
        LabelProbe lastProbe;

        while (true) {
            lastProbe = manager.Query(serviceId).Probe;
            if (lastProbe == LabelProbe.Absent) return null;

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        // Same 26-vs-27 split as every other rollback poll in this file: Unknown is a genuine
        // timeout (couldn't tell), still Loaded is an affirmatively wrong state.
        var reason = lastProbe == LabelProbe.Unknown ? VerifyExit.RollbackBudget : VerifyExit.RestoreVerification;
        Say(lastProbe == LabelProbe.Unknown ? VerifyExit.RollbackBudgetToken : VerifyExit.RestoreVerificationToken);
        return reason;
    }

    /// <summary>
    /// install --replace's ownership matrix (spec §3.4 table). Called only after the pre-mutation
    /// probe has already been rejected as <see cref="LabelProbe.Unknown"/> by the caller, with the
    /// "captured" marker already on disk. Returns a non-null exit code when clearing the label
    /// couldn't be confirmed (<see cref="ClearLabelAsync"/>'s own exits) or the kill couldn't be
    /// confirmed (<see cref="VerifyExit.StopUnconfirmed"/>) — in every such case nothing further is
    /// written and the marker is retained at its last-recorded phase. Every other outcome falls
    /// through to the shared write+bootstrap+verify tail.
    /// </summary>
    async Task<int?> ApplyReplaceMatrixAsync(string serviceId, ServiceQuery pre, string preState, string op) {
        var validatedPid = validatedDaemonPid(serviceId);
        var owning       = pre.Probe == LabelProbe.Loaded && pre.JobPid is not null && pre.JobPid == validatedPid;

        if (owning) {
            // The label's own bootout already terminates the process it owns (launchd/systemd tear
            // down the job as part of unloading it) — no separate kill needed or wanted.
            if (await ClearLabelAsync(serviceId) is { } clearExit) return clearExit;
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "label-cleared", preState, "no-unit", null));
            return null;
        }

        if (pre.Probe == LabelProbe.Loaded || pre.UnitPresent) {
            // A non-owning or orphan label, or a stopped-but-installed unit — --replace (unlike a
            // fresh install) is allowed to clear this. Benign-absence semantics: an already-absent
            // label costs nothing to "clear" again.
            if (await ClearLabelAsync(serviceId) is { } clearExit) return clearExit;
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "label-cleared", preState, "no-unit", null));
        }

        // Re-read AFTER any clearing above, not the pre-clear snapshot: the clear itself (bootout)
        // can terminate the true owner as a side effect of unloading its label — e.g. `pre.JobPid`
        // came back null from a launchctl-print race even though the label truly did own the live
        // daemon, so `owning` was false and we went through the clear above anyway. Killing the
        // ORIGINAL captured pid after a clear that can take up to _rollbackReserve risks signaling
        // a since-recycled pid instead. "Kill the validated owner IF ONE REMAINS" only holds if the
        // owner check happens after the clear, not before it.
        var liveOwner = validatedDaemonPid(serviceId);
        if (liveOwner is null) return null; // no live owner left to stop

        if (!DaemonKill.KillValidatedOwner(serviceId, liveOwner.Value, KillWait))
            Say($"replace: kill of validated owner (PID {liveOwner}) did not confirm gone immediately");

        if (!await WaitForStopConfirmedAsync(serviceId, _forwardBudget)) {
            Say(VerifyExit.StopUnconfirmedToken);
            return VerifyExit.StopUnconfirmed;
        }

        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "owner-stopped", preState, "no-unit", null));
        return null;
    }

    /// <summary>Stop-confirmation predicate for --replace's takeover kill: gone means the validated
    /// pid is null AND a fresh hello dial is not well-formed — a stronger bar than <see
    /// cref="DaemonKill"/>'s own pid-only gone-check, since a hung-but-still-answering process is not
    /// actually stopped.</summary>
    async Task<bool> IsStoppedAsync(string serviceId, TimeSpan budget) {
        if (budget <= TimeSpan.Zero) return false;

        var h = await hello(serviceId, budget);
        if (h.WellFormed) return false;

        return validatedDaemonPid(serviceId) is null;
    }

    async Task<bool> WaitForStopConfirmedAsync(string serviceId, TimeSpan budget) {
        var deadline = time.GetUtcNow() + budget;

        while (time.GetUtcNow() < deadline) {
            if (await IsStoppedAsync(serviceId, deadline - time.GetUtcNow())) return true;

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        return false;
    }

    /// <summary>Install's ownership + readiness + protocol/name/version predicate (spec §3.4:
    /// "Install/replace validates protocol, daemon name, and version"). Every one of those three is
    /// a deterministic fact about the answering daemon, not something a retry can fix, so each
    /// reports the SAME <see cref="InstallReady.VersionMismatch"/> the caller rolls back on
    /// immediately rather than waiting out the forward budget:
    /// <list type="bullet">
    /// <item>wrong <c>DaemonName</c> — something else is answering on our socket under this
    /// service id;</item>
    /// <item>unsupported <c>ProtocolVersion</c> — an incompatible wire shape;</item>
    /// <item><c>DaemonVersion</c> mismatch, but only when <paramref name="expectedVersion"/> is
    /// non-null — null means the caller opted out of version validation, not "any version is
    /// wrong".</item>
    /// </list>
    /// </summary>
    async Task<InstallReady> IsInstallReadyAsync(string serviceId, string? expectedVersion, TimeSpan budget) {
        if (budget <= TimeSpan.Zero) return InstallReady.NotReady;

        var h = await hello(serviceId, budget);
        if (!h.WellFormed) return InstallReady.NotReady;
        if (h.DaemonName != serviceId) return InstallReady.VersionMismatch;
        if (h.ProtocolVersion != HelloProtocol.CurrentVersion) return InstallReady.VersionMismatch;
        if (expectedVersion is not null && h.DaemonVersion != expectedVersion) return InstallReady.VersionMismatch;

        var jobPid    = manager.Query(serviceId).JobPid;
        var daemonPid = validatedDaemonPid(serviceId);
        return jobPid is not null && daemonPid is not null && jobPid == daemonPid
            ? InstallReady.Ready
            : InstallReady.NotReady;
    }

    /// <summary>Rollback for the fresh-install path: uninstall OUR unit and verify the restored state
    /// is label-absent AND file-gone (unlike start, which retains the plist), bounded by
    /// <see cref="_rollbackReserve"/>. A foreign plist (fingerprint mismatch) is never touched — same
    /// rule as the final recheck, checked before the uninstall is even attempted.</summary>
    async Task<int> InstallRollback(string serviceId, string plistPath, string fingerprint, int reasonExit, string reasonToken) {
        var onDisk = _readPlist(plistPath);
        if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) != fingerprint) {
            Say(VerifyExit.RestoreVerificationToken);
            return VerifyExit.RestoreVerification;
        }

        manager.Uninstall(serviceId, out var uninstallError);
        if (uninstallError is not null) Say($"uninstall: {uninstallError}");

        var rollbackDeadline = time.GetUtcNow() + _rollbackReserve;
        ServiceQuery lastQuery;

        while (true) {
            lastQuery = manager.Query(serviceId);
            if (lastQuery.Probe == LabelProbe.Absent && !lastQuery.UnitPresent) {
                ServiceTxnMarker.Delete(serviceId);
                Say(reasonToken);
                return reasonExit;
            }

            if (time.GetUtcNow() >= rollbackDeadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        // 26 vs 27 turns on the LAST observation, not just "reserve expired": Unknown means we
        // genuinely couldn't tell (a timeout); still Loaded, or unloaded with the file still
        // present, is an affirmatively wrong state (see VerifyExit) even at the same deadline.
        if (lastQuery.Probe == LabelProbe.Unknown) {
            Say(VerifyExit.RollbackBudgetToken);
            return VerifyExit.RollbackBudget;
        }

        Say(VerifyExit.RestoreVerificationToken);
        return VerifyExit.RestoreVerification;
    }
}
