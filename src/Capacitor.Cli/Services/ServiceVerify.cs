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

    /// <summary>Pre-mutation viability failed before anything on disk was touched: the daemon binary
    /// is missing, the pinned profile does not resolve to a valid http/https server URL, or the plist
    /// could not be rendered (e.g. an XML-unrepresentable captured env value). Nothing is touched.</summary>
    public const int Viability = 21;
    public const string ViabilityToken = "verify_viability";

    /// <summary>Install-verify's pre-mutation probe classified the label <c>Unknown</c> — neither
    /// clearly loaded nor clearly absent, so a fresh install refuses to guess and writes nothing.</summary>
    public const int BootoutUnknown = 22;
    public const string BootoutUnknownToken = "verify_bootout_unknown";

    /// <summary>Either <c>--replace</c>'s takeover kill couldn't confirm the owner gone, or an owning
    /// takeover's cleared label never released the daemon name (its validated pid never went null),
    /// within the forward budget. Nothing is written; the marker is retained at its last phase.</summary>
    public const int StopUnconfirmed = 23;
    public const string StopUnconfirmedToken = "verify_stop_unconfirmed";

    /// <summary>Forward cutoff hit and rollback restored the verified-safe state (start: unloaded,
    /// plist retained; install: label absent AND unit file removed).</summary>
    public const int ReadinessTimeout = 24;
    public const string ReadinessTimeoutToken = "verify_readiness_timeout";

    /// <summary>Install-verify's hello was well-formed but reported a wrong name/protocol/version — a
    /// deterministic mismatch, so rollback fires without waiting out the forward budget.</summary>
    public const int HelloValidation = 25;
    public const string HelloValidationToken = "verify_hello_validation";

    /// <summary>A rollback poll ran out with the state genuinely undetermined — the LAST observation
    /// was <see cref="LabelProbe.Unknown"/>, so there is no way to tell whether the restore happened.
    /// A timeout, not an observed-wrong state (compare <see cref="RestoreVerification"/>). The marker
    /// is retained.</summary>
    public const int RollbackBudget = 26;
    public const string RollbackBudgetToken = "verify_rollback_budget";

    /// <summary>Rollback (or the final recheck) affirmatively found the wrong state: still Loaded,
    /// unloaded but the unit file still present, or a plist whose fingerprint doesn't match what this
    /// transaction wrote (a foreign writer). The marker, and any foreign file, are left alone.</summary>
    public const int RestoreVerification = 27;
    public const string RestoreVerificationToken = "verify_restore_verification";
}

/// <summary>
/// Spec §3.4 transaction engine: viability → marker → mutate → ownership+readiness poll → final
/// recheck → commit, or rollback to the verified-safe failure state. One forward cutoff bounds the
/// entire forward phase (viability, clear, write, bootstrap, readiness); a separately reserved
/// rollback budget guarantees time to restore. Injectable seams (manager, pid probe, hello probe,
/// clock, profile viability) make every case drivable without shelling out to <c>launchctl</c>.
/// </summary>
sealed class ServiceVerify(
    IVerifyServiceManager manager,
    Func<string, int?> validatedDaemonPid,
    Func<string, TimeSpan, Task<HelloProbeResult>> hello,
    TimeProvider time,
    TimeSpan? forwardBudget = null,
    TimeSpan? rollbackReserve = null,
    Func<string, string?>? readPlist = null,
    Func<string, bool>? plistExists = null,
    Func<bool>? profileViable = null,
    Action? onCommitted = null) {
    static readonly TimeSpan LockWait      = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PollInterval  = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan KillWait      = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan DefaultForwardBudget   = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultRollbackReserve = TimeSpan.FromSeconds(10);

    /// <summary>The advertised transaction bound: the forward cutoff plus the rollback reserve bound
    /// the whole mutate-then-restore envelope (30s at the defaults). A caller's kill-timeout (the
    /// desktop app's mutation timeout, §3.6) MUST sit strictly above this. Two bounded phases can
    /// precede it — lock acquisition (≤ 10s) and, only on crash residue, a recovery pre-phase
    /// (≤ the rollback reserve) — so for full headroom a caller should allow the sum, not just this.</summary>
    public static readonly TimeSpan AdvertisedBound = DefaultForwardBudget + DefaultRollbackReserve;

    readonly TimeSpan _forwardBudget    = forwardBudget ?? DefaultForwardBudget;
    readonly TimeSpan _rollbackReserve  = rollbackReserve ?? DefaultRollbackReserve;

    /// <summary>Whether the pinned profile resolves to a valid absolute http/https server URL — the
    /// CLI-side enforcement of §4.1's precondition, so a <c>--replace</c> can never destroy a working
    /// unit only to install one whose daemon exits config-invalid. Default true for the engine's own
    /// tests; the command wires the real resolution.</summary>
    readonly Func<bool> _profileViable = profileViable ?? (() => true);

    /// <summary>Install-only seam for the on-disk plist read (final recheck + rollback's foreign-file
    /// guard). Real launchd needs HOME to compute the path, so tests inject this directly.</summary>
    readonly Func<string, string?> _readPlist = readPlist ?? (path => {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    });

    /// <summary>Entry-recovery-only seam: distinguishes "absent" from "present but unreadable" when
    /// <see cref="_readPlist"/> returns null for both.</summary>
    readonly Func<string, bool> _plistExists = plistExists ?? File.Exists;

    /// <summary>Closed-stdio tolerance: the npm grandchild shares the GUI's pipes, so a broken pipe
    /// must never abort the transaction.</summary>
    static void Say(string line) {
        try { Console.Error.WriteLine(line); } catch (IOException) { }
    }

    /// <summary>Time left before <paramref name="deadline"/>, floored at zero — every launchctl call
    /// and every wait recomputes this immediately before use so the single deadline can never be
    /// overrun by accumulated elapsed time.</summary>
    TimeSpan Remaining(DateTimeOffset deadline) {
        var r = deadline - time.GetUtcNow();
        return r > TimeSpan.Zero ? r : TimeSpan.Zero;
    }

    static string DescribeQuery(ServiceQuery q) =>
        $"{q.Probe.ToString().ToLowerInvariant()}|{(q.UnitPresent ? "unit" : "nounit")}|{q.BinaryPath}|pid={q.JobPid}";

    /// <summary>start --verify: no viability check (start writes nothing). Accepts ANY well-formed
    /// hello — capability-incompatible old daemons included.</summary>
    public async Task<int> StartVerifiedAsync(string serviceId) {
        using var txn = ServiceTxnLock.TryAcquire(serviceId, LockWait);
        if (txn is null) {
            Say(VerifyExit.ContendedToken);
            return VerifyExit.Contended;
        }

        var deadline = time.GetUtcNow() + _forwardBudget;

        var pre = manager.Query(serviceId, Remaining(deadline));
        ServiceTxnMarker.Write(serviceId,
            new TxnMarker(1, "start", "captured", DescribeQuery(pre), "unloaded-plist-retained", null));

        // A false Start doesn't short-circuit — the readiness poll is the source of truth — but the
        // reason is worth surfacing if it never recovers.
        if (!manager.Start(serviceId, Remaining(deadline), out var startError) && startError is not null) Say($"start: {startError}");

        ServiceTxnMarker.Write(serviceId,
            new TxnMarker(1, "start", "bootstrapped", DescribeQuery(pre), "unloaded-plist-retained", null));

        while (time.GetUtcNow() < deadline) {
            var (ready, pid) = await IsReadyAsync(serviceId, Remaining(deadline), requirePid: null);
            if (ready) {
                // Confirm what the primary check just observed; floor at one interval so a slow-but-
                // healthy primary hello isn't rolled back on a clock technicality. The final recheck
                // requires the SAME incarnation (pinned pid) so a job that answered then respawned
                // under KeepAlive can't bless a crash-looping unit.
                var confirmBudget = Remaining(deadline);
                if (confirmBudget < PollInterval) confirmBudget = PollInterval;

                var (confirmed, _) = await IsReadyAsync(serviceId, confirmBudget, requirePid: pid);
                if (confirmed) {
                    ServiceTxnMarker.Write(serviceId,
                        new TxnMarker(1, "start", "committed", DescribeQuery(pre), "unloaded-plist-retained", null));
                    onCommitted?.Invoke();
                    ServiceTxnMarker.Delete(serviceId);
                    return VerifyExit.Ok;
                }
            }

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        return await Rollback(serviceId);
    }

    /// <summary>Ownership + readiness predicate: hello well-formed AND the freshly-queried job pid
    /// matches the validated daemon pid (both non-null). Returns the observed job pid so a caller can
    /// pin it; <paramref name="requirePid"/>, when set, demands that SAME incarnation.</summary>
    async Task<(bool Ready, int? JobPid)> IsReadyAsync(string serviceId, TimeSpan budget, int? requirePid) {
        if (budget <= TimeSpan.Zero) return (false, null);

        var h = await hello(serviceId, budget);
        if (!h.WellFormed) return (false, null);

        var jobPid    = manager.Query(serviceId, budget).JobPid;
        var daemonPid = validatedDaemonPid(serviceId);
        var owned     = jobPid is not null && daemonPid is not null && jobPid == daemonPid;
        if (!owned) return (false, jobPid);
        if (requirePid is not null && jobPid != requirePid) return (false, jobPid);
        return (true, jobPid);
    }

    /// <summary>Bootout (plist retained) then verify the restore, polled (bounded by the reserve)
    /// rather than single-shot — <c>bootout</c> can return before the label is gone. Absent → the
    /// rollback itself is the verified-safe state, marker deleted. Still loaded at reserve expiry →
    /// marker kept so a resumer can see the transaction never settled.</summary>
    async Task<int> Rollback(string serviceId) {
        var deadline = time.GetUtcNow() + _rollbackReserve;

        manager.Stop(serviceId, Remaining(deadline), out var stopError);
        if (stopError is not null) Say($"stop: {stopError}");

        LabelProbe lastProbe;
        while (true) {
            lastProbe = manager.Query(serviceId, Remaining(deadline)).Probe;
            if (lastProbe == LabelProbe.Absent) {
                ServiceTxnMarker.Delete(serviceId);
                Say(VerifyExit.ReadinessTimeoutToken);
                return VerifyExit.ReadinessTimeout;
            }

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        // Unknown = a genuine timeout (couldn't tell); still Loaded = an affirmatively wrong state.
        if (lastProbe == LabelProbe.Unknown) {
            Say(VerifyExit.RollbackBudgetToken);
            return VerifyExit.RollbackBudget;
        }

        Say(VerifyExit.RestoreVerificationToken);
        return VerifyExit.RestoreVerification;
    }

    enum InstallReady { NotReady, Ready, VersionMismatch }

    /// <summary>install [--replace] --verify. <paramref name="replace"/> selects the ownership matrix
    /// (spec §3.4): a fresh install refuses to touch an existing label/unit
    /// (<see cref="VerifyExit.Contended"/>), while <c>--replace</c> clears/takes it over first.</summary>
    public async Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion) {
        var serviceId = spec.ServiceId;
        var op        = replace ? "replace" : "install";

        using var txn = ServiceTxnLock.TryAcquire(serviceId, LockWait);
        if (txn is null) {
            Say(VerifyExit.ContendedToken);
            return VerifyExit.Contended;
        }

        // Viability is proven BEFORE any destructive step (recovery, marker, bootout, kill) so
        // VerifyExit.Viability's "nothing is touched" contract holds even for --replace: a missing
        // binary, an invalid pinned-profile URL, or a plist that cannot be rendered (e.g. an
        // XML-unrepresentable captured env value) must abort before the old setup is ever cleared.
        if (!File.Exists(spec.DaemonBinaryPath)) {
            Say(VerifyExit.ViabilityToken);
            return VerifyExit.Viability;
        }

        if (!_profileViable()) {
            Say(VerifyExit.ViabilityToken);
            return VerifyExit.Viability;
        }

        GeneratedFile generated;
        try {
            generated = manager.GenerateFiles(spec).Single();
        } catch (Exception ex) {
            Say($"{op}: {ex.Message}");
            Say(VerifyExit.ViabilityToken);
            return VerifyExit.Viability;
        }
        var fingerprint = ServiceTxnMarker.Fingerprint(generated.Content);

        // A leftover marker means a prior attempt never reached a terminal state. "committed" is just a
        // crash between writing that phase and deleting the marker — self-heal. Any other phase may
        // have left residue; recovery authority is scoped by CONTENT (see RecoverLeftoverMarker).
        if (ServiceTxnMarker.Read(serviceId) is { } leftover) {
            if (leftover.Phase == "committed") {
                ServiceTxnMarker.Delete(serviceId);
            } else if (await RecoverLeftoverMarker(serviceId, generated.Path, leftover) is { } recoveryExit) {
                return recoveryExit;
            }
        }

        // ── forward phase: one cutoff shared by pre-query, the matrix, write, bootstrap, readiness ──
        var forward = time.GetUtcNow() + _forwardBudget;

        var pre = manager.Query(serviceId, Remaining(forward));
        if (pre.Probe == LabelProbe.Unknown) {
            Say(VerifyExit.BootoutUnknownToken);
            return VerifyExit.BootoutUnknown;
        }

        var preState = DescribeQuery(pre);

        if (!replace) {
            if (pre.Probe == LabelProbe.Loaded || (pre.Probe == LabelProbe.Absent && pre.UnitPresent)) {
                // Loaded, or stopped-but-installed (`service stop` retains the plist). A fresh install
                // must not overwrite or clear an existing unit — that's --replace's job.
                Say(VerifyExit.ContendedToken);
                return VerifyExit.Contended;
            }
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "captured", preState, "no-unit", null));
        } else {
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "captured", preState, "no-unit", null));
            if (await ApplyReplaceMatrixAsync(serviceId, pre, preState, op, forward) is { } stopExit) {
                return stopExit;
            }
        }

        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "written", preState, "no-unit", fingerprint));

        try {
            manager.WriteAndBootstrap(spec, Remaining(forward));
        } catch (Exception ex) {
            // WriteAndBootstrap writes the unit before it bootstraps — a throw here can still leave the
            // plist on disk, so route through the same fingerprint-gated rollback.
            Say($"{op}: {ex.Message}");
            return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
        }

        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "bootstrapped", preState, "no-unit", fingerprint));

        while (time.GetUtcNow() < forward) {
            var (primary, pinnedPid) = await IsInstallReadyAsync(serviceId, expectedVersion, Remaining(forward), requirePid: null);
            if (primary == InstallReady.VersionMismatch)
                return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

            if (primary == InstallReady.Ready) {
                var confirmBudget = Remaining(forward);
                if (confirmBudget < PollInterval) confirmBudget = PollInterval;

                // Final recheck requires the SAME incarnation (pinnedPid): a job that answered then
                // exited and let a KeepAlive respawn take over must not commit a crash-looping unit.
                var (confirm, _) = await IsInstallReadyAsync(serviceId, expectedVersion, confirmBudget, requirePid: pinnedPid);
                if (confirm == InstallReady.VersionMismatch)
                    return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

                if (confirm == InstallReady.Ready) {
                    // The plist on disk must still be the one this transaction wrote — a lock-unaware
                    // old CLI can overwrite it under a healthy job, and a PID check would miss that.
                    var onDisk = _readPlist(generated.Path);
                    if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) == fingerprint) {
                        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "committed", preState, "no-unit", fingerprint));
                        onCommitted?.Invoke();
                        ServiceTxnMarker.Delete(serviceId);
                        return VerifyExit.Ok;
                    }

                    Say(VerifyExit.RestoreVerificationToken);
                    return VerifyExit.RestoreVerification;
                }
            }

            if (time.GetUtcNow() >= forward) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
    }

    /// <summary>
    /// Entry-time recovery for a leftover marker whose phase is not "committed". Recovery authority is
    /// scoped by CONTENT: only a plist whose fingerprint matches what the dead transaction recorded is
    /// provably its own residue and safe to clean; everything else is surfaced
    /// (<see cref="VerifyExit.RestoreVerification"/>). Returns null when it is safe to proceed (marker
    /// already deleted); otherwise the exit code to return immediately (marker left untouched).
    /// </summary>
    async Task<int?> RecoverLeftoverMarker(string serviceId, string onDiskPath, TxnMarker leftover) {
        if (leftover.PlistFingerprint is null) {
            // Died before ever writing a plist — nothing on disk to clean up.
            ServiceTxnMarker.Delete(serviceId);
            return null;
        }

        var onDisk = _readPlist(onDiskPath);
        if (onDisk is null) {
            if (_plistExists(onDiskPath)) {
                // Present but unreadable is NOT absent — the file exists and was never fingerprint-
                // compared, so never guess it's our own gone residue.
                Say(VerifyExit.RestoreVerificationToken);
                return VerifyExit.RestoreVerification;
            }
            ServiceTxnMarker.Delete(serviceId); // confirmed absent — residue already gone
            return null;
        }

        if (ServiceTxnMarker.Fingerprint(onDisk) != leftover.PlistFingerprint) {
            // A foreign writer touched the file after the death — surface, never pave.
            Say(VerifyExit.RestoreVerificationToken);
            return VerifyExit.RestoreVerification;
        }

        // Our own residue: clear it via the SAME confirmed-gone poll the matrix uses — a bare Uninstall
        // bool on a bootout exit 0 does not itself prove the label/file are gone.
        if (await ClearLabelAsync(serviceId, time.GetUtcNow() + _rollbackReserve) is { } clearExit) return clearExit;

        ServiceTxnMarker.Delete(serviceId);
        return null;
    }

    /// <summary>
    /// Uninstall (clear the label/plist) and poll — bounded by <paramref name="deadline"/> — until BOTH
    /// the label is <see cref="LabelProbe.Absent"/> AND the unit file is gone. A <c>bootout</c> that
    /// fails-then-retains the plist leaves the label unloading while the file lingers; when the label
    /// later reads Absent, re-attempt the Uninstall to delete the now-unloaded file rather than
    /// declaring an orphan-plist state a clean clear. Returns null once both hold, else the terminal
    /// exit code (nothing further is written).
    /// </summary>
    async Task<int?> ClearLabelAsync(string serviceId, DateTimeOffset deadline) {
        manager.Uninstall(serviceId, Remaining(deadline), out var error);
        if (error is not null) Say($"uninstall: {error}");

        ServiceQuery last;
        while (true) {
            last = manager.Query(serviceId, Remaining(deadline));
            if (last.Probe == LabelProbe.Absent) {
                if (!last.UnitPresent) return null;                     // label gone AND file gone
                // Label unloaded but plist retained by a failed bootout — delete the now-unloaded unit.
                manager.Uninstall(serviceId, Remaining(deadline), out var reError);
                if (reError is not null) Say($"uninstall: {reError}");
            }

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        // Unknown = a genuine timeout; still Loaded, or Absent-but-file-present, is affirmatively wrong.
        var reason = last.Probe == LabelProbe.Unknown ? VerifyExit.RollbackBudget : VerifyExit.RestoreVerification;
        Say(last.Probe == LabelProbe.Unknown ? VerifyExit.RollbackBudgetToken : VerifyExit.RestoreVerificationToken);
        return reason;
    }

    /// <summary>
    /// install --replace's ownership matrix (spec §3.4). Called after the pre-mutation probe was
    /// rejected as <see cref="LabelProbe.Unknown"/>, with the "captured" marker on disk. Everything it
    /// does draws from the shared forward <paramref name="deadline"/>. Returns a non-null exit code
    /// when a clear/kill couldn't be confirmed (marker retained at its last phase); otherwise falls
    /// through to the shared write+bootstrap+verify tail.
    /// </summary>
    async Task<int?> ApplyReplaceMatrixAsync(string serviceId, ServiceQuery pre, string preState, string op, DateTimeOffset deadline) {
        var validatedPid = validatedDaemonPid(serviceId);
        var owning       = pre.Probe == LabelProbe.Loaded && pre.JobPid is not null && pre.JobPid == validatedPid;

        if (owning) {
            // The label's own bootout terminates the process it owns — no separate kill needed. But the
            // old job may still be terminating and holding the name lock, so confirm its validated pid
            // is gone before writing/bootstrapping the replacement, else the new job hits a
            // deliberate-refusal exit and the replacement spuriously fails.
            if (await ClearLabelAsync(serviceId, deadline) is { } clearExit) return clearExit;
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "label-cleared", preState, "no-unit", null));

            if (!await WaitForPidGoneAsync(serviceId, deadline)) {
                Say(VerifyExit.StopUnconfirmedToken);
                return VerifyExit.StopUnconfirmed;
            }
            return null;
        }

        if (pre.Probe == LabelProbe.Loaded || pre.UnitPresent) {
            // A non-owning/orphan label, or a stopped-but-installed unit — --replace may clear it.
            if (await ClearLabelAsync(serviceId, deadline) is { } clearExit) return clearExit;
            ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "label-cleared", preState, "no-unit", null));
        }

        // Re-read AFTER any clearing: bootout can terminate the true owner as a side effect of
        // unloading its label, so the pre-clear pid may be stale — kill the validated owner only if one
        // still remains.
        var liveOwner = validatedDaemonPid(serviceId);
        if (liveOwner is null) return null;

        if (!DaemonKill.KillValidatedOwner(serviceId, liveOwner.Value, KillWait))
            Say($"replace: kill of validated owner (PID {liveOwner}) did not confirm gone immediately");

        if (!await WaitForStopConfirmedAsync(serviceId, deadline)) {
            Say(VerifyExit.StopUnconfirmedToken);
            return VerifyExit.StopUnconfirmed;
        }

        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "owner-stopped", preState, "no-unit", null));
        return null;
    }

    /// <summary>Poll (bounded by <paramref name="deadline"/>) until the validated daemon pid is null —
    /// the owning job actually exited and released the name lock.</summary>
    async Task<bool> WaitForPidGoneAsync(string serviceId, DateTimeOffset deadline) {
        while (true) {
            if (validatedDaemonPid(serviceId) is null) return true;
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }
    }

    /// <summary>Stop-confirmation for --replace's takeover kill: gone means the validated pid is null
    /// AND a fresh hello dial is not well-formed — a hung-but-still-answering process is not stopped.</summary>
    async Task<bool> IsStoppedAsync(string serviceId, TimeSpan budget) {
        if (budget <= TimeSpan.Zero) return false;

        var h = await hello(serviceId, budget);
        if (h.WellFormed) return false;

        return validatedDaemonPid(serviceId) is null;
    }

    async Task<bool> WaitForStopConfirmedAsync(string serviceId, DateTimeOffset deadline) {
        while (time.GetUtcNow() < deadline) {
            if (await IsStoppedAsync(serviceId, Remaining(deadline))) return true;
            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }
        return false;
    }

    /// <summary>Install's ownership + readiness + protocol/name/version predicate. Name, protocol, and
    /// version are deterministic facts a retry can't fix, so each reports the same
    /// <see cref="InstallReady.VersionMismatch"/> the caller rolls back on immediately. Returns the
    /// observed job pid so the caller can pin it; <paramref name="requirePid"/>, when set, demands that
    /// SAME incarnation.</summary>
    async Task<(InstallReady Status, int? JobPid)> IsInstallReadyAsync(string serviceId, string? expectedVersion, TimeSpan budget, int? requirePid) {
        if (budget <= TimeSpan.Zero) return (InstallReady.NotReady, null);

        var h = await hello(serviceId, budget);
        if (!h.WellFormed) return (InstallReady.NotReady, null);
        if (h.DaemonName != serviceId) return (InstallReady.VersionMismatch, null);
        if (h.ProtocolVersion != HelloProtocol.CurrentVersion) return (InstallReady.VersionMismatch, null);
        if (expectedVersion is not null && h.DaemonVersion != expectedVersion) return (InstallReady.VersionMismatch, null);

        var jobPid    = manager.Query(serviceId, budget).JobPid;
        var daemonPid = validatedDaemonPid(serviceId);
        var owned     = jobPid is not null && daemonPid is not null && jobPid == daemonPid;
        if (!owned) return (InstallReady.NotReady, jobPid);
        if (requirePid is not null && jobPid != requirePid) return (InstallReady.NotReady, jobPid);
        return (InstallReady.Ready, jobPid);
    }

    /// <summary>Rollback for the fresh-install path: uninstall OUR unit and verify the restored state
    /// is label-absent AND file-gone (unlike start, which retains the plist), bounded by the reserve. A
    /// foreign plist (fingerprint mismatch) is never touched — checked before the uninstall.</summary>
    async Task<int> InstallRollback(string serviceId, string plistPath, string fingerprint, int reasonExit, string reasonToken) {
        var onDisk = _readPlist(plistPath);
        if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) != fingerprint) {
            Say(VerifyExit.RestoreVerificationToken);
            return VerifyExit.RestoreVerification;
        }

        var deadline = time.GetUtcNow() + _rollbackReserve;

        manager.Uninstall(serviceId, Remaining(deadline), out var uninstallError);
        if (uninstallError is not null) Say($"uninstall: {uninstallError}");

        ServiceQuery last;
        while (true) {
            last = manager.Query(serviceId, Remaining(deadline));
            if (last.Probe == LabelProbe.Absent && !last.UnitPresent) {
                ServiceTxnMarker.Delete(serviceId);
                Say(reasonToken);
                return reasonExit;
            }

            if (time.GetUtcNow() >= deadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        // Unknown = a genuine timeout; still Loaded, or unloaded-with-file-present, is affirmatively wrong.
        if (last.Probe == LabelProbe.Unknown) {
            Say(VerifyExit.RollbackBudgetToken);
            return VerifyExit.RollbackBudget;
        }

        Say(VerifyExit.RestoreVerificationToken);
        return VerifyExit.RestoreVerification;
    }
}
