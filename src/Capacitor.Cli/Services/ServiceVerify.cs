using Capacitor.Cli.Core.Auth;
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

    /// <summary>start --verify's pre-mutation consent-directive gate refused to
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

    readonly Func<string, (LaunchdUnit.PlistRead Status, string? Content)> _phaseAPlistRead =
        readPlist is null && plistExists is null
            ? (path => { var status = LaunchdUnit.TryReadPlist(path, out var content); return (status, content); })
            : (path => {
                var content = readPlist?.Invoke(path);
                if (content is not null) return (LaunchdUnit.PlistRead.Ok, content);
                var exists = plistExists?.Invoke(path) ?? false;
                return (exists ? LaunchdUnit.PlistRead.Unreadable : LaunchdUnit.PlistRead.Absent, null);
            });

    /// <summary>The gated start seam: when non-null AND <c>gateEnv(ConsentSeedVar)</c> is
    /// PRESENT — any value, including empty, under the exact-value contract —
    /// <see cref="StartVerifiedAsync"/> runs the pre-mutation gate (Phase A) and the
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

        // Gated ONLY when the invoking launcher itself carries the consent-seed
        // directive — a bare `kcap daemon service start --verify` typed at a terminal never sees
        // this branch. `is not null` (not IsNullOrEmpty): an EMPTY invoking value is a deliberate
        // refusal under the exact-value contract, not absence, so it must still activate the gate —
        // EvaluateStartGate's own directive check then reports DirectiveInvalid for it. Phase A runs
        // right here (after the fresh query, before ANY write) so a rejected gate leaves absolutely
        // nothing touched — not even the marker.
        var gated = _gateEnv is not null && _gateEnv(ConsentSeedVar) is not null;
        string? phaseAPlistContent = null;

        // The unit's own baked KCAP_EXPECT_SERVER_URL, captured alongside Phase
        // A's gate evaluation and threaded through to the readiness-timeout attribution below —
        // there is no other point in this method that parses the unit's environment.
        string? unitExpectation = null;

        if (gated) {
            var plistPath = LaunchdUnit.PlistPath(serviceId);
            var (readStatus, content) = _phaseAPlistRead(plistPath);
            phaseAPlistContent = content;

            StartGateReason? reason;
            if (readStatus == LaunchdUnit.PlistRead.Unreadable) {
                reason = StartGateReason.EvidenceUnreadable;
            } else {
                try {
                    var unitEnv = content is not null ? LaunchdUnit.EnvFromPlist(content) : new Dictionary<string, string>();
                    var unitBinaryPath = content is not null ? LaunchdUnit.BinaryFromPlist(content) : null;
                    reason = EvaluateStartGate(unitEnv, unitBinaryPath, UnitIdentity.ResolveDaemonBinary(), _gateEnv!, _digestMatches);
                    unitEnv.TryGetValue(ExpectVar, out unitExpectation);
                } catch {
                    reason = StartGateReason.EvidenceUnreadable;
                }
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
                if (bootOutError is not null) {
                    // An unconfirmed bootout must never fall through to the ungated Start()
                    // below — that would kickstart launchd's still-loaded, possibly stale
                    // definition, exactly the path this gate exists to prevent. Reuse the
                    // existing bootout-unknown contract (22): Rollback re-attempts the bootout
                    // with its own verification, landing in RestoreVerification if that also fails.
                    Say($"stop: {bootOutError}");
                    ServiceTxnMarker.Write(serviceId,
                        new TxnMarker(1, "start", "gate-bootout-failed", DescribeQuery(pre), "unloaded-plist-retained", null));
                    return await Rollback(serviceId, VerifyExit.BootoutUnknown, VerifyExit.BootoutUnknownToken);
                }

                // Stop() reporting success is only the CALL's exit code — launchd does not
                // guarantee the label is synchronously gone the instant bootout returns. Confirm
                // Absent in a bounded poll before ever bootstrapping on top of it: bootstrapping
                // while the probe still reads Loaded would silently kickstart the stale definition
                // (via the ungated manager.Start below's own Loaded→kickstart branch) instead of
                // provably starting the fresh one this gate is authorizing.
                if (!await WaitForLabelAbsentAsync(serviceId, deadline)) {
                    ServiceTxnMarker.Write(serviceId,
                        new TxnMarker(1, "start", "gate-bootout-unconfirmed", DescribeQuery(pre), "unloaded-plist-retained", null));
                    return await Rollback(serviceId, VerifyExit.BootoutUnknown, VerifyExit.BootoutUnknownToken);
                }
            }

            // Same XmlException hazard as Phase A's parse, but here escaping is worse: it would
            // abort AFTER the marker write and boot-out, past the point Rollback runs, leaving the
            // service stopped with a stuck marker instead of the guaranteed unloaded-plist-retained
            // outcome. A recheck that can't be parsed/validated is exactly what drift means — the
            // content changed (to something unreadable) or can no longer be confirmed unchanged —
            // so route it into the same drift branch rather than letting it throw.
            if (!RecheckPlistUnchanged(serviceId, phaseAPlistContent)) {
                ServiceTxnMarker.Write(serviceId,
                    new TxnMarker(1, "start", "gate-drift", DescribeQuery(pre), "unloaded-plist-retained", null));
                return await Rollback(serviceId, VerifyExit.StartGateDrift, VerifyExit.StartGateDriftToken);
            }
        }

        // Verified pre-clear before bootstrap, gated paths only — the marker a
        // readiness timeout later finds is only trustworthy evidence for THIS attempt once any
        // leftover from a prior one is provably gone. A clear that can't be verified (locked file,
        // undeletable directory sitting at the path) disables coded attribution for this action
        // entirely rather than risk attributing a stale marker; the mutation itself still proceeds.
        var attributionEnabled = false;
        if (gated) {
            attributionEnabled = BootRefusalReader.TryClear(serviceId);
            if (!attributionEnabled) Say("boot-refusal marker could not be cleared; coded attribution disabled");
        }

        // Bootstrap-only (see IVerifyServiceManager.StartBootstrapOnly): any failure — including a
        // Loaded probe — rolls back via BootoutUnknown rather than falling through to the ungated
        // Start() below, whose own readiness poll is normally the source of truth.
        if (gated) {
            if (!manager.StartBootstrapOnly(serviceId, Remaining(deadline), out var bootstrapOnlyError)) {
                Say($"start: {bootstrapOnlyError}");
                ServiceTxnMarker.Write(serviceId,
                    new TxnMarker(1, "start", "gate-bootstrap-only-failed", DescribeQuery(pre), "unloaded-plist-retained", null));
                return await Rollback(serviceId, VerifyExit.BootoutUnknown, VerifyExit.BootoutUnknownToken);
            }
        } else if (!manager.Start(serviceId, Remaining(deadline), out var startError) && startError is not null) {
            Say($"start: {startError}");
        }

        ServiceTxnMarker.Write(serviceId,
            new TxnMarker(1, "start", "bootstrapped", DescribeQuery(pre), "unloaded-plist-retained", null));

        var pollDeadline = deadline - _confirmReserve;

        // Every job pid actually observed (owned or not) across this readiness window — the
        // attribution rule below only trusts a marker whose pid was seen here, never a stale one.
        var observedJobPids = new HashSet<int>();

        while (time.GetUtcNow() < pollDeadline) {
            var (ready, pid) = await IsReadyAsync(serviceId, pollDeadline, requirePid: null);
            if (pid is not null) observedJobPids.Add(pid.Value);

            // IsReadyAsync only reports a pid once hello is well-formed — but a REFUSING daemon
            // exits before its control socket exists, so hello is NEVER well-formed in exactly the
            // scenario attribution needs to see. Query the job pid directly whenever IsReadyAsync
            // couldn't, so a refused boot's pid still lands in observedJobPids. Gated paths only —
            // the ungated flow never reads this set, so it stays untouched.
            if (gated && pid is null) {
                var directRemaining = Remaining(pollDeadline);
                if (directRemaining > TimeSpan.Zero) {
                    var directPid = manager.Query(serviceId, directRemaining).JobPid;
                    if (directPid is not null) observedJobPids.Add(directPid.Value);
                }
            }

            if (ready) {
                // Recheck against the ORIGINAL deadline, pinning pid: a job that answered then
                // respawned under KeepAlive must not bless a crash-looping unit.
                var (confirmed, confirmedPid) = await IsReadyAsync(serviceId, deadline, requirePid: pid);
                if (confirmedPid is not null) observedJobPids.Add(confirmedPid.Value);
                if (confirmed) {
                    // Post-readiness recheck (spec §3, mirrors install/replace's own final recheck):
                    // Phase B's pre-bootstrap recheck only bounds the recheck→exec race up to the
                    // moment bootstrap started — a foreign writer swapping the plist or the binary
                    // bytes AFTER that, but before this commit, must not ride readiness into a
                    // silent commit. Re-read the plist (contained) and compare against Phase A's own
                    // captured content, and re-check the digest one more time.
                    if (gated && !RecheckPlistUnchanged(serviceId, phaseAPlistContent)) {
                        ServiceTxnMarker.Write(serviceId,
                            new TxnMarker(1, "start", "gate-post-readiness-drift", DescribeQuery(pre), "unloaded-plist-retained", null));
                        return await Rollback(serviceId, VerifyExit.StartGateDrift, VerifyExit.StartGateDriftToken);
                    }

                    ServiceTxnMarker.Write(serviceId,
                        new TxnMarker(1, "start", "committed", DescribeQuery(pre), "unloaded-plist-retained", null));
                    onCommitted?.Invoke();
                    ServiceTxnMarker.Delete(serviceId);
                    if (gated) BootRefusalReader.Consume(serviceId); // hygiene — no refusal to attribute on success
                    return VerifyExit.Ok;
                }
            }

            if (time.GetUtcNow() >= pollDeadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        var rollbackExit = await Rollback(serviceId);

        return AttributeReadinessTimeout(serviceId, rollbackExit, gated, attributionEnabled, unitExpectation, observedJobPids);
    }

    /// <summary>Re-reads the plist and re-checks the digest (both contained — never an escaping
    /// exception), comparing against Phase A's captured <paramref name="phaseAPlistContent"/>;
    /// false means drift. Shared by Phase B's pre-bootstrap and post-readiness rechecks — callers
    /// own their own marker phase and rollback call.</summary>
    bool RecheckPlistUnchanged(string serviceId, string? phaseAPlistContent) {
        var content = _readPlist(LaunchdUnit.PlistPath(serviceId));
        bool digestGood;
        try {
            var binary = content is not null ? LaunchdUnit.BinaryFromPlist(content) : null;
            digestGood = binary is not null && _digestMatches(binary);
        } catch {
            digestGood = false;
        }

        return content == phaseAPlistContent && digestGood;
    }

    /// <summary>
    /// Shared readiness-timeout attribution tail for both <see cref="StartVerifiedAsync"/> and
    /// <see cref="InstallVerifiedAsync"/>: attribute ONLY on the genuine readiness-timeout exit,
    /// ONLY when pre-clear was verified, and ONLY via the pure <see cref="Attributable"/> rule — a
    /// deterministic mismatch (hello validation, gate drift) or any other rollback reason is never
    /// what a boot-refusal marker explains, so <paramref name="rollbackExit"/> passes through
    /// unchanged in every case; this only ever adds the one stderr line and consumes the marker.
    /// </summary>
    static int AttributeReadinessTimeout(string serviceId, int rollbackExit, bool gated, bool attributionEnabled,
            string? unitExpectation, IReadOnlySet<int> observedJobPids) {
        if (gated && attributionEnabled && rollbackExit == VerifyExit.ReadinessTimeout
            && BootRefusalReader.TryRead(serviceId) is { } evidence
            && Attributable(evidence, serviceId, unitExpectation, observedJobPids)) {
            Say($"refusal_reason={evidence.Token}");
            BootRefusalReader.Consume(serviceId);
        }

        return rollbackExit;
    }

    /// <summary>
    /// The pure decision core of readiness-timeout boot-refusal attribution. A
    /// marker is trustworthy evidence for THIS attempt only when: it names this same daemon; it
    /// carries no attempt id (an attempt-id-bearing marker is DETACHED evidence for a different
    /// subsystem, not this service verb's own boot — see the daemon-side boot-carrier lifecycle);
    /// its baked server expectation agrees with the unit's own (see <see cref="ExpectationsAgree"/>);
    /// and its pid was positively observed as a job pid during THIS readiness window, never a pid
    /// merely assumed from a stale marker left by a previous incarnation.
    /// </summary>
    internal static bool Attributable(BootRefusalEvidence evidence, string daemonName, string? unitExpectation, IReadOnlySet<int> observedJobPids) =>
        evidence.DaemonName == daemonName
        && evidence.AttemptId is null
        && ExpectationsAgree(evidence.Expectation, unitExpectation)
        && observedJobPids.Contains(evidence.Pid);

    /// <summary>Whether two expectation values agree for attribution purposes. Both genuinely UNSET
    /// (null) is agreement — no expectation configured on either side is not a mismatch. A
    /// present-but-empty value on EITHER side is a deliberate-but-invalid value under the same
    /// exact-value contract used everywhere else in this gate, so it can never trivially agree with
    /// anything, including another empty value — only a null/null pair is absence. Otherwise both
    /// sides are canonicalized and compared through <see cref="ServerIdentity.Matches"/>.</summary>
    internal static bool ExpectationsAgree(string? a, string? b) =>
        (a is null && b is null) || (!string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && ServerIdentity.Matches(a, b));

    /// <summary>
    /// The pure decision core of the gated start path. Given the environment
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
        var invokingDirective = env(ConsentSeedVar);
        if (invokingDirective is null) return null; // true absence — this invocation never asked to be gated

        // Exact-value contract: an empty (or otherwise non-"prompt") invoking value is a deliberate
        // refusal, not absence — reported the same way an invalid UNIT directive is below.
        if (invokingDirective != "prompt") return StartGateReason.DirectiveInvalid;

        // Key absent → DirectiveMissing (the unit never baked one at all). Key present with any
        // value other than "prompt" — INCLUDING empty — → DirectiveInvalid: a present-but-empty
        // value is a deliberate (if broken) value, not absence, same exact-value contract as the
        // invoking side above.
        if (!unitEnv.TryGetValue(ConsentSeedVar, out var unitDirective))
            return StartGateReason.DirectiveMissing;

        if (unitDirective != "prompt")
            return StartGateReason.DirectiveInvalid;

        var matches = digestMatches ?? DaemonDigest.Matches;

        try {
            if (unitBinaryPath is null) return StartGateReason.EvidenceUnreadable;

            if (!matches(unitBinaryPath)) {
                var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var samePath = installBinaryPath is not null && SameBinaryPath(unitBinaryPath, installBinaryPath, comparison);
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

    /// <summary>Whether the unit's baked binary path and this install's own resolved binary path
    /// name the same file — the split between <see cref="StartGateReason.PackageInconsistent"/>
    /// (this install's own binary, just stale/broken) and <see cref="StartGateReason.ForeignBinary"/>
    /// (a different path entirely). Takes the comparison explicitly (rather than deciding OS-ness
    /// itself) so both branches are directly unit-testable: callers pass
    /// <see cref="StringComparison.Ordinal"/> on Linux's case-sensitive filesystems and
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> elsewhere (macOS/Windows default to
    /// case-insensitive), so distinct paths on a case-sensitive filesystem never compare equal.</summary>
    internal static bool SameBinaryPath(string unitBinaryPath, string installBinaryPath, StringComparison comparison) =>
        string.Equals(Path.GetFullPath(unitBinaryPath), Path.GetFullPath(installBinaryPath), comparison);

    /// <summary>
    /// Identity half of <see cref="EvaluateStartGate"/>. The unit's effective identity is its
    /// baked <c>KCAP_URL</c> (precedence) or, absent that, its
    /// baked profile's <c>server_url</c> looked up via <see cref="ConfigMutator.TryLoadPure"/> under
    /// the unit's own baked <c>KCAP_CONFIG_DIR</c> (or the default config root when it baked none).
    /// Fail-closed on absent required evidence (spec §3.4(b)): a gated launch is always app-managed,
    /// so the invoking environment is expected to carry BOTH <c>KCAP_PROFILE</c> and
    /// <c>KCAP_EXPECT_SERVER_URL</c>, and the unit being gated is expected to carry its own baked
    /// <c>KCAP_EXPECT_SERVER_URL</c> — any of those missing, or the unit's server proving
    /// unresolvable, means the identity assertion this gate exists to make simply cannot be made, so
    /// it must never be silently skipped as "no assertion to make". Only the unit's OWN baked
    /// <c>KCAP_PROFILE</c> stays optional — a unit pinned directly by a baked <c>KCAP_URL</c>
    /// legitimately carries none. The stale-pin rule: the unit's baked expectation, its resolved
    /// identity, and this invocation's expected server URL must all agree — so a unit installed for
    /// server S whose profile was later re-pointed to T is caught here even when a fresh
    /// T-expecting invocation would otherwise match one side of that split. Profile identity is
    /// compared by exact name, server identity through <see cref="ServerIdentity.Canonicalize"/> — a
    /// candidate that fails to canonicalize can never silently agree with the others.
    /// </summary>
    static StartGateReason? EvaluateIdentity(IReadOnlyDictionary<string, string> unitEnv, Func<string, string?> env) {
        unitEnv.TryGetValue(ProfileVar, out var unitProfile);
        unitEnv.TryGetValue(UrlVar, out var unitUrl);
        unitEnv.TryGetValue(ExpectVar, out var unitExpect);

        var envProfile = env(ProfileVar);
        var envExpect  = env(ExpectVar);

        if (string.IsNullOrEmpty(unitExpect) || string.IsNullOrEmpty(envProfile) || string.IsNullOrEmpty(envExpect))
            return StartGateReason.IdentityMismatch;

        if (!string.IsNullOrEmpty(unitProfile) && !string.Equals(envProfile, unitProfile, StringComparison.Ordinal))
            return StartGateReason.IdentityMismatch;

        var unitResolved = !string.IsNullOrEmpty(unitUrl) ? unitUrl : BakedProfileServerUrl(unitEnv, unitProfile);
        if (string.IsNullOrEmpty(unitResolved)) return StartGateReason.IdentityMismatch; // unresolvable unit server

        string? canonical = null;
        foreach (var candidate in new[] { unitResolved, unitExpect, envExpect }) {
            var normalized = ServerIdentity.Canonicalize(candidate);
            if (normalized is null) return StartGateReason.IdentityMismatch;
            if (canonical is null) { canonical = normalized; continue; }
            if (!string.Equals(canonical, normalized, StringComparison.Ordinal))
                return StartGateReason.IdentityMismatch;
        }

        return null;
    }

    /// <summary>The <c>KCAP_URL</c>-absent fallback for <see cref="EvaluateIdentity"/> — the
    /// config-path resolution itself is shared with <c>DaemonCommands.BakedProfileServerUrl</c>
    /// (the same lookup <c>service status --json</c> does for its UX-only evidence fields) via
    /// <see cref="UnitIdentity.ConfigPathFromUnitEnv"/>; the failure contract on top is NOT
    /// shared and must not be: this uses <see cref="ConfigMutator.TryLoadPure"/> rather than
    /// <c>LoadPure</c>, because a config file that exists but cannot be read/parsed (a directory
    /// sitting at the path, a permissions error) is unreadable EVIDENCE for a pre-mutation gate —
    /// the caller's try/catch reports that as <see cref="StartGateReason.EvidenceUnreadable"/>,
    /// never silently folded into the same "server unresolvable" outcome an absent/unconfigured
    /// profile gets. <c>DaemonCommands</c>' UX-only counterpart fails soft to null instead.</summary>
    static string? BakedProfileServerUrl(IReadOnlyDictionary<string, string> unitEnv, string? profile) {
        if (string.IsNullOrEmpty(profile)) return null;
        var configPath = UnitIdentity.ConfigPathFromUnitEnv(unitEnv);

        if (!ConfigMutator.TryLoadPure(configPath, out var config))
            throw new InvalidDataException($"unreadable config at '{configPath}'");

        return config.Profiles.TryGetValue(profile, out var p) ? p.ServerUrl : null;
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
    /// rollback itself is the verified-safe state, marker deleted, and reports
    /// <paramref name="reasonExit"/>/<paramref name="reasonToken"/> — defaulted to a genuine
    /// readiness timeout, but a caller rolling back for a DIFFERENT reason (e.g. gated start's
    /// Phase B drift) overrides both so exactly one verify_* line is ever printed for that failure,
    /// mirroring <see cref="InstallRollback"/>'s own reasonToken parameter. Still loaded at reserve
    /// expiry → marker kept so a resumer can see the transaction never settled; that failure path
    /// always reports its OWN token regardless of the override, since the rollback itself did not
    /// reach the verified-safe state the override describes.</summary>
    async Task<int> Rollback(string serviceId, int reasonExit = VerifyExit.ReadinessTimeout, string reasonToken = VerifyExit.ReadinessTimeoutToken) {
        var deadline = time.GetUtcNow() + _rollbackReserve;

        manager.Stop(serviceId, Remaining(deadline), out var stopError);
        if (stopError is not null) Say($"stop: {stopError}");

        LabelProbe lastProbe;
        while (true) {
            lastProbe = manager.Query(serviceId, Remaining(deadline)).Probe;
            if (lastProbe == LabelProbe.Absent) {
                ServiceTxnMarker.Delete(serviceId);
                Say(reasonToken);
                return reasonExit;
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

    /// <summary>The gated install/replace path's digest recheck seam, shared by
    /// the viability arm and both TOCTOU rechecks below. An exception hashing the binary (a race
    /// with a package manager mid-replace, a permissions error) is treated as failure —
    /// unreadable evidence at a recheck is drift, never let it escape uncaught.</summary>
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

        // Gated ONLY when the invoking launcher itself carries the consent-seed
        // directive — a bare `kcap daemon service install/replace --verify` typed at a terminal
        // never sees any of this task's checks. `is not null`, same exact-value contract as
        // StartVerifiedAsync's `gated`: an empty invoking value still activates. Computed once, up
        // front, so the viability arm and both rechecks below share one activation decision.
        var gated = _gateEnv is not null && _gateEnv(ConsentSeedVar) is not null;

        // The refusal-attribution unit expectation for install/replace: unlike start (which reads
        // an EXISTING unit's baked env), install is about to WRITE a fresh unit, so there is no
        // installed plist to re-parse — the freshly generated spec's own baked env, about to become
        // the unit's env, is the equivalent evidence.
        spec.Environment.TryGetValue(ExpectVar, out var unitExpectation);
        var observedJobPids = new HashSet<int>();

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

        // The binary about to be installed must still hash-match this CLI build's own
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

        // TOCTOU re-check immediately before the mutation viability's digest check
        // authorized — the same binary content could have been swapped (a foreign writer, a broken
        // mid-upgrade package) in the window between that check and this bootstrap. Drift here rolls
        // back through the SAME fingerprint-gated machinery a bootstrap throw does, just with the
        // gate's own exit code/token instead of ReadinessTimeout's.
        if (gated && !DigestStillGood(spec.DaemonBinaryPath))
            return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.StartGateDrift, VerifyExit.StartGateDriftToken);

        // Verified pre-clear before bootstrap, gated paths only — mirrors StartVerifiedAsync: the
        // marker a readiness timeout later finds is only trustworthy evidence for THIS attempt once
        // any leftover from a prior one is provably gone. A clear that can't be verified disables
        // coded attribution for this action entirely rather than risk attributing a stale marker;
        // the mutation itself still proceeds.
        var attributionEnabled = false;
        if (gated) {
            attributionEnabled = BootRefusalReader.TryClear(serviceId);
            if (!attributionEnabled) Say("boot-refusal marker could not be cleared; coded attribution disabled");
        }

        try {
            manager.WriteAndBootstrap(spec, Remaining(forward));
        } catch (Exception ex) {
            // WriteAndBootstrap writes the unit before it bootstraps — a throw here can still leave the
            // plist on disk, so route through the same fingerprint-gated rollback.
            Say($"{op}: {ex.Message}");
            var bootstrapThrowExit = await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
            return AttributeReadinessTimeout(serviceId, bootstrapThrowExit, gated, attributionEnabled, unitExpectation, observedJobPids);
        }

        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "bootstrapped", preState, "no-unit", fingerprint));

        var pollDeadline = forward - _confirmReserve;

        while (time.GetUtcNow() < pollDeadline) {
            var (primary, pinnedPid) = await IsInstallReadyAsync(serviceId, expectedVersion, pollDeadline, requirePid: null);
            if (pinnedPid is not null) observedJobPids.Add(pinnedPid.Value);

            // Same reasoning as StartVerifiedAsync's readiness loop: IsInstallReadyAsync only
            // reports a pid once hello is well-formed, but a REFUSING daemon exits before its
            // control socket exists — query the job pid directly whenever hello couldn't, so a
            // refused boot's pid still lands in observedJobPids.
            if (gated && pinnedPid is null) {
                var directRemaining = Remaining(pollDeadline);
                if (directRemaining > TimeSpan.Zero) {
                    var directPid = manager.Query(serviceId, directRemaining).JobPid;
                    if (directPid is not null) observedJobPids.Add(directPid.Value);
                }
            }

            if (primary == InstallReady.VersionMismatch)
                return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

            if (primary == InstallReady.Ready) {
                // Recheck against the ORIGINAL forward cutoff, pinning pid (same incarnation).
                var (confirm, confirmedPid) = await IsInstallReadyAsync(serviceId, expectedVersion, forward, requirePid: pinnedPid);
                if (confirmedPid is not null) observedJobPids.Add(confirmedPid.Value);
                if (confirm == InstallReady.VersionMismatch)
                    return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.HelloValidation, VerifyExit.HelloValidationToken);

                if (confirm == InstallReady.Ready) {
                    // The plist on disk must still be the one this transaction wrote — a lock-unaware
                    // old CLI can overwrite it under a healthy job, and a PID check would miss that.
                    var onDisk = _readPlist(generated.Path);
                    if (onDisk is not null && ServiceTxnMarker.Fingerprint(onDisk) == fingerprint) {
                        // The final gated recheck, joined onto this existing on-disk
                        // recheck rather than a separate poll step — catches a binary swap that
                        // landed AFTER bootstrap started the job (the pre-bootstrap recheck only
                        // covers the window up to that point).
                        if (gated && !DigestStillGood(spec.DaemonBinaryPath))
                            return await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.StartGateDrift, VerifyExit.StartGateDriftToken);

                        ServiceTxnMarker.Write(serviceId, new TxnMarker(1, op, "committed", preState, "no-unit", fingerprint));
                        onCommitted?.Invoke();
                        ServiceTxnMarker.Delete(serviceId);
                        if (gated) BootRefusalReader.Consume(serviceId); // hygiene — no refusal to attribute on success
                        return VerifyExit.Ok;
                    }

                    Say(VerifyExit.RestoreVerificationToken);
                    return VerifyExit.RestoreVerification;
                }
            }

            if (time.GetUtcNow() >= pollDeadline) break;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }

        var readinessTimeoutExit = await InstallRollback(serviceId, generated.Path, fingerprint, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
        return AttributeReadinessTimeout(serviceId, readinessTimeoutExit, gated, attributionEnabled, unitExpectation, observedJobPids);
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

    /// <summary>Poll (bounded by <paramref name="deadline"/>) until <c>manager.Query</c> reports the
    /// label <see cref="LabelProbe.Absent"/> — used by the gated start path to confirm a bootout
    /// actually settled before bootstrapping on top of it (a launchctl exit code alone is not proof
    /// the label unloaded synchronously).</summary>
    async Task<bool> WaitForLabelAbsentAsync(string serviceId, DateTimeOffset deadline) {
        while (true) {
            if (manager.Query(serviceId, Remaining(deadline)).Probe == LabelProbe.Absent) return true;
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }
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
