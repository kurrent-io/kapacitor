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
    sealed class FakeServiceManager : IVerifyServiceManager {
        public readonly List<string> Calls = [];
        public LabelProbe InitialProbe = LabelProbe.Absent;
        public bool InitialUnitPresent;
        public bool Bootstrapped, Uninstalled;
        public bool StayUnknownAfterUninstall;
        public int? RunningPid = 4242;
        public string PlistPath = "/fake/agents/io.kurrent.kcap.daemon.svc-verify-install.plist";
        public string PlistContent = OwnPlistContent;
        public Exception? GenerateFilesThrows;
        public Exception? WriteAndBootstrapThrows;
        public Action<string>? OnGenerateFiles;
        public Action<string>? OnWriteAndBootstrap;

        /// <summary>When set, a post-bootstrap Query blocks for its whole timeout (a hung
        /// <c>launchctl print</c>) by advancing this clock — proves the readiness step never hands
        /// the Query a second full budget once a late hello has burned the first.</summary>
        public FakeTimeProvider? HangQueryClock;

        public int QueryCalls              => Calls.Count(c => c == "query");
        public int WriteAndBootstrapCalls  => Calls.Count(c => c == "writeAndBootstrap");
        public int UninstallCalls          => Calls.Count(c => c == "uninstall");

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) {
            OnGenerateFiles?.Invoke(spec.ServiceId);
            if (GenerateFilesThrows is not null) throw GenerateFilesThrows;
            return [new GeneratedFile(PlistPath, PlistContent)];
        }

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            Calls.Add("query");
            if (Bootstrapped && HangQueryClock is not null) HangQueryClock.Advance(timeout);
            // Bootstrapped wins over a PRIOR Uninstalled: entry-time marker recovery and the
            // --replace matrix both call Uninstall() before this same transaction's own later
            // WriteAndBootstrap succeeds, and Uninstall never gets a second chance to reset the
            // flag once that happens. Every other test path only ever sets Uninstalled AFTER
            // Bootstrapped (rollback undoing a bootstrap that already ran) — Uninstall() itself
            // resets Bootstrapped=false in that case, so this ordering is a no-op for them.
            if (Bootstrapped) return new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/bin/kcap-daemon", RunningPid);
            if (Uninstalled)
                return StayUnknownAfterUninstall
                    ? new ServiceQuery(LabelProbe.Unknown, true, ServiceState.NotInstalled, null, null)
                    : new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
            return new ServiceQuery(InitialProbe, InitialProbe != LabelProbe.Absent || InitialUnitPresent, ServiceState.NotInstalled, null, null);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) {
            Calls.Add("writeAndBootstrap");
            OnWriteAndBootstrap?.Invoke(spec.ServiceId);
            if (WriteAndBootstrapThrows is not null) throw WriteAndBootstrapThrows;
            Bootstrapped = true;
        }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add("uninstall");
            Uninstalled  = true;
            Bootstrapped = false;
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
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
            // Render viability now runs GenerateFiles BEFORE the first destructive step, so no marker
            // exists yet when it fires (the old ordering wrote "captured" first — that was the bug).
            var markerExistsAtGenerateFiles = true;
            manager.OnGenerateFiles = id => markerExistsAtGenerateFiles = ServiceTxnMarker.Exists(id);
            var phaseAtWriteAndBootstrap = "";
            manager.OnWriteAndBootstrap = id => phaseAtWriteAndBootstrap = ServiceTxnMarker.Read(id)!.Phase;

            var phaseAtFirstHello = "";
            var helloCalls = 0;
            Task<HelloProbeResult> Hello(string id, TimeSpan _) {
                if (helloCalls++ == 0) phaseAtFirstHello = ServiceTxnMarker.Read(id)!.Phase;
                return Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));
            }

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(markerExistsAtGenerateFiles).IsFalse();
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
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Leftover_written_phase_marker_with_matching_plist_is_cleaned_up_and_proceeds() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // Unlike the mismatch case below, the on-disk plist's fingerprint matches exactly what
            // the dead transaction itself recorded — provably its own residue, so recovery cleans
            // it up (Uninstall, benign-absence semantics) rather than surfacing.
            var matchingFingerprint = ServiceTxnMarker.Fingerprint(OwnPlistContent);
            ServiceTxnMarker.Write(Id, new TxnMarker(1, "install", "written", "stale", "no-unit", matchingFingerprint));

            var manager = new FakeServiceManager();
            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(manager.Calls.IndexOf("uninstall")).IsLessThan(manager.Calls.IndexOf("writeAndBootstrap"));
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Leftover_marker_with_null_fingerprint_is_cleared_without_touching_the_manager() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // Died before ever writing a plist (e.g. a crash right after "captured") — there is
            // nothing on disk that could be the dead transaction's residue, so recovery just clears
            // the marker; it must not call Uninstall on a label it never touched.
            ServiceTxnMarker.Write(Id, new TxnMarker(1, "install", "captured", "stale", "no-unit", null));

            var manager = new FakeServiceManager();
            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(manager.UninstallCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Leftover_marker_with_unreadable_but_present_plist_surfaces_restore_verification_untouched() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // A read failure (permission error, transient I/O error, ...) is NOT the same as
            // absence: _readPlist returns null for both, but the file demonstrably exists here, so
            // recovery must never guess it's gone and clean up regardless — that would pave over
            // something it never actually examined.
            var matchingFingerprint = ServiceTxnMarker.Fingerprint(OwnPlistContent);
            ServiceTxnMarker.Write(Id, new TxnMarker(1, "install", "written", "stale", "no-unit", matchingFingerprint));

            var manager = new FakeServiceManager();
            var sut = new ServiceVerify(manager, _ => 4242,
                (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)),
                TimeProvider.System,
                readPlist: _ => null,     // simulates a read failure, not absence
                plistExists: _ => true);  // ...but the file IS there

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
            await Assert.That(manager.Calls).IsEmpty();
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Leftover_marker_whose_clear_never_confirms_absent_aborts_without_deleting_the_marker() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // Uninstall "succeeds" (bootout exit 0) but the label never actually settles to
            // Absent — the exact race ClearLabelAsync (now shared with entry recovery, not a bare
            // trust-the-bool call) exists to catch.
            var matchingFingerprint = ServiceTxnMarker.Fingerprint(OwnPlistContent);
            ServiceTxnMarker.Write(Id, new TxnMarker(1, "install", "written", "stale", "no-unit", matchingFingerprint));

            var manager = new FakeServiceManager { StayUnknownAfterUninstall = true };
            var time = new FakeTimeProvider();

            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), time,
                rollbackReserve: TimeSpan.FromSeconds(1), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.RollbackBudget);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();
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
                Task.FromResult(new HelloProbeResult(true, 1, "0.9.0", Id)); // version != ExpectedVersion; name/protocol right

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.HelloValidation);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    /// <summary>Spec §3.4: install/replace validates the daemon NAME too — a well-formed, right-
    /// version hello answering under a different reported name means something else is on our
    /// socket, and that's just as much a rollback trigger as a version mismatch.</summary>
    [Test]
    public async Task Wrong_hello_name_rolls_back_by_uninstalling_its_own_unit() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, "someone-elses-daemon"));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.HelloValidation);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    /// <summary>Spec §3.4: install/replace validates the hello's PROTOCOL version too — an
    /// otherwise-well-formed hello reporting a protocol this build doesn't speak is a deterministic
    /// incompatibility, not something a retry can fix.</summary>
    [Test]
    public async Task Unsupported_protocol_version_rolls_back_by_uninstalling_its_own_unit() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 2, ExpectedVersion, Id)); // protocol 2 != this build's 1

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.HelloValidation);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    /// <summary>The nullable contract: <c>expectedVersion: null</c> means "skip version
    /// validation" (callers pass a non-null <c>CapacitorVersion.Current()</c> in practice), not
    /// "any version is a mismatch" — a well-formed hello with the right name/protocol and
    /// confirmed ownership still commits.</summary>
    [Test]
    public async Task Null_expected_version_skips_version_validation_but_still_succeeds() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, "whatever-version-nobody-checks", Id));

            var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, expectedVersion: null);

            await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Foreign_plist_at_final_recheck_is_never_deleted_and_keeps_the_marker() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

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
    public async Task PreQuery_absent_but_unit_present_stopped_but_installed_is_contended() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // `service stop` retains the plist by design — a stopped-but-installed service must be
            // treated the same as Loaded, not as a fresh Absent slot to overwrite.
            var manager = new FakeServiceManager { InitialProbe = LabelProbe.Absent, InitialUnitPresent = true };
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
            await Assert.That(manager.QueryCalls).IsEqualTo(1);
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task GenerateFiles_throwing_is_a_viability_abort_before_any_destructive_step() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager { GenerateFilesThrows = new InvalidOperationException("invalid captured env value") };
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            // Rendering the plist is a VIABILITY step now — a throw aborts before anything on disk is
            // touched (no query, no marker), rather than after the marker was already written.
            await Assert.That(exit).IsEqualTo(VerifyExit.Viability);
            await Assert.That(manager.Calls).IsEmpty();
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(0);
            await Assert.That(manager.UninstallCalls).IsEqualTo(0);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Invalid_pinned_profile_url_is_a_viability_abort_touching_nothing() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager();
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)),
                TimeProvider.System, readPlist: OwnPlist, profileViable: () => false);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.Viability);
            await Assert.That(manager.Calls).IsEmpty();
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task WriteAndBootstrap_throwing_still_rolls_back_the_plist_it_already_wrote() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // launchctl bootstrap can throw (EPERM under MDM, I/O error) AFTER WriteUnitFiles has
            // already put the plist on disk — readPlist reflects that with matching ("own") content.
            var manager = new FakeServiceManager { WriteAndBootstrapThrows = new InvalidOperationException("launchctl bootstrap failed (exit 5): Input/output error") };
            var sut = new ServiceVerify(manager, _ => 4242, (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)), TimeProvider.System, readPlist: OwnPlist);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.WriteAndBootstrapCalls).IsEqualTo(1);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Rollback_reserve_exhausted_with_undetermined_state_is_rollback_budget() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var manager = new FakeServiceManager { RunningPid = 111, StayUnknownAfterUninstall = true }; // ownership never holds
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) =>
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

            var sut = new ServiceVerify(manager, _ => 222, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            // Uninstall ran, but the post-uninstall probe never settles below Unknown before the
            // reserve runs out — a genuine timeout, not an observed-wrong state.
            await Assert.That(exit).IsEqualTo(VerifyExit.RollbackBudget);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
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
                Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));

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

    [Test]
    public async Task Final_recheck_at_a_different_incarnation_rolls_back_instead_of_committing() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // Every readiness check observes a NEW job pid — a KeepAlive respawn between the primary
            // check and the final recheck. Ownership holds for each individual check, but the pinned
            // incarnation never survives to the recheck, so the transaction must never commit a
            // crash-looping unit; it rolls back at the forward cutoff.
            var manager = new FakeServiceManager { RunningPid = 1000 };
            var time = new FakeTimeProvider();

            Task<HelloProbeResult> Hello(string _, TimeSpan __) {
                if (manager.Bootstrapped) manager.RunningPid++;   // respawn: a new pid per observation
                return Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));
            }

            var sut = new ServiceVerify(manager, _ => manager.RunningPid, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task A_late_hello_never_hands_a_hung_query_a_second_full_budget() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            var time = new FakeTimeProvider();
            var start = time.GetUtcNow();

            // A hung `launchctl print` paired with a hello that consumes ~all of its budget must not
            // let readiness exceed the forward deadline: the Query is bounded by remaining-to-deadline,
            // so the transaction rolls back rather than committing after ~2x the forward time.
            var manager = new FakeServiceManager { HangQueryClock = time };
            Task<HelloProbeResult> Hello(string _, TimeSpan budget) {
                time.Advance(budget);
                return Task.FromResult(new HelloProbeResult(true, 1, ExpectedVersion, Id));
            }

            var sut = new ServiceVerify(manager, _ => 4242, Hello, time,
                forwardBudget: TimeSpan.FromSeconds(2), rollbackReserve: TimeSpan.FromSeconds(1), readPlist: OwnPlist);

            var task = sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);
            var exit = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            // Capped to the single forward deadline: the burned-budget hello leaves no time for the
            // Query, so readiness aborts to rollback instead of doubling its spend...
            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            // ...and the whole transaction stays inside the advertised forward + reserve envelope.
            var elapsed = time.GetUtcNow() - start;
            await Assert.That(elapsed <= TimeSpan.FromSeconds(3)).IsTrue();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Rollback_never_uninstalls_an_unreadable_present_plist() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // A lock-unaware writer replaced our plist with a foreign/unreadable file between
            // bootstrap and rollback: _readPlist returns null (can't fingerprint it) but the file
            // demonstrably exists. Uninstalling it would delete something we never verified is ours,
            // so rollback must fail closed and leave it untouched.
            var manager = new FakeServiceManager { WriteAndBootstrapThrows = new InvalidOperationException("launchctl bootstrap failed (exit 5)") };
            var sut = new ServiceVerify(manager, _ => 4242,
                (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)),
                TimeProvider.System,
                readPlist: _ => null,     // present but unreadable — cannot be fingerprinted
                plistExists: _ => true);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.RestoreVerification);
            await Assert.That(manager.UninstallCalls).IsEqualTo(0);      // the foreign file is never paved
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsTrue();     // marker retained for explicit repair
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Rollback_uninstalls_when_the_plist_is_genuinely_absent() {
        var (dir, daemonPath) = SetUpViableInstall();
        try {
            // The other null-read shape: the file is genuinely gone (read null AND not present), so
            // the idempotent uninstall still runs and verifies the restored (absent) state.
            var manager = new FakeServiceManager { WriteAndBootstrapThrows = new InvalidOperationException("launchctl bootstrap failed (exit 5)") };
            var sut = new ServiceVerify(manager, _ => 4242,
                (_, _) => Task.FromResult(new HelloProbeResult(false, null, null, null)),
                TimeProvider.System,
                readPlist: _ => null,
                plistExists: _ => false);

            var exit = await sut.InstallVerifiedAsync(Spec(daemonPath), replace: false, ExpectedVersion);

            await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
            await Assert.That(manager.UninstallCalls).IsEqualTo(1);
            await Assert.That(ServiceTxnMarker.Exists(Id)).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }
}
