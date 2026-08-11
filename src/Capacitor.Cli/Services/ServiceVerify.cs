namespace Capacitor.Cli.Services;

/// <summary>Coded, stable exit codes for the <see cref="ServiceVerify"/> transaction engine.
/// Every non-<see cref="Ok"/> member has a matching stderr token defined beside it.</summary>
public static class VerifyExit {
    public const int Ok = 0;

    /// <summary>Service lock (or, for install, the name) contended without <c>--replace</c>.</summary>
    public const int Contended = 20;
    public const string ContendedToken = "verify_contended";

    /// <summary>Reserved for install-verify's pre-mutation viability check (Task 10/11).</summary>
    public const int Viability = 21;
    public const string ViabilityToken = "verify_viability";

    /// <summary>Reserved for install-verify's replace rollback (Task 10/11).</summary>
    public const int BootoutUnknown = 22;
    public const string BootoutUnknownToken = "verify_bootout_unknown";

    /// <summary>Reserved for install-verify's replace rollback (Task 10/11).</summary>
    public const int StopUnconfirmed = 23;
    public const string StopUnconfirmedToken = "verify_stop_unconfirmed";

    /// <summary>Forward cutoff hit and rollback restored the verified-safe (unloaded) state.</summary>
    public const int ReadinessTimeout = 24;
    public const string ReadinessTimeoutToken = "verify_readiness_timeout";

    /// <summary>Reserved for install-verify's version-gated hello check (Task 10/11).</summary>
    public const int HelloValidation = 25;
    public const string HelloValidationToken = "verify_hello_validation";

    /// <summary>Reserved: rollback itself exceeded its reserve budget.</summary>
    public const int RollbackBudget = 26;
    public const string RollbackBudgetToken = "verify_rollback_budget";

    /// <summary>Rollback ran but the post-stop query still shows the service loaded — the marker
    /// is retained so an operator/resumer can see the transaction never reached a verified-safe state.</summary>
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
    TimeSpan? rollbackReserve = null) {
    static readonly TimeSpan LockWait      = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PollInterval  = TimeSpan.FromMilliseconds(500);

    readonly TimeSpan _forwardBudget    = forwardBudget ?? TimeSpan.FromSeconds(20);
    readonly TimeSpan _rollbackReserve  = rollbackReserve ?? TimeSpan.FromSeconds(10);

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

        Say(VerifyExit.RestoreVerificationToken);
        return VerifyExit.RestoreVerification;
    }

    /// <summary>install [--replace] --verify (Task 10/11).</summary>
    public Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion) =>
        throw new NotImplementedException();
}
