using Capacitor.Cli.Core;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class ServiceVerifyStartTests {
    const string Id = "svc-verify";

    /// <summary>Scripted <see cref="IServiceManager"/>: Query reflects a simple started/stopped
    /// state machine driven by Start/Stop, so a test only sets the flags/pid it cares about.
    /// <see cref="Calls"/> records every verb in argv-order (per the brief) so a test can assert
    /// e.g. Stop happened after Start, and the restore Query happened after Stop.</summary>
    sealed class FakeServiceManager : IVerifyServiceManager {
        public readonly List<string> Calls = [];
        public bool Started, Stopped, RemainsLoadedAfterStop, ProbeUnknownAfterStop;
        public int? RunningPid = 4242;
        public string? StopError;
        public Action<string>? OnStart;
        public Action<string>? OnStop;

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
                    return new ServiceQuery(LabelProbe.Unknown, true, ServiceState.Installed, "/bin/kcap-daemon", null);
                if (!RemainsLoadedAfterStop)
                    return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
            }
            if (Started)
                return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
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

        public bool Stop(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("stop");
            OnStop?.Invoke(serviceId);
            Stopped = true;
            error = StopError;
            return StopError is null;
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
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();
            var phaseAtStart = "";
            manager.OnStart = id => phaseAtStart = ServiceTxnMarker.Read(id)!.Phase;

            var phaseAtFirstHello = "";
            var helloCalls = 0;
            Task<HelloProbeResult> Hello(string id, TimeSpan _) {
                if (helloCalls++ == 0) phaseAtFirstHello = ServiceTxnMarker.Read(id)!.Phase;
                return Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
            }

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System);

            var exit = await sut.StartVerifiedAsync(Id);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(phaseAtStart).IsEqualTo("captured");
            await Assert.That(phaseAtFirstHello).IsEqualTo("bootstrapped");
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
            await Assert.That(manager.StartCalls).IsEqualTo(1);
            await Assert.That(manager.StopCalls).IsEqualTo(0);
            // Primary check + the final recheck confirmation — both hello calls actually happened.
            await Assert.That(helloCalls).IsEqualTo(2);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Readiness_never_satisfied_rolls_back_and_reports_readiness_timeout() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(false, null, null, null));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.StartCalls).IsEqualTo(1);
            await Assert.That(manager.StopCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Ownership_mismatch_never_satisfies_the_predicate_and_never_uninstalls() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager { RunningPid = 111 };
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            var sut = new ServiceVerify(manager, _ => 222, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.StopCalls).IsEqualTo(1);
            await Assert.That(manager.UninstallCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Start_accepts_a_capability_incompatible_hello() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();

            // Old daemon: well-formed hello, but no capability data at all.
            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, null, "0.9.0", null));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System);

            var exit = await sut.StartVerifiedAsync(Id);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Rollback_reserve_exhausted_while_still_loaded_is_restore_verification() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager { RemainsLoadedAfterStop = true, StopError = "launchctl bootout: 5: Input/output error" };
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(false, null, null, null));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            // The last observation is still Loaded (an affirmatively wrong state), so this is
            // RestoreVerification even though the reserve also ran out — RollbackBudget is only
            // for a last observation that's genuinely Unknown (see the sibling test below).
            await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
            await Assert.That(manager.StopCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
            await Assert.That(ServiceTxnMarker.Read(Id)!.Phase).IsEqualTo("bootstrapped");

            // argv-order: Start precedes Stop, and the rollback's restore Query keeps polling
            // (bounded by rollbackReserve) after Stop rather than giving up on a single shot.
            await Assert.That(manager.Calls.IndexOf("start")).IsLessThan(manager.Calls.IndexOf("stop"));
            var lastStop = manager.Calls.LastIndexOf("stop");
            await Assert.That(manager.Calls.LastIndexOf("query")).IsGreaterThan(lastStop);
            await Assert.That(manager.Calls.Skip(lastStop + 1).Count(c => c == "query")).IsGreaterThan(1);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Rollback_reserve_exhausted_while_still_unknown_is_rollback_budget() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager { ProbeUnknownAfterStop = true, StopError = "launchctl bootout: 5: Input/output error" };
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(false, null, null, null));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            // The last observation is genuinely Unknown — we ran out of reserve without ever being
            // able to tell whether the restore happened, so this is RollbackBudget, not
            // RestoreVerification (which needs an affirmatively-observed wrong state).
            await Assert.That(exit).IsEqualTo(VerifyExit.RollbackBudget);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Predicate_holding_once_is_not_enough_a_failed_final_recheck_still_rolls_back() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();
            var time = new FakeTimeProvider();

            // Well-formed exactly once — the primary check catches that one good answer, but the
            // immediate confirmation recheck (and every poll after) sees a dead/flaky daemon.
            var helloCalls = 0;
            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(helloCalls++ == 0, 1, "1.2.3", "kcap-daemon"));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.StopCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
            // The primary check plus at least one confirmation attempt actually ran.
            await Assert.That(helloCalls).IsGreaterThanOrEqualTo(2);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Final_recheck_gets_the_reserved_confirm_slice_when_the_primary_lands_near_the_deadline() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();
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

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

            var exit = await sut.StartVerifiedAsync(Id);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(helloCalls).IsEqualTo(2);
            await Assert.That(manager.StopCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task A_late_hello_never_hands_a_hung_query_a_second_full_budget() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var time = new FakeTimeProvider();
            var start = time.GetUtcNow();

            // A hung `launchctl print` paired with a hello that consumes ~all of its budget must not
            // let readiness exceed the forward deadline: the Query is bounded by remaining-to-deadline,
            // so the transaction rolls back rather than committing after ~2x the forward time.
            var manager = new FakeServiceManager { HangQueryClock = time };
            Task<HelloProbeResult> Hello(string _, TimeSpan budget) {
                time.Advance(budget);
                return Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
            }

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            // Readiness is capped to the single forward deadline: the burned-budget hello leaves no
            // time for the Query, so the step aborts to rollback rather than doubling its spend...
            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            // ...and the whole transaction stays inside the advertised forward + reserve envelope.
            var elapsed = time.GetUtcNow() - start;
            await Assert.That(elapsed <= TimeSpan.FromSeconds(3)).IsTrue();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Final_recheck_at_a_different_incarnation_rolls_back_instead_of_committing() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            // A new job pid on every observation (KeepAlive respawn between the primary check and the
            // final recheck): each check owns, but the pinned incarnation never survives to the
            // recheck, so a crash-looping unit is never committed — it rolls back at the forward cutoff.
            var manager = new FakeServiceManager { RunningPid = 1000 };
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) {
                if (manager.Started) manager.RunningPid++;
                return Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
            }

            var sut = new ServiceVerify(manager, _ => manager.RunningPid, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.StopCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Start_success_records_committed_phase_before_deleting_the_marker() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();
            string? phaseAtCommit = null;

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            // A crash between verify-success and marker removal must be recoverable as "committed →
            // just clear the marker", so the durable committed phase is written BEFORE the delete —
            // mirroring the install path.
            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
                onCommitted: () => phaseAtCommit = ServiceTxnMarker.Read(Id)?.Phase);

            var exit = await sut.StartVerifiedAsync(Id);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(phaseAtCommit).IsEqualTo("committed");
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    // ── Task 16 (AI-1655): gated start — Phase A (exit 28) / Phase B (exit 29) ──

    /// <summary>Minimal launchd plist whose <c>&lt;array&gt;</c> (ProgramArguments) and baked
    /// <c>KCAP_CONSENT_SEED_DEFAULT</c> are exactly what <see cref="LaunchdUnit.BinaryFromPlist"/>/
    /// <see cref="LaunchdUnit.EnvFromPlist"/> read — real launchd never sees this content, only the
    /// gate's re-parse of it does.</summary>
    static string MinimalPlist(string binary, string consentSeedDefault) => $"""
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
          </dict>
        </dict>
        </plist>
        """;

    [Test]
    public async Task Pre_mutation_gate_failure_returns_28_and_touches_nothing() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            // The unit bakes nothing (readPlist "sees" no unit) while this invocation carries the
            // consent-seed directive — Phase A's DirectiveMissing case — so the gate must fire
            // before the fresh query's under-lock work does anything else.
            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
                readPlist: _ => null,
                gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);

            var exit = await sut.StartVerifiedAsync(Id);

            await Assert.That(exit).IsEqualTo(VerifyExit.StartGate);
            await Assert.That(manager.Calls.Count).IsEqualTo(1);
            await Assert.That(manager.Calls.All(c => c == "query")).IsTrue();
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Plist_drift_between_phase_a_and_phase_b_rolls_back_to_29_without_ever_starting() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            // Loaded at the fresh query — the gated path must boot it out (never kickstart it)
            // before re-checking evidence immediately ahead of bootstrap.
            var manager = new FakeServiceManager { Started = true };
            var stopPhases = new List<string?>();
            manager.OnStop = id => stopPhases.Add(ServiceTxnMarker.Read(id)?.Phase);

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));

            var reads = 0;
            string? ReadPlist(string _) {
                reads++;
                // Phase A observes a passing unit; the plist a foreign writer swaps in before Phase
                // B's re-read points at a different binary entirely — content drift, not just a
                // digest failure (digestMatches is stubbed to pass either way below).
                return reads == 1 ? MinimalPlist("/bin/kcap-daemon", "prompt") : MinimalPlist("/bin/kcap-daemon-moved", "prompt");
            }

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
                readPlist: ReadPlist,
                gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null,
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
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }
}
