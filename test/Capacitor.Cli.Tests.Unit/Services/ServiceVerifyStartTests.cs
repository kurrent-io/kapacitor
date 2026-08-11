using Capacitor.Cli.Core;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class ServiceVerifyStartTests {
    const string Id = "svc-verify";

    /// <summary>Scripted <see cref="IServiceManager"/>: Query reflects a simple started/stopped
    /// state machine driven by Start/Stop, so a test only sets the flags/pid it cares about.</summary>
    sealed class FakeServiceManager : IServiceManager {
        public bool Started, Stopped, RemainsLoadedAfterStop;
        public int StartCalls, StopCalls, UninstallCalls;
        public int? RunningPid = 4242;
        public Action<string>? OnStart;
        public Action<string>? OnStop;

        public string Describe() => "fake";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);

        public ServiceQuery Query(string serviceId) {
            if (Stopped && !RemainsLoadedAfterStop)
                return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
            if (Started)
                return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            return new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/bin/kcap-daemon", null);
        }

        public void Install(ServiceSpec spec, bool startNow) { }

        public bool Uninstall(string serviceId, out string? error) {
            UninstallCalls++;
            error = null;
            return true;
        }

        public bool Start(string serviceId, out string? error) {
            StartCalls++;
            OnStart?.Invoke(serviceId);
            Started = true;
            error = null;
            return true;
        }

        public bool Stop(string serviceId, out string? error) {
            StopCalls++;
            OnStop?.Invoke(serviceId);
            Stopped = true;
            error = null;
            return true;
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
    public async Task Rollback_restore_verification_failure_keeps_the_marker() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager { RemainsLoadedAfterStop = true };
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(false, null, null, null));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time, forwardBudget: TimeSpan.FromSeconds(2));

            var task = sut.StartVerifiedAsync(Id);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
            await Assert.That(manager.StopCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
            await Assert.That(ServiceTxnMarker.Read(Id)!.Phase).IsEqualTo("bootstrapped");
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }
}
