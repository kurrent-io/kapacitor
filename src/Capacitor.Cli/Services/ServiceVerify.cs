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

    /// <summary>Reserved for install-verify's <c>--replace</c> rollback (Task 11).</summary>
    public const int StopUnconfirmed = 23;
    public const string StopUnconfirmedToken = "verify_stop_unconfirmed";

    /// <summary>Forward cutoff hit and rollback restored the verified-safe state (start: unloaded,
    /// plist retained; install: label absent AND unit file removed).</summary>
    public const int ReadinessTimeout = 24;
    public const string ReadinessTimeoutToken = "verify_readiness_timeout";

    /// <summary>Install-verify's hello was well-formed but reported a <c>DaemonVersion</c> other than
    /// the expected one — a deterministic mismatch, so rollback fires without waiting out the forward
    /// budget. Also reserved for install-verify's <c>--replace</c> path (Task 11).</summary>
    public const int HelloValidation = 25;
    public const string HelloValidationToken = "verify_hello_validation";

    /// <summary>Rollback's own bounded poll ran out before the verified-safe state was observed —
    /// distinct from <see cref="RestoreVerification"/>, which is an affirmative failure (a definitely
    /// wrong state was observed, e.g. a foreign plist), not a timeout. The marker is retained either way.</summary>
    public const int RollbackBudget = 26;
    public const string RollbackBudgetToken = "verify_rollback_budget";

    /// <summary>Rollback (or the final recheck) affirmatively found the wrong state — the service
    /// still loaded, or a plist on disk whose fingerprint doesn't match what this transaction wrote
    /// (a foreign writer). The marker, and any foreign file, are left alone so an operator/resumer can
    /// see the transaction never reached a verified-safe state.</summary>
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
    Func<string, string?>? readPlist = null) {
    static readonly TimeSpan LockWait      = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PollInterval  = TimeSpan.FromMilliseconds(500);

    readonly TimeSpan _forwardBudget    = forwardBudget ?? TimeSpan.FromSeconds(20);
    readonly TimeSpan _rollbackReserve  = rollbackReserve ?? TimeSpan.FromSeconds(10);

    /// <summary>Install-only seam for the on-disk plist read (final recheck + rollback's foreign-file
    /// guard). Real launchd needs HOME to compute the path at all, so tests inject this directly
    /// rather than fiddling with the environment for fakes that aren't real launchd anyway.</summary>
    readonly Func<string, string?> _readPlist = readPlist ?? (path => {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    });

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

        while (true) {
            if (manager.Query(serviceId).Probe == LabelProbe.Absent) {
                ServiceTxnMarker.Delete(serviceId);
                Say(VerifyExit.ReadinessTimeoutToken);
                return VerifyExit.ReadinessTimeout;
            }

            if (time.GetUtcNow() >= rollbackDeadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        // Reserve ran out before the restore was ever confirmed — a timeout, not an observed-wrong
        // state, so this is RollbackBudget rather than RestoreVerification (see VerifyExit).
        Say(VerifyExit.RollbackBudgetToken);
        return VerifyExit.RollbackBudget;
    }

    enum InstallReady { NotReady, Ready, VersionMismatch }

    /// <summary>install [--replace] --verify. This overload implements only the fresh path
    /// (<paramref name="replace"/> == false); <c>--replace</c> is Task 11.</summary>
    public async Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion) {
        if (replace) throw new NotImplementedException("install --replace --verify is Task 11.");

        var serviceId = spec.ServiceId;
        using var txn = ServiceTxnLock.TryAcquire(serviceId, LockWait);
        if (txn is null) {
            Say(VerifyExit.ContendedToken);
            return VerifyExit.Contended;
        }

        // A leftover marker means a prior attempt never reached a terminal state. Full resumption
        // is Task 11's job; here we only self-heal the one trivial case — a crash between writing
        // the "committed" phase and deleting the marker — and otherwise surface rather than pave
        // over an unfinished transaction.
        if (ServiceTxnMarker.Read(serviceId) is { } leftover) {
            if (leftover.Phase != "committed") {
                Say(VerifyExit.RestoreVerificationToken);
                return VerifyExit.RestoreVerification;
            }

            ServiceTxnMarker.Delete(serviceId);
        }

        if (!File.Exists(spec.DaemonBinaryPath)) {
            Say(VerifyExit.ViabilityToken);
            return VerifyExit.Viability;
        }

        var pre = manager.Query(serviceId);
        switch (pre.Probe) {
            case LabelProbe.Loaded:
                // Fresh install must not clear an existing label — that's --replace's job (Task 11).
                Say(VerifyExit.ContendedToken);
                return VerifyExit.Contended;
            case LabelProbe.Unknown:
                Say(VerifyExit.BootoutUnknownToken);
                return VerifyExit.BootoutUnknown;
        }

        var preState = DescribeQuery(pre);
        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, "install", "captured", preState, "no-unit", null));

        var plist       = manager.GenerateFiles(spec).Single().Content;
        var fingerprint = ServiceTxnMarker.Fingerprint(plist);
        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, "install", "written", preState, "no-unit", fingerprint));

        manager.WriteAndBootstrap(spec);
        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, "install", "bootstrapped", preState, "no-unit", fingerprint));

        var deadline = time.GetUtcNow() + _forwardBudget;

        while (time.GetUtcNow() < deadline) {
            var primary = await IsInstallReadyAsync(serviceId, expectedVersion, deadline - time.GetUtcNow());
            if (primary == InstallReady.VersionMismatch)
                return await InstallRollback(serviceId, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

            if (primary == InstallReady.Ready) {
                // Same floor rationale as start: confirming what the primary check just observed
                // must not be starved by the same forward deadline that gated it.
                var confirmBudget = deadline - time.GetUtcNow();
                if (confirmBudget < PollInterval) confirmBudget = PollInterval;

                var confirm = await IsInstallReadyAsync(serviceId, expectedVersion, confirmBudget);
                if (confirm == InstallReady.VersionMismatch)
                    return await InstallRollback(serviceId, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

                if (confirm == InstallReady.Ready) {
                    // Final recheck adds what start's doesn't need: the plist on disk must still be
                    // the one this transaction wrote. A foreign writer's replacement is never deleted
                    // by us — the marker and the foreign file are both left for an operator to see.
                    var onDisk = _readPlist(LaunchdUnit.PlistPath(serviceId));
                    if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) == fingerprint) {
                        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, "install", "committed", preState, "no-unit", fingerprint));
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

        return await InstallRollback(serviceId, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
    }

    /// <summary>Install's ownership + readiness + version predicate. Version mismatch is reported
    /// distinctly from "not yet ready" — it's a deterministic fact about the running daemon, not
    /// something a retry can fix, so the caller rolls back immediately rather than waiting out the
    /// forward budget.</summary>
    async Task<InstallReady> IsInstallReadyAsync(string serviceId, string? expectedVersion, TimeSpan budget) {
        if (budget <= TimeSpan.Zero) return InstallReady.NotReady;

        var h = await hello(serviceId, budget);
        if (!h.WellFormed) return InstallReady.NotReady;
        if (h.DaemonVersion != expectedVersion) return InstallReady.VersionMismatch;

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
    async Task<int> InstallRollback(string serviceId, string fingerprint, int reasonExit, string reasonToken) {
        var onDisk = _readPlist(LaunchdUnit.PlistPath(serviceId));
        if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) != fingerprint) {
            Say(VerifyExit.RestoreVerificationToken);
            return VerifyExit.RestoreVerification;
        }

        manager.Uninstall(serviceId, out var uninstallError);
        if (uninstallError is not null) Say($"uninstall: {uninstallError}");

        var rollbackDeadline = time.GetUtcNow() + _rollbackReserve;

        while (true) {
            var q = manager.Query(serviceId);
            if (q.Probe == LabelProbe.Absent && !q.UnitPresent) {
                ServiceTxnMarker.Delete(serviceId);
                Say(reasonToken);
                return reasonExit;
            }

            if (time.GetUtcNow() >= rollbackDeadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        Say(VerifyExit.RollbackBudgetToken);
        return VerifyExit.RollbackBudget;
    }
}
