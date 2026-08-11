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
    sealed class FakeServiceManager : IServiceManager {
        public readonly List<string> Calls = [];
        public bool Started, Stopped, RemainsLoadedAfterStop;
        public int? RunningPid = 4242;
        public string? StopError;
        public Action<string>? OnStart;
        public Action<string>? OnStop;

        public int StartCalls => Calls.Count(c => c == "start");
        public int StopCalls => Calls.Count(c => c == "stop");
        public int UninstallCalls => Calls.Count(c => c == "uninstall");

        public string Describe() => "fake";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);

        public ServiceQuery Query(string serviceId) {
            Calls.Add("query");
            if (Stopped && !RemainsLoadedAfterStop)
                return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
            if (Started)
                return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
        }

        public void Install(ServiceSpec spec, bool startNow) { }
        public void WriteAndBootstrap(ServiceSpec spec) { }

        public bool Uninstall(string serviceId, out string? error) {
            Calls.Add("uninstall");
            error = null;
            return true;
        }

        public bool Start(string serviceId, out string? error) {
            Calls.Add("start");
            OnStart?.Invoke(serviceId);
            Started = true;
            error = null;
            return true;
        }

        public bool Stop(string serviceId, out string? error) {
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
    public async Task Rollback_reserve_exhausted_keeps_the_marker() {
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

            // Reserve ran out before the restore was ever confirmed — RollbackBudget, not
            // RestoreVerification (that's reserved for an affirmatively-observed wrong state).
            await Assert.That(exit).IsEqualTo(VerifyExit.RollbackBudget);
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
    public async Task Recheck_starved_by_the_forward_deadline_still_gets_a_floor_to_confirm() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();
            var time = new FakeTimeProvider();

            // The primary hello call itself burns the entire remaining forward budget (a slow
            // resolve landing right at the deadline) — the confirmation call right after it must
            // still get a real chance to probe rather than being auto-failed by remaining <= 0.
            var helloCalls = 0;
            Task<HelloProbeResult> Hello(string _, TimeSpan budget) {
                if (helloCalls++ == 0) time.Advance(budget);
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
}
