using Capacitor.Cli.Core;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceVerifyStartTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Id = "svc-verify";

    /// <summary>Scripted <see cref="IServiceManager"/>: Query reflects a simple started/stopped
    /// state machine driven by Start/Stop, so a test only sets the flags/pid it cares about.
    /// <see cref="Calls"/> records every verb in argv-order (per the brief) so a test can assert
    /// e.g. Stop happened after Start, and the restore Query happened after Stop.</summary>
    sealed class FakeServiceManager(UserHome home) : IVerifyServiceManager {
        public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);
        public readonly List<string> Calls = [];
        public bool Started, Stopped, RemainsLoadedAfterStop, ProbeUnknownAfterStop;
        public int? RunningPid = 4242;
        public string? StopError;

        /// <summary>Reported as <see cref="ServiceQuery.UnitPresent"/> on every <see cref="Query"/>
        /// call. Defaults true; a genuinely-absent-unit test sets this false.</summary>
        public bool UnitPresent = true;

        /// <summary>When set, <see cref="StopError"/> is reported on only the FIRST <see cref="Stop"/>
        /// call and cleared thereafter — for scripting a bootout that fails once (e.g. a foreign
        /// writer momentarily holding the label) but succeeds on Rollback's own re-attempt.</summary>
        public bool StopErrorOnceOnly;

        /// <summary>When set, a Stop() that reports SUCCESS (no <see cref="StopError"/>) still shows
        /// the label as Loaded on every Query() until a SECOND Stop() call happens — scripting a
        /// bootout whose exit code lied about the label being synchronously gone. Distinct from
        /// <see cref="RemainsLoadedAfterStop"/> (which never confirms Absent at all): this confirms
        /// on Rollback's own re-attempted bootout, so a test can assert the gate's confirm-loop times
        /// out and rolls back to a result Rollback itself then successfully reaches.</summary>
        public bool RemainsLoadedUntilSecondStop;
        public Action<string>? OnStart;
        public Action<string>? OnStop;

        /// <summary>Scripts a foreign writer that loads the label in the window right before the
        /// gated path's bootstrap-only call — <see cref="StartBootstrapOnly"/> must refuse rather
        /// than fall back to a kickstart the way the generic <see cref="Start"/> would.</summary>
        public bool LoadedBeforeBootstrapOnly;

        /// <summary>When set, a post-start Query blocks for its whole timeout (a hung
        /// <c>launchctl print</c>) by advancing this clock — so a test can prove the readiness step
        /// never hands the Query a second full budget after a late hello already burned the first.</summary>
        public FakeTimeProvider? HangQueryClock;

        public int StartCalls => Calls.Count(c => c == "start");
        public int StopCalls => Calls.Count(c => c == "stop");
        public int UninstallCalls => Calls.Count(c => c == "uninstall");

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            Calls.Add("query");
            if (Started && HangQueryClock is not null) HangQueryClock.Advance(timeout);
            if (Stopped) {
                if (ProbeUnknownAfterStop)
                    return new ServiceQuery(LabelProbe.Unknown, UnitPresent, ServiceState.Installed, "/bin/kcap-daemon", null);
                if (RemainsLoadedUntilSecondStop && StopCalls < 2)
                    return new ServiceQuery(LabelProbe.Loaded, UnitPresent, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
                if (!RemainsLoadedAfterStop)
                    return new ServiceQuery(LabelProbe.Absent, UnitPresent, ServiceState.Installed, "/bin/kcap-daemon", null);
            }
            if (Started)
                return new ServiceQuery(LabelProbe.Loaded, UnitPresent, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            return new ServiceQuery(LabelProbe.Absent, UnitPresent, ServiceState.Installed, "/bin/kcap-daemon", null);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) { }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("uninstall");
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("start");
            OnStart?.Invoke(serviceId);
            Started = true;
            error = null;
            return true;
        }

        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("start");
            if (LoadedBeforeBootstrapOnly) {
                error = "cannot bootstrap-only: label unexpectedly Loaded";
                return false;
            }
            OnStart?.Invoke(serviceId);
            Started = true;
            error = null;
            return true;
        }

        public bool Stop(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("stop");
            OnStop?.Invoke(serviceId);
            Stopped = true;
            error = StopError;
            if (StopErrorOnceOnly) StopError = null;
            return error is null;
        }
    }

    /// <summary>Drives a suspended poll loop by repeatedly advancing a <see cref="FakeTimeProvider"/>
    /// until the engine's task settles — Task.Delay(interval, time, ct)'s continuation resumes
    /// synchronously inside Advance(), so no real waiting is needed (verified empirically: a tight
    /// Advance-loop reliably steps a multi-iteration Task.Delay poll to completion).</summary>
    static async Task<int> Drive(Task<int> task, FakeTimeProvider time, TimeSpan step) {
        var guard = 0;
        while (!task.IsCompleted && guard++ < 500) time.Advance(step);
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Happy_bootstrap_writes_marker_before_start_and_deletes_it_after_verified_success() {
        var manager = new FakeServiceManager(Home);
        var phaseAtStart = "";
        manager.OnStart = id => phaseAtStart = ServiceTxnMarker.Read(Daemons.Store, id)!.Phase;

        var phaseAtFirstHello = "";
        var helloCalls = 0;
        Task<HelloProbeResult> Hello(string id, TimeSpan _) {
            if (helloCalls++ == 0) phaseAtFirstHello = ServiceTxnMarker.Read(Daemons.Store, id)!.Phase;
            return Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(phaseAtStart).IsEqualTo("captured");
        await Assert.That(phaseAtFirstHello).IsEqualTo("bootstrapped");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
        await Assert.That(manager.StartCalls).IsEqualTo(1);
        await Assert.That(manager.StopCalls).IsEqualTo(0);
        // Primary check + the final recheck confirmation — both hello calls actually happened.
        await Assert.That(helloCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Readiness_never_satisfied_rolls_back_and_reports_readiness_timeout() {
        var manager = new FakeServiceManager(Home);
        var time = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(false, null, null, null));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(manager.StartCalls).IsEqualTo(1);
        await Assert.That(manager.StopCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Ownership_mismatch_never_satisfies_the_predicate_and_never_uninstalls() {
        var manager = new FakeServiceManager(Home) { RunningPid = 111 };
        var time = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 222, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(manager.StopCalls).IsEqualTo(1);
        await Assert.That(manager.UninstallCalls).IsEqualTo(0);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Start_accepts_a_capability_incompatible_hello() {
        var manager = new FakeServiceManager(Home);

        // Old daemon: well-formed hello, but no capability data at all.
        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, null, "0.9.0", null));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Rollback_reserve_exhausted_while_still_loaded_is_restore_verification() {
        var manager = new FakeServiceManager(Home) { RemainsLoadedAfterStop = true, StopError = "launchctl bootout: 5: Input/output error" };
        var time = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(false, null, null, null));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, time,
            forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1));

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        // The last observation is still Loaded (an affirmatively wrong state), so this is
        // RestoreVerification even though the reserve also ran out — RollbackBudget is only
        // for a last observation that's genuinely Unknown (see the sibling test below).
        await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
        await Assert.That(manager.StopCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsTrue();
        await Assert.That(ServiceTxnMarker.Read(Daemons.Store, Id)!.Phase).IsEqualTo("bootstrapped");

        // argv-order: Start precedes Stop, and the rollback's restore Query keeps polling
        // (bounded by rollbackReserve) after Stop rather than giving up on a single shot.
        await Assert.That(manager.Calls.IndexOf("start")).IsLessThan(manager.Calls.IndexOf("stop"));
        var lastStop = manager.Calls.LastIndexOf("stop");
        await Assert.That(manager.Calls.LastIndexOf("query")).IsGreaterThan(lastStop);
        await Assert.That(manager.Calls.Skip(lastStop + 1).Count(c => c == "query")).IsGreaterThan(1);
    }

    [Test]
    public async Task Rollback_reserve_exhausted_while_still_unknown_is_rollback_budget() {
        var manager = new FakeServiceManager(Home) { ProbeUnknownAfterStop = true, StopError = "launchctl bootout: 5: Input/output error" };
        var time = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(false, null, null, null));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, time,
            forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1));

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        // The last observation is genuinely Unknown — we ran out of reserve without ever being
        // able to tell whether the restore happened, so this is RollbackBudget, not
        // RestoreVerification (which needs an affirmatively-observed wrong state).
        await Assert.That(exit).IsEqualTo(VerifyExit.RollbackBudget);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsTrue();
    }

    [Test]
    public async Task Predicate_holding_once_is_not_enough_a_failed_final_recheck_still_rolls_back() {
        var manager = new FakeServiceManager(Home);
        var time = new FakeTimeProvider();

        // Well-formed exactly once — the primary check catches that one good answer, but the
        // immediate confirmation recheck (and every poll after) sees a dead/flaky daemon.
        var helloCalls = 0;
        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(helloCalls++ == 0, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(manager.StopCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
        // The primary check plus at least one confirmation attempt actually ran.
        await Assert.That(helloCalls).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Final_recheck_gets_the_reserved_confirm_slice_when_the_primary_lands_near_the_deadline() {
        var manager = new FakeServiceManager(Home);
        var time = new FakeTimeProvider();

        // The primary hello burns almost the whole poll budget (a slow resolve landing right at
        // the poll cutoff), leaving just enough for its own ownership Query. The final recheck
        // must still get a real probe — not from a clock-technicality floor, but from the
        // confirm slice reserved INSIDE the forward budget for exactly this case.
        var helloCalls = 0;
        Task<HelloProbeResult> Hello(string _, TimeSpan budget) {
            if (helloCalls++ == 0) time.Advance(budget - TimeSpan.FromMilliseconds(1));
            return Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(helloCalls).IsEqualTo(2);
        await Assert.That(manager.StopCalls).IsEqualTo(0);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task A_late_hello_never_hands_a_hung_query_a_second_full_budget() {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();

        // A hung `launchctl print` paired with a hello that consumes ~all of its budget must not
        // let readiness exceed the forward deadline: the Query is bounded by remaining-to-deadline,
        // so the transaction rolls back rather than committing after ~2x the forward time.
        var manager = new FakeServiceManager(Home) { HangQueryClock = time };
        Task<HelloProbeResult> Hello(string _, TimeSpan budget) {
            time.Advance(budget);
            return Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, time,
            forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1));

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        // Readiness is capped to the single forward deadline: the burned-budget hello leaves no
        // time for the Query, so the step aborts to rollback rather than doubling its spend...
        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        // ...and the whole transaction stays inside the advertised forward + reserve envelope.
        var elapsed = time.GetUtcNow() - start;
        await Assert.That(elapsed <= TimeSpan.FromSeconds(3)).IsTrue();
    }

    [Test]
    public async Task Final_recheck_at_a_different_incarnation_rolls_back_instead_of_committing() {
        // A new job pid on every observation (KeepAlive respawn between the primary check and the
        // final recheck): each check owns, but the pinned incarnation never survives to the
        // recheck, so a crash-looping unit is never committed — it rolls back at the forward cutoff.
        var manager = new FakeServiceManager(Home) { RunningPid = 1000 };
        var time = new FakeTimeProvider();

        Task<HelloProbeResult> Hello(string _, TimeSpan __) {
            if (manager.Started) manager.RunningPid++;
            return Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => manager.RunningPid, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(manager.StopCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Start_success_records_committed_phase_before_deleting_the_marker() {
        var manager = new FakeServiceManager(Home);
        string? phaseAtCommit = null;

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        // A crash between verify-success and marker removal must be recoverable as "committed →
        // just clear the marker", so the durable committed phase is written BEFORE the delete —
        // mirroring the install path.
        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            onCommitted: () => phaseAtCommit = ServiceTxnMarker.Read(Daemons.Store, Id)?.Phase);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(phaseAtCommit).IsEqualTo("committed");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    // ── gated start — Phase A (exit 28) / Phase B (exit 29) ──

    /// <summary>Test identity shared by every gated test below that needs the identity check to
    /// PASS (Phase A) so it can exercise something past it — the digest/drift/bootout machinery.</summary>
    const string GatedServerUrl = "https://s.example";

    /// <summary>Minimal launchd plist whose <c>&lt;array&gt;</c> (ProgramArguments) and baked
    /// <c>KCAP_CONSENT_SEED_DEFAULT</c> are exactly what <see cref="LaunchdUnit.BinaryFromPlist"/>/
    /// <see cref="LaunchdUnit.EnvFromPlist"/> read — real launchd never sees this content, only the
    /// gate's re-parse of it does. <paramref name="expectServerUrl"/>, when non-null, bakes BOTH
    /// <c>KCAP_URL</c> and <c>KCAP_EXPECT_SERVER_URL</c> with that value — the identity gate now
    /// fails closed on either being absent (spec §3.4(b)), so any test that needs Phase A's identity
    /// check to PASS must supply one (matching the invoking <c>gateEnv</c>'s own
    /// <c>KCAP_EXPECT_SERVER_URL</c>/<c>KCAP_PROFILE</c>).</summary>
    static string MinimalPlist(string binary, string consentSeedDefault, string? expectServerUrl = null) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.svc-verify</string>
          <key>ProgramArguments</key><array>
            <string>{binary}</string>
          </array>
          <key>EnvironmentVariables</key><dict>
            <key>KCAP_CONSENT_SEED_DEFAULT</key><string>{consentSeedDefault}</string>
            {(expectServerUrl is not null ? $"<key>KCAP_URL</key><string>{expectServerUrl}</string><key>KCAP_EXPECT_SERVER_URL</key><string>{expectServerUrl}</string>" : "")}
          </dict>
        </dict>
        </plist>
        """;

    /// <summary>Invoking env for a gated test that needs Phase A's identity check to pass: the
    /// consent-seed directive plus a profile/expectation matching <see cref="MinimalPlist"/>'s
    /// <paramref name="expectServerUrl"/> — both now required (fail-closed) by the identity gate.</summary>
    static Func<string, string?> GatedEnvWithIdentity(string expectServerUrl = GatedServerUrl) => k => k switch {
        "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
        "KCAP_PROFILE"              => "default",
        "KCAP_EXPECT_SERVER_URL"    => expectServerUrl,
        _                           => null,
    };

    // Contradictory evidence (query saw the unit; the read didn't) must classify
    // evidence_unreadable, never directive_missing — that's reserved for genuine agreed-absence.
    [Test, NotInParallel]
    [Arguments(false, "directive_missing")]   // query and read agree the unit is absent
    [Arguments(true, "evidence_unreadable")]  // query saw the unit but the read reports absent
    public async Task Phase_a_absence_evidence_disambiguates_directive_missing_from_evidence_unreadable(
        bool unitPresent, string expectedReason) {
        var manager = new FakeServiceManager(Home) { UnitPresent = unitPresent };

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => null,
            plistExists: _ => false,
            gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

        int    exit;
        string capturedErr;

        using (var capture = ConsoleOutput.StartErrorCapture()) {
            exit        = await sut.StartVerifiedAsync(Id);
            capturedErr = capture.GetCapturedError();
        }

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
        var lines = capturedErr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).Contains($"start_gate_reason={expectedReason}");
        await Assert.That(manager.Calls.Count).IsEqualTo(1);
        await Assert.That(manager.Calls.All(c => c == "query")).IsTrue();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    // F5: the in-process evidence properties describe ONE operation — a reused engine must not
    // carry a prior gate refusal into a later call. A gate-refusing start leaves the reason set;
    // the next call's lock contention (a non-gate exit, before any gate evaluation) must observe
    // the entry reset.
    [Test, NotInParallel]
    public async Task Last_gate_reason_is_cleared_at_the_next_operation_entry() {
        var manager = new FakeServiceManager(Home);

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => null,
            plistExists: _ => false,
            gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

        using (var capture = ConsoleOutput.StartErrorCapture()) {
            await Assert.That(await sut.StartVerifiedAsync(Id)).IsEqualTo(VerifyExit.StartGate);
        }
        await Assert.That(sut.LastGateReason).IsNotNull();

        // Hold the lock so the second call exits Contended before any gate evaluation.
        using var held = ServiceTxnLock.TryAcquire(Daemons.Store, Id, TimeSpan.FromSeconds(1));
        await Assert.That(held).IsNotNull();

        using (var capture = ConsoleOutput.StartErrorCapture()) {
            await Assert.That(await sut.StartVerifiedAsync(Id)).IsEqualTo(VerifyExit.Contended);
        }
        await Assert.That(sut.LastGateReason).IsNull();
    }

    [Test]
    public async Task Plist_drift_between_phase_a_and_phase_b_rolls_back_to_29_without_ever_starting() {
        // Loaded at the fresh query — the gated path must boot it out (never kickstart it)
        // before re-checking evidence immediately ahead of bootstrap.
        var manager = new FakeServiceManager(Home) { Started = true };
        var stopPhases = new List<string?>();
        manager.OnStop = id => stopPhases.Add(ServiceTxnMarker.Read(Daemons.Store, id)?.Phase);

        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var reads = 0;
        string? ReadPlist(string _) {
            reads++;
            // Phase A observes a passing unit (identity matching GatedEnvWithIdentity so Phase A
            // clears); the plist a foreign writer swaps in before Phase B's re-read points at a
            // different binary entirely — content drift, not just a digest failure (digestMatches
            // is stubbed to pass either way below).
            return reads == 1
                ? MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl)
                : MinimalPlist("/bin/kcap-daemon-moved", "prompt", GatedServerUrl);
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: ReadPlist,
            gateEnv: GatedEnvWithIdentity(),
            digestMatches: _ => true);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGateDrift);
        await Assert.That(manager.Calls.Contains("stop")).IsTrue();
        await Assert.That(manager.StartCalls).IsEqualTo(0);
        // The boot-out (Phase B, pre-drift-detection) ran under the "captured" phase; the
        // rollback's own stop (post-drift-detection) ran under "gate-drift" — proving the
        // marker was written BEFORE Rollback fired, not just that the exit code is 29.
        await Assert.That(stopPhases.Count).IsEqualTo(2);
        await Assert.That(stopPhases[0]).IsEqualTo("captured");
        await Assert.That(stopPhases[1]).IsEqualTo("gate-drift");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Malformed_plist_at_phase_a_is_evidence_unreadable_and_touches_nothing() {
        var manager = new FakeServiceManager(Home);

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        // A truncated/malformed plist — exactly the foreign-writer race Phase B's own re-check
        // defends against — makes LaunchdUnit.EnvFromPlist/BinaryFromPlist throw XmlException.
        // That must land as EvidenceUnreadable (28), not escape StartVerifiedAsync to a
        // generic, uncoded exit 1.
        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => "<plist version=\"1.0\"><dict><key>Truncated",
            gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
        await Assert.That(manager.Calls.Count).IsEqualTo(1);
        await Assert.That(manager.Calls.All(c => c == "query")).IsTrue();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Duplicate_key_plist_at_phase_a_is_evidence_unreadable_and_touches_nothing() {
        var manager = new FakeServiceManager(Home);

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        // A duplicate EnvironmentVariables key — never written by LaunchdUnit.Plist itself, so
        // this can only be a foreign/corrupt writer. LaunchdUnit.EnvFromPlist throws
        // InvalidDataException rather than last-win; that must land as EvidenceUnreadable (28),
        // not escape StartVerifiedAsync to a generic, uncoded exit 1.
        const string duplicateKeyPlist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.svc-verify</string>
              <key>ProgramArguments</key><array>
                <string>/bin/kcap-daemon</string>
              </array>
              <key>EnvironmentVariables</key><dict>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>prompt</string>
                <key>KCAP_CONSENT_SEED_DEFAULT</key><string>allow</string>
              </dict>
            </dict>
            </plist>
            """;

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => duplicateKeyPlist,
            gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
        await Assert.That(manager.Calls.Count).IsEqualTo(1);
        await Assert.That(manager.Calls.All(c => c == "query")).IsTrue();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Garbage_plist_at_phase_b_recheck_is_treated_as_drift_and_rolls_back() {
        var manager = new FakeServiceManager(Home) { Started = true };
        var stopPhases = new List<string?>();
        manager.OnStop = id => stopPhases.Add(ServiceTxnMarker.Read(Daemons.Store, id)?.Phase);

        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var reads = 0;
        string? ReadPlist(string _) {
            reads++;
            // Phase A reads a valid, passing plist (identity matching GatedEnvWithIdentity so
            // Phase A clears); a foreign writer replaces it with unparseable garbage before Phase
            // B's re-read — the parse itself must not escape StartVerifiedAsync (it would skip
            // Rollback entirely, since this happens AFTER the "captured" marker write and the
            // boot-out Stop, leaving the service stopped with a stuck marker instead of the
            // guaranteed unloaded-plist-retained outcome).
            return reads == 1 ? MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl) : "not even xml, let alone a plist";
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: ReadPlist,
            gateEnv: GatedEnvWithIdentity(),
            digestMatches: _ => true);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGateDrift);
        await Assert.That(manager.Calls.Contains("stop")).IsTrue();
        await Assert.That(manager.StartCalls).IsEqualTo(0);
        await Assert.That(stopPhases.Count).IsEqualTo(2);
        await Assert.That(stopPhases[0]).IsEqualTo("captured");
        await Assert.That(stopPhases[1]).IsEqualTo("gate-drift");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Phase_b_bootout_failure_never_kickstarts_the_stale_loaded_definition() {
        // Loaded at the fresh query, and the FIRST Stop (Phase B's boot-out) reports an error —
        // a foreign writer or a launchd hiccup. The gate must never fall through to the ungated
        // Start() below on an unconfirmed bootout: that would kickstart launchd's still-loaded,
        // possibly stale definition — exactly the path this gate exists to prevent. Rollback's
        // own re-attempt (the second Stop) succeeds, confirming the restore.
        var manager = new FakeServiceManager(Home) {
            Started = true,
            StopError = "launchctl bootout: 5: Input/output error",
            StopErrorOnceOnly = true,
        };
        var stopPhases = new List<string?>();
        manager.OnStop = id => stopPhases.Add(ServiceTxnMarker.Read(Daemons.Store, id)?.Phase);

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl),
            gateEnv: GatedEnvWithIdentity(),
            digestMatches: _ => true);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.BootoutUnknown);
        await Assert.That(manager.StartCalls).IsEqualTo(0);
        await Assert.That(manager.Calls.Contains("start")).IsFalse();
        // The failed boot-out ran under "captured"; the marker written right before Rollback
        // (which re-attempts the bootout, this time successfully) is "gate-bootout-failed" —
        // proving the marker landed BEFORE Rollback fired, not just that the exit code is 22.
        await Assert.That(stopPhases.Count).IsEqualTo(2);
        await Assert.That(stopPhases[0]).IsEqualTo("captured");
        await Assert.That(stopPhases[1]).IsEqualTo("gate-bootout-failed");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    [Test]
    public async Task Bootstrap_only_after_confirmed_bootout_a_lying_success_exit_still_rolls_back() {
        // Stop() reports SUCCESS (no error) — the launchctl exit code alone — but the label is
        // still Loaded on every query until Rollback's own re-attempted bootout. Bootstrapping
        // on an unconfirmed bootout would silently kickstart the stale still-loaded definition,
        // so the gate must roll back to BootoutUnknown instead, with zero start calls, even
        // though Stop() never reported an error at all.
        var manager = new FakeServiceManager(Home) { Started = true, RemainsLoadedUntilSecondStop = true };
        var time = new FakeTimeProvider();

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, time,
            forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(2),
            readPlist: _ => MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl),
            gateEnv: GatedEnvWithIdentity(),
            digestMatches: _ => true);

        var task = sut.StartVerifiedAsync(Id);
        var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(exit).IsEqualTo(VerifyExit.BootoutUnknown);
        await Assert.That(manager.StartCalls).IsEqualTo(0);
        await Assert.That(manager.Calls.Contains("start")).IsFalse();
        // Two Stop calls: Phase B's own boot-out (whose lying success exit is what this test
        // pins), then Rollback's re-attempt, which is the one that actually confirms Absent.
        await Assert.That(manager.StopCalls).IsEqualTo(2);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    /// <summary>The gated path re-checks the plist/digest once more after readiness is confirmed,
    /// not just pre-bootstrap: content only drifts on the FIRST read taken after both readiness
    /// probes already saw matching content.</summary>
    [Test]
    public async Task Post_readiness_recheck_detects_plist_drift_after_confirmed_ready_and_rolls_back_to_29() {
        var manager = new FakeServiceManager(Home);

        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var reads = 0;
        string? ReadPlist(string _) {
            reads++;
            // Phase A (read 1) and Phase B's own pre-bootstrap recheck (read 2) both see the
            // same passing content — readiness is then genuinely, twice confirmed. A foreign
            // writer swaps the plist only AFTER that, so the post-readiness recheck (read 3)
            // must be what catches it.
            return reads <= 2
                ? MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl)
                : MinimalPlist("/bin/kcap-daemon-moved", "prompt", GatedServerUrl);
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: ReadPlist,
            gateEnv: GatedEnvWithIdentity(),
            digestMatches: _ => true);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGateDrift);
        // Bootstrap DID run — this is a post-mutation catch, unlike the Phase A/B drift tests
        // above, which never start at all.
        await Assert.That(manager.StartCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
        await Assert.That(reads).IsGreaterThanOrEqualTo(3);
    }

    /// <summary>The post-readiness recheck also re-verifies the digest, independent of plist
    /// content drift — a swapped binary at the SAME baked path must still roll back to 29.</summary>
    [Test]
    public async Task Post_readiness_recheck_detects_digest_drift_after_confirmed_ready_and_rolls_back_to_29() {
        var manager = new FakeServiceManager(Home);

        Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var digestChecks = 0;
        bool DigestMatches(string _) {
            digestChecks++;
            // Passes for Phase A and Phase B's pre-bootstrap recheck; fails from the third
            // check onward — the post-readiness recheck.
            return digestChecks <= 2;
        }

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl),
            gateEnv: GatedEnvWithIdentity(),
            digestMatches: DigestMatches);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGateDrift);
        await Assert.That(manager.StartCalls).IsEqualTo(1);
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }

    /// <summary>The UNGATED path (no consent-seed directive carried by the invoker) must never run
    /// the post-readiness recheck — start behavior for a bare terminal invocation is
    /// unchanged.</summary>
    [Test]
    public async Task Ungated_start_never_runs_the_post_readiness_recheck() {
        var manager = new FakeServiceManager(Home);
        var readPlistCalls = 0;

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => { readPlistCalls++; return MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl); });
        // no gateEnv — ungated

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(readPlistCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Bootstrap_only_never_kickstarts_a_label_that_turned_loaded_just_before_it() {
        // Pre-mutation query sees Absent (no Phase B boot-out needed), but a foreign writer
        // loads the label in the window right before the gate's own bootstrap-only call —
        // manager.StartBootstrapOnly must refuse rather than silently kickstart it the way the
        // generic Start() would.
        var manager = new FakeServiceManager(Home) { LoadedBeforeBootstrapOnly = true };

        static Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

        var sut = new ServiceVerify(Daemons.Store, Config.Root, manager, _ => 4242, Hello, TimeProvider.System,
            readPlist: _ => MinimalPlist("/bin/kcap-daemon", "prompt", GatedServerUrl),
            gateEnv: GatedEnvWithIdentity(),
            digestMatches: _ => true);

        var exit = await sut.StartVerifiedAsync(Id);

        await Assert.That(exit).IsEqualTo(VerifyExit.BootoutUnknown);
        await Assert.That(manager.Started).IsFalse();
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, Id)).IsFalse();
    }
}
