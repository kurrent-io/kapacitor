using Capacitor.Cli.Core;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class ServiceVerifyInstallTests {
    const string Id             = "svc-verify-install";
    const string ExpectedVersion = "1.2.3";
    const string OwnPlistContent = "<plist>own-unit</plist>";

    /// <summary>Scripted <see cref="IServiceManager"/> for the install path: <see cref="Query"/>
    /// reflects a simple not-installed/loaded/uninstalled state machine driven by
    /// <see cref="WriteAndBootstrap"/>/<see cref="Uninstall"/>, so a test only sets what it cares
    /// about. <see cref="Calls"/> records every verb in argv-order, matching
    /// ServiceVerifyStartTests' fake.</summary>
    sealed class FakeServiceManager : IServiceManager {
        public readonly List<string> Calls = [];
        public LabelProbe InitialProbe = LabelProbe.Absent;
        public bool Bootstrapped, Uninstalled;
        public int? RunningPid = 4242;
        public string PlistPath = "/fake/agents/io.kurrent.kcap.daemon.svc-verify-install.plist";
        public string PlistContent = OwnPlistContent;
        public Action<string>? OnGenerateFiles;
        public Action<string>? OnWriteAndBootstrap;

        public int QueryCalls              => Calls.Count(c => c == "query");
        public int WriteAndBootstrapCalls  => Calls.Count(c => c == "writeAndBootstrap");
        public int UninstallCalls          => Calls.Count(c => c == "uninstall");

        public string Describe() => "fake";
        public IReadOnlyList<string> ListInstalled() => [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) {
            OnGenerateFiles?.Invoke(spec.ServiceId);
            return [new GeneratedFile(PlistPath, PlistContent)];
        }

        public ServiceQuery Query(string serviceId) {
            Calls.Add("query");
            if (Uninstalled) return new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
            if (Bootstrapped) return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            return new ServiceQuery(InitialProbe, InitialProbe != LabelProbe.Absent, ServiceState.NotInstalled, null, null);
        }

        public void Install(ServiceSpec spec, bool startNow) { }

        public void WriteAndBootstrap(ServiceSpec spec) {
            Calls.Add("writeAndBootstrap");
            OnWriteAndBootstrap?.Invoke(spec.ServiceId);
            Bootstrapped = true;
        }

        public bool Uninstall(string serviceId, out string? error) {
            Calls.Add("uninstall");
            Uninstalled  = true;
            Bootstrapped = false;
            error = null;
            return true;
        }

        public bool Start(string serviceId, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, out string? error) { error = null; return true; }
    }

    /// <summary>Same drive loop as ServiceVerifyStartTests: Task.Delay(interval, time, ct)'s
    /// continuation resumes synchronously inside Advance(), so a tight Advance-loop reliably steps
    /// a multi-iteration poll to completion without any real waiting.</summary>
    static async Task<int> Drive(Task<int> task, FakeTimeProvider time, TimeSpan step) {
        var guard = 0;
        while (!task.IsCompleted && guard++ < 500) time.Advance(step);
        return await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    static (string Dir, string DaemonPath) SetUpViableInstall() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        var daemonPath = Path.Combine(dir, "kcap-daemon");
        File.WriteAllText(daemonPath, "");
        return (dir, daemonPath);
    }

    static ServiceSpec Spec(string daemonPath) =>
        new(Id, daemonPath, Path.Combine(Path.GetTempPath(), "daemon.log"), new Dictionary<string, string>(), []);

    // A matching-fingerprint readPlist: the final recheck and rollback's foreign-file guard both
    // see "our own" unit still on disk, unmodified since WriteAndBootstrap.
    static string? OwnPlist(string _) => OwnPlistContent;

    [Test]
    public async Task Viability_abort_missing_binary_touches_nothing() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var manager = new FakeServiceManager();
            var missingPath = Path.Combine(dir, "does-not-exist-kcap-daemon");
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(missingPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Viability);
            await Assert.That(manager.Calls).IsEmpty();
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task PreQuery_loaded_is_contended_not_bootout_unknown() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager { InitialProbe = LabelProbe.Loaded };
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
            await Assert.That(manager.QueryCalls).IsEqualTo(1);
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task PreQuery_unknown_is_bootout_unknown_distinct_from_loaded() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager { InitialProbe = LabelProbe.Unknown };
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.BootoutUnknown);
            await Assert.That(manager.QueryCalls).IsEqualTo(1);
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Happy_path_writes_marker_through_every_phase_then_commits_and_deletes() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();
            var phaseAtGenerateFiles = "";
            manager.OnGenerateFiles = id => phaseAtGenerateFiles = ServiceTxnMarker.Read(id)!.Phase;
            var phaseAtWriteAndBootstrap = "";
            manager.OnWriteAndBootstrap = id => phaseAtWriteAndBootstrap = ServiceTxnMarker.Read(id)!.Phase;

            var phaseAtFirstHello = "";
            var helloCalls = 0;
            Task<HelloProbeResult> Hello(string id, TimeSpan _) {
                if (helloCalls++ == 0) phaseAtFirstHello = ServiceTxnMarker.Read(id)!.Phase;
                return Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, "kcap-daemon"));
            }

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(phaseAtGenerateFiles).IsEqualTo("captured");
            await Assert.That(phaseAtWriteAndBootstrap).IsEqualTo("written");
            await Assert.That(phaseAtFirstHello).IsEqualTo("bootstrapped");
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
            await Assert.That(manager.UninstallCalls).IsEqualTo(0);
            // Primary check + the floored final-recheck confirmation — both hello calls happened.
            await Assert.That(helloCalls).IsEqualTo(2);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Leftover_committed_marker_at_entry_self_heals_and_proceeds() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            ServiceTxnMarker.Write(Id, new TxnMarker(1, "install", "committed", "stale", "no-unit", "stale-fingerprint"));

            var manager = new FakeServiceManager();
            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, "kcap-daemon"));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Leftover_written_phase_marker_at_entry_surfaces_restore_verification_untouched() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            ServiceTxnMarker.Write(Id, new TxnMarker(1, "install", "written", "stale", "no-unit", "stale-fingerprint"));

            var manager = new FakeServiceManager();
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
            await Assert.That(manager.Calls).IsEmpty();
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
            await Assert.That(ServiceTxnMarker.Read(Id)!.Phase).IsEqualTo("written");
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Wrong_hello_version_rolls_back_by_uninstalling_its_own_unit() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "0.9.0", "kcap-daemon")); // != ExpectedVersion

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.HelloValidation);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Foreign_plist_at_final_recheck_is_never_deleted_and_keeps_the_marker() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, "kcap-daemon"));

            // A different writer's plist text is on disk by the time the final recheck reads it —
            // the fingerprint can never match what WriteAndBootstrap wrote.
            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: _ => "<plist>someone-else</plist>");

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
            await Assert.That(manager.UninstallCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Lock_loser_never_converges_rolls_back_to_readiness_timeout() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager { RunningPid = 111 }; // never matches the validated pid below
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, "kcap-daemon"));

            var sut = new ServiceVerify(manager, _ => 222, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            // No stop-by-pid path exists here at all — the only rollback verb is Uninstall.
            await Assert.That(manager.Calls.Contains("stop")).IsFalse();
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }
}
