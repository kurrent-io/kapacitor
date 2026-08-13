using Capacitor.Cli.Core.Config;
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

    /// <summary>start --verify's pre-mutation consent-directive gate (Task 16, AI-1655) refused to
    /// proceed: the invoking launcher set <c>KCAP_CONSENT_SEED_DEFAULT</c> but the installed unit's
    /// own baked directive, binary digest, or identity evidence didn't satisfy the gate. Fires right
    /// after the fresh query and BEFORE the marker is even written — nothing is touched. The stderr
    /// line <c>start_gate_reason=&lt;reason&gt;</c> names which check failed.</summary>
    public const int StartGate = 28;
    public const string StartGateToken = "verify_start_gate";

    /// <summary>The gated start path detected drift between the evidence Phase A gated on and what
    /// bootstrap was about to act on — the unit's plist content changed, or its digest stopped
    /// matching, in the window between the gate check and the boot-out/bootstrap it authorizes.
    /// Rolled back the same way a forward-phase failure is (bootout, plist retained).</summary>
    public const int StartGateDrift = 29;
    public const string StartGateDriftToken = "verify_start_gate_drift";
}

/// <summary>Why <see cref="ServiceVerify.EvaluateStartGate"/> refused a gated start. Reported on
/// stderr as <c>start_gate_reason=&lt;snake_case&gt;</c> beside <see cref="VerifyExit.StartGateToken"/>.</summary>
internal enum StartGateReason { DirectiveMissing, DirectiveInvalid, IdentityMismatch, ForeignBinary, PackageInconsistent, EvidenceUnreadable }

/// <summary>
/// Spec §3.4 transaction engine: viability → marker → mutate → ownership+readiness poll → final
/// recheck → commit, or rollback to the verified-safe failure state. One forward cutoff bounds the
/// entire forward phase (viability, clear, write, bootstrap, readiness AND the final recheck); a
/// separately reserved rollback budget guarantees time to restore. Injectable seams (manager, pid
/// probe, hello probe, clock, profile viability) make every case drivable without shelling out to
/// <c>launchctl</c>.
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
    Action? onCommitted = null,
    Func<string, string?>? gateEnv = null,
    Func<string, bool>? digestMatches = null) {
    static readonly TimeSpan LockWait      = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PollInterval  = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan KillWait      = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan DefaultForwardBudget   = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultRollbackReserve = TimeSpan.FromSeconds(10);

    /// <summary>The advertised transaction bound (30s at the defaults): forward cutoff + rollback
    /// reserve. A caller's kill-timeout (the desktop app's mutation timeout, §3.6) MUST sit strictly
    /// above this. Two bounded phases can precede it — lock acquisition (≤ 10s) and, only on crash
    /// residue, a recovery pre-phase (≤ the rollback reserve) — so for full headroom a caller should
    /// allow the sum. The one accepted exception is <see cref="KillWait"/> (≤ 5s) on the manual-owner
    /// takeover kill, whose raw wait sits just outside the forward envelope but well within the
    /// caller's 60s kill-timeout.</summary>
    public static readonly TimeSpan AdvertisedBound = DefaultForwardBudget + DefaultRollbackReserve;

    readonly TimeSpan _forwardBudget    = forwardBudget ?? DefaultForwardBudget;
    readonly TimeSpan _rollbackReserve  = rollbackReserve ?? DefaultRollbackReserve;

    /// <summary>A small slice reserved INSIDE the forward budget for the final recheck, so a primary
    /// readiness observation that lands right at the poll cutoff still gets one confirmation probe
    /// without pushing total forward work (poll + recheck) past the single forward deadline. Capped
    /// at half the forward budget so a pathologically small budget still leaves a poll window.</summary>
    readonly TimeSpan _confirmReserve   = ConfirmReserveFor(forwardBudget ?? DefaultForwardBudget);

    static TimeSpan ConfirmReserveFor(TimeSpan forward) {
        var reserve = TimeSpan.FromTicks(Math.Min((2 * PollInterval).Ticks, (forward / 2).Ticks));
        return reserve > TimeSpan.Zero ? reserve : forward;
    }

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

    /// <summary>Task 16 (AI-1655) seam: when non-null AND <c>gateEnv(ConsentSeedVar)</c> is
    /// non-empty, <see cref="StartVerifiedAsync"/> runs the pre-mutation gate (Phase A) and the
    /// TOCTOU re-check before bootstrap (Phase B). Null (the default) leaves start completely
    /// behavior-unchanged — the production caller in <c>DaemonCommands</c> passes
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
    readonly Func<string, string?>? _gateEnv = gateEnv;

    /// <summary>Digest seam for the gate's foreign/package-inconsistent check and Phase B's
    /// TOCTOU re-check. Defaults to <see cref="DaemonDigest.Matches"/>; tests that only care about
    /// the identity half of the gate inject a trivial pass here since the real digest is a
    /// fail-closed placeholder in dev/test builds.</summary>
    readonly Func<string, bool> _digestMatches = digestMatches ?? DaemonDigest.Matches;

    const string ConsentSeedVar = "KCAP_CONSENT_SEED_DEFAULT";
    const string ProfileVar     = "KCAP_PROFILE";
    const string UrlVar         = "KCAP_URL";
    const string ExpectVar      = "KCAP_EXPECT_SERVER_URL";
    const string ConfigDirVar   = "KCAP_CONFIG_DIR";

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

        // Task 16 (AI-1655): gated ONLY when the invoking launcher itself carries the consent-seed
        // directive — a bare `kcap daemon service start --verify` typed at a terminal never sees
        // this branch. Phase A runs right here (after the fresh query, before ANY write) so a
        // rejected gate leaves absolutely nothing touched — not even the marker.
        var gated = _gateEnv is not null && !string.IsNullOrEmpty(_gateEnv(ConsentSeedVar));
        string? phaseAPlistContent = null;

        if (gated) {
            var plistPath = LaunchdUnit.PlistPath(serviceId);
            phaseAPlistContent = _readPlist(plistPath);

            // A malformed/truncated plist (exactly the foreign-writer race Phase B defends
            // against, caught here instead) must not let XDocument.Parse's XmlException escape
            // this method — that would abort to a generic, uncoded exit 1 instead of the gate's
            // own contract. Unreadable evidence is EvidenceUnreadable, same as every other
            // evidence read this gate performs.
            StartGateReason? reason;
            try {
                var unitEnv = phaseAPlistContent is not null ? LaunchdUnit.EnvFromPlist(phaseAPlistContent) : new Dictionary<string, string>();
                var unitBinaryPath = phaseAPlistContent is not null ? LaunchdUnit.BinaryFromPlist(phaseAPlistContent) : null;
                reason = EvaluateStartGate(unitEnv, unitBinaryPath, ResolveInstallBinaryPath(), _gateEnv!, _digestMatches);
            } catch {
                reason = StartGateReason.EvidenceUnreadable;
            }

            if (reason is { } r) {
                Say($"start_gate_reason={GateReasonToken(r)}");
                Say(VerifyExit.StartGateToken);
                return VerifyExit.StartGate;
            }
        }

        ServiceTxnMarker.Write(serviceId,
            new TxnMarker(1, "start", "captured", DescribeQuery(pre), "unloaded-plist-retained", null));

        if (gated) {
            // Phase B: the gated path never kickstarts a loaded label the way the ungated Start()
            // below would — it boots it out first, then re-reads the plist and re-checks the digest
            // ONE more time immediately before bootstrap. That closes the TOCTOU window between
            // Phase A's read and this mutation: anything that changed the evidence in between (a
            // foreign writer, a swapped binary) must not ride the gate's earlier pass into a start.
            if (pre.Probe == LabelProbe.Loaded) {
                manager.Stop(serviceId, Remaining(deadline), out var bootOutError);
                if (bootOutError is not null) Say($"stop: {bootOutError}");
            }

            var plistPath      = LaunchdUnit.PlistPath(serviceId);
            var recheckContent = _readPlist(plistPath);

            // Same XmlException hazard as Phase A's parse, but here escaping is worse: it would
            // abort AFTER the marker write and boot-out, past the point Rollback runs, leaving the
            // service stopped with a stuck marker instead of the guaranteed unloaded-plist-retained
            // outcome. A recheck that can't be parsed/validated is exactly what drift means — the
            // content changed (to something unreadable) or can no longer be confirmed unchanged —
            // so route it into the same drift branch rather than letting it throw.
            bool digestStillGood;
            try {
                var recheckBinary = recheckContent is not null ? LaunchdUnit.BinaryFromPlist(recheckContent) : null;
                digestStillGood = recheckBinary is not null && _digestMatches(recheckBinary);
            } catch {
                digestStillGood = false;
            }

            if (recheckContent != phaseAPlistContent || !digestStillGood) {
                ServiceTxnMarker.Write(serviceId,
                    new TxnMarker(1, "start", "gate-drift", DescribeQuery(pre), "unloaded-plist-retained", null));
                await Rollback(serviceId);
                Say(VerifyExit.StartGateDriftToken);
                return VerifyExit.StartGateDrift;
            }
        }

        // A false Start doesn't short-circuit — the readiness poll is the source of truth — but the
        // reason is worth surfacing if it never recovers.
        if (!manager.Start(serviceId, Remaining(deadline), out var startError) && startError is not null) Say($"start: {startError}");

        ServiceTxnMarker.Write(serviceId,
            new TxnMarker(1, "start", "bootstrapped", DescribeQuery(pre), "unloaded-plist-retained", null));

        var pollDeadline = deadline - _confirmReserve;

        while (time.GetUtcNow() < pollDeadline) {
            var (ready, pid) = await IsReadyAsync(serviceId, pollDeadline, requirePid: null);
            if (ready) {
                // Recheck against the ORIGINAL deadline, pinning pid: a job that answered then
                // respawned under KeepAlive must not bless a crash-looping unit.
                var (confirmed, _) = await IsReadyAsync(serviceId, deadline, requirePid: pid);
                if (confirmed) {
                    ServiceTxnMarker.Write(serviceId,
                        new TxnMarker(1, "start", "committed", DescribeQuery(pre), "unloaded-plist-retained", null));
                    onCommitted?.Invoke();
                    ServiceTxnMarker.Delete(serviceId);
                    return VerifyExit.Ok;
                }
            }

            if (time.GetUtcNow() >= pollDeadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        return await Rollback(serviceId);
    }

    /// <summary>
    /// Task 16 (AI-1655): the pure decision core of the gated start path. Given the environment
    /// baked into the installed unit and this invocation's own environment, decides whether a
    /// gated start may proceed. Order of checks (first hit wins): the invocation itself never asked
    /// to be gated → null (pass); the unit lacks the directive the invocation carries →
    /// <see cref="StartGateReason.DirectiveMissing"/>; the unit's directive isn't exactly
    /// <c>"prompt"</c> → <see cref="StartGateReason.DirectiveInvalid"/>; the unit's baked binary
    /// doesn't hash-match this CLI build's embedded digest → <see cref="StartGateReason.PackageInconsistent"/>
    /// (same path as this install's own daemon binary — a broken/stale package, not a hijack) or
    /// <see cref="StartGateReason.ForeignBinary"/> (a different path entirely); the unit's resolved
    /// server identity disagrees with itself or with this invocation's expectation →
    /// <see cref="StartGateReason.IdentityMismatch"/>. Any evidence read that throws (a malformed
    /// plist fragment, an unreadable config file) reports <see cref="StartGateReason.EvidenceUnreadable"/>
    /// rather than letting the exception escape — pure and total over its inputs.
    /// </summary>
    internal static StartGateReason? EvaluateStartGate(
            IReadOnlyDictionary<string, string> unitEnv, string? unitBinaryPath,
            string? installBinaryPath, Func<string, string?> env, Func<string, bool>? digestMatches = null) {
        if (string.IsNullOrEmpty(env(ConsentSeedVar))) return null; // this invocation never asked to be gated

        if (!unitEnv.TryGetValue(ConsentSeedVar, out var unitDirective) || string.IsNullOrEmpty(unitDirective))
            return StartGateReason.DirectiveMissing;

        if (unitDirective != "prompt")
            return StartGateReason.DirectiveInvalid;

        var matches = digestMatches ?? DaemonDigest.Matches;

        try {
            if (unitBinaryPath is null) return StartGateReason.EvidenceUnreadable;

            if (!matches(unitBinaryPath)) {
                var samePath = installBinaryPath is not null && string.Equals(
                    Path.GetFullPath(unitBinaryPath), Path.GetFullPath(installBinaryPath), StringComparison.OrdinalIgnoreCase);
                return samePath ? StartGateReason.PackageInconsistent : StartGateReason.ForeignBinary;
            }
        } catch {
            return StartGateReason.EvidenceUnreadable;
        }

        try {
            return EvaluateIdentity(unitEnv, env);
        } catch {
            return StartGateReason.EvidenceUnreadable;
        }
    }

    /// <summary>
    /// Identity half of <see cref="EvaluateStartGate"/>, split out so its own try/catch stays small.
    /// The unit's effective identity is its baked <c>KCAP_URL</c> (precedence) or, absent that, its
    /// baked profile's <c>server_url</c> looked up via <see cref="ConfigMutator.LoadPure"/> under the
    /// unit's own baked <c>KCAP_CONFIG_DIR</c> (or the default config root when it baked none). The
    /// stale-pin rule: the unit's OWN baked <c>KCAP_EXPECT_SERVER_URL</c>, its resolved identity, and
    /// this invocation's expected server URL must all agree once present — not just any one pair of
    /// them — so a unit whose baked expectation no longer matches what it actually resolves to is
    /// caught even when a fresh invocation's expectation happens to match one side of that split.
    /// Absent evidence makes no assertion; this is drift detection, not a requirement that every
    /// field be pinned. Profile identity is compared by exact name, server identity through
    /// <see cref="AppConfig.NormalizeUrl"/> case-insensitively.
    /// </summary>
    static StartGateReason? EvaluateIdentity(IReadOnlyDictionary<string, string> unitEnv, Func<string, string?> env) {
        unitEnv.TryGetValue(ProfileVar, out var unitProfile);
        unitEnv.TryGetValue(UrlVar, out var unitUrl);
        unitEnv.TryGetValue(ExpectVar, out var unitExpect);

        var envProfile = env(ProfileVar);
        var envExpect  = env(ExpectVar);

        if (!string.IsNullOrEmpty(envProfile) && !string.IsNullOrEmpty(unitProfile)
            && !string.Equals(envProfile, unitProfile, StringComparison.Ordinal))
            return StartGateReason.IdentityMismatch;

        var unitResolved = !string.IsNullOrEmpty(unitUrl) ? unitUrl : BakedProfileServerUrl(unitEnv, unitProfile);

        string? canonical = null;
        foreach (var candidate in new[] { unitResolved, unitExpect, envExpect }) {
            if (string.IsNullOrEmpty(candidate)) continue;
            var normalized = AppConfig.NormalizeUrl(candidate);
            if (canonical is null) { canonical = normalized; continue; }
            if (!string.Equals(canonical, normalized, StringComparison.OrdinalIgnoreCase))
                return StartGateReason.IdentityMismatch;
        }

        return null;
    }

    /// <summary>The <c>KCAP_URL</c>-absent fallback for <see cref="EvaluateIdentity"/> — mirrors
    /// <c>DaemonCommands.BakedProfileServerUrl</c> (the same lookup <c>service status --json</c>
    /// does for its UX-only evidence fields), duplicated here so the pure gate stays self-contained.</summary>
    static string? BakedProfileServerUrl(IReadOnlyDictionary<string, string> unitEnv, string? profile) {
        if (string.IsNullOrEmpty(profile)) return null;
        var configPath = unitEnv.TryGetValue(ConfigDirVar, out var dir) && !string.IsNullOrEmpty(dir)
            ? Path.Combine(dir, "config.json")
            : AppConfig.GetConfigPath();
        var config = ConfigMutator.LoadPure(configPath);
        return config.Profiles.TryGetValue(profile, out var p) ? p.ServerUrl : null;
    }

    /// <summary>The daemon binary this CLI build would itself install — mirrors
    /// <c>DaemonCommands.ResolveDaemonBinary</c> (duplicated rather than shared across the two
    /// classes, same precedent as this file's already-duplicated verify exit codes).</summary>
    static string? ResolveInstallBinaryPath() {
        var ext     = OperatingSystem.IsWindows() ? ".exe" : "";
        var sibling = Path.Combine(AppContext.BaseDirectory, $"kcap-daemon{ext}");
        return File.Exists(sibling) ? sibling : null;
    }

    static string GateReasonToken(StartGateReason reason) => reason switch {
        StartGateReason.DirectiveMissing     => "directive_missing",
        StartGateReason.DirectiveInvalid     => "directive_invalid",
        StartGateReason.IdentityMismatch     => "identity_mismatch",
        StartGateReason.ForeignBinary        => "foreign_binary",
        StartGateReason.PackageInconsistent  => "package_inconsistent",
        _                                    => "evidence_unreadable"
    };

    /// <summary>Ownership + readiness predicate: hello well-formed AND the freshly-queried job pid
    /// matches the validated daemon pid (both non-null). Returns the observed job pid so a caller can
    /// pin it; <paramref name="requirePid"/>, when set, demands that SAME incarnation. Both bounded
    /// sub-calls draw from ONE absolute <paramref name="deadline"/>, recomputed immediately before
    /// each: a late-but-well-formed hello cannot then hand the Query a second full budget.</summary>
    async Task<(bool Ready, int? JobPid)> IsReadyAsync(string serviceId, DateTimeOffset deadline, int? requirePid) {
        if (Remaining(deadline) <= TimeSpan.Zero) return (false, null);

        var h = await hello(serviceId, Remaining(deadline));
        if (!h.WellFormed) return (false, null);

        if (Remaining(deadline) <= TimeSpan.Zero) return (false, null);
        var jobPid    = manager.Query(serviceId, Remaining(deadline)).JobPid;
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

    /// <summary>Task 17 (AI-1655) seam: the gated install/replace path's digest recheck, shared by
    /// the viability arm and both TOCTOU rechecks below. An exception hashing the binary (a race
    /// with a package manager mid-replace, a permissions error) is treated as failure — Task 16's
    /// unreadable-evidence-at-a-recheck-is-drift precedent — never let it escape uncaught.</summary>
    bool DigestStillGood(string binaryPath) {
        try { return _digestMatches(binaryPath); }
        catch { return false; }
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

        // Task 17 (AI-1655): gated ONLY when the invoking launcher itself carries the consent-seed
        // directive — a bare `kcap daemon service install/replace --verify` typed at a terminal
        // never sees any of this task's checks. Computed once, up front, so the viability arm and
        // both rechecks below share one activation decision (mirrors StartVerifiedAsync's `gated`).
        var gated = _gateEnv is not null && !string.IsNullOrEmpty(_gateEnv(ConsentSeedVar));

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

        // Task 17: the binary about to be installed must still hash-match this CLI build's own
        // embedded digest — same "package_inconsistent" failure mode the gated start path already
        // reports, but here it's a viability abort (nothing written yet, no marker) rather than a
        // rollback. One stderr line naming the reason; no separate generic viability token.
        // Deliberate conflation: DigestStillGood's catch also reports an unreadable/unhashable
        // binary as package_inconsistent here, where EvaluateStartGate would separately bucket
        // that as EvidenceUnreadable — install has no third bucket, and either way the binary
        // about to be installed cannot be trusted, so it's still a viability abort.
        if (gated && !DigestStillGood(spec.DaemonBinaryPath)) {
            Say($"viability_reason={GateReasonToken(StartGateReason.PackageInconsistent)}");
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

        // Task 17: TOCTOU re-check immediately before the mutation viability's digest check
        // authorized — the same binary content could have been swapped (a foreign writer, a broken
        // mid-upgrade package) in the window between that check and this bootstrap. Drift here rolls
        // back through the SAME fingerprint-gated machinery a bootstrap throw does, just with the
        // gate's own exit code/token instead of ReadinessTimeout's.
        if (gated && !DigestStillGood(spec.DaemonBinaryPath))
            return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.StartGateDrift, VerifyExit.StartGateDriftToken);

        try {
            manager.WriteAndBootstrap(spec, Remaining(forward));
        } catch (Exception ex) {
            // WriteAndBootstrap writes the unit before it bootstraps — a throw here can still leave the
            // plist on disk, so route through the same fingerprint-gated rollback.
            Say($"{op}: {ex.Message}");
            return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
        }

        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "bootstrapped", preState, "no-unit", fingerprint));

        var pollDeadline = forward - _confirmReserve;

        while (time.GetUtcNow() < pollDeadline) {
            var (primary, pinnedPid) = await IsInstallReadyAsync(serviceId, expectedVersion, pollDeadline, requirePid: null);
            if (primary == InstallReady.VersionMismatch)
                return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

            if (primary == InstallReady.Ready) {
                // Recheck against the ORIGINAL forward cutoff, pinning pid (same incarnation).
                var (confirm, _) = await IsInstallReadyAsync(serviceId, expectedVersion, forward, requirePid: pinnedPid);
                if (confirm == InstallReady.VersionMismatch)
                    return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

                if (confirm == InstallReady.Ready) {
                    // The plist on disk must still be the one this transaction wrote — a lock-unaware
                    // old CLI can overwrite it under a healthy job, and a PID check would miss that.
                    var onDisk = _readPlist(generated.Path);
                    if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) == fingerprint) {
                        // Task 17: the final gated recheck, joined onto this existing on-disk
                        // recheck rather than a separate poll step — catches a binary swap that
                        // landed AFTER bootstrap started the job (the pre-bootstrap recheck only
                        // covers the window up to that point).
                        if (gated && !DigestStillGood(spec.DaemonBinaryPath))
                            return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.StartGateDrift, VerifyExit.StartGateDriftToken);

                        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "committed", preState, "no-unit", fingerprint));
                        onCommitted?.Invoke();
                        ServiceTxnMarker.Delete(serviceId);
                        return VerifyExit.Ok;
                    }

                    Say(VerifyExit.RestoreVerificationToken);
                    return VerifyExit.RestoreVerification;
                }
            }

            if (time.GetUtcNow() >= pollDeadline) break;
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
    async Task<(InstallReady Status, int? JobPid)> IsInstallReadyAsync(string serviceId, string? expectedVersion, DateTimeOffset deadline, int? requirePid) {
        if (Remaining(deadline) <= TimeSpan.Zero) return (InstallReady.NotReady, null);

        var h = await hello(serviceId, Remaining(deadline));
        if (!h.WellFormed) return (InstallReady.NotReady, null);
        if (h.DaemonName != serviceId) return (InstallReady.VersionMismatch, null);
        if (h.ProtocolVersion != HelloProtocol.CurrentVersion) return (InstallReady.VersionMismatch, null);
        if (expectedVersion is not null && h.DaemonVersion != expectedVersion) return (InstallReady.VersionMismatch, null);

        if (Remaining(deadline) <= TimeSpan.Zero) return (InstallReady.NotReady, null);
        var jobPid    = manager.Query(serviceId, Remaining(deadline)).JobPid;
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
        // Never uninstall what we can't verify is ours. A null read is either genuine absence OR a
        // present-but-unreadable file (a lock-unaware writer replaced ours between bootstrap and
        // rollback): only the confirmed-absent case (read null AND the file does not exist) may be
        // cleared. Present-but-unreadable, and readable-but-foreign, both surface untouched — the
        // same fail-closed rule RecoverLeftoverMarker applies at entry.
        var onDisk = _readPlist(plistPath);
        if (onDisk is null) {
            if (_plistExists(plistPath)) {
                Say(VerifyExit.RestoreVerificationToken);
                return VerifyExit.RestoreVerification;
            }
        } else if (ServiceTxnMarker.Fingerprint(onDisk) != fingerprint) {
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
